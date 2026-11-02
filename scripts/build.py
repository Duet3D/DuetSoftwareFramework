#!/usr/bin/env python3
# pyright: strict
"""Build the Duet3D firmware with CMake.

Every board is a CMake preset (see CMakePresets.json) with its own build tree under build/. This
script builds those presets for one or more boards, a whole project, or everything, then shows the
memory each image uses.

    scripts/build.py EXP3HC TOOL1LC               build those boards
    scripts/build.py --project bootloader         build every board of a project
    scripts/build.py --all --debug                build every board, Debug
    scripts/build.py --all -x iap -x EXP1XD       build every board but those
    scripts/build.py --clean EXP3HC               clean then build a board

Boards are named as CMake presets (see scripts/boards.py); projects in full or as canmaster,
rrf, d3e, bootloader or iap. --exclude takes boards or projects.
"""

from __future__ import annotations

import argparse
import sys
import time
from collections.abc import Sequence
from dataclasses import dataclass

from common.firmware import add_board_arguments, build, load_boards, select
from common.memory import Result, add_markdown_argument, built, print_summary


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    boards: list[str]
    projects: list[str]
    all: bool
    exclude: list[str]
    debug: bool
    jobs: int | None
    verbose: bool
    keep_going: bool
    fresh: bool
    clean: bool
    markdown: str | None


def parse_options(argv: Sequence[str]) -> Options:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    add_board_arguments(parser)
    parser.add_argument('-d', '--debug', action='store_true', help='build the Debug variants')
    parser.add_argument('-j', '--jobs', type=int, help='parallel jobs for each build (default: all cores)')
    parser.add_argument('-v', '--verbose', action='store_true', help='show the compiler command lines')
    parser.add_argument('-k', '--keep-going', action='store_true',
                        help='carry on with the other boards after a failure')
    parser.add_argument('-f', '--fresh', action='store_true', help='reconfigure from scratch')
    parser.add_argument('-c', '--clean', action='store_true', help='clean each build tree before building it')
    add_markdown_argument(parser)
    return Options(**vars(parser.parse_args(argv)))


def main() -> int:
    options = parse_options(sys.argv[1:])
    selected = select(load_boards(), options.boards, options.projects, options.all, options.exclude)
    results: list[Result] = []
    for preset in (b.variant(options.debug) for b in selected):
        if not options.keep_going and any(r.status == 'failed' for r in results):
            results.append(Result(preset, 'skipped'))
            continue
        start = time.monotonic()
        ok = build(preset, options.clean, options.fresh, options.jobs, options.verbose)
        results.append(built(preset, 'ok' if ok else 'failed', time.monotonic() - start))
    print_summary(results, options.markdown)
    print()
    failed = [r.preset for r in results if r.status == 'failed']
    if failed:
        print('Failed: ' + ', '.join(failed))
        return 1
    print('All builds succeeded; the images are in build/<preset>/')
    return 0


if __name__ == '__main__':
    sys.exit(main())
