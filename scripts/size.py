#!/usr/bin/env python3
# pyright: strict
"""Show the memory the built firmware images use: flash, RAM and any other linker region.

    scripts/size.py                               every built board
    scripts/size.py EXP3HC --debug                one board's Debug build
    scripts/size.py d3e --markdown summary.md     append a project's as a Markdown table, e.g. for a CI job summary

Targets are firmware boards and groups of them (see scripts/list.py).
"""

from __future__ import annotations

import sys
from collections.abc import Sequence
from dataclasses import dataclass

from common.cli import parse, parser, typed
from common.memory import Result, add_markdown_argument, built, print_summary
from common.repo import BUILD
from common.targets import FIRMWARE, add_target_arguments, select


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    targets: list[str]
    all: bool
    exclude: list[str]
    debug: bool
    markdown: str | None


def parse_options(argv: Sequence[str] | None = None) -> Options:
    p = parser(__doc__)
    add_target_arguments(p, kinds=(FIRMWARE,))
    p.add_argument('-d', '--debug', action='store_true', help='the Debug builds')
    add_markdown_argument(p)
    return typed(Options, parse(p, argv))


def main() -> int:
    options = parse_options()
    named = bool(options.all or options.targets)
    chosen = select(options.targets, options.all or not named, options.exclude, kinds=(FIRMWARE,))
    results: list[Result] = []
    for board in chosen.boards:
        preset = chosen.preset(board, options.debug)
        if any((BUILD / preset).glob('*.elf')):
            results.append(built(preset, 'ok'))
        elif named:
            results.append(Result(preset, 'not built'))
    if not results:
        sys.exit('No board has been built')
    print_summary(results, options.markdown)
    return 0


if __name__ == '__main__':
    sys.exit(main())
