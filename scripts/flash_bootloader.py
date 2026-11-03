#!/usr/bin/env python3
# pyright: strict
"""Flash a bootloader to a Duet 3 expansion or tool board over SWD, through OpenOCD.

OpenOCD runs as the GDB server, normally on the host where the probe is plugged in (see "Debug the
Firmware" in DEVELOPER.md); it drives J-Link probes too. This script writes a GDB command file and runs
arm-none-eabi-gdb on it, so that each step is a GDB or OpenOCD command that can be tried by hand. GDB
prints each command as it runs it, and stops at the first that fails.

    scripts/flash_bootloader.py bootloader-SAME5x                     flash the SAME5x bootloader
    scripts/flash_bootloader.py bootloader-SAMC21 --build             build it first
    scripts/flash_bootloader.py bootloader-SAME5x -s localhost:3333   use this GDB server
    scripts/flash_bootloader.py bootloader-STM32H5 --debug            flash the Debug build

It first checks that the board's MCU is the one the bootloader is built for. The expansion firmware
protects its bootloader with the BOOTPROT fuse, which also stops a probe from writing it, so on the SAM
MCUs OpenOCD clears the fuse, which takes effect when the MCU is reset. The firmware protects the
bootloader again when it next starts. A bootloader that starts above the start of flash (the composite
one, at 4K) shares its erase block with what the board has below it, so that is read and written back
with it. The image is read back to check it, then the board is reset and left running.

A board that already has a bootloader and firmware can have its bootloader updated over CAN instead,
with scripts/deploy.py --bootloader.
"""

from __future__ import annotations

import shlex
import sys
import tempfile
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

from common.cli import complete_with, parse, parser
from common.firmware import Board, build, load_boards, read_elf
from common.repo import BUILD, run
from common.targets import FIRMWARE, select

DEFAULT_SERVER = 'host.docker.internal:2331'
BOOTLOADER_SIZE = 0x4000  # the flash the firmware leaves for the bootloader

# GDB expressions for the part number in the core's CPUID register, the SAM MCUs' DSU.DID register and
# the STM32H5's DBGMCU_IDCODE.DEV_ID
CPU_PART = '({unsigned int}0xE000ED00 >> 4 & 0xFFF)'
SAM_DID = '{unsigned int}0x41002018'
STM32_DEV_ID = '({unsigned int}0x44024000 & 0xFFF)'


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    board: Board
    server: str
    debug: bool
    build: bool
    clean: bool


@dataclass(frozen=True)
class Mcu:
    """What flashing a bootloader needs to know about an MCU family."""
    flash: int  # the address of the start of flash
    check: str  # a GDB expression that is true if the board has an MCU of the family
    flash_driver: str | None  # the OpenOCD flash driver whose bootloader command clears BOOTPROT


MCUS = {
    # A Cortex-M4 with DSU.DID PROCESSOR Cortex-M4 and FAMILY SAM E: the SAME51, SAME53 and SAME54
    'SAME5x': Mcu(0x00000000, f'{CPU_PART} == 0xC24 && ({SAM_DID} & 0xFF800000) == 0x61800000', 'atsame5'),
    # A Cortex-M0+ with DSU.DID PROCESSOR Cortex-M0+, FAMILY SAM C and SERIES C21
    'SAMC21': Mcu(0x00000000, f'{CPU_PART} == 0xC60 && ({SAM_DID} & 0xFFBF0000) == 0x11010000', 'at91samd'),
    # A Cortex-M33 with the DEV_ID of the STM32H503, the STM32H52x and H53x, or the STM32H56x and H57x
    'STM32H5': Mcu(0x08000000, f'{CPU_PART} == 0xD21 && ({STM32_DEV_ID} == 0x474 || {STM32_DEV_ID} == 0x478'
                               f' || {STM32_DEV_ID} == 0x484)', None),
}


def bootloaders(boards: list[Board]) -> list[Board]:
    """The bootloader presets. The Duet3Bootloader project also builds the main boards' CAN IAPs, which run
    from RAM. The bootloader inputs in .vscode/launch.json and .vscode/tasks.json pick the presets the same
    way: keep them in step."""
    return [b for b in boards if b.project == 'Duet3Bootloader' and b.mcu in MCUS and 'CAN IAP' not in b.display]


def bootloader_image(b: Board, mcu: Mcu, debug: bool) -> tuple[Path, int]:
    """The bootloader's .bin, which has the CRC the firmware checks, and where its .elf says it goes."""
    preset = b.variant(debug)
    elves = sorted((BUILD / preset).glob('*.elf'))
    if not elves:
        sys.exit(f'{preset} has not been built; build it, or add --build')
    address = read_elf(elves[0]).load_address()
    path = elves[0].with_suffix('.bin')
    if address is None or not path.exists():
        sys.exit(f'{path.relative_to(BUILD)} has not been built; build it, or add --build')
    end = address + path.stat().st_size
    if not mcu.flash <= address < end <= mcu.flash + BOOTLOADER_SIZE:
        sys.exit(f'{path.name} occupies {address:#010x}-{end:#010x}, outside the '
                 f'{BOOTLOADER_SIZE // 1024}K at the start of flash that the bootloader has')
    return path, address


def gdb_commands(server: str, b: Board, mcu: Mcu, image: Path, address: int, work: Path) -> str:
    """The GDB commands that flash the image, leaving what it read back in work/readback.bin."""
    size = image.stat().st_size
    commands = [
        'set trace-commands on',
        f'target extended-remote {server}',
        'monitor reset halt',
        f'if !({mcu.check})',
        f'  echo {b.preset} is for a {b.mcu}, but the board has a different MCU\\n',
        '  detach',
        '  quit 1',
        'end',
    ]
    if mcu.flash_driver:
        # Clear BOOTPROT in the user row, and reset the MCU so that it takes effect
        commands += [f'monitor {mcu.flash_driver} bootloader 0', 'monitor reset halt']
    if address > mcu.flash:
        # GDB erases whole erase blocks, so write what is below the image too
        kept, combined = work / 'kept.bin', work / 'combined.bin'
        commands += [
            f'dump binary memory {kept} {mcu.flash:#x} {address:#x}',
            f'shell cat {shlex.quote(str(kept))} {shlex.quote(str(image))} > {shlex.quote(str(combined))}',
        ]
        image = combined
    commands += [
        # Load the .bin rather than the .elf, as only the .bin has the CRC
        'set gnutarget binary',
        f'load {image} {mcu.flash:#x}',
        f'dump binary memory {work / "readback.bin"} {address:#x} {address + size:#x}',
        'monitor reset run',
        'detach',
    ]
    return ''.join(f'{c}\n' for c in commands)


def parse_options(argv: Sequence[str] | None = None) -> Options:
    p = parser(__doc__)
    complete_with(p.add_argument('preset', help='the bootloader preset, e.g. bootloader-SAME5x; a -debug suffix '
                                                'names its Debug build'),
                  lambda: [b.variant(debug) for b in bootloaders(load_boards()) for debug in (False, True)])
    p.add_argument('-s', '--server', default=DEFAULT_SERVER,
                   help=f"OpenOCD's GDB server as host:port (default: {DEFAULT_SERVER})")
    p.add_argument('-d', '--debug', action='store_true', help='flash the Debug build')
    p.add_argument('--build', action='store_true', help='build the bootloader first')
    p.add_argument('-c', '--clean', action='store_true', help='clean its build tree before building (implies --build)')
    ns = parse(p, argv)
    chosen = select([ns.preset], kinds=(FIRMWARE,))
    known = bootloaders(load_boards())
    if len(chosen.boards) != 1 or chosen.boards[0] not in known:
        p.error(f'{ns.preset} is not a bootloader; the bootloaders are {", ".join(b.preset for b in known)}')
    b = chosen.boards[0]
    return Options(board=b, server=ns.server, debug=ns.debug or b.preset in chosen.debug_boards,
                   build=ns.build or ns.clean, clean=ns.clean)


def main() -> int:
    options = parse_options()
    b = options.board
    mcu = MCUS[b.mcu]
    if options.build and not build(b.variant(options.debug), clean=options.clean):
        sys.exit(f'Building {b.variant(options.debug)} failed')
    image, address = bootloader_image(b, mcu, options.debug)

    with tempfile.TemporaryDirectory() as directory:
        work = Path(directory)
        script = work / 'flash.gdb'
        script.write_text(gdb_commands(options.server, b, mcu, image, address, work))
        if not run(['arm-none-eabi-gdb', '-batch', '-nx', '-x', str(script)]):
            sys.exit('Flashing the bootloader failed')
        if (work / 'readback.bin').read_bytes() != image.read_bytes():
            sys.exit('The bootloader did not read back as written')
    print(f'Flashed {image.relative_to(BUILD)} to {address:#010x}, and restarted the board')
    return 0


if __name__ == '__main__':
    sys.exit(main())
