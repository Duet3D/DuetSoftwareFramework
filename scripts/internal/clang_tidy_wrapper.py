#!/usr/bin/env python3
# pyright: strict
"""Run clang-tidy on one firmware translation unit, as part of the build.

CMake's CXX_CLANG_TIDY hands the linter the GCC command line it is about to run:

    clang_tidy_wrapper.py <wrapper and tidy args> -- arm-none-eabi-g++ <firmware flags> -c <source>

clang cannot consume that command line as it is (see common/clang.py), so this rewrites it the same
way scripts/lint.py rewrites the compile database, runs clang-tidy, and reports what it
found in the firmware's own code: diagnostics located in third-party code are dropped, and so are
headers of other projects, which are reported when that project is built instead. Every finding is an
error, and fails the build.

Some translation units are skipped (SKIP in common/clang.py); the decision lives here rather than in a
per-source CMake property because the firmware targets pick their sources up from globs, and
because each exclusion needs the explanation that goes with it.

Options consumed by the wrapper itself (anything else is passed through to clang-tidy):
    --clang-tidy-binary=<path>   linter to run (default: clang-tidy from PATH)
    --cross-compile=<prefix>     arm-none-eabi- prefix to query for include paths
"""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

# Run by CMake as a file, so scripts/, where the shared modules are, is not on the path by itself
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from common.clang import cross_compile, lintable, own_diagnostics, posix_escape, project_dir, rewrite, skipped  # noqa: E402

def main() -> int:
    argv = sys.argv[1:]
    if '--' not in argv:
        sys.exit("clang_tidy_wrapper: expected '--' before the compile command")
    split = argv.index('--')
    tidy_args, compile_args = argv[:split], argv[split + 1:]
    if not compile_args:
        sys.exit('clang_tidy_wrapper: empty compile command')

    tidy = os.environ.get('CLANG_TIDY', 'clang-tidy')
    prefix = cross_compile()
    passthrough: list[str] = []
    for arg in tidy_args:
        if arg.startswith('--clang-tidy-binary='):
            tidy = arg.split('=', 1)[1]
        elif arg.startswith('--cross-compile='):
            prefix = arg.split('=', 1)[1]
        else:
            passthrough.append(arg)

    # CMake always ends the compile command with the source file ("... -c Foo.cpp")
    source = os.path.abspath(compile_args[-1])
    if not lintable(source) or skipped(source):
        return 0

    # Have clang-tidy check only the headers of the project the TU belongs to (see own_diagnostics())
    if (project := project_dir(source)) is not None:
        passthrough.append(f'--header-filter=^{posix_escape(project)}/')
    command = [tidy, '--quiet', *passthrough, '--', *rewrite(compile_args, prefix)]
    result = subprocess.run(command, capture_output=True, text=True)
    kept = [line.replace(': warning: ', ': error: ', 1)
            for line in own_diagnostics((result.stdout + result.stderr).splitlines(), source)]
    if kept:
        print('\n'.join(kept), flush=True)
    if result.returncode not in (0, 1) or (result.returncode and not result.stdout and not kept):
        # clang-tidy failed without diagnosing anything: it crashed or could not run
        print(f'{source}: error: clang-tidy failed ({result.returncode}): {result.stderr.strip()}', flush=True)
        return 1
    return 1 if kept else 0


if __name__ == '__main__':
    sys.exit(main())
