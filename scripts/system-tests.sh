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
#
# Two categories in the sources say what a run is expected to look like: KnownGap marks a scenario
# whose behaviour is not implemented yet, so it fails on purpose, and LongRunning marks one that
# costs far more than a scenario normally does. The summaries report both against what the run
# actually did, and --tag-known-gaps and --tag-long-running write the difference back into the
# sources instead of reporting it.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/src/SystemTests/SystemTests.csproj"

BUILD_TYPE=Debug
RESULTS_DIR="$REPO_ROOT/test-results"
KNOWN_GAP=KnownGap
LONG_RUNNING=LongRunning
# Scenarios in the KnownGap category document behaviour that is not implemented yet, so they are
# expected to fail and are left out unless they are what the run is about. The LongRunning ones do
# pass, and cost the most time of anything in the suite, so leaving those out is offered as well.
# Either skip composes the filter the run is given, and a skip that was asked for narrows a named
# filter as well. Leaving the KnownGap scenarios out is only the default for a run that named no
# filter of its own, since a filter says which tests the run is about.
SKIP_KNOWN_GAPS=1
SKIP_KNOWN_GAPS_GIVEN=0
SKIP_LONG_RUNNING=0
SKIP_LONG_RUNNING_GIVEN=0
FILTER=""
FILTER_GIVEN=0
# A scenario starts a DuetControlServer and drives it, so a second is normal and ten is not. Tests
# over this are listed after the run, slowest first, because the suite is long enough that the ones
# costing the most are worth seeing without reading back through every line. It is also what the
# LongRunning category is measured against.
SLOW_SECONDS=5
TAG_KNOWN_GAPS=0
TAG_LONG_RUNNING=0

usage() {
    cat <<EOF
Usage: $(basename "$0") [OPTIONS] [-- <dotnet test args>]

Run the system tests, then list the slow tests and the failed ones by name.

Options:
  -c, --configuration <cfg>  Build configuration (default: $BUILD_TYPE)
  -o, --results-dir <dir>    Where to write results (default: $RESULTS_DIR)
      --filter <expr>        Test filter, in place of the one the skips below compose. A skip that
                             was asked for narrows it further
      --skip-known-gaps      Leave out the $KNOWN_GAP scenarios, which are expected to fail. On by
                             default for a run that named no filter
      --skip-long-running    Leave out the $LONG_RUNNING scenarios, the slowest in the suite
      --all                  Run every scenario, the $KNOWN_GAP and $LONG_RUNNING ones included
      --no-build             Skip the build and run the assembly as it stands
      --slow <seconds>       List tests slower than this afterwards (default: $SLOW_SECONDS)
      --tag-known-gaps       Write the $KNOWN_GAP category into the sources to match the run: add it
                             to the tests that failed, remove it from the ones that passed. Runs the
                             $KNOWN_GAP scenarios, since a test that did not run says nothing about
                             which category it belongs in
      --tag-long-running     Write the $LONG_RUNNING category into the sources to match the run: add
                             it to the tests over the slow threshold, remove it from the ones under
  -h, --help                 Show this help

Outputs:
  <results-dir>/SystemTests.trx   Test results

Without the tagging flags the same differences are reported instead: failures are split into the
ones already marked $KNOWN_GAP and the ones that are not, tests marked $KNOWN_GAP that passed are
listed on their own, and the slow list is split and followed by the tests marked $LONG_RUNNING that
came in under the threshold.

Examples:
  $(basename "$0")
  $(basename "$0") --skip-long-running
  $(basename "$0") --all
  $(basename "$0") --filter 'FullyQualifiedName~JobControl'
  $(basename "$0") --filter 'FullyQualifiedName~JobControl' --skip-long-running
  $(basename "$0") --all --tag-long-running --slow 20
  $(basename "$0") -- -p:Profiling=true
EOF
}

EXTRA_ARGS=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--configuration)   BUILD_TYPE="$2"; shift 2 ;;
        -o|--results-dir)     RESULTS_DIR="$2"; shift 2 ;;
        --filter)             FILTER="$2"; FILTER_GIVEN=1; shift 2 ;;
        --skip-known-gaps)    SKIP_KNOWN_GAPS=1; SKIP_KNOWN_GAPS_GIVEN=1; shift ;;
        --skip-long-running)  SKIP_LONG_RUNNING=1; SKIP_LONG_RUNNING_GIVEN=1; shift ;;
        --all)                SKIP_KNOWN_GAPS=0; SKIP_KNOWN_GAPS_GIVEN=1
                              SKIP_LONG_RUNNING=0; SKIP_LONG_RUNNING_GIVEN=1; shift ;;
        --no-build)           EXTRA_ARGS+=(--no-build); shift ;;
        --slow)               SLOW_SECONDS="$2"; shift 2 ;;
        --tag-known-gaps)     TAG_KNOWN_GAPS=1; shift ;;
        --tag-long-running)   TAG_LONG_RUNNING=1; shift ;;
        -h|--help)            usage; exit 0 ;;
        --)                   shift; EXTRA_ARGS+=("$@"); break ;;
        *)                    echo "Unknown option: $1" >&2; usage; exit 1 ;;
    esac
done

# The KnownGap scenarios are left out by default, and a category cannot be taken off a test that
# never ran. Asking to tag them is therefore asking to run them, unless the run was told otherwise.
if [[ $TAG_KNOWN_GAPS -eq 1 && $FILTER_GIVEN -eq 0 && $SKIP_KNOWN_GAPS_GIVEN -eq 0 ]]; then
    SKIP_KNOWN_GAPS=0
    echo "--tag-known-gaps runs the $KNOWN_GAP scenarios as well, so the category can be taken off the ones that pass"
fi

# One more category for the run to leave out
SKIPS=""
skip_category() {
    SKIPS="${SKIPS:+$SKIPS&}TestCategory!=$1"
}

# A skip that was asked for narrows the filter whether or not one was named, while the default skip
# applies only to a run that named none: a filter states which tests the run is about, and a skip it
# did not ask for has no say in that.
if [[ $SKIP_KNOWN_GAPS -eq 1 && ( $FILTER_GIVEN -eq 0 || $SKIP_KNOWN_GAPS_GIVEN -eq 1 ) ]]; then
    skip_category "$KNOWN_GAP"
fi
if [[ $SKIP_LONG_RUNNING -eq 1 && ( $FILTER_GIVEN -eq 0 || $SKIP_LONG_RUNNING_GIVEN -eq 1 ) ]]; then
    skip_category "$LONG_RUNNING"
fi

# The skips are the whole filter for a run that named none, and narrow the named one otherwise. That
# one is parenthesised on the way in because it may be a disjunction, which the conjunction joining
# the two would otherwise bind tighter than.
if [[ -n "$SKIPS" ]]; then
    FILTER="${FILTER:+($FILTER)&}$SKIPS"
fi

if [[ -n "$FILTER" ]]; then
    EXTRA_ARGS+=(--filter "$FILTER")
fi

mkdir -p "$RESULTS_DIR"
# The trx logger resolves its file name inside the results directory, so it is given the name alone
# and the directory is made absolute here. A relative one passed to both would be taken twice, once
# by the runner and once by this script, leaving the results a directory deeper than the summaries
# below look for them and reporting a run that wrote none.
RESULTS_DIR="$(cd "$RESULTS_DIR" && pwd)"
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
    --logger "trx;LogFileName=$(basename "$TRX")" \
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
    # is what keeps "name" from matching "className" and "testName". A test case names itself with
    # the arguments it was given, so the value comes back as XML wrote it and is decoded here to
    # read as the source does.
    function attr(k,   prefix, v) {
        if (!match($0, " " k "=\"[^\"]*\"")) return ""
        prefix = length(k) + 3
        v = substr($0, RSTART + prefix, RLENGTH - prefix - 1)
        gsub(/&quot;/, "\"", v)
        gsub(/&apos;/, "'\''", v)
        gsub(/&lt;/, "<", v)
        gsub(/&gt;/, ">", v)
        gsub(/&amp;/, "\\&", v)
        return v
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

# Neither the TRX nor the runner records where a test is written or which categories it carries, so
# both are resolved from the sources instead: the namespace and class enclosing a declaration make up
# the fully qualified name the TRX reports, the line is the method carrying it, and the categories
# are the attributes standing over it. A path with a line number is the form terminals linkify, so
# the summaries can be clicked into the test itself, and it is also what the tagging flags edit.
DECLARATIONS="$(find "$REPO_ROOT/src/SystemTests" -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -print0 |
    xargs -0 awk -v prefix="$REPO_ROOT/" '
    function reset() { categories = ""; names = 0 }

    FNR == 1 { ns = ""; cls = ""; reset() }

    /^[ \t]*namespace / { ns = $2; sub(/[;{].*/, "", ns); next }

    # Nested types are not used here, so the innermost class seen is the one a declaration belongs to.
    # The whole opening of the line has to match, or the word class in a comment would count as one.
    /^[ \t]*((public|internal|private|protected|abstract|sealed|static|partial|file)[ \t]+)*(class|record|struct)[ \t]+[A-Za-z_]/ {
        match($0, /(class|record|struct)[ \t]+[A-Za-z_][A-Za-z0-9_]*/)
        cls = substr($0, RSTART, RLENGTH)
        sub(/^(class|record|struct)[ \t]+/, "", cls)
        reset()
        next
    }

    # The text between the first pair of quotes in s
    function quoted(s,   from) {
        from = index(s, "\"") + 1
        return substr(s, from, index(substr(s, from), "\"") - 1)
    }

    # Attributes stand between the documentation and the declaration they belong to, so what has been
    # seen since the last declaration is what the next one carries. A category names an expectation
    # about the test; a TestCase naming itself takes over the name the TRX reports for that case, so
    # the name to look the declaration up by is that one rather than the method it was written on.
    /^[ \t]*\[/ {
        rest = $0
        while (match(rest, /Category\("[^"]*"\)/)) {
            categories = categories (categories == "" ? "" : ",") quoted(substr(rest, RSTART, RLENGTH))
            rest = substr(rest, RSTART + RLENGTH)
        }
        rest = $0
        while (match(rest, /TestName[ \t]*=[ \t]*"[^"]*"/)) {
            testName[++names] = quoted(substr(rest, RSTART, RLENGTH))
            rest = substr(rest, RSTART + RLENGTH)
        }
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
        file = FILENAME
        sub(prefix, "", file)
        record(((ns == "" ? "" : ns ".") cls "." member), file, FNR, categories)
        for (i = 1; i <= names; i++) {
            record(((ns == "" ? "" : ns ".") cls "." testName[i]), file, FNR, categories)
        }
        reset()
    }

    function record(key, file, line, cats) {
        if (key in loc) return
        loc[key] = file "\t" line "\t" cats
    }

    END { for (key in loc) printf "%s\t%s\n", key, loc[key] }
')"

# The name a result carries ends in the arguments of a TestCase, which the declaration it came from
# does not, so the lookup is on the name with those cut off. A TestCase that named itself is indexed
# under that name already. A test the index has nothing for, such as one generated at runtime, keeps
# an empty location and is printed without one, and the tagging flags leave it alone.
#
# Columns from here on: outcome, seconds, name, file:line, categories, file, line
RESULTS="$(awk -F'\t' -v OFS='\t' '
    NR == FNR { file[$1] = $2; line[$1] = $3; cats[$1] = $4; next }
    {
        key = $3
        sub(/\(.*/, "", key)
        if (key in file) print $0, file[key] ":" line[key], cats[key], file[key], line[key]
        else print $0, "", "", "", ""
    }
' <(printf '%s\n' "$DECLARATIONS") <(printf '%s\n' "$RESULTS"))"

# Whether a test belongs in a category is a property of the declaration rather than of one result,
# because a TestCase method reports a result per case: it is a known gap while any of its cases
# fails, and it is long running while its slowest case is over the threshold. This collapses the
# table onto the declaration and states, per category, whether the run agrees with the sources.
#
# Columns: file, line, category, add|remove, name, seconds
DIFFERENCES="$(awk -F'\t' -v OFS='\t' -v limit="$SLOW_SECONDS" -v gap="$KNOWN_GAP" -v long="$LONG_RUNNING" '
    function tagged(list, category) { return index("," list ",", "," category ",") > 0 }

    $6 == "" { next }
    {
        key = $6 SUBSEP $7
        file[key] = $6; line[key] = $7; cats[key] = $5
        if (!(key in shown) || $1 == "Failed") shown[key] = $3
        if ($1 == "Failed") failed[key] = 1
        else if ($1 == "Passed") passed[key] = 1
        if ($2 + 0 > slowest[key] + 0) slowest[key] = $2
    }

    END {
        for (key in file) {
            # A test that was skipped or never executed says nothing either way, so only a run that
            # reached a verdict is allowed to take a category off a declaration
            ran = (key in failed) || (key in passed)
            if ((key in failed) && !tagged(cats[key], gap))
                print file[key], line[key], gap, "add", shown[key], slowest[key]
            else if (ran && !(key in failed) && tagged(cats[key], gap))
                print file[key], line[key], gap, "remove", shown[key], slowest[key]

            if (slowest[key] + 0 > limit + 0 && !tagged(cats[key], long))
                print file[key], line[key], long, "add", shown[key], slowest[key]
            else if (ran && slowest[key] + 0 <= limit + 0 && tagged(cats[key], long))
                print file[key], line[key], long, "remove", shown[key], slowest[key]
        }
    }
' <<< "$RESULTS" | sort -t$'\t' -k3,3 -k1,1 -k2,2n)"

# Adding a category writes one attribute line over the declaration, which puts it at the end of the
# attribute block the declaration already carries. Removing one deletes the line that holds it out of
# that same block. Both are addressed by the line the declaration is on, and the file is rewritten in
# one pass, so the line numbers the run resolved stay valid however many edits a file takes.
apply_tags() {
    local edits="$1" file rewritten
    rewritten="$(mktemp)"
    while read -r file; do
        awk -F'\t' -v target="$file" '
            # A declaration takes both categories when both flags asked for it, so each side collects
            # the categories per line. The count is what says whether a separator is due, because an
            # assignment creates the element it writes to before the test could ask whether it exists.
            NR == FNR {
                if ($1 != target) next
                if ($4 == "add") added[$2] = (adds[$2]++ ? added[$2] "," : "") $3
                else removed[$2] = (removals[$2]++ ? removed[$2] "," : "") $3
                next
            }

            { src[FNR] = $0; lines = FNR }

            END {
                # The attribute holding a category is somewhere in the block standing over the
                # declaration, which is the run of lines above it that open with one
                for (at in removed) {
                    count = split(removed[at], categories, ",")
                    for (i = 1; i <= count; i++) {
                        for (line = at - 1; line >= 1; line--) {
                            head = src[line]
                            sub(/^[ \t]+/, "", head)
                            if (substr(head, 1, 1) != "[") break
                            if (index(src[line], "Category(\"" categories[i] "\")")) { drop[line] = 1; break }
                        }
                    }
                }

                for (line = 1; line <= lines; line++) {
                    if (line in added) {
                        match(src[line], /^[ \t]*/)
                        indent = substr(src[line], 1, RLENGTH)
                        count = split(added[line], categories, ",")
                        for (i = 1; i <= count; i++) print indent "[Category(\"" categories[i] "\")]"
                    }
                    if (!(line in drop)) print src[line]
                }
            }
        ' "$edits" "$REPO_ROOT/$file" > "$rewritten" && cat "$rewritten" > "$REPO_ROOT/$file"
    done < <(cut -f1 "$edits" | sort -u)
    rm -f "$rewritten"
}

# Rows of seconds, name and location, slowest first, as the slow lists print them
slow_rows() {
    sort -t$'\t' -k1,1rn -k2,2 | awk -F'\t' '{ printf "%8.1f s  %s\n", $1, $2; if ($3 != "") printf "%11s%s\n", "", $3 }'
}

# Rows of name and location, as the failure lists print them
named_rows() {
    sort | awk -F'\t' '{ printf "  %s\n", $1; if ($2 != "") printf "    %s\n", $2 }'
}

section() {
    local title="$1" body="$2"
    if [[ -n "$body" ]]; then
        echo
        echo "=== $title ==="
        echo "$body"
    fi
}

TAGGED="$(awk -F'\t' -v gap="$KNOWN_GAP" -v long="$LONG_RUNNING" -v dogap="$TAG_KNOWN_GAPS" -v dolong="$TAG_LONG_RUNNING" \
    '($3 == gap && dogap == 1) || ($3 == long && dolong == 1)' <<< "$DIFFERENCES")"

if [[ -n "$TAGGED" ]]; then
    EDITS="$(mktemp)"
    printf '%s\n' "$TAGGED" > "$EDITS"
    apply_tags "$EDITS"
    rm -f "$EDITS"

    echo
    echo "=== Categories written into the sources ==="
    awk -F'\t' '{ printf "  %-6s %-12s %s\n           %s:%s\n", $4, $3, $5, $1, $2 }' <<< "$TAGGED"

    # The summaries below report the run against the sources, and the sources have just been given
    # what the run says, so the categories the table carries are brought up to date with the edits.
    # Without this a test tagged a moment ago would be reported again as one that is not tagged.
    RESULTS="$(awk -F'\t' -v OFS='\t' '
        function without(list, unwanted,   count, parts, i, kept) {
            count = split(list, parts, ",")
            kept = ""
            for (i = 1; i <= count; i++)
                if (parts[i] != "" && index("," unwanted ",", "," parts[i] ",") == 0)
                    kept = kept (kept == "" ? "" : ",") parts[i]
            return kept
        }

        NR == FNR {
            key = $1 SUBSEP $2
            if ($4 == "add") added[key] = (adds[key]++ ? added[key] "," : "") $3
            else dropped[key] = (drops[key]++ ? dropped[key] "," : "") $3
            next
        }

        {
            key = $6 SUBSEP $7
            if (key in dropped) $5 = without($5, dropped[key])
            if (key in added) $5 = $5 ($5 == "" ? "" : ",") added[key]
            print
        }
    ' <(printf '%s\n' "$TAGGED") <(printf '%s\n' "$RESULTS"))"
fi

# What the run says about the categories the sources do not state, for the ones that were not just
# written back. A difference the tagging flags acted on is settled and is not reported again.
REMAINING="$(awk -F'\t' -v gap="$KNOWN_GAP" -v long="$LONG_RUNNING" -v dogap="$TAG_KNOWN_GAPS" -v dolong="$TAG_LONG_RUNNING" \
    '($3 == gap && dogap == 1) || ($3 == long && dolong == 1) { next } { print }' <<< "$DIFFERENCES")"

STALE_GAPS="$(awk -F'\t' -v OFS='\t' -v gap="$KNOWN_GAP" '$3 == gap && $4 == "remove" { print $5, $1 ":" $2 }' <<< "$REMAINING" | named_rows)"
STALE_LONG="$(awk -F'\t' -v OFS='\t' -v long="$LONG_RUNNING" '$3 == long && $4 == "remove" { print $6, $5, $1 ":" $2 }' <<< "$REMAINING" | slow_rows)"

SLOW_UNTAGGED="$(awk -F'\t' -v OFS='\t' -v limit="$SLOW_SECONDS" -v long="$LONG_RUNNING" \
    '$2 > limit && index("," $5 ",", "," long ",") == 0 { print $2, $3, $4 }' <<< "$RESULTS" | slow_rows)"
SLOW_TAGGED="$(awk -F'\t' -v OFS='\t' -v limit="$SLOW_SECONDS" -v long="$LONG_RUNNING" \
    '$2 > limit && index("," $5 ",", "," long ",") > 0 { print $2, $3, $4 }' <<< "$RESULTS" | slow_rows)"

section "Tests over ${SLOW_SECONDS}s that are not marked $LONG_RUNNING" "$SLOW_UNTAGGED"
section "Tests over ${SLOW_SECONDS}s marked $LONG_RUNNING" "$SLOW_TAGGED"
section "Marked $LONG_RUNNING but under ${SLOW_SECONDS}s in this run" "$STALE_LONG"

FAILED="$(awk -F'\t' -v OFS='\t' -v gap="$KNOWN_GAP" \
    '$1 == "Failed" && index("," $5 ",", "," gap ",") == 0 { print $3, $4 }' <<< "$RESULTS" | named_rows)"
FAILED_GAPS="$(awk -F'\t' -v OFS='\t' -v gap="$KNOWN_GAP" \
    '$1 == "Failed" && index("," $5 ",", "," gap ",") > 0 { print $3, $4 }' <<< "$RESULTS" | named_rows)"

section "Known gaps that failed as expected" "$FAILED_GAPS"
section "Marked $KNOWN_GAP but passed in this run" "$STALE_GAPS"

echo
if [[ -z "$FAILED" ]]; then
    echo "=== No unexpected failures ==="
else
    echo "=== Failed tests ==="
    echo "$FAILED"
    echo
    echo "Rerun one of them with:"
    echo "  $(basename "$0") --filter \"FullyQualifiedName~<test-name>\""
fi
echo "Results: $TRX"

exit "$STATUS"
