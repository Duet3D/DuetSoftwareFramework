#!/usr/bin/env python3
# pyright: strict
"""Show the memory the built firmware images use: flash, RAM and any other linker region.

    scripts/size.py                               every built board
    scripts/size.py EXP3HC --debug                one board's Debug build
    scripts/size.py -p d3e --markdown summary.md  append a project's as a Markdown table, e.g. for a CI job summary

Boards are named as CMake presets (see scripts/boards.py); projects in full or as canmaster,
rrf, d3e, bootloader or iap. --exclude takes boards or projects.
"""

from __future__ import annotations

import argparse
import sys
from collections.abc import Sequence
from dataclasses import dataclass

from common.firmware import BUILD, add_board_arguments, load_boards, select
from common.memory import Result, add_markdown_argument, built, print_summary


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    boards: list[str]
    projects: list[str]
    all: bool
    exclude: list[str]
    debug: bool
    markdown: str | None


def parse_options(argv: Sequence[str]) -> Options:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    add_board_arguments(parser)
    parser.add_argument('-d', '--debug', action='store_true', help='the Debug variants')
    add_markdown_argument(parser)
    return Options(**vars(parser.parse_args(argv)))


def main() -> int:
    options = parse_options(sys.argv[1:])
    named = options.all or options.boards or options.projects
    selected = select(load_boards(), options.boards, options.projects, options.all or not named, options.exclude)
    results: list[Result] = []
    for board in selected:
        preset = board.variant(options.debug)
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
