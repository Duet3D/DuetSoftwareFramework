# DSF4 implementation order

The order in which to bring DSF to feature parity with RepRapFirmware 3.7. Part A covers the codes that already have system tests, Part B the codes that have none, and Part C the codes that need an architecture decision before either.

The tests, not this document, say what is done. A stage is finished when its codes pass with `scripts/test.py system --code <code>`, the scenarios have lost `[Category("KnownGap")]`, the regression cases that cover the codes pass on the bench, and the affected articles and `KNOWN_BUGS.md` are updated. The counts below are a snapshot from a full run (`scripts/test.py system --all`) on 2026-11-24, with the pause and resume fixes of that day in: 1432 cases, 1117 passed, 315 failed, counting `PauseInsideWhileLoop`, which passed or failed by timing in that run, as failed. The causes are read from the failure messages and have not been confirmed by fixing them. No regression bench results were available.

## Principles behind the order

1. Wrong motion comes before missing features. A fault that leaves the head in the wrong place is fixed first.
2. A shared mechanism comes before the codes that need it, so that it is written once. Most of the failures in Part A come from a few of these, listed against their stages.
3. A code comes after the codes that its scenarios use to set up the machine.
4. Codes with scenarios come before codes without. Codes without get their scenarios first, then the port.

## Part A: codes with system tests

| Stage | Subject | Failing cases | Depends on |
| --- | --- | --- | --- |
| 0 | A baseline that can be trusted | 0 | |
| 1 | Pause, resume and cancel | 4 | 0 |
| 2 | Restore points and tools | 20 | 0 |
| 3 | Drives | 74 | 0 |
| 4 | M950 devices, heat and spindles | 62 | 0 |
| 5 | Kinematics | 58 | 3 (M584) |
| 6 | Endstop and probe configuration | 44 | 3 (M584), 4 (M950 J) |
| 7 | Probing and compensation | 37 | 2, 5, 6, M291/M292 |
| 8 | Files, general and laser | 16 | 0; some cases need Part B |

Stages 1 to 4 touch mostly separate code and can be worked on in parallel once stage 0 is done. Stages 5 to 7 are the critical path: M584, then M669, then M574/M558, then G30.

### Stage 0: a baseline that can be trusted

- Decide which scenarios describe behaviour DSF should not copy, and change the scenario or add the deviation to `rrf-differences.md`: `M122` (the `=== Diagnostics ===` header), `M550` (DSF ties the name to the Linux hostname) and `M552` (owned by DuetPiManagementPlugin).
- Remove the `KNOWN_BUGS.md` entries for `M557RefusesASecondPointCountUnderTwo` and `M915AsksTheBoardsInAddressOrder`, which no longer exist, and the link to the deleted `GCODE_COVERAGE.md` in `docs/devel/README.md`.

### Stage 1: pause, resume and cancel

1. `JobEdgeCaseTests.PauseInsideWhileLoop`: a job paused inside a `while` loop loses the iterations that were left, so part of the print is silently skipped. The rewind point has to carry the open code blocks.
2. `CancelRestartTests.RestartFractionSchedulesOnlyThePartNotYetMade` (`M26 P`): the job re-runs the part of the line already made.
3. `DeferredPauseTests.PauseDuringPlainMacroDefers`: the reply to a second `M25` while a pause is pending.
4. `M0`/`M1`/`M2` outside a job are refused (`M0M1M2Tests`).

`KNOWN_BUGS.md` also records that `Code.IsCurrentlyDeferred` is never cleared on a pooled code. No scenario shows its effect.

### Stage 2: restore points and tools

Mechanism: restore points are published to the object model only by `G60` and the job sequences (`MovePlanner.PublishRestorePoints`), so scenarios that read them otherwise find an empty collection. Publish all of them from start-up, one coordinate per visible axis. `G60` also publishes the feed rate in mm/s where the scenario expects mm/min.

Mechanism: bring the user position into step when the transform changes (`MoveInterpreter.SyncInterpreterToCoords`). `G10` offsets already do this; tool selection here and loading or clearing a height map in stage 7 need the same call.

1. `G60`, then the initial `inputs[].drivesRelative` (`M82`/`M83`).
2. `G10` (report wording, and `G10 P S R` through the `M568` path, which is not reached at all today) and the `M567` report wording.
3. `M37` report, which `TTests.SimulatedToolChangeOnlySelects` relies on.
4. `T`: `state.nextTool`/`previousTool`, restore point 2, `T{expression}` and `T T<n>`, `T R<n>`, generic `tfree.g`/`tpre.g`/`tpost.g`, reselecting the current tool, an undefined tool, simulation, then offsets and axis maps applied at selection. `ResumeReselectsTheToolThePauseRecorded` comes last, after stage 1.

### Stage 3: drives

Mechanism: one request per board for a set of drivers (`RemoteDrivers`). `M584` sends two or three where RRF sends one, and the `M906` scenarios show settings kept in the model but not sent when a later part of the code is refused. Both belong in the shared code, not in each handler.

Mechanism: a reader for a list of drivers across boards. `M569`, `M569.1`, `M569.4` and `M915` each take a single driver and refuse a list.

Mechanism: only record what a board accepted. `M915` and `M569.x` do not do this yet.

1. `M584`, first of all, because stages 5 and 6 need it: lower-case axis letters in the parser, visible axes (`P`), `R`/`S` ranges, new axis defaults (including an endstop entry per axis), board batching, then resetting phase stepping on the old drivers.
2. `M906`, `M913` (fractional currents and percentages, idle factor to idle drivers) and `M915`.
3. `M569` (the chopper register, driver lists, missing `P`), then `M569.1`, `.2` and `.7`. The object model classes for their settings exist already (`DriverClosedLoopConfig`, `DriverWaveformCorrection`, `DriverBrake`), so this is mostly recording and reporting. Then `.4` and `.6`, and the `Command is not supported` severity of `.3`, `.8` and `.9`.
4. `M572`: store the value as given rather than at half precision, and the infinite distance and report cases.
5. `M593`: `move.shaping` impulses and delays, and turning shaping off. Nothing sends `CanMessageSetInputShapingV1`, and the boards do the shaping, so today `M593` has no effect on motion.

### Stage 4: M950 devices, heat and spindles

Mechanism: one create, replace, delete and report flow for every `M950` device, with the board address and port name handling in `IoPorts`. The same failures recur across the forms: a refused device is left in the model, the old pin is not released before the new one is claimed, `C"nil"` is not understood, port names are not reduced, and reports are answered locally instead of by the board. `M558` and `M574` have the same rule (stage 6), so the flow must not be specific to `M950`.

1. `M950 F` (5 cases), as the form closest to complete, to settle the flow.
2. `M950 H` (14), then `M570` and `M105`.
3. `M950 P`, `S` and `D` (10).
4. `M950 J` (8). Unblocks `M574 E` in stage 6, and `M581`/`M582` input triggers in Part B.
5. `M950 R` (10), then `M3`/`M4`/`M5` (2).
6. `M950 E` (11). Unblocks `M150` in Part B.

### Stage 5: kinematics

1. Names as RRF 3.7 spells them, in the model and in the reports (`scara`, `polar`, `rotaryDelta`, `delta`). This alone fixes or unblocks much of the stage: `AxisLetterOrderTests`, `M665Tests` and most `M669` reports.
2. The matrix in the `M669` report, then matrix rows and their refusals (Cartesian and core geometries).
3. Selecting a geometry: what is kept, what is unhomed, where the head is afterwards, and nothing of a refused code applied. Resets `tiltCorrection`.
4. SCARA and polar parameters.
5. Linear delta extra towers (`M669` and `M665`), five-bar SCARA, rotary delta.
6. Hangprinter, last. It is the largest port and the least used, and the `M669` parameters are not supported at all today.
7. `M671` (`tiltCorrection.lastCorrections`), which `G32` needs in Part B.

### Stage 6: endstop and probe configuration

1. `M574` (12): refusals, moving an existing switch, replaced ports, `S0`, stall reports, `triggered` of a probe endstop, then `E` after `M950 J`. `M119` goes with it.
2. `M558` (26), in this order: one offset per axis and the defaults (also `M851`), speeds in mm/min, redefinition and refusal (the stage 4 rule), the board pin report, scanning probe reading (`CanMessageReadInputsRequest`, which nothing sends yet), heater suspension (`B1`), then load cell (`P12`), which needs a place in the object model.
3. `G31` (4).

### Stage 7: probing and compensation

Prerequisite from Part B: `M291`/`M292`. `G30WithAManualProbeAsksTheUserForTheHeight` waits for a message box, and the event pause boxes (`EventProcessor`) wait for the same code.

1. `G30` without `P`: refusal until homed, unhomed dive, start height, no second move down, lifts, macro names, a probe deployed by `M401`, tap averaging and datum, fast then slow. The detail is in `KNOWN_BUGS.md`.
2. `G30 S-1`, `S-2`, `S-3`. `S-2` needs tool selection (stage 2).
3. Height map user position: `M375`, `M561`, `G29 S1` and `S2`, then `G29 S0`. Includes `LoadingAHeightMapReportsZWithTheCorrectionTakenOff`.
4. `M557` limits.
5. `G30 P` and its report, with the two `GeneratedMoveTransformTests` that use it. Then manual probing.

### Stage 8: files, general and laser

Small and independent; suitable for filling gaps between the larger stages.

- `M23`, `M30`, `M36` (invalid JSON for a missing file), `M36.2`, `M38`, `M472`, `M606` refusals.
- `M118` and its channel targets.
- `M581` report. `M581.1` did not run its macro in the scenario.
- `M500 P68` needs `G68`, and `M501` inside `M502` needs `M502`; both are in Part B.
- `LaserModeAppliesM3PowerToMoves` needs a decision (Part C).

## Part B: codes with no system tests

DSF has no table row for these. Each group gets scenarios from the RRF source, the G-code documentation and the regression references first, then the port. They are ordered by what they unblock and then by how many machines need them.

| Order | Group | Codes | Needs |
| --- | --- | --- | --- |
| B1 | Messages | `M291`, `M292`, `M117`, `M300` | Before stage 7 |
| B2 | Coordinates | `G53`, `G54` to `G59.3`, `G68`, `G69`, `M206`, `M579` | `G10 L2`/`L20` exist but no code selects a workplace |
| B3 | Arcs and retraction | `G2`, `G3`, `G17` to `G19`, `G10`/`G11` without parameters, `M207`, `M101` to `M103`, `M200`, `M404`, `M407` | Stage 1: an arc or a retraction must not be pausable part-way (TODO in `MoveInterpreter.BuildRawMove`) |
| B4 | Levelling and calibration | `G32`, delta and leadscrew calibration, `G38.2` to `G38.5`, `M585`, `M675`, `M673`, `M558.1` to `M558.4`, `M672`, `M594`, `M951` | Stage 7 and `M671` |
| B5 | Heater tuning | `M303`, `M309`, `M144`, `M305` | Stage 4. `CanMessageHeaterTuningCommand` and `HeaterFeedForwardV1` are unused today |
| B6 | Filament | `M591`, `M701` to `M703` | Stage 4. The filament monitor messages are unused, and the status report has no consumer |
| B7 | Job | `M486` (needs `job.build`), `job.layer`, `M502` | Stages 1 and 8 |
| B8 | LEDs and extrusion outputs | `M150`, `M571` | `M950 E` |
| B9 | Triggers and power | `M582`, `M80`, `M81` | `M950 J`; `M581` |
| B10 | Accelerometers | `M955`, `M956` | `CanMessageStartAccelerometer` and `AccelerometerData` are unused |
| B11 | Serial input and housekeeping | `M110`, `M998`, `M555`, `M575`, `M905`, `M559`, `M560` | `M559`/`M560` exist on `v3.7-dev`, whose merge is separate work |
| B12 | Multiple motion systems | `M595` to `M599`, `M606` proper | Last. Most `TODO`s naming `M596` assume one ring and one motion system throughout |

Outside the dispatch tables, two things also belong before release: re-creating the state of an expansion board that reconnects from the object model alone, which nothing does yet, and scenarios for the codes that have a row and none today (`M85`, `M592`, `M553`, `M554`, `M587` to `M589`).

## Part C: decisions needed first

| Codes | Question |
| --- | --- |
| `M452`, `M3` in laser mode | RRF's laser port is on the main board. Board 0 has no ports, and a move has no field for laser power. Which board drives the laser, and does the wire format carry the power per move? |
| `M80`, `M81`, `M571` | Same question for the pin: is it an expansion board output, and who switches it? |
| `M260`, `M261`, `M672` | Which board has the I2C, UART or Modbus port, and should DSF support it? |
| `M911`, `M912`, `M916` | Does auto save use DuetCANMaster's supply reading, and where does `resurrect.g` come from? |
| `M21`, `M22`, `M540`, `M918` | Does the SBC have an equivalent, or are these refused with a reason? |

Not to be ported: `M576` and `M954` (dropped in `rrf-differences.md` section 1), IOBits and so `M670` (section 7), `M674` (not implemented in RRF) and `M750` to `M756` (removed from RRF).
