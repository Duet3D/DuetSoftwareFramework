#!/usr/bin/env python3
# pyright: strict
"""Build DSF projects and firmware boards.

    scripts/build.py dsf                                  every DSF project, for linux-arm64
    scripts/build.py DuetControlServer --debug            one, as Debug (it brings DuetRealtimeCore)
    scripts/build.py dsf --aot --arch linux-arm           Native AOT, for 32-bit ARM
    scripts/build.py dsf --clean                          every DSF project, from clean
    scripts/build.py EXP3HC TOOL1LC                       firmware boards
    scripts/build.py canmaster-Duet3_MB6HC-debug          a board's Debug build
    scripts/build.py d3e -x EXP1XD --keep-going           a firmware project's boards, less one
    scripts/build.py --all --debug                        everything, as Debug
    scripts/build.py TracyClient --arch linux-arm64       the profiling client, for a Pi

Targets are DSF projects, firmware boards (CMake presets) and groups of them; scripts/list.py lists
them. Builds are Release unless --debug is given, and --clean cleans what is built first.

Each DSF project publishes to its own output directory, bin/<config>/<tfm>/<arch>/publish (bin/aot/...
with --aot), which scripts/deploy.py installs on a Pi from. Selecting DuetControlServer builds
DuetRealtimeCore, the native library it P/Invokes into, too, into build/rtc-<arch>[-debug]. That library links glibc dynamically, so it must not need a newer glibc than the
Pi has: the devcontainer matches Raspberry Pi OS Bookworm, and --sysroot or --fetch-sysroot build
against an older Pi's own libraries instead.

Each firmware board builds in its own tree, build/<preset>/, and the memory each image uses is shown
afterwards.
"""

from __future__ import annotations

import sys
import time
from collections.abc import Sequence
from dataclasses import dataclass

from common.cli import parse, parser, typed
from common.dsf import TRACY, BuildOptions, add_build_arguments, build_options, build_projects, plan
from common.firmware import build
from common.memory import Result, add_markdown_argument, built, print_summary
from common.targets import Selection, add_target_arguments, select
from common.tracy import TRACY_VERSION, build_tracy


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    targets: list[str]
    all: bool
    exclude: list[str]
    debug: bool
    keep_going: bool
    clean: bool
    # Firmware
    jobs: int | None
    verbose: bool
    fresh: bool
    markdown: str | None
    # DSF
    dsf: BuildOptions
    arch: str | None
    tracy_version: str
    on_demand: bool


def parse_options(argv: Sequence[str] | None = None) -> Options:
    p = parser(__doc__)
    add_target_arguments(p)
    p.add_argument('-d', '--debug', action='store_true', help='build Debug rather than Release')
    p.add_argument('-k', '--keep-going', action='store_true', help='carry on with the other targets after a failure')
    p.add_argument('-c', '--clean', action='store_true', help='clean what is built before building it')
    firmware = p.add_argument_group('firmware boards')
    firmware.add_argument('-j', '--jobs', type=int, help='parallel jobs for each build (default: all cores)')
    firmware.add_argument('-v', '--verbose', action='store_true', help='show the compiler command lines')
    firmware.add_argument('-f', '--fresh', action='store_true', help='reconfigure from scratch')
    add_markdown_argument(p)
    dsf = p.add_argument_group('DSF projects', f'{TRACY} is built for this machine unless --arch is given')
    add_build_arguments(dsf)
    dsf.add_argument('--tracy-version', default=TRACY_VERSION, metavar='TAG',
                     help=f'the Tracy release to build {TRACY} from (default: {TRACY_VERSION})')
    dsf.add_argument('--no-on-demand', dest='on_demand', action='store_false',
                     help=f'build {TRACY} to record from process start instead of when the GUI connects')
    namespace = parse(p, argv)
    return typed(Options, namespace, dsf=build_options(namespace))


def build_dsf(options: BuildOptions, projects: list[str]) -> bool:
    print(f'=== DSF projects for {options.arch} ({options.configuration}) ===', flush=True)
    return build_projects(plan(options, projects))


def build_firmware(options: Options, chosen: Selection) -> bool:
    results: list[Result] = []
    for preset in (chosen.preset(b, options.debug) for b in chosen.boards):
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
        return False
    print('All builds succeeded; the images are in build/<preset>/')
    return True


def main() -> int:
    options = parse_options()
    chosen = select(options.targets, options.all, options.exclude)
    projects = [p for p in chosen.dsf if p != TRACY]
    ok = True
    if projects:
        ok = build_dsf(options.dsf, projects)
    if TRACY in chosen.dsf and (ok or options.keep_going):
        build_tracy(options.arch, options.tracy_version, options.on_demand)
    if chosen.boards and (ok or options.keep_going):
        ok = build_firmware(options, chosen) and ok
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
