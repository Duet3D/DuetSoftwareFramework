#!/usr/bin/env python3
# pyright: strict
"""Work with the submodules and the branches they track in .gitmodules.

    scripts/submodule.py check [REV]    check that each submodule committed in REV (default HEAD) is in
                                        .gitmodules and on its tracked branch, and how far behind it is
    scripts/submodule.py update         pull each tracked branch and merge it into the submodule's checkout
    scripts/submodule.py checkout       switch each submodule to its tracked branch
    scripts/submodule.py attach         put each submodule on a branch ending at the commit it has checked out
        [-b BRANCH] [--pull]            preferring BRANCH; with --pull, switch to BRANCH wherever it ends and
                                        fast-forward from origin

update, checkout and attach take submodule paths to act on only those.

Fix a check failure by merging the submodule's PR, then "scripts/submodule.py update <path>" and committing.
"""

from __future__ import annotations

import argparse
import sys
from collections.abc import Sequence

from common.cli import complete_with, parse, parser
from common.submodules import attach, branch_names, check, checkout, revisions, submodules, update


def main(argv: Sequence[str] | None = None) -> int:
    p = parser(__doc__)
    commands = p.add_subparsers(dest='command', required=True)
    check_parser = commands.add_parser('check', help='check each submodule is on its tracked branch')
    complete_with(check_parser.add_argument('rev', nargs='?', default='HEAD', help='the superproject revision to check (default: HEAD)'), revisions)
    subparsers: dict[str, argparse.ArgumentParser] = {}
    for name, description in (('update', 'merge each tracked branch into the submodule'),
                              ('checkout', 'switch each submodule to its tracked branch'),
                              ('attach', 'put each submodule on a branch ending at its commit')):
        subparsers[name] = commands.add_parser(name, help=description)
        complete_with(subparsers[name].add_argument('paths', nargs='*', help='submodules to act on (default: all)'), lambda: [s.path for s in submodules()])
    complete_with(subparsers['attach'].add_argument('--branch', '-b', help='prefer this branch, above the tracked one'), branch_names)
    subparsers['attach'].add_argument('--pull', action='store_true',
                                      help='switch to --branch wherever it ends, then fast-forward the branch '
                                           'from origin; this can change the commit')
    args = parse(p, argv)

    if args.command == 'check':
        return 0 if check(args.rev) else 1
    if args.command == 'attach':
        return attach(args.paths, args.branch, args.pull)
    return {'update': update, 'checkout': checkout}[args.command](args.paths)


if __name__ == '__main__':
    sys.exit(main())
