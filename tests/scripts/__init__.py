"""Tests of the scripts' command lines, needing no hardware, toolchain or build: run them from the
repository root with

    python3 -m unittest discover -s tests/scripts -t .
"""

import sys
from pathlib import Path

# The scripts import each other as top-level modules, as when run from scripts/
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
