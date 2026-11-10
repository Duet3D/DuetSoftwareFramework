#!/usr/bin/env python3
# pyright: strict
"""Install or update the latest vsdbg on the debug target, for the remote .NET launch configurations.

    scripts/internal/install_remote_vsdbg.py -H 192.168.4.38 --debugger-path /root/vsdbg/vsdbg

The task that runs it passes the dsf.debug settings.
"""

from __future__ import annotations

import argparse
import shlex
import subprocess
import sys
from pathlib import Path


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('-H', '--host', required=True, help='the Pi: its host name or IP address')
    p.add_argument('-u', '--user', default='root', help='SSH user on the Pi (default: root)')
    p.add_argument('--debugger-path', required=True, help='where the launch configurations run vsdbg on the Pi')
    args = p.parse_args()

    install_dir = shlex.quote(str(Path(args.debugger_path).parent))
    command = (f'mkdir -p {install_dir} && curl -fsSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l {install_dir} '
               f'&& cat {install_dir}/version.txt')
    return subprocess.run(['ssh', '-o', 'StrictHostKeyChecking=no', f'{args.user}@{args.host}', command]).returncode


if __name__ == '__main__':
    sys.exit(main())
