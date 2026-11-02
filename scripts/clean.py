#!/usr/bin/env python3
# pyright: strict
"""Clean the build output of DSF projects and firmware boards, Release and Debug alike.

    scripts/clean.py EXP3HC                       clean a board's build trees
    scripts/clean.py d3e                          clean every board of a firmware project
    scripts/clean.py DuetControlServer            clean a DSF project
    scripts/clean.py --all --purge                delete every build tree and output directory

Targets are DSF projects, firmware boards and groups of them (see scripts/list.py). Cleaning runs each
build's own clean; --purge deletes the build trees instead, and with any DSF project, the staged
DuetPiManagementPlugin (build/plugins).
"""

from __future__ import annotations

import shutil
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

from common.cli import parse, parser, typed
from common.dsf import PLUGIN_BUILD_DIR, PROJECTS, REALTIME_CORE, REALTIME_CORE_DIR, TRACY
from common.repo import BUILD, ROOT, run
from common.targets import add_target_arguments, select


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    targets: list[str]
    all: bool
    exclude: list[str]
    purge: bool


def parse_options(argv: Sequence[str] | None = None) -> Options:
    p = parser(__doc__)
    add_target_arguments(p)
    p.add_argument('--purge', action='store_true', help='delete the build trees instead of cleaning them')
    return typed(Options, parse(p, argv))


def delete(tree: Path) -> None:
    if tree.exists():
        print(f'Deleting {tree.relative_to(ROOT)}')
        shutil.rmtree(tree)


def clean_cmake(tree: Path, purge: bool) -> bool:
    """Clean or delete a CMake build tree, if there is one."""
    if purge:
        delete(tree)
        return True
    return not (tree / 'CMakeCache.txt').is_file() or run(['cmake', '--build', str(tree), '--target', 'clean'])


def clean_dsf(project: str, purge: bool) -> bool:
    if project == TRACY:
        return clean_cmake(BUILD / 'tracy', True) if purge else True
    if project == REALTIME_CORE:
        trees = sorted(p for p in (REALTIME_CORE_DIR / 'build').glob('*') if p.is_dir())
        return all([clean_cmake(tree, purge) for tree in trees])
    if purge:
        for output in ('bin', 'obj'):
            delete(ROOT / PROJECTS[project] / output)
        return True
    return all([run(['dotnet', 'clean', str(ROOT / PROJECTS[project]), '-c', c, '-v', 'q'])
                for c in ('Debug', 'Release')])


def main() -> int:
    options = parse_options()
    chosen = select(options.targets, options.all, options.exclude)
    ok = all([clean_dsf(p, options.purge) for p in chosen.dsf])
    if options.purge and chosen.dsf:
        delete(PLUGIN_BUILD_DIR)
    for board in chosen.boards:
        for preset in (board.variant(False), board.variant(True)):
            tree = BUILD / preset
            if tree.is_dir():
                ok = clean_cmake(tree, options.purge) and ok
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
