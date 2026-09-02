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
# everything else, so both are gathered from the TRX once the run is over, and each name is given
# the source location the terminal can turn into a link.

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

# Neither the TRX nor the runner records where a test is written, so the location under each name
# below is resolved from the sources instead: the namespace and class enclosing a declaration make
# up the fully qualified name the TRX reports, and the line is the method carrying it. A path with a
# line number is the form terminals linkify, so the summaries can be clicked into the test itself.
LOCATIONS="$(find "$REPO_ROOT/src/SystemTests" -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -print0 |
    xargs -0 awk -v prefix="$REPO_ROOT/" '
    FNR == 1 { ns = ""; cls = "" }

    /^[ \t]*namespace / { ns = $2; sub(/[;{].*/, "", ns); next }

    # Nested types are not used here, so the innermost class seen is the one a declaration belongs to.
    # The whole opening of the line has to match, or the word class in a comment would count as one.
    /^[ \t]*((public|internal|private|protected|abstract|sealed|static|partial|file)[ \t]+)*(class|record|struct)[ \t]+[A-Za-z_]/ {
        match($0, /(class|record|struct)[ \t]+[A-Za-z_][A-Za-z0-9_]*/)
        cls = substr($0, RSTART, RLENGTH)
        sub(/^(class|record|struct)[ \t]+/, "", cls)
    }

    # A member declaration opens with an access modifier and names itself just before its parameter
    # list. That also catches constructors and helpers, which cost an unused entry and nothing else.
    /^[ \t]*(public|protected|internal|private)[ \t]/ {
        p = index($0, "(")
        if (p == 0 || cls == "") next
        head = substr($0, 1, p - 1)
        # An assignment before the parenthesis means a field or a property, not a member being named
        if (index(head, "=") > 0) next
        if (!match(head, /[A-Za-z_][A-Za-z0-9_]*[ \t]*$/)) next
        member = substr(head, RSTART, RLENGTH)
        gsub(/[ \t]/, "", member)
        key = (ns == "" ? "" : ns ".") cls "." member
        if (!(key in loc)) { file = FILENAME; sub(prefix, "", file); loc[key] = file ":" FNR }
    }

    END { for (key in loc) printf "%s\t%s\n", key, loc[key] }
')"

# The name a result carries ends in the arguments of a TestCase, which the declaration it came from
# does not, so the lookup is on the name with those cut off. A test the index has nothing for, such
# as one generated at runtime, keeps an empty location and is printed without one.
RESULTS="$(awk -F'\t' -v OFS='\t' '
    NR == FNR { loc[$1] = $2; next }
    { key = $3; sub(/\(.*/, "", key); print $0, (key in loc) ? loc[key] : "" }
' <(printf '%s\n' "$LOCATIONS") <(printf '%s\n' "$RESULTS"))"

SLOW="$(awk -F'\t' -v limit="$SLOW_SECONDS" '$2 > limit' <<< "$RESULTS" | sort -t$'\t' -k2,2rn -k3,3 |
    awk -F'\t' '{ printf "%8.1f s  %s\n", $2, $3; if ($4 != "") printf "%11s%s\n", "", $4 }')"
if [[ -n "$SLOW" ]]; then
    echo
    echo "=== Tests over ${SLOW_SECONDS}s ==="
    echo "$SLOW"
fi

FAILED="$(awk -F'\t' -v OFS='\t' '$1 == "Failed" { print $3, $4 }' <<< "$RESULTS" | sort)"
echo
if [[ -z "$FAILED" ]]; then
    echo "=== No failures ==="
else
    echo "=== Failed tests ==="
    while IFS=$'\t' read -r NAME LOCATION; do
        echo "  $NAME"
        [[ -n "$LOCATION" ]] && echo "    $LOCATION"
    done <<< "$FAILED"
    echo
    echo "Rerun one of them with:"
    echo "  $(basename "$0") --filter \"FullyQualifiedName~<test-name>\""
fi
echo "Results: $TRX"

exit "$STATUS"
