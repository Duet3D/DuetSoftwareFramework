---
name: no-full-system-test-suite
description: Never run the whole SystemTests suite; run only the fixtures the change touches
metadata:
  type: feedback
---

Do not run `dotnet test src/SystemTests/SystemTests.csproj` without a `--filter`. Run only the
fixtures the change affects, plus `src/UnitTests` in full.

**Why:** The full test suite takes a long time to run and has a number of known failures.

**How to apply:** Pick the fixtures from the files being changed and pass them as
`--filter "FullyQualifiedName~XTests|FullyQualifiedName~YTests"`. See
[[regression-suite-workflow]] for the separate regression harness and
[[pi-hardware-test-workflow]] for hardware runs.
