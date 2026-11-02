#!/usr/bin/env python3
# pyright: strict
"""Export readability-identifier-naming fixes for a project across every one of its boards.

A compilation database only ever describes one board. Renaming from a single board's database
rewrites that board's pins header plus every use of those constants in the shared .cpp files, while
the other boards' headers keep the old names, so the other targets stop compiling. Covering all
boards and applying the merged result once is the only safe way to do it. Only the project's own
sources are covered, not the libraries under lib/, which the other projects also build.

The fixes are exported, not applied. Apply them in a single pass afterwards with
    clang-apply-replacements --format --style=file <out-dir>
then rebuild every board.

    scripts/export_naming_fixes.py -p canmaster                   into build/naming-fixes
    scripts/export_naming_fixes.py -p canmaster -o /tmp/fixes     into another directory

Projects are named in full or as canmaster, rrf, d3e, bootloader or iap; the project needs naming
rules in its .clang-tidy, which DuetCANMaster has.
"""

from __future__ import annotations

import argparse
import contextlib
import io
import shutil
import subprocess
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

from common.firmware import BUILD, ROOT, load_boards, project_boards, project_name
from gen_compile_commands import generate, sources


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    project: str
    out_dir: Path


def parse_options(argv: Sequence[str]) -> Options:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('-p', '--project', required=True, help='the project whose sources to cover')
    parser.add_argument('-o', '--out-dir', type=Path, default=BUILD / 'naming-fixes',
                        help='where to write the fixes (default: %(default)s)')
    return Options(**vars(parser.parse_args(argv)))


def main() -> int:
    options = parse_options(sys.argv[1:])
    project = project_name(options.project)
    boards = project_boards(project, load_boards())
    own = str(ROOT / 'src' / project) + '/'
    out = options.out_dir.resolve()
    shutil.rmtree(out, ignore_errors=True)
    out.mkdir(parents=True)

    failed: list[str] = []
    for b in boards:
        print('=' * 62)
        print(f'>> {b.preset}: generating compile database')
        log = io.StringIO()
        try:
            with contextlib.redirect_stdout(log):
                directory = generate(b.preset)
        except subprocess.CalledProcessError:
            (out / f'{b.preset}.build.log').write_text(log.getvalue())
            print(f'   !! configure failed, see {out / f"{b.preset}.build.log"}')
            failed.append(b.preset)
            continue
        (out / f'{b.preset}.build.log').write_text(log.getvalue())

        # Every one of the project's TUs must be covered, including ones clang reports errors in. A
        # renamed declaration in a header is only useful if the use-sites are renamed too, and a
        # use-site's replacement is emitted solely by the TU that contains it, so skipping a .cpp
        # renames the header out from under it.
        tus = [f for f in sources(directory) if f.startswith(own)]
        print(f'>> {b.preset}: exporting fixes from {len(tus)} translation units')
        for i, tu in enumerate(tus, 1):
            subprocess.run(['clang-tidy', '-p', str(directory), '--checks=-*,readability-identifier-naming',
                            f'--export-fixes={out / f"{b.preset}-{i}.yaml"}', tu],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        # clang-tidy leaves an empty file behind for a clean TU
        for empty in (f for f in out.glob(f'{b.preset}-*.yaml') if f.stat().st_size < 2):
            empty.unlink()
        print(f'>> {b.preset}: {len(list(out.glob(f"{b.preset}-*.yaml")))} non-empty fix files')

    print('=' * 62)
    if failed:
        print(f'BOARDS THAT FAILED TO CONFIGURE: {" ".join(failed)}')
        print('Fixes are incomplete -- do NOT apply them.')
        return 1
    print(f'All boards covered. Fix files in {out}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
