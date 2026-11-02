# pyright: strict
"""Turning the GCC command lines the firmware is built with into ones clang-tidy can parse, and
telling the firmware's own code from the third-party code it builds.

Two independent problems have to be solved or clang-tidy analyses a broken translation unit:

  1. clang errors out on GCC-only flags (-mfp16-format=ieee, -fsingle-precision-constant,
     -Wnoexcept, ...). One unknown argument aborts the whole TU.
  2. clang does not know where the bare-metal cross toolchain keeps its headers, so <cstddef>
     and friends are "file not found" and every subsequent declaration is unparsed.

Both are silent-ish: clang-tidy still emits diagnostics, so it is easy to believe a run succeeded
when in fact most of the code was never analysed. Any --fix derived from that state can corrupt
source, so the build's linting (clang_tidy_wrapper.py) and the standalone compile database
(scripts/lint.py) both go through here."""

from __future__ import annotations

import functools
import json
import os
import re
import shlex
import subprocess
import sys
from collections.abc import Iterable, Sequence
from pathlib import Path
from typing import cast

from common.repo import ROOT


# GCC flags clang does not accept. An unknown argument is a hard error, not a warning.
DROP_EXACT = {
    '-fsingle-precision-constant',
    '-mfp16-format=ieee',
    '-Wnoexcept',
    '-fno-strict-volatile-bitfields',
    '-mno-unaligned-access',
    '-fstack-usage',
    '-fcallgraph-info',
    '-Wa,-adhlns',
    # The firmware builds with -Werror under GCC. Keeping it here would promote clang's own
    # stricter-than-GCC diagnostics to errors on code GCC compiles cleanly, burying the actual
    # clang-tidy findings under thousands of entries that no source change can resolve.
    '-Werror',
}
DROP_PREFIX = (
    '-mfp16-format',
    '-fstack-usage',
    '-fcallgraph-info',
    '-fmacro-prefix-map',
    '-fdump-rtl',
    '-Wa,',
    '--specs=',
    '-flto',
)

# The options that select the multilib, and so the C++ library headers, the toolchain uses
MULTILIB = re.compile(r'^-m(cpu|fpu|float-abi|thumb)\b')

# Match the cross toolchain's fixed-width integer typedefs. GCC's arm-none-eabi defines the 32-bit
# types as `long`, clang's as `int`. That is not cosmetic: CoreIO.h overloads on both uint32_t and
# unsigned int (and int32_t and int), which are distinct types for GCC but the same type for clang,
# so clang rejects them as redeclarations and abandons the rest of the header. Newlib's stdint.h
# builds its typedefs from these macros, so overriding them makes clang model the real ABI.
TARGET_TYPEDEFS = [
    '-D__UINT32_TYPE__=long unsigned int',
    '-D__INT32_TYPE__=long int',
]

# Diagnostics where clang and the GCC cross compiler genuinely disagree on code GCC accepts.
# These are analyser artefacts, not defects, so they are silenced rather than "fixed":
#   missing-braces     - clang wants {{...}} for the nested aggregates in the PinTable; GCC does not
#   double-promotion   - clang flags float->double in contexts GCC's -Wdouble-promotion does not
#   unknown-attributes - the _ecv_* Escher/eCv annotations and some GCC attributes
#   uninitialized      - `register ... asm("sp")` reads the stack pointer; clang cannot model it
#   mismatched-tags    - struct/class forward-declaration mismatches GCC tolerates
SUPPRESS = [
    '-Wno-missing-braces',
    '-Wno-double-promotion',
    '-Wno-unknown-attributes',
    '-Wno-uninitialized',
    '-Wno-mismatched-tags',
    '-Wno-unknown-warning-option',
    '-Wno-ignored-optimization-argument',
]

# Third-party code that the firmware builds but this repository does not maintain. Its sources are
# not linted and diagnostics located in it are dropped. That is also the only way past the Microchip
# SAME5x/SAMC21 device packs, whose reserved register fields are unnamed volatile bit-fields: GCC
# accepts them in C++, clang rejects every one as a hard error, even in a system header. The pattern
# is the root .clang-tidy's, which excludes the same headers from a hand run of clang-tidy.


def _third_party() -> re.Pattern[str]:
    match = re.search(r"^ExcludeHeaderFilterRegex: '(.*)'$", (ROOT / '.clang-tidy').read_text(), re.MULTILINE)
    if match is None:
        sys.exit('error: the root .clang-tidy has no ExcludeHeaderFilterRegex')
    return re.compile(match[1])


THIRD_PARTY = _third_party()


def cross_compile() -> str:
    """The cross toolchain prefix: CROSS_COMPILE if set, else the toolchain on PATH."""
    return os.environ.get('CROSS_COMPILE', 'arm-none-eabi-')


@functools.cache
def toolchain_includes(cxx: str, multilib: tuple[str, ...]) -> list[str]:
    """The cross g++'s own system include paths for a multilib, whose C++ library configuration
    headers differ between cores and float ABIs."""
    try:
        proc = subprocess.run([cxx, *multilib, '-E', '-x', 'c++', '-', '-v'],
                              input='', capture_output=True, text=True, check=True)
    except (OSError, subprocess.CalledProcessError) as exc:
        sys.exit(f'error: could not query {cxx} for include paths: {exc}')
    paths: list[str] = []
    collecting = False
    for line in proc.stderr.splitlines():
        if line.startswith('#include <...> search starts here'):
            collecting = True
        elif line.startswith('End of search list'):
            break
        elif collecting and os.path.isdir(candidate := os.path.normpath(line.strip())):
            paths.append(candidate)
    if not paths:
        sys.exit(f'error: {cxx} reported no usable include directories')
    return paths


def keep(arg: str) -> bool:
    return arg not in DROP_EXACT and not arg.startswith(DROP_PREFIX)


def rewrite(command: Sequence[str], prefix: str) -> list[str]:
    """A GCC command line for clang: the compiler, what clang needs to model the cross toolchain,
    then the GCC arguments clang takes. The additions go after the compiler so that the trailing
    "-o Foo.o -c Foo.cpp" stays intact."""
    multilib = tuple(a for a in command[1:] if MULTILIB.match(a))
    options = ['--target=arm-none-eabi', *TARGET_TYPEDEFS, *SUPPRESS]
    for include in toolchain_includes(prefix + 'g++', multilib):
        options += ['-isystem', include]
    return [command[0], *options, *(a for a in command[1:] if keep(a))]


# Translation units not linted, by the build or by scripts/lint.py, matched against the absolute path
# of the TU, besides third-party code.
#
#   Hardware/ExceptionHandlers.cpp
#       clang cannot parse this TU, and the diagnostics it produces are hard errors rather than
#       warnings, so they cannot be silenced with a -Wno- flag or a NOLINT and would fail every
#       build:
#         * NMI_Handler and UsageFault_Handler are declared __attribute__((naked)) and defined with
#           a C++ body that calls SoftwareReset. GCC accepts it (SoftwareReset never returns);
#           clang rejects a non-asm statement in a naked function outright.
#         * vAssertCalled and std::__throw_bad_function_call are first declared with
#           __attribute__((noreturn)) elsewhere (FreeRTOSConfig.h, libstdc++'s functexcept.h) and
#           redeclared here with the standard [[noreturn]]; clang treats that as the attribute
#           appearing on a later declaration.
#
# Library sources that clang cannot parse, for the same reasons. Unlike DuetCANMaster's, these are to
# be fixed in the libraries, and each comes off the list once it is:
#   CoreN2G Cache.cpp, CoreN2G SAME70/Startup.cpp, RRFLibraries RTOSIface.cpp
#       redeclare vAssertCalled, or Reset_Handler, with [[noreturn]] after a declaration without it
#       (FreeRTOSConfig.h declares vAssertCalled with __attribute__((noreturn))); Startup.cpp's
#       Reset_Handler is also a naked function with a C++ body.
#   CoreN2G SAM4S_4E_E70/Reset.cpp
#       defines EraseAndReset without the noexcept its declaration in Reset.h has.
#   CoreN2G TinyUsbInterface.cpp
#       defines two tinyusb callbacks noexcept, which tinyusb's headers declare without.
SKIP = (
    os.path.join('src', 'Hardware', 'ExceptionHandlers.cpp'),
    os.path.join('lib', 'CoreN2G', 'src', 'Cache.cpp'),
    os.path.join('lib', 'CoreN2G', 'src', 'SAM4S_4E_E70', 'SAME70', 'Startup.cpp'),
    os.path.join('lib', 'CoreN2G', 'src', 'SAM4S_4E_E70', 'Reset.cpp'),
    os.path.join('lib', 'CoreN2G', 'src', 'TinyUsbInterface.cpp'),
    os.path.join('lib', 'RRFLibraries', 'src', 'RTOSIface', 'RTOSIface.cpp'),
)

# The first line of a diagnostic; its notes and source excerpt follow up to the next one
DIAGNOSTIC = re.compile(r'^(?P<path>[^\s:][^:]*):\d+:\d+: (?P<severity>warning|error): .*\[[\w.,-]+\]$')
# clang-tidy's summary lines, which count what is dropped here too
SUMMARY = re.compile(r'^(\d+ (warnings?|errors?)( and \d+ errors?)? generated\.|Error while processing .*\.'
                     r'|Suppressed \d+ warnings.*|\d+ warnings? treated as errors?)$')


def posix_escape(text: str) -> str:
    """Text as a POSIX extended regular expression, which clang-tidy's are. re.escape will not do: it
    also escapes '-', which POSIX leaves undefined."""
    return re.sub(r'([.^$*+?()\[\]{}|\\])', r'\\\1', text)


def skipped(source: str) -> bool:
    """Whether a translation unit is one of SKIP."""
    return any(skip in source for skip in SKIP)


def project_dir(source: str) -> str | None:
    """The directory of the project or library a source belongs to: src/<project> or lib/<library>."""
    try:
        parts = os.path.relpath(source, ROOT).split(os.sep)
    except ValueError:
        return None
    if len(parts) > 2 and parts[0] in ('src', 'lib'):
        return os.path.join(str(ROOT), parts[0], parts[1])
    return None


def own_diagnostics(lines: Iterable[str], source: str) -> list[str]:
    """The diagnostics of one translation unit's clang-tidy output that belong to its own project, with
    their notes: those in the TU and in its project's headers, less any in third-party code.

    clang-tidy applies the .clang-tidy of the TU to every header it reports on, so a library header
    reported from a DuetCANMaster TU would be judged by DuetCANMaster's checks rather than the
    library's. Each header is therefore reported only by the TUs of its own project. clang-tidy applies
    a header filter to its checks but not to clang's own errors, so that is done here."""
    project = project_dir(source)
    own = f'{project}/' if project is not None else None
    kept: list[str] = []
    keeping = False
    for line in lines:
        if match := DIAGNOSTIC.match(line):
            path = os.path.abspath(match['path'])
            keeping = not THIRD_PARTY.search(path) and (path == source or own is None or path.startswith(own))
        elif SUMMARY.match(line):
            keeping = False
            continue
        if keeping:
            kept.append(line)
    return kept


def lintable(source: str) -> bool:
    """Whether a translation unit is the firmware's own C++, which the build lints."""
    return source.endswith(('.cpp', '.cc')) and not THIRD_PARTY.search(source)


def sanitise_database(path: Path, prefix: str) -> None:
    """Rewrite a compile_commands.json in place for clang, keeping the translation units the build
    lints."""
    entries = cast(list[dict[str, object]], json.loads(path.read_text()))
    kept: list[dict[str, object]] = []
    for entry in entries:
        if not lintable(str(entry['file'])):
            continue
        if 'arguments' in entry:
            args = cast(list[str], entry['arguments'])
        else:
            args = shlex.split(str(entry.pop('command')))
        entry['arguments'] = rewrite(args, prefix)
        kept.append(entry)
    path.write_text(json.dumps(kept, indent=2))
    print(f'   kept and rewrote {len(kept)} of {len(entries)} entries in {path}')
