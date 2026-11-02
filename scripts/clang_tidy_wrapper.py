#!/usr/bin/env python3
# pyright: strict
"""Run clang-tidy on one firmware translation unit, as part of the build.

CMake's CXX_CLANG_TIDY hands the linter the GCC command line it is about to run:

    clang_tidy_wrapper.py <wrapper and tidy args> -- arm-none-eabi-g++ <firmware flags> -c <source>

clang cannot consume that command line as it is (see common/clang.py), so this rewrites it the same
way gen_compile_commands.py rewrites the compile database, runs clang-tidy, and reports what it
found in the firmware's own code: diagnostics located in third-party code are dropped, and so are
headers of other projects, which are reported when that project is built instead. A finding is a
warning unless --errors is given, when it fails the build (cmake/ClangTidy.cmake decides which
projects that applies to).

Some translation units are skipped (SKIP below); the decision lives here rather than in a
per-source CMake property because the firmware targets pick their sources up from globs, and
because each exclusion needs the explanation that goes with it.

Options consumed by the wrapper itself (anything else is passed through to clang-tidy):
    --clang-tidy-binary=<path>   linter to run (default: clang-tidy from PATH)
    --cross-compile=<prefix>     arm-none-eabi- prefix to query for include paths
    --errors                     report findings as errors and fail the build on any
"""

from __future__ import annotations

import os
import re
import subprocess
import sys

from common.clang import ROOT, THIRD_PARTY, cross_compile, lintable, rewrite

# Translation units not linted, matched against the absolute path of the TU, besides third-party
# code (common/clang.py).
#
#   Hardware/ExceptionHandlers.cpp
#       clang cannot parse this TU, and the diagnostics it produces are hard errors rather than
#       warnings, so they cannot be silenced with a -Wno- flag or a NOLINT and would fail every
#       strict build:
#         * NMI_Handler and UsageFault_Handler are declared __attribute__((naked)) and defined with
#           a C++ body that calls SoftwareReset. GCC accepts it (SoftwareReset never returns);
#           clang rejects a non-asm statement in a naked function outright.
#         * vAssertCalled and std::__throw_bad_function_call are first declared with
#           __attribute__((noreturn)) elsewhere (FreeRTOSConfig.h, libstdc++'s functexcept.h) and
#           redeclared here with the standard [[noreturn]]; clang treats that as the attribute
#           appearing on a later declaration.
#       scripts/gen_compile_commands.py + clang-tidy still reports the file's other findings, which
#       is where these were triaged.
SKIP = (os.path.join('src', 'Hardware', 'ExceptionHandlers.cpp'),)

# The first line of a diagnostic; its notes and source excerpt follow up to the next one
DIAGNOSTIC = re.compile(r'^(?P<path>[^\s:][^:]*):\d+:\d+: (?P<severity>warning|error): .*\[[\w.,-]+\]$')
# clang-tidy's summary lines, which count what is dropped here too
SUMMARY = re.compile(r'^(\d+ (warnings?|errors?)( and \d+ errors?)? generated\.|Error while processing .*\.)$')


def posix_escape(text: str) -> str:
    """Text as a POSIX extended regular expression, which clang-tidy's are. re.escape will not do: it
    also escapes '-', which POSIX leaves undefined."""
    return re.sub(r'([.^$*+?()\[\]{}|\\])', r'\\\1', text)


def project_dir(source: str) -> str | None:
    """The directory of the project or library a source belongs to: src/<project> or lib/<library>."""
    try:
        parts = os.path.relpath(source, ROOT).split(os.sep)
    except ValueError:
        return None
    if len(parts) > 2 and parts[0] in ('src', 'lib'):
        return os.path.join(str(ROOT), parts[0], parts[1])
    return None


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
    errors = False
    passthrough: list[str] = []
    for arg in tidy_args:
        if arg.startswith('--clang-tidy-binary='):
            tidy = arg.split('=', 1)[1]
        elif arg.startswith('--cross-compile='):
            prefix = arg.split('=', 1)[1]
        elif arg == '--errors':
            errors = True
        else:
            passthrough.append(arg)

    # CMake always ends the compile command with the source file ("... -c Foo.cpp")
    source = os.path.abspath(compile_args[-1])
    if not lintable(source) or any(skip in source for skip in SKIP):
        return 0

    # Report the headers of the project the TU belongs to, so that each header's findings come from
    # the build of its own project rather than from every project that includes it. A project whose
    # findings are warnings reports only the header named like the TU, so that the build does not
    # repeat each finding in a header for every TU that includes it: a header with no .cpp of the
    # same name is reported by a project whose findings are errors (which has none), and by a run of
    # clang-tidy over the whole compile database (scripts/gen_compile_commands.py).
    # clang-tidy applies the filter to its checks but not to clang's own errors, so it is applied to
    # those here too; the pattern is in the subset POSIX and Python share.
    header_filter: re.Pattern[str] | None = None
    project = project_dir(source)
    if project is not None:
        stem = os.path.splitext(os.path.basename(source))[0]
        headers = '' if errors else rf'(.*/)?{posix_escape(stem)}\.h(pp)?$'
        pattern = f'^{posix_escape(project)}/{headers}'
        passthrough.append(f'--header-filter={pattern}')
        header_filter = re.compile(pattern)
    command = [tidy, '--quiet', *passthrough, '--', *rewrite(compile_args, prefix)]
    result = subprocess.run(command, capture_output=True, text=True)

    kept: list[str] = []
    keeping = False
    for line in (result.stdout + result.stderr).splitlines():
        if match := DIAGNOSTIC.match(line):
            path = os.path.abspath(match['path'])
            keeping = not THIRD_PARTY.search(path) and (
                path == source or header_filter is None or header_filter.search(path) is not None)
            # Every finding takes the project's severity. That includes a parse error: in a project
            # whose findings are warnings, a TU clang cannot fully parse must not look like a failed build
            if keeping and errors:
                line = line.replace(': warning: ', ': error: ', 1)
            elif keeping:
                line = line.replace(': error: ', ': warning: ', 1)
        elif SUMMARY.match(line):
            keeping = False
            continue
        if keeping:
            kept.append(line)
    if kept:
        print('\n'.join(kept), flush=True)
    if result.returncode not in (0, 1) or (result.returncode and not result.stdout and not kept):
        # clang-tidy failed without diagnosing anything: it crashed or could not run
        print(f'{source}: warning: clang-tidy failed ({result.returncode}): {result.stderr.strip()}', flush=True)
        return 1 if errors else 0
    return 1 if errors and kept else 0


if __name__ == '__main__':
    sys.exit(main())
