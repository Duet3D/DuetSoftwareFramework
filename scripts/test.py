#!/usr/bin/env python3
# pyright: strict
"""Run the tests.

    scripts/test.py unit                                          the C# unit tests, with coverage
    scripts/test.py system --filter 'FullyQualifiedName~JobControl'
                                                                  the system tests a filter names
    scripts/test.py system                                        every system test but the known gaps
    scripts/test.py system -- -p:Profiling=true                   pass the rest to dotnet test
    scripts/test.py native                                        DuetRealtimeCore's host-side tests
    scripts/test.py scripts                                       the tests of these scripts

unit runs src/UnitTests under coverage and prints line and branch coverage per assembly. system runs
src/SystemTests, which host the real DuetControlServer and libduet_realtime_core.so against a scripted
controller, naming each test as it starts and listing the slow and the failed ones afterwards; the
full suite takes many minutes. native builds DuetRealtimeCore for this machine and runs its ctest
suite. scripts runs tests/scripts, which test the scripts' command lines with the builds, git and the
boards faked, so they need no toolchain, build or hardware. `scripts/test.py <command> --help` describes each command's options.
"""

from __future__ import annotations

import argparse
import sys
from collections.abc import Sequence

from common import coverage, system_tests
from common.cli import parse, parser, typed
from common.repo import BUILD, ROOT, call, jobs


def native() -> int:
    for command in (['cmake', '--preset', 'rtc-native'], ['cmake', '--build', '--preset', 'rtc-native', f'-j{jobs()}'],
                    ['ctest', '--test-dir', str(BUILD / 'rtc-native'), '--output-on-failure']):
        if status := call(command):
            return status
    return 0


def scripts() -> int:
    return call([sys.executable, '-m', 'unittest', 'discover', '-s', str(ROOT / 'tests/scripts'), '-t', str(ROOT)])


def main(argv: Sequence[str] | None = None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)
    # Everything after "--" goes to dotnet test as it is
    split = args.index('--') if '--' in args else len(args)
    passthrough = args[split + 1:]

    p = parser(__doc__)
    commands = p.add_subparsers(dest='command', metavar='COMMAND', required=True)
    unit = commands.add_parser('unit', help='the C# unit tests, with coverage', description=coverage.__doc__,
                               formatter_class=argparse.RawDescriptionHelpFormatter)
    coverage.add_arguments(unit)
    system = commands.add_parser('system', help='the system tests', description=system_tests.__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter,
                                 usage='%(prog)s [options] [-- <dotnet test args>]')
    system_tests.add_arguments(system)
    commands.add_parser('native', help="DuetRealtimeCore's host-side tests")
    commands.add_parser('scripts', help="the tests of these scripts, which need no build or hardware")
    namespace = parse(p, args[:split])

    if namespace.command == 'unit':
        return coverage.run(typed(coverage.Options, namespace))
    if namespace.command == 'system':
        return system_tests.run(typed(system_tests.Options, namespace, dotnet_args=passthrough))
    if namespace.command == 'scripts':
        return scripts()
    return native()


if __name__ == '__main__':
    sys.exit(main())
