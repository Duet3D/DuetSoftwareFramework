#!/usr/bin/env python3
# pyright: strict
"""List the targets the other scripts take: the DSF projects, the firmware boards by project, and the
groups that name several at once.

    scripts/list.py                               every target
    scripts/list.py d3e                           the boards of a group
    scripts/list.py --names firmware              just the names, one per line, e.g. for CI
    scripts/list.py --names --debug canmaster     the names of the boards' Debug builds

A firmware board is named by its CMake preset, with a -debug suffix for its Debug build. Boards with a
build tree under build/ are marked as built.
"""

from __future__ import annotations

import sys
from collections.abc import Sequence
from dataclasses import dataclass

from common.cli import parse, parser, typed
from common.dsf import TRACY
from common.firmware import built_image
from common.targets import ALIASES, DSF, FIRMWARE, add_target_arguments, select


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    targets: list[str]
    exclude: list[str]
    names: bool
    debug: bool


def parse_options(argv: Sequence[str] | None = None) -> Options:
    p = parser(__doc__)
    add_target_arguments(p, every=False)
    p.add_argument('-n', '--names', action='store_true', help='print only the names, one per line')
    p.add_argument('-d', '--debug', action='store_true', help="with --names, the boards' Debug builds")
    return typed(Options, parse(p, argv))


def main() -> int:
    options = parse_options()
    everything = not options.targets
    chosen = select(options.targets or [DSF, FIRMWARE], exclude=options.exclude)
    dsf = chosen.dsf + ([TRACY] if everything and TRACY not in options.exclude else [])
    if options.names:
        for name in dsf + [chosen.preset(b, options.debug) for b in chosen.boards]:
            print(name)
        return 0

    if everything:
        aliases = ', '.join(f'{alias} ({project})' for alias, project in ALIASES.items())
        print(f'Groups: {DSF} (the DSF projects), {FIRMWARE} (every board), and each firmware project, in full or as\n'
              f'        {aliases}')
    if dsf:
        print('\nDSF')
        for name in dsf:
            note = '  (only when named: the profiling client, see DEVELOPER.md)' if name == TRACY else ''
            print(f'  {name}{note}')
    project = None
    for b in chosen.boards:
        if b.project != project:
            print(f'\n{b.project}')
            project = b.project
        built = ' (built)' if built_image(b.preset, '.elf') else ''
        print(f'  {b.preset:32} {b.display}{built}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
