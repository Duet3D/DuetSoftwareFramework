#!/usr/bin/env python3
# pyright: strict
"""Deploy firmware to a Duet 3 main board and the boards connected to it over CAN.

The main board is reached through its HTTP API, so it must be running standalone (not with an SBC).
Its object model says which boards are connected and which firmware file each one needs. That file
is taken from the boards' CMake builds (see scripts/build.py) and uploaded to the main board's /firmware
directory, with the main board's SD IAP if it has been built. Then M997 updates the expansion boards
one at a time, and finally the main board, which restarts.

    scripts/deploy.py duet3.local                     upload and update every board
    scripts/deploy.py duet3.local --debug             use the Debug builds
    scripts/deploy.py duet3.local -b 0 -b 121         only the main board and the board at CAN address 121
    scripts/deploy.py duet3.local -t EXP1HCL          only the 1HCL boards
    scripts/deploy.py duet3.local --upload-only       upload the files but do not update the boards
    scripts/deploy.py duet3.local --main Duet3_CAN0   use this build for the main board
    scripts/deploy.py duet3.local --build             build what the boards need, then deploy it
    scripts/deploy.py duet3.local --clean --debug     rebuild it from clean as Debug, then deploy it
    scripts/deploy.py duet3.local --build --dry-run   show what that would do, without doing it

Boards whose firmware has not been built are left alone. When several builds produce the main
board's firmware file (Duet3_MB6HC and Duet3_MB6HC_no_SD both make Duet3Firmware_MB6HC.bin), the
plain one is used unless --main names another.
"""

from __future__ import annotations

import argparse
import dataclasses
import json
import sys
import time
import urllib.parse
import urllib.request
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import cast

from common.firmware import BUILD, Board, board, build, load_boards

FIRMWARE_DIRECTORY = '0:/firmware/'
EXPANSION_UPDATE_TIMEOUT = 180  # seconds for an expansion board to flash and restart
MAIN_UPDATE_TIMEOUT = 120  # seconds for the main board to flash and come back on the network
START_TIMEOUT = 30  # seconds for an update to start before it is assumed to have failed
ANNOUNCE_TIMEOUT = 2  # seconds for the expansion boards to announce themselves to a restarted main board
MAX_ANNOUNCE_RESTARTS = 2  # times to restart the main board to make them announce themselves


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    host: str
    password: str
    debug: bool
    addresses: list[int]
    types: list[str]
    main: str | None
    upload_only: bool
    build: bool
    clean: bool
    dry_run: bool


class DuetError(Exception):
    pass


# The main board's HTTP API


class Duet:
    """A session with a Duet's HTTP API (the rr_ requests)."""

    def __init__(self, host: str, password: str) -> None:
        self.base = host if '://' in host else f'http://{host}'
        self.password = password
        self.headers: dict[str, str] = {}

    def request(self, path: str, params: dict[str, str] | None = None, data: bytes | None = None,
                timeout: float = 10) -> bytes:
        url = f'{self.base}/{path}'
        if params:
            url += '?' + urllib.parse.urlencode(params)
        headers = dict(self.headers)
        if data is not None:
            headers['Content-Type'] = 'application/octet-stream'
        with urllib.request.urlopen(urllib.request.Request(url, data, headers), timeout=timeout) as response:
            return cast(bytes, response.read())

    def json(self, path: str, params: dict[str, str] | None = None, data: bytes | None = None,
             timeout: float = 10) -> dict[str, object]:
        return cast(dict[str, object], json.loads(self.request(path, params, data, timeout)))

    def connect(self) -> None:
        reply = self.json('rr_connect', {'password': self.password, 'sessionKey': 'yes'})
        if reply.get('err') != 0:
            reasons = {1: 'wrong password', 2: 'no more HTTP sessions available'}
            raise DuetError(f'cannot log in: {reasons.get(cast(int, reply.get("err")), reply)}')
        key = reply.get('sessionKey')
        self.headers = {'X-Session-Key': str(key)} if key else {}

    def disconnect(self) -> None:
        try:
            self.request('rr_disconnect')
        except OSError:
            pass

    def boards(self) -> list[dict[str, object]]:
        """The boards in the object model, main board first; verbose for the file names."""
        reply = self.json('rr_model', {'key': 'boards', 'flags': 'd99v'})
        return cast(list[dict[str, object]], reply['result'])

    def upload(self, name: str, data: bytes) -> None:
        params = {'name': FIRMWARE_DIRECTORY + name, 'crc32': f'{zlib.crc32(data):08x}'}
        reply = self.json('rr_upload', params, data, timeout=120)
        if reply.get('err') != 0:
            raise DuetError(f'uploading {name} failed: {reply}')

    def gcode(self, code: str) -> None:
        self.request('rr_gcode', {'gcode': code})

    def reply(self) -> str:
        """The replies to G-codes sent since the last call."""
        return self.request('rr_reply').decode(errors='replace').strip()


# Firmware files


@dataclass(frozen=True)
class Image:
    """A firmware file from a build tree."""
    path: Path
    preset: str


def built_images(boards: list[Board], debug: bool) -> dict[str, list[Image]]:
    """The firmware files the builds have produced, by file name. The firmware is from the Release or
    Debug builds; the IAPs are always Release, as they are only used to write the firmware."""
    images: dict[str, list[Image]] = {}
    for b in boards:
        if b.project not in ('RepRapFirmware', 'Duet3Expansion', 'DuetIAP'):
            continue
        preset = b.variant(debug and b.project != 'DuetIAP')
        for path in sorted((BUILD / preset).glob('*.bin')) + sorted((BUILD / preset).glob('*.uf2')):
            images.setdefault(path.name, []).append(Image(path, preset))
    return images


def find_image(name: str, images: dict[str, list[Image]], preset: str | None) -> Image | None:
    """The build of a firmware file: the named preset's image of that type if given, whatever the
    build calls it (Duet3_CAN0 builds Duet3Firmware_MB6HC_CAN0.bin for the MB6HC), else the build whose
    preset has the shortest name, being the board's plain variant rather than a special one."""
    if preset is not None:
        return next((Image(p, preset) for p in sorted((BUILD / preset).glob('*' + Path(name).suffix))), None)
    return min(images.get(name, []), key=lambda i: len(i.preset), default=None)


def preset_for(file_name: str, boards: list[Board]) -> Board | None:
    """The preset that builds a file a board asks for, to build it before it is looked for. The files
    are named after the board's short name: Duet3Firmware_<name> is built by the RepRapFirmware or
    Duet3Expansion preset of that name less any Duet3 prefix (Duet3_MB6HC, Duet3Mini5plus, EXP3HC), and
    Duet3_SDiap32_<name> by the DuetIAP _SD preset whose name, so shortened, is or starts it
    (iap-Duet3_MB6HC_SD, iap-Duet3Mini_SD for Mini5plus)."""

    def short(b: Board) -> str:
        return b.preset.removeprefix('iap-').removesuffix('_SD').removeprefix('Duet3_').removeprefix('Duet3')

    stem = Path(file_name).stem
    if stem.startswith('Duet3Firmware_'):
        name = stem.removeprefix('Duet3Firmware_')
        return next((b for b in boards if b.project in ('RepRapFirmware', 'Duet3Expansion') and short(b) == name),
                    None)
    if stem.startswith('Duet3_SDiap32_'):
        name = stem.removeprefix('Duet3_SDiap32_')
        iaps = [b for b in boards if b.project == 'DuetIAP' and b.preset.endswith('_SD') and name.startswith(short(b))]
        return max(iaps, key=lambda b: len(short(b)), default=None)
    return None


# Updating


@dataclass(frozen=True)
class Target:
    """A connected board and the firmware it is to be given."""
    address: int
    name: str
    image: Image


def check_reply(duet: Duet, what: str) -> None:
    reply = duet.reply()
    if 'Error' in reply:
        raise DuetError(f'{what}: {reply}')


def update_expansion_board(duet: Duet, target: Target) -> None:
    """Update an expansion board and wait for it to restart. The main board reports it as flashing,
    then resetting, and it may then drop out of the object model until it is running again."""
    print(f'Updating {target.name} at CAN address {target.address}', flush=True)
    duet.reply()
    duet.gcode(f'M997 B{target.address}')
    start = time.monotonic()
    started = False
    state = None
    while time.monotonic() - start < EXPANSION_UPDATE_TIMEOUT:
        time.sleep(1)
        check_reply(duet, f'M997 B{target.address}')
        board_data = next((b for b in duet.boards() if b.get('canAddress') == target.address), None)
        new_state = cast(str, board_data.get('state', 'unknown')) if board_data else 'unknown'
        if new_state != state:
            print(f'  {new_state}', flush=True)
            state = new_state
        if state == 'flashFailed':
            raise DuetError(f'{target.name} at CAN address {target.address} failed to flash')
        if state != 'running':
            started = True
        elif started and board_data:
            print(f'  firmware {board_data.get("firmwareVersion")} ({board_data.get("firmwareDate")})')
            return
        elif time.monotonic() - start > START_TIMEOUT:
            raise DuetError(f'{target.name} at CAN address {target.address} did not start updating')
    raise DuetError(f'{target.name} at CAN address {target.address} did not finish updating')


def restart_main_board(duet: Duet, name: str, command: str) -> None:
    """Send a command that restarts the main board, wait for it to go off the network and come back
    on, and log in again."""
    duet.reply()
    start = time.monotonic()
    try:
        duet.gcode(command)
        while True:
            time.sleep(1)
            check_reply(duet, command)
            if time.monotonic() - start > START_TIMEOUT:
                raise DuetError(f'{name} did not restart after {command}')
    except OSError:
        pass  # it has gone off the network
    print('  restarting', flush=True)
    while time.monotonic() - start < MAIN_UPDATE_TIMEOUT:
        time.sleep(2)
        try:
            duet.connect()
            return
        except OSError:
            continue
    raise DuetError(f'{name} did not come back on the network')


def board_details(b: dict[str, object]) -> tuple[object, ...]:
    """What the main board reports about an expansion board that should not change when it restarts."""
    return (b.get('state'), b.get('firmwareVersion'), b.get('firmwareDate'))


def unannounced_boards(duet: Duet, expected: dict[int, tuple[object, ...]]) -> list[int]:
    """The CAN addresses of the expansion boards the main board does not report as it did before it
    restarted, once they have had time to announce themselves to it."""
    start = time.monotonic()
    while True:
        reported = {cast(int, b.get('canAddress')): board_details(b) for b in duet.boards()[1:]}
        missing = [address for address, details in expected.items() if reported.get(address) != details]
        if not missing or time.monotonic() - start > ANNOUNCE_TIMEOUT:
            return missing
        time.sleep(2)


def update_main_board(duet: Duet, target: Target) -> None:
    """Update the main board, which restarts once it has written its firmware, and log in again.
    Expansion boards sometimes fail to announce themselves to the restarted main board, leaving its
    object model wrong about them; restarting the main board again makes them announce themselves."""
    expected = {cast(int, b.get('canAddress')): board_details(b) for b in duet.boards()[1:]
                if b.get('state') == 'running'}
    print(f'Updating {target.name}', flush=True)
    restart_main_board(duet, target.name, 'M997')
    main = duet.boards()[0]
    print(f'  firmware {main.get("firmwareVersion")} ({main.get("firmwareDate")})')
    for restarts in range(MAX_ANNOUNCE_RESTARTS + 1):
        missing = unannounced_boards(duet, expected)
        if not missing:
            if restarts:
                print('  the expansion boards have announced themselves')
            return
        addresses = ', '.join(map(str, missing))
        if restarts == MAX_ANNOUNCE_RESTARTS:
            raise DuetError(f'the expansion boards at CAN addresses {addresses} have not announced themselves '
                            f'to {target.name} after {restarts} restarts')
        print(f'  the expansion boards at CAN addresses {addresses} have not announced themselves; '
              'restarting it again', flush=True)
        restart_main_board(duet, target.name, 'M999')


def is_selected(b: dict[str, object], main_address: int, options: Options, boards: list[Board]) -> bool:
    """Whether a connected board is to be updated: every board, unless some are chosen by CAN address
    or by type, given as the short name it reports or the preset that builds its firmware."""
    if not options.addresses and not options.types:
        return True
    if cast(int, b.get('canAddress', main_address)) in options.addresses:
        return True
    names = {str(b.get('shortName', '')).lower()}
    if (file_name := cast(str | None, b.get('firmwareFileName'))) and (found := preset_for(file_name, boards)):
        names.add(found.preset.lower())
    return any(t.lower() in names for t in options.types)


def file_preset(file_name: str, is_main: bool, options: Options, boards: list[Board]) -> str | None:
    """The preset that builds a file for a board: --main's for the main board's firmware, the
    Release one for an IAP, else the preset_for() it in the build type asked for."""
    is_iap = file_name.startswith('Duet3_SDiap32_')
    if is_main and not is_iap and options.main is not None:
        return options.main
    found = preset_for(file_name, boards)
    return found.variant(options.debug and not is_iap) if found else None


def build_firmware(connected: list[dict[str, object]], main_address: int, options: Options,
                   boards: list[Board]) -> bool:
    """Build the firmware the boards being updated need, and the main board's SD IAP."""
    presets: list[str] = []
    for b in connected:
        is_main = cast(int, b.get('canAddress', main_address)) == main_address
        files = [cast(str | None, b.get('firmwareFileName'))]
        if is_main:
            files.append(cast(str | None, b.get('iapFileNameSD')))
        for file_name in filter(None, files):
            if (preset := file_preset(file_name, is_main, options, boards)) is not None:
                presets.append(preset)
            else:
                print(f'Not building {file_name}: no preset is known to build it')
    for preset in dict.fromkeys(presets):
        if options.dry_run:
            print(f'Would build {preset}' + (' from clean' if options.clean else ''))
        elif not build(preset, clean=options.clean):
            print(f'Building {preset} failed', file=sys.stderr)
            return False
    return True


def deploy(options: Options, boards: list[Board]) -> int:
    duet = Duet(options.host, options.password)
    duet.connect()
    try:
        connected = duet.boards()
        main_address = cast(int, connected[0].get('canAddress', 0))
        connected = [b for b in connected if is_selected(b, main_address, options, boards)]
        if not connected:
            print('No connected board matches the boards asked for')
            return 1
        if options.build:
            # Log in again afterwards, as the session times out while building
            duet.disconnect()
            if not build_firmware(connected, main_address, options, boards):
                return 1
            duet.connect()
        images = built_images(boards, options.debug)

        def image_for(file_name: str, is_main: bool) -> Image | None:
            image = find_image(file_name, images, options.main if is_main and '_SDiap32_' not in file_name else None)
            if image is None and options.dry_run and options.build:
                # Not built yet, but it would have been by now
                if (preset := file_preset(file_name, is_main, options, boards)) is not None:
                    return Image(BUILD / preset / file_name, preset)
            return image

        targets: list[Target] = []
        uploads: list[tuple[str, Image]] = []  # with the name the main board looks for
        for b in connected:
            address = cast(int, b.get('canAddress', main_address))
            name = f'{b.get("name")} ({b.get("shortName")})'
            file_name = cast(str | None, b.get('firmwareFileName'))
            is_main = address == main_address
            if not file_name or (image := image_for(file_name, is_main)) is None:
                print(f'Skipping {name} at CAN address {address}: {file_name or "its firmware file"} has not been built')
                continue
            targets.append(Target(address, name, image))
            uploads.append((file_name, image))
            iap_name = cast(str | None, b.get('iapFileNameSD'))
            if is_main and iap_name:
                if (iap := image_for(iap_name, True)) is not None:
                    uploads.append((iap_name, iap))
                else:
                    print(f'Not uploading {iap_name}, which has not been built; the one on the board will be used')
        if not targets:
            print('Nothing to deploy')
            return 1

        for remote, image in dict.fromkeys(uploads):
            source = image.path.relative_to(BUILD)
            if options.dry_run:
                print(f'Would upload {source} to {FIRMWARE_DIRECTORY}{remote}')
                continue
            print(f'Uploading {source} to {FIRMWARE_DIRECTORY}{remote}', flush=True)
            duet.upload(remote, image.path.read_bytes())
        if options.upload_only:
            return 0

        # The main board goes last, because it restarts and they are updated through it
        for target in sorted(targets, key=lambda t: t.address == main_address):
            if target.address == main_address:
                if options.dry_run:
                    print(f'Would update {target.name} (M997)')
                else:
                    update_main_board(duet, target)
            elif options.dry_run:
                print(f'Would update {target.name} at CAN address {target.address} (M997 B{target.address})')
            else:
                update_expansion_board(duet, target)
        return 0
    finally:
        duet.disconnect()


def parse_options(argv: list[str]) -> Options:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('host', help="the main board's host name or IP address")
    parser.add_argument('-P', '--password', default='reprap', help="the main board's password (default: reprap)")
    parser.add_argument('-d', '--debug', action='store_true', help='deploy the Debug builds')
    parser.add_argument('-b', '--board', dest='addresses', type=int, action='append', default=[], metavar='ADDRESS',
                        help='only update the board at this CAN address (repeatable; the main board is 0)')
    parser.add_argument('-t', '--type', dest='types', action='append', default=[], metavar='TYPE',
                        help='only update boards of this type, e.g. EXP1HCL or Duet3_MB6HC (repeatable; '
                             'with --board, boards matching either are updated)')
    parser.add_argument('-m', '--main', metavar='PRESET', help="the build to use for the main board's firmware")
    parser.add_argument('-u', '--upload-only', action='store_true', help='upload the files but do not update the boards')
    parser.add_argument('--build', action='store_true', help='build the firmware the boards need first')
    parser.add_argument('-c', '--clean', action='store_true', help='clean the build trees before building (implies --build)')
    parser.add_argument('-n', '--dry-run', action='store_true',
                        help='show what would be built, uploaded and updated, without doing it')
    ns = parser.parse_args(argv)
    return Options(
        host=cast(str, ns.host),
        password=cast(str, ns.password),
        debug=cast(bool, ns.debug),
        addresses=cast(list[int], ns.addresses),
        types=cast(list[str], ns.types),
        main=cast(str | None, ns.main),
        upload_only=cast(bool, ns.upload_only),
        build=cast(bool, ns.build) or cast(bool, ns.clean),
        clean=cast(bool, ns.clean),
        dry_run=cast(bool, ns.dry_run),
    )


def main() -> int:
    options = parse_options(sys.argv[1:])
    boards = load_boards()
    if options.main is not None:
        options = dataclasses.replace(options, main=board(options.main, boards).variant(options.debug))
    try:
        return deploy(options, boards)
    except (DuetError, OSError) as e:
        print(f'Error: {e}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
