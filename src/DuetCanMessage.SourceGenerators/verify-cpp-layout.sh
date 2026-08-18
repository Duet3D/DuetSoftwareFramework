#!/usr/bin/env bash
#
# Verify that the generated CAN message layouts are byte-for-byte identical to CANlib's.
#
# The generator emits CanMessageLayoutProbe.cpp, which asserts the size of every struct, the offset of
# every plain member and — by setting each bitfield to all ones on an otherwise-zeroed struct — the exact
# bit position and width of every bitfield. The probe is compiled twice:
#
#   1. against CANlib's hand-written lib/CANlib/src/CanMessageFormats.h, proving that the schema
#      describes the real message formats faithfully;
#   2. against the generated header, proving that it is equivalent to the hand-written one.
#
# It then compiles CANlib's own translation units against the generated header to confirm that it is a
# drop-in replacement, not merely layout-compatible, and diffs the two headers' method surfaces. That
# last step catches what neither the probe nor the compile can see: only three of CANlib's translation
# units are available here, so a method that only the firmware calls can go missing without any of the
# other checks noticing.
#
# The matching C# side is checked by the generated NUnit fixture in src/UnitTests/Link/CanMessageLayout.g.cs,
# which asserts the same expectations against the generated C# structs.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GENERATED="$ROOT/src/DuetCanMessage.SourceGenerators/generated/cpp"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

CXX="${CXX:-g++}"
INCLUDES=(-I "$ROOT/lib/CANlib/src" -I "$ROOT/lib/RRFLibraries/src" -I "$ROOT/lib/CoreN2G/src")
FLAGS=(-std=c++17 -w)

# CANlib targets ARM, where float16_t is __fp16. Which spelling of a 16-bit float the host compiler
# accepts depends on the host: an AArch64 g++ has __fp16 as a keyword and (before g++ 13) rejects
# _Float16 in C++, while on x86-64 it is the other way round. Substituting one for the other
# unconditionally therefore fixes one host by breaking the other, so ask the compiler instead.
cxx_accepts_type() {
	echo "typedef $1 probe_float16_t; probe_float16_t v;" | "$CXX" -std=c++17 -w -x c++ -fsyntax-only - 2>/dev/null
}

if cxx_accepts_type __fp16; then
	: # Native __fp16 (ARM hosts); CANlib's headers compile as written
elif cxx_accepts_type _Float16; then
	FLAGS+=(-D__fp16=_Float16)
else
	echo "error: $CXX supports neither __fp16 nor _Float16, so CANlib's float16_t cannot be compiled here" >&2
	exit 1
fi

for dir in "$ROOT/lib/CANlib/src" "$ROOT/lib/RRFLibraries/src" "$ROOT/lib/CoreN2G/src"; do
	if [ ! -d "$dir" ]; then
		echo "error: $dir is missing; run 'git submodule update --init' first" >&2
		exit 1
	fi
done

echo "==> Probing CANlib's hand-written CanMessageFormats.h"
"$CXX" "${FLAGS[@]}" "${INCLUDES[@]}" -o "$WORK/probe-canlib" "$GENERATED/CanMessageLayoutProbe.cpp"
"$WORK/probe-canlib"

echo "==> Probing the generated CanMessageFormats.h"
"$CXX" "${FLAGS[@]}" -I "$GENERATED" "${INCLUDES[@]}" -o "$WORK/probe-generated" "$GENERATED/CanMessageLayoutProbe.cpp"
"$WORK/probe-generated"

echo "==> Probing CANlib's hand-written CanMessageGenericTables.h"
"$CXX" "${FLAGS[@]}" "${INCLUDES[@]}" -o "$WORK/tables-canlib" "$GENERATED/CanMessageGenericTablesProbe.cpp"
"$WORK/tables-canlib"

echo "==> Probing the generated CanMessageGenericTables.h"
"$CXX" "${FLAGS[@]}" -I "$GENERATED" "${INCLUDES[@]}" -o "$WORK/tables-generated" "$GENERATED/CanMessageGenericTablesProbe.cpp"
"$WORK/tables-generated"

echo "==> Compiling CANlib's own sources against the generated headers"
for source in CanMessageFormats CanMessageBuffer CanMessageGenericParser CanSettings; do
	"$CXX" "${FLAGS[@]}" -fsyntax-only -I "$GENERATED" "${INCLUDES[@]}" "$ROOT/lib/CANlib/src/$source.cpp"
	echo "    $source.cpp OK"
done

echo "==> Comparing method surfaces"
python3 "$ROOT/src/DuetCanMessage.SourceGenerators/compare-method-surface.py" \
	"$ROOT/lib/CANlib/src/CanMessageFormats.h" "$GENERATED/CanMessageFormats.h"
python3 "$ROOT/src/DuetCanMessage.SourceGenerators/compare-method-surface.py" \
	"$ROOT/lib/CANlib/src/CanSettings.h" "$GENERATED/CanSettings.h"

# The layout probe proves where every field sits and is blind to what any of them is worth, so the protocol
# magic numbers are checked separately
echo "==> Comparing constants"
python3 "$ROOT/src/DuetCanMessage.SourceGenerators/compare-constants.py" \
	"$ROOT/src/DuetCanMessage.SourceGenerators/Schema/can-messages.json" "$ROOT/lib/CANlib/src"

# A wrong enum value does not corrupt a message, it makes a well-formed one mean something else, and none of
# the checks above look at any of these values
echo "==> Comparing enums"
python3 "$ROOT/src/DuetCanMessage.SourceGenerators/compare-enums.py" \
	"$ROOT/src/DuetCanMessage.SourceGenerators/Schema/can-messages.json" "$ROOT/lib/CANlib/src"

echo "==> C++ layouts verified"
