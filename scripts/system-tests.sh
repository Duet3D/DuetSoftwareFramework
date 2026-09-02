#!/usr/bin/env bash
set -uo pipefail

# Run the C# system tests, naming each test as it starts and listing the slow ones and the failures
# at the end.
#
# The per-test progress is written by src/SystemTests/Host/TestProgress.cs, which needs the console
# logger this asks for below to reach the terminal at all. The summaries are this script's own. The
# runner does report both facts already, but only spread through the run: each failure where it
# happened, buried under its stack trace and the DuetControlServer log the fixture dumps, and each
# duration on the line of the test it belongs to. Reading either back means scrolling through
# everything else, so both are gathered from the TRX once the run is over.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/src/SystemTests/SystemTests.csproj"

BUILD_TYPE=Debug
RESULTS_DIR="$REPO_ROOT/test-results"
# Scenarios in the KnownGap category document behaviour that is not implemented yet, so they are
# expected to fail. Pass --all to run them as well.
FILTER="TestCategory!=KnownGap"
# A scenario starts a DuetControlServer and drives it, so a second is normal and ten is not. Tests
# over this are listed after the run, slowest first, because the suite is long enough that the ones
# costing the most are worth seeing without reading back through every line.
SLOW_SECONDS=5

usage() {
    cat <<EOF
Usage: $(basename "$0") [OPTIONS] [-- <dotnet test args>]

Run the system tests, then list the slow tests and the failed ones by name.

Options:
  -c, --configuration <cfg>  Build configuration (default: $BUILD_TYPE)
  -o, --results-dir <dir>    Where to write results (default: $RESULTS_DIR)
      --filter <expr>        Test filter (default: $FILTER)
      --all                  Include the KnownGap scenarios, which are expected to fail
      --no-build             Skip the build and run the assembly as it stands
      --slow <seconds>       List tests slower than this afterwards (default: $SLOW_SECONDS)
  -h, --help                 Show this help

Outputs:
  <results-dir>/SystemTests.trx   Test results

Examples:
  $(basename "$0")
  $(basename "$0") --all
  $(basename "$0") --filter 'FullyQualifiedName~JobControl'
  $(basename "$0") -- -p:Profiling=true
EOF
}

EXTRA_ARGS=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--configuration) BUILD_TYPE="$2"; shift 2 ;;
        -o|--results-dir)   RESULTS_DIR="$2"; shift 2 ;;
        --filter)           FILTER="$2"; shift 2 ;;
        --all)              FILTER=""; shift ;;
        --no-build)         EXTRA_ARGS+=(--no-build); shift ;;
        --slow)             SLOW_SECONDS="$2"; shift 2 ;;
        -h|--help)          usage; exit 0 ;;
        --)                 shift; EXTRA_ARGS+=("$@"); break ;;
        *)                  echo "Unknown option: $1" >&2; usage; exit 1 ;;
    esac
done

if [[ -n "$FILTER" ]]; then
    EXTRA_ARGS+=(--filter "$FILTER")
fi

mkdir -p "$RESULTS_DIR"
TRX="$RESULTS_DIR/SystemTests.trx"
rm -f "$TRX"

echo "=== Running system tests ($BUILD_TYPE${FILTER:+, $FILTER}) ==="
# The console logger at normal verbosity is what streams the progress; its default verbosity holds
# everything back until the run is over. -tl:off because MSBuild's terminal logger echoes the test
# output on top of that logger, printing every line twice, and it turns itself on only when the
# output is a terminal: without this the same command behaves differently piped and interactively.
dotnet test "$PROJECT" -c "$BUILD_TYPE" \
    -tl:off \
    --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=$TRX" \
    --results-directory "$RESULTS_DIR" \
    "${EXTRA_ARGS[@]}"
STATUS=$?

# The TRX is the only record of the run that survives the scrollback, and it is what says which
# tests failed rather than merely how many. A run that fell over before producing one has nothing
# to summarise, so its exit status is all there is to report.
if [[ ! -f "$TRX" ]]; then
    echo
    echo "=== No test results were written to $TRX ===" >&2
    exit "$STATUS"
fi

# Both summaries below want the same thing the TRX does not state in one place: a result carries the
# outcome and the duration but only the method name, which is ambiguous across fixtures and is not
# what --filter matches on, while the fully qualified name is on the TestMethod of the definition the
# result points at by id. So one pass joins the two into a table of outcome, seconds and name, and
# the summaries are then just filters over it.
#
# Splitting on '<' rather than on newlines makes each record one tag, so nothing here depends on how
# the writer wrapped the file. Attribute values cannot contain a raw '<' because XML escapes it.
RESULTS="$(awk '
    BEGIN { RS = "<"; FS = "\n" }

    # The value of attribute k on this tag, or "" when the tag does not carry it. The leading space
    # is what keeps "name" from matching "className" and "testName".
    function attr(k,   prefix, v) {
        if (!match($0, " " k "=\"[^\"]*\"")) return ""
        prefix = length(k) + 3
        return substr($0, RSTART + prefix, RLENGTH - prefix - 1)
    }

    # HH:MM:SS.fffffff
    function seconds(d,   t) {
        return (split(d, t, ":") == 3) ? t[1] * 3600 + t[2] * 60 + t[3] : 0
    }

    # A definition is <UnitTest id=...> with the name on a <TestMethod> child a couple of tags later
    /^UnitTest / { id = attr("id"); next }
    /^TestMethod / { if (id != "") { name[id] = attr("className") "." attr("name"); id = "" } next }

    # Results come before the definitions in the file, so the join has to wait for the end
    /^UnitTestResult / {
        tid = attr("testId")
        if (tid == "") next
        order[++count] = tid
        outcome[tid] = attr("outcome")
        elapsed[tid] = seconds(attr("duration"))
    }

    END {
        for (i = 1; i <= count; i++) {
            tid = order[i]
            printf "%s\t%.1f\t%s\n", outcome[tid], elapsed[tid], (tid in name) ? name[tid] : "?"
        }
    }
' "$TRX")"

SLOW="$(awk -F'\t' -v limit="$SLOW_SECONDS" '$2 > limit { printf "%8.1f s  %s\n", $2, $3 }' <<< "$RESULTS" | sort -rn)"
if [[ -n "$SLOW" ]]; then
    echo
    echo "=== Tests over ${SLOW_SECONDS}s ==="
    echo "$SLOW"
fi

FAILED="$(awk -F'\t' '$1 == "Failed" { print $3 }' <<< "$RESULTS")"
echo
if [[ -z "$FAILED" ]]; then
    echo "=== No failures ==="
else
    echo "=== Failed tests ==="
    while IFS= read -r NAME; do
        echo "  $NAME"
    done <<< "$FAILED"
    echo
    echo "Rerun one of them with:"
    echo "  $(basename "$0") --filter \"FullyQualifiedName~<test-name>\""
fi
echo "Results: $TRX"

exit "$STATUS"
