#!/usr/bin/env python3
# pyright: strict
"""Check that this checkout and machine can build everything.

    scripts/doctor.py                             every check
    scripts/doctor.py tools                       the tools the builds need are installed
    scripts/doctor.py submodules                  every submodule is on the branch .gitmodules names
    scripts/doctor.py submodules --rev origin/v3.6-dev
                                                  as committed in another revision

tools looks for the tools on PATH, and that the submodules are checked out. submodules checks that each
submodule commit recorded in HEAD (or --rev) is on its tracked branch, as CI does for pull requests:
a PR may point a submodule at an unmerged PR of its own while the two are tested together, but has to
point it at its tracked branch before it is merged. Commits behind the branch pass, and are listed with
how far behind they are.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from collections.abc import Sequence
from dataclasses import dataclass

from common.cli import complete_with, parse, parser, typed
from common.repo import ROOT
from common import submodules

CHECKS = ('tools', 'submodules')

# What builds what, so a missing tool says what cannot be built without it
TOOLS = {
    'cmake': 'every native and firmware build',
    'ninja': 'the firmware',
    'arm-none-eabi-gcc': 'the firmware',
    'clang-tidy': 'the linting during builds',
    'run-clang-tidy': 'scripts/lint.py',
    'dotnet': 'the DSF projects and the tests',
    'aarch64-linux-gnu-g++': 'DuetRealtimeCore for linux-arm64',
    'arm-linux-gnueabihf-g++': 'DuetRealtimeCore for linux-arm',
    'rsync': 'deploying the DSF projects',
    'ssh': 'deploying the DSF projects',
}
OPTIONAL = {'ccache': 'faster rebuilds of the firmware'}


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    checks: list[str]
    rev: str


def parse_options(argv: Sequence[str] | None = None) -> Options:
    p = parser(__doc__)
    complete_with(p.add_argument('checks', nargs='*', metavar='CHECK',
                                 help=f'the checks to run: {", ".join(CHECKS)} (default: all)'), lambda: CHECKS)
    p.add_argument('--rev', default='HEAD', help='the revision whose submodules to check (default: HEAD)')
    options = typed(Options, parse(p, argv))
    if unknown := [c for c in options.checks if c not in CHECKS]:
        p.error(f'unknown check {", ".join(unknown)}; the checks are {", ".join(CHECKS)}')
    return options


def check_tools() -> bool:
    print('Tools')
    ok = True
    for tool, needed in {**TOOLS, **OPTIONAL}.items():
        found = shutil.which(tool)
        print(f'  {tool:24} {found or "missing, needed for " + needed}')
        ok = ok and (found is not None or tool in OPTIONAL)
    status = subprocess.run(['git', 'submodule', 'status', '--recursive'], cwd=ROOT, capture_output=True, text=True)
    # A leading '-' marks a submodule that is not checked out; pico-sdk is never needed
    missing = [line.split()[1] for line in status.stdout.splitlines()
               if line.startswith('-') and 'pico-sdk' not in line]
    print('Submodules')
    if missing:
        print(f'  not checked out: {" ".join(missing)}\n  check them out with: git submodule update --init --recursive')
    else:
        print('  all checked out')
    return ok and not missing


def main() -> int:
    options = parse_options()
    checks = options.checks or list(CHECKS)
    ok = True
    if 'tools' in checks:
        ok = check_tools() and ok
    if 'submodules' in checks:
        print(f'Submodule branches ({options.rev})')
        ok = submodules.check(options.rev) and ok
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
