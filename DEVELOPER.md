# Duet Software Framework Developer Setup Guide

This guide explains how to set up a development environment for [Duet Software Framework (DSF)](https://github.com/Duet3D/DuetSoftwareFramework) from scratch using [Visual Studio Code](https://code.visualstudio.com/) and [Docker](https://www.docker.com/).

No prior knowledge of VS Code, Docker, or DSF is assumed.

## Table of Contents

1. [Overview](#overview)
2. [Prerequisites](#prerequisites)
3. [Install Required Software](#install-required-software)
4. [Clone the Repository](#clone-the-repository)
5. [Open the Project in VS Code](#open-the-project-in-vs-code)
6. [Open in Dev Container](#open-in-dev-container)
7. [First Build](#first-build)
8. [Build Using VS Code Tasks](#build-using-vs-code-tasks)
9. [Run the Tests](#run-the-tests)
10. [Deploy to a Remote DuetPi/SBC](#deploy-to-a-remote-duetpisbc)
11. [Build the Firmware](#build-the-firmware)
12. [Debug the Firmware](#debug-the-firmware)
13. [Profiling with Tracy](#profiling-with-tracy)
14. [Working with Git in the Dev Container](#working-with-git-in-the-dev-container)
15. [Troubleshooting](#troubleshooting)

## Overview

Duet Software Framework is a collection of .NET services and tools that run on a Linux single-board computer (SBC) and communicate with a Duet board. For a higher-level project overview, see [README.md](README.md).

This repository includes a Dev Container configuration in [.devcontainer/devcontainer.json](.devcontainer/devcontainer.json) and [.devcontainer/Dockerfile](.devcontainer/Dockerfile). A Dev Container is a Docker-based development environment defined in code. When you open this project in VS Code, it can automatically build and start a container with all required build tools preinstalled.

Inside the Dev Container, you get:

- .NET SDK
- Git
- Make and packaging utilities
- SSH and rsync tools for deployment
- Node/npm tools needed by parts of the build workflow

## Prerequisites

Install these tools on your host machine first:

- [Git](https://git-scm.com/)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (or [Docker Engine](https://docs.docker.com/engine/install/) on Linux)
- [Visual Studio Code](https://code.visualstudio.com/)
- VS Code extension: [Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) (by Microsoft)

### What is Docker?

Docker runs isolated environments called containers. Each container has its own filesystem and toolchain.

For DSF, Docker avoids "works on my machine" problems by giving everyone the same Linux build environment.

### What is VS Code Dev Containers?

The Dev Containers extension lets VS Code:

- Build the container image from .devcontainer/Dockerfile
- Start the container
- Reopen your project inside that container

When this is done, terminals in VS Code run inside the container.

## Install Required Software

### 1. Git

Download from [git-scm.com/downloads](https://git-scm.com/downloads) or install from your package manager.

Verify:

```bash
git --version
```

### 2. Docker

Install Docker Desktop:
[docker.com/products/docker-desktop](https://www.docker.com/products/docker-desktop/)

Linux users can install Docker Engine instead:
[docs.docker.com/engine/install](https://docs.docker.com/engine/install/)

Verify:

```bash
docker --version
```

### 3. Visual Studio Code

Install VS Code:
[code.visualstudio.com](https://code.visualstudio.com/)

Then install the Dev Containers extension:

1. Open VS Code.
2. Open Extensions.
3. Search for Dev Containers.
4. Install Dev Containers (publisher: Microsoft).

## Clone the Repository

Open a terminal and run:

```bash
git clone https://github.com/Duet3D/DuetSoftwareFramework.git
cd DuetSoftwareFramework
```

If you need a specific branch, check it out before building:

```bash
git checkout v3.7-andy
```

## Open the Project in VS Code

From a terminal inside the repository:

```bash
code .
```

Or use File -> Open Folder and select the DuetSoftwareFramework folder.

## Open in Dev Container

When VS Code opens this repository, it should detect [.devcontainer/devcontainer.json](.devcontainer/devcontainer.json) and prompt:

"Folder contains a Dev Container configuration file. Reopen in Container?"

Choose Reopen in Container.

If you do not see the prompt:

1. Open the Command Palette.
2. Run Dev Containers: Reopen in Container.

On first run, VS Code will:

1. Build the Docker image from [.devcontainer/Dockerfile](.devcontainer/Dockerfile).
2. Start the container.
3. Run post-create setup, including restore of the solution.

The first build can take several minutes. Later starts are usually much faster due to Docker layer caching.

## First Build

After the container finishes starting, open a new terminal in VS Code.

Build the whole solution:

```bash
cd src
dotnet build DuetSoftwareFramework.sln
```

The solution file is [src/DuetSoftwareFramework.sln](src/DuetSoftwareFramework.sln).

If build succeeds, your environment is ready.

## Build Using VS Code Tasks

This repository includes predefined tasks in [.vscode/tasks.json](.vscode/tasks.json).

To run a task:

1. Press Ctrl+Shift+P.
2. Run Tasks: Run Task.
3. Select a task.

Common tasks:

- Build All
- Build and Deploy
- Restart DSF Services
- Build individual components (for example Build DuetControlServer)

The Build All task produces binaries under the local build/ directory.

## Run the Tests

The unit tests run under coverage and print a per-assembly table:

```bash
scripts/coverage.py
```

The system tests start a real DuetControlServer against a scripted CAN controller, so they take
minutes rather than seconds:

```bash
scripts/system_tests.py
```

They run the real `libduet_realtime_core.so`, which the project builds from `src/DuetRealtimeCore` as part of
the test build, so the submodule that native build needs has to be checked out. A clone made without
`--recurse-submodules`, and every new git worktree, starts without it:

```bash
git submodule update --init lib/RRFLibraries
```

Each test is named on the console as it starts, with its position in the run and how long the run
has been going:

```
[  42  03:17] SystemTests.Scenarios.DeferredPauseTests.PauseDuringToolChange
  Passed PauseDuringToolChange [2 s]
```

A run that has stopped therefore looks different from a run that is merely slow: whatever was named
last is the test that is stuck. Once the run is over the script lists the failed tests by name,
below the stack traces and the DuetControlServer logs a failure dumps, where they can be read as a
list.

Scenarios in the `KnownGap` category assert behaviour the source does not produce yet, because it is
unimplemented or implemented incorrectly, so they fail until it is fixed and the category is taken
off. The script leaves them out, as CI does; `--all` puts them back. To narrow a run:

```bash
scripts/system_tests.py --filter 'FullyQualifiedName~JobControl'
scripts/system_tests.py --help
```

Running the project directly works, but the progress needs the console logger asked for by name,
because at its default verbosity it prints nothing until the run is over:

```bash
dotnet test src/SystemTests/SystemTests.csproj -tl:off \
    --logger 'console;verbosity=normal' --filter 'TestCategory!=KnownGap'
```

`-tl:off` turns off MSBuild's terminal logger, which otherwise echoes the test output on top of that
logger and prints every line twice. It turns itself on only when the output is a terminal, so
without it the same command behaves differently piped and interactively.

## Deploy to a Remote DuetPi/SBC

The VS Code tasks include a deployment flow that uses SSH and rsync.

Typical sequence:

1. Run Build and Deploy.
2. Enter the target IP address when prompted.

What the task flow does:

1. Stops DSF services on the target.
2. Builds components locally into build/.
3. Copies build output to /opt/dsf/bin/ on the target via rsync.
4. Copies DuetPiManagementPlugin to /opt/dsf/plugins/ on the target, manifest included.
5. Starts DSF services again.

Notes:

- The remote commands use the root account by default.
- DuetPiManagementPlugin is a plugin, not a service, so it is deployed to the plugin directory
  DuetPluginService runs it from rather than to /opt/dsf/bin/. Its manifest is regenerated from
  src/Directory.Build.props on every build, because DuetControlServer refuses to start a plugin
  whose manifest names a different DSF version than the one it is running.
- Ensure SSH access is configured to the target machine.
- Ensure Docker container networking can reach the target IP.

Optional helper task:

- Install vsdbg on Remote

This installs Microsoft vsdbg on the remote machine for debugging scenarios.

The task definitions are in [.vscode/tasks.json](.vscode/tasks.json) if you want to inspect or extend them.

## Build the Firmware

The firmware projects build from the repository root with one toolchain, against the shared libraries in `lib/`:

| Project | Path | Builds |
| --- | --- | --- |
| DuetCANMaster | `src/DuetCANMaster` | The SPI-to-CAN-FD bridge on the main board an SBC drives (`canmaster-*`) |
| Duet3Expansion | `src/Duet3Expansion` | Expansion and tool boards (EXP3HC, EXP1HCL, TOOL1LC, …) |
| RepRapFirmware | `src/RepRapFirmware` | Standalone main boards (MB6HC, MB6XD, Mini 5+, …), as on the regression suite's `rrf` bench |
| Duet3Bootloader | `src/Duet3Bootloader` | Bootloaders and CAN IAPs (`bootloader-*`) |
| DuetIAP | `src/DuetIAP` | Firmware update IAPs (`iap-*`) |

They need their submodules, including the nested ones: `git submodule update --init --recursive`.

Each board is a CMake preset in [CMakePresets.json](CMakePresets.json), and its build tree is `build/<preset>/`. The easiest way to build is with the wrapper scripts (each takes `--help`):

```bash
scripts/boards.py                          # list the boards, by project
scripts/build.py canmaster-Duet3_MB6HC     # build one board
scripts/build.py -p d3e                    # every board in a project (canmaster, rrf, d3e, bootloader, iap)
scripts/build.py --all --debug             # every board, as Debug
scripts/size.py                            # the memory each built board uses
scripts/clean.py EXP3HC                    # clean a board's build trees (--purge deletes them)
```

Or call CMake directly. Add `-debug` to a preset name for the Debug variant:

```bash
cmake --preset canmaster-Duet3_MB6HC
cmake --build --preset canmaster-Duet3_MB6HC
```

`-DDUET_DISASSEMBLY=ON` also writes the disassembly to `build/<preset>/<target>.lst`; it is on by default for DuetCANMaster, RepRapFirmware and Duet3Expansion, with the branch targets in each function labelled unless `-DDUET_DISASSEMBLY_LABELS=OFF`. In VS Code, the CMake Tools extension reads the presets: pick a board as the configure preset, then build. The **[Firmware] Build Board** task does the same through `scripts/build.py`.

The CMake build also lints the C++ of every project and library with clang-tidy as it compiles it (`-DDUET_CLANG_TIDY=OFF` turns that off, for a faster build). The checks are in the root [.clang-tidy](.clang-tidy), which DuetCANMaster's and DuetRealtimeCore's own add their naming rules to. Third-party code is skipped: the device packs, CMSIS, tinyusb, mbedTLS, lwIP, MQTT-C and the vendored `src/libc` and `src/libcpp`. A project's findings are warnings until it is on `DUET_CLANG_TIDY_ERRORS` in [cmake/ClangTidy.cmake](cmake/ClangTidy.cmake), after which they fail the build; DuetCANMaster is on it. To lint a board's whole build by hand, or with `--fix`, write a compile database clang can use and run clang-tidy against it (the **[Style] Tidy Firmware Board** task does both). Renames from `readability-identifier-naming` must cover every board of a project at once, which `scripts/export_naming_fixes.py` does:

```bash
scripts/gen_compile_commands.py EXP3HC
run-clang-tidy -p build/EXP3HC/clang-tidy -quiet src/Duet3Expansion/
```

Every board except DuetCANMaster's also has a make build, which `Makefile.firmware` drives and the top-level Makefile forwards to (`make EXP3HC`, `make -j16 Duet3_MB6HC DEBUG=1`, `make firmware.help` for the list). Its output goes in `src/<project>/<config>/`. CI builds each board both ways and checks that the images are identical; to do the same locally:

```bash
scripts/compare_builds.py EXP3HC --build
```

`scripts/deploy.py` uploads the built firmware to a standalone (non-SBC) Duet 3 main board over HTTP and updates it and every connected CAN board that has a matching build, as on the regression suite's `rrf` bench. It does not deploy DuetCANMaster.

## Debug the Firmware

The **[Firmware] Build, Flash & Debug** launch configuration builds the Debug preset for the DuetCANMaster, RepRapFirmware or Duet3Expansion board you pick, flashes it through a GDB server, and starts debugging. **[Firmware] Debug Only** attaches without rebuilding. When either session ends, the board is reset and left running. The image is flashed from the `.bin` rather than the `.elf`, because only the `.bin` carries the CRC the bootloaders check.

You start the GDB server yourself, normally on the host outside the devcontainer, because that is where the probe is plugged in. The launch configurations ask for its address, which defaults to `host.docker.internal:2331`; the commands below all listen on port 2331. They send `monitor reset` and `monitor halt`, which J-Link, OpenOCD and pyOCD all accept. When a session ends, the **[Firmware] Restart Firmware** task reconnects, sends `monitor reset`, then `monitor go` (J-Link, pyOCD) and `monitor resume` (OpenOCD) to let the board run, and detaches; the server rejects whichever of the two it does not know.

On Linux, a connection from the container to `host.docker.internal` comes from the Docker bridge rather than from localhost, so the server must listen on all interfaces: that is what `-LocalhostOnly 0`, `bindto 0.0.0.0` and `--allow-remote` below are for. Docker Desktop on Windows and macOS delivers the connection to localhost, so those options are not needed there.

Each tool names the MCU differently:

| Board | MCU | J-Link `-device` | OpenOCD target | pyOCD `-t` |
| --- | --- | --- | --- | --- |
| canmaster-Duet3_MB6HC, canmaster-Duet3_MB6XD, Duet3_MB6HC, Duet3_MB6XD, Duet3_CAN0, Duet3_MB6HC_no_SD | SAME70Q20B | `ATSAME70Q20B` | `target/atsamv.cfg` | `atsame70q20b` |
| Duet3Mini5plus | SAME54P20A | `ATSAME54P20A` | `target/atsame5x.cfg` | `atsame54p20a` |
| EXP3HC | SAME51N19A | `ATSAME51N19A` | `target/atsame5x.cfg` | `atsame51n19a` |
| EXP1HCL, M23CL, F3PTB, TOOL1RR | SAME51G19A | `ATSAME51G19A` | `target/atsame5x.cfg` | `atsame51g19a` |
| TOOLINDX | SAME51J19A | `ATSAME51J19A` | `target/atsame5x.cfg` | `atsame51j19a` |
| EXP1XD, TOOL1LC, SAMMYC21, SZP | SAMC21G18A | `ATSAMC21G18A` | `target/at91samdXX.cfg` | `atsamc21g18a` |
| NodeTrix | STM32H523RE | `STM32H523RE` | `target/stm32h5x.cfg` | `stm32h523retx` |

A board's bootloader and IAP builds run on the same MCU as its firmware.

**SEGGER J-Link**: install the [J-Link Software and Documentation Pack](https://www.segger.com/downloads/jlink/), then run (on Windows, `JLinkGDBServerCL.exe` with the same arguments):

```bash
JLinkGDBServer -device ATSAME70Q20B -if SWD -speed 4000 -port 2331 -LocalhostOnly 0
```

**OpenOCD** supports most probes: `interface/stlink.cfg` for ST-LINK V2/V3, `interface/cmsis-dap.cfg` for Atmel-ICE, PICkit 4, MPLAB Snap, the Raspberry Pi Debug Probe and other CMSIS-DAP probes, and `interface/jlink.cfg` for J-Link. ST-LINK firmware older than V2J24 only works with STM32 targets, and `target/stm32h5x.cfg` needs OpenOCD 0.12 or newer.

```bash
openocd -f interface/cmsis-dap.cfg -c "transport select swd" -f target/atsamv.cfg \
        -c "bindto 0.0.0.0" -c "gdb_port 2331"
```

**pyOCD** needs the device's CMSIS pack, which it installs once. `pyocd list` shows the connected probes; add `-u <unique id>` to choose one when there are several.

```bash
pip install pyocd
pyocd pack install atsame70q20b
pyocd gdbserver -t atsame70q20b --port 2331 --allow-remote
```

To check the server without VS Code, connect from a terminal in the devcontainer:

```bash
arm-none-eabi-gdb build/canmaster-Duet3_MB6HC-debug/vscode/firmware.elf \
    -ex "target extended-remote host.docker.internal:2331" -ex "monitor halt" -ex "bt"
```

## Profiling with Tracy

`-p:Profiling=true` builds DuetControlServer with a Tracy zone around every method in the namespaces
listed in
[src/DuetControlServer/Profiling/ProfiledCode.cs](src/DuetControlServer/Profiling/ProfiledCode.cs).
Connecting the [Tracy](https://github.com/wolfpld/tracy) GUI to the running process then shows the
call tree of the profiled code on a timeline, with the duration, count and distribution of every
zone and the log messages that came out alongside them. Nothing in the profiled code is annotated:
the zones are woven into the compiled assembly by
[src/DuetProfiling.Fody](src/DuetProfiling.Fody), and a normal build does not compile, reference or
run any of it.

This is a development tool. It cannot be combined with `--aot`, which the build fails to say so, and
it is not something to leave switched on in a deployment.

### One-off setup

Build the Tracy client library for the architecture being profiled. The one in the NuGet package is
linked against a newer glibc than either the devcontainer or Raspberry Pi OS Bookworm has, so it is
built here instead:

```bash
scripts/build_tracy_client.py                     # this machine, for the system tests
scripts/build_tracy_client.py --arch linux-arm64  # a Pi
```

Install the Tracy GUI on the machine you will watch from. It must be the same release as the client,
v0.13.1: Tracy's protocol carries a version and the server refuses a connection from anything else.

### Profiling the system tests

```bash
dotnet test src/SystemTests/SystemTests.csproj --filter Name~PauseStopsMidMove -p:Profiling=true
```

### Profiling a Pi

```bash
scripts/build_dsf.py --all --target <pi-ip> --start-services -p "Profiling=true"
```

`build_dsf.py -p` takes the property without the `-p:` prefix, which it adds itself.

### Connecting

The client library is built on demand, so it records nothing and costs almost nothing until the GUI
connects, and DuetControlServer can be left running with it. In the Tracy GUI, connect to the
address of the machine being profiled on port 8086. Capture starts on connection and stops when you
disconnect, so a session covers whatever you do in between.

### What ends up on the timeline

Only the namespaces listed in `ProfiledCode.cs`. Add or remove entries there to change that; each
one covers the namespace or type named and everything below it. Every profiled method costs a field
read and two calls into the Tracy client per invocation, so profiling everything at once slows down
what is being measured. The build says what it did:

```
Fody/DuetProfiling: Profiling: wove 1323 methods in 4 scopes; left alone 1453 property accessors,
1056 compiler generated, 505 constructors, 15 using stackalloc
```

Those exclusions are why an expected method may be missing. Property accessors are skipped as too
small to measure; constructors and methods using `stackalloc` cannot have their bodies wrapped in
the try/finally a zone needs; compiler generated methods are the lambdas, closures, iterators and
async state machines behind the methods that are woven.

An async method's zone covers its synchronous run up to the first `await` that yields, not the
lifetime of the task it returns. Tracy requires a zone to be closed on the thread that opened it,
and a continuation is free to resume on another one. The work after an await appears as the zones of
whatever profiled methods that continuation calls.

For a profile that follows work across awaits, or of code no zone covers, use the sampling profiler
instead: `dotnet-trace collect -p <pid> --profile cpu-sampling --format Chromium` and open the result
in [Perfetto](https://ui.perfetto.dev).

### Log messages

A profiling build also registers a logging provider that puts DuetControlServer's log messages on
the timeline, marked on the thread that logged them and so among the zones that were open at the
time, coloured by level and collected in Tracy's message window. The filters that drive the console
apply to it too, so the runtime log level (`M111 P-1 S"debug"`) decides what Tracy sees as well.
Nothing is formatted while no GUI is connected.

### Notes

- The `Profiling/` sources are excluded from a normal build, so an editor that has not been told
  about the switch reports them as errors. Building with `-p:Profiling=true` resolves them.
- `TRACY_CLIENT_LIBRARY` points the process at a specific client library, overriding the one beside
  the assemblies and the one in `build/tracy/<arch>/`.

## Working with Git in the Dev Container

Git works normally inside the Dev Container.

Useful commands:

```bash
git status
git log --oneline -10
git diff
git checkout -b feature/my-change
git add .
git commit -m "Describe your change"
git push
```

If authentication fails when pushing:

- Check your remote URL with git remote -v.
- Use HTTPS with a token, or configure SSH keys on your host and ensure forwarding works in your setup.

### Submodules

The firmware projects and libraries in `src/` and `lib/` are submodules, each tracking the branch set in [.gitmodules](.gitmodules); this repository only records which commit of each to use.

- Change submodule code in its own repository first: branch, commit and open a PR there.
- Submodules check out at a detached `HEAD`, where commits are easily lost. Before editing one, switch it to its tracked branch with the **[Git] Checkout Submodule Branches** task, then make a feature branch.
- Push submodule commits **before** pushing the commit here that points at them, otherwise nobody else can check it out. `git config push.recurseSubmodules check` (below) enforces this.
- Before merging, point the submodule at a commit on its tracked branch, not at an unmerged feature branch. CI enforces this on PRs; check it locally with the **[Git] Check Submodules** task (`scripts/check_submodules.py`).
- After pulling, run `git submodule update --init --recursive`, or set `submodule.recurse true`.
- The **[Git] Update Submodules** task moves every submodule to the tip of its tracked branch (`git submodule update --remote --merge`), merging into local work rather than discarding it. Commit the new pointers.

These settings make submodules easier to work with. Run them on the host to keep them across container rebuilds and for other projects, or leave out `--global` to set them for this repository only:

```bash
# Refuse to push if a submodule commit referenced here has not been pushed to its remote
git config --global push.recurseSubmodules check
# Make checkout, switch, pull and reset also update submodules to match
git config --global submodule.recurse true
# Fetch submodule commits when a fetched commit references new ones
git config --global fetch.recurseSubmodules on-demand
# Show a summary of submodule changes in git status, and their commits in git diff
git config --global status.submoduleSummary true
git config --global diff.submodule log
```

## Troubleshooting

### Docker is not running

If VS Code cannot open the container, make sure Docker is running on your host.

Quick check:

```bash
docker ps
```

### VS Code does not show Reopen in Container

- Confirm Dev Containers extension is installed.
- Confirm you opened the repository root (where .devcontainer/ exists).

### Container build is slow on first run

This is normal. The first build downloads base images and installs packages.

### dotnet restore or build fails in container

Try rebuilding the container:

1. Open Command Palette.
2. Run Dev Containers: Rebuild Container.

Then retry:

```bash
cd src
dotnet restore DuetSoftwareFramework.sln
dotnet build DuetSoftwareFramework.sln
```

### Deployment task fails with SSH or rsync errors

- Confirm target IP is correct and reachable.
- Confirm SSH login works from the container terminal.
- Confirm the target has DSF service names expected by the task.
- Confirm you have permission to write /opt/dsf/bin/ on the target.

Manual connectivity check:

```bash
ssh root@<target-ip> hostname
```

### build/ output looks stale

Run the clean task first:

1. Tasks: Run Task
2. Clean Build Directory
3. Build All

