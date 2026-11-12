# pyright: strict
"""Running the emulation tests: the emulation command of scripts/test.py.

    scripts/test.py emulation                                   every emulation test
    scripts/test.py emulation --build                           build the firmware images first
    scripts/test.py emulation --stack dsf                       only the scenarios on DuetControlServer
    scripts/test.py emulation --filter 'FullyQualifiedName~Heater'
    scripts/test.py emulation -- --logger 'console;verbosity=detailed'

The tests run the firmware images in build/<preset>/ in Renode, which the devcontainer installs in
/opt/renode: --build builds the images first (the presets in PRESETS). Each fixture's logs, CAN and I/O
traces and DuetControlServer log are kept in build/emulation/runs/<fixture>. The results are written to
<results-dir>/EmulationTests.trx.
"""

from __future__ import annotations

import argparse
import shutil
from dataclasses import dataclass
from pathlib import Path

from common.repo import RESULTS, ROOT, call

PROJECT = ROOT / 'src' / 'EmulationTests' / 'EmulationTests.csproj'

# The firmware the bench runs: every FirmwarePreset and BootloaderPreset in
# src/DuetEmulation/Boards/BoardTypes.cs, which src/EmulationTests/Harness/BoardTypeTests.cs checks
PRESETS = ['Duet3Firmware_MB6HC', 'DuetCANMaster_MB6HC', 'Duet3Firmware_EXP3HC', 'Duet3Firmware_EXP1HCL', 'Duet3Bootloader-SAME5x']

STACKS = {'rrf': 'Rrf', 'dsf': 'Dsf'}


@dataclass(frozen=True)
class Options:
    """The emulation command's options."""
    configuration: str
    results_dir: str
    filter: str | None
    stack: str | None
    build: bool
    dotnet_args: list[str]


def add_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument('-c', '--configuration', default='Debug', metavar='CFG',
                        help='build configuration of the test assembly (default: Debug)')
    parser.add_argument('-o', '--results-dir', default=str(RESULTS), metavar='DIR',
                        help='where to write the results (default: test-results)')
    parser.add_argument('--filter', metavar='EXPR', help='test filter, which --stack narrows when both are given')
    parser.add_argument('--stack', choices=sorted(STACKS),
                        help='only the scenarios on this stack, and the tests that run on it alone')
    parser.add_argument('--build', action='store_true', help='build the firmware images the bench runs first')


def test_filter(options: Options) -> str | None:
    """The dotnet test filter the options compose."""
    clauses: list[str] = []
    if options.filter:
        clauses.append(options.filter)
    if options.stack:
        # Each fixture says which stack it runs on with a category
        other = next(n for n in STACKS.values() if n != STACKS[options.stack])
        clauses.append(f'TestCategory!={other}')
    if not clauses:
        return None
    return '&'.join(f'({c})' if len(clauses) > 1 else c for c in clauses)


def run(options: Options) -> int:
    if not shutil.which('renode') and not Path('/opt/renode/renode').exists():
        print('Renode is not installed: see .devcontainer/install-renode.sh')
        return 1
    if options.build and (status := call([str(ROOT / 'scripts' / 'build.py'), *PRESETS])):
        return status

    command = ['dotnet', 'test', str(PROJECT), '-c', options.configuration,
               '--logger', 'trx;LogFileName=EmulationTests.trx', '--results-directory', options.results_dir]
    if (expression := test_filter(options)) is not None:
        command += ['--filter', expression]
    return call(command + options.dotnet_args)
