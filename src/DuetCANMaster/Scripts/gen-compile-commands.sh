#!/usr/bin/env bash
#
# Generate a clang-tidy-consumable compile_commands.json for DuetCANMaster.
#
# The firmware and its libraries are now ordinary CMake targets (add_executable/add_library), so
# CMake writes a complete compile_commands.json itself - no `bear` wrapper needed. It still has to
# be rewritten before clang-tidy can use it though: the firmware is built by GCC for bare-metal
# ARM, and clang rejects a handful of GCC-only flags outright and cannot find even <cstddef>
# without the cross toolchain's own include paths. clang-tidy against a database in that state
# still *reports* things, but it is parsing a broken TU, so any --fix output is untrustworthy.
#
# Usage:  Scripts/gen-compile-commands.sh [board-target]
#
set -euo pipefail

BOARD="${1:-Duet3_MB6HC}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${PROJECT_DIR}/../.." && pwd)"
BUILD_DIR="${PROJECT_DIR}/build/compile-commands"

LIBRARIES_DIR="${LIBRARIES_DIR:-${REPO_ROOT}/lib}"
CROSS_COMPILE="${CROSS_COMPILE:-/opt/arm-gnu-toolchain-15.2.rel1/bin/arm-none-eabi-}"

cd "${PROJECT_DIR}"

# CMAKE_EXPORT_COMPILE_COMMANDS makes CMake emit the database covering every target (the firmware
# and all six libraries) at configure time - it does not depend on anything being compiled.
cmake -S "${PROJECT_DIR}" -B "${BUILD_DIR}" \
	--toolchain "${PROJECT_DIR}/cmake/arm-none-eabi-toolchain.cmake" \
	-DCMAKE_BUILD_TYPE=Release \
	-DCMAKE_EXPORT_COMPILE_COMMANDS=ON \
	-DLIBRARIES_DIR="${LIBRARIES_DIR}" \
	-DCROSS_COMPILE="${CROSS_COMPILE}" >/dev/null

# The exported database covers every target (both boards, all libraries). Keep only the requested
# board's firmware translation units - one board's -DDUET3_<suffix> - so clang-tidy sees each
# first-party source once, the way the old one-board build did.
BOARD_DEFINE="DUET3_${BOARD#Duet3_}"
python3 - "${BUILD_DIR}/compile_commands.json" "${PROJECT_DIR}/compile_commands.json" "${BOARD_DEFINE}" <<'PY'
import json, sys
src, dst, board_define = sys.argv[1], sys.argv[2], sys.argv[3]
entries = json.load(open(src))
kept = [e for e in entries if board_define in (e.get("command") or " ".join(e.get("arguments", [])))]
json.dump(kept, open(dst, "w"), indent=2)
print(f"   kept {len(kept)}/{len(entries)} entries for {board_define}")
PY

echo ">> Rewriting compile_commands.json for clang"
CROSS_COMPILE="${CROSS_COMPILE}" python3 "${PROJECT_DIR}/Scripts/sanitise-compile-commands.py" \
	compile_commands.json

echo ">> Done. Run: clang-tidy -p ${PROJECT_DIR} <file>"
