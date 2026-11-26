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
                self.assertEqual((method, path), ('POST', '/steps'))
                self.assertEqual(json.loads(text or ''), body)

    def test_a_step_given_as_yaml_is_passed_on_as_it_is(self) -> None:
        _, _, sent = self.requests('step', '{code: G28, timeout: 30s}')
        self.assertEqual(sent, [('POST', '/steps', '{code: G28, timeout: 30s}')])

    def test_queries(self) -> None:
        cases: list[tuple[list[str], tuple[str, str]]] = [
            (['status'], ('GET', '/')),
            (['stop'], ('DELETE', '/session')),
            (['om', 'move.axes'], ('GET', '/om?key=move.axes')),
            (['capture'], ('GET', '/capture')),
            (['capture', '--since', '5', '--text'], ('GET', '/capture?since=5&format=text')),
            (['save'], ('POST', '/save')),
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
            'step': 'G91',
            'rrf': {'time': 2.88, 'events': [{'kind': 'code', 't': 2.78, 'code': 'G91'}, {'kind': 'reply', 't': 2.82, 'severity': 'success', 'text': ''}]},
            'dsf': {'time': 2.0, 'events': [{'kind': 'om', 't': 2.0, 'path': 'inputs[10].axesRelative', 'old': False, 'new': True}]},
            'differences': ['om: inputs[10].axesRelative: false vs true'],
        }]}
        _, out, _ = self.requests('send', 'G91', answer=answer)
        self.assertEqual(out.splitlines(), [
            '== G91',
            '-- rrf (t=2.880000)',
            '    2.780000 > G91',
            '    2.820000 < (empty)',
            '-- dsf (t=2.000000)',
            '    2.000000 OM inputs[10].axesRelative: false -> true',
            '-- differences: 1',
            '   om: inputs[10].axesRelative: false vs true',
        ])


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
        ]
        for event, text in cases:
            with self.subTest(kind=event['kind']):
                self.assertEqual(emulator.describe_event(event), f'{1:12.6f} {text}')
