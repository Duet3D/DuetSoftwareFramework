#!/usr/bin/env python3
# pyright: strict
"""Drive a Duet board over its HTTP API, served by the board when it runs standalone or by DSF when it
runs with an SBC. Which of the two it is gets detected unless --mode names one.

    scripts/remote_control.py -H duet3 info                     board, firmware and connection summary
    scripts/remote_control.py -H duet3 status                   state, positions, heaters and job
    scripts/remote_control.py -H duet3 code M122                run a code and print its reply
    scripts/remote_control.py -H duet3 code - < test.g          run the codes read from stdin
    scripts/remote_control.py -H duet3 model move.axes[0].max   query the object model
    scripts/remote_control.py -H duet3 ls /sys                  list a directory
    scripts/remote_control.py -H duet3 get /sys/config.g -      print a file
    scripts/remote_control.py -H duet3 put test.gcode           upload a file to /gcodes

$DUET_HOST replaces -H. In SBC mode `code` prints the reply to the code that was sent; in standalone
mode rr_reply is not correlated with it, so it prints the first reply to arrive.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from collections.abc import Callable
from pathlib import Path

from common.cli import complete, complete_with, complete_with_prefix
from common.duet import Connector, DuetError, as_dict, as_list, open_session

COMPLETION_TIMEOUT = 3  # seconds for a request made to complete an argument


def dump(value: object) -> None:
    print(json.dumps(value, indent=2, sort_keys=True))


def format_size(size: object) -> str:
    return f'{size:9d}' if isinstance(size, int) else '        -'


# Commands


def cmd_info(duet: Connector, args: argparse.Namespace) -> None:
    board = as_dict(duet.model('boards[0]'))
    network = as_dict(duet.model('network'))
    state = as_dict(duet.model('state'))
    if args.json:
        dump({'mode': duet.mode, 'board': board, 'network': network, 'state': state})
        return
    print(f'host      {duet.base}')
    print(f'mode      {duet.mode}')
    print(f'board     {board.get("name", "?")}')
    print(f'firmware  {board.get("firmwareName", "?")} {board.get("firmwareVersion", "?")} ({board.get("firmwareDate", "?")})')
    if duet.sbc:
        print(f'dsf       {as_dict(duet.model("sbc.dsf")).get("version", "?")}')
    print(f'name      {network.get("name", "?")}')
    print(f'state     {state.get("status", "?")} (up {state.get("upTime", "?")} s)')


def cmd_status(duet: Connector, args: argparse.Namespace) -> None:
    state = as_dict(duet.model('state'))
    axes = [as_dict(axis) for axis in as_list(duet.model('move.axes'))]
    heaters = [as_dict(heater) for heater in as_list(duet.model('heat.heaters'))]
    job = as_dict(duet.model('job'))
    if args.json:
        dump({'state': state, 'axes': axes, 'heaters': heaters, 'job': job})
        return
    print(f'state     {state.get("status", "?")}')
    print('position  ' + '  '.join(f'{axis.get("letter", "?")}{axis.get("machinePosition") or 0.0:.3f}' for axis in axes))
    print('heaters   ' + '  '.join(f'{heater.get("current") or 0.0:.1f}/{heater.get("active") or 0.0:.1f}' for heater in heaters))
    job_file = as_dict(job.get('file'))
    if job_file.get('fileName'):
        print(f'job       {job_file["fileName"]}  {job.get("filePosition", 0)}/{job_file.get("size", 0)} bytes')


def cmd_code(duet: Connector, args: argparse.Namespace) -> None:
    reply = duet.code(sys.stdin.read() if args.gcode == ['-'] else '\n'.join(args.gcode), not args.no_wait)
    if reply:
        sys.stdout.write(reply if reply.endswith('\n') else reply + '\n')


def cmd_model(duet: Connector, args: argparse.Namespace) -> None:
    dump(duet.model(args.key, args.flags))


def cmd_ls(duet: Connector, args: argparse.Namespace) -> None:
    items = duet.filelist(args.directory)
    if args.json:
        dump(items)
        return
    for item in sorted(items, key=lambda item: (item.get('type') != 'd', str(item.get('name', '')))):
        print(f'{item.get("type", "?")} {format_size(item.get("size"))} {str(item.get("date", "")):19}  {item.get("name", "?")}')


def cmd_fileinfo(duet: Connector, args: argparse.Namespace) -> None:
    dump(duet.fileinfo(args.file))


def cmd_get(duet: Connector, args: argparse.Namespace) -> None:
    data = duet.download(args.remote)
    if args.local == '-':
        sys.stdout.buffer.write(data)
        return
    target = Path(args.local or Path(args.remote).name)
    target.write_bytes(data)
    print(f'{args.remote} -> {target} ({len(data)} bytes)', file=sys.stderr)


def cmd_put(duet: Connector, args: argparse.Namespace) -> None:
    local = Path(args.local)
    data = local.read_bytes()
    remote = args.remote or f'/gcodes/{local.name}'
    duet.upload(remote, data, time.strftime('%Y-%m-%dT%H:%M:%S', time.localtime(local.stat().st_mtime)))
    print(f'{local} -> {remote} ({len(data)} bytes)', file=sys.stderr)


def cmd_rm(duet: Connector, args: argparse.Namespace) -> None:
    duet.delete(args.path, args.recursive)


def cmd_mv(duet: Connector, args: argparse.Namespace) -> None:
    duet.move(args.old, args.new, args.force)


def cmd_mkdir(duet: Connector, args: argparse.Namespace) -> None:
    duet.mkdir(args.directory)


# Completion


def query(args: argparse.Namespace, ask: Callable[[Connector], object]) -> object:
    """Ask the board named on the command line so far, to complete an argument; None if that fails."""
    if not args.host:
        return None
    try:
        duet = open_session(args.host, args.password, args.mode, COMPLETION_TIMEOUT, COMPLETION_TIMEOUT)
    except DuetError:
        return None
    try:
        return ask(duet)
    except DuetError:
        return None
    finally:
        duet.disconnect()


def remote_paths(prefix: str, args: argparse.Namespace) -> list[str]:
    """The files and directories on the board that complete an absolute path."""
    directory = prefix.rpartition('/')[0]
    items = [as_dict(item) for item in as_list(query(args, lambda duet: duet.filelist(directory or '/')))]
    return [f'{directory}/{item.get("name")}' + ('/' if item.get('type') == 'd' else '') for item in items]


def model_keys(prefix: str, args: argparse.Namespace) -> list[str]:
    """The object model keys that complete a key like move.axes[0].max."""
    cut = max(prefix.rfind('.'), prefix.rfind('['))
    parent = prefix[:cut] if cut >= 0 else ''
    value = query(args, lambda duet: duet.model(parent, 'd2'))
    if prefix[cut:cut + 1] == '[':
        return [f'{parent}[{index}]' for index in range(len(as_list(value)))]
    return [f'{parent}.{key}' if parent else key for key in as_dict(value)]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('-H', '--host', default=os.environ.get('DUET_HOST'), help='hostname or URL of the board, defaults to $DUET_HOST')
    parser.add_argument('-p', '--password', help='machine password')
    parser.add_argument('-m', '--mode', choices=['auto', 'standalone', 'sbc'], default='auto', help='protocol to use, detected by default')
    parser.add_argument('-t', '--timeout', type=float, default=15.0, help='request timeout in seconds')
    parser.add_argument('--code-timeout', type=float, default=60.0, help='how long to wait for a G-code reply')
    parser.add_argument('--json', action='store_true', help='print raw JSON instead of a summary')
    commands = parser.add_subparsers(dest='command', required=True)

    command = commands.add_parser('info', help='board, firmware and connection summary')
    command.set_defaults(run=cmd_info)

    command = commands.add_parser('status', help='state, positions, heaters and job')
    command.set_defaults(run=cmd_status)

    command = commands.add_parser('code', help='execute G/M/T-codes and print the reply', description='Execute G/M/T-codes. In SBC mode the returned reply belongs to the code that was sent; in standalone mode rr_reply is not correlated with it, so the first reply to arrive is printed.')
    complete_with(command.add_argument('gcode', nargs='+', help='codes to run, one per argument, or - to read stdin'), list)
    command.add_argument('--no-wait', action='store_true', help='do not wait for the reply')
    command.set_defaults(run=cmd_code)

    command = commands.add_parser('model', help='query the object model')
    complete_with_prefix(command.add_argument('key', nargs='?', default='', help='object model key, e.g. move.axes[0].max'), model_keys)
    command.add_argument('-f', '--flags', help='query flags, standalone mode only')
    command.set_defaults(run=cmd_model)

    command = commands.add_parser('ls', help='list a directory')
    complete_with_prefix(command.add_argument('directory', nargs='?', default='/gcodes'), remote_paths)
    command.set_defaults(run=cmd_ls)

    command = commands.add_parser('fileinfo', help='parsed information about a G-code file')
    complete_with_prefix(command.add_argument('file', nargs='?', default='', help='defaults to the file being printed'), remote_paths)
    command.set_defaults(run=cmd_fileinfo)

    command = commands.add_parser('get', help='download a file')
    complete_with_prefix(command.add_argument('remote'), remote_paths)
    command.add_argument('local', nargs='?', help='local path, - for stdout, defaults to the basename')
    command.set_defaults(run=cmd_get)

    command = commands.add_parser('put', help='upload a file')
    command.add_argument('local')
    complete_with_prefix(command.add_argument('remote', nargs='?', help='defaults to /gcodes/<basename>'), remote_paths)
    command.set_defaults(run=cmd_put)

    command = commands.add_parser('rm', help='delete a file or directory')
    complete_with_prefix(command.add_argument('path'), remote_paths)
    command.add_argument('-r', '--recursive', action='store_true')
    command.set_defaults(run=cmd_rm)

    command = commands.add_parser('mv', help='move or rename a file')
    complete_with_prefix(command.add_argument('old'), remote_paths)
    complete_with_prefix(command.add_argument('new'), remote_paths)
    command.add_argument('-f', '--force', action='store_true', help='overwrite the destination')
    command.set_defaults(run=cmd_mv)

    command = commands.add_parser('mkdir', help='create a directory')
    complete_with_prefix(command.add_argument('directory'), remote_paths)
    command.set_defaults(run=cmd_mkdir)

    complete(parser)
    args = parser.parse_args()
    if not args.host:
        parser.error('no host given, pass -H or set DUET_HOST')
    return args


def main() -> int:
    args = parse_args()
    duet = None
    try:
        duet = open_session(args.host, args.password, args.mode, args.timeout, args.code_timeout)
        args.run(duet, args)
    except DuetError as e:
        print(f'Error: {e}', file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 130
    finally:
        if duet is not None:
            duet.disconnect()
    return 0


if __name__ == '__main__':
    sys.exit(main())
