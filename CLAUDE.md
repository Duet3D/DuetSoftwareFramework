# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this tree is

Duet Software Framework (DSF), a .NET 10 (C# 14) service stack for a Linux SBC, plus the native and firmware projects it now ships with. The tree is partway through a migration away from RepRapFirmware (RRF):

- **DSF runs on a mandatory SBC** and takes over everything RRF did: G-code interpretation, motion planning, heat, tools, events, files, networking. The work is porting RRF's subsystems into `src/DuetControlServer`, one at a time.
- **`src/DuetRealtimeCore`** is a C++ library that runs on the SBC. It manages any computation that needs a low latency or jitter.
- **`src/DuetCANMaster`** is RRF-derived firmware stripped down to an SPI-to-CAN-FD bridge and master step timer. Its silence about a feature is not evidence that RRF lacks it.
- **`src/Duet3Expansion`** is the firmware for the CAN expansion and tool boards, which do the real-time control.

Place new functionality accordingly: host-side concerns in DSF, motion and real-time control in Duet3Expansion, only SPI/CAN bridging and step timing in DuetCANMaster.

`src/RepRapFirmware` is upstream RRF, a submodule on its `3.7-dev` branch that is built (for the standalone main boards of the regression bench) but is **read-only here**: it is the reference for behaviour, not part of DSF. Read `src/RepRapFirmware/src` to see how RRF implements something being ported, and grep its call sites before claiming DSF diverges from RRF. Never edit it. `src/Duet3Bootloader` and `src/DuetIAP` are upstream submodules built alongside the firmware. Everything under `lib/` (CANlib, CoreN2G, DuetSpiInterface, FreeRTOS, RRFLibraries, ...), DuetCANMaster, Duet3Expansion and DuetRealtimeCore are owned by this project and should be fixed at the source rather than worked around. Each submodule tracks the branch `.gitmodules` names for it, which `scripts/submodule.py check` (also run by `scripts/doctor.py submodules`, and by CI on PRs) checks.

## Build

The user-facing scripts are in `scripts/`, one per task: `list.py`, `build.py`, `deploy.py`, `test.py`, `lint.py`, `clean.py`, `size.py`, `codegen.py` and `doctor.py`, plus `migrate.py`, `submodule.py`, `remote_control.py` and `flash_bootloader.py`. Each takes `--help`. Their shared code is in `scripts/common/`, and tools that CMake, CI or the devcontainer run, not people, are in `scripts/internal/`. Those that act on builds take the same targets: a DSF project (`DuetControlServer`, `DuetRealtimeCore`, ...), a firmware board, named by its CMake preset (`Duet3Firmware_EXP3HC`, `DuetCANMaster_MB6HC`; a `-debug` suffix names its Debug build), or a group: `dsf`, `firmware`, or a firmware project in full or as `canmaster`, `rrf`, `d3e`, `bootloader` or `iap`. `scripts/list.py` lists them. Builds are Release unless `--debug` is given.

```bash
scripts/build.py dsf                                            # every DSF project, each into its own bin/<config>/<tfm>/<arch>/publish
scripts/build.py DuetCANMaster_MB6HC                            # one board, into build/<preset>/
scripts/build.py canmaster d3e                                  # every DuetCANMaster and Duet3Expansion board
scripts/deploy.py -H <pi> dsf --build --debug --start-services  # build every DSF project and deploy it to a Pi
scripts/deploy.py -H <pi> canmaster --build                     # build and flash DuetCANMaster to board 0
scripts/deploy.py -H <pi> d3e --build                           # build and flash Duet3Expansion to all expansion boards on the bus
```

Prefer `scripts/build.py` over `dotnet build` on a single project: it cross-compiles `libduet_realtime_core.so` for aarch64, which DuetControlServer P/Invokes into and cannot run without. `deploy.py --local` fails in the devcontainer.

A fresh clone or new git worktree needs the native submodule before the native build or system tests will work, and the rest of the submodules for the firmware; `scripts/doctor.py` checks for both:

```bash
git submodule update --init lib/RRFLibraries
git submodule update --init --recursive
```

Every firmware board is a preset in the root `CMakePresets.json`. A firmware preset is named after the image it builds (`Duet3Firmware_EXP3HC`, `DuetCANMaster_MB6HC`, `Duet3Bootloader-SAME5x`, `Duet3_SBCiap32_MB6HC`), with `-debug` added for its Debug build. Configuring fails if the two differ, so each image's file name is unique and is how the scripts find its preset. DuetRealtimeCore's presets are in the same file, prefixed `DuetRealtimeCore-` (`DuetRealtimeCore-native`, `DuetRealtimeCore-arm64`, ...), and build into `build/<preset>/` too.

The CMake build lints DuetCANMaster's and the libraries' C++ with clang-tidy as it compiles it, using the root `.clang-tidy` plus a project's own where it has one (DuetCANMaster's and DuetRealtimeCore's add their naming rules). RepRapFirmware, Duet3Expansion, Duet3Bootloader and DuetIAP are not currently linted: their presets set `DUET_CLANG_TIDY` off, and third-party code (device packs, CMSIS, tinyusb, mbedTLS, lwIP, `src/libc`, ...) never is. Every finding is an error that fails the build.. The libraries are held to fewer checks for now: `lib/.clang-tidy` turns off those they still fail, and `SKIP` in `scripts/internal/clang_tidy_wrapper.py` lists the library files clang cannot parse. Take a check or a file off those lists once the libraries pass it; never add to them to get a change through. `scripts/lint.py <board>` lints a whole build by hand.

### CAN message formats are generated

`src/DuetCanMessage.SourceGenerators/Schema/can-messages.json` is the single source for the CAN wire formats, generating both the C++ headers dropped into CANlib and the C# types under `src/DuetControlServer/Link/Protocol/CanMessages/Generated`. Edit the schema, never the generated files, then:

```bash
scripts/codegen.py            # regenerate (output is checked in)
scripts/codegen.py --check    # CI: fails if checked-in output is stale
scripts/codegen.py --verify   # compile-time layout check against CANlib's header
```

`CANlib` messages are currently hand maintained for `RepRapFirmware`, ultimately the source generated files will replace these but for now the C++ generated files are just used for verification.

The SPI wire protocol shared by DuetRealtimeCore and DuetCANMaster lives in `lib/DuetSpiInterface` and is `static_assert`-checked against the C# structs in `src/DuetControlServer/Link/Protocol`.

## Tests

| Suite | Command | Notes |
|---|---|---|
| Unit (NUnit) | `scripts/test.py unit` | Run in full; with coverage, as CI does |
| System (NUnit) | `scripts/test.py system --filter 'FullyQualifiedName~JobControl'` | Always filter to the affected fixtures; the full suite takes many minutes |
| Native | `scripts/test.py native` | DuetRealtimeCore's ctest suite, in a host build used only for tests |
| Scripts | `scripts/test.py scripts` | The Python scripts' tests (`tests/scripts`), with builds, git and boards faked; run with `pyright` after changing `scripts/` |

Single unit test: `dotnet test src/UnitTests/UnitTests.csproj --filter "FullyQualifiedName~ClassName.MethodName"`.

System tests host the real DuetControlServer in-process (`src/SystemTests/Host/DcsTestHost.cs`) with the real `libduet_realtime_core.so`, talking over a socket transport to a scripted controller (`src/SystemTests/ScriptedCanMaster`) that records the link traffic. Scenarios under `src/SystemTests/Scenarios` drive G-code and assert on the object model and the captured packets. Use `scripts/test.py system` rather than raw `dotnet test` on that project: it reports progress per test and splits failures into known and new.

- A code's scenarios go in `Scenarios/Codes/<Subsystem>/<Code>Tests.cs`, one fixture per code or family of codes documented together (`M140M141Tests`), tagged `[GCode("M140", "M141")]`; what is not one code's goes in `Scenarios/Behaviour/<Area>`. `scripts/test.py system --code M140` runs every scenario tagged with a code.
- A scenario starts from `Bench.StartAsync()`, which enables CAN and configures nothing else, and adds only the parts it needs (`Machines.Xye`, `DriversRig.Config`, `JobMacros.Instrumented`, ... in `src/SystemTests/Host/Config`) or its own lines. Do not give a scenario a machine, macros or homing it does not use. The bench clears the controller's capture once `config.g` has finished, so a scenario does not call `ClearCapture()` after starting one; `KeepingConfigTraffic()` keeps the start-up traffic.
- Interpolate boards, drivers, ports and the values tests assert into config strings rather than writing them as literals: `Expansion.Driver(Machines.DriverBoard, Machines.XDriver)`, `Expansion.Out(Machines.DriverBoard, 3)`, `DriversRig.MainDriver(0)`, `Machines.XyStepsPerMm`, so the config and the assertions cannot drift apart. A `[TestCase]` argument cannot be interpolated, so a case naming a driver or port goes in a `[TestCaseSource]` array.

- `[Category("KnownGap")]` marks a scenario asserting behaviour the source does not produce yet. These are excluded by default; the category comes off when the source is fixed. `--tag-known-gaps` rewrites the categories to match a run.
- `[Category("LongRunning")]` marks the slowest; `--skip-long-running` drops them.

Write the unit and system tests for a feature before implementing it where possible. This is the general approach rather than a hard rule; some features cannot be specified usefully until part of the implementation exists. A feature's system scenarios should cover every input edge case, every object model change, the outgoing packets validated field by field, and every response an expansion board can send back, including failures and refusals. For a port from RRF, the scenarios capture RRF's behaviour so they, not the implementation, pin the port.

### Regression suite (real hardware)

DuetRegressionTesting (https://github.com/Duet3D/DuetRegressionTesting) is a separate project that captures the effect of running real G-code on real hardware. For each test case it sends the G-code to a bench and records the reply text and severity, every CAN-FD frame the code produced (decoded and timestamped), and the object model before and after. That capture is stored as the case's **reference**. A later run on another firmware version or another stack is compared against the reference, and any difference is reported.

There is one reference per test, typically recorded against RepRapFirmware on a standalone MB6HC. Runs against DSF are compared with that same reference, so the references are the recorded RRF behaviour that DSF has to reproduce. When porting a code, read the references of the cases that cover it alongside the RRF source.

#### Reaching it

The harness runs on the host machine, not in the devcontainer, because the CAN adapter is a host USB device. It is not checked out in this repository; use its HTTP API at `http://host.docker.internal:8765`. If the server does not answer, the user has not started it (`drt web` on the host); ask rather than trying to start it.

It has two profiles, which are the two stacks on the same bench and CAN bus:

- `rrf`: the MB6HC running RepRapFirmware standalone. The references are recorded from this profile.
- `dsf`: the test Pi running this repository's build, driving DuetCANMaster. A run on this profile tests whatever is currently deployed, so deploy first (`scripts/deploy.py -H <ip> dsf --build --start-services`, see `DEVELOPER.md`).

#### Reading results (no hardware needed)

- `GET /api/testcases?profile=dsf` lists every case: `id`, `folder`, `description`, `gcode_preview`, `boards`, `has_reference`, `known_bad`, and `last_run` on that profile (`run_id`, `verdict`, finding counts by severity). This is the starting point for finding what DSF gets wrong.
- `GET /api/testcases/<id>` returns one case in full, including its setup, G-code and teardown.
- `GET /api/references/<id>` returns the recorded reference: replies, CAN frames and object model change for each repeat.
- `GET /api/history/<id>` lists past runs of a case on every profile, each compared against the reference as it stands now.
- `GET /api/runs/<run_id>/detail` gives per-code reply diffs, the reference and observed CAN traces aligned frame by frame, and the object model delta. This shows exactly how DSF deviates, without touching the rig.

Findings are graded `regression`, `warning` and `info`. Only regressions fail a case.

#### Running cases

1. `GET /api/activity` and `GET /api/job` first. Only one job can hold the rig, and a run started from a terminal on the host also counts (`rig_busy`).
2. `POST /api/job` with `{"mode":"run","profile":"dsf","tests":["<id>", ...]}`. Every named case needs a reference.
3. Poll `GET /api/job` until `state` is `finished`, `cancelled` or `failed` (it passes through `stopping` after a stop; `error` says why a job failed), then read each case's result through `last_run.run_id` and `/api/runs/<run_id>/detail`.

Run the cases that cover the code being changed, not the whole suite; a full run takes multiple hours. `POST /api/job/stop` stops gracefully: it finishes the code in flight and runs the teardown, which can take up to the code timeout.

Do not record (`"mode":"record"`) or adopt (`POST /api/runs/<run_id>/adopt`) without the user asking. Both rewrite what counts as correct behaviour, and recording or adopting from the `dsf` profile would make DSF's behaviour its own reference.

#### What the results mean

- A case marked `known_bad` records behaviour believed to be wrong in RRF: `kind: bug` (RRF misbehaves, for example disagreeing with the wiki) or `kind: provisional` (not implemented in RRF yet, so the reference is a placeholder). Its `reason` says how that is known. A known-bad case passes while behaviour matches the recording; when DSF deliberately does the right thing instead, the case fails, and that is expected. Check `known_bad` before treating a failure as a DSF bug.
- Reply text is not translated between benches, so a reply naming a board address (`Driver 1.3 ...`) is tied to the bench's addressing. CAN frames are translated back to board aliases, so they compare across benches.
- A case skipped because the rig lacks a declared board, or because of `min_firmware`, `max_firmware` or `stack`, did not run and says nothing either way.
- The bench boots with an empty `config.g` apart from RRF's default X, Y and Z axes. Each case builds what it needs in `setup` and undoes it in `teardown`, and motion cases drive expansion-board drivers only, because a mainboard move produces no CAN traffic to compare. In DSF every driver is on an expansion board.
- The machine does not reboot between cases and the order is not fixed. A failure on state the case never touched usually comes from something an earlier case left behind, such as a queued message box or an axis created with `M584`, which cannot be deleted without a reset.

#### Using it during development

- A deviation found through the suite gets a system scenario in `src/SystemTests` that reproduces it, before it is fixed.
- If a reference disagrees with the RRF source or the G-code documentation, report it to the user (see Porting from RepRapFirmware).
- On the Pi, `/opt/dsf/bin/CodeConsole` sends codes from stdin, and `journalctl -u duetcontrolserver --since <time>` shows DuetControlServer's debug log, including per-code processing and CAN traffic.

## Architecture

### Runtime processes (`src/`)

- **DuetControlServer (DCS)**: the core daemon. A `Microsoft.Extensions.Hosting` process whose components are hosted `BackgroundService`s and DI singletons, wired in `Program.cs`. Owns the object model, code processing, the job, motion planning and the firmware link. Exposes a UNIX socket IPC server (`/run/dsf/dcs.sock`) that every other component uses.
- **DuetWebServer**: ASP.NET/Kestrel REST + WebSocket front end, serving Duet Web Control.
- **DuetPluginService**, **DuetPiManagementPlugin** (a plugin deployed to `/opt/dsf/plugins`, not a service), and small IPC clients (CodeConsole, CodeLogger, CodeStream, ModelObserver, PluginManager).
- **DuetAPI** (object model types, commands, IPC contracts) and **DuetAPIClient**: the shared libraries every component and third-party plugin builds against. Object model changes here are public API.
- **DuetRealtimeCore**: native C++ library DCS loads through a C ABI (`CApi.h`; `Link/Native/NativeMethods.cs` and `Motion/Native` on the C# side). It runs the SPI transfer loop on a pinned real-time thread and the low-level motion engine (DDA ring, lookahead, segments).

### Code flow inside DCS

Each `CodeChannel` (HTTP, File, Daemon, Trigger, ...) has a `ChannelProcessor` running codes through the pipeline stages in `Codes/Pipelines` (`Start`, `Pre`, `ProcessInternally`, `Post`, `Executed`). Codes ported from RRF are handled internally by the partial classes in `Codes/Handlers` (`MCodeHandler.Heat.cs`, `MCodeHandler.Motion.cs`, `GCodeHandler.Homing.cs`, ...), grouped by subsystem. Moves go through `Motion/` (`MoveInterpreter`, `MovePlanner`, `MotionService`) into the native engine; CAN traffic to expansion boards goes out over the link (`Link/`, with `Link/Expansion/ExpansionBoardManager.cs` tracking boards). The job lifecycle is its own state machine in `Files/Job/JobController.cs`, separate from per-code processing.

The object model (`src/DuetAPI/ObjectModel`) is the machine's configuration, not a cache of what the hardware is doing. It must hold enough to recreate the configuration of any expansion board that reconnects, so that DSF can reconfigure the board from the model alone. Sending a setting to a board over CAN without recording it in the model is not acceptable, even when the board is what acts on it. When a configuration code sets something the model does not represent yet, add the field or model class to the object model rather than keeping it in a private field. Transient state, such as driver enable or current position, is exempt.


## Documentation conventions

Two separate places, for two audiences:

- **`src/Documentation/articles/`**: the published DSF wiki (docfx). Current behaviour only, with no history, plans or migration status. A change a user can observe (G/M-code semantics, object model, IPC, firmware link, REST API, file handling) updates the matching article in the same change. `rrf-differences.md` lists only deliberate, implemented deviations from RRF; unimplemented gaps belong in `docs/devel`. New articles need an entry in `toc.yml`.

When writing Markdown, put each paragraph and each list item on a single line. Do not insert newlines to wrap prose at a column width. Blank lines between paragraphs, code blocks, tables and headings are unaffected.

## Porting from RepRapFirmware

The aim is a well-structured DSF code base that follows consistent rules and conventions, with RRF as the reference for behaviour. Port behaviour faithfully, including parameter handling, error messages and edge cases.

Three references describe RRF's behaviour. Check all that are available:

- The RRF source in `src/RepRapFirmware/src`.
- The regression suite references, where a case covers the code (see the regression suite section above).
- The G-code documentation in the Duet wiki source, https://github.com/Duet3D/wiki-content.

If these disagree with each other, one of them probably has a bug. Report the disagreement to the user rather than choosing one silently.

A behaviour change is acceptable where RRF's behaviour is inconsistent with the way RRF handles similar cases (for example, one unknown G-code returning an error where the rest return a warning), or where RRF's implementation has a bug. Follow the convention, not the outlier, record the deviation in `src/Documentation/articles/rrf-differences.md`, and point it out when reporting the work. Any other departure from RRF needs approval first: say what RRF does, what does not fit, and what the options are.

When a feature depends on another feature that is not implemented yet, write the feature in the structure its final implementation will have. Put a minimal shim in place of the missing piece only where one is needed, with a `// TODO` describing what has to be added. Do not build a temporary version of the feature around the gap: implementing something once only to reimplement it when the dependency lands is wasted work, and a substitute behaviour hides the gap that a `TODO` keeps visible.

## Code conventions

- Look for an existing setting, config value or parameter before writing a numeric literal.
- Refactor to a shared base instead of duplicating functionality.
- Comments describe the code as it is; never narrate what was removed or moved.
- Keep comments short: one line by default, saying what the code is for or the one fact a reader cannot get from the code.
- A comment is not a record of the investigation. Scenarios, chains of cause and effect and arguments that the code is safe go in the commit message.
- Name what a comment refers to. If "this one", "that board" or "it" could mean more than one thing, use the actual name.
