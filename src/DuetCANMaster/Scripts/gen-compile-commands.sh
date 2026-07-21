#!/usr/bin/env bash
#
# Generate a clang-tidy-consumable compile_commands.json for DuetCANMaster.
#
# The firmware is built by GCC for bare-metal ARM, so the raw compile database bear captures is
# not directly usable by clang-tidy: clang rejects a handful of GCC-only flags outright, and
# without the cross toolchain's own include paths it cannot find even <cstddef>. A clang-tidy run
# against a database in that state still *reports* things, but it is parsing a broken TU -- which
# makes any --fix output untrustworthy. So we build under bear, then rewrite the database.
#
# Usage:  Scripts/gen-compile-commands.sh [board-target]
#
set -euo pipefail

BOARD="${1:-Duet3_MB6HC}"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${PROJECT_DIR}/../.." && pwd)"

# The shared libraries live in the repo's lib/, not the Makefile's default ./libraries.
LIBRARIES_DIR="${LIBRARIES_DIR:-${REPO_ROOT}/lib}"
CROSS_COMPILE="${CROSS_COMPILE:-/opt/arm-gnu-toolchain-15.2.rel1/bin/arm-none-eabi-}"

if ! command -v bear >/dev/null 2>&1; then
	echo "error: bear is not installed (apt-get install bear)" >&2
	exit 1
fi

cd "${PROJECT_DIR}"

echo ">> Clean rebuild of ${BOARD} under bear (a partial build only captures the TUs it recompiles)"
make "clean-${BOARD}" LIBRARIES_DIR="${LIBRARIES_DIR}" >/dev/null 2>&1 || true
rm -f compile_commands.json

# -k so a link failure (the prebuilt .a libraries may be absent) still leaves every TU captured.
bear -- make "${BOARD}" -j"$(nproc)" -k \
	CROSS_COMPILE="${CROSS_COMPILE}" \
	LIBRARIES_DIR="${LIBRARIES_DIR}"

echo ">> Rewriting compile_commands.json for clang"
CROSS_COMPILE="${CROSS_COMPILE}" python3 "${PROJECT_DIR}/Scripts/sanitise-compile-commands.py" \
	compile_commands.json

echo ">> Done. Run: clang-tidy -p ${PROJECT_DIR} <file>"
