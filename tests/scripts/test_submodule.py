# pyright: strict
"""submodule.py's command line, with the git work of each command (common/submodules.py) mocked."""

from __future__ import annotations

import io
from contextlib import redirect_stderr, redirect_stdout
from unittest import TestCase
from unittest.mock import patch

import submodule
from common import submodules
from common.submodules import Submodule


class SubmoduleTest(TestCase):
    def test_commands_and_their_arguments(self) -> None:
        cases: list[tuple[list[str], str, tuple[object, ...]]] = [
            (['check'], 'check', ('HEAD',)),
            (['check', 'v3.6.0'], 'check', ('v3.6.0',)),
            (['update'], 'update', ([],)),
            (['update', 'lib/CANlib', 'src/RepRapFirmware'], 'update', (['lib/CANlib', 'src/RepRapFirmware'],)),
            (['checkout', 'lib/CANlib'], 'checkout', (['lib/CANlib'],)),
            (['attach'], 'attach', ([], None, False)),
            (['attach', '-b', 'dev', 'lib/CANlib'], 'attach', (['lib/CANlib'], 'dev', False)),
            (['attach', '--branch', 'dev', '--pull'], 'attach', ([], 'dev', True)),
        ]
        for argv, command, args in cases:
            # check says whether every submodule passed, which main() turns into the exit status
            returned, status = ((False, 1) if command == 'check' else (3, 3))
            with self.subTest(argv=argv), patch.object(submodule, command, return_value=returned) as mocked:
                self.assertEqual(submodule.main(argv), status)
                mocked.assert_called_once_with(*args)
        with patch.object(submodule, 'check', return_value=True):
            self.assertEqual(submodule.main(['check']), 0)

    def test_bad_command_lines(self) -> None:
        for argv in ([], ['pull'], ['update', '--pull'], ['checkout', '-b', 'dev'], ['check', 'a', 'b']):
            with self.subTest(argv=argv), redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as raised:
                submodule.main(argv)
            self.assertEqual(raised.exception.code, 2)

    def test_paths_must_be_submodules(self) -> None:
        self.assertEqual([s.path for s in submodule.submodules(paths=['lib/CANlib/'])], ['lib/CANlib'])
        with self.assertRaisesRegex(SystemExit, 'Not submodules: not/a/submodule'):
            submodule.submodules(paths=['lib/CANlib', 'not/a/submodule'])

    def test_check_refuses_a_submodule_gitmodules_does_not_list(self) -> None:
        listed = [Submodule('lib/CANlib', 'https://example.com/CANlib.git', '3.7-dev')]
        recorded = {'lib/CANlib': 'a' * 40, 'lib/Stray': 'b' * 40, 'lib/Other': 'c' * 40}
        with (patch.object(submodules, 'submodules', return_value=listed),
              patch.object(submodules, 'committed', return_value=recorded),
              patch.object(submodules, 'check_one') as check_one,
              self.assertRaisesRegex(SystemExit, r'HEAD records submodules .gitmodules does not list: lib/Other, lib/Stray')):
            submodules.check('HEAD')
        check_one.assert_not_called()

    def test_check_passes_when_gitmodules_lists_every_submodule(self) -> None:
        listed = [Submodule('lib/CANlib', 'https://example.com/CANlib.git', '3.7-dev')]
        with (patch.object(submodules, 'submodules', return_value=listed),
              patch.object(submodules, 'committed', return_value={'lib/CANlib': 'a' * 40}),
              patch.object(submodules, 'check_one', return_value=(True, 'on 3.7-dev, up to date')),
              redirect_stdout(io.StringIO())):
            self.assertTrue(submodules.check('HEAD'))
