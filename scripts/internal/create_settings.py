#!/usr/bin/env python3
# pyright: strict
"""Create or update .vscode/settings.json with the settings the remote debugging tasks read.

    scripts/internal/create_settings.py                             add any setting that is missing
    scripts/internal/create_settings.py --target-ip 192.168.4.27    and set the Pi to debug on

Run when the devcontainer is created, and by the "[DSF] Set Debug Target IP" task.

Settings already in the file are kept. The file may be in VS Code's own syntax, with comments and
trailing commas, but it is written back as plain JSON.
"""

from __future__ import annotations

import argparse
import json
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import cast

# Run as a file, so scripts/, where the shared modules are, is not on the path by itself
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from common.repo import ROOT  # noqa: E402

SETTINGS = ROOT / '.vscode' / 'settings.json'
DEFAULTS = {
    'dsf.debug.sshUser': 'root',
    'dsf.debug.targetIp': '',
    'dsf.debug.debuggerPath': '/root/vsdbg/vsdbg',
}


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    target_ip: str | None


def parse_options(argv: Sequence[str]) -> Options:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--target-ip', metavar='IP', help='set the target IP address for debugging')
    return Options(**vars(parser.parse_args(argv)))


def skip_string(text: str, i: int, out: list[str]) -> int:
    """Copy the JSON string starting at text[i] to out, and return the index after it."""
    end = i + 1
    while end < len(text) and text[end] != '"':
        end += 2 if text[end] == '\\' else 1
    out.append(text[i:end + 1])
    return end + 1


def strip_comments(text: str) -> str:
    """JSON with comments, as VS Code writes it, without the comments."""
    out: list[str] = []
    i = 0
    while i < len(text):
        if text[i] == '"':
            i = skip_string(text, i, out)
        elif text.startswith('//', i):
            # The newline is kept, so the line numbers in any error stay right
            end = text.find('\n', i)
            i = len(text) if end < 0 else end
        elif text.startswith('/*', i):
            end = text.find('*/', i + 2)
            i = len(text) if end < 0 else end + 2
        else:
            out.append(text[i])
            i += 1
    return ''.join(out)


def strip_trailing_commas(text: str) -> str:
    """JSON without the commas VS Code allows before a closing bracket."""
    out: list[str] = []
    i = 0
    while i < len(text):
        if text[i] == '"':
            i = skip_string(text, i, out)
            continue
        if text[i] == ',' and text[i + 1:].lstrip(' \t\r\n')[:1] in (']', '}'):
            i += 1
            continue
        out.append(text[i])
        i += 1
    return ''.join(out)


def load_settings() -> dict[str, object]:
    """The settings in the file, or none if it is missing, empty or cannot be read."""
    if not SETTINGS.is_file():
        return {}
    raw = SETTINGS.read_text(encoding='utf-8')
    for text in (raw, strip_trailing_commas(strip_comments(raw))):
        try:
            settings = json.loads(text)
        except ValueError:
            continue
        if isinstance(settings, dict):
            return cast(dict[str, object], settings)
    return {}


def main() -> int:
    options = parse_options(sys.argv[1:])
    settings = load_settings()
    for key, value in DEFAULTS.items():
        settings.setdefault(key, value)
    if options.target_ip:
        settings['dsf.debug.targetIp'] = options.target_ip
        print(f'Target IP set to: {options.target_ip}')

    SETTINGS.parent.mkdir(parents=True, exist_ok=True)
    SETTINGS.write_text(json.dumps(settings, indent=4) + '\n', encoding='utf-8')
    print(f'Settings saved: {SETTINGS}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
