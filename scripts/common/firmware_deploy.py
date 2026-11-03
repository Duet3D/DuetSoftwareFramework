# pyright: strict
"""Deploying firmware to a Duet 3 main board and the boards connected to it over CAN: the firmware
half of scripts/deploy.py.

The main board is reached through its HTTP API: DSF's REST API on an SBC, else the rr_ requests
RepRapFirmware serves on a standalone main board (see common/duet.py). Its object model says which
boards are connected and which firmware file each one needs. That file is taken from the boards' CMake
builds and uploaded to /firmware, with the main board's IAP if it has been built. Then M997 updates the expansion boards one at a time, and
finally the main board, which restarts.

On an SBC the main board runs DuetCANMaster, so its firmware comes from the DuetCANMaster build rather
than RepRapFirmware's, and its IAP is the SBC one.

Boards whose firmware has not been built are left alone. When several builds produce the main
board's firmware file (Duet3_MB6HC and Duet3_MB6HC_no_SD both make Duet3Firmware_MB6HC.bin), the
board's own preset (Duet3_MB6HC) is used unless --main names another.

With bootloader set, the expansion boards' bootloaders are updated instead, over CAN (M997 S3), leaving
their firmware and the main board alone. Each board asks for the bootloader its firmware was built for,
Duet3Bootloader-<BOOTLOADER_NAME>.bin, which is taken from the Release build of the bootloader preset of
that name. The firmware writes the new bootloader over the old one, so a board must be running Release
firmware, which has a bootloader, to do it; a Debug build ignores the request. The STM32 boards cannot
update their bootloader yet, and a board without a working bootloader cannot be reached over CAN: flash
those over SWD with scripts/flash_bootloader.py. A main board in expansion mode has no bootloader: it
updates its firmware with its CAN IAP (Duet3_CANiap32_<board>.bin), which it loads from its own SD card,
so that has to be copied there by hand.
"""

from __future__ import annotations

import re
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import cast

from common.duet import Connector, DuetError, TransportError, as_dict, as_list, open_session
from common.firmware import Board, build
from common.repo import BUILD, ROOT

FIRMWARE_DIRECTORY = '0:/firmware/'
REQUEST_TIMEOUT = 10  # seconds for a request other than an upload or a code
EXPANSION_UPDATE_TIMEOUT = 180  # seconds for an expansion board to flash and restart
MAIN_UPDATE_TIMEOUT = 120  # seconds for the main board to flash and come back on the network
START_TIMEOUT = 30  # seconds for an update to start before it is assumed to have failed
CONNECT_TIMEOUT = 60  # seconds for the HTTP API to come up, after deploying DSF has restarted it
EXPANSION_CONFIGS = ROOT / 'src/Duet3Expansion/src/Config'  # the expansion boards' configuration headers
DIAGNOSTICS_DELAY = 2  # seconds for an expansion board's diagnostics to reach the main board
ANNOUNCE_TIMEOUT = 2  # seconds for the expansion boards to announce themselves to a restarted main board
MAX_ANNOUNCE_RESTARTS = 2  # times to restart the main board to make them announce themselves


@dataclass(frozen=True)
class Options:
    """What to deploy, and where."""
    host: str
    password: str
    debug: bool
    addresses: list[int]
    types: list[str]  # presets, or the short names the boards report; none for every board
    main: str | None
    upload_only: bool
    bootloader: bool  # update the expansion boards' bootloaders instead of the boards' firmware
    build: bool
    clean: bool
    dry_run: bool
    wait: bool  # the HTTP API may still be starting, so keep trying to log in for a while


# The main board's HTTP API


def open_when_up(options: Options, timeout: float) -> Connector:
    """Log in, retrying while the HTTP API is not up yet or cannot reach the main board. Each code is
    given as long as an update can take, because over DSF's REST API M997 returns once it is done."""
    start = time.monotonic()
    while True:
        try:
            return open_session(options.host, options.password, 'auto', REQUEST_TIMEOUT,
                                max(EXPANSION_UPDATE_TIMEOUT, MAIN_UPDATE_TIMEOUT))
        except DuetError:
            if time.monotonic() - start > timeout:
                raise
        time.sleep(2)


def connected_boards(duet: Connector) -> list[dict[str, object]]:
    """The boards in the object model, main board first; verbose for the file names."""
    return [as_dict(b) for b in as_list(duet.model('boards', 'd99v'))]


def main_board_state(duet: Connector) -> tuple[str, int]:
    """The main board's status and how many seconds it has been up."""
    state = as_dict(duet.model('state', 'v'))
    return cast(str, state.get('status')), cast(int, state.get('upTime'))


# Firmware files

IAP = re.compile(r'^Duet3_(SD|SBC)iap32_')


def main_project(sbc: bool) -> str:
    """The project of the main board's firmware: DuetCANMaster on an SBC, else RepRapFirmware."""
    return 'DuetCANMaster' if sbc else 'RepRapFirmware'


def iap_key(sbc: bool) -> str:
    """The object model field that names the IAP the main board updates itself with."""
    return 'iapFileNameSBC' if sbc else 'iapFileNameSD'


@dataclass(frozen=True)
class Image:
    """A firmware file from a build tree."""
    path: Path
    preset: str


def built_images(boards: list[Board], debug: bool, sbc: bool) -> dict[str, list[Image]]:
    """The firmware files the builds have produced, by file name. The firmware is from the Release or
    Debug builds; the IAPs and bootloaders are always Release, as the IAPs are only used to write the
    firmware and a bootloader has to fit in the space the firmware leaves for it. The main board's is
    DuetCANMaster's or RepRapFirmware's, which both name it Duet3Firmware_<board>."""
    images: dict[str, list[Image]] = {}
    for b in boards:
        if b.project not in (main_project(sbc), 'Duet3Expansion', 'DuetIAP', 'Duet3Bootloader'):
            continue
        preset = b.variant(debug and b.project in (main_project(sbc), 'Duet3Expansion'))
        for path in sorted((BUILD / preset).glob('*.bin')) + sorted((BUILD / preset).glob('*.uf2')):
            images.setdefault(path.name, []).append(Image(path, preset))
    return images


def find_image(name: str, images: dict[str, list[Image]], preset: str | None) -> Image | None:
    """The build of a firmware file: the named preset's file of that name if given, else its image of
    that type, whatever the build calls it (Duet3_CAN0 builds Duet3Firmware_MB6HC_CAN0.bin for the
    MB6HC). With no preset, the build whose preset has the shortest name, being likely the plain variant."""
    if preset is not None:
        if (BUILD / preset / name).is_file():
            return Image(BUILD / preset / name, preset)
        return next((Image(p, preset) for p in sorted((BUILD / preset).glob('*' + Path(name).suffix))), None)
    return min(images.get(name, []), key=lambda i: len(i.preset), default=None)


def preset_for(file_name: str, boards: list[Board], sbc: bool) -> Board | None:
    """The preset that builds a file a board asks for, to build it before it is looked for. The files
    are named after the board's short name: Duet3Firmware_<name> is built by the main board's project's
    (see main_project()) or the Duet3Expansion preset of that name less any prefix (Duet3_MB6HC,
    canmaster-Duet3_MB6HC, Duet3Mini5plus, EXP3HC), and Duet3_SDiap32_<name> or Duet3_SBCiap32_<name> by
    the DuetIAP _SD or _SBC preset whose name, so shortened, is or starts it (iap-Duet3_MB6HC_SD,
    iap-Duet3Mini_SBC for Mini5plus), Duet3Bootloader-<name> by the Duet3Bootloader preset
    bootloader-<name>, and Duet3_CANiap32_<name> by bootloader-Duet3_<name>."""

    def short(b: Board) -> str:
        name = b.preset.removeprefix('canmaster-').removeprefix('iap-').removesuffix('_SD').removesuffix('_SBC')
        return name.removeprefix('Duet3_').removeprefix('Duet3')

    stem = Path(file_name).stem
    if stem.startswith('Duet3Firmware_'):
        name = stem.removeprefix('Duet3Firmware_')
        return next((b for b in boards if b.project in (main_project(sbc), 'Duet3Expansion') and short(b) == name),
                    None)
    if iap := IAP.match(stem):
        name = stem[iap.end():]
        iaps = [b for b in boards
                if b.project == 'DuetIAP' and b.preset.endswith(f'_{iap[1]}') and name.startswith(short(b))]
        return max(iaps, key=lambda b: len(short(b)), default=None)
    if stem.startswith('Duet3Bootloader-'):
        preset = 'bootloader-' + stem.removeprefix('Duet3Bootloader-')
    elif stem.startswith('Duet3_CANiap32_'):
        preset = 'bootloader-Duet3_' + stem.removeprefix('Duet3_CANiap32_')
    else:
        return None
    return next((b for b in boards if b.project == 'Duet3Bootloader' and b.preset == preset), None)


def bootloader_names() -> dict[str, str]:
    """The bootloader each type of expansion board asks for, by the short name it reports: the
    BOOTLOADER_NAME and BOARD_TYPE_NAME of the board's configuration header."""
    names: dict[str, str] = {}
    for header in sorted(EXPANSION_CONFIGS.glob('*.h')):
        text = header.read_text(errors='replace')
        board_name = re.search(r'^#define\s+BOARD_TYPE_NAME\s+"([^"]+)"', text, re.MULTILINE)
        bootloader = re.search(r'^#define\s+BOOTLOADER_NAME\s+"([^"]+)"', text, re.MULTILINE)
        if board_name and bootloader:
            names[board_name[1]] = bootloader[1]
    return names


def can_iap_file(b: dict[str, object], boards: list[Board], sbc: bool) -> str | None:
    """The CAN IAP of a main board in expansion mode, which has no bootloader to update (M997 S3). Its
    firmware is updated by its CAN IAP, which it loads from its own SD card, as files cannot be sent to it
    over CAN. None for an expansion board."""
    firmware = preset_for(cast(str, b.get('firmwareFileName', '')), boards, sbc)
    if firmware is None or firmware.project == 'Duet3Expansion':
        return None
    return f'Duet3_CANiap32_{b.get("shortName", "")}.bin'


def bootloader_file(b: dict[str, object], names: dict[str, str], boards: list[Board],
                    sbc: bool) -> tuple[str | None, str]:
    """The bootloader file an expansion board asks for, or None and why it cannot be updated."""
    short_name = str(b.get('shortName', ''))
    if (iap := can_iap_file(b, boards, sbc)) is not None:
        found = preset_for(iap, boards, sbc)
        if found is None:
            where = f'{iap}, which no preset builds'
        elif (path := BUILD / found.preset / iap).exists():
            where = f'{iap}, so copy {path.relative_to(ROOT)} to its /firmware directory'
        else:
            where = f'{iap}, so build {found.preset} and copy that to its /firmware directory'
        return None, f'a main board in expansion mode has no bootloader; it loads its CAN IAP from its own SD card: {where}'
    if short_name not in names:
        return None, f'the bootloader of a {short_name} board is not known'
    file_name = f'Duet3Bootloader-{names[short_name]}.bin'
    found = preset_for(file_name, boards, sbc)
    if found is None:
        return None, f'no preset builds {file_name}'
    if found.mcu == 'STM32H5':
        return None, ('its firmware cannot update its bootloader yet; '
                      f'flash it with scripts/flash_bootloader.py {found.preset}')
    return file_name, ''


# Updating


@dataclass(frozen=True)
class Target:
    """A connected board and the firmware it is to be given."""
    address: int
    name: str
    image: Image


def check_reply(reply: str, what: str) -> None:
    if 'Error' in reply:
        raise DuetError(f'{what}: {reply.strip()}')


def update_command(target: Target, bootloader: bool) -> str:
    return f'M997 B{target.address}' + (' S3' if bootloader else '')


def bootloader_id(duet: Connector, address: int) -> str | None:
    """The bootloader an expansion board reports in its diagnostics."""
    duet.replies()
    reply = duet.send(f'M122 B{address}')
    if not reply:
        time.sleep(DIAGNOSTICS_DELAY)
        reply = duet.replies()
    found = re.search(r'Bootloader ID: (.*)', reply)
    return found[1].strip() if found else None


def update_expansion_board(duet: Connector, target: Target, bootloader: bool) -> None:
    """Update an expansion board's firmware or bootloader and wait for it to restart. The main board
    reports it as flashing, then resetting, and it may then drop out of the object model until it is
    running again."""
    what = 'the bootloader of ' if bootloader else ''
    print(f'Updating {what}{target.name} at CAN address {target.address}', flush=True)
    if bootloader:
        print(f'  bootloader {bootloader_id(duet, target.address) or "not reported"}')
    command = update_command(target, bootloader)
    duet.replies()
    check_reply(duet.send(command), command)
    start = time.monotonic()
    started = False
    state = None
    while time.monotonic() - start < EXPANSION_UPDATE_TIMEOUT:
        time.sleep(1)
        check_reply(duet.replies(), command)
        board_data = next((b for b in connected_boards(duet) if b.get('canAddress') == target.address), None)
        new_state = cast(str, board_data.get('state', 'unknown')) if board_data else 'unknown'
        if new_state != state:
            print(f'  {new_state}', flush=True)
            state = new_state
        if state == 'flashFailed':
            raise DuetError(f'{target.name} at CAN address {target.address} failed to flash')
        if state != 'running':
            started = True
        elif started and board_data:
            if bootloader:
                print(f'  bootloader {bootloader_id(duet, target.address) or "not reported"}')
            else:
                print(f'  firmware {board_data.get("firmwareVersion")} ({board_data.get("firmwareDate")})')
            return
        elif time.monotonic() - start > START_TIMEOUT:
            raise DuetError(f'{target.name} at CAN address {target.address} did not start updating')
    # Debug firmware accepts M997 S3 but does nothing; a failed bootloader update flashes the LED forever
    hint = (': it must be running Release firmware, and if its LED is flashing the update failed '
            '(see scripts/flash_bootloader.py)' if bootloader else '')
    raise DuetError(f'{target.name} at CAN address {target.address} did not finish updating{hint}')


def restart_main_board(duet: Connector, name: str, command: str) -> None:
    """Send a command that restarts the main board and wait for it to come back up, logging in again
    if it went off the network. Standalone, it goes off the network as it restarts. On an SBC the web
    server keeps answering while DuetControlServer restarts, refusing requests until it is back, and the
    restart shows as its up time starting again."""
    status, up_time = main_board_state(duet)
    duet.replies()
    started = gone = False
    try:
        check_reply(duet.send(command), command)
    except TransportError:
        gone = True
    # On an SBC, sending it may have taken until the board was updated
    start = time.monotonic()
    while time.monotonic() - start < MAIN_UPDATE_TIMEOUT:
        time.sleep(1)
        try:
            if gone:
                duet.connect()
            else:
                check_reply(duet.replies(), command)
            new_status, new_up_time = main_board_state(duet)
        except TransportError:
            gone = True
            new_status, new_up_time = status, up_time
        if not started and (gone or new_status != status or new_up_time < up_time):
            print('  restarting', flush=True)
            started = True
        if not started and time.monotonic() - start > START_TIMEOUT:
            raise DuetError(f'{name} did not restart after {command}')
        if started and new_up_time < up_time:
            return
    raise DuetError(f'{name} did not come back after {command}')


def board_details(b: dict[str, object]) -> tuple[object, ...]:
    """What the main board reports about an expansion board that should not change when it restarts."""
    return (b.get('state'), b.get('firmwareVersion'), b.get('firmwareDate'))


def unannounced_boards(duet: Connector, expected: dict[int, tuple[object, ...]]) -> list[int]:
    """The CAN addresses of the expansion boards the main board does not report as it did before it
    restarted, once they have had time to announce themselves to it."""
    start = time.monotonic()
    while True:
        reported = {cast(int, b.get('canAddress')): board_details(b) for b in connected_boards(duet)[1:]}
        missing = [address for address, details in expected.items() if reported.get(address) != details]
        if not missing or time.monotonic() - start > ANNOUNCE_TIMEOUT:
            return missing
        time.sleep(2)


def update_main_board(duet: Connector, target: Target) -> None:
    """Update the main board, which restarts once it has written its firmware, and log in again.
    Expansion boards sometimes fail to announce themselves to the restarted main board, leaving its
    object model wrong about them; restarting the main board again makes them announce themselves."""
    expected = {cast(int, b.get('canAddress')): board_details(b) for b in connected_boards(duet)[1:]
                if b.get('state') == 'running'}
    print(f'Updating {target.name}', flush=True)
    restart_main_board(duet, target.name, 'M997')
    main = connected_boards(duet)[0]
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


def is_selected(b: dict[str, object], main_address: int, options: Options, boards: list[Board], sbc: bool) -> bool:
    """Whether a connected board is to be updated: every board, unless some are chosen by CAN address
    or by type, given as the short name it reports or the preset that builds its firmware."""
    if not options.addresses and not options.types:
        return True
    if cast(int, b.get('canAddress', main_address)) in options.addresses:
        return True
    names = {str(b.get('shortName', '')).lower()}
    if (file_name := cast(str | None, b.get('firmwareFileName'))) and (found := preset_for(file_name, boards, sbc)):
        names.add(found.preset.lower())
    return any(t.lower() in names for t in options.types)


def file_preset(file_name: str, is_main: bool, options: Options, boards: list[Board], sbc: bool) -> str | None:
    """The preset that builds a file for a board: --main's for the main board's firmware, the
    Release one for an IAP or a bootloader, else the preset_for() it in the build type asked for."""
    is_firmware = file_name.startswith('Duet3Firmware_')
    if is_main and is_firmware and options.main is not None:
        return options.main
    found = preset_for(file_name, boards, sbc)
    return found.variant(options.debug and is_firmware) if found else None


def build_firmware(connected: list[dict[str, object]], main_address: int, options: Options,
                   boards: list[Board], sbc: bool) -> bool:
    """Build the firmware the boards being updated need, and the main board's IAP, or with
    bootloader set the bootloaders they need."""
    presets: list[str] = []
    names = bootloader_names()
    for b in connected:
        is_main = cast(int, b.get('canAddress', main_address)) == main_address
        if options.bootloader:
            # The CAN IAPs of main boards in expansion mode are built for copying to them by hand
            files = [bootloader_file(b, names, boards, sbc)[0] or can_iap_file(b, boards, sbc)]
        else:
            files = [cast(str | None, b.get('firmwareFileName'))]
        if is_main and not options.bootloader:
            files.append(cast(str | None, b.get(iap_key(sbc))))
        for file_name in filter(None, files):
            if (preset := file_preset(file_name, is_main, options, boards, sbc)) is not None:
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


def _deploy(options: Options, boards: list[Board]) -> int:
    if options.wait:
        print(f'Waiting up to {CONNECT_TIMEOUT}s for the HTTP API to come up', flush=True)
    duet = open_when_up(options, CONNECT_TIMEOUT if options.wait else 0)
    sbc = duet.sbc
    try:
        connected = connected_boards(duet)
        main_address = cast(int, connected[0].get('canAddress', 0))
        connected = [b for b in connected if is_selected(b, main_address, options, boards, sbc)]
        if options.bootloader:
            # The main board has no bootloader to update over CAN
            connected = [b for b in connected if cast(int, b.get('canAddress', main_address)) != main_address]
        if not connected:
            print('No connected board matches the boards asked for')
            return 1
        if options.build:
            # Log in again afterwards, as the session times out while building
            duet.disconnect()
            if not build_firmware(connected, main_address, options, boards, sbc):
                return 1
            duet.connect()
        images = built_images(boards, options.debug, sbc)

        def image_for(file_name: str, is_main: bool) -> Image | None:
            # The board's own preset, so another that happens to leave a file of that name is not used
            preset = file_preset(file_name, is_main, options, boards, sbc)
            image = find_image(file_name, images, preset)
            if image is None and options.dry_run and options.build and preset is not None:
                # Not built yet, but it would have been by now
                return Image(BUILD / preset / file_name, preset)
            return image

        targets: list[Target] = []
        uploads: list[tuple[str, Image]] = []  # with the name the main board looks for
        names = bootloader_names()
        for b in connected:
            address = cast(int, b.get('canAddress', main_address))
            name = f'{b.get("name")} ({b.get("shortName")})'
            file_name = cast(str | None, b.get('firmwareFileName'))
            if options.bootloader:
                file_name, problem = bootloader_file(b, names, boards, sbc)
                if file_name is None:
                    print(f'Skipping {name} at CAN address {address}: {problem}')
                    continue
            is_main = address == main_address
            if not file_name or (image := image_for(file_name, is_main)) is None:
                print(f'Skipping {name} at CAN address {address}: {file_name or "its firmware file"} has not been built')
                continue
            targets.append(Target(address, name, image))
            uploads.append((file_name, image))
            iap_name = cast(str | None, b.get(iap_key(sbc)))
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
            duet.upload(FIRMWARE_DIRECTORY + remote, image.path.read_bytes())
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
                what = 'the bootloader of ' if options.bootloader else ''
                print(f'Would update {what}{target.name} at CAN address {target.address} '
                      f'({update_command(target, options.bootloader)})')
            else:
                update_expansion_board(duet, target, options.bootloader)
        return 0
    finally:
        duet.disconnect()


def deploy_firmware(options: Options, boards: list[Board]) -> int:
    """Build what the connected boards need if asked, upload it and update them; the exit status."""
    try:
        return _deploy(options, boards)
    except (DuetError, OSError) as e:
        print(f'Error: {e}', file=sys.stderr)
        return 1
