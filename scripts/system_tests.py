#!/usr/bin/env python3
# pyright: strict
"""Run the system tests, naming each test as it starts, then list the slow tests and the failed ones.

    scripts/system_tests.py                                             every scenario but the known gaps
    scripts/system_tests.py --skip-long-running                         and not the slowest either
    scripts/system_tests.py --all                                       every scenario
    scripts/system_tests.py --filter 'FullyQualifiedName~JobControl'    the scenarios a filter names
    scripts/system_tests.py --filter 'FullyQualifiedName~JobControl' --skip-long-running
    scripts/system_tests.py --all --tag-long-running --slow 20          write LongRunning into the sources
    scripts/system_tests.py -- -p:Profiling=true                        pass the rest to dotnet test

The results are written to <results-dir>/SystemTests.trx. The per-test progress comes from
src/SystemTests/Host/TestProgress.cs; the summaries are this script's own, gathered from the TRX once
the run is over, each name with the source location a terminal can turn into a link.

Two categories in the sources say what a run is expected to look like: KnownGap marks a scenario the
source does not satisfy yet, because the behaviour is unimplemented or implemented incorrectly, and
LongRunning one that costs far more than a scenario normally does. Without the tagging flags the run
is reported against them: failures are split into the ones already marked KnownGap and the ones that
are not, tests marked KnownGap that passed are listed on their own, and the slow list is split and
followed by the tests marked LongRunning that came in under the threshold. --tag-known-gaps and
--tag-long-running write the differences into the sources instead.
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ElementTree
from collections.abc import Iterable, Sequence
from dataclasses import dataclass, replace
from pathlib import Path

from common.dsf import RESULTS, ROOT, call

PROJECT = ROOT / 'src' / 'SystemTests' / 'SystemTests.csproj'
SOURCES = ROOT / 'src' / 'SystemTests'
KNOWN_GAP = 'KnownGap'
LONG_RUNNING = 'LongRunning'
# A scenario starts a DuetControlServer and drives it, so a second is normal and ten is not. Tests over
# this are listed after the run, slowest first, and it is what the LongRunning category is measured against.
SLOW_SECONDS = 5.0


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    configuration: str
    results_dir: str
    filter: str | None
    skip_known_gaps: bool | None
    skip_long_running: bool | None
    all: bool
    no_build: bool
    slow: float
    tag_known_gaps: bool
    tag_long_running: bool
    dotnet_args: list[str]


def parse_options(argv: Sequence[str]) -> Options:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter,
                                     usage='%(prog)s [options] [-- <dotnet test args>]')
    parser.add_argument('-c', '--configuration', default='Debug', metavar='CFG',
                        help='build configuration (default: Debug)')
    parser.add_argument('-o', '--results-dir', default=str(RESULTS), metavar='DIR',
                        help='where to write the results (default: test-results)')
    parser.add_argument('--filter', metavar='EXPR',
                        help='test filter, in place of the one the skips below compose. A skip that was asked for '
                             'narrows it further')
    parser.add_argument('--skip-known-gaps', action='store_true', default=None,
                        help=f'leave out the {KNOWN_GAP} scenarios, which fail until the source they describe is '
                             'fixed. On by default for a run that named no filter')
    parser.add_argument('--skip-long-running', action='store_true', default=None,
                        help=f'leave out the {LONG_RUNNING} scenarios, the slowest in the suite')
    parser.add_argument('--all', action='store_true',
                        help=f'run every scenario, the {KNOWN_GAP} and {LONG_RUNNING} ones included')
    parser.add_argument('--no-build', action='store_true', help='skip the build and run the assembly as it stands')
    parser.add_argument('--slow', type=float, default=SLOW_SECONDS, metavar='SECONDS',
                        help=f'list tests slower than this afterwards (default: {SLOW_SECONDS:g})')
    parser.add_argument('--tag-known-gaps', action='store_true',
                        help=f'write the {KNOWN_GAP} category into the sources to match the run: add it to the '
                             f'tests that failed, remove it from the ones that passed. Runs the {KNOWN_GAP} '
                             'scenarios, since a test that did not run says nothing about which category it belongs in')
    parser.add_argument('--tag-long-running', action='store_true',
                        help=f'write the {LONG_RUNNING} category into the sources to match the run: add it to the '
                             'tests over the slow threshold, remove it from the ones under')
    # Everything after "--" goes to dotnet test as it is
    split = argv.index('--') if '--' in argv else len(argv)
    return Options(**vars(parser.parse_args(argv[:split])), dotnet_args=list(argv[split + 1:]))


def test_filter(options: Options) -> str | None:
    """The filter the run is given. A skip that was asked for narrows the filter whether or not one was
    named, while the default skip of the KnownGap scenarios applies only to a run that named none: a
    filter states which tests the run is about, and a skip it did not ask for has no say in that."""
    skip_gaps = options.skip_known_gaps
    skip_long = options.skip_long_running
    if options.all:
        skip_gaps = skip_long = False
    # A category cannot be taken off a test that never ran, so asking to tag the KnownGap scenarios is
    # asking to run them, unless the run was told otherwise
    if options.tag_known_gaps and options.filter is None and skip_gaps is None:
        skip_gaps = False
        print(f'--tag-known-gaps runs the {KNOWN_GAP} scenarios as well, so the category can be taken off the '
              'ones that pass')
    if skip_gaps is None and options.filter is None:
        skip_gaps = True
    skips = [f'TestCategory!={c}' for c, skip in ((KNOWN_GAP, skip_gaps), (LONG_RUNNING, skip_long)) if skip]
    if not skips:
        return options.filter
    # The named filter is parenthesised because it may be a disjunction, which the conjunction joining
    # the two would otherwise bind tighter than
    return '&'.join(([f'({options.filter})'] if options.filter else []) + skips)


# The results


@dataclass(frozen=True)
class Declaration:
    """Where a test is written, and the categories standing over it."""
    file: str  # relative to the repository
    line: int
    categories: tuple[str, ...]

    @property
    def location(self) -> str:
        return f'{self.file}:{self.line}'


@dataclass(frozen=True)
class Result:
    outcome: str
    seconds: float
    name: str
    declaration: Declaration | None

    @property
    def location(self) -> str:
        return self.declaration.location if self.declaration else ''

    @property
    def categories(self) -> tuple[str, ...]:
        return self.declaration.categories if self.declaration else ()


@dataclass(frozen=True)
class Difference:
    """A category the run says a declaration should gain or lose."""
    declaration: Declaration
    category: str
    action: str  # 'add' or 'remove'
    name: str
    seconds: float


def local_name(tag: str) -> str:
    return tag.rpartition('}')[2]


def seconds(duration: str) -> float:
    """HH:MM:SS.fffffff, to the tenth of a second the summaries show."""
    parts = duration.split(':')
    if len(parts) != 3:
        return 0.0
    return round(int(parts[0]) * 3600 + int(parts[1]) * 60 + float(parts[2]), 1)


def read_trx(trx: Path) -> list[tuple[str, float, str]]:
    """The outcome, seconds and name of each result. A result carries only the method name, which is
    ambiguous across fixtures and is not what --filter matches on; the fully qualified name is on the
    TestMethod of the definition the result points at by id."""
    names: dict[str, str] = {}
    results: list[tuple[str, str, float]] = []
    for element in ElementTree.parse(trx).iter():
        tag = local_name(element.tag)
        if tag == 'UnitTest':
            method = next((e for e in element.iter() if local_name(e.tag) == 'TestMethod'), None)
            if method is not None:
                names[element.get('id', '')] = f'{method.get("className", "")}.{method.get("name", "")}'
        elif tag == 'UnitTestResult' and element.get('testId'):
            results.append((element.get('testId', ''), element.get('outcome', ''),
                            seconds(element.get('duration', ''))))
    return [(outcome, elapsed, names.get(tid, '?')) for tid, outcome, elapsed in results]


NAMESPACE = re.compile(r'^\s*namespace\s+([^\s;{]+)')
# Nested types are not used, so the innermost class seen is the one a declaration belongs to. The whole
# opening of the line has to match, or the word class in a comment would count as one.
CLASS = re.compile(r'^\s*(?:(?:public|internal|private|protected|abstract|sealed|static|partial|file)\s+)*'
                   r'(?:class|record|struct)\s+([A-Za-z_]\w*)')
CATEGORY = re.compile(r'Category\("([^"]*)"\)')
TEST_NAME = re.compile(r'TestName\s*=\s*"([^"]*)"')
MEMBER = re.compile(r'^\s*(?:public|protected|internal|private)\s')
MEMBER_NAME = re.compile(r'([A-Za-z_]\w*)\s*$')


def declarations() -> dict[str, Declaration]:
    """The tests in the sources, by fully qualified name. Neither the TRX nor the runner records where a
    test is written or which categories it carries, so both are resolved from the sources: the
    namespace and class enclosing a declaration make up the name the TRX reports, the line is the
    method carrying it, and the categories are the attributes standing over it."""
    found: dict[str, Declaration] = {}
    for path in sorted(SOURCES.rglob('*.cs')):
        relative = path.relative_to(SOURCES).parts
        if 'obj' in relative or 'bin' in relative:
            continue
        file = str(path.relative_to(ROOT))
        namespace = cls = ''
        categories: list[str] = []
        test_names: list[str] = []
        for number, line in enumerate(path.read_text(errors='replace').splitlines(), 1):
            if match := NAMESPACE.match(line):
                namespace = match[1]
                continue
            if match := CLASS.match(line):
                cls = match[1]
                categories, test_names = [], []
                continue
            # Attributes stand between the documentation and the declaration they belong to, so what has
            # been seen since the last declaration is what the next one carries. A TestCase naming itself
            # takes over the name the TRX reports for that case.
            if line.lstrip().startswith('['):
                categories += CATEGORY.findall(line)
                test_names += TEST_NAME.findall(line)
            # A member declaration opens with an access modifier and names itself just before its
            # parameter list. That also catches constructors and helpers, which cost an unused entry.
            if not MEMBER.match(line) or '(' not in line or not cls:
                continue
            head = line[:line.index('(')]
            # An assignment before the parenthesis means a field or a property, not a member
            if '=' in head or not (match := MEMBER_NAME.search(head)):
                continue
            prefix = f'{namespace}.{cls}' if namespace else cls
            declaration = Declaration(file, number, tuple(categories))
            for name in [match[1], *test_names]:
                found.setdefault(f'{prefix}.{name}', declaration)
            categories, test_names = [], []
    return found


def resolve(rows: list[tuple[str, float, str]], index: dict[str, Declaration]) -> list[Result]:
    """The results with their declarations. A name ends in the arguments of a TestCase, which the
    declaration does not, so the lookup is on the name with those cut off. A test the index has nothing
    for, such as one generated at runtime, is printed without a location and is never tagged."""
    return [Result(outcome, elapsed, name, index.get(name.split('(', 1)[0])) for outcome, elapsed, name in rows]


def differences(results: list[Result], limit: float) -> list[Difference]:
    """Where the run disagrees with the categories in the sources. Belonging to a category is a property
    of the declaration rather than of one result, because a TestCase method reports a result per case:
    it is a known gap while any of its cases fails, and long running while its slowest case is over the
    threshold."""
    shown: dict[Declaration, str] = {}
    failed: set[Declaration] = set()
    passed: set[Declaration] = set()
    slowest: dict[Declaration, float] = {}
    for r in results:
        d = r.declaration
        if d is None:
            continue
        if d not in shown or r.outcome == 'Failed':
            shown[d] = r.name
        if r.outcome == 'Failed':
            failed.add(d)
        elif r.outcome == 'Passed':
            passed.add(d)
        slowest[d] = max(slowest.get(d, 0.0), r.seconds)

    found: list[Difference] = []
    for d in shown:
        # A test that was skipped or never executed says nothing either way, so only a run that reached
        # a verdict is allowed to take a category off a declaration
        ran = d in failed or d in passed
        if d in failed and KNOWN_GAP not in d.categories:
            found.append(Difference(d, KNOWN_GAP, 'add', shown[d], slowest[d]))
        elif ran and d not in failed and KNOWN_GAP in d.categories:
            found.append(Difference(d, KNOWN_GAP, 'remove', shown[d], slowest[d]))
        if slowest[d] > limit and LONG_RUNNING not in d.categories:
            found.append(Difference(d, LONG_RUNNING, 'add', shown[d], slowest[d]))
        elif ran and slowest[d] <= limit and LONG_RUNNING in d.categories:
            found.append(Difference(d, LONG_RUNNING, 'remove', shown[d], slowest[d]))
    return sorted(found, key=lambda x: (x.category, x.declaration.file, x.declaration.line))


def apply_tags(edits: list[Difference]) -> None:
    """Write categories into the sources. Adding one writes an attribute line over the declaration, at
    the end of the attribute block it already carries; removing one deletes the line holding it from
    that block. Each file is rewritten in one pass, so the line numbers the run resolved stay valid
    however many edits it takes."""
    for file in sorted({e.declaration.file for e in edits}):
        path = ROOT / file
        # Bytes in and out, so the file keeps its line endings and any byte order mark
        lines = path.read_bytes().decode().splitlines(keepends=True)
        added: dict[int, list[str]] = {}
        drop: set[int] = set()
        for e in (e for e in edits if e.declaration.file == file):
            at = e.declaration.line - 1
            if e.action == 'add':
                added.setdefault(at, []).append(e.category)
                continue
            # The attribute is somewhere in the block over the declaration: the lines above it that open with [
            for line in range(at - 1, -1, -1):
                if not lines[line].lstrip().startswith('['):
                    break
                if f'Category("{e.category}")' in lines[line]:
                    drop.add(line)
                    break
        rewritten: list[str] = []
        for number, text in enumerate(lines):
            if number in added:
                indent = text[:len(text) - len(text.lstrip(' \t'))]
                ending = text[len(text.rstrip('\r\n')):] or '\n'
                rewritten += [f'{indent}[Category("{c}")]{ending}' for c in added[number]]
            if number not in drop:
                rewritten.append(text)
        path.write_bytes(''.join(rewritten).encode())


def retagged(results: list[Result], edits: list[Difference]) -> list[Result]:
    """The results with the categories just written into the sources, so a test tagged a moment ago is
    not reported again as one that is not tagged."""
    def updated(d: Declaration) -> Declaration:
        categories = list(d.categories)
        for e in edits:
            if (e.declaration.file, e.declaration.line) == (d.file, d.line):
                if e.action == 'add':
                    categories.append(e.category)
                elif e.category in categories:
                    categories.remove(e.category)
        return replace(d, categories=tuple(categories))

    return [replace(r, declaration=updated(r.declaration)) if r.declaration else r for r in results]


# The summaries


def slow_rows(rows: Iterable[tuple[float, str, str]]) -> list[str]:
    """Seconds, name and location, slowest first."""
    lines: list[str] = []
    for elapsed, name, location in sorted(rows, key=lambda r: (-r[0], r[1])):
        lines.append(f'{elapsed:8.1f} s  {name}')
        if location:
            lines.append(' ' * 11 + location)
    return lines


def named_rows(rows: Iterable[tuple[str, str]]) -> list[str]:
    """Name and location, by name."""
    lines: list[str] = []
    for name, location in sorted(rows):
        lines.append(f'  {name}')
        if location:
            lines.append(f'    {location}')
    return lines


def section(title: str, body: list[str]) -> None:
    if body:
        print(f'\n=== {title} ===')
        print('\n'.join(body))


def summarise(options: Options, trx: Path) -> None:
    results = resolve(read_trx(trx), declarations())
    limit = options.slow
    found = differences(results, limit)

    def acted_on(d: Difference) -> bool:
        return (d.category == KNOWN_GAP and options.tag_known_gaps) or \
            (d.category == LONG_RUNNING and options.tag_long_running)

    tagged = [d for d in found if acted_on(d)]
    if tagged:
        apply_tags(tagged)
        print('\n=== Categories written into the sources ===')
        for d in tagged:
            print(f'  {d.action:<6} {d.category:<12} {d.name}\n           {d.declaration.location}')
        results = retagged(results, tagged)

    # A difference the tagging flags acted on is settled and is not reported again
    remaining = [d for d in found if not acted_on(d)]
    stale_gaps = named_rows((d.name, d.declaration.location) for d in remaining
                            if d.category == KNOWN_GAP and d.action == 'remove')
    stale_long = slow_rows((d.seconds, d.name, d.declaration.location) for d in remaining
                           if d.category == LONG_RUNNING and d.action == 'remove')
    slow = [r for r in results if r.seconds > limit]
    section(f'Tests over {limit:g}s that are not marked {LONG_RUNNING}',
            slow_rows((r.seconds, r.name, r.location) for r in slow if LONG_RUNNING not in r.categories))
    section(f'Tests over {limit:g}s marked {LONG_RUNNING}',
            slow_rows((r.seconds, r.name, r.location) for r in slow if LONG_RUNNING in r.categories))
    section(f'Marked {LONG_RUNNING} but under {limit:g}s in this run', stale_long)

    failures = [r for r in results if r.outcome == 'Failed']
    section('Known gaps that failed as expected',
            named_rows((r.name, r.location) for r in failures if KNOWN_GAP in r.categories))
    section(f'Marked {KNOWN_GAP} but passed in this run', stale_gaps)

    unexpected = named_rows((r.name, r.location) for r in failures if KNOWN_GAP not in r.categories)
    print()
    if not unexpected:
        print('=== No unexpected failures ===')
    else:
        print('=== Failed tests ===')
        print('\n'.join(unexpected))
        print('\nRerun one of them with:')
        print('  scripts/system_tests.py --filter "FullyQualifiedName~<test-name>"')
    print(f'Results: {trx}')


def main() -> int:
    options = parse_options(sys.argv[1:])
    selection = test_filter(options)

    # The trx logger resolves its file name inside the results directory, so it is given the name alone
    # and the directory is made absolute here; a relative one would be taken twice, once by the runner
    # and once by this script, leaving the results a directory deeper than the summaries look for them
    results_dir = Path(options.results_dir).resolve()
    results_dir.mkdir(parents=True, exist_ok=True)
    trx = results_dir / 'SystemTests.trx'
    trx.unlink(missing_ok=True)

    described = options.configuration + (f', {selection}' if selection else '')
    print(f'=== Running system tests ({described}) ===', flush=True)
    # The console logger at normal verbosity is what streams the progress; its default verbosity holds
    # everything back until the run is over. -tl:off because MSBuild's terminal logger echoes the test
    # output on top of that logger, printing every line twice, and it turns itself on only when the
    # output is a terminal: without this the same command behaves differently piped and interactively.
    command = ['dotnet', 'test', str(PROJECT), '-c', options.configuration, '-tl:off',
               '--logger', 'console;verbosity=normal', '--logger', f'trx;LogFileName={trx.name}',
               '--results-directory', str(results_dir)]
    command += ['--no-build'] if options.no_build else []
    command += options.dotnet_args
    command += ['--filter', selection] if selection else []
    status = call(command, cwd=None)

    # The TRX is the only record of the run that survives the scrollback, and what says which tests
    # failed rather than merely how many. A run that fell over before writing one has nothing to summarise.
    if not trx.is_file():
        print(f'\n=== No test results were written to {trx} ===', file=sys.stderr)
        return status
    summarise(options, trx)
    return status


if __name__ == '__main__':
    sys.exit(main())
