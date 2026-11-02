#!/usr/bin/env python3
# pyright: strict
"""Write the disassembly of a firmware image, with labels for the branch targets within functions.

objdump shows the target of a branch as an address and an offset into the function, but nothing
marks the instruction it lands on. So each function's targets are numbered .L1, .L2... in address
order, a line with the label is put before the target instruction, and the branches name the label
in place of the offset. The addresses are kept, to match those in software reset data.

    cmake/disassemble.py <objdump> <image>.elf <image>.lst
    cmake/disassemble.py <objdump> <image>.elf <image>.lst --no-labels    objdump's output as it is

The build runs this after linking when it is configured with -DDUET_DISASSEMBLY=ON, without the
labels if also with -DDUET_DISASSEMBLY_LABELS=OFF.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from collections.abc import Iterable, Iterator
from pathlib import Path
from typing import TextIO

FUNCTION = re.compile(r'([0-9a-f]+) <(.+)>:$')
# An instruction line is "  <address>:\t<encoding>\t<mnemonic>\t<operands>"
INSTRUCTION = re.compile(r'\s*([0-9a-f]+):\t')
BRANCH = re.compile(r'(b(eq|ne|cs|hs|cc|lo|mi|pl|vs|vc|hi|ls|ge|lt|gt|le|al)?(\.[nw])?|cbn?z)$')


def functions(lines: Iterable[str]) -> Iterator[tuple[str | None, list[str]]]:
    """objdump's output split at each function: its name (None for what precedes the first) and lines."""
    name: str | None = None
    body: list[str] = []
    for line in lines:
        match = FUNCTION.match(line)
        if match:
            yield name, body
            name, body = match[2], []
        body.append(line)
    yield name, body


def label(name: str, body: list[str], out: TextIO) -> None:
    """Write a function's lines with labels for the branches to addresses within it."""
    local = re.compile(rf'([0-9a-f]+) <{re.escape(name)}\+0x[0-9a-f]+>$')
    targets: set[int] = set()
    for line in body:
        fields = line.split('\t')
        if INSTRUCTION.match(line) and len(fields) >= 4 and BRANCH.match(fields[2]):
            match = local.search(fields[3])
            if match:
                targets.add(int(match[1], 16))
    labels = {address: f'.L{n}' for n, address in enumerate(sorted(targets), 1)}
    for line in body:
        match = INSTRUCTION.match(line)
        if match:
            address = int(match[1], 16)
            if address in labels:
                out.write(f'{labels[address]}:\n')
            fields = line.split('\t')
            if len(fields) >= 4 and BRANCH.match(fields[2]):
                target = local.search(fields[3])
                if target:
                    fields[3] = fields[3][:target.start()] + f'{target[1]} <{labels[int(target[1], 16)]}>'
                    line = '\t'.join(fields) + '\n'
        out.write(line)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('objdump', help="the cross toolchain's objdump")
    parser.add_argument('elf', type=Path, help='the firmware image')
    parser.add_argument('output', type=Path, help='the disassembly to write')
    parser.add_argument('--no-labels', action='store_true', help='do not label the branch targets')
    args = parser.parse_args()

    command = [args.objdump, '--disassemble', '--source', '--demangle', str(args.elf)]
    with subprocess.Popen(command, stdout=subprocess.PIPE, text=True, errors='replace') as objdump, \
            args.output.open('w') as out:
        assert objdump.stdout is not None
        for name, body in functions(objdump.stdout):
            if name is None or args.no_labels:
                out.writelines(body)
            else:
                label(name, body, out)
    if objdump.returncode:
        args.output.unlink(missing_ok=True)
        print(f'{args.objdump} failed with exit status {objdump.returncode}', file=sys.stderr)
    return objdump.returncode


if __name__ == '__main__':
    sys.exit(main())
