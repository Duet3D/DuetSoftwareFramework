#!/usr/bin/env python3
# pyright: strict
"""Run the emulation bench: scenarios, captures and interactive sessions, on RepRapFirmware and DSF.

Scenarios (YAML files, see src/DuetEmulation/README.md) run on each stack they name, and each run is
captured: every code and reply, CAN frame, object model change and pin change, in emulated time.

    scripts/emulate.py run src/DuetEmulation/Scenarios/Examples/endstop-homing.yaml
                                                                run on rrf and dsf, save and compare the captures
    scripts/emulate.py run homing.yaml --stack rrf              on one stack
    scripts/emulate.py diff build/emulation/scenarios/endstop-homing/rrf build/emulation/scenarios/endstop-homing/dsf
    scripts/emulate.py show build/emulation/scenarios/endstop-homing/dsf

A session boots the boards once and runs steps one at a time, on both stacks side by side:

    scripts/emulate.py serve &                                  start the session server; its page is http://127.0.0.1:8770/
    scripts/emulate.py start session.yaml                       boot a scenario's boards and config.g
    scripts/emulate.py start --board 1=EXP3HC --config 'M584 X1.0'
                                                                or name the boards and config.g lines here
    scripts/emulate.py start session.yaml --hold                stop before config.g, so that steps run before it
    scripts/emulate.py config                                   run config.g on a session stopped before it
    scripts/emulate.py send 'M584 X1.0' 'G1 X10 F600'           send codes; each answers on every stack
    scripts/emulate.py drive 1 io0.in high                      drive an input
    scripts/emulate.py thermistor 1 temp0 60                    make a thermistor read 60C
    scripts/emulate.py wait 500ms                               let emulated time pass
    scripts/emulate.py step '{code: G28, timeout: 30s}'         any step, as YAML
    scripts/emulate.py om move.axes                             the object model at a key
    scripts/emulate.py capture --text                           everything recorded so far
    scripts/emulate.py differences                              how the stacks differ so far
    scripts/emulate.py save                                     save the captures
    scripts/emulate.py export homing.yaml --skip 4              save the session as a scenario, without step 4
    scripts/emulate.py restart --replay                         boot the boards afresh and run the steps again
    scripts/emulate.py stop                                     end the session

The tool builds and runs src/DuetEmulator; the firmware images must have been built
(scripts/test.py emulation --build builds them).
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.parse
from collections.abc import Sequence
from typing import Any

from common import emulator
from common.cli import parse, parser
from common.repo import call


def tool(arguments: list[str]) -> int:
    return call(emulator.build_command()) or call(emulator.tool_command(arguments))


def comparison_arguments(p: argparse.ArgumentParser) -> None:
    p.add_argument('--time-tolerance', type=float, metavar='SECONDS',
                   help='compare when events happen within their step; ignored without it')
    p.add_argument('--ignore-path', action='append', default=[], metavar='PATH',
                   help='leave out an object model path ([*] matches any index); replaces the defaults')
    p.add_argument('--ignore-can-type', action='append', default=[], metavar='TYPE',
                   help='leave out a CAN message type; replaces the defaults')
    p.add_argument('--no-pins', action='store_true', help='do not compare pin changes')


def comparison_flags(args: argparse.Namespace) -> list[str]:
    flags: list[str] = []
    if args.time_tolerance is not None:
        flags += ['--time-tolerance', str(args.time_tolerance)]
    for path in args.ignore_path:
        flags += ['--ignore-path', path]
    for kind in args.ignore_can_type:
        flags += ['--ignore-can-type', kind]
    if args.no_pins:
        flags.append('--no-pins')
    return flags


def show(answer: dict[str, Any], as_json: bool) -> None:
    if as_json or 'steps' not in answer:
        print(json.dumps(answer, indent=2))
    else:
        print(emulator.describe_steps(answer))


def main(argv: Sequence[str] | None = None) -> int:
    p = parser(__doc__)
    p.add_argument('--port', type=int, default=emulator.DEFAULT_PORT, help='the session server\'s port (default: %(default)s)')
    p.add_argument('--json', action='store_true', help='print the session server\'s answers as JSON')
    commands = p.add_subparsers(dest='command', metavar='COMMAND', required=True)

    run = commands.add_parser('run', help='run a scenario, save and compare its captures')
    run.add_argument('scenario', help='the scenario file')
    run.add_argument('--stack', choices=['rrf', 'dsf'], help='only this stack')
    run.add_argument('-o', '--out', metavar='DIR', help='where the captures go (default: build/emulation/scenarios/<name>)')
    run.add_argument('--fail-on-difference', action='store_true', help='exit with 1 if the stacks differ')
    comparison_arguments(run)

    diff = commands.add_parser('diff', help='compare two captures')
    diff.add_argument('first')
    diff.add_argument('second')
    comparison_arguments(diff)

    show_capture = commands.add_parser('show', help='print a capture as text')
    show_capture.add_argument('capture')

    serve = commands.add_parser('serve', help='serve sessions on localhost')
    serve.add_argument('-o', '--out', metavar='DIR', help='where sessions\' files go (default: build/emulation/sessions)')

    start = commands.add_parser('start', help='start a session from a scenario file, or from the boards and config.g lines given here')
    start.add_argument('scenario', nargs='?', help='a scenario file (- for standard input); its steps run at once')
    start.add_argument('--board', action='append', default=[], metavar='ADDRESS=TYPE',
                       help='an expansion board, such as 1=EXP3HC or 121=TOOL1RR; may be repeated')
    start.add_argument('--stack', choices=('rrf', 'dsf'), help='only on this stack (default both)')
    start.add_argument('--config', action='append', default=[], metavar='LINE', help='a line of config.g; may be repeated')
    start.add_argument('--name', help='what the session is called, which names its directory (default session)')
    start.add_argument('--hold', action='store_true',
                       help='stop before config.g, so that the steps sent next run before it, until the config command')
    commands.add_parser('config', help='run config.g on a session stopped before it, then the scenario\'s steps after it')
    commands.add_parser('stop', help='end the session')
    restart = commands.add_parser('restart', help='boot the session\'s boards afresh, with its config.g and files')
    restart.add_argument('--replay', action='store_true', help='run the session\'s steps again once booted')
    commands.add_parser('status', help='the session\'s stacks, boards and times')

    send = commands.add_parser('send', help='send codes, each waited for')
    send.add_argument('codes', nargs='+')
    send.add_argument('--timeout', metavar='DURATION', help='how long each may take (default 10s)')

    step = commands.add_parser('step', help='run steps given as YAML: one step, or a list')
    step.add_argument('yaml')

    for name, help_text in (('drive', 'drive a digital input high or low'), ('release', 'stop driving a digital input'),
                            ('analog', 'make an analogue input read a fraction of its reference'),
                            ('thermistor', 'make a thermistor input read a temperature in C')):
        pin_parser = commands.add_parser(name, help=help_text)
        pin_parser.add_argument('board', type=int)
        pin_parser.add_argument('pin', metavar='PORT', help='the port name, such as io0.in')
        if name != 'release':
            pin_parser.add_argument('value')

    wait = commands.add_parser('wait', help='let emulated time pass')
    wait.add_argument('duration', help='such as 500ms or 2s')
    note = commands.add_parser('note', help='record a remark, which starts a step when captures are compared')
    note.add_argument('text')

    om = commands.add_parser('om', help='the object model at a key on each stack')
    om.add_argument('key', nargs='?', default='')
    capture = commands.add_parser('capture', help='the events recorded so far')
    capture.add_argument('--since', type=int, help='from this event on')
    capture.add_argument('--text', action='store_true', help='as text')
    commands.add_parser('differences', help='how the stacks differ so far')
    commands.add_parser('save', help='save the captures and their differences')
    export = commands.add_parser('export', help='the session as a scenario: printed, or saved in the repository')
    export.add_argument('path', nargs='?', help='where to save it, from the repository\'s root (default: print it)')
    export.add_argument('--name', help='the scenario\'s name (default: the session\'s)')
    export.add_argument('--description', help='what it does, written as the comment it starts with')
    export.add_argument('--skip', type=step_numbers, default=[], metavar='N,...', help='steps to leave out')
    export.add_argument('--to-config', type=step_numbers, default=[], metavar='N,...', help='code steps to move to config.g')
    export.add_argument('--overwrite', action='store_true', help='replace the file if it exists')

    args = parse(p, argv)
    command: str = args.command
    if command == 'run':
        flags = ['run', args.scenario] + (['--stack', args.stack] if args.stack else []) + (['--out', args.out] if args.out else [])
        flags += ['--fail-on-difference'] if args.fail_on_difference else []
        return tool(flags + comparison_flags(args))
    if command == 'diff':
        return tool(['diff', args.first, args.second] + comparison_flags(args))
    if command == 'show':
        return tool(['show', args.capture])
    if command == 'serve':
        return tool(['serve', '--port', str(args.port)] + (['--out', args.out] if args.out else []))

    port: int = args.port
    try:
        if command == 'start':
            show(emulator.request(port, 'POST', '/api/session' + ('?hold=true' if args.hold else ''), start_text(p, args)), args.json)
        elif command == 'config':
            show(emulator.request(port, 'POST', '/api/config', ''), args.json)
        elif command == 'stop':
            show(emulator.request(port, 'DELETE', '/api/session'), True)
        elif command == 'restart':
            show(emulator.request(port, 'POST', '/api/session/restart' + ('?replay=true' if args.replay else ''), ''), args.json)
        elif command == 'status':
            show(emulator.request(port, 'GET', '/api/session'), True)
        elif command == 'send':
            steps = [{'code': c, **({'timeout': args.timeout} if args.timeout else {})} for c in args.codes]
            show(emulator.request(port, 'POST', '/api/steps', json.dumps(steps)), args.json)
        elif command == 'step':
            show(emulator.request(port, 'POST', '/api/steps', args.yaml), args.json)
        elif command in ('drive', 'release', 'analog', 'thermistor'):
            pin: dict[str, object] = {'board': args.board, 'port': args.pin}
            if command == 'drive':
                pin['level'] = args.value
            elif command == 'analog':
                pin['value'] = float(args.value)
            elif command == 'thermistor':
                pin['celsius'] = float(args.value)
            show(emulator.request(port, 'POST', '/api/steps', emulator.step_json(command, pin)), args.json)
        elif command == 'wait':
            show(emulator.request(port, 'POST', '/api/steps', emulator.step_json('wait', args.duration)), args.json)
        elif command == 'note':
            show(emulator.request(port, 'POST', '/api/steps', emulator.step_json('note', args.text)), args.json)
        elif command == 'om':
            show(emulator.request(port, 'GET', f'/api/om?key={urllib.parse.quote(args.key)}'), True)
        elif command == 'capture':
            query = '&'.join(q for q in (f'since={args.since}' if args.since is not None else '', 'format=text' if args.text else '') if q)
            answer = emulator.request(port, 'GET', '/api/capture' + (f'?{query}' if query else ''))
            if args.text and not args.json:
                for stack, text in answer.items():
                    print(f'-- {stack}\n{text}')
            else:
                show(answer, True)
        elif command == 'differences':
            answer = emulator.request(port, 'GET', '/api/differences')
            print(answer['report'] if not args.json else json.dumps(answer, indent=2))
        elif command == 'save':
            show(emulator.request(port, 'POST', '/api/save'), True)
        elif command == 'export':
            steps = emulator.request(port, 'GET', '/api/steps')['steps']
            request: dict[str, object] = {
                'steps': [s['number'] for s in steps if s['number'] not in args.skip],
                'toConfig': args.to_config,
                'overwrite': args.overwrite,
            }
            request.update({k: v for k, v in (('name', args.name), ('description', args.description), ('path', args.path)) if v})
            answer = emulator.request(port, 'POST', '/api/scenario', json.dumps(request))
            if args.json:
                show(answer, True)
            elif 'path' in answer:
                print(f'Saved {answer["path"]}')
            else:
                print(answer['yaml'], end='')
    except emulator.SessionError as e:
        print(e, file=sys.stderr)
        return 1
    return 0


def step_numbers(text: str) -> list[int]:
    """Step numbers given as a list such as 2,5,7."""
    try:
        return [int(n) for n in text.split(',') if n.strip()]
    except ValueError:
        raise argparse.ArgumentTypeError(f'"{text}" is not a list of step numbers, such as 2,5') from None


def start_text(p: argparse.ArgumentParser, args: argparse.Namespace) -> str:
    """The scenario a session starts from: the file named, or one made of the options given."""
    options: list[str] = [o for o, given in (('--board', args.board), ('--stack', args.stack), ('--config', args.config), ('--name', args.name)) if given]
    if args.scenario is not None:
        if options:
            p.error(f'start: give a scenario file or {", ".join(options)}, not both')
        return emulator.scenario_text(args.scenario)
    try:
        return json.dumps(emulator.session_scenario(args.board, args.stack, args.config, args.name))
    except ValueError as e:
        p.error(f'start: {e}')


if __name__ == '__main__':
    sys.exit(main())
