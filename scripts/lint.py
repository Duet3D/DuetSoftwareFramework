#!/usr/bin/env python3
# pyright: strict
"""Run clang-tidy over the whole of a build, and tally what it finds.

    scripts/lint.py EXP3HC                                every project and library EXP3HC builds
    scripts/lint.py EXP3HC -f src/Duet3Expansion/src/Movement/Move.cpp
                                                          one file of it
    scripts/lint.py DuetRealtimeCore                      DuetRealtimeCore, built for this machine
    scripts/lint.py canmaster-Duet3_MB6HC --database-only just write the compile database
    scripts/lint.py canmaster --naming-fixes              export naming fixes over every board of a project

The build already lints each file as it compiles it, reporting a header's findings once (see
cmake/ClangTidy.cmake). This lints everything in one go from the build's compile database, which
reports every finding in every header and is what --fix needs. Targets are firmware boards (see
scripts/list.py) and DuetRealtimeCore

Do not --fix the naming check per board: a board's database covers only that board, so renaming from
it rewrites the shared code and that board's own headers while the other boards' keep the old names.
--naming-fixes exports the renames from every board given, to build/naming-fixes/; apply them in one
pass with
    clang-apply-replacements --format --style=file build/naming-fixes
then rebuild every board. It covers only the projects' own sources, not the libraries under lib/,
which other projects also build, and needs a project whose .clang-tidy has naming rules.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

from common.cli import parse, parser, typed
from common.dsf import REALTIME_CORE
from common.lint import board_database, export_naming_fixes, realtime_core_database, sources, tidy
from common.repo import BUILD, ROOT
from common.targets import DSF, FIRMWARE, Selection, add_target_arguments, select


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    targets: list[str]
    all: bool
    exclude: list[str]
    debug: bool
    files: list[str]
    fix: bool
    database_only: bool
    naming_fixes: bool
    out_dir: str


def parse_options(argv: Sequence[str] | None = None) -> Options:
    p = parser(__doc__)
    add_target_arguments(p)
    p.add_argument('-d', '--debug', action='store_true', help="the boards' Debug builds")
    p.add_argument('-f', '--file', dest='files', action='append', default=[], metavar='FILE',
                   help='lint only this file of the build (repeatable)')
    p.add_argument('--fix', action='store_true', help="apply clang-tidy's fixes (not for the naming check)")
    p.add_argument('--database-only', action='store_true', help='write the compile database, and lint nothing')
    p.add_argument('--naming-fixes', action='store_true', help='export the naming fixes of every board given')
    p.add_argument('-o', '--out-dir', default=str(BUILD / 'naming-fixes'), metavar='DIR',
                   help='where --naming-fixes writes them (default: build/naming-fixes)')
    return typed(Options, parse(p, argv))


def databases(options: Options, chosen: Selection) -> list[tuple[str, str, Path]]:
    """Each target's name, the source directory of its own project, and its compile database."""
    # --all takes the C++ of them, but a C# project named on its own is a mistake
    named = select(options.targets, kinds=(DSF, FIRMWARE), required=False).dsf
    if other := [p for p in named if p != REALTIME_CORE]:
        sys.exit(f'Only {REALTIME_CORE} and the firmware boards are C++ to lint, not {", ".join(other)}')
    found: list[tuple[str, str, Path]] = []
    if REALTIME_CORE in chosen.dsf:
        found.append((REALTIME_CORE, str(ROOT / 'src' / REALTIME_CORE) + '/', realtime_core_database()))
    for board in chosen.boards:
        preset = chosen.preset(board, options.debug)
        print(f'=== Compile database for {preset} ===', flush=True)
        found.append((preset, str(ROOT / 'src' / board.project) + '/', board_database(preset)))
    return found


def main() -> int:
    options = parse_options()
    chosen = select(options.targets, options.all, options.exclude)
    try:
        found = databases(options, chosen)
    except subprocess.CalledProcessError as e:
        sys.exit(f'Configuring failed: {e}')

    if options.database_only:
        for name, _, directory in found:
            print(f'{name}: clang-tidy -p {directory.relative_to(ROOT)} <file>')
        return 0

    if options.naming_fixes:
        out = Path(options.out_dir).resolve()
        shutil.rmtree(out, ignore_errors=True)
        out.mkdir(parents=True)
        for name, own, directory in found:
            files = [f for f in sources(directory) if f.startswith(own)]
            print(f'=== {name}: exporting naming fixes from {len(files)} translation units ===', flush=True)
            print(f'    {export_naming_fixes(directory, files, out, name)} non-empty fix files')
        print(f'Fix files in {out}; apply them with clang-apply-replacements --format --style=file {out}')
        return 0

    files = [str(Path(f).resolve()) for f in options.files]
    for name, _, directory in found:
        print(f'=== Linting {name} ===', flush=True)
        tidy(directory, files, options.fix)
    return 0


if __name__ == '__main__':
    sys.exit(main())
