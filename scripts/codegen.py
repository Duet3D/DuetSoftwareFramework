#!/usr/bin/env python3
# pyright: strict
"""Generate the CAN message formats from their schema, or check them.

    scripts/codegen.py                            regenerate the C++ and C# formats (the output is checked in)
    scripts/codegen.py --check                    fail if the checked-in output is stale, as CI does
    scripts/codegen.py --verify                   check the generated C++ layouts against CANlib's header

src/DuetCanMessage.SourceGenerators/Schema/can-messages.json is the single source of the CAN wire
formats: it generates both the C++ headers for CANlib and the C# types in
src/DuetControlServer/Link/Protocol/CanMessages/Generated. Edit the schema, never the generated files.

--verify compiles the generated layout probe against both CANlib's hand-written CanMessageFormats.h and
the generated header and runs it, proving that every struct size, member offset and bitfield position
is identical. It needs the CANlib, RRFLibraries and CoreN2G submodules and a host C++ compiler.
"""

from __future__ import annotations

import sys
from collections.abc import Sequence
from dataclasses import dataclass

from common.cli import parse, parser, typed
from common.repo import ROOT, call

GENERATOR = ROOT / 'src' / 'DuetCanMessage.SourceGenerators'


@dataclass(frozen=True)
class Options:
    """The command line, typed."""
    check: bool
    verify: bool


def parse_options(argv: Sequence[str] | None = None) -> Options:
    p = parser(__doc__)
    what = p.add_mutually_exclusive_group()
    what.add_argument('--check', action='store_true', help='fail if the generated files are stale')
    what.add_argument('--verify', action='store_true', help="check the generated C++ layouts against CANlib's")
    return typed(Options, parse(p, argv))


def main() -> int:
    options = parse_options()
    if options.verify:
        return call([GENERATOR / 'verify-cpp-layout.sh'])
    return call(['dotnet', 'run', '--project', GENERATOR, *(['--', '--check'] if options.check else [])])


if __name__ == '__main__':
    sys.exit(main())
