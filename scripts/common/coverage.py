# pyright: strict
"""Running the C# unit tests under coverage and printing line and branch coverage per assembly: the
unit command of scripts/test.py.

    scripts/test.py unit                          run them and summarise the coverage
    scripts/test.py unit --threshold 35           and fail if total line coverage is under 35%

Coverage comes from the coverlet.collector package reference in UnitTests.csproj, so nothing has to be
installed for this to work. What is measured, and what is left out, is configured in
src/UnitTests/coverlet.runsettings rather than here.

The run writes <results-dir>/coverage.cobertura.xml, which is what CI publishes and what report viewers
read, and <results-dir>/UnitTests.trx. For a browsable HTML report, install reportgenerator and point it
at the Cobertura file:

    dotnet tool install -g dotnet-reportgenerator-globaltool
    reportgenerator -reports:test-results/coverage.cobertura.xml -targetdir:coverage-report
"""

from __future__ import annotations

import argparse
import shutil
import sys
import xml.etree.ElementTree as ElementTree
from dataclasses import dataclass
from pathlib import Path

from common.repo import RESULTS, ROOT, call

PROJECT = ROOT / 'src' / 'UnitTests' / 'UnitTests.csproj'
RUNSETTINGS = ROOT / 'src' / 'UnitTests' / 'coverlet.runsettings'
# Tests in the RequiresMachine category talk to a real Duet or SBC over HTTP. They skip themselves
# unless DSF_TEST_MACHINE_URL is set, but excluding them explicitly keeps them from stalling a run.
FILTER = 'TestCategory!=RequiresMachine'


@dataclass(frozen=True)
class Options:
    """The unit command's options."""
    configuration: str
    results_dir: str
    threshold: float | None
    filter: str


def add_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument('-c', '--configuration', default='Debug', metavar='CFG',
                        help='build configuration (default: Debug)')
    parser.add_argument('-o', '--results-dir', default=str(RESULTS), metavar='DIR',
                        help='where to write the results (default: test-results)')
    parser.add_argument('--threshold', type=float, metavar='PCT',
                        help='exit non-zero if total line coverage is below this percentage')
    parser.add_argument('--filter', default=FILTER, metavar='EXPR', help=f'NUnit test filter (default: {FILTER})')


def percent(rate: str | None) -> str:
    return f'{float(rate or 0) * 100:.1f}'


def run(options: Options) -> int:
    results_dir = Path(options.results_dir).resolve()

    # Each run writes into a fresh GUID directory under the results directory. Clearing them first means
    # the newest report can be found without having to guess which GUID belongs to this run.
    if results_dir.is_dir():
        for stale in results_dir.iterdir():
            if stale.is_dir() and not stale.is_symlink():
                shutil.rmtree(stale)
    results_dir.mkdir(parents=True, exist_ok=True)

    print(f'=== Running unit tests with coverage ({options.configuration}) ===', flush=True)
    status = call(['dotnet', 'test', str(PROJECT), '-c', options.configuration, '--filter', options.filter,
                   '--settings', str(RUNSETTINGS), '--collect', 'XPlat Code Coverage',
                   '--logger', 'trx;LogFileName=UnitTests.trx', '--results-directory', str(results_dir)], cwd=None)
    if status:
        return status

    report = results_dir / 'coverage.cobertura.xml'
    generated = next((p for p in results_dir.rglob('coverage.cobertura.xml') if p != report), None)
    if generated is None:
        sys.exit(f'Error: the test run produced no coverage report under {results_dir}')
    generated.replace(report)

    coverage = ElementTree.parse(report).getroot()
    rule = f'{"-" * 28} {"-" * 9} {"-" * 9}'
    print('\n=== Coverage ===')
    print(f'{"Assembly":<28} {"Line":>9} {"Branch":>9}')
    print(rule)
    for package in coverage.iterfind('packages/package'):
        print(f'{package.get("name", ""):<28} {percent(package.get("line-rate")):>8}% '
              f'{percent(package.get("branch-rate")):>8}%')
    line_percent = percent(coverage.get('line-rate'))
    print(rule)
    print(f'{"TOTAL":<28} {line_percent:>8}% {percent(coverage.get("branch-rate")):>8}%')
    print(f'\nLines covered: {coverage.get("lines-covered")} / {coverage.get("lines-valid")}')
    print(f'Report:        {report}', flush=True)

    if options.threshold is not None:
        if float(line_percent) < options.threshold:
            print(f'\nError: total line coverage {line_percent}% is below the {options.threshold:g}% threshold',
                  file=sys.stderr)
            return 1
        print(f'Threshold:     {line_percent}% >= {options.threshold:g}%')
    return 0
