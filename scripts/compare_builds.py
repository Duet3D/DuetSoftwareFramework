#!/usr/bin/env python3
# pyright: strict
"""Compare the firmware images built with make against those built with CMake.

make builds each board into src/<project>/<config>/ and CMake into build/<preset>/, and both name the
images alike. For each board this compares the images (.bin, .hex, .uf2) byte for byte and, when
they differ, explains how: the ranges that differ and the symbols they fall in, the ELF sections
whose size or contents differ, the symbols whose size differs and the strings only one build has.

    scripts/compare_builds.py                       every board both have built
    scripts/compare_builds.py EXP3HC bootloader-SAMC21
    scripts/compare_builds.py -p d3e --build        build a project's boards with both, then compare
    scripts/compare_builds.py --all --build --clean rebuild everything from clean with both
    scripts/compare_builds.py --debug EXP3HC        the Debug builds (make DEBUG=1, the -debug presets)
    scripts/compare_builds.py EXP3HC --limit 50     list up to 50 of the symbols and strings that differ

Boards are named as CMake presets (see "scripts/boards.py"), which are also Makefile.firmware's targets;
projects in full or as rrf, d3e, bootloader or iap.
DuetCANMaster has no make build, so it cannot be compared.
Some boards embed the build date and time, so their images only match when both builds compile those
files with the same SOURCE_DATE_EPOCH; --build sets it to the time of the HEAD commit unless it is
already set. make builds Release and Debug into the same directory, so it holds whichever was built last.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import struct
import subprocess
import sys
from collections import Counter, defaultdict
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

from common.firmware import BUILD, ROOT, Board, Elf, add_board_arguments, load_boards, read_elf, run, select

IMAGE_SUFFIXES = ('.bin', '.hex', '.uf2')
# The firmware make build; the top-level Makefile builds DSF
MAKE = ('make', '-f', 'Makefile.firmware')
PREFIXES = {'Duet3Bootloader': 'bootloader-', 'DuetIAP': 'iap-'}
STRING = re.compile(rb'[\x20-\x7e]{6,}')


def make_dir(b: Board) -> Path:
    """Where make builds a board: a directory named after its configuration, inside its project."""
    return ROOT / 'src' / b.project / b.preset.removeprefix(PREFIXES.get(b.project, ''))


def cmake_dir(b: Board, debug: bool) -> Path:
    return BUILD / b.variant(debug)


def images(directory: Path) -> dict[str, Path]:
    """The images and ELF in a build directory, by file name."""
    if not directory.is_dir():
        return {}
    return {p.name: p for p in sorted(directory.iterdir()) if p.suffix in IMAGE_SUFFIXES + ('.elf',)}


def demangle(names: list[str]) -> dict[str, str]:
    """C++ names demangled, if a c++filt is on PATH."""
    tool = shutil.which('arm-none-eabi-c++filt') or shutil.which('c++filt')
    if not tool or not names:
        return {n: n for n in names}
    out = subprocess.run([tool], input='\n'.join(names), capture_output=True, text=True).stdout.splitlines()
    return dict(zip(names, out)) if len(out) == len(names) else {n: n for n in names}


# Comparison


@dataclass(frozen=True)
class Options:
    boards: list[str]
    projects: list[str]
    all: bool
    exclude: list[str]
    debug: bool
    build: bool
    clean: bool
    jobs: int | None
    keep_going: bool
    ranges: int
    limit: int


def describe(elf: Elf | None, address: int) -> str:
    """Where an image address is: its address, section and symbol."""
    if elf is None:
        return f'0x{address:08x}'
    run = elf.run_address(address)
    section = elf.section_at(run)
    if section is None:
        return f'0x{address:08x} (outside the ELF, e.g. an appended CRC)'
    where = f'0x{address:08x} {section.name}'
    if run != address:
        where += f' (runs at 0x{run:08x})'
    if (symbol := elf.symbol_at(run)) is not None:
        where += f' {demangle([symbol.name])[symbol.name]}+0x{run - symbol.address:x}'
    return where


def printable(data: bytes) -> str:
    return ''.join(chr(c) if 32 <= c < 127 else '.' for c in data)


def differing_ranges(a: bytes, b: bytes) -> list[tuple[int, int]]:
    """The [start, end) ranges in which two byte strings differ, over their common length."""
    ranges: list[tuple[int, int]] = []
    start = None
    for i in range(min(len(a), len(b))):
        if a[i] != b[i]:
            if start is None:
                start = i
        elif start is not None:
            if ranges and start - ranges[-1][1] < 8:  # merge ranges a few bytes apart
                start = ranges.pop()[0]
            ranges.append((start, i))
            start = None
    if start is not None:
        ranges.append((start, min(len(a), len(b))))
    return ranges


def compare_bin(make: bytes, cmake: bytes, make_elf: Elf | None, cmake_elf: Elf | None, options: Options) -> list[str]:
    """How two raw images differ."""
    lines: list[str] = []
    make_base = (make_elf.load_address() if make_elf else None) or 0
    cmake_base = (cmake_elf.load_address() if cmake_elf else None) or 0
    ranges = differing_ranges(make, cmake)
    if len(make) != len(cmake):
        if ranges:
            first = ranges[0][0]
            lines.append(f'first difference at offset 0x{first:x}:')
            lines.append(f'  make:  {describe(make_elf, make_base + first)}')
            lines.append(f'  cmake: {describe(cmake_elf, cmake_base + first)}')
        else:
            lines.append('the shorter image matches the start of the longer')
        lines.append('(the images differ in size, so everything after the first difference may be shifted)')
        return lines
    lines.append(f'{sum(x != y for x, y in zip(make, cmake))} bytes differ, in {len(ranges)} ranges')
    for start, end in ranges[:options.ranges]:
        shown = slice(start, min(end, start + 32))
        lines.append(f'  0x{start:06x}+{end - start}: {describe(cmake_elf, cmake_base + start)}')
        lines.append(f'      make:  {make[shown].hex(" ")}  |{printable(make[shown])}|')
        lines.append(f'      cmake: {cmake[shown].hex(" ")}  |{printable(cmake[shown])}|')
    if len(ranges) > options.ranges:
        lines.append(f'  ... and {len(ranges) - options.ranges} more ranges (see --ranges)')
    return lines


def compare_sections(make: Elf, cmake: Elf) -> list[str]:
    """The allocated sections whose address, size or contents differ, as a table."""
    make_sections = {s.name: s for s in make.sections}
    cmake_sections = {s.name: s for s in cmake.sections}
    rows: list[tuple[str, ...]] = []
    for name in set(list(make_sections) + list(cmake_sections)):
        m, c = make_sections.get(name), cmake_sections.get(name)
        if m == c:
            continue
        notes: list[str] = []
        if m and c:
            if m.address != c.address:
                notes.append(f'moved 0x{m.address:08x} -> 0x{c.address:08x}')
            if m.size == c.size and m.data != c.data:
                notes.append('contents differ')
        rows.append((
            name,
            str(m.size) if m else '-',
            str(c.size) if c else '-',
            f'{(c.size if c else 0) - (m.size if m else 0):+d}',
            ', '.join(notes),
        ))
    if not rows:
        return []
    rows.insert(0, ('section', 'make', 'cmake', 'delta', ''))
    widths = [max(len(r[i]) for r in rows) for i in range(4)]
    return ['ELF sections that differ:'] + [
        ('  ' + r[0].ljust(widths[0]) + ''.join('  ' + r[i].rjust(widths[i]) for i in range(1, 4)) + '  ' + r[4]).rstrip()
        for r in rows]


def compare_symbols(make: Elf, cmake: Elf, limit: int) -> list[str]:
    """The symbols whose size differs, or that only one build has, largest change first. If the two
    lay out every symbol alike, the symbols whose contents differ too."""
    if limit <= 0:
        return []
    make_sizes: dict[str, list[int]] = defaultdict(list)
    cmake_sizes: dict[str, list[int]] = defaultdict(list)
    for s in make.symbols:
        make_sizes[s.name].append(s.size)
    for s in cmake.symbols:
        cmake_sizes[s.name].append(s.size)
    changes: list[tuple[int, str, str]] = []
    for name in make_sizes.keys() | cmake_sizes.keys():
        m, c = sorted(make_sizes.get(name, [])), sorted(cmake_sizes.get(name, []))
        if m == c:
            continue
        note = 'cmake only' if not m else 'make only' if not c else f'{"/".join(map(str, m))} -> {"/".join(map(str, c))}'
        changes.append((sum(c) - sum(m), name, note))
    changes.sort(key=lambda t: (-abs(t[0]), t[1]))

    lines: list[str] = []
    names = demangle([name for _, name, _ in changes[:limit]])
    if changes:
        lines.append(f'Symbols whose size differs ({len(changes)}):')
        lines += [f'  {delta:+7d}  {names[name]}  ({note})' for delta, name, note in changes[:limit]]
        if len(changes) > limit:
            lines.append(f'  ... and {len(changes) - limit} more (see --limit)')

    make_layout = [(s.name, s.address, s.size) for s in make.symbols]
    cmake_layout = [(s.name, s.address, s.size) for s in cmake.symbols]
    if make_layout == cmake_layout:
        differ = [c.name for m, c in zip(make.symbols, cmake.symbols)
                  if make.symbol_bytes(m) != cmake.symbol_bytes(c)]
        if differ:
            names = demangle(differ[:limit])
            lines.append(f'Symbols laid out alike whose contents differ ({len(differ)}):')
            lines += [f'  {names[name]}' for name in differ[:limit]]
            if len(differ) > limit:
                lines.append(f'  ... and {len(differ) - limit} more (see --limit)')
    elif not changes:
        lines += compare_order(make, cmake)
    return lines


def compare_order(make: Elf, cmake: Elf) -> list[str]:
    """Where two builds with the same symbols, each the same size, lay them out differently: the first
    symbol out of place, and how many bytes of each section no symbol covers. Those are alignment padding,
    which a different link order changes, and unnamed data such as string literals, whose differences
    compare_strings lists."""
    make_order, cmake_order = [s.name for s in make.symbols], [s.name for s in cmake.symbols]
    if make_order == cmake_order:
        i = next(i for i, (m, c) in enumerate(zip(make.symbols, cmake.symbols)) if m.address != c.address)
        lines = ['Every symbol is the same size and in the same order in both builds, but some have moved;',
                 'the first that moved, and the symbol before it:']
        i = max(i - 1, 0)
        rows = [make.symbols[i], make.symbols[i + 1]], [cmake.symbols[i], cmake.symbols[i + 1]]
    else:
        i = next(i for i, (m, c) in enumerate(zip(make_order, cmake_order)) if m != c)
        lines = ['Every symbol is the same size in both builds, but they are in a different order',
                 '(as when the object files are linked in a different order); the first out of place:']
        rows = [make.symbols[i]], [cmake.symbols[i]]
    names = demangle(list(dict.fromkeys(s.name for side in rows for s in side)))
    for side, symbols in zip(('make: ', 'cmake:'), rows):
        lines.append(f'  {side} ' + ', '.join(f'0x{s.address:08x} {names[s.name]}' for s in symbols))

    def padding(elf: Elf) -> dict[str, int]:
        """The bytes of each section that no symbol covers."""
        gaps: dict[str, int] = {}
        for section in elf.sections:
            end, gap = section.address, 0
            for s in elf.symbols:
                if section.address <= s.address < section.address + section.size:
                    gap += max(0, s.address - end)
                    end = max(end, s.address + s.size)
            gaps[section.name] = gap + max(0, section.address + section.size - end)
        return gaps

    make_padding, cmake_padding = padding(make), padding(cmake)
    if any(make_padding[n] != cmake_padding.get(n, make_padding[n]) for n in make_padding):
        lines.append('Bytes outside any symbol (alignment padding, and unnamed data such as string literals):')
    for name in make_padding:
        if name in cmake_padding and make_padding[name] != cmake_padding[name]:
            lines.append(f'  {name}: {make_padding[name]} -> {cmake_padding[name]} bytes '
                         f'({cmake_padding[name] - make_padding[name]:+d})')
    return lines


def compare_strings(make: Elf, cmake: Elf, limit: int) -> list[str]:
    """The printable strings that only one build has, such as differing __FILE__ paths or __TIME__.
    Functions are left out: their bytes often look like text, and change wherever the code moves."""
    if limit <= 0:
        return []

    def strings(elf: Elf) -> Counter[str]:
        found: Counter[str] = Counter()
        for section in elf.sections:
            if not section.data:
                continue
            data = bytearray(section.data)
            for s in elf.symbols:
                if s.function and section.address <= s.address < section.address + section.size:
                    start = s.address - section.address
                    data[start:start + s.size] = bytes(len(data[start:start + s.size]))
            found.update(m.group().decode() for m in STRING.finditer(data))
        return found

    make_strings, cmake_strings = strings(make), strings(cmake)
    lines: list[str] = []
    for side, only in (('make', make_strings - cmake_strings), ('CMake', cmake_strings - make_strings)):
        if only:
            lines.append(f'Strings only in the {side} build ({only.total()}):')
            lines += [f'  {text!r}' for text in list(only.elements())[:limit]]
            if only.total() > limit:
                lines.append(f'  ... and {only.total() - limit} more (see --limit)')
    return lines


def load(path: Path | None) -> Elf | None:
    if path is None:
        return None
    try:
        return read_elf(path)
    except (OSError, ValueError, struct.error) as e:
        print(f'Cannot read {path}: {e}', file=sys.stderr)
        return None


def compare(b: Board, options: Options) -> tuple[str, list[str]]:
    """Whether a board's make and CMake images match ('identical', 'different', 'not built' or
    'incomplete'), with the details of how they differ."""
    make_files, cmake_files = images(make_dir(b)), images(cmake_dir(b, options.debug))
    if not make_files or not cmake_files:
        missing = ' and '.join(n for n, f in (('make', make_files), ('CMake', cmake_files)) if not f)
        return 'not built', [f'not built with {missing}']

    def elf(files: dict[str, Path]) -> Path | None:
        return next((p for name, p in files.items() if name.endswith('.elf')), None)

    make_elf, cmake_elf = load(elf(make_files)), load(elf(cmake_files))
    status = 'identical'
    lines: list[str] = []
    for name in set(list(make_files) + list(cmake_files)):
        if name.endswith('.elf'):
            continue
        if name not in make_files or name not in cmake_files:
            lines.append(f'{name}: only built by {"make" if name in make_files else "CMake"}')
            status = 'incomplete' if status == 'identical' else status
            continue
        m, c = make_files[name].read_bytes(), cmake_files[name].read_bytes()
        if m == c:
            lines.append(f'{name}: identical ({len(m)} bytes)')
            continue
        status = 'different'
        lines.append(f'{name}: DIFFERENT (make {len(m)} bytes, CMake {len(c)} bytes, {len(c) - len(m):+d})')
        if name.endswith('.bin'):
            lines += ['  ' + line for line in compare_bin(m, c, make_elf, cmake_elf, options)]
    if not any(name.endswith(IMAGE_SUFFIXES) for name in make_files.keys() & cmake_files.keys()):
        lines.append('no image to compare; comparing the ELF sections only')
        if make_elf and cmake_elf and make_elf.sections != cmake_elf.sections:
            status = 'different'
    if status == 'different' and make_elf and cmake_elf:
        lines += compare_sections(make_elf, cmake_elf)
        lines += compare_symbols(make_elf, cmake_elf, options.limit)
        lines += compare_strings(make_elf, cmake_elf, options.limit)
    return status, lines


# Building


def build(b: Board, options: Options, env: dict[str, str]) -> bool:
    """Build a board with make and then CMake. The root Makefile's targets are the preset names."""
    jobs = ['-j', str(options.jobs or os.cpu_count() or 1)]
    debug = ['DEBUG=1'] if options.debug else []
    preset = b.variant(options.debug)
    ok = run([*MAKE, *jobs, b.preset, *debug], env)
    cmake = ['cmake', '--build', '--preset', preset, '--parallel', jobs[1]]
    if options.clean:
        cmake.append('--clean-first')
    return ok and run(['cmake', '--preset', preset], env) and run(cmake, env)


def source_date_epoch() -> str:
    """SOURCE_DATE_EPOCH if it is set, else the time of the HEAD commit, so GCC's __DATE__ and __TIME__
    are the same in both builds."""
    if epoch := os.environ.get('SOURCE_DATE_EPOCH'):
        return epoch
    out = subprocess.run(['git', 'log', '-1', '--format=%ct'], cwd=ROOT, capture_output=True, text=True)
    return out.stdout.strip() or '0'


# Command line


def parse_options(argv: Sequence[str]) -> Options:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    add_board_arguments(parser)
    parser.add_argument('-d', '--debug', action='store_true', help='compare the Debug builds')
    parser.add_argument('-b', '--build', action='store_true', help='build the boards with make and CMake first')
    parser.add_argument('-c', '--clean', action='store_true', help='with --build, clean first (make clean-all, then CMake --clean-first for each board)')
    parser.add_argument('-j', '--jobs', type=int, help='with --build, parallel jobs (default: all cores)')
    parser.add_argument('-k', '--keep-going', action='store_true',
                        help='with --build, carry on with the other boards after a failure')
    parser.add_argument('--ranges', type=int, default=8, metavar='N',
                        help='show up to N differing byte ranges of each image (default: 8)')
    parser.add_argument('--limit', type=int, default=20, metavar='N',
                        help='list up to N of each kind of differing symbol and string, 0 for none (default: 20)')
    ns = parser.parse_args(argv)
    if ns.clean and not ns.build:
        parser.error('--clean needs --build')
    return Options(ns.boards, ns.projects, ns.all, ns.exclude, ns.debug, ns.build, ns.clean, ns.jobs,
                   ns.keep_going, ns.ranges, ns.limit)


def main() -> int:
    options = parse_options(sys.argv[1:])
    # With no boards named, every board, less those make and CMake have not both built unless building them
    named = options.all or bool(options.boards or options.projects)
    selected = select(load_boards(), options.boards, options.projects, options.all or not named, options.exclude)
    if not named and not options.build:
        selected = [b for b in selected if images(make_dir(b)) and images(cmake_dir(b, options.debug))]
    if not selected:
        sys.exit('No board has been built with both make and CMake; name some boards, or use --build')

    failed: set[Board] = set()
    if options.build:
        env = os.environ | {'SOURCE_DATE_EPOCH': source_date_epoch()}
        print(f'Building with SOURCE_DATE_EPOCH={env["SOURCE_DATE_EPOCH"]}')
        # clean-<config> leaves the libraries built, so make cleans everything once rather than per board
        if options.clean and not run([*MAKE, 'clean-all'], env):
            sys.exit('make clean-all failed')
        for b in selected:
            if failed and not options.keep_going:
                break
            if not build(b, options, env):
                failed.add(b)

    results: list[tuple[str, str]] = []
    for b in selected:
        status, lines = ('build failed', []) if b in failed else compare(b, options)
        if status == 'not built' and not named and not options.build:
            continue
        results.append((b.preset, status))
        print(f'\n== {b.preset}: {status}')
        for line in lines:
            print('  ' + line)

    width = max(len(preset) for preset, _ in results)
    print('\nSummary (make vs CMake' + (', Debug' if options.debug else '') + ')')
    for preset, status in results:
        print(f'  {preset.ljust(width)}  {status}')
    return 0 if all(status == 'identical' for _, status in results) else 1


if __name__ == '__main__':
    sys.exit(main())
