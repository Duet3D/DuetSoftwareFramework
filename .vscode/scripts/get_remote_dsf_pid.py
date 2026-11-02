#!/usr/bin/env python3
# pyright: strict
"""Write the process ID of DuetControlServer on the debug target to .vscode/dcs.pid, from where the
"[DSF] Attach Native (lib*.so)" launch configuration reads it.

    DSF_SSH_USER=root DSF_TARGET_IP=192.168.4.38 .vscode/scripts/get_remote_dsf_pid.py

The tasks that run it set DSF_SSH_USER and DSF_TARGET_IP from the dsf.debug settings.
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent


def main() -> int:
    argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter).parse_args()
    target = f'{os.environ["DSF_SSH_USER"]}@{os.environ["DSF_TARGET_IP"]}'
    out = subprocess.run(['ssh', '-T', target, 'pidof DuetControlServer'], capture_output=True, text=True).stdout
    pid = next(iter(out.split()), '')
    (ROOT / '.vscode' / 'dcs.pid').write_text(pid + '\n')
    print(f'DuetControlServer PID: {pid}')
    return 0 if pid else 1


if __name__ == '__main__':
    sys.exit(main())
