#!/usr/bin/env python3
# pyright: strict
"""Clean the boards' CMake build trees under build/, both Release and Debug.

    scripts/clean.py EXP3HC                       clean a board's build trees
    scripts/clean.py -p rrf                       clean every board of a project
    scripts/clean.py --all --purge                delete every build tree

Boards are named as CMake presets (see scripts/boards.py); projects in full or as canmaster,
rrf, d3e, bootloader or iap. --exclude takes boards or projects.
"""

from __future__ import annotations

import argparse
import shutil
import sys
from collections.abc import Sequence
from dataclasses import dataclass

from common.firmware import BUILD, ROOT, add_board_arguments, load_boards, run, select


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    boards: list[str]
    projects: list[str]
    all: bool
    exclude: list[str]
    purge: bool


def parse_options(argv: Sequence[str]) -> Options:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    add_board_arguments(parser)
    parser.add_argument('--purge', action='store_true', help='delete the build trees instead of cleaning them')
    return Options(**vars(parser.parse_args(argv)))


def main() -> int:
    options = parse_options(sys.argv[1:])
    ok = True
    for board in select(load_boards(), options.boards, options.projects, options.all, options.exclude):
        for preset in (board.variant(False), board.variant(True)):
            tree = BUILD / preset
            if not tree.is_dir():
                continue
            if options.purge:
                print(f'Deleting {tree.relative_to(ROOT)}')
                shutil.rmtree(tree)
            else:
                ok = run(['cmake', '--build', '--preset', preset, '--target', 'clean']) and ok
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
