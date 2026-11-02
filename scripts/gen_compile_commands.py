#!/usr/bin/env python3
# pyright: strict
"""Write a compile database that clang-tidy can use, for running it by hand over a board's build.

CMake writes the board's compile database when its preset is configured. It still has to be
rewritten before clang-tidy can use it (see common/clang.py): clang against a database in that
state still reports things, but it is parsing a broken TU, so any --fix output is untrustworthy.
The database is reduced to the C++ translation units the build lints, every project's and library's
but not third-party code, and written to build/<preset>/clang-tidy/.

    scripts/gen_compile_commands.py canmaster-Duet3_MB6HC      a board's Release build
    scripts/gen_compile_commands.py EXP3HC --debug             its Debug build

Then, for example:

    clang-tidy -p build/EXP3HC/clang-tidy src/Duet3Expansion/src/Movement/Move.cpp
    run-clang-tidy -p build/EXP3HC/clang-tidy -quiet src/Duet3Expansion/

Boards are named as CMake presets (see scripts/boards.py).
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

from common.clang import cross_compile, sanitise_database
from common.firmware import BUILD, ROOT, board, load_boards


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    board: str
    debug: bool


def parse_options(argv: Sequence[str]) -> Options:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('board', help='board preset, e.g. EXP3HC or canmaster-Duet3_MB6HC')
    parser.add_argument('-d', '--debug', action='store_true', help='the Debug variant')
    return Options(**vars(parser.parse_args(argv)))


def generate(preset: str) -> Path:
    """Configure a preset and write its database for clang-tidy; returns the database's directory."""
    # Configuring writes the database for every target; nothing has to be compiled
    subprocess.run(['cmake', '--preset', preset], cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
    directory = BUILD / preset / 'clang-tidy'
    directory.mkdir(exist_ok=True)
    database = directory / 'compile_commands.json'
    shutil.copyfile(BUILD / preset / 'compile_commands.json', database)
    sanitise_database(database, cross_compile())
    return directory


def sources(directory: Path) -> list[str]:
    """The translation units in a database written by generate."""
    entries: list[dict[str, str]] = json.loads((directory / 'compile_commands.json').read_text())
    return list(dict.fromkeys(e['file'] for e in entries))


def main() -> int:
    options = parse_options(sys.argv[1:])
    preset = board(options.board, load_boards()).variant(options.debug)
    directory = generate(preset)
    print(f'>> Done. Run: clang-tidy -p {directory.relative_to(ROOT)} <file>')
    return 0


if __name__ == '__main__':
    sys.exit(main())
