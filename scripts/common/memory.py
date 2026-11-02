# pyright: strict
"""The memory the built images use, and the table of builds that shows it."""

from __future__ import annotations

import argparse
import re
import struct
import sys
from dataclasses import dataclass
from pathlib import Path

from common.firmware import BUILD, read_elf


@dataclass(frozen=True)
class Region:
    """A MEMORY region of a linker script, and how much of it an image uses."""
    name: str
    origin: int
    length: int
    used: int = 0


@dataclass(frozen=True)
class Result:
    """How one preset's build went."""
    preset: str
    status: str  # 'ok', 'failed', 'skipped' or 'not built'
    seconds: float | None = None
    regions: tuple[Region, ...] = ()


def memory_usage(elf: Path, map_file: Path, image: Path | None) -> tuple[Region, ...]:
    """The memory regions of the map file, with what the ELF puts in them. A segment uses its memory
    size where it runs and, if it is loaded from elsewhere (initialised data), its file size where it
    is stored. The image counts against the region it starts in, as it may hold more than the ELF
    (a CRC, embedded files)."""
    text = map_file.read_text(errors='replace')
    config = text.split('Memory Configuration', 1)[-1].split('Linker script and memory map', 1)[0]
    regions = [(m[1], int(m[2], 16), int(m[3], 16))
               for m in re.finditer(r'^(\w+)\s+0x([0-9a-f]+)\s+0x([0-9a-f]+)', config, re.MULTILINE)]

    def region_of(address: int) -> int | None:
        return next((i for i, (_, origin, length) in enumerate(regions)
                     if origin <= address < origin + length), None)

    used = [0] * len(regions)
    segments = read_elf(elf).segments
    for vaddr, paddr, filesz, memsz in segments:
        if (run_at := region_of(vaddr)) is not None:
            used[run_at] += memsz
        if filesz and region_of(paddr) != region_of(vaddr) and (stored_at := region_of(paddr)) is not None:
            used[stored_at] += filesz
    stored = [paddr for _, paddr, filesz, _ in segments if filesz]
    if image and stored and (start := region_of(min(stored))) is not None:
        used[start] = max(used[start], image.stat().st_size)
    return tuple(Region(name, origin, length, u)
                 for (name, origin, length), u in zip(regions, used) if u)


def built(preset: str, status: str, seconds: float | None = None) -> Result:
    """The result of a build, with its image's memory usage if it succeeded."""
    tree = BUILD / preset
    elves = sorted(tree.glob('*.elf'))
    if status != 'ok' or len(elves) != 1:
        return Result(preset, status, seconds)
    elf = elves[0]
    image = elf.with_suffix('.bin')
    image = image if image.is_file() else None
    try:
        regions = memory_usage(elf, elf.with_suffix('.map'), image)
    except (OSError, ValueError, struct.error) as e:
        print(f'Cannot read the memory usage of {preset}: {e}', file=sys.stderr)
        regions = ()
    return Result(preset, status, seconds, regions)


def kib(n: int) -> str:
    return f'{n / 1024:.1f}'


NOTE = 'RAM excludes the heap and stacks allocated at run time; free RAM is what they can use.'


def summary_rows(results: list[Result]) -> list[tuple[str, ...]]:
    """A table of the builds, heading first: their outcome, time and the memory they use. The rom
    and ram regions have columns of their own; any other region in use is listed after them. The
    time and other regions columns are left out when they would be empty."""

    def usage(region: Region | None) -> str:
        if region is None:
            return '-'
        return f'{kib(region.used)}/{kib(region.length)} KiB {100 * region.used / region.length:3.0f}%'

    rows: list[tuple[str, ...]] = [('Board', 'Result', 'Time', 'Flash (rom)', 'RAM (ram)', 'Other regions')]
    for r in results:
        regions = {region.name: region for region in r.regions}
        others = ', '.join(f'{region.name} {usage(region)}'
                           for region in r.regions if region.name not in ('rom', 'ram'))
        rows.append((
            r.preset,
            r.status,
            f'{r.seconds:.0f}s' if r.seconds is not None else '',
            usage(regions.get('rom')) if r.status == 'ok' else '',
            usage(regions.get('ram')) if r.status == 'ok' else '',
            others,
        ))
    keep = [i for i in range(len(rows[0])) if i not in (2, 5) or any(row[i] for row in rows[1:])]
    return [tuple(row[i] for i in keep) for row in rows]


def text_table(rows: list[tuple[str, ...]]) -> str:
    """The table aligned in columns, the numbers right-justified."""
    widths = [max(len(row[i]) for row in rows) for i in range(len(rows[0]))]
    right = {i for i, heading in enumerate(rows[0]) if heading in ('Time', 'Flash (rom)', 'RAM (ram)')}
    lines = ['  '.join(cell.rjust(w) if i in right else cell.ljust(w)
                       for i, (cell, w) in enumerate(zip(row, widths))).rstrip() for row in rows]
    lines.insert(1, '  '.join('-' * w for w in widths))
    return '\n'.join(lines) + f'\n\n{NOTE}\n'


def markdown_table(rows: list[tuple[str, ...]]) -> str:
    """The table in GitHub-flavoured Markdown."""
    right = {i for i, heading in enumerate(rows[0]) if heading in ('Time', 'Flash (rom)', 'RAM (ram)')}
    lines = ['| ' + ' | '.join(' '.join(cell.split()) for cell in row) + ' |' for row in rows]
    lines.insert(1, '|' + '|'.join('---:' if i in right else '---' for i in range(len(rows[0]))) + '|')
    return '### Firmware memory usage\n\n' + '\n'.join(lines) + f'\n\n_{NOTE}_\n'


def add_markdown_argument(parser: argparse.ArgumentParser) -> None:
    parser.add_argument('--markdown', metavar='FILE',
                        help='write the memory usage as a Markdown table, appended to FILE '
                             '(e.g. $GITHUB_STEP_SUMMARY), or printed instead of the plain table if FILE is -')


def print_summary(results: list[Result], markdown: str | None) -> None:
    """Print the table of the builds, or with markdown, write it as Markdown: appended to that file,
    or printed if it is '-'."""
    rows = summary_rows(results)
    if markdown is None:
        print('\n' + text_table(rows), end='')
    elif markdown == '-':
        print('\n' + markdown_table(rows), end='')
    else:
        with open(markdown, 'a') as f:
            f.write(markdown_table(rows) + '\n')
        print(f'\nWrote the memory usage to {markdown}')
