#!/usr/bin/env python3
# pyright: strict
"""Move a Pi and its main board between DSF 3 with RepRapFirmware and DSF 4 with DuetCANMaster.

    scripts/migrate.py upgrade -H 192.168.4.27 --build      build DSF 4 and DuetCANMaster, then move to them
    scripts/migrate.py upgrade -H 192.168.4.27              the same, with what was last built
    scripts/migrate.py downgrade -H 192.168.4.27            back to the DSF 3 packages and their RepRapFirmware
    scripts/migrate.py downgrade -H 192.168.4.27 --dry-run  check the machine and show what would be done
    scripts/migrate.py upgrade -H 192.168.4.27 -e           flash the expansion boards as well
    scripts/migrate.py upgrade -H 192.168.4.27 --dsf-only   finish a move whose firmware update went through

The order matters. DSF 4 only talks to DuetCANMaster on board 0 and DSF 3 only to RepRapFirmware, so the
main board is always flashed first, by the stack that is leaving, whose M997 takes the file to flash:
once the firmware has changed, DSF has lost the main board and cannot update it any more.

    upgrade    DSF 3 flashes DuetCANMaster, then DSF 4 is deployed as scripts/deploy.py would
    downgrade  DSF 4 flashes RepRapFirmware, then the DSF 3 packages are reinstalled over it

A downgrade puts all the firmware packaged with that RepRapFirmware in the firmware directory, so that
DSF 3 can update any expansion board. With --expansion-boards the stack that is leaving also flashes the
connected expansion boards, one at a time, before it flashes the main board, as a board that fails can
then still be flashed again by running the same command.

Deploying DSF 4 overwrites the installed DSF 3 packages' files without telling dpkg, so dpkg goes on
reporting the DSF 3 versions, and those are what a downgrade reinstalls. They are installed from the
package repository, as are the files they restore. The firmware is the one packaged for that version,
unpacked from reprapfirmware, because the firmware directory cannot be trusted to hold it:
deploy.py --main uploads an image under the name of the firmware the board is running. The package
itself is not reinstalled, because its postinst updates every board that differs from it.

Everything that could stop the move is checked before the main board is flashed: that the machine is
on the stack being left and idle, that it can be reached over SSH, and that what it moves to has been
built or can be downloaded. Afterwards the machine has to report the new stack, connected to the main
board, within VERIFY_TIMEOUT.

If the move stops between the two steps, DSF cannot reach the main board, so run the same command with
--dsf-only to do the second step alone.
"""

from __future__ import annotations

import shlex
import subprocess
import sys
import time
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import cast

from common.cli import parse, parser, typed
from common.dsf import (PROJECTS, Build, BuildOptions, DeployOptions, add_build_arguments, binary_sources, build_options,
                        build_projects, deploy, dsf_version, plan, services)
from common.duet import Connector, DuetError, TransportError
from common.firmware import Board, image_path, load_boards
from common.firmware_deploy import (Target, board_for, board_name, build_presets, check_reply, connected_boards,
                                    main_board_state, open_when_up, preset_for, update_expansion_board, upload_images)

VERIFY_TIMEOUT = 120  # seconds for the new stack to come up and connect to the main board
IDLE_STATES = ('idle', 'off')  # what state.status may be for the machine to be moved

# The packages whose files deploying DSF 4 overwrites
DSF_PACKAGES = ('duetruntime', 'duetcontrolserver', 'duettools', 'duetwebserver', 'duetpluginservice',
                'duetpimanagementplugin')
RRF_PACKAGE = 'reprapfirmware'
SD_FIRMWARE = '/opt/dsf/sd/firmware'
DSF_BIN = '/opt/dsf/bin'


@dataclass(frozen=True)
class Stack:
    """A version of DSF and the firmware it talks to on the main board."""
    major: int  # of the DSF version
    firmware_prefix: str  # how the main board's firmware file starts
    name: str

    def firmware_file(self, short_name: str) -> str:
        return f'{self.firmware_prefix}{short_name}.bin'


def major_version(version: str) -> int:
    return int(version.split('.')[0])


def dsf3() -> Stack:
    return Stack(3, 'Duet3Firmware_', 'DSF 3 with RepRapFirmware')


def dsf4() -> Stack:
    return Stack(major_version(dsf_version()), 'DuetCANMaster_', 'DSF 4 with DuetCANMaster')


@dataclass(frozen=True)
class Machine:
    """What a machine reports about itself."""
    dsf_version: str
    status: str
    short_name: str
    firmware_file: str

    def is_on(self, stack: Stack) -> bool:
        return major_version(self.dsf_version) == stack.major and self.firmware_file.startswith(stack.firmware_prefix)

    def __str__(self) -> str:
        return f'DSF {self.dsf_version}, {self.firmware_file}, {self.status}'


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    direction: str
    host: str
    user: str
    password: str
    debug: bool
    build: bool
    clean: bool
    dsf_only: bool
    expansion_boards: bool
    dry_run: bool
    dsf: BuildOptions

    @property
    def to(self) -> DeployOptions:
        return DeployOptions(self.host, self.user, start_services=True, dry_run=self.dry_run)


def parse_options(argv: Sequence[str] | None = None) -> Options:
    p = parser(__doc__)
    p.add_argument('direction', choices=('upgrade', 'downgrade'), help='upgrade to DSF 4, or downgrade to DSF 3')
    p.add_argument('-H', '--host', required=True, help='the Pi: its host name or IP address')
    p.add_argument('-u', '--user', default='root', help='SSH user on the Pi (default: root)')
    p.add_argument('-P', '--password', default='reprap', help="the main board's password (default: reprap)")
    p.add_argument('--dsf-only', action='store_true',
                   help='the main board has been flashed already: only move DSF')
    p.add_argument('-e', '--expansion-boards', action='store_true',
                   help='flash the expansion boards too, before the main board')
    p.add_argument('-n', '--dry-run', action='store_true',
                   help='check the machine and show what would be done, without doing it')
    upgrade_only = p.add_argument_group('upgrade')
    upgrade_only.add_argument('-d', '--debug', action='store_true', help='use the Debug builds')
    upgrade_only.add_argument('--build', action='store_true', help='build DSF and the firmware first')
    upgrade_only.add_argument('-c', '--clean', action='store_true', help='rebuild from clean first (implies --build)')
    add_build_arguments(upgrade_only)
    namespace = parse(p, argv)
    if namespace.dsf_only and namespace.expansion_boards:
        p.error('--dsf-only cannot be used with --expansion-boards')
    return typed(Options, namespace, dsf=build_options(namespace))


# What the machine reports


def remote_output(options: Options, command: str) -> str:
    """The output of a command that changes nothing, which is run on a dry run too."""
    result = subprocess.run(['ssh', f'{options.user}@{options.host}', command], stdout=subprocess.PIPE, text=True)
    if result.returncode:
        raise DuetError(f'{command} on {options.host} failed')
    return result.stdout


def read_machine(duet: Connector) -> Machine:
    version = duet.model('sbc.dsf.version')
    boards = connected_boards(duet)
    if not version or not boards:
        raise DuetError('DSF is not reporting the main board')
    main = boards[0]
    return Machine(str(version), main_board_state(duet)[0], str(main.get('shortName')),
                   str(main.get('firmwareFileName')))


def open_source(options: Options, source: Stack, target: Stack) -> tuple[Connector, Machine] | None:
    """A session with a machine that is ready to be moved, or None if it has been moved already."""
    duet = open_when_up(options.host, options.password, 0, 'sbc')
    machine = read_machine(duet)
    print(f'{options.host}: {machine}', flush=True)
    if machine.is_on(target):
        print(f'{options.host} is on {target.name} already')
        duet.disconnect()
        return None
    if not machine.is_on(source):
        raise DuetError(f'{options.host} is not on {source.name}; if its firmware has been flashed already, '
                        'finish the move with --dsf-only')
    if machine.status not in IDLE_STATES:
        raise DuetError(f'{options.host} is {machine.status}; wait for it to be idle')
    return duet, machine


def wait_for(options: Options, target: Stack) -> None:
    """Wait for the machine to report the stack it was moved to, up and connected to the main board."""
    if options.dry_run:
        print(f'Would wait for {options.host} to report {target.name}')
        return
    print(f'Waiting up to {VERIFY_TIMEOUT}s for {target.name}', flush=True)
    start = time.monotonic()
    duet = open_when_up(options.host, options.password, VERIFY_TIMEOUT, 'sbc')
    machine = None
    lost = False
    try:
        while time.monotonic() - start < VERIFY_TIMEOUT:
            try:
                if lost:
                    duet.connect()
                    lost = False
                machine = read_machine(duet)
                if machine.is_on(target) and machine.status in IDLE_STATES:
                    print(f'=== {options.host} is on {target.name}: {machine} ===', flush=True)
                    return
            except DuetError:
                lost = True
            time.sleep(2)
    finally:
        duet.disconnect()
    raise DuetError(f'{options.host} has not come up on {target.name}: {machine or "it does not answer"}')


# The expansion boards


def connected_expansion_boards(duet: Connector) -> list[Target]:
    """The connected expansion boards, main boards in expansion mode included, each with the name of the
    file it asks for as its image, which is empty for a board that has not announced itself."""
    return [Target(cast(int, b.get('canAddress')), board_name(b), Path(str(b.get('firmwareFileName') or '')))
            for b in connected_boards(duet)[1:]]


def boards_to_upload_for(duet: Connector, boards: list[Board]) -> list[Target]:
    """The connected expansion boards there is a build for. A board that has not announced itself stops
    the move, as what has to be uploaded for it cannot be told."""
    targets: list[Target] = []
    connected = connected_expansion_boards(duet)
    unidentified = [str(t.address) for t in connected if not t.image.name]
    if unidentified:
        raise DuetError(f'the expansion boards at CAN addresses {", ".join(unidentified)} have not announced '
                        'themselves, so their firmware is not known; nothing has been changed. Get them to '
                        'announce themselves, or leave out --expansion-boards')
    for target in connected:
        if board_for(target.image.name, boards) is None:
            print(f'Skipping {target.name} at CAN address {target.address}: no preset is known to build '
                  f'{target.image.name}')
        else:
            targets.append(target)
    return targets


def flash_expansion_boards(duet: Connector, targets: list[Target], options: Options) -> None:
    """Have the stack that is leaving flash the expansion boards, if asked, while it can still reach them."""
    if not options.expansion_boards:
        return
    # The web server drops a session that has been idle for a few seconds, as it has if anything was prepared
    duet.connect()
    for target in targets:
        update_expansion_board(duet, target, False, options.dry_run)


# The main board


def flash_main_board(duet: Connector, file_name: str, options: Options) -> None:
    """Have the stack that is leaving flash the main board. It stops once it has answered, as it can no
    longer talk to the firmware, so all that can be told from here is what it answered."""
    command = f'M997 P"{file_name}"'
    if options.dry_run:
        print(f'Would flash the main board ({command})')
        return
    print(f'=== Flashing the main board with {file_name} ===', flush=True)
    # The web server drops a session that has been idle for a few seconds, as it has if anything was prepared
    duet.connect()
    try:
        check_reply(duet.send(command), command)
    except TransportError as e:
        if 'HTTP 4' in str(e):
            raise DuetError(f'{command} was refused, so the main board has not been flashed: {e}')
        raise DuetError(f"no answer to {command} ({e}), so it is not known whether the main board was flashed; "
                        'look at the DuetControlServer log, and if it was, finish the move with --dsf-only')
    print('  flashed', flush=True)


def require(ok: bool, what: str) -> None:
    """Stop a move that has flashed the main board because DSF could not follow it."""
    if not ok:
        raise DuetError(f'{what} failed. The main board has been flashed, so DSF cannot reach it: '
                        'fix that and finish the move with --dsf-only')


# Upgrading


def firmware_images(short_name: str, expansion: list[Target], options: Options,
                    boards: list[Board]) -> dict[str, Path]:
    """The images to put in the firmware directory by preset: DuetCANMaster, the IAP that flashes it, which
    is always a Release build, and the expansion boards' firmware."""
    main_board = [dsf4().firmware_file(short_name), f'Duet3_SBCiap32_{short_name}.bin']
    images: dict[str, Path] = {}
    for file_name in (*main_board, *(t.image.name for t in expansion)):
        preset = preset_for(file_name, options.debug, boards)
        if preset is None:
            raise DuetError(f'DuetCANMaster is not built for a {short_name}')
        images[preset] = image_path(preset, Path(file_name).suffix)
    return images


def check_built(dsf: Build, images: dict[str, Path]) -> None:
    """Refuse to start a move that could not be finished, as DSF 3 cannot follow DuetCANMaster."""
    binary_sources(dsf)
    if not dsf.plugin_stage_dir.is_dir():
        sys.exit(f'Error: {dsf.plugin_stage_dir} has not been staged; run with --build')
    for preset, image in images.items():
        if not image.is_file():
            sys.exit(f'Error: {preset} has not been built; run with --build')


def upgrade(options: Options, boards: list[Board]) -> int:
    source, target = dsf3(), dsf4()
    dsf = plan(options.dsf, list(PROJECTS))
    remote_output(options, 'true')
    opened = None
    expansion: list[Target] = []
    images: dict[str, Path] = {}
    if not options.dsf_only:
        opened = open_source(options, source, target)
        if opened is None:
            return 0
        if options.expansion_boards:
            expansion = boards_to_upload_for(opened[0], boards)
        images = firmware_images(opened[1].short_name, expansion, options, boards)

    building = options.build or options.clean
    if building:
        if opened is not None:
            opened[0].disconnect()  # the session times out while building
        if not build_presets(images, options.clean, options.dry_run):
            return 1
        if options.dry_run:
            print(f'Would build {", ".join(dsf.selected)} ({dsf.options.variant})')
        elif not build_projects(dsf):
            return 1
    if not (options.dry_run and building):
        check_built(dsf, images)

    if opened is not None:
        duet, machine = opened
        duet.connect()  # the session may have timed out while building
        upload_images(duet, [(image.name, image) for image in images.values()], options.dry_run)
        flash_expansion_boards(duet, expansion, options)
        flash_main_board(duet, target.firmware_file(machine.short_name), options)
        duet.disconnect()

    require(deploy(dsf, options.to), f'Deploying {target.name}')
    wait_for(options, target)
    return 0


# Downgrading


def installed_packages(options: Options) -> dict[str, str]:
    """The version of each DSF package dpkg has installed, which deploying DSF 4 leaves alone."""
    output = remote_output(options, "dpkg-query -W -f='${Package} ${Version} ${db:Status-Abbrev}\\n' "
                                    f"'duet*' {RRF_PACKAGE}")
    fields = (line.split() for line in output.splitlines())
    installed = {f[0]: f[1] for f in fields if len(f) >= 3 and f[2] == 'ii'}
    version = installed.get('duetcontrolserver')
    if version is None or RRF_PACKAGE not in installed:
        raise DuetError(f'the DSF packages are not installed on {options.host}, so there is no DSF 3 to go back to')
    if major_version(version) != dsf3().major:
        raise DuetError(f'duetcontrolserver {version} is installed on {options.host}, not DSF 3')
    return installed


def pinned(installed: dict[str, str]) -> str:
    """The installed DSF packages, each at the version it is installed at."""
    return ' '.join(shlex.quote(f'{p}={installed[p]}') for p in DSF_PACKAGES if p in installed)


def download_packages(options: Options, installed: dict[str, str]) -> bool:
    return options.to.run(['ssh', options.to.remote, 'apt-get update -qq && apt-get install -y --reinstall '
                           f'--download-only {pinned(installed)}'])


def install_firmware(options: Options, version: str, main_board_file: str) -> bool:
    """Unpack the package's firmware files, which leaves its postinst out of it, as long as it has the
    main board's. A file an expansion board lacks is reported when it is flashed."""
    package = shlex.quote(f'{RRF_PACKAGE}={version}')
    name = shlex.quote(main_board_file)
    return options.to.run(['ssh', options.to.remote,
                           f'work=$(mktemp -d) && cd "$work" && apt-get download {package} '
                           f'&& dpkg-deb -x {RRF_PACKAGE}_*.deb . && cd .{SD_FIRMWARE} && test -s {name} '
                           f'&& install -o dsf -g dsf -m 644 * {SD_FIRMWARE}/; '
                           'status=$?; cd / && rm -rf "$work"; exit $status'])


def reinstall_packages(options: Options, installed: dict[str, str]) -> bool:
    """Put the packages' files back and delete what DSF 4 left behind, with the services stopped."""
    names = ' '.join(p for p in DSF_PACKAGES if p in installed)
    services(options.to, 'stop')
    ok = options.to.run(['ssh', options.to.remote, 'DEBIAN_FRONTEND=noninteractive apt-get install -y --reinstall '
                         f'-o Dpkg::Options::=--force-confold {pinned(installed)}'])
    ok = ok and options.to.run(['ssh', options.to.remote,
                                f'owned=$(mktemp) && dpkg -L {names} | sort -u > "$owned" '
                                f'&& find {DSF_BIN} ! -type d | sort | comm -23 - "$owned" '
                                "| xargs -r -d '\\n' rm -fv; status=$?; rm -f \"$owned\"; exit $status"])
    services(options.to, 'restart')
    return ok


def downgrade(options: Options) -> int:
    source, target = dsf4(), dsf3()
    installed = installed_packages(options)
    opened = None
    expansion: list[Target] = []
    if not options.dsf_only:
        opened = open_source(options, source, target)
        if opened is None:
            return 0
        if options.expansion_boards:
            expansion = connected_expansion_boards(opened[0])  # now, as the session times out while downloading
    if not download_packages(options, installed):
        raise DuetError(f'The DSF {installed["duetcontrolserver"]} packages cannot be downloaded')

    if opened is not None:
        duet, machine = opened
        firmware = target.firmware_file(machine.short_name)
        if not install_firmware(options, installed[RRF_PACKAGE], firmware):
            raise DuetError(f'{firmware} cannot be unpacked from {RRF_PACKAGE} {installed[RRF_PACKAGE]}')
        flash_expansion_boards(duet, expansion, options)
        flash_main_board(duet, firmware, options)
        duet.disconnect()

    require(reinstall_packages(options, installed), f'Reinstalling {target.name}')
    wait_for(options, target)
    return 0


def main() -> int:
    options = parse_options()
    try:
        return upgrade(options, load_boards()) if options.direction == 'upgrade' else downgrade(options)
    except (DuetError, OSError) as e:
        print(f'Error: {e}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
