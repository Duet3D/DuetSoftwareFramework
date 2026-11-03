# pyright: strict
"""Where things are in the repository, and running the commands the scripts are made of."""

from __future__ import annotations

import os
import subprocess
import sys
from collections.abc import Mapping, Sequence
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
BUILD = ROOT / 'build'
RESULTS = ROOT / 'test-results'

Command = Sequence[str | os.PathLike[str]]


def call(command: Command, cwd: Path | None = ROOT, env: Mapping[str, str] | None = None) -> int:
    """Run a command, showing it first, and return its exit status."""
    print('+', ' '.join(map(str, command)), flush=True)
    return subprocess.run(command, cwd=cwd, env=env).returncode


def run(command: Command, cwd: Path | None = ROOT, env: Mapping[str, str] | None = None) -> bool:
    return call(command, cwd, env) == 0


def run_checked(command: Command, cwd: Path | None = ROOT, env: Mapping[str, str] | None = None) -> None:
    """Run a command, and exit with its status if it fails."""
    if status := call(command, cwd, env):
        sys.exit(status)


def jobs() -> int:
    return os.cpu_count() or 1
