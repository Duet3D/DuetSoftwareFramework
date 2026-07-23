# Post-processor for clang-tidy / run-clang-tidy output.
#
#   run-clang-tidy-18 -p . -j "$(nproc)" -quiet '<file regex>' 2>&1 | awk -f scripts/tidy-report.awk
#
# Passes the diagnostics through unchanged (minus clang's per-TU "N warnings generated." and
# "Suppressed N warnings" chatter, which is meaningless once several TUs are interleaved), then
# prints a tally of how many of each check fired.
#
# Diagnostic lines look like
#     /path/File.cpp:12:34: warning: some message [modernize-use-auto]
# and a single line can name more than one check, comma separated. Such a line counts once towards
# the total and once towards each check it names, so the per-check column can add up to more than
# the total. Note also that a warning raised in a shared header is reported once per translation
# unit that includes it, so counts are occurrences, not distinct source locations.
#
# Written for POSIX awk (mawk): the sort is delegated to sort(1) rather than gawk's asorti().

/^[0-9]+ warnings?( and [0-9]+ errors?)? generated\.$/ { next }
/^Suppressed [0-9]+ warnings/                          { next }
/^[0-9]+ warnings? treated as errors?$/                { next }

{ print }

/:[0-9]+:[0-9]+: (warning|error): / {
	total++
	if ($0 ~ /: error: /) { errors++ } else { warnings++ }

	if (match($0, /\[[^][]+\]$/)) {
		names = substr($0, RSTART + 1, RLENGTH - 2)
		n = split(names, parts, ",")
		for (i = 1; i <= n; i++) {
			gsub(/^[ \t]+|[ \t]+$/, "", parts[i])
			# Every clang-tidy check is "<group>-<name>"; anything without a dash is a stray
			# bracketed word at the end of a message, not a check.
			if (parts[i] ~ /-/) { count[parts[i]]++ } else { unnamed++ }
		}
	} else {
		unnamed++
	}
}

END {
	printf "\n== clang-tidy summary ==\n"
	if (total == 0) {
		printf "  no diagnostics\n"
		exit
	}

	# Flush our own writes before handing the tally to the child process, otherwise the two
	# streams interleave.
	fflush()

	sorter = "sort -k1,1nr -k2,2"
	for (c in count) { printf "  %6d  %s\n", count[c], c | sorter }
	if (unnamed > 0) { printf "  %6d  %s\n", unnamed, "(no check name)" | sorter }
	close(sorter)

	printf "  %6s  %s\n", "------", "---"
	printf "  %6d  diagnostics total (%d %s, %d %s)\n",
		total, warnings, plural(warnings, "warning"), errors, plural(errors, "error")
}

function plural(n, word) {
	return (n == 1) ? word : word "s"
}
