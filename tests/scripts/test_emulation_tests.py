# pyright: strict
"""The filter test.py emulation composes from --filter and --stack."""

from __future__ import annotations

from unittest import TestCase

from common.emulation_tests import Options, test_filter


def options(filter: str | None = None, stack: str | None = None) -> Options:
    return Options(configuration='Debug', results_dir='test-results', filter=filter, stack=stack, build=False, dotnet_args=[])


class EmulationTestFilterTest(TestCase):
    def test_filters(self) -> None:
        cases: list[tuple[Options, str | None]] = [
            (options(), None),
            (options(filter='FullyQualifiedName~Heater'), 'FullyQualifiedName~Heater'),
            # A stack leaves out the other stack's fixtures, keeping those that run on no stack
            (options(stack='rrf'), 'TestCategory!=Dsf'),
            (options(stack='dsf'), 'TestCategory!=Rrf'),
            (options(filter='Name~Moves', stack='dsf'), '(Name~Moves)&(TestCategory!=Rrf)'),
        ]
        for given, expected in cases:
            with self.subTest(filter=given.filter, stack=given.stack):
                self.assertEqual(test_filter(given), expected)
