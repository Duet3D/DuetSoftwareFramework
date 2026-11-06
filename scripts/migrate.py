#!/usr/bin/env python3
# pyright: strict
"""Move a Pi and its main board between DSF 3 with RepRapFirmware and DSF 4 with DuetCANMaster.

    scripts/migrate.py upgrade -H 192.168.4.27 --build      build DSF 4 and DuetCANMaster, then move to them
    scripts/migrate.py upgrade -H 192.168.4.27              the same, with what was last built
    scripts/migrate.py downgrade -H 192.168.4.27            back to the DSF 3 packages and their RepRapFirmware
    scripts/migrate.py downgrade -H 192.168.4.27 --dry-run  check the machine and show what would be done
    scripts/migrate.py upgrade -H 192.168.4.27 --dsf-only   finish a move whose firmware update went through

The order matters. DSF 4 only talks to DuetCANMaster on board 0 and DSF 3 only to RepRapFirmware, so the
main board is always flashed first, by the stack that is leaving, whose M997 takes the file to flash:
once the firmware has changed, DSF has lost the main board and cannot update it any more.

    upgrade    DSF 3 flashes DuetCANMaster, then DSF 4 is deployed as scripts/deploy.py would
    downgrade  DSF 4 flashes RepRapFirmware, then the DSF 3 packages are reinstalled over it

Deploying DSF 4 overwrites the installed DSF 3 packages' files without telling dpkg, so dpkg goes on
reporting the DSF 3 versions, and those are what a downgrade reinstalls. They are installed from the
package repository, as are the files they restore. The RepRapFirmware is the one packaged for that
version, unpacked from reprapfirmware, because the firmware directory cannot be trusted to hold it:
deploy.py --main uploads an image under the name of the firmware the board is running. The package
itself is not reinstalled, because its postinst updates every board that differs from it.

Everything that could stop the move is checked before the main board is flashed: that the machine is
on the stack being left and idle, that it can be reached over SSH, and that what it moves to has been
built or can be downloaded. Afterwards the machine has to report the new stack, connected to the main
board, within VERIFY_TIMEOUT.

If the move stops between the two steps, DSF cannot reach the main board, so run the same command with
--dsf-only to do the second step alone. Only the main board and DSF are moved: expansion boards keep
their firmware; update them with scripts/deploy.py.
"""

from __future__ import annotations

import shlex
import subprocess
import sys
import time
from collections.abc import Sequence
from dataclasses import dataclass

from common.cli import parse, parser, typed
from common.dsf import (PROJECTS, Build, BuildOptions, DeployOptions, add_build_arguments, binary_sources, build_options,
                        build_projects, deploy, dsf_version, plan, services)
from common.duet import Connector, DuetError, TransportError
from common.firmware import Board, build, image_path, load_boards
from common.firmware_deploy import (FIRMWARE_DIRECTORY, board_for, check_reply, connected_boards, main_board_state,
                                    open_when_up)

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
    p.add_argument('-n', '--dry-run', action='store_true',
                   help='check the machine and show what would be done, without doing it')
    upgrade_only = p.add_argument_group('upgrade')
    upgrade_only.add_argument('-d', '--debug', action='store_true', help='use the Debug builds')
    upgrade_only.add_argument('--build', action='store_true', help='build DSF and the firmware first')
    upgrade_only.add_argument('-c', '--clean', action='store_true', help='rebuild from clean first (implies --build)')
    add_build_arguments(upgrade_only)
    namespace = parse(p, argv)
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


def main_board_presets(short_name: str, options: Options, boards: list[Board]) -> tuple[str, str]:
    """The presets of DuetCANMaster and of the IAP that flashes it. The IAP is always a Release build."""
    firmware = board_for(dsf4().firmware_file(short_name), boards)
    iap = board_for(f'Duet3_SBCiap32_{short_name}.bin', boards)
    if firmware is None or iap is None:
        raise DuetError(f'DuetCANMaster is not built for a {short_name}')
    return firmware.variant(options.debug), iap.preset


def check_built(dsf: Build, images: list[str]) -> None:
    """Refuse to start a move that could not be finished, as DSF 3 cannot follow DuetCANMaster."""
    binary_sources(dsf)
    if not dsf.plugin_stage_dir.is_dir():
        sys.exit(f'Error: {dsf.plugin_stage_dir} has not been staged; run with --build')
    for preset in images:
        if not image_path(preset, '.bin').is_file():
            sys.exit(f'Error: {preset} has not been built; run with --build')


def upgrade(options: Options, boards: list[Board]) -> int:
    source, target = dsf3(), dsf4()
    dsf = plan(options.dsf, list(PROJECTS))
    remote_output(options, 'true')
    opened = None
    presets: tuple[str, str] | None = None
    if not options.dsf_only:
        opened = open_source(options, source, target)
        if opened is None:
            return 0
        presets = main_board_presets(opened[1].short_name, options, boards)

    building = options.build or options.clean
    if building:
        if opened is not None:
            opened[0].disconnect()  # the session times out while building
        for preset in presets or ():
            if options.dry_run:
                print(f'Would build {preset}' + (' from clean' if options.clean else ''))
            elif not build(preset, clean=options.clean):
                print(f'Building {preset} failed', file=sys.stderr)
                return 1
        if options.dry_run:
            print(f'Would build {", ".join(dsf.selected)} ({dsf.options.variant})')
        elif not build_projects(dsf):
            return 1
    if not (options.dry_run and building):
        check_built(dsf, list(presets or ()))

    if opened is not None and presets is not None:
        duet = opened[0]
        duet.connect()  # the session may have timed out while building
        for preset in presets:
            image = image_path(preset, '.bin')
            if options.dry_run:
                print(f'Would upload {preset} to {FIRMWARE_DIRECTORY}{image.name}')
            else:
                print(f'Uploading {preset} to {FIRMWARE_DIRECTORY}{image.name}', flush=True)
                duet.upload(FIRMWARE_DIRECTORY + image.name, image.read_bytes())
        flash_main_board(duet, image_path(presets[0], '.bin').name, options)
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


def install_firmware(options: Options, version: str, file_name: str) -> bool:
    """Unpack the main board's firmware from the package, which leaves the package's postinst out of it."""
    package = shlex.quote(f'{RRF_PACKAGE}={version}')
    name = shlex.quote(file_name)
    return options.to.run(['ssh', options.to.remote,
                           f'work=$(mktemp -d) && cd "$work" && apt-get download {package} '
                           f'&& dpkg-deb --fsys-tarfile {RRF_PACKAGE}_*.deb | tar -xO .{SD_FIRMWARE}/{name} > {name} '
                           f'&& test -s {name} && install -o dsf -g dsf -m 644 {name} {SD_FIRMWARE}/{name}; '
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
    if not options.dsf_only:
        opened = open_source(options, source, target)
        if opened is None:
            return 0
    if not download_packages(options, installed):
        raise DuetError(f'The DSF {installed["duetcontrolserver"]} packages cannot be downloaded')

    if opened is not None:
        duet, machine = opened
        firmware = target.firmware_file(machine.short_name)
        if not install_firmware(options, installed[RRF_PACKAGE], firmware):
            raise DuetError(f'{firmware} cannot be unpacked from {RRF_PACKAGE} {installed[RRF_PACKAGE]}')
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
