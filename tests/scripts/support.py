# pyright: strict
"""What the tests share: an empty build/ in place of the real one, and running a script's main()."""

from __future__ import annotations

import io
import sys
import tempfile
import unittest
from collections.abc import Callable
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest.mock import patch

import clean
import flash_bootloader
from common import firmware, firmware_deploy, repo


class ScriptTest(unittest.TestCase):
    """Each test gets an empty build/ of its own, in which built() makes the files a build leaves."""

    def setUp(self) -> None:
        self.root = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.build = self.root / 'build'
        self.build.mkdir()
        for module in (repo, firmware, clean, firmware_deploy, flash_bootloader):
            self.enterContext(patch.object(module, 'BUILD', self.build))

    def built(self, preset: str, *files: str, data: bytes = b'image') -> None:
        (self.build / preset).mkdir(parents=True, exist_ok=True)
        for name in files:
            (self.build / preset / name).write_bytes(data)


def run_main(main: Callable[[], int], *argv: str) -> tuple[int, str]:
    """Run a script's main() with this command line: its exit status and what it printed."""
    out = io.StringIO()
    with patch.object(sys, 'argv', ['script', *argv]), redirect_stdout(out), redirect_stderr(out):
        try:
            status = main()
        except SystemExit as e:
            if isinstance(e.code, str):
                print(e.code, file=out)
            status = e.code if isinstance(e.code, int) else 0 if e.code is None else 1
    return status, out.getvalue()
