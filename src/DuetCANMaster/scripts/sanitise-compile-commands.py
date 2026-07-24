#!/usr/bin/env python3
"""Rewrite a GCC-produced compile_commands.json so clang-tidy can parse the TUs.

Two independent problems have to be solved or clang-tidy analyses a broken translation unit:

  1. clang errors out on GCC-only flags (-mfp16-format=ieee, -fsingle-precision-constant,
     -Wnoexcept, ...). One unknown argument aborts the whole TU.
  2. clang does not know where the bare-metal cross toolchain keeps its headers, so <cstddef>
     and friends are "file not found" and every subsequent declaration is unparsed.

Both are silent-ish: clang-tidy still emits diagnostics, so it is easy to believe a run
succeeded when in fact most of the code was never analysed. Any --fix derived from that state
can corrupt source, so this script exists to make the database honest.
"""

import json
import os
import subprocess
import sys

# GCC flags clang does not accept. An unknown argument is a hard error, not a warning.
DROP_EXACT = {
    "-fsingle-precision-constant",
    "-mfp16-format=ieee",
    "-Wnoexcept",
    "-fno-strict-volatile-bitfields",
    "-mno-unaligned-access",
    "-fstack-usage",
    "-fcallgraph-info",
    "-Wa,-adhlns",
    # The firmware builds with -Werror under GCC. Keeping it here would promote clang's own
    # stricter-than-GCC diagnostics to errors on code GCC compiles cleanly, burying the actual
    # clang-tidy findings under thousands of entries that no source change can resolve.
    "-Werror",
}

# Diagnostics where clang and the GCC cross compiler genuinely disagree on code GCC accepts.
# These are analyser artefacts, not defects, so they are silenced rather than "fixed":
#   missing-braces     - clang wants {{...}} for the nested aggregates in the PinTable; GCC does not
#   double-promotion   - clang flags float->double in contexts GCC's -Wdouble-promotion does not
#   unknown-attributes - the _ecv_* Escher/eCv annotations and some GCC attributes
#   uninitialized      - `register ... asm("sp")` reads the stack pointer; clang cannot model it
#   mismatched-tags    - struct/class forward-declaration mismatches GCC tolerates
# Match the cross toolchain's fixed-width integer typedefs. GCC's arm-none-eabi defines the 32-bit
# types as `long`, clang's as `int`. That is not cosmetic: CoreIO.h overloads on both uint32_t and
# unsigned int (and int32_t and int), which are distinct types for GCC but the same type for clang,
# so clang rejects them as redeclarations and abandons the rest of the header. Newlib's stdint.h
# builds its typedefs from these macros, so overriding them makes clang model the real ABI.
TARGET_TYPEDEFS = [
    "-D__UINT32_TYPE__=long unsigned int",
    "-D__INT32_TYPE__=long int",
]

SUPPRESS = [
    "-Wno-missing-braces",
    "-Wno-double-promotion",
    "-Wno-unknown-attributes",
    "-Wno-uninitialized",
    "-Wno-mismatched-tags",
    "-Wno-unknown-warning-option",
    "-Wno-ignored-optimization-argument",
]
DROP_PREFIX = (
    "-mfp16-format",
    "-fstack-usage",
    "-fcallgraph-info",
    "-Wa,",
    "--specs=",
    "-flto",
)


def toolchain_includes(cross_compile: str) -> list[str]:
    """Ask the cross g++ for its own system include paths."""
    cxx = cross_compile + "g++"
    try:
        proc = subprocess.run(
            [cxx, "-mcpu=cortex-m7", "-mthumb", "-E", "-x", "c++", "-", "-v"],
            input="",
            capture_output=True,
            text=True,
            check=True,
        )
    except (OSError, subprocess.CalledProcessError) as exc:
        sys.exit(f"error: could not query {cxx} for include paths: {exc}")

    paths, collecting = [], False
    for line in proc.stderr.splitlines():
        if line.startswith("#include <...> search starts here"):
            collecting = True
            continue
        if line.startswith("End of search list"):
            break
        if collecting:
            candidate = os.path.normpath(line.strip())
            if os.path.isdir(candidate):
                paths.append(candidate)
    if not paths:
        sys.exit(f"error: {cxx} reported no usable include directories")
    return paths


def keep(arg: str) -> bool:
    return arg not in DROP_EXACT and not arg.startswith(DROP_PREFIX)


def main() -> None:
    db_path = sys.argv[1] if len(sys.argv) > 1 else "compile_commands.json"
    cross = os.environ.get(
        "CROSS_COMPILE", "/opt/arm-gnu-toolchain-15.2.rel1/bin/arm-none-eabi-"
    )

    includes = toolchain_includes(cross)
    extra = ["--target=arm-none-eabi"] + TARGET_TYPEDEFS + SUPPRESS
    for inc in includes:
        extra += ["-isystem", inc]

    with open(db_path) as handle:
        entries = json.load(handle)

    dropped: set[str] = set()
    for entry in entries:
        if "arguments" in entry:
            args = entry["arguments"]
        else:
            # bear normally emits "arguments"; fall back to a naive split for "command".
            args = entry.pop("command").split()

        dropped.update(a for a in args[1:] if not keep(a))
        cleaned = [args[0]] + [a for a in args[1:] if keep(a)]

        # Insert after argv[0] so a trailing "-o foo.o -c foo.cpp" stays intact.
        entry["arguments"] = [cleaned[0]] + extra + cleaned[1:]

    with open(db_path, "w") as handle:
        json.dump(entries, handle, indent=2)

    print(f"   rewrote {len(entries)} entries in {db_path}")
    print(f"   added {len(includes)} toolchain include paths + --target=arm-none-eabi")
    if dropped:
        print(f"   dropped clang-incompatible flags: {' '.join(sorted(dropped))}")


if __name__ == "__main__":
    main()
