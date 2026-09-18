---
name: no-full-system-test-suite
description: Run the system tests through ./scripts/system-tests.sh with a --filter; never run the whole suite
metadata:
  type: feedback
---

Run the system tests with `./scripts/system-tests.sh`, never with a raw
`dotnet test src/SystemTests/SystemTests.csproj`, and always pass a `--filter` that names only the
fixtures the change affects. `src/UnitTests` is still run in full with
`dotnet test src/UnitTests/UnitTests.csproj` ([[build-command]]).

The script builds and runs the suite, names each test as it starts, and then lists the slow tests
and the failures by name with a clickable `file:line` for each. It leaves the `KnownGap` scenarios
out by default: those cover behaviour the source gets wrong or has not implemented yet, so they fail
until the source is fixed and the category comes off. `--skip-long-running` also drops the slowest
ones, and `--all` runs everything. `--help` lists the rest.

**Why:** The full suite takes a long time and carries failures that are already known about, so an
unfiltered run costs minutes and returns a result that has to be read against those by hand. The
script does that reading: it reports failures split into the ones already marked `KnownGap` and the
ones that are not, so what comes back is the set of tests the change actually broke.

**How to apply:** Pick the fixtures from the files being changed and run
`./scripts/system-tests.sh --filter "FullyQualifiedName~XTests|FullyQualifiedName~YTests"`. This
applies to subagents as well: a subagent given a test task gets the filter to use, not permission to
run the suite. See [[regression-suite-workflow]] for the separate regression harness and
[[pi-hardware-test-workflow]] for hardware runs.
