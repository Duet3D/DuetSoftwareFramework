#!/usr/bin/env python3
# pyright: strict
"""Pass clang-tidy or run-clang-tidy output through, then tally how many of each check fired.

    run-clang-tidy -p build -j "$(nproc)" -quiet '<file regex>' 2>&1 | scripts/tidy_report.py

The diagnostics pass through unchanged, less clang's per-TU "N warnings generated." and "Suppressed N
warnings" chatter, which is meaningless once several TUs are interleaved.

Diagnostic lines look like

    /path/File.cpp:12:34: warning: some message [modernize-use-auto]

and a single line can name more than one check, comma separated. Such a line counts once towards the
total and once towards each check it names, so the per-check column can add up to more than the total.
A warning raised in a shared header is reported once per translation unit that includes it, so counts
are occurrences, not distinct source locations.
"""

from __future__ import annotations

import argparse
import re
import sys
from collections import Counter

CHATTER = re.compile(r'^[0-9]+ warnings?( and [0-9]+ errors?)? generated\.$'
                     r'|^Suppressed [0-9]+ warnings'
                     r'|^[0-9]+ warnings? treated as errors?$')
DIAGNOSTIC = re.compile(r':[0-9]+:[0-9]+: (warning|error): ')
CHECKS = re.compile(r'\[([^\[\]]+)\]$')
UNNAMED = '(no check name)'


def plural(n: int, word: str) -> str:
    return word if n == 1 else word + 's'


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.parse_args()

    checks: Counter[str] = Counter()
    warnings = errors = 0
    for raw in sys.stdin:
        line = raw.rstrip('\n')
        if CHATTER.search(line):
            continue
        print(line, flush=True)
        if not (diagnostic := DIAGNOSTIC.search(line)):
            continue
        if diagnostic[1] == 'error':
            errors += 1
        else:
            warnings += 1
        if names := CHECKS.search(line):
            for name in (n.strip() for n in names[1].split(',')):
                # Every clang-tidy check is "<group>-<name>"; anything without a dash is a stray
                # bracketed word at the end of a message, not a check
                checks[name if '-' in name else UNNAMED] += 1
        else:
            checks[UNNAMED] += 1

    print('\n== clang-tidy summary ==')
    total = warnings + errors
    if total == 0:
        print('  no diagnostics')
        return 0
    for name, count in sorted(checks.items(), key=lambda c: (-c[1], c[0])):
        print(f'  {count:6}  {name}')
    print(f'  {"------":>6}  ---')
    print(f'  {total:6}  diagnostics total ({warnings} {plural(warnings, "warning")}, '
          f'{errors} {plural(errors, "error")})')
    return 0


if __name__ == '__main__':
    sys.exit(main())
