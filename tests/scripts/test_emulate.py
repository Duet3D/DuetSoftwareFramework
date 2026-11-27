# pyright: strict
"""scripts/emulate.py: the tool's commands it runs, and the session commands it sends to the server."""

from __future__ import annotations

import io
import json
from contextlib import redirect_stderr, redirect_stdout
from typing import Any
from unittest import TestCase
from unittest.mock import patch

import emulate
from common import emulator


def run(*argv: str) -> tuple[int, str]:
    out = io.StringIO()
    with redirect_stdout(out), redirect_stderr(out):
        status = emulate.main(list(argv))
    return status, out.getvalue()


class ToolCommandsTest(TestCase):
    def test_tool_commands_build_then_run_the_tool(self) -> None:
        cases: list[tuple[list[str], list[str]]] = [
            (['run', 'homing.yaml'], ['run', 'homing.yaml']),
            (['run', 'homing.yaml', '--stack', 'dsf', '-o', 'out', '--fail-on-difference', '--time-tolerance', '0.01'],
             ['run', 'homing.yaml', '--stack', 'dsf', '--out', 'out', '--fail-on-difference', '--time-tolerance', '0.01']),
            (['diff', 'a', 'b', '--ignore-path', 'fans', '--ignore-can-type', 'TimeSync', '--no-pins'],
             ['diff', 'a', 'b', '--ignore-path', 'fans', '--ignore-can-type', 'TimeSync', '--no-pins']),
            (['show', 'capture.jsonl'], ['show', 'capture.jsonl']),
            (['--port', '9000', 'serve'], ['serve', '--port', '9000']),
        ]
        for argv, tool_arguments in cases:
            with self.subTest(argv=argv), patch.object(emulate, 'call', return_value=0) as call:
                status, _ = run(*argv)
                self.assertEqual(status, 0)
                self.assertEqual(call.call_args_list[0].args[0], emulator.build_command())
                self.assertEqual(call.call_args_list[1].args[0], emulator.tool_command(tool_arguments))

    def test_a_failed_build_runs_nothing(self) -> None:
        with patch.object(emulate, 'call', return_value=1) as call:
            status, _ = run('show', 'capture.jsonl')
        self.assertEqual(status, 1)
        self.assertEqual(call.call_count, 1)


class SessionCommandsTest(TestCase):
    def requests(self, *argv: str, answer: dict[str, Any] | None = None) -> tuple[int, str, list[tuple[str, str, str | None]]]:
        sent: list[tuple[str, str, str | None]] = []

        def fake(port: int, method: str, path: str, body: str | None = None, timeout: float = 3600) -> dict[str, Any]:
            self.assertEqual(port, emulator.DEFAULT_PORT)
            sent.append((method, path, body))
            return answer if answer is not None else {'steps': []}

        with patch.object(emulator, 'request', side_effect=fake):
            status, out = run(*argv)
        return status, out, sent

    def test_steps_are_sent_as_json(self) -> None:
        cases: list[tuple[list[str], Any]] = [
            (['send', 'G91', 'G1 X10'], [{'code': 'G91'}, {'code': 'G1 X10'}]),
            (['send', 'G28', '--timeout', '30s'], [{'code': 'G28', 'timeout': '30s'}]),
            (['drive', '1', 'io0.in', 'high'], {'drive': {'board': 1, 'port': 'io0.in', 'level': 'high'}}),
            (['release', '1', 'io0.in'], {'release': {'board': 1, 'port': 'io0.in'}}),
            (['analog', '1', 'temp0', '0.25'], {'analog': {'board': 1, 'port': 'temp0', 'value': 0.25}}),
            (['thermistor', '1', 'temp0', '60'], {'thermistor': {'board': 1, 'port': 'temp0', 'celsius': 60.0}}),
            (['wait', '500ms'], {'wait': '500ms'}),
            (['note', 'probing'], {'note': 'probing'}),
        ]
        for argv, body in cases:
            with self.subTest(argv=argv):
                status, _, sent = self.requests(*argv)
                self.assertEqual(status, 0)
                self.assertEqual(len(sent), 1)
                method, path, text = sent[0]
                self.assertEqual((method, path), ('POST', '/api/steps'))
                self.assertEqual(json.loads(text or ''), body)

    def test_a_session_starts_from_the_boards_and_config_given(self) -> None:
        cases: list[tuple[list[str], dict[str, Any]]] = [
            (['start'], {'name': 'session', 'boards': {}}),
            (['start', '--board', '1=EXP3HC', '--board', '121=TOOL1RR', '--config', 'M584 X1.0', '--config', 'M92 X80'],
             {'name': 'session', 'boards': {'1': 'EXP3HC', '121': 'TOOL1RR'}, 'config': 'M584 X1.0\nM92 X80\n'}),
            (['start', '--stack', 'rrf', '--name', 'probing'], {'name': 'probing', 'boards': {}, 'stacks': ['rrf']}),
        ]
        for argv, scenario in cases:
            with self.subTest(argv=argv):
                status, _, sent = self.requests(*argv, answer={})
                self.assertEqual(status, 0)
                self.assertEqual([(m, p) for m, p, _ in sent], [('POST', '/api/session')])
                self.assertEqual(json.loads(sent[0][2] or ''), scenario)

    def test_a_session_starts_from_a_scenario_file(self) -> None:
        with patch.object(emulator, 'scenario_text', return_value='boards: {1: EXP3HC}') as read:
            _, _, sent = self.requests('start', 'session.yaml', answer={})
        read.assert_called_once_with('session.yaml')
        self.assertEqual(sent, [('POST', '/api/session', 'boards: {1: EXP3HC}')])

    def test_a_start_that_cannot_be_understood_sends_nothing(self) -> None:
        cases = [
            ['start', 'session.yaml', '--board', '1=EXP3HC'],
            ['start', '--board', 'EXP3HC'],
            ['start', '--board', 'x=EXP3HC'],
            ['start', '--board', '1=EXP3HC', '--board', '1=TOOL1RR'],
        ]
        for argv in cases:
            with self.subTest(argv=argv), redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                self.requests(*argv)

    def test_a_step_given_as_yaml_is_passed_on_as_it_is(self) -> None:
        _, _, sent = self.requests('step', '{code: G28, timeout: 30s}')
        self.assertEqual(sent, [('POST', '/api/steps', '{code: G28, timeout: 30s}')])

    def test_queries(self) -> None:
        cases: list[tuple[list[str], tuple[str, str]]] = [
            (['status'], ('GET', '/api/session')),
            (['stop'], ('DELETE', '/api/session')),
            (['restart'], ('POST', '/api/session/restart')),
            (['restart', '--replay'], ('POST', '/api/session/restart?replay=true')),
            (['om', 'move.axes'], ('GET', '/api/om?key=move.axes')),
            (['om', 'boards[1]'], ('GET', '/api/om?key=boards%5B1%5D')),
            (['capture'], ('GET', '/api/capture')),
            (['capture', '--since', '5', '--text'], ('GET', '/api/capture?since=5&format=text')),
            (['save'], ('POST', '/api/save')),
        ]
        for argv, request in cases:
            with self.subTest(argv=argv):
                _, _, sent = self.requests(*argv, answer={})
                self.assertEqual([(m, p) for m, p, _ in sent], [request])

    def test_a_refusal_is_printed_and_fails(self) -> None:
        with patch.object(emulator, 'request', side_effect=emulator.SessionError('No session: POST /session first')):
            status, out = run('send', 'M115')
        self.assertEqual(status, 1)
        self.assertIn('No session', out)

    def test_steps_print_as_text(self) -> None:
        answer: dict[str, Any] = {'steps': [{
            'number': 1, 'step': 'G91', 'state': 'done',
            'stacks': {
                'rrf': {'started': 2.78, 'finished': 2.88, 'events': [{'kind': 'code', 't': 2.78, 'code': 'G91'},
                                                                      {'kind': 'reply', 't': 2.82, 'severity': 'success', 'text': ''}]},
                'dsf': {'started': 1.9, 'finished': 2.0, 'events': [{'kind': 'om', 't': 2.0, 'path': 'inputs[10].axesRelative', 'old': False, 'new': True}]},
            },
            'differences': ['om: inputs[10].axesRelative: false vs true'],
        }, {
            'number': 2, 'step': 'G1 X10', 'state': 'failed',
            'stacks': {'rrf': {'started': 3, 'finished': 13, 'error': 'Timed out', 'events': []}},
            'differences': [],
        }]}
        _, out, _ = self.requests('send', 'G91', answer=answer)
        self.assertEqual(out.splitlines(), [
            '== 1: G91',
            '-- rrf (t=2.880000)',
            '    2.780000 > G91',
            '    2.820000 < (empty)',
            '-- dsf (t=2.000000)',
            '    2.000000 OM inputs[10].axesRelative: false -> true',
            '-- differences: 1',
            '   om: inputs[10].axesRelative: false vs true',
            '== 2: G1 X10 (failed)',
            '-- rrf (t=13.000000)',
            '   failed: Timed out',
        ])

    def test_export_keeps_every_step_but_those_skipped(self) -> None:
        steps = {'steps': [{'number': n} for n in (1, 2, 3, 4)]}
        sent: list[tuple[str, str, str | None]] = []

        def fake(port: int, method: str, path: str, body: str | None = None, timeout: float = 3600) -> dict[str, Any]:
            sent.append((method, path, body))
            return steps if path == '/api/steps' else {'yaml': 'name: homing\n', 'path': 'src/DuetEmulation/Scenarios/homing.yaml'}

        with patch.object(emulator, 'request', side_effect=fake):
            status, out = run('export', 'src/DuetEmulation/Scenarios/homing.yaml', '--skip', '2,4', '--to-config', '1', '--name', 'homing')
        self.assertEqual(status, 0)
        self.assertEqual([(m, p) for m, p, _ in sent], [('GET', '/api/steps'), ('POST', '/api/scenario')])
        self.assertEqual(json.loads(sent[1][2] or ''), {
            'steps': [1, 3], 'toConfig': [1], 'overwrite': False, 'name': 'homing', 'path': 'src/DuetEmulation/Scenarios/homing.yaml'})
        self.assertIn('Saved src/DuetEmulation/Scenarios/homing.yaml', out)

    def test_export_without_a_path_prints_the_scenario(self) -> None:
        def fake(port: int, method: str, path: str, body: str | None = None, timeout: float = 3600) -> dict[str, Any]:
            return {'steps': []} if path == '/api/steps' else {'yaml': 'name: homing\n'}

        with patch.object(emulator, 'request', side_effect=fake):
            status, out = run('export')
        self.assertEqual((status, out), (0, 'name: homing\n'))

    def test_export_refuses_a_list_that_is_not_of_numbers(self) -> None:
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            run('export', '--skip', 'two')


class DescribeEventTest(TestCase):
    def test_each_kind_of_event(self) -> None:
        cases: list[tuple[dict[str, Any], str]] = [
            ({'kind': 'reply', 't': 1, 'severity': 'warning', 'text': 'careful'}, '< warning: careful'),
            ({'kind': 'can', 't': 1, 'source': 0, 'destination': 63, 'type': 'TimeSync', 'response': False, 'data': 'ab'}, 'CAN 0->all TimeSync ab'),
            ({'kind': 'can', 't': 1, 'source': 1, 'destination': 0, 'type': 'StandardReply', 'response': True, 'message': {'Ok': 1}},
             'CAN 1->0 StandardReply (response) {"Ok":1}'),
            ({'kind': 'pin', 't': 1, 'board': 1, 'ports': ['out0'], 'output': 'Pwm', 'duty': 0.5, 'frequency': 250}, 'PIN 1.out0 Pwm 50.0% at 250.0Hz'),
            ({'kind': 'input', 't': 1, 'board': 1, 'port': 'io0.in', 'value': 'high'}, 'INPUT 1.io0.in high'),
            ({'kind': 'input-change', 't': 1, 'board': 1, 'ports': ['io0.in'], 'value': 'low'}, 'INPUT 1.io0.in low (scripted)'),
            ({'kind': 'note', 't': 1, 'text': 'probing'}, '# probing'),
            ({'kind': 'await', 't': 1, 'code': 'G28'}, 'await G28'),
            ({'kind': 'wait', 't': 1, 'duration': '500ms'}, 'wait 500ms'),
        ]
        for event, text in cases:
            with self.subTest(kind=event['kind']):
                self.assertEqual(emulator.describe_event(event), f'{1:12.6f} {text}')
