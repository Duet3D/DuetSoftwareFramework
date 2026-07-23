#!/usr/bin/env python3
"""Run clang-tidy on one firmware translation unit, as part of the build.

CMake's CXX_CLANG_TIDY hands the linter the *GCC* command line it is about to run:

    clang-tidy-wrapper.py <tidy args> <source> -- arm-none-eabi-g++ <firmware flags> -c <source>

clang cannot consume that command line as-is. It is the same pair of problems
Scripts/sanitise-compile-commands.py exists to solve for the standalone (compile_commands.json)
workflow: GCC-only flags are unknown arguments and abort the whole TU, and without the cross
toolchain's own include paths even <cstddef> is "file not found", so most of the code goes
unanalysed while clang-tidy still reports enough to look like it worked. This wrapper applies
exactly those fixups - importing the flag lists from that script so the two entry points cannot
drift apart - and then execs clang-tidy.

Some translation units are skipped (SKIP list below); the decision lives here rather than in a
per-source CMake property because the firmware target picks its sources up from one glob, and
because each exclusion needs the explanation that goes with it.

Options consumed by the wrapper itself (anything else is passed through to clang-tidy):
    --clang-tidy-binary=<path>   linter to exec (default: clang-tidy from PATH)
    --cross-compile=<prefix>     arm-none-eabi- prefix to query for include paths
"""

import importlib.util
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))

# The flag lists live in a script whose file name is not a legal module name, so load it by path.
_spec = importlib.util.spec_from_file_location(
    "sanitise_compile_commands", os.path.join(_HERE, "sanitise-compile-commands.py")
)
sanitise = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(sanitise)

# Translation units not linted during the build, matched against the absolute path of the TU.
#
#   src/libc/, src/libcpp/  - vendored newlib and GCC runtime sources (FSF copyright). They keep
#                             upstream formatting, and .clang-tidy excludes their headers for the
#                             same reason.
#   Hardware/ExceptionHandlers.cpp
#                           - clang cannot parse this TU, and the four diagnostics it produces are
#                             hard errors rather than warnings, so they cannot be silenced with a
#                             -Wno- flag or a NOLINT and would fail every build:
#                               * NMI_Handler and UsageFault_Handler are declared
#                                 __attribute__((naked)) and defined with a C++ body that calls
#                                 SoftwareReset. GCC accepts it (SoftwareReset never returns);
#                                 clang rejects a non-asm statement in a naked function outright.
#                               * vAssertCalled and std::__throw_bad_function_call are first
#                                 declared with __attribute__((noreturn)) elsewhere
#                                 (FreeRTOSConfig.h, libstdc++'s functexcept.h) and redeclared here
#                                 with the standard [[noreturn]]; clang treats that as the
#                                 attribute appearing on a later declaration.
#                             Scripts/gen-compile-commands.sh + clang-tidy still reports the file's
#                             other findings, which is where these were triaged.
SKIP = (
    os.path.join("src", "libc") + os.sep,
    os.path.join("src", "libcpp") + os.sep,
    os.path.join("src", "Hardware", "ExceptionHandlers.cpp"),
)

DEFAULT_CROSS_COMPILE = os.environ.get('ARM_GNU_TOOLCHAIN_DIR')


def main() -> None:
    argv = sys.argv[1:]
    if "--" not in argv:
        sys.exit("clang-tidy-wrapper: expected '--' before the compile command")
    split = argv.index("--")
    tidy_args, compile_args = argv[:split], argv[split + 1 :]
    if not compile_args:
        sys.exit("clang-tidy-wrapper: empty compile command")

    tidy = os.environ.get("CLANG_TIDY", "clang-tidy")
    cross = os.environ.get("CROSS_COMPILE", DEFAULT_CROSS_COMPILE)
    passthrough = []
    for arg in tidy_args:
        if arg.startswith("--clang-tidy-binary="):
            tidy = arg.split("=", 1)[1]
        elif arg.startswith("--cross-compile="):
            cross = arg.split("=", 1)[1]
        else:
            passthrough.append(arg)

    # CMake always ends the compile command with the source file ("... -c Foo.cpp").
    source = os.path.abspath(compile_args[-1])
    if any(skip in source for skip in SKIP):
        return

    extra = ["--target=arm-none-eabi"] + sanitise.TARGET_TYPEDEFS + sanitise.SUPPRESS
    for inc in sanitise.toolchain_includes(cross):
        extra += ["-isystem", inc]

    # Insert after the compiler itself so the trailing "-o Foo.o -c Foo.cpp" stays intact.
    cleaned = [a for a in compile_args[1:] if sanitise.keep(a)]
    os.execvp(tidy, [tidy] + passthrough + ["--", compile_args[0]] + extra + cleaned)


if __name__ == "__main__":
    main()
