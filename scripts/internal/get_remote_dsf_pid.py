#!/usr/bin/env python3
# pyright: strict
"""Write the process ID of DuetControlServer on the debug target to .vscode/dcs.pid, from where the
"[DSF] Attach Native (lib*.so)" launch configuration reads it.

    DSF_SSH_USER=root DSF_TARGET_IP=192.168.4.38 scripts/internal/get_remote_dsf_pid.py

The tasks that run it set DSF_SSH_USER and DSF_TARGET_IP from the dsf.debug settings. scripts/deploy.py
writes the file too, whenever it starts the services.
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

# Run by VS Code as a file, so scripts/, where the shared modules are, is not on the path by itself
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from common.dsf import DeployOptions, write_dcs_pid  # noqa: E402


def main() -> int:
    argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter).parse_args()
    write_dcs_pid(DeployOptions(os.environ['DSF_TARGET_IP'], os.environ['DSF_SSH_USER'], start_services=False))
    return 0


if __name__ == '__main__':
    sys.exit(main())
