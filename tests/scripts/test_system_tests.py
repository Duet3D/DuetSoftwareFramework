# pyright: strict
"""The filter test.py system composes from --filter, --code and the category skips, and the declarations it
resolves a result's name to."""

from __future__ import annotations

import io
import tempfile
from contextlib import redirect_stdout
from pathlib import Path
from unittest import TestCase

from common.system_tests import Declaration, Options, declarations, test_filter


def options(filter: str | None = None, code: list[str] | None = None, skip_known_gaps: bool | None = None,
            skip_long_running: bool | None = None, all: bool = False, tag_known_gaps: bool = False) -> Options:
    return Options(configuration='Debug', results_dir='test-results', filter=filter, code=code or [],
                   skip_known_gaps=skip_known_gaps, skip_long_running=skip_long_running, all=all, no_build=False,
                   slow=5.0, tag_known_gaps=tag_known_gaps, tag_long_running=False, dotnet_args=[])


class SystemTestFilterTest(TestCase):
    def test_filters(self) -> None:
        cases: list[tuple[Options, str | None]] = [
            # With nothing named, the known gaps are left out
            (options(), 'TestCategory!=KnownGap'),
            (options(all=True), None),
            (options(skip_long_running=True), 'TestCategory!=KnownGap&TestCategory!=LongRunning'),
            # A filter or a code says which tests the run is about, so the known gaps are in unless skipped
            (options(filter='FullyQualifiedName~JobControl'), 'FullyQualifiedName~JobControl'),
            (options(code=['M140']), 'TestCategory=M140'),
            (options(code=['M140', 'M141']), 'TestCategory=M140|TestCategory=M141'),
            (options(code=['M140'], skip_known_gaps=True), '(TestCategory=M140)&TestCategory!=KnownGap'),
            # Both narrow each other
            (options(filter='Name~Waits', code=['M190', 'M191']),
             '(Name~Waits)&(TestCategory=M190|TestCategory=M191)'),
            (options(filter='Name~Waits', code=['M190'], skip_long_running=True),
             '((Name~Waits)&(TestCategory=M190))&TestCategory!=LongRunning'),
        ]
        for given, expected in cases:
            with self.subTest(filter=given.filter, code=given.code), redirect_stdout(io.StringIO()):
                self.assertEqual(test_filter(given), expected)

    def test_tagging_known_gaps_runs_them_unless_a_code_or_filter_is_named(self) -> None:
        with redirect_stdout(io.StringIO()):
            self.assertIsNone(test_filter(options(tag_known_gaps=True)))
            self.assertEqual(test_filter(options(code=['M116'], tag_known_gaps=True)), 'TestCategory=M116')


class DeclarationsTest(TestCase):
    def test_a_named_case_resolves_to_the_test_declaring_it(self) -> None:
        source = '''namespace Scenarios;

public class M1Tests
{
    [Category("KnownGap")]
    [TestCase(1, TestName = "M1 named by its attribute")]
    [TestCase(2)]
    public async Task Attributed(int value) { }

    [Category("KnownGap")]
    [TestCaseSource(nameof(Cases))]
    public async Task FromNameof(int value) { }

    [TestCaseSource("Later")]
    public async Task FromString(int value) { }

    private static IEnumerable<TestCaseData> Cases()
    {
        yield return new TestCaseData(1).SetName("M1 named by SetName");
        yield return new TestCaseData(2)
            .SetName("M1 named on the next line");
        yield return new TestCaseData(3);
    }

    private static TestCaseData[] Later => [new TestCaseData(1).SetName("M1 from a property declared later")];
}
'''
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'Tests').mkdir()
            (root / 'Tests' / 'M1Tests.cs').write_text(source)
            found = declarations(root / 'Tests', root)
        attributed = Declaration('Tests/M1Tests.cs', 8, ('KnownGap',))
        from_nameof = Declaration('Tests/M1Tests.cs', 12, ('KnownGap',))
        from_string = Declaration('Tests/M1Tests.cs', 15, ())
        for name, expected in [('Attributed', attributed), ('M1 named by its attribute', attributed),
                               ('FromNameof', from_nameof), ('M1 named by SetName', from_nameof),
                               ('M1 named on the next line', from_nameof),
                               ('FromString', from_string), ('M1 from a property declared later', from_string)]:
            with self.subTest(name=name):
                self.assertEqual(found.get(f'Scenarios.M1Tests.{name}'), expected)
