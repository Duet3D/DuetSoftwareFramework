---
name: build-command
description: "Build the whole project with ./scripts/build.sh --all, not per-project dotnet build"
metadata:
  type: project
---

Build the project with `./scripts/build.sh --all` from the repo root. It builds every dotnet project *and*
cross-compiles the native `libduet_sbc.so` for aarch64 through CMake, then collates everything into
`build/dotnet/`. It ends with "Build complete. No deployment target specified." and deploys nothing.

**Why it matters:** `dotnet build src/DuetControlServer/DuetControlServer.csproj` misses the native
half entirely, and running `make` inside `src/DuetSbcInterface/build/native` compiles for the host
rather than the aarch64 target the deployed library needs, so a change to `DuetSbcInterface` can
look green under both and still be untested against the real toolchain.

**Do not pass `--local` or `--target`** unless deployment is actually wanted: `--local` tries to stop
systemd services and rsync into `/opt/dsf/bin`, neither of which exists in the devcontainer, so the
run fails at the end after a successful build.

Tests are separate: `dotnet test src/UnitTests/UnitTests.csproj` for the managed side, and `ctest`
in the native build directory. See [[mcode-migration-plan]].
