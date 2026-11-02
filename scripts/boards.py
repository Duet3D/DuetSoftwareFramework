#!/usr/bin/env python3
# pyright: strict
"""List the boards, by project: their CMake presets, which the other scripts take as board names.

    scripts/boards.py                             every board
    scripts/boards.py -p d3e                      the boards of a project

Projects can be named in full or as canmaster, rrf, d3e, bootloader or iap. Boards with a build
tree under build/ are marked as built.
"""

from __future__ import annotations

import argparse
import sys

from common.firmware import BUILD, load_boards, project_name


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('-p', '--project', dest='projects', action='append', default=[],
                        help='only this project (repeatable)')
    wanted = {project_name(p) for p in parser.parse_args().projects}
    project = None
    for board in load_boards():
        if wanted and board.project not in wanted:
            continue
        if board.project != project:
            print(f'\n{board.project}')
            project = board.project
        built = ' (built)' if (BUILD / board.preset).is_dir() else ''
        print(f'  {board.preset:32} {board.display}{built}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
