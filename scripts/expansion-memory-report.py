#!/usr/bin/env python3
"""Report flash and RAM occupancy for each built Duet3Expansion firmware.

Occupancy is taken from the ELF program headers and measured against the MEMORY regions the
board's linker script declares, so the figures are the ones the linker itself enforced rather
than a summary of section sizes. A segment is charged to a region by address: code and constants
are charged to flash, .bss to RAM, and initialised data to both, because it is stored in flash
and copied into RAM at startup.
"""

import argparse
import re
import struct
import sys
from pathlib import Path

PT_LOAD = 1

# Region names as the SAME5x and SAMC21 linker scripts spell them, in the order they are reported.
# Any further region a linker script declares is reported too, but only once something lands in it.
PRIMARY_REGIONS = (("rom", "Flash"), ("ram", "RAM"))

SIZE_SUFFIXES = {"k": 1024, "m": 1024 * 1024, "g": 1024 * 1024 * 1024}


class Segment:
    def __init__(self, vaddr, paddr, filesz, memsz):
        self.vaddr = vaddr
        self.paddr = paddr
        self.filesz = filesz
        self.memsz = memsz


class Region:
    def __init__(self, name, origin, length):
        self.name = name
        self.origin = origin
        self.length = length

    def contains(self, address):
        return self.origin <= address < self.origin + self.length


def parse_size(text):
    """Read a linker-script number: hex, decimal, optionally with a K/M/G suffix."""
    text = text.strip().rstrip("lL")
    multiplier = 1
    if text and text[-1].lower() in SIZE_SUFFIXES:
        multiplier = SIZE_SUFFIXES[text[-1].lower()]
        text = text[:-1]
    return int(text, 0) * multiplier


def read_memory_regions(linker_script):
    """Pull the MEMORY block out of a linker script."""
    text = linker_script.read_text()
    block = re.search(r"\bMEMORY\s*\{(.*?)\}", text, re.DOTALL)
    if not block:
        raise ValueError(f"{linker_script} declares no MEMORY block")

    regions = []
    entry = re.compile(
        r"^\s*(\w+)\s*(?:\([^)]*\))?\s*:\s*ORIGIN\s*=\s*([^,]+),\s*LENGTH\s*=\s*(\S+)",
        re.MULTILINE,
    )
    for name, origin, length in entry.findall(block.group(1)):
        regions.append(Region(name, parse_size(origin), parse_size(length)))
    return regions


def read_load_segments(elf_path):
    """Read the PT_LOAD program headers of a 32-bit little-endian ELF."""
    data = elf_path.read_bytes()
    if data[:4] != b"\x7fELF":
        raise ValueError(f"{elf_path} is not an ELF file")
    if data[4] != 1 or data[5] != 1:
        raise ValueError(f"{elf_path} is not 32-bit little-endian")

    phoff, = struct.unpack_from("<I", data, 0x1C)
    phentsize, phnum = struct.unpack_from("<HH", data, 0x2A)

    segments = []
    for index in range(phnum):
        header = phoff + index * phentsize
        ptype, _, vaddr, paddr, filesz, memsz = struct.unpack_from("<6I", data, header)
        if ptype == PT_LOAD:
            segments.append(Segment(vaddr, paddr, filesz, memsz))
    return segments


def region_usage(region, segments):
    """Bytes a region holds once every load segment is charged to the region it occupies.

    A segment loaded and run from the same region occupies its whole run size there. One whose
    run address sits elsewhere — initialised data, staged in flash and copied to RAM — occupies
    only its stored bytes in the region it is stored in, and its run size where it ends up.
    """
    used = 0
    for segment in segments:
        stored_here = region.contains(segment.paddr)
        runs_here = region.contains(segment.vaddr)
        if stored_here and runs_here:
            used += segment.memsz
        elif stored_here:
            used += segment.filesz
        elif runs_here:
            used += segment.memsz
    return used


def board_definitions(expansion_dir):
    """Read each board's binary name, MCU and linker script out of its board makefile."""
    boards = []
    for makefile in sorted((expansion_dir / "Makefiles").glob("*.mk")):
        text = makefile.read_text()

        def setting(name):
            match = re.search(rf"^{name}\s*:=\s*(\S+)", text, re.MULTILINE)
            return match.group(1) if match else None

        board = setting("BOARD")
        binary = setting("BINARY")
        linker_script = setting("LINKER_SCRIPT")
        if not (board and binary and linker_script):
            continue

        # The board makefiles are included from src/Duet3Expansion, so $(CURDIR) is that directory.
        linker_script = linker_script.replace("$(CURDIR)", str(expansion_dir))
        boards.append(
            {
                "board": board,
                "mcu": setting("MCU") or "",
                "elf": expansion_dir / board / f"{binary}.elf",
                "linker_script": Path(linker_script),
            }
        )
    return boards


def measure(board):
    """Collect per-region usage for one board, or None if its firmware has not been built."""
    if not board["elf"].exists():
        return None

    segments = read_load_segments(board["elf"])
    regions = read_memory_regions(board["linker_script"])
    return {region.name: (region_usage(region, segments), region.length) for region in regions}


def kib(value):
    return f"{value / 1024:.1f}"


def percent(used, total):
    return f"{100.0 * used / total:.1f}%" if total else "n/a"


def build_rows(boards):
    """One row per board, plus the names of any region used beyond flash and RAM."""
    rows = []
    extra_regions = set()
    for board in boards:
        usage = measure(board)
        if usage is None:
            rows.append([board["board"], board["mcu"], "not built", "", "", "", "", ""])
            continue

        row = [board["board"], board["mcu"]]
        for name, _ in PRIMARY_REGIONS:
            used, total = usage.get(name, (0, 0))
            row += [kib(used), kib(total), percent(used, total), kib(total - used)]
        rows.append(row)

        primary = {name for name, _ in PRIMARY_REGIONS}
        extra_regions |= {
            name for name, (used, _) in usage.items() if used and name not in primary
        }
    return rows, sorted(extra_regions)


def headers():
    # Free RAM is worth a column of its own: it is the pool the heap, the permanently allocated
    # blocks and the system stack all come out of at runtime, none of which the ELF accounts for.
    columns = ["Board", "MCU"]
    for _, label in PRIMARY_REGIONS:
        columns += [
            f"{label} used (KiB)",
            f"{label} size (KiB)",
            f"{label} %",
            f"{label} free (KiB)",
        ]
    return columns


def render_text(rows):
    columns = headers()
    widths = [
        max(len(str(cell)) for cell in [columns[i]] + [row[i] for row in rows])
        for i in range(len(columns))
    ]
    lines = ["  ".join(name.ljust(widths[i]) for i, name in enumerate(columns)).rstrip()]
    lines.append("  ".join("-" * width for width in widths))
    for row in rows:
        lines.append("  ".join(str(cell).ljust(widths[i]) for i, cell in enumerate(row)).rstrip())
    return "\n".join(lines)


def render_markdown(rows):
    columns = headers()
    lines = ["| " + " | ".join(columns) + " |"]
    lines.append("|" + "|".join(["---"] * len(columns)) + "|")
    for row in rows:
        lines.append("| " + " | ".join(str(cell) for cell in row) + " |")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--dir",
        type=Path,
        default=Path(__file__).resolve().parent.parent / "src" / "Duet3Expansion",
        help="the Duet3Expansion tree holding the board makefiles and build output",
    )
    parser.add_argument(
        "--markdown", action="store_true", help="emit a Markdown table instead of plain text"
    )
    args = parser.parse_args()

    if not (args.dir / "Makefiles").is_dir():
        parser.error(f"{args.dir} does not look like a Duet3Expansion tree")

    boards = board_definitions(args.dir)
    if not boards:
        parser.error(f"no board makefiles found under {args.dir / 'Makefiles'}")

    rows, extra_regions = build_rows(boards)

    if args.markdown:
        print("### Duet3Expansion firmware memory usage\n")
        print(render_markdown(rows))
    else:
        print(render_text(rows))

    # Flash and RAM are the regions worth a column; say so when a board puts something anywhere
    # else, so the table is never read as the whole picture when it is not.
    if extra_regions:
        note = "Also in use: " + ", ".join(extra_regions)
        print(f"\n{note}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
