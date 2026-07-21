#!/usr/bin/env bash
#
# Export readability-identifier-naming fixes for DuetCANMaster across *every* board target.
#
# A compilation database only ever describes one board. Renaming from a single board's database
# rewrites that board's Pins_<board>.h plus every use of those constants in the shared .cpp files,
# while the other six pins headers keep the old names -- so the other targets stop compiling.
# Covering all boards and applying the merged result once is the only safe way to do it.
#
# The fixes are exported, NOT applied. Apply them in a single pass afterwards with:
#     clang-apply-replacements-18 --format --style=file <outdir>
# then rebuild every board.
#
set -uo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-${PROJECT_DIR}/.naming-fixes}"
BOARDS=(Duet3_MB6HC Duet3_MB6XD)

cd "${PROJECT_DIR}"
rm -rf "${OUT_DIR}"
mkdir -p "${OUT_DIR}"

failed=()
for board in "${BOARDS[@]}"; do
	echo "=============================================================="
	echo ">> ${board}: generating compile database"
	if ! Scripts/gen-compile-commands.sh "${board}" > "${OUT_DIR}/${board}.build.log" 2>&1; then
		echo "   !! build failed, see ${OUT_DIR}/${board}.build.log"
		failed+=("${board}")
		continue
	fi

	# Only this project's own first-party sources; src/libc and src/libcpp are vendored.
	#
	# Every remaining TU must be covered, including ones clang reports errors in. A renamed
	# declaration in a header is only useful if the use-sites are renamed too, and a use-site's
	# replacement is emitted solely by the TU that contains it -- skipping a .cpp renames the
	# header out from under it. ExceptionHandlers.cpp and Tasks.cpp between them reference 39
	# symbols that other TUs rename, so dropping either one breaks the build outright.
	mapfile -t sources < <(python3 -c "
import json, sys
seen = set()
for e in json.load(open('compile_commands.json')):
    f = e['file']
    if '/src/libc/' in f or '/src/libcpp/' in f:
        continue
    if f.endswith(('.cpp', '.cc')) and f not in seen:
        seen.add(f)
        print(f)
")
	echo ">> ${board}: exporting fixes from ${#sources[@]} translation units"
	i=0
	for f in "${sources[@]}"; do
		i=$((i + 1))
		clang-tidy -p . --checks='-*,readability-identifier-naming' \
			--export-fixes="${OUT_DIR}/${board}-${i}.yaml" "${f}" >/dev/null 2>&1
	done
	# Drop the empties clang-tidy leaves behind for clean TUs.
	find "${OUT_DIR}" -name "${board}-*.yaml" -size -2c -delete
	echo ">> ${board}: $(find "${OUT_DIR}" -name "${board}-*.yaml" | wc -l) non-empty fix files"
done

echo "=============================================================="
if [ ${#failed[@]} -gt 0 ]; then
	echo "BOARDS THAT FAILED TO BUILD: ${failed[*]}"
	echo "Fixes are incomplete -- do NOT apply them."
	exit 1
fi
echo "All boards covered. Fix files in ${OUT_DIR}"
