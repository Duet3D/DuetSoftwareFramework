# pyright: strict
"""remote_control.py, against a mocked session with the board."""

from __future__ import annotations

import io
import json
import os
import sys
import tempfile
from pathlib import Path
from unittest import TestCase
from unittest.mock import MagicMock, call, create_autospec, patch

import remote_control
from common.duet import Connector, DuetError

from .support import run_main

MODEL: dict[str, object] = {
    'boards[0]': {'name': 'Duet 3 MB6HC', 'firmwareName': 'RepRapFirmware', 'firmwareVersion': '3.7'},
    'network': {'name': 'printer'},
    'state': {'status': 'idle', 'upTime': 42},
    'sbc.dsf': {'version': '3.7.0'},
    'move.axes': [{'letter': 'X', 'machinePosition': 1.5}],
    'heat.heaters': [{'current': 20.0, 'active': 0.0}],
    'job': {'file': {'fileName': '0:/gcodes/part.gcode', 'size': 100}, 'filePosition': 50},
}


def model(key: str, flags: str | None = None) -> object:
    return MODEL.get(key)


class RemoteControlTest(TestCase):
    def setUp(self) -> None:
        self.duet: MagicMock = create_autospec(Connector, instance=True)
        self.duet.mode, self.duet.sbc, self.duet.base = 'standalone', False, 'http://duet3'
        self.duet.model.side_effect = model
        self.duet.code.return_value = 'ok'
        self.duet.fileinfo.return_value = {'size': 100}
        self.duet.filelist.return_value = []
        self.duet.download.return_value = b'G28\n'
        self.open_session = self.enterContext(patch.object(remote_control, 'open_session', return_value=self.duet))
        self.enterContext(patch.dict(os.environ, {'DUET_HOST': ''}))
        self.directory = Path(self.enterContext(tempfile.TemporaryDirectory()))

    def remote(self, *argv: str) -> tuple[int, str]:
        return run_main(remote_control.main, '-H', 'duet3', *argv)

    def test_connection_options(self) -> None:
        self.assertEqual(self.remote('info')[0], 0)
        self.open_session.assert_called_once_with('duet3', None, 'auto', 15.0, 60.0)
        self.duet.disconnect.assert_called_once()
        run_main(remote_control.main, '--host', 'h', '--password', 'pw', '--mode', 'sbc', '--timeout', '5',
                 '--code-timeout', '9', 'info')
        self.open_session.assert_called_with('h', 'pw', 'sbc', 5.0, 9.0)
        run_main(remote_control.main, '-H', 'h', '-p', 'pw', '-m', 'standalone', '-t', '1', 'info')
        self.open_session.assert_called_with('h', 'pw', 'standalone', 1.0, 60.0)

    def test_host_from_the_environment(self) -> None:
        with patch.dict(os.environ, {'DUET_HOST': 'envhost'}):
            run_main(remote_control.main, 'info')
        self.assertEqual(self.open_session.call_args.args[0], 'envhost')
        status, out = run_main(remote_control.main, 'info')
        self.assertEqual(status, 2)
        self.assertIn('no host given', out)

    def test_bad_command_lines(self) -> None:
        for argv in ([], ['-m', 'usb', 'info'], ['-t', 'soon', 'info'], ['nope'], ['code'], ['get'], ['mv', '/a']):
            with self.subTest(argv=argv):
                self.assertEqual(self.remote(*argv)[0], 2)
        self.open_session.assert_not_called()

    def test_commands_call_the_board(self) -> None:
        cases = [
            (['code', 'G28', 'M122'], 'code', call('G28\nM122', True)),
            (['code', '--no-wait', 'M112'], 'code', call('M112', False)),
            (['model', 'move.axes[0].max', '-f', 'd2'], 'model', call('move.axes[0].max', 'd2')),
            (['model', '--flags', 'v'], 'model', call('', 'v')),
            (['ls'], 'filelist', call('/gcodes')),
            (['ls', '/sys'], 'filelist', call('/sys')),
            (['fileinfo'], 'fileinfo', call('')),
            (['fileinfo', '/gcodes/part.gcode'], 'fileinfo', call('/gcodes/part.gcode')),
            (['rm', '/gcodes/old'], 'delete', call('/gcodes/old', False)),
            (['rm', '-r', '/gcodes/old'], 'delete', call('/gcodes/old', True)),
            (['rm', '--recursive', '/gcodes/old'], 'delete', call('/gcodes/old', True)),
            (['mv', '/a', '/b'], 'move', call('/a', '/b', False)),
            (['mv', '-f', '/a', '/b'], 'move', call('/a', '/b', True)),
            (['mv', '/a', '/b', '--force'], 'move', call('/a', '/b', True)),
            (['mkdir', '/gcodes/new'], 'mkdir', call('/gcodes/new')),
        ]
        for argv, method, expected in cases:
            with self.subTest(argv=argv):
                self.duet.reset_mock(return_value=False, side_effect=False)
                self.assertEqual(self.remote(*argv)[0], 0)
                self.assertEqual(getattr(self.duet, method).call_args, expected)

    def test_code_from_stdin(self) -> None:
        with patch.object(sys, 'stdin', io.StringIO('G28\nG1 X10\n')):
            self.assertEqual(self.remote('code', '-'), (0, 'ok\n'))
        self.duet.code.assert_called_once_with('G28\nG1 X10\n', True)

    def test_info_and_status(self) -> None:
        out = self.remote('info')[1]
        self.assertIn('board     Duet 3 MB6HC', out)
        self.assertNotIn('dsf', out)
        self.duet.sbc = True
        self.assertIn('dsf       3.7.0', self.remote('info')[1])
        self.assertEqual(json.loads(self.remote('--json', 'info')[1])['network'], {'name': 'printer'})
        out = self.remote('status')[1]
        self.assertIn('position  X1.500', out)
        self.assertIn('job       0:/gcodes/part.gcode  50/100 bytes', out)
        self.assertEqual(json.loads(self.remote('--json', 'status')[1])['axes'], MODEL['move.axes'])

    def test_ls_json(self) -> None:
        self.duet.filelist.return_value = [{'type': 'f', 'name': 'a.g', 'size': 3}, {'type': 'd', 'name': 'macros'}]
        out = self.remote('ls')[1]
        self.assertLess(out.index('macros'), out.index('a.g'))
        self.assertEqual(json.loads(self.remote('--json', 'ls')[1]), self.duet.filelist.return_value)

    def test_get(self) -> None:
        local = self.directory / 'local.g'
        self.remote('get', '/sys/config.g', str(local))
        self.duet.download.assert_called_with('/sys/config.g')
        self.assertEqual(local.read_bytes(), b'G28\n')

        buffer = io.BytesIO()
        stdout = io.TextIOWrapper(buffer)
        argv = ['remote_control.py', '-H', 'duet3', 'get', '/sys/config.g', '-']
        with patch.object(sys, 'argv', argv), patch.object(sys, 'stdout', stdout):
            self.assertEqual(remote_control.main(), 0)
        self.assertEqual(buffer.getvalue(), b'G28\n')

        cwd = os.getcwd()
        os.chdir(self.directory)
        self.addCleanup(os.chdir, cwd)
        self.remote('get', '/sys/config.g')
        self.assertEqual((self.directory / 'config.g').read_bytes(), b'G28\n')

    def test_put(self) -> None:
        local = self.directory / 'part.gcode'
        local.write_bytes(b'G1 X1\n')
        self.remote('put', str(local))
        self.assertEqual(self.duet.upload.call_args.args[:2], ('/gcodes/part.gcode', b'G1 X1\n'))
        self.remote('put', str(local), '/macros/part.g')
        self.assertEqual(self.duet.upload.call_args.args[0], '/macros/part.g')

    def test_errors(self) -> None:
        self.duet.mkdir.side_effect = DuetError('cannot create /x')
        self.assertEqual(self.remote('mkdir', '/x'), (1, 'Error: cannot create /x\n'))
        self.duet.disconnect.assert_called_once()
