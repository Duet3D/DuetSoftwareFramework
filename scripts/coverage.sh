#!/usr/bin/env bash
set -euo pipefail

# Run the C# unit tests under coverage and report line/branch coverage per assembly.
#
# Coverage comes from the coverlet.collector package reference in UnitTests.csproj, so nothing has
# to be installed for this to work. What is measured, and what is left out, is configured in
# src/UnitTests/coverlet.runsettings rather than here.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/src/UnitTests/UnitTests.csproj"
RUNSETTINGS="$REPO_ROOT/src/UnitTests/coverlet.runsettings"

BUILD_TYPE=Debug
RESULTS_DIR="$REPO_ROOT/test-results"
THRESHOLD=""
# Tests in the RequiresMachine category talk to a real Duet/SBC over HTTP. They skip themselves
# unless DSF_TEST_MACHINE_URL is set, but excluding them explicitly keeps them from stalling a run.
FILTER="TestCategory!=RequiresMachine"

usage() {
    cat <<EOF
Usage: $(basename "$0") [OPTIONS]

Run the unit tests with code coverage and print a per-assembly summary.

Options:
  -c, --configuration <cfg>  Build configuration (default: $BUILD_TYPE)
  -o, --results-dir <dir>    Where to write results (default: $RESULTS_DIR)
      --threshold <pct>      Exit non-zero if total line coverage is below <pct>
      --filter <expr>        NUnit test filter (default: $FILTER)
  -h, --help                 Show this help

Outputs:
  <results-dir>/coverage.cobertura.xml   Coverage report, Cobertura format
  <results-dir>/UnitTests.trx            Test results

The Cobertura file is what CI publishes and what report viewers consume. For a browsable HTML
report, install reportgenerator and point it at that file:

  dotnet tool install -g dotnet-reportgenerator-globaltool
  reportgenerator -reports:$RESULTS_DIR/coverage.cobertura.xml -targetdir:coverage-report

Examples:
  $(basename "$0")
  $(basename "$0") --threshold 35
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--configuration) BUILD_TYPE="$2"; shift 2 ;;
        -o|--results-dir)   RESULTS_DIR="$2"; shift 2 ;;
        --threshold)        THRESHOLD="$2"; shift 2 ;;
        --filter)           FILTER="$2"; shift 2 ;;
        -h|--help)          usage; exit 0 ;;
        *)                  echo "Unknown option: $1" >&2; usage; exit 1 ;;
    esac
done

if ! command -v xmllint >/dev/null 2>&1; then
    echo "Error: xmllint is required to summarise the report (apt package libxml2-utils)" >&2
    exit 1
fi

# Each run writes into a fresh GUID directory under the results directory. Clearing them first
# means the newest report can be found without having to guess which GUID belongs to this run.
rm -rf "${RESULTS_DIR:?}"/*/ 2>/dev/null || true
mkdir -p "$RESULTS_DIR"

echo "=== Running unit tests with coverage ($BUILD_TYPE) ==="
dotnet test "$PROJECT" -c "$BUILD_TYPE" \
    --filter "$FILTER" \
    --settings "$RUNSETTINGS" \
    --collect "XPlat Code Coverage" \
    --logger "trx;LogFileName=UnitTests.trx" \
    --results-directory "$RESULTS_DIR"

REPORT="$RESULTS_DIR/coverage.cobertura.xml"
GENERATED="$(find "$RESULTS_DIR" -name 'coverage.cobertura.xml' -not -path "$REPORT" | head -1)"
if [[ -z "$GENERATED" ]]; then
    echo "Error: the test run produced no coverage report under $RESULTS_DIR" >&2
    exit 1
fi
mv "$GENERATED" "$REPORT"

# xmllint rather than a language runtime: libxml2-utils is already a declared dependency of the
# devcontainer image, so this runs the same way locally and in CI.
attr() { xmllint --xpath "string($1)" "$REPORT"; }

pct() { awk -v r="$1" 'BEGIN { printf "%.1f", r * 100 }'; }

echo
echo "=== Coverage ==="
printf '%-28s %9s %9s\n' "Assembly" "Line" "Branch"
printf '%-28s %9s %9s\n' "----------------------------" "---------" "---------"

PACKAGES="$(attr 'count(/coverage/packages/package)')"
for ((i = 1; i <= PACKAGES; i++)); do
    printf '%-28s %8s%% %8s%%\n' \
        "$(attr "/coverage/packages/package[$i]/@name")" \
        "$(pct "$(attr "/coverage/packages/package[$i]/@line-rate")")" \
        "$(pct "$(attr "/coverage/packages/package[$i]/@branch-rate")")"
done

LINE_RATE="$(attr '/coverage/@line-rate')"
LINE_PCT="$(pct "$LINE_RATE")"
printf '%-28s %9s %9s\n' "----------------------------" "---------" "---------"
printf '%-28s %8s%% %8s%%\n' "TOTAL" "$LINE_PCT" "$(pct "$(attr '/coverage/@branch-rate')")"
echo
echo "Lines covered: $(attr '/coverage/@lines-covered') / $(attr '/coverage/@lines-valid')"
echo "Report:        $REPORT"

if [[ -n "$THRESHOLD" ]]; then
    if awk -v have="$LINE_PCT" -v want="$THRESHOLD" 'BEGIN { exit !(have < want) }'; then
        echo
        echo "Error: total line coverage ${LINE_PCT}% is below the ${THRESHOLD}% threshold" >&2
        exit 1
    fi
    echo "Threshold:     ${LINE_PCT}% >= ${THRESHOLD}%"
fi
