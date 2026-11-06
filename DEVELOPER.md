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

After the container finishes starting, open a new terminal in VS Code and check that everything the builds need is there:

```bash
scripts/doctor.py tools
```

Then build every DSF project:

```bash
scripts/build.py dsf
```

The scripts in [scripts/](scripts/) are how everything is built, deployed, tested and linted, one script per task:

| Script | Does |
|---|---|
| `scripts/list.py` | Lists the targets the others take |
| `scripts/build.py` | Builds DSF projects and board firmware |
| `scripts/deploy.py` | Deploys (and optionally builds) DSF projects and firmware |
| `scripts/migrate.py` | Moves a Pi and its main board between DSF 3 with RepRapFirmware and DSF 4 with DuetCANMaster |
| `scripts/test.py` | Runs the unit, system or native tests |
| `scripts/lint.py` | Runs clang-tidy over a whole build |
| `scripts/clean.py` | Cleans or deletes build output |
| `scripts/size.py` | Shows the memory the built firmware uses |
| `scripts/codegen.py` | Generates, or checks, the CAN message formats |
| `scripts/doctor.py` | Checks the tools, and that the submodules are checked out and on their branches |
| `scripts/flash_bootloader.py` | Flashes a bootloader to an expansion or tool board over SWD |
| `scripts/remote_control.py` | Runs codes, queries the object model and handles files on a board over HTTP, standalone or with an SBC |
| `scripts/submodule.py` | Checks the submodules are on their tracked branches, and updates, checks out or attaches them |

Each takes `--help`. They take the same targets: a DSF project (`DuetControlServer`, `DuetRealtimeCore`, ...), a firmware board named by its CMake preset (`Duet3Firmware_EXP3HC`, `DuetCANMaster_MB6HC`, with a `-debug` suffix for its Debug build), or a group (`dsf`, `firmware`, or a firmware project in full or as `canmaster`, `rrf`, `d3e`, `bootloader` or `iap`). `-x` leaves a target or group out, and builds are Release unless `--debug` is given.

With [argcomplete](https://github.com/kislyuk/argcomplete) installed, the scripts tab-complete their options and the target names. Source the file for your shell from its startup file: `scripts/completions/dsf.fish` from `config.fish`, `scripts/completions/dsf.zsh` from `.zshrc` after `compinit`, or `scripts/completions/dsf.bash` from `.bashrc`. The devcontainer does this for all three.

## Build Using VS Code Tasks

This repository includes predefined tasks in [.vscode/tasks.json](.vscode/tasks.json).

To run a task:

1. Press Ctrl+Shift+P.
2. Run Tasks: Run Task.
3. Select a task.

Common tasks, which run the scripts above:

- [DSF] Build, for one DSF project or all of them (`dsf`)
- [DSF] Build and Deploy To, and Build and Deploy DCS to, for the Pi in the `dsf.debug` settings
- [DSF] Restart DSF Services
- [Firmware] Build Board

The DSF tasks build Debug, so that the debugger can be attached; each project's binaries are in its own `bin/Debug/<tfm>/linux-arm64/publish`.

## Run the Tests

The unit tests run under coverage and print a per-assembly table:

```bash
scripts/test.py unit
```

The system tests start a real DuetControlServer against a scripted CAN controller, so they take
minutes rather than seconds:

```bash
scripts/test.py system
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
scripts/test.py system --filter 'FullyQualifiedName~JobControl'
scripts/test.py system --help
```

DuetRealtimeCore's own tests build it for this machine and run its ctest suite:

```bash
scripts/test.py native
```

The scripts in `scripts/` have tests of their own in `tests/scripts`, which drive each script's command line with the builds, git and the boards faked, so they need no toolchain, build or hardware. After changing the scripts, run them and pyright, as CI does:

```bash
scripts/test.py scripts
pyright
```

Running the system test project directly works, but the progress needs the console logger asked for by name,
because at its default verbosity it prints nothing until the run is over:

```bash
dotnet test src/SystemTests/SystemTests.csproj -tl:off \
    --logger 'console;verbosity=normal' --filter 'TestCategory!=KnownGap'
```

`-tl:off` turns off MSBuild's terminal logger, which otherwise echoes the test output on top of that
logger and prints every line twice. It turns itself on only when the output is a terminal, so
without it the same command behaves differently piped and interactively.

## Deploy to a Remote DuetPi/SBC

`scripts/deploy.py` deploys DSF projects to a Pi over SSH and rsync, building them first with `--build`, and the [DSF] Build and Deploy tasks run it for the Pi in the `dsf.debug` settings ([DSF] Set Debug Target IP sets it):

```bash
scripts/deploy.py -H <pi> dsf --start-services            # every DSF project, as last built
scripts/deploy.py -H <pi> dsf --build --start-services    # build them first
scripts/deploy.py -H <pi> DuetControlServer --build --debug  # build one as Debug, to debug it
```

What it does:

1. With --build, builds the projects locally, each into its own `bin/<config>/<tfm>/<arch>/publish` (`bin/aot/...` with --aot); --clean builds them from clean.
2. Stops DSF services on the target.
3. Copies the projects' publish directories, and `libduet_realtime_core.so` from its CMake build, to /opt/dsf/bin/ on the target in one rsync. Which build is deployed follows --debug, --aot and --arch, with or without --build.
4. Copies DuetPiManagementPlugin to /opt/dsf/plugins/ on the target, manifest included.
5. Starts DSF services again.

Notes:

- The remote commands use the `root` account by default; `-u` names another.
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

`scripts/remote_control.py` drives a board over HTTP:

```bash
scripts/remote_control.py -H <pi> info                  # board, firmware and connection summary
scripts/remote_control.py -H <pi> code M122             # run a code and print its reply
scripts/remote_control.py -H <pi> model boards          # query the object model
scripts/remote_control.py -H <pi> put test.gcode        # upload a file to /gcodes
```

## Build the Firmware

The firmware projects build from the repository root with one toolchain, against the shared libraries in `lib/`:

| Project | Path | Builds |
| --- | --- | --- |
| DuetCANMaster | `src/DuetCANMaster` | The SPI-to-CAN-FD bridge on the main board an SBC drives (`DuetCANMaster_*`) |
| Duet3Expansion | `src/Duet3Expansion` | Expansion and tool boards (EXP3HC, EXP1HCL, TOOL1LC, …) |
| RepRapFirmware | `src/RepRapFirmware` | Standalone main boards (MB6HC, MB6XD, Mini 5+, …), as on the regression suite's `rrf` bench |
| Duet3Bootloader | `src/Duet3Bootloader` | Bootloaders and CAN IAPs (`Duet3Bootloader-*`, `Duet3_CANiap32_*`) |
| DuetIAP | `src/DuetIAP` | Firmware update IAPs (`Duet3_SDiap32_*`, `Duet3_SBCiap32_*`) |

They need their submodules, including the nested ones: `git submodule update --init --recursive`.

Each board is a CMake preset in [CMakePresets.json](CMakePresets.json), and its build tree is `build/<preset>/`. The easiest way to build is with the scripts:

```bash
scripts/list.py firmware               # list the boards, by project
scripts/build.py DuetCANMaster_MB6HC   # build one board
scripts/build.py d3e                   # every board of a project (canmaster, rrf, d3e, bootloader, iap)
scripts/build.py firmware --debug      # every board, as Debug
scripts/size.py                        # the memory each built board uses
scripts/clean.py Duet3Firmware_EXP3HC  # clean a board's build trees (--purge deletes them)
```

Or call CMake directly. Add `-debug` to a preset name for the Debug variant:

```bash
cmake --preset DuetCANMaster_MB6HC
cmake --build --preset DuetCANMaster_MB6HC
```

A firmware preset is named after the image it builds, so `Duet3Firmware_EXP3HC` builds `build/Duet3Firmware_EXP3HC/Duet3Firmware_EXP3HC.bin`, and configuring fails if the two differ. That keeps every image's file name unique, and the scripts find the preset for a file a board asks for by its name.

`-DDUET_DISASSEMBLY=ON` also writes the disassembly to `build/<preset>/<target>.lst`; it is on by default for DuetCANMaster, RepRapFirmware and Duet3Expansion, with the branch targets in each function labelled unless `-DDUET_DISASSEMBLY_LABELS=OFF`. In VS Code, the CMake Tools extension reads the presets: pick a board as the configure preset, then build. The **[Firmware] Build Board** task does the same through `scripts/build.py`.

The CMake build also lints DuetCANMaster's and the libraries' C++ with clang-tidy as it compiles it (`-DDUET_CLANG_TIDY=OFF` turns that off, for a faster build). The checks are in the root [.clang-tidy](.clang-tidy), which DuetCANMaster's and DuetRealtimeCore's own add their naming rules to. RepRapFirmware, Duet3Expansion, Duet3Bootloader and DuetIAP are not linted: their presets in [CMakePresets.json](CMakePresets.json) set `DUET_CLANG_TIDY` off, and `-DDUET_CLANG_TIDY=ON` lints them too. Third-party code is never linted: the device packs, CMSIS, tinyusb, mbedTLS, lwIP, MQTT-C and the vendored `src/libc` and `src/libcpp`. Every finding is an error, which fails the build. The libraries do not pass every check yet: [lib/.clang-tidy](lib/.clang-tidy) turns off the ones they fail, with how many findings each had, and the wrapper ([scripts/internal/clang_tidy_wrapper.py](scripts/internal/clang_tidy_wrapper.py)) skips the few library files clang cannot parse. Both lists are to shrink as the libraries are fixed. `scripts/lint.py` lints a board's whole build by hand, every header included, and can apply fixes (the **[Style] Tidy Firmware Board** task runs it). Renames from `readability-identifier-naming` must cover every board of a project at once, which `--naming-fixes` does:

```bash
scripts/lint.py Duet3Firmware_EXP3HC  # every project and library EXP3HC builds
scripts/lint.py Duet3Firmware_EXP3HC -f src/Duet3Expansion/src/Movement/Move.cpp
scripts/lint.py canmaster --naming-fixes               # export renames across every DuetCANMaster board
```

`scripts/deploy.py` also deploys the firmware of a main board and the CAN boards connected to it, building it first with `--build`. It uploads the files over the REST API, then updates the boards with M997.

```bash
scripts/deploy.py -H duet3.local firmware                       # every connected board
scripts/deploy.py -H duet3.local firmware --build               # build what they need first
scripts/deploy.py -H duet3.local Duet3Firmware_EXP1HCL --debug  # only the 1HCL boards, as Debug
scripts/deploy.py -H duet3.local firmware --dry-run             # show what it would do
scripts/deploy.py -H <pi> dsf firmware --build                  # the firmware through the running DSF, then the new DSF, to a Pi
```

### Moving between DSF 3 and DSF 4

DSF 4 only talks to DuetCANMaster on board 0 and DSF 3 only to RepRapFirmware, so the migration order is important. The mainboard needs to be updated first, and once its firmware has changed, DSF cannot reach it to update it. `scripts/migrate.py` does the two in that order, having the stack that is leaving flash the main board with `M997 P"<file>"`.

```bash
scripts/migrate.py upgrade -H <pi> --build      # build DSF 4 and DuetCANMaster, flash through DSF 3, deploy DSF 4
scripts/migrate.py downgrade -H <pi>            # flash RepRapFirmware through DSF 4, reinstall the DSF 3 packages
scripts/migrate.py <up|down>grade -H <pi> --dry-run  # check the machine and show what would be done
scripts/migrate.py <up|down>grade -H <pi> --expansion-boards  # flash the expansion boards too, before the main board
```

Before flashing it checks that what it moves to has been built (upgrade) or can be downloaded from the package repository (downgrade). Afterwards the machine has to report the new stack, connected to the main board.

A downgrade goes back to the DSF packages dpkg has installed, which `scripts/deploy.py`, and takes the firmware from the matching `reprapfirmware` package rather than from the firmware directory. Files in `/opt/dsf/bin` that the packages do not own are deleted.

A downgrade puts all the firmware packaged in `reprapfirmware` in the firmware directory.

If it stops after the flash, DSF cannot reach the main board, so finish with the same command and `--dsf-only`. Expansion boards keep their firmware either way; update them with `scripts/deploy.py`.

### Bootloaders

The expansion and tool boards' bootloaders can be deployed two ways.

**Over CAN**, for a board that already has a working bootloader and is running Release firmware. `--bootloader` uploads the bootloader each expansion board asks for and updates it with `M997 B<address> S3`. The boards' firmware and the main board are left alone:

```bash
scripts/deploy.py -H duet3.local firmware --bootloader              # every expansion board
scripts/deploy.py -H duet3.local Duet3Firmware_EXP3HC --bootloader  # only the 3HCs
scripts/deploy.py -H duet3.local firmware --bootloader --build      # build the bootloaders they need first
```

The STM32 boards (NodeTrix) cannot update their bootloader over CAN yet. A main board running as an expansion board has no bootloader. Its firmware is updated by its CAN IAP (`Duet3_CANiap32_<board>.bin`, built by the `Duet3_CANiap32_*` presets), which it loads from its own SD card, so copy that file to its `/firmware` directory yourself.

**Over SWD**, for a new or broken board, or an STM32 one. `scripts/flash_bootloader.py` flashes a bootloader through OpenOCD's GDB server (see [Debug the Firmware](#debug-the-firmware)). It checks that the board has the right MCU, has OpenOCD clear the BOOTPROT protection the firmware puts on the bootloader, writes the bootloader with GDB, reads it back to check it, and restarts the board. GDB prints each command as it runs it:

```bash
scripts/flash_bootloader.py Duet3Bootloader-SAME5x --build                       # EXP3HC, EXP1HCL, TOOL1RR, M23CL, …
scripts/flash_bootloader.py Duet3Bootloader-SAMC21 -s host.docker.internal:3333  # TOOL1LC, EXP1XD, SZP, on another port
```

A board with a bootloader but no firmware fetches its firmware from the main board over CAN when it starts, so `scripts/deploy.py` can then update it as usual. The **[Firmware] Deploy Bootloaders (CAN)** and **[Firmware] Flash Bootloader (SWD)** tasks run these.

## Debug the Firmware

The **[Firmware] Build, Flash & Debug** launch configuration builds the Debug preset for the DuetCANMaster, RepRapFirmware or Duet3Expansion board you pick, flashes it through a GDB server, and starts debugging, through the [Cortex-Debug](https://marketplace.visualstudio.com/items?itemName=marus25.cortex-debug) extension. **[Firmware] Debug Only** attaches without rebuilding, and **[Firmware] Flash & Debug Bootloader** flashes the Debug build of a bootloader with `scripts/flash_bootloader.py` and debugs it. The image is flashed from the `.bin` rather than the `.elf`, because only the `.bin` carries the CRC the bootloaders check.

You start the GDB server yourself, normally on the host outside the devcontainer, because that is where the probe is plugged in. The launch configurations ask for its address, which defaults to `host.docker.internal:2331`, and which server it is: `jlink`, `openocd` or `pyocd`. The commands below all listen on port 2331, so the default works for each of them. The launch configurations stop the board at its reset vector, before the bootloader runs, so that breakpoints in the startup code are hit. Each server resets differently, so they run the commands in that server's script in [.vscode/gdb](.vscode/gdb). When a session ends, the **[Firmware] Restart Firmware** task resets the board with the same script and leaves it running.

The **XPERIPHERALS** view shows the MCU's registers, from the SVD file that configuring a board links as `vscode/device.svd` in its build tree. CoreN2G has no SVD file for the SAME70, so the view is empty for the main boards.

On Linux, a connection from the container to `host.docker.internal` comes from the Docker bridge rather than from localhost, so the server must listen on all interfaces: that is what `-LocalhostOnly 0`, `bindto 0.0.0.0` and `--allow-remote` below are for. Docker Desktop on Windows and macOS delivers the connection to localhost, so those options are not needed there.

Each tool names the MCU differently:

| Board | MCU | J-Link `-device` | OpenOCD target | pyOCD `-t` |
| --- | --- | --- | --- | --- |
| MB6HC, MB6XD | SAME70Q20B | `ATSAME70Q20B` | `target/atsamv.cfg`* | `atsame70q20b`* |
| Duet3Mini5plus | SAME54P20A | `ATSAME54P20A`* | `target/atsame5x.cfg`* | `atsame54p20a`* |
| EXP3HC | SAME51N19A | `ATSAME51N19A` | `target/atsame5x.cfg` | `atsame51n19a`* |
| EXP1HCL, M23CL, F3PTB, TOOL1RR | SAME51G19A | `ATSAME51G19A` | `target/atsame5x.cfg` | `atsame51g19a`* |
| TOOLINDX | SAME51J19A | `ATSAME51J19A`* | `target/atsame5x.cfg`* | `atsame51j19a`* |
| EXP1XD, TOOL1LC, SAMMYC21, SZP | SAMC21G18A | `ATSAMC21G18A`* | `target/at91samdXX.cfg`* | `atsamc21g18a`* |
| NodeTrix | STM32H523RE | `STM32H523RE`* | `target/stm32h5x.cfg`* | `stm32h523retx`* |

_\* These boards have not been tested yet with that tool._

A board's bootloader and IAP builds run on the same MCU as its firmware. `scripts/flash_bootloader.py` and **[Firmware] Flash & Debug Bootloader** only work through OpenOCD, which clears the BOOTPROT protection the firmware puts on the bootloader, so use OpenOCD for them whatever the probe, a J-Link included.

### OpenOCD (recommended)

OpenOCD supports most probes. Pick the interface file for the probe and the target file from the table above:

| Probe | Interface file |
| --- | --- |
| ST-LINK V2/V3 | `interface/stlink-dap.cfg` |
| Atmel-ICE, Microchip PICkit 4, MPLAB Snap | `interface/cmsis-dap.cfg` |
| Raspberry Pi Debug Probe, DAPLink, other CMSIS-DAP | `interface/cmsis-dap.cfg` |
| J-Link | `interface/jlink.cfg` |

```bash
openocd -f <interface_file> -c "transport select swd" -f <target_file> -c "bindto 0.0.0.0" -c "gdb_port 2331"
```

For example, an Atmel-ICE debugging an EXP1HCL:

```bash
openocd -f interface/cmsis-dap.cfg -c "transport select swd" -f target/atsame5x.cfg \
        -c "bindto 0.0.0.0" -c "gdb_port 2331"
```

`interface/stlink-dap.cfg` drives the ST-LINK as a plain debug probe, which needs ST-LINK firmware V2J24 or newer; update older firmware with STM32CubeProgrammer. `interface/stlink.cfg` selects the ST-LINK's older high-level mode instead, which fails on `target/stm32h5x.cfg` with "The selected transport doesn't support this target". `target/stm32h5x.cfg` needs OpenOCD 0.12 or newer. Depending on the OpenOCD version, `gdb port` may be needed instead of `gdb_port`.

If OpenOCD stops at `Error connecting DP: cannot read IDR` although the probe sees the board, the code running on it may block a normal connection, for example by driving LEDs from the SWD pins (see below). Connect while holding the board in reset instead, which needs the probe's reset line connected to the board. OpenOCD releases the reset once it is ready, so the code runs again before GDB connects; make OpenOCD halt the board at its reset vector when GDB connects, or it rejects the connection. Add this after the target file:

```bash
-c "reset_config srst_only srst_nogate connect_assert_srst" -c "[target current] configure -event gdb-attach { reset halt }"
```

The `target/stm32h5x.cfg` in STMicroelectronics' OpenOCD (tested with 0.12.0+dev-00664) reads registers with `mem2array`, which that OpenOCD rejects on the target it uses, so its setup after connecting fails with `Error executing event examine-end`. That setup keeps the debugger working while the MCU is in a low-power mode and stops the watchdogs while it is halted. Add this after the target file to replace the failing command:

```bash
-c 'proc stm32h5x_mrw {used_target reg} { return [$used_target read_memory $reg 32 1] }'
```

### Boards with LEDs on the SWD pins

Most Duet 3 boards drive their status LEDs from the SWD pins, and a Release build of the firmware takes those pins over as soon as it runs, which locks a debugger out:

- Main boards: Duet3_MB6HC, Duet3_MB6XD, Duet3Mini5plus
- Expansion and tool boards: EXP1HCL, F3PTB, TOOL1RR, TOOLINDX, SZP, NodeTrix, and EXP3HC from v1.03 and TOOL1LC from v1.1 unless a debugger is attached when they start

Debug builds leave the SWD pins alone. So **[Firmware] Build, Flash & Debug** and **[Firmware] Flash & Debug Bootloader** need the connect under reset above whenever the board may be running a Release build, and **[Firmware] Debug Only** reaches a running Debug build without it.

Expansion and tool boards start their bootloader after every reset, before the firmware. A current bootloader leaves the SWD pins alone in a Debug build or, on the SAME5x and SAMC21, when a debugger is attached as the board starts, and on the STM32H5 it gives them back before starting the firmware. Older bootloaders keep the LEDs on the SWD pins, so the firmware cannot be debugged behind them: update the bootloader first (see [Bootloaders](#bootloaders)). Over CAN, which a Debug build of the firmware ignores, the board must be running a Release build. A Release bootloader on the NodeTrix still drives its LEDs until it starts the firmware, so the debugger loses the board for that moment after each reset.

### SEGGER J-Link

Install the [J-Link Software and Documentation Pack](https://www.segger.com/downloads/jlink/), then run (on Windows, `JLinkGDBServerCL.exe` with the same arguments, or the J-Link GDB Server GUI):

```bash
JLinkGDBServer -device ATSAME70Q20B -if SWD -speed 4000 -port 2331 -LocalhostOnly 0
```

### pyOCD

pyOCD is untested here. It needs the device's CMSIS pack, which it installs once. `pyocd list` shows the connected probes; add `-u <unique id>` to choose one when there are several.

```bash
pip install pyocd
pyocd pack install atsame70q20b
pyocd gdbserver -t atsame70q20b --port 2331 --allow-remote
```

### Checking the connection

To check the server without VS Code, connect from a terminal in the devcontainer:

```bash
arm-none-eabi-gdb build/DuetCANMaster_MB6HC-debug/vscode/firmware.elf \
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
scripts/build.py TracyClient                      # this machine, for the system tests
scripts/build.py TracyClient --arch linux-arm64   # a Pi
```

Install the Tracy GUI on the machine you will watch from. It must be the same release as the client,
v0.13.1: Tracy's protocol carries a version and the server refuses a connection from anything else.

### Profiling the system tests

```bash
dotnet test src/SystemTests/SystemTests.csproj --filter Name~PauseStopsMidMove -p:Profiling=true
```

### Profiling a Pi

```bash
scripts/deploy.py -H <pi-ip> dsf --build --start-services -p "Profiling=true"
```

`-p` takes the property without the `-p:` prefix, which the script adds itself.

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
- Before merging, point the submodule at a commit on its tracked branch, not at an unmerged feature branch. CI enforces this on PRs; check it locally with the **[Git] Check Submodules** task (`scripts/submodule.py check`, which `scripts/doctor.py submodules` also runs).
- After pulling, run `git submodule update --init --recursive`, or set `submodule.recurse true`.
- The **[Git] Update Submodules** task moves every submodule to the tip of its tracked branch (`scripts/submodule.py update`), merging into local work rather than discarding it. Commit the new pointers.
- **[Git] Attach Submodules to Branches** (`scripts/submodule.py attach`) puts each submodule on a branch ending at the commit it has checked out, without changing its files, and **[Git] Pull Submodules to Branch** (`attach --branch <name> --pull`) switches those that have the branch to it and fast-forwards it from origin.

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

Delete the build output and build again:

```bash
scripts/clean.py dsf --purge
scripts/build.py dsf
```

