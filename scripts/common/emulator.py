# pyright: strict
"""The emulation bench's command line tool and its session server: scripts/emulate.py.

The tool (src/DuetEmulator) runs scenarios, compares captures and serves sessions; the session
commands here are a client of that server's API (under /api), which listens on localhost.
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, cast

from common.repo import ROOT

PROJECT = ROOT / 'src' / 'DuetEmulator' / 'DuetEmulator.csproj'
DEFAULT_PORT = 8770


class SessionError(Exception):
    """The session server refused a request, or is not running."""


def build_command(configuration: str = 'Debug') -> list[str]:
    """The command that builds the tool, quietly, so that its output is the tool's own."""
    return ['dotnet', 'build', str(PROJECT), '-c', configuration, '-v', 'q', '-nologo', '-clp:ErrorsOnly']


def tool_command(arguments: list[str], configuration: str = 'Debug') -> list[str]:
    """The command that runs the built tool with these arguments."""
    return ['dotnet', 'run', '--no-build', '--project', str(PROJECT), '-c', configuration, '--', *arguments]


def request(port: int, method: str, path: str, body: str | None = None, timeout: float = 3600) -> dict[str, Any]:
    """Send a request to the session server and return its JSON answer."""
    data = body.encode() if body is not None else None
    req = urllib.request.Request(f'http://127.0.0.1:{port}{path}', data=data, method=method,
                                 headers={'Content-Type': 'application/yaml'} if data is not None else {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            return cast(dict[str, Any], json.loads(response.read()))
    except urllib.error.HTTPError as e:
        try:
            message = cast(dict[str, Any], json.loads(e.read())).get('error', str(e))
        except ValueError:
            message = str(e)
        raise SessionError(str(message)) from e
    except urllib.error.URLError as e:
        raise SessionError(f'No session server on port {port}: start one with scripts/emulate.py serve ({e.reason})') from e


def describe_event(event: dict[str, Any]) -> str:
    """One recorded event as a line of text, as the tool's reports show it."""
    kind = event.get('kind')
    if kind == 'code':
        text = f'> {event["code"]}'
    elif kind == 'reply':
        severity = event.get('severity', 'success')
        reply = event.get('text') or '(empty)'
        text = f'< {"" if severity == "success" else severity + ": "}{reply}'
    elif kind == 'can':
        destination = 'all' if event.get('destination') == 63 else event.get('destination')
        message = json.dumps(event['message'], separators=(',', ':')) if event.get('message') is not None else event.get('data', '')
        response = ' (response)' if event.get('response') else ''
        text = f'CAN {event.get("source")}->{destination} {event.get("type")}{response} {message}'
    elif kind == 'om':
        text = f'OM {event["path"]}: {json.dumps(event.get("old"))} -> {json.dumps(event.get("new"))}'
    elif kind == 'pin':
        duty = f' {event["duty"]:.1%} at {event.get("frequency", 0):.1f}Hz' if event.get('duty') is not None else ''
        text = f'PIN {event["board"]}.{"/".join(event.get("ports", []))} {event["output"]}{duty}'
    elif kind == 'input':
        text = f'INPUT {event["board"]}.{event["port"]} {event["value"]}'
    elif kind == 'input-change':
        text = f'INPUT {event["board"]}.{"/".join(event.get("ports", []))} {event["value"]} (scripted)'
    elif kind == 'om-snapshot':
        text = 'OM snapshot'
    elif kind == 'note':
        text = f'# {event["text"]}'
    elif kind == 'await':
        text = f'await {event["code"]}'
    elif kind == 'wait':
        text = f'wait {event["duration"]}'
    elif kind == 'config':
        text = 'run config.g'
    else:
        text = json.dumps(event)
    return f'{float(event.get("t", 0)):12.6f} {text}'


def describe_steps(answer: dict[str, Any]) -> str:
    """The answer to running steps as text: each step's reply and events on each stack, then their differences."""
    lines: list[str] = []
    for step in cast(list[dict[str, Any]], answer.get('steps', [])):
        before = ' (before config.g)' if step.get('kind') == 'beforeConfig' else ''
        lines.append(f'== {step["number"]}: {step["step"]}{before}' + ('' if step.get('state') != 'failed' else ' (failed)'))
        for stack, result in cast(dict[str, dict[str, Any]], step.get('stacks', {})).items():
            finished = result.get('finished')
            lines.append(f'-- {stack} (t={float(finished if finished is not None else result.get("started", 0)):.6f})')
            lines += [describe_event(e) for e in cast(list[dict[str, Any]], result.get('events') or [])]
            if result.get('error'):
                lines.append(f'   failed: {result["error"]}')
        differences = cast(list[str], step.get('differences') or [])
        if len(step.get('stacks', {})) == 2:
            lines.append('-- differences: ' + ('none' if not differences else str(len(differences))))
            lines += [f'   {d}' for d in differences]
    return '\n'.join(lines)


def step_json(action: str, value: object) -> str:
    """One step as the server takes it, such as {"code": "G28"} or {"drive": {"board": 1, ...}}."""
    return json.dumps({action: value})


def scenario_text(path: str) -> str:
    """A scenario file's text, or standard input's for '-'."""
    if path == '-':
        import sys
        return sys.stdin.read()
    return Path(path).read_text()


def session_scenario(boards: list[str], stack: str | None, config: list[str], name: str | None) -> dict[str, object]:
    """A scenario with no steps, from boards given as 'ADDRESS=TYPE', config.g's lines and the stack to run on."""
    by_address: dict[str, str] = {}
    for board in boards:
        address, _, board_type = board.partition('=')
        if not address.strip().isdigit() or not board_type.strip():
            raise ValueError(f'"{board}" is not a board: give ADDRESS=TYPE, such as 1=EXP3HC')
        if address.strip() in by_address:
            raise ValueError(f'two boards at address {address.strip()}')
        by_address[str(int(address))] = board_type.strip()
    scenario: dict[str, object] = {'name': name or 'session', 'boards': by_address}
    if stack:
        scenario['stacks'] = [stack]
    if config:
        scenario['config'] = ''.join(line + '\n' for line in config)
    return scenario
