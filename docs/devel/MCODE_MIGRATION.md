# Porting `GCodes::HandleMcode` into DuetControlServer

Tracking document for migrating the M-code handling in
[`lib/RepRapFirmware/src/GCodes/GCodes2.cpp`](lib/RepRapFirmware/src/GCodes/GCodes2.cpp)
(`GCodes::HandleMcode`, lines 617-4746) into
[MCodeHandler.cs](src/DuetControlServer/Codes/Handlers/MCodeHandler.cs) and the subsystems it drives.

RRF's switch has **204 case labels** covering **~190 distinct M-codes**. This document is the
inventory: what each one does, where its configuration belongs in the object model, and whether it is
done.

---

## 1. The contract every ported M-code follows

These rules come from the architecture already established on this branch — see
[MotionParameters.cs](src/DuetControlServer/Motion/MotionParameters.cs) and
[GCodeHandler.cs](src/DuetControlServer/Codes/Handlers/GCodeHandler.cs) — they are not new inventions.

1. **The object model is the configuration, and it must hold enough to recreate the machine.** An
   M-code that configures the machine writes `move.axes[]`, `move.extruders[]`, `move.kinematics`,
   `boards[].drivers[]`, `sensors.*` and so on. Nothing keeps a private authoritative copy. If the
   object model has nowhere to put a value, extend the object model (§6) rather than storing it
   beside it.

   **Sending a setting to an expansion board is not storing it.** A configuration code that forwards
   its parameters over CAN and records nothing leaves the object model unable to describe the
   machine, which breaks M500 writing config-override.g, breaks what the interfaces can show, and
   leaves no way to reconfigure a board that reconnects. The test to apply is: if the process
   restarted and had to rebuild the machine from `model` alone, would anything be lost? Whatever
   would be lost belongs in the object model. Transient state - whether a driver is currently
   energised, where an axis is right now - is exempt; configuration is not.

2. **Derived state is rebuilt, never edited.** `MotionParameters.FromObjectModel()` snapshots the
   object model for the planner and `MovePlanner.ReconfigureAsync()` pushes it down to the native
   engine. An M-code that changes motion configuration calls `ReconfigureAsync`; it does not reach
   into `MotionParameters`.

3. **`ReconfigureAsync` requires standstill.** Steps/mm, kinematics or driver mapping changing under a
   queued move makes the endpoints it was planned against mean something else. Anything in that class
   must flush and drain first — the equivalent of RRF's
   `LockAllMovementSystemsAndWaitForStandstill`. The **Standstill** column records which codes need it.

4. **Remote hardware only.** RRF branches on local vs. CAN-attached hardware throughout. In this
   architecture DSF is the sole main board and all drivers, heaters, fans and sensors live on
   Duet3Expansion boards, so **only the CAN path is ported**. Every `#if SUPPORT_CAN_EXPANSION` branch
   is the one to keep; the local-hardware branch is dropped. In practice this makes the ports
   *smaller* than the RRF original.

5. **Reporting form is preserved.** With no parameters, RRF reports the current setting in a specific
   text format. Keep those strings — DWC, PanelDue and a decade of macros parse them.

6. **Codes that never reach here.** Codes intercepted earlier in the pipeline (`Codes/Pipelines/*`,
   `Codes/Meta/*`) or by the SBC plugins are marked ⛔.

### Recipe for porting one code

1. Read the RRF implementation at the line given in the tables below.
2. Strip the local-hardware branch; keep the CAN branch.
3. Find or add the object model home for the setting (§6).
4. Add the `case` to `MCodeHandler.ProcessAsync`, writing the object model under
   `model.AccessReadWriteAsync`.
5. If it is motion configuration, flush to standstill and call `MovePlanner.ReconfigureAsync`.
6. If a board needs telling, send the CAN message — see
   [Link/Protocol/CanMessages/Generated/](src/DuetControlServer/Link/Protocol/CanMessages/Generated/).
   `CanGenericTables.g.cs` already carries parameter tables for M111, M150, M308, M569
   (+ `.1`/`.2`/`.4`/`.6`/`.7`), M655, M915, M950 (heater/fan/gpio/led), M955 and M959.
7. Report in RRF's format when no parameters are given.
8. Tick the box here.

### Status legend

| | Meaning |
|---|---|
| ✅ | Fully handled inside DSF |
| 🔵 | ~~SBC half only~~ — resolved, see §2 |
| 🟡 | Partially ported — see the note |
| ⬜ | Not started |
| ⛔ | Out of scope: handled elsewhere in the pipeline, local-hardware only, or withdrawn in RRF |

---

## 2. The existing handlers are only half of each code

`MCodeHandler` predates this branch. It was written for the **split architecture**, where DSF ran on
the SBC and RepRapFirmware ran on the Duet over SPI: DSF did the file/network/plugin part of a code
and then handed the code to RRF to do the machine part. The code says so in as many words:

```
// Let RRF do everything else                        MCodeHandler.cs:250, :505
// Hostname is legit - pass this code on to RRF      MCodeHandler.cs:939
// No SBC fields in the expression — let RRF handle  MCodeHandler.cs:972
// Command not supported. Let RRF decide what to do  MCodeHandler.cs:1038
// Let RRF carry on                                  MCodeHandler.cs:1054
// Let RepRapFirmware process this request so it
//   can invoke resume.g                             MCodeHandler.cs:270
```

There is a second switch in `MCodeHandler.CodeExecutedAsync` that is *entirely* post-processing of a
reply RRF produced: M122 appends DSF diagnostics to RRF's output, M409 patches RRF's JSON, and M596
and M606 re-sync the object model after RRF changed `inputs[].active`.

**This has now been resolved.** The split is gone: `PipelineStage.Firmware` and the whole
`Link/Channel` namespace have been deleted, a code that no handler claims resolves as
`Unsupported command: ...` rather than being forwarded anywhere, and every code that used to return
"not finished here" now returns a real result. `CodeExecutedAsync` keeps only the two hooks that were
never about RRF replies — resuming the job after M0/M1/M2/M24/M32/M37, and starting the second job
after M606 S1.

The remaining 🔵 marks below are historical; treat them as ✅ for the SBC half and read the note for
what the machine half now does (or does not yet) do.

---

## 3. Progress summary

| Group | ✅ Done | 🔵 SBC half | Rows (excl. ⛔) |
|---|---|---|---|
| §5.1 Motion — drives and axes | 26 | 0 | 26 |
| §5.2 Motion — kinematics and geometry | 8 | 0 | 12 |
| §5.3 Motion — compensation and probing | 12 | 0 | 14 |
| §5.4 Motion — queue, sync and shaping | 3 | 1 | 9 |
| §5.5 Heat | 0 | 0 | 19 |
| §5.6 Fans | 0 | 0 | 3 |
| §5.7 Tools and filament | 0 | 0 | 14 |
| §5.8 Spindles, laser and machine mode | 0 | 0 | 9 |
| §5.9 Job, files and SD | 17 | 4 | 29 |
| §5.10 Network | 4 | 1 | 13 |
| §5.11 I/O, expansion and miscellaneous | 6 | 5 | 38 |
| **Total** | **77** | **11** | **186** |

Update these counts as boxes are ticked.

---

## 4. Prerequisites that block whole groups

| Blocker | Blocks | Note |
|---|---|---|
| **No Heat subsystem in DCS** | §5.5, parts of §5.7 | `src/DuetControlServer/` has `Motion/` but no `Heat/`. The object model (`DuetAPI/ObjectModel/Heat/`) and the CAN messages (`CanMessageSetHeaterTemperatureV1`, `CanMessageHeaterModelV3`, `CanMessageSetHeaterMonitors`, `CanMessageHeaterTuningCommand`, …) both exist, so the gap is the service layer: heater state machine, tuning, fault handling, sensor polling |
| **No Fan subsystem in DCS** | §5.6 | Same shape: `CanMessageFanParameters` / `CanMessageSetFanSpeed` / `CanMessageFansReport` exist, the service does not |
| **No Tool subsystem in DCS** | §5.7, M116, M568 | Tool selection, offsets, mix ratios, standby/active temperatures. [TCodeHandler.cs](src/DuetControlServer/Codes/Handlers/TCodeHandler.cs) is a 27-line stub |
| **No Spindle subsystem in DCS** | §5.8 | |
| **No endstop/probe abstraction in DCS** | M119, M558, M574, M577, M585, M401, M402, M851 | Needs the input-monitor CAN messages (`CanMessageCreateInputMonitorV1`, `CanMessageChangeInputMonitorV1`, `CanMessageInputChangedV2`) wired to `sensors.endstops[]` / `sensors.probes[]` |

Because of these, **§5.1-§5.4 (motion) is the tractable scope on this branch**; the rest is gated on
subsystems that do not exist yet.

### Kinematics engines

Every geometry RepRapFirmware supports now has an engine under
[Motion/Kinematics/](src/DuetControlServer/Motion/Kinematics/), and
`MotionParameters.BuildGeometry` picks between them from `move.kinematics`. What is left is the
M-codes that configure them (M665, M666, M667 and the non-core half of M669), not the geometry itself.

| Engine | Ported from | Notes |
|---|---|---|
| `CoreKinematicsEngine` | `CoreKinematics` | Cartesian, CoreXY, CoreXZ, CoreXYU, CoreXYUV, MarkForged |
| `LinearDeltaKinematicsEngine` | `LinearDeltaKinematics` | Up to six towers, bed tilt correction |
| `RotaryDeltaKinematicsEngine` | `RotaryDeltaKinematics` | Nothing in the object model configures it — built with RRF's defaults |
| `ScaraKinematicsEngine` | `ScaraKinematics` | Arm mode is engine state, as in RRF |
| `FiveBarScaraKinematicsEngine` | `FiveBarScaraKinematics` | Nothing in the object model configures it — built with the M669 documentation's defaults |
| `PolarKinematicsEngine` | `PolarKinematics` | Turntable speed and acceleration limits are converted to step clocks at build time |
| `HangprinterKinematicsEngine` | `HangprinterKinematics` | Constant spool radius only; see below |

Two of RepRapFirmware's hangprinter refinements are deliberately not ported, because nothing in the
object model can express them and so nothing could configure them: **line buildup compensation**,
which varies steps per mm with how much line is on the spool, and **flex compensation**, which needs
the mover's weight and the lines' spring constants. The engine uses the constant-radius model, which
is the branch RepRapFirmware itself takes when the buildup factor is zero. Both need
`move.kinematics` to grow the fields M669 sets before they can be brought across.

Position limiting (`LimitPosition`), homing, auto-calibration and bed levelling are not part of
`KinematicsEngine` yet; the reachability predicates each engine exposes (`IsReachable`) are the piece
of that work which the transforms already needed.

---

## 5. Inventory

RRF line numbers refer to `lib/RepRapFirmware/src/GCodes/GCodes2.cpp`.

### 5.1 Motion — drives and axes

| M-code | RRF | Purpose | Object model home | Standstill | Status |
|---|---|---|---|---|---|
| M17 | 910 | Motors on | `move.axes[].drivers` → CAN `MultipleDrivesRequestDriverStateControl` | no | ✅ |
| M18 / M84 | 911 | Motors off, set idle timeout | `move.idle.timeout`, `move.idle.factor` | yes | ✅ |
| M82 | 1600 | Absolute extruder positioning | `inputs[].drivesRelative = false` | no | ✅ |
| M83 | 1605 | Relative extruder positioning | `inputs[].drivesRelative = true` | no | ✅ |
| M85 | 1612 | Set inactive time | `move.idle.timeout` | no | ✅ |
| M92 | 1615 | Steps per mm | `move.axes[].stepsPerMm`, `move.extruders[].stepsPerMm` → CAN `MultipleDrivesRequestStepsPerUnitAndMicrostepping` | yes | ✅ |
| M114 | 1945 | Report position | reads `move.axes[].machinePosition` / `userPosition` | no | ✅ |
| M120 | 2210 | Push machine state | `inputs[].stack` | no | ✅ |
| M121 | 2214 | Pop machine state | `inputs[].stack` | no | ✅ |
| M201 | 2527 | Axis/extruder accelerations | `move.axes[].acceleration`, `move.extruders[].acceleration` | yes | ✅ |
| M201.1 | 2527 | Reduced accelerations for probing and stall homing | `move.axes[].reducedAcceleration` | yes | ✅ |
| M203 | 2612 | Min/max feedrates | `move.axes[].speed`, `move.extruders[].speed`, `move.minimumMovementSpeed` | yes | ✅ |
| M204 | 2670 | Print/travel acceleration | `move.motionSystems[].printingAcceleration` / `.travelAcceleration` | no | ✅ |
| M205 / M566 | 3782 | Jerk (mm/s and mm/min forms), jerk policy | `move.axes[].jerk` / `.printingJerk`, `move.jerkPolicy` | yes | ✅ |
| M208 | 2701 | Axis minima/maxima | `move.axes[].min` / `.max` | no | ✅ |
| M220 | 2705 | Speed factor override | `move.speedFactor` | no | ✅ |
| M221 | 2734 | Extrusion factor override | `move.extruders[].factor` | no | ✅ |
| M350 | 2996 | Microstepping | `move.axes[].microstepping` → CAN `MultipleDrivesRequestStepsPerUnitAndMicrostepping` | yes | ✅ |
| M400 | 3120 | Wait for moves to finish | — (flush and drain) | n/a | ✅ |
| M569 | 3878 | Driver configuration (direction, mode, timings) | `boards[].drivers[]` → CAN generic `M569Params` (+ `.1`/`.2`/`.4`/`.6`/`.7`) | yes | ✅ |
| M584 | 3956 | Axis/extruder → driver mapping | `move.axes[].drivers`, `move.extruders[].driver` | yes | ✅ |
| M906 | 4377 | Motor currents | `move.axes[].current` → CAN `MultipleDrivesRequestMotorCurrents` | no | ✅ |
| M913 | 4378 | Motor current percentage | `move.axes[].percentCurrent` | no | ✅ |
| M915 | 4539 | Stall detection | `boards[].drivers[]` → CAN generic `M915Params`, `CanMessageEnableStallEndstop` | no | ✅ |
| M917 | 4380 | Standstill current percentage | `move.axes[].percentStstCurrent` → CAN `MultipleDrivesRequestStandstillCurrentFactor` | no | ✅ |
| M970 | 4639 | Phase stepping mode | — | n/a | ✅ reports unsupported, see §8 |

### 5.2 Motion — kinematics and geometry

| M-code | RRF | Purpose | Object model home | Standstill | Status |
|---|---|---|---|---|---|
| M290 | 2812 | Babystepping | `move.axes[].babystep` | no | ✅ |
| M425 | 3223 | Backlash compensation | `move.axes[].backlash`, `move.backlashFactor` | yes | ✅ |
| M556 | 3653 | Axis skew compensation | `move.compensation.skew` | no | ✅ |
| M579 | 3925 | Scale Cartesian axes | needs new field — §6 | no | ⬜ |
| M665 | 4052 | Delta configuration | `move.kinematics` (`DeltaKinematics`) | yes | ✅ |
| M666 | 4082 | Delta endstop adjustments | `move.kinematics` (`DeltaKinematics`) | yes | ✅ |
| M667 | 4099 | CoreXY mode (legacy, superseded by M669) | `move.kinematics` (`CoreKinematics`) | yes | ✅ |
| M669 | 4104 | Kinematics selection and parameters | `move.kinematics` | yes | ✅ |
| M671 | 4152 | Z leadscrew positions | `move.kinematics.tiltCorrection` | no | ✅ |
| M673 | 4168 | Align plane on rotary axis | `move.rotation` | yes | ⬜ blocked: needs homing |
| M674 | 4275 | Set Z to centre point | — | yes | ⬜ blocked: needs probe points |
| M675 | 4311 | Find centre of cavity | — | yes | ⬜ blocked: needs G30 P |

### 5.3 Motion — compensation and probing

| M-code | RRF | Purpose | Object model home | Standstill | Status |
|---|---|---|---|---|---|
| M119 | 2206 | Report endstop status | `sensors.endstops[]` | no | ✅ |
| M374 | 3089 | Save height map to file | `move.compensation.file` | no | ✅ |
| M375 | 3093 | Load height map and enable compensation | `move.compensation` | no | ✅ |
| M376 | 3102 | Set taper height | `move.compensation.fadeHeight` | no | ✅ |
| M401 | 3131 | Deploy Z probe | `sensors.probes[]` | no | ✅ |
| M402 | 3144 | Retract Z probe | `sensors.probes[]` | no | ✅ |
| M557 | 3686 | Probe grid definition | `move.compensation.probeGrid` | no | ✅ |
| M558 | 3690 | Z probe type/configuration; `.1`/`.2` scanning probe calibration | `sensors.probes[]` | no | ✅ |
| M561 | 3730 | Identity transform, disable height map | `move.compensation.type` | no | ✅ |
| M574 | 3897 | Endstop configuration | `sensors.endstops[]` | no | ✅ |
| M577 | 3919 | Wait for endstop trigger | `sensors.endstops[]` | no | ✅ |
| M585 | 3960 | Probe tool | `sensors.probes[]`, tools | yes | ⬜ blocked: needs G30 P |
| M672 | 4164 | Program Z probe | CAN to the probe's board | no | ⬜ blocked |
| M851 | 4357 | Z probe offset (Marlin compatibility) | `sensors.probes[].offsets` | no | ✅ |

### 5.4 Motion — queue, sync and shaping

| M-code | RRF | Purpose | Object model home | Standstill | Status |
|---|---|---|---|---|---|
| M572 | 3891 | Pressure advance | `move.extruders[].pressureAdvance` → CAN `MultipleDrivesRequestPressureAdvanceV1` | yes | ✅ |
| M592 | 3992 | Nonlinear extrusion | `move.extruders[].nonlinear` | yes | ✅ |
| M593 | 3997 | Input shaping | `move.shaping` → CAN `CanMessageSetInputShapingV1` | yes | ✅ |
| M595 | 4007 | Movement queue size | `move.queue[]` | yes | ⬜ |
| M596 | 4016 | Select movement queue | `inputs[].motionSystem`, `move.motionSystems[]` | no | ⬜ only a post-execution OM re-sync exists |
| M597 | 4020 | Collision avoidance | needs new field — §6 | no | ⬜ |
| M598 | 4024 | Sync movement systems | — | n/a | ⬜ |
| M599 | 4030 | Define keepout zone | `move.keepout[]` | no | ⬜ |
| M606 | 4038 | Fork input reader | `inputs[]` | no | 🔵 forks the job on the SBC, then defers |

### 5.5 Heat — blocked on a Heat subsystem (§4)

| M-code | RRF | Purpose | Object model home | Status |
|---|---|---|---|---|
| M104 | 1844 | Set extruder temperature (no wait) | `heat.heaters[].active` | ⬜ blocked |
| M105 | 1749 | Report temperatures | `heat.heaters[]` | ⬜ blocked |
| M108 | 1819 | Cancel wait for temperature | — | ⬜ blocked |
| M109 | 1823 | Set extruder temperature and wait | `heat.heaters[].active` | ⬜ blocked |
| M116 | 1991 | Wait for temperatures | `heat.heaters[]` | ⬜ blocked |
| M140 | 2265 | Bed temperature | `heat.bedHeaters[]` | ⬜ blocked |
| M141 | 2266 | Chamber temperature | `heat.chamberHeaters[]` | ⬜ blocked |
| M143 | 2407 | Heater protection and limits | `heat.heaters[].monitors[]` → CAN `CanMessageSetHeaterMonitors` | ⬜ blocked |
| M144 | 2411 | Bed to standby/active | `heat.bedHeaters[]` | ⬜ blocked |
| M190 | 2432 | Set bed temperature and wait | `heat.bedHeaters[]` | ⬜ blocked |
| M191 | 2433 | Set chamber temperature and wait | `heat.chamberHeaters[]` | ⬜ blocked |
| M302 | 2927 | Cold extrude/retract permission and limits | `heat.coldExtrudeTemperature`, `heat.coldRetractTemperature` | ⬜ blocked |
| M303 | 2972 | Run PID tuning | `heat.heaters[].model` → CAN `CanMessageHeaterTuningCommand` | ⬜ blocked |
| M305 | 2976 | Legacy heater parameters | `heat.heaters[]` | ⬜ blocked |
| M307 | 2981 | Heater process model | `heat.heaters[].model` → CAN `CanMessageHeaterModelV3` | ⬜ blocked |
| M308 | 2985 | Configure sensor | `sensors.analog[]` → CAN generic `M308V1Params` | ⬜ blocked |
| M309 | 2989 | Tool feedforward | `tools[].feedForward` → CAN `CanMessageHeaterFeedForwardV1` | ⬜ blocked |
| M562 | 3738 | Reset temperature fault | `heat.heaters[].state` | ⬜ blocked |
| M570 | 3882 | Heater fault detection | → CAN `CanMessageSetHeaterFaultDetectionParameters` | ⬜ blocked |

### 5.6 Fans — blocked on a Fan subsystem (§4)

| M-code | RRF | Purpose | Object model home | Status |
|---|---|---|---|---|
| M106 | 1755 | Set fan speed and parameters | `fans[]` → CAN `CanMessageSetFanSpeed` / `CanMessageFanParameters` | ⬜ blocked |
| M107 | 1815 | Fan off (deprecated) | `fans[].requestedValue` | ⬜ blocked |
| M950 (fan) | 4589 | Create fan | `fans[]` → CAN generic `M950FanParams` | ⬜ blocked |

### 5.7 Tools and filament — mostly blocked on a Tool subsystem (§4)

| M-code | RRF | Purpose | Object model home | Status |
|---|---|---|---|---|
| M101 | 1733 | Un-retract (S3D legacy) | `tools[].retraction` | ⬜ blocked |
| M102 | 1737 | S3D no-op | — | ⛔ no-op |
| M103 | 1743 | Retract (S3D legacy) | `tools[].retraction` | ⬜ blocked |
| M200 | 2484 | Filament diameter / volumetric extrusion | `move.extruders[].filamentDiameter` | ⬜ |
| M206 | 2676 | Offset axes (legacy workplace offset) | `move.axes[].workplaceOffsets`, `move.workplaceNumber` | ⬜ |
| M207 | 2680 | Firmware retraction | `tools[].retraction` | ⬜ blocked |
| M404 | 3156 | Nominal filament width | see §6 | ⬜ |
| M407 | 3163 | Report filament width | reads the above | ⬜ |
| M563 | 3754 | Define tool | `tools[]` | ⬜ blocked |
| M567 | 3843 | Tool mix ratios | `tools[].mix` | ⬜ blocked |
| M568 | 3874 | Tool settings (active/standby/spindle RPM) | `tools[]` | ⬜ blocked |
| M591 | 3984 | Configure filament sensor | `sensors.filamentMonitors[]` → CAN `CanMessageCreateFilamentMonitor` + generic `ConfigureFilamentMonitorParams` | ⬜ |
| M701 | 4315 | Load filament | `tools[].filament` | ⬜ blocked |
| M702 | 4319 | Unload filament | `tools[].filament` | ⬜ blocked |
| M703 | 4323 | Configure filament | `tools[].filament` | ⬜ blocked |

### 5.8 Spindles, laser and machine mode — blocked on a Spindle subsystem (§4)

| M-code | RRF | Purpose | Object model home | Status |
|---|---|---|---|---|
| M3 | 805 | Spindle clockwise / laser power | `spindles[]` | ⬜ blocked |
| M4 | 806 | Spindle counter-clockwise | `spindles[]` | ⬜ blocked |
| M5 | 864 | Spindle off | `spindles[]` | ⬜ blocked |
| M450 | 3227 | Report printer mode | `state.machineMode` | ⬜ |
| M451 | 3231 | FFF mode | `state.machineMode` | ⬜ |
| M452 | 3244 | Laser mode | `state.machineMode`, laser config | ⬜ |
| M453 | 3284 | CNC mode | `state.machineMode` | ⬜ |
| M571 | 3887 | Set output on extrude | `state.gpOut[]` | ⬜ |
| M670 | 4146 | IO port allocation / laser task | `state.gpOut[]` | ⬜ |

### 5.9 Job, files and SD

| M-code | RRF | Purpose | Status |
|---|---|---|---|
| M0 / M1 / M2 | 755 | Stop / sleep / program end | 🔵 cancels the job, then relies on RRF to stop the machine |
| M20 | 990 | List files | ✅ |
| M21 | 1068 | Initialise SD card | ✅ |
| M22 | 1079 | Release SD card | ✅ |
| M23 / M32 | 1092 | Select file / select and start | 🔵 |
| M24 | 1160 | Start or resume print | 🔵 relies on RRF to invoke `resume.g` |
| M25 | 1281 | Pause print | ⬜ |
| M26 | 1319 | Set SD position | ✅ |
| M27 | 1339 | Report print status | ✅ |
| M28 | 1356 | Begin write to file | ✅ |
| M29 | 1373 | End write to file | ✅ |
| M30 | 1377 | Delete file | ✅ |
| M36 | 1397 | File information and thumbnails | ✅ |
| M37 | 1451 | Simulation mode | 🔵 |
| M38 | 1487 | File CRC32 | ✅ |
| M39 | 1517 | SD card info | ✅ |
| M73 | 1584 | Slicer print-time hints | ⬜ |
| M98 | 1701 | Call macro | ✅ |
| M99 | 1729 | Return from macro | ⛔ handled by the meta parser |
| M226 / M600 / M601 | 1249 | Synchronous pause / filament change | ⬜ |
| M470 | 3308 | mkdir | ✅ |
| M471 | 3325 | Rename file or directory | ✅ |
| M472 | 3346 | Delete file or directory | ✅ |
| M486 | 3365 | Object cancellation | ⬜ |
| M500 | 3370 | Save to `config-override.g` | ⬜ |
| M501 | 3376 | Load `config-override.g` | ⬜ |
| M502 | 3387 | Reset to factory settings | ⬜ |
| M503 | 3404 | List configuration | ✅ |
| M505 | 3451 | Set sys/web folder | ✅ |
| M559 / M560 | 3699 | Binary file upload | ⬜ |

### 5.10 Network

| M-code | RRF | Purpose | Status |
|---|---|---|---|
| M118 | 2092 | Echo message to a channel (incl. MQTT) | ✅ |
| M540 | 3485 | Set/report MAC address | ⬜ |
| M550 | 3504 | Machine name | 🔵 sets the hostname, then passes the code on |
| M551 | 3527 | Set password | ✅ |
| M552 | 3539 | Enable network / IP address | ✅ |
| M553 | 3601 | Netmask | ⬜ |
| M554 | 3618 | Gateway | ⬜ |
| M555 | 3637 | Firmware emulation mode | ⬜ |
| M575 | 3901 | Serial communications parameters | ⬜ |
| M576 | 3906 | SPI communications parameters | ⛔ the SPI link is gone |
| M586 | 3965 | Configure network protocols | ✅ |
| M587 | 3974 | Add WiFi network | ⬜ |
| M588 | 3975 | Forget WiFi network | ⬜ |
| M589 | 3976 | Configure access point | ⬜ |

### 5.11 I/O, expansion and miscellaneous

| M-code | RRF | Purpose | Object model home | Status |
|---|---|---|---|---|
| M42 | 1576 | Set output pin | `state.gpOut[]` → CAN `CanMessageWriteGpio` | ⬜ |
| M80 | 1588 | ATX power on | `state.atxPower` | ⬜ |
| M81 | 1592 | ATX power off | `state.atxPower` | ⬜ |
| M110 | 1933 | Set line number | — | ⬜ |
| M111 | 1937 | Debug level | → CAN generic `M111Params` | 🔵 sets DSF log levels only |
| M112 | 1941 | Emergency stop | → CAN `CanMessageEmergencyStop` | ✅ |
| M115 | 1949 | Firmware version / board type | `boards[]` → CAN `CanMessageReturnInfo` | 🟡 `B>0` works; `B0` is a TODO stub |
| M117 | 2084 | Display message | `state.displayMessage` | ⬜ |
| M122 | 2227 | Diagnostics | `boards[]` → CAN generic `M122P1Params` | 🔵 appends to RRF's output |
| M150 | 2427 | Set LED colours | `ledStrips[]` → CAN generic `M150Params` | ⬜ |
| M260 | 2778 | I2C send / Modbus write | — | ⬜ only local-variable bookkeeping exists |
| M261 | 2782 | I2C receive / Modbus read | — | ⬜ only local-variable bookkeeping exists |
| M280 | 2786 | Servo control | `state.gpOut[]` → CAN `CanMessageWriteGpio` | ⬜ |
| M291 | 2906 | Message box | `state.messageBox` | ⬜ |
| M292 | 2910 | Acknowledge message box | `state.messageBox` | ⬜ |
| M300 | 2914 | Beep | `state.beep` | ⬜ |
| M409 | 3169 | Object model query | — | 🔵 patches RRF's JSON |
| M564 | 3758 | Limit axes / allow movement before homing | `move.limitAxes`, `move.noMovesBeforeHoming` | ✅ |
| M581 | 3948 | Configure external trigger | `sensors.gpIn[]` | 🔵 SBC-side expressions only |
| M582 | 3952 | Check external trigger | `sensors.gpIn[]` | ⬜ |
| M594 | 4002 | Height following mode | — | ⬜ |
| M655 | 4046 | CAN configuration | → CAN generic `M655Params` | ⬜ |
| M905 | 4373 | Set RTC date and time | — | ⬜ |
| M911 | 4472 | Auto-save on power loss | `state.powerFailScript` | ⬜ |
| M912 | 4522 | MCU temperature calibration | `boards[].mcuTemp` | ⬜ |
| M916 | 4545 | Resume after power fail | — | ⬜ |
| M918 | 4566 | Configure direct-connect display | `boards[].directDisplay` | ⬜ |
| M929 | 4581 | Event logging | `state.logFile`, `state.logLevel` | ✅ |
| M950 | 4589 | Configure I/O pins (heater/fan/gpio/led/servo) | `state.gpOut[]`, `sensors.gpIn[]` → CAN generic `M950*Params` | ⬜ |
| M951 | 4594 | Height control | — | ⬜ |
| M952 | 4600 | Change expansion board CAN address | → CAN `CanMessageSetAddressAndNormalTiming` | ✅ |
| M953 | 4604 | CAN fast data rate | → CAN `CanMessageSetAddressAndNormalTiming` | ✅ |
| M954 | 4610 | Configure as expansion board | — | ⛔ DSF is always the main board |
| M955 | 4619 | Configure accelerometer | `boards[].accelerometer` → CAN generic `M955Params` | ⬜ |
| M956 | 4623 | Start accelerometer collection | → CAN `CanMessageStartAccelerometer` | ⬜ |
| M957 | 4628 | Raise event | → CAN `CanMessageEvent` | ⬜ |
| M959 | 4633 | Expansion board connection timeout | → CAN generic `M959Params` | ⬜ |
| M997 | 4645 | Firmware update | → CAN `CanMessageUpdateYourFirmware` | 🔵 |
| M998 | 4650 | Request resend | — | ⬜ currently throws `NotSupportedException` |
| M999 | 4663 | Reset | → CAN `CanMessageReset` | ✅ |
| M750-M756 | 4345 | 3D scanner extension | — | ⛔ withdrawn in RRF |
| M408 | — | Legacy status report | — | ⛔ withdrawn in RRF 3.7 |

---

## 6. Object model extensions needed

Most settings already have a home — the object model mirrors RRF's. These are the gaps found so far.
Add to this list as ports uncover more; extending the object model is the expected fix, per §1.1.

| Setting | M-code | Proposed location | Note |
|---|---|---|---|
| Cartesian axis scale factors | M579 | `move.axes[].scale` (float, default 1.0) | RRF keeps this in `GCodes::axisScaleFactors`, outside the object model |
| Collision avoidance limits | M597 | `move.collisionAvoidance[]` | New model object; RRF stores a minimum separation per axis pair |
| Nominal filament width | M404 / M407 | reconcile with `move.extruders[].filamentDiameter` | RRF has one global width; the object model has one per extruder. Decide which wins before porting either |
| Firmware emulation mode | M555 | `state.compatibility` + a `Compatibility` enum | Neither exists. RRF keeps it per input channel (`inputs[].compatibility`) — decide global vs. per-channel before adding |
| Reduced acceleration | M201.1 | `move.axes[].reducedAcceleration` | Already consumed by `MotionParameters`; confirm it is settable and persisted |

---

## 7. Suggested order of work

Each phase leaves the tree in a state where the machine is more usable than before.

1. ~~**Phase 1 — make an axis movable.** M92, M201, M203, M204, M205/M566, M208, M350, M400, M584,
   M906.~~ **Done** — see §8. Lives in
   [MCodeHandler.Motion.cs](../../src/DuetControlServer/Codes/Handlers/MCodeHandler.Motion.cs).
2. ~~**Phase 2 — driver detail.** M17, M18/M84, M85, M569, M913, M915, M917, M970, M572, M593, M592.~~
   **Done** — see §8.
3. ~~**Phase 3 — interpreter state and reporting.** M82, M83, M114, M120, M121, M220, M221, M290,
   M425, M556, M564.~~ **Done** — see §8.
4. ~~**Phase 4 — geometry.** M665, M666, M667, M669, M671.~~ **Done** — see §8. M673-M675 move
   to phase 5: they need homed axes and probe points.
5. **Phase 5 — endstops and probing.** Needs the input-monitor plumbing first: M119, M574, M558, M401,
   M402, M577, M585, M851, then the height map codes M374-M376, M557, M561.
6. **Phase 6 — queue and multi-system.** M595, M596, M597, M598, M599.
7. **Phase 7 — close out the 🔵 rows.** Absorb the RRF half of M0/M1/M2, M23/M32, M24, M37, M111,
   M122, M409, M550, M581, M606, M997, and delete the `CodeExecutedAsync` hooks that only patched up
   RRF replies.
8. **Later, gated on new subsystems.** Heat (§5.5), fans (§5.6), tools (§5.7), spindles (§5.8).

---

## 8. Notes and decisions

Record decisions here as they are made, so a later reader does not have to re-derive them from RRF.

### Phase 1 (M92, M201, M203, M204, M205/M566, M208, M350, M400, M584, M906)

**M584 is what creates an axis.** Nothing in DuetControlServer populated `move.axes[]`,
`move.extruders[]` or `move.motionSystems[]` — RepRapFirmware used to, over SPI. `move.axes[]` starts
empty and stays empty until M584 names a letter, exactly as in RRF, so M584 has to run before any of
the other motion codes have anything to configure. This is the reason a machine could not be set up
at all before this phase.

**M204 writes `move.motionSystems[].printingAcceleration`, not `move.printingAcceleration`.** The
latter is `[Obsolete]` in the object model. `MotionParameters.FromObjectModel` was reading the
obsolete pair, so writing to the correct one would have made M204 silently do nothing; it now reads
`motionSystems[0]`, falling back to the object model's own default when no motion system exists yet.
The planner is not per motion system, so the first one sets the limits for all of them. Two tests in
`MoveBuilderTests`/`MotionParametersTests` were setting the deprecated fields and were moved with it.

**Reconfiguring preserves the machine position.** Motor positions are microstep counts, so changing
steps per mm or microstepping changes where a given count is in mm. `MovePlanner.ReconfigureAsync`
now re-derives the endpoints from the axis coordinates (`MoveBuilder.RecalculateEndPoints`) and
pushes them to the engine, which is what RRF achieves with `AdjustEndpoint` scaling each endpoint by
the ratio the steps per mm changed by.

**Standstill is a real wait, not just a flush.** `MovePlanner.WaitForStandstillAsync` polls the
engine's scheduled-versus-completed move counts per ring. Flushing the code pipeline only guarantees
the moves have been *submitted*; RRF's `LockAllMovementSystemsAndWaitForStandstill` waits for them to
have been *run*, and M92, M350, M584 and M906 all need the latter. It is applied only when the code
actually names a drive, so that a bare `M92` or `M906` — which DWC polls — does not stall mid-print.

**Per-driver CAN messages are grouped by board.** `CanMessageMultipleDrivesRequest` addresses one
board with a bitmap in that board's local driver numbers, and the receiver pairs the n'th value with
the n'th set bit. `Link/Protocol/CanMessages/RemoteDrivers.cs` does the split, the ascending-order
packing and the chunking at eight drivers per message; the ordering is what
`UnitTests/Link/RemoteDriversTests.cs` covers, because getting it wrong applies settings to the wrong
motors rather than failing.

**`MCodeHandler` became `internal partial`.** Internal to match `GCodeHandler` and because it now
takes `MovePlanner`, which is internal; partial so the motion codes live in their own file rather
than extending an already long switch. Nothing outside the assembly referenced it.

**Not carried over from RRF's versions of these codes:** M201's `T` acceleration-time parameter and
the S-curve flag (`SUPPORT_3RD_ORDER`), M584's `MinVisibleAxes` lower bound, and M350/M92's
recalculation of backlash steps (`UpdateBacklashSteps`) — backlash arrives with M425 in phase 3.
M906's `I` and `T` set `move.idle`, but nothing acts on idle current yet; that needs M18/M84 in phase 2.

### Phase 2 (M17, M18/M84, M85, M569, M572, M592, M593, M913, M915, M917, M970)

**M970 can never work here and now says so.** RepRapFirmware refuses phase stepping for any axis
with a remote driver (`Move::SetStepMode` returns false the moment it sees one), because the mode
drives the motor coils directly from the main board. Every driver is remote in this architecture, so
the code reports that phase stepping is not supported on CAN-connected drivers rather than pretending
to configure it. The mapping this document previously gave for it was wrong twice over: `M959Params`
is the expansion board *connection timeout*, not phase stepping.

**M569 is repackaged rather than reimplemented.** Every parameter of it belongs to the driver, so the
code is turned straight into the CAN message its parameter table describes — the generated
`ICanGenericMessage.FromCode` does that — and answered by the board that owns the driver. The
sub-codes `.1`, `.2`, `.4`, `.6` and `.7` are separate message types over the same mechanism. Nothing
is mirrored into the object model: the driver's configuration is the board's, and what this side
records is the mapping M584 wrote.

**M906, M913 and M917 are one handler.** They differ only in which current they address, which is
also true in RepRapFirmware. M913 is a percentage of the configured current rather than a setting of
its own on the driver, so both M906 and M913 send the resulting current in mA; only M917 has a
message of its own.

**M915's driver bitmap is set here, not taken from the code.** Its parameter table names the bitmap
`d` in lowercase precisely so `FromCode` never picks it up from a G-code — it is the board's own
driver numbering, which only this side can work out after grouping the drivers by board.

**M593 writes the configuration, not the impulses.** `move.shaping` carries type, frequency and
damping; the amplitudes and delays are the motion engine's to derive. Naming a frequency or damping
without a type switches the shaper on, as in RRF.

### Phase 4 (M665, M666, M667, M669, M671)

**M667 is retired in RepRapFirmware itself**, which replies "M667 is no longer supported - use M669
instead" and treats it as an error. Ported verbatim rather than reimplemented.

**M669 K has its own numbering.** The K values are RepRapFirmware's `KinematicsType` enum, which is
ordered differently from the object model's `KinematicsName`. The mapping is spelled out rather than
derived from either enum, because it is part of the interface a config.g depends on.

**Selecting a geometry replaces the object model instance.** Several geometries share one class and
differ only by name, so `Kinematics.Create` was added to the object model - `Name` has a protected
setter, so only something inside that hierarchy can apply it. Selecting a named core geometry also
writes the matrix that name implies, because the matrix is what the planner uses and the name is what
everything else reads; leaving them disagreeing would describe a machine that does not move the way
it says it does.

**M669's S and T are only read for geometries that do not use those letters.** SCARA and polar both
take S and T for their own parameters, so treating them as segmentation everywhere would silently
capture them.

**Hangprinter is rejected rather than half-supported.** `Kinematics.Create` makes the object model
instance, but M669 reports that hangprinter is not supported: its anchors need array handling the
other geometries do not, and `MotionParameters.BuildHangprinter` has not been exercised. Saying so is
better than accepting the parameters and behaving as something else.

**M673, M674 and M675 moved to phase 5.** M673 needs every axis homed, and M674 and M675 need probe
points, so all three depend on the endstop and probe work rather than on geometry.

### Phase 3 (M82, M83, M114, M120, M121, M220, M221, M290, M425, M556, M564)

**M120/M121 keep their stack outside the object model.** What they save is how the next code will be
read - feed rate, relative flags, units, selected plane - not anything about the machine, so it is
transient state and rule §1.1 exempts it. `InterpreterStateStack` holds one stack per channel and
maintains `inputs[].stackDepth`, which is the part the object model does carry. RepRapFirmware does
not expose the saved values either. The depth is capped at 10, because a macro looping over M120
without a matching M121 would otherwise grow it without bound.

**M290 now has an effect rather than only being recorded.** The offset is added to the target of
every move built afterwards and taken back off in `CommitPositions`, so it shifts where the machine
goes without appearing in the reported coordinates - which is what makes it adjustable during a
print. This differs from RepRapFirmware in one respect worth knowing: RRF applies a change as a small
move of its own, so it takes effect immediately, whereas here it takes effect on the next commanded
move.

**M221 requires D for now.** Without it the code applies to the extruders of the current tool, and
there is no tool subsystem. Reporting "No tool selected" is what RRF does in the same situation, and
is better than silently applying the factor to every extruder.

**M114 reports positions the machine has been commanded to**, not measured ones. `axis.stepPos` is
whatever the object model holds; the live motor counts the engine could report are not plumbed
through to it yet, so the `Count` field is only as good as that.

### Audit against "the object model must recreate the machine"

Rule §1.1 was tightened after the phase 2 review, and three ports failed it.

**M569 stored nothing.** I had reasoned that the driver's configuration belongs to the board that
acts on it, so sending it over CAN was enough. That was wrong, and the object model already said so:
`DriverConfig`'s summary reads "Configured (M569) settings of a driver". It now records direction,
enable polarity, mode, off time, blanking time, the stealthChop and coolStep thresholds, current
scaler, spreadCycle hysteresis and step timings, writing only the parameters the code carried so a
code setting one thing does not reset the rest. `DriverConfig` was extended for all but the first two.

**M915 stored nothing.** Stall detection had no home at all, so `DriverStallDetection` was added under
`boards[].drivers[].config.stallDetection`, holding threshold, filter, minimum speed, coolStep and
whether a stall raises an event.

**M572 stored a third of itself.** `S` may carry two coefficients with `L` naming the extrusion speed
they transition at, and only the first was kept. `pressAdv.K0`, `.K1` and `.D` are now all written.
The CAN message still carries only the first coefficient — the wider one is not ported — so the
second and its transition point are held here alone, which is exactly what the rule is for.

Both codes create the board and driver entries on demand: config.g configures drivers before the
boards carrying them have necessarily announced themselves, so waiting for an announcement would
lose the configuration.

### Removing the DSF/RRF split

**`Link/Channel` is gone** (`Processor`, `Manager`, `StackState` — 1,444 lines). It maintained a
second per-channel stack that mirrored the code pipeline's own, plus the RRF-specific state around
it: waiting-for-acknowledgement levels, start codes, lock/unlock requests, suspended codes and reply
routing. Most of it was already dead — `DoFirmwareCode`, `MacroFileClosed`, `MessageAcknowledged`,
`PrintPaused`, `WaitForAcknowledgement` and `ResourceLocked` had no callers at all.

**The flush machinery did not need rewriting.** `PipelineStackItem` already implements flush natively
with an idle event per stack level, and `ChannelProcessor.FlushAsync` already walks every stage. Only
the Firmware stage delegated elsewhere, so deleting that stage *was* the replacement. What did need
writing was `ChannelProcessor.AbortAllFilesAsync` and `CodeProcessor.GetCurrentFile`, which are the
two things callers actually wanted from the old channel processor.

**Macro execution is not wired up at all.** `FileFactory.CreateMacro(virtualFile, physicalFile, …)`
has no callers and nothing calls `MacroFile.Start()`; the only macro ever constructed was the *copy*
made when forking a channel. Macros used to be opened because RepRapFirmware asked for one over SPI,
and nothing asks now. This is why deleting the channel stack broke no working behaviour — and it
means **M98 is not implemented**, despite being marked ✅ in §5.9: it only handles the `R` parameter.
The same gap means M24 cannot run `resume.g` and M0/M1/M2 cannot run `stop.g`. Wiring macro execution
DSF-side is a prerequisite for a usable machine and is not covered by any phase in §7 yet.

**What the absorbed codes now do.** M0/M1/M2 cancel the job and return, but the machine half — heaters
off, spindles off, motors idle — belongs to subsystems that are not ported. M409 answers for every
object model key rather than only `network`/`plugins`/`sbc`/`volumes`, because there is only one
object model now. M122 always reports DSF diagnostics. M550 writes `network.name`. M581 without the
expression form can only drop a DSF-managed trigger and reports that the plain form is unsupported.

**Unsupported codes error rather than pass through.** `Code.ResolveAsUnsupported` produces
`Unsupported command: <code>` with `MessageType.Error`, matching RepRapFirmware's wording. Note the
consequence: every ⬜ row in §5 is now a *visible* error at runtime rather than a silent no-op, which
is the point, but it also means a config.g written for RRF will report a lot of errors until the
remaining phases land.

### Expansion board manager

`Link/Expansion/ExpansionBoardManager.cs` receives what the boards broadcast and writes it to the
object model. It is a `BackgroundService` with a bounded queue: `LinkService` recognises the report
types and hands over raw payloads on the dispatch thread, and decoding plus the object model write
lock happen on the manager's own task. The queue drops the oldest entry when full, because these are
periodic reports where the newest is worth more than a backlog, and because blocking the dispatch
thread would stall move completions and message output.

| Report | Object model |
|---|---|
| `AnnounceV0` / `AnnounceV1` | `boards[]` — short name, firmware version and date (split from the `type|version|date` string Duet3Expansion sends), unique id, `maxMotors`, `state` |
| `BoardStatusReportV0` / `V1` | `boards[].vIn`, `.v12`, `.mcuTemp` (min/current/max), `.freeRam` |
| `DriversStatusReport` | `boards[].drivers[].status` |
| `SensorTemperaturesReport` | `sensors.analog[].lastReading`, `.state` |
| `HeatersStatusReport` | `heat.heaters[].current`, `.avgPwm`, `.state` |
| `FansReport` | `fans[].actualValue`, `.rpm` |
| `InputStateChangedV1` / `V2` | `sensors.gpIn[].value` |
| `Event` | logged as a warning |
| `DebugText` | logged at debug level |

Two things it deliberately does not do yet. `FilamentMonitorsStatusReportV2` is logged but not
applied: `sensors.filamentMonitors[]` is keyed by extruder, and nothing populates that mapping until
M591 is ported. Endstop and Z probe handles in the input messages are skipped for the same reason —
M574 and M558 have not created those sensors.

The readings in these messages are positional, not indexed: a board status report packs only the
values it has, in a fixed order, and the temperature/heater/fan reports pair the n'th value with the
n'th set bit of a bitmap. Both are handled the way the boards expect; getting either wrong attributes
a reading to the wrong device rather than failing.

The V1 and V2 input messages have different per-handle entry sizes, so each is deserialized as
itself. Reading one as the other shifts every handle silently.

---

## 9. Macro files

Macros were opened because RepRapFirmware asked for one over SPI. Nothing asked once that link was
removed, so `MacroFile` — which is complete, and reads, executes, error-handles and aborts on its own
— was never started by anything. [MacroRunner.cs](../../src/DuetControlServer/Files/MacroRunner.cs)
is the missing piece: it pushes a stack level onto the channel's pipeline, starts the macro, waits for
it and pops the level again. Running on its own level is what makes a flush inside a macro wait for
the macro's codes rather than for whatever started it, and what lets macros nest without interleaving.
Nesting is capped at 10 levels, as RepRapFirmware caps its own stack.

### The macros RepRapFirmware runs

Every macro RRF invokes, taken from its `DoFileMacro` call sites and the filename constants in
`GCodes.h`.

| Macro | What runs it | Status |
|---|---|---|
| `config.g`, falling back to `config.g.bak` | Boot | ✅ run on the trigger channel when the link comes up |
| `runonce.g` | After config.g, then deleted | ✅ |
| `config-override.g` | M501 | ✅ M501 implemented |
| `dsf-config.g` | After the plugins start | ✅ already issued as `M98 P"dsf-config.g"`, which now works |
| `<letter><number>[.<fraction>].g` | Any code no handler recognises | ✅ see below |
| Any file | `M98 P"..."` | ✅ |
| `start.g` | Start of a job | ⬜ needs a job lifecycle hook |
| `stop.g` | M0, M2 | ⬜ |
| `cancel.g` | A job being cancelled | ⬜ |
| `pause.g` | M25 and pause requests | ⬜ M25 is not implemented |
| `resume.g` | M24 resuming a paused job | ⬜ |
| `filament-change.g` | M600 | ⬜ |
| `resurrect.g`, `resurrect-prologue.g` | M916 after a power fail | ⬜ |
| `daemon.g` | Repeatedly on the daemon channel | ⬜ |
| `trigger<n>.g` | An external trigger firing (M581) | ⬜ |
| `network-override.g` | Network configuration | ⬜ |
| `homeall.g`, `home<axis>.g` | G28 | ⬜ blocked: no endstops |
| `homedelta.g`, `homebed.g`, `homeradius.g`, `homeproximal.g`, `homedistal.g`, `home5barscara.g` | G28 on those kinematics | ⬜ blocked: kinematics and endstops |
| `bed.g` | G32 | ⬜ blocked: no probes |
| `mesh.g` | G29 | ⬜ blocked: no probes |
| `deployprobe<n>.g`, `retractprobe<n>.g` | M401, M402 and probing moves | ⬜ blocked: no probes |
| `tfree<n>.g`, `tpre<n>.g`, `tpost<n>.g` | T-codes | ⬜ blocked: no tool subsystem |
| `filaments/<name>/load.g`, `unload.g`, `config.g` | M701, M702, M703 | ⬜ blocked: no tool subsystem |

### A code no handler recognises runs a macro named after it

This is how a machine adds a code of its own in RepRapFirmware, and it is why §2's change had to be
finished rather than left at "unsupported". `GCodes::TryMacroFile` looks for
`sys/<letter><number>.g`, or `sys/<letter><number>.<fraction>.g` when the code has a fraction, and
only reports the code unsupported if there is no such file. `Code.TryRunCodeMacroAsync` does the same.

The reply when there is no such macro now matches RRF exactly: `<code>: Command is not supported`, as
a **warning** rather than an error. The earlier `Unsupported command: <code>` error was mine and was
wrong on both counts.

One difference remains: RRF passes the code's own parameters into the macro as variables
(`DoFileMacroWithParameters`), so `M1234 X5` can read `param.X`. That needs variable plumbing through
`MacroFile` and is not done, so a macro implementing a code cannot see its parameters yet.

### Consequences worth knowing

M98 previously did nothing but handle its `R` parameter, so **every macro-based feature of a machine
was silently inert**. It now runs the file, which means config.g takes effect and the rest of the
inventory above becomes reachable rather than dead. Expect a machine's config.g to report errors for
the codes still on the ⬜ list in §5 — that is the visible-error change from §2 doing its job, not a
regression in macro handling.

---

## 10. Endstops: stopping a move short

Phase 5's architecture, written down here because it spans four components and no one of them shows
the whole shape.

RepRapFirmware does all of this on one board: it generates the steps, so it knows when an endstop
fired and where every drive was at that instant. Here the drives are on CAN-connected expansion
boards, DuetSbcInterface plans the motion, DuetCANMaster bridges SPI to CAN, and no single component
knows all of it. What follows is what that forces.

### The stop is decided on the controller

An expansion board reports an input change as `CanMessageInputChangedV2`. The move has to stop *now*:
an axis at 100 mm/s covers a millimetre every 10 ms, so a round trip out to DuetControlServer and
back would overrun the endstop visibly.

So **DuetCANMaster decides the stop**, being the only component close enough to the bus. It needs no
notion of what an endstop is: each move tells it which input stops which driver, and it matches an
incoming change against that directly. The message is still forwarded to DCS, because the object
model has to see the input change whether or not anything was moving.

An endstop already triggered when the move starts is handled as RepRapFirmware handles it - the
driver is simply given no steps before the move goes out (`StopDriverWhenProvisional`).

### The stop identity travels with the move, per driver

Each driver in a move carries the CAN address and `RemoteInputHandle` of the input that stops it -
exactly the two fields that arrive in `CanMessageInputChangedV2`, so matching needs no lookup table.

**Per driver rather than per move.** That is what lets one move home several axes at once, each
stopping on its own endstop, and what stops a driver that watches nothing from being stopped by its
neighbour's endstop.

| Stage | Where it lives |
|---|---|
| DCS plans the move | `RawMove.StopOnInput[drive]` |
| DCS → DuetSbcInterface | `MoveParams` third trailing array, `stopOnInput[numDrives]` |
| Held while queued | `DDA::m_stopOnInput[]` |
| DuetSbcInterface → DuetCANMaster | `ScheduleMoveDriver::stopOnBoard` and `stopOnHandle` |

`Motion::kNoStopInput` and `SbcProtocol::NoEndstopBoard` are the sentinels; every non-endstop move
carries them.

### Stopping and correcting are different jobs

The controller stops the drives but cannot say where they should *end up*: it never generated the
steps and does not know how far each had travelled. Undoing the overshoot needs the position at the
instant the endstop fired, which only DuetSbcInterface can answer - it planned the motion and
evaluates the same segment chain anyway to report live positions (`Motion::DriveTracker`).

```
board          controller                        DuetSbcInterface
  |                |                                    |
  |-- InputChanged->|                                   |
  |                 |-- stop matching drivers           |
  |<- StopMovement -|                                   |
  |                 |-- MotionStopped (SPI) ----------->|
  |                 |                    position at whenTriggered
  |                 |                    correct DriveTracker + DDA endpoint
  |<---------------- CanMessageRevertPosition ----------|
```

`FirmwareRequest::MotionStopped` carries the trigger timestamp and the stopped drivers. It was
already reserved in the protocol with no struct and an empty `case`; `MotionStoppedHeader` and
`MotionStoppedDriver` fill it in.

**Why the calculation is not in DuetControlServer.** Considered and rejected: nothing in
`DuetControlServer/Motion` evaluates a velocity profile at a timestamp - `MoveBuilder` tracks
endpoints and live positions come *from* the engine - so putting it there means reimplementing
`DriveTracker`'s segment evaluation in C#, or calling back into native for it. And the native
correction is required regardless, because DCS plans each move as a delta from the engine's
endpoints, so emitting the revert from the same place keeps one operation atomic rather than opening
a window where the trackers and the boards disagree.

The cost is worth knowing: this is the **only** native-originated CAN message. Every other one goes
DCS → `DuetSbc_QueueCanMessage` → link, and that invariant is why DuetSbcInterface had no CANlib
dependency before this work.

**The correction has to reach the DDA, not just the tracker.** `MotionService::OnMoveRetired` reports
`dda.DriveCoordinates()` for endstop moves, which is `DDA::m_endPoint` - the *planned* endpoints.
Correcting only the tracker would leave DCS being told the move finished where it intended, planning
the next move from a position the machine was never at. Silent, and it would present as a homing
offset. `HandleMotionStopped` therefore also calls `DDA::SetDriveCoordinate`, finding the move by
scanning the rings for one with `IsCheckingEndstops()` - an endstop move is always isolated, so at
most one can be running.

### What the controller no longer does

`DriversStopList::stopSteps` and the whole revert path - `RevertStoppedDrivers`,
`FinishedStoppingDrivers`, `revertAll`/`revertedAll`, `sentRevertRequest` - were inherited from
RepRapFirmware, had no callers, and are gone. `GetUrgentMessage` now only sends stop messages, and
`StopDriverWhenExecuting` no longer takes a step count.

### Building CANlib for the SBC

DuetSbcInterface needs the CAN message definitions so both ends of the link describe a message with
the same declaration. `lib/CANlib/CANlib.cmake` gained an `MCU HOST` variant mirroring the one in
`RRFLibraries.cmake`. Two things were not obvious:

- **`-fsingle-precision-constant` must stay on.** It is not a diagnostic flag: it decides whether a
  literal like `0.01` is float or double. Dropping it changes what `HeaterModel` computes relative to
  the firmware, and fills the build with `-Wdouble-promotion` warnings saying so. `-nostdlib` and
  `-Werror` *are* dropped for HOST, because those affect linking and diagnostics rather than results.
- **`float16_t` must stay 16 bits.** `Compat/Float16Compat.h` maps it to `float`, which is right for
  RRFLibraries - there it appears only in a field that never crosses the link. CANlib puts it *in the
  messages* (`ShortMinCurMax`, pressure advance), so widening it changes a message's size and trips
  CANlib's own "CAN message too big" assertion. `Compat/CoreN2G/CoreTypes.h` resolves it to
  `_Float16`, or `__fp16` where that is the available spelling. That header is itself a compat shim:
  CANlib includes it, it comes from CoreN2G, and building all of that for one header of typedefs
  would be the wrong trade.

### Known limits

- **`CanMessageInputChangedV1` carries no timestamp.** Only V2 has `GetWhen`. A board on the older
  message is stopped where the message found it and keeps its overshoot. Inherent to the format.
- **`HandleMotionStopped` is not covered by tests.** It needs a `MotionService`, populated rings and
  a link, which the harness does not stand up. The ring scan and the forced DDA endpoint are verified
  by inspection only, and are the first things to exercise on hardware.
### Reaching it from a G-code

M574 configures the endstops and asks the board carrying each port to watch it
(`CanMessageCreateInputMonitorV1`); without that request the input is never reported at all, so an
endstop that is configured but not monitored would silently never trigger. `G1 H1`, `H3` and `H4` set
`CheckEndstops` and fill in `RawMove.StopOnInput` for the axes the code actually mentions - a homing
move naming X and Y must not be stopped by Z's switch happening to be closed already. M119 reports
the states, which the board manager keeps current from the same `InputChanged` messages the
controller acts on.

### The four stop actions

RepRapFirmware picks one of four actions when an endstop fires, and which one it picks depends on the
**geometry**, not on the endstop:

| RRF action | When | Here |
|---|---|---|
| `none` | The axis has no endstop | No stop input is written, so nothing watches |
| `stopAxis` | The axis is independently driven | Its drive carries the endstop's input; the controller stops that axis' drivers |
| `stopAll` | Moving the axis needs drives other than its own | **Every** drive in the move carries that one input, so whichever driver sees the change first, they all stop |
| `stopDriver` | The axis has as many switches as drivers | Each drive entry carries the same board but a handle whose minor field is the driver's index, so each driver stops on its own switch |

`stopAll` is the one that matters for correctness rather than tidiness. On a CoreXY, holding X still
needs both motors, so stopping only "X's drivers" would leave the other motor running and drag the
head diagonally into the switch. The test is exactly RepRapFirmware's, from
`SwitchEndstop::PrimeAxis`: the axis needs `stopAll` if its controlling drives include anything other
than itself. `KinematicsEngine.GetControllingDrives` already answered that question for the planner.

Because a drive can carry only one stop input, a `stopAll` axis cannot be armed alongside anything
else - the second endstop would have nowhere to live. `G1 H1` rejects that combination rather than
silently arming one of them, which also matches how a CoreXY `homeall.g` is written in practice: the
coupled axes are homed one at a time.

### `stopDriver`: a switch per driver

`stopDriver` is what squares a gantry. An axis driven by two motors is given two switches -
`M574 Y1 S1 P"1.io1.in+1.io2.in"` - and each motor runs on until it reaches *its own* switch, so a
gantry that started skewed ends up square against the two switches. Stopping both motors on the first
trigger would preserve the skew, which is why this is a separate action rather than a variation of
`stopAxis`.

RepRapFirmware pairs port *i* with driver *i* of the axis and chooses `stopDriver` only when the two
counts are equal (`SwitchEndstop::PrimeAxis`); any other count falls back to stopping the whole axis
on the first trigger. That fallback is what makes a dual-motor axis with one switch safe, because the
motor with no switch of its own would otherwise never stop. Both rules are reproduced here.

A move carries the switches per drive as a `MoveStopInput`, which is `SwitchEndstop` reduced to what a
move needs:

| Field | Meaning |
|---|---|
| `handle` | The `RemoteInputHandle` the switches are registered under, minor field zero |
| `numSwitches` | 0 = this drive watches nothing; 1 = every driver watches `boards[0]`; n = driver *i* watches `boards[i]` |
| `boards[]` | CAN address of each switch, in driver order |

Only the board differs from one switch of an axis to the next - the handle follows from which switch
it is, because `RemoteInputHandle`'s minor field is the switch index and RepRapFirmware derives it the
same way. `StopInputForDriver` rebuilds the pair as `DDA::Prepare` emits each driver's movement, and
M574 registers one input monitor per port under the matching handle, so the handles a move names are
the ones the board is already watching. **The switches of an axis may be on different boards**, as
they may in the firmware: each carries its own CAN address.

One rule does not come from RepRapFirmware's endstop code at all: **`stopAll` outranks `stopDriver`**.
On coupled kinematics the drive's entry is rewritten to the axis' first switch before it is copied to
every drive, for the same reason RepRapFirmware's `stopAll` test comes first - waiting for each motor's
own switch would leave the coupled drives running.

The drive tracker complicates matters slightly. Adopting a stopped driver's position freezes the
tracker, and the tracker is exactly what tells the *remaining* drivers where they were when their own
switch fired - freezing it on the first trigger would revert the second motor to the first motor's
position and undo the squaring. So `DDA::NoteDriverStopped` records which drivers of a drive have
stopped, and the position is only adopted once the last of them has. Each driver's
`CanMessageRevertPosition` is still computed as it is reported, from the live tracker at that driver's
own trigger time.

Not carried over: RepRapFirmware sets the axis to its low or high limit when the *last* switch of a
`stopDriver` axis fires (`setAxisLow` / `setAxisHigh`). Nothing sets an axis position from an endstop
here yet - that belongs with G28, which is the next step.

`Motion/RemoteEndstops.cs` holds the naming the three places have to agree on: M574 when it asks for
the monitor, the move when it says what stops it, and the receiver when a change comes back. They
agree because the handle is derived from the axis rather than allocated, so nothing has to remember
or look up an allocation.

### The Z probe (M558, G31, M401, M402, M851)

A probe here is an input monitor with a trigger height attached. RepRapFirmware's `ZProbe` hierarchy
exists mostly to separate probes on the main board from probes on an expansion board; only the second
kind exists here, so what is left is `RemoteZProbe` and there is no hierarchy. `Motion/RemoteProbes.cs`
derives the handle from the probe number the same way `RemoteEndstops` derives one from the axis.

The types an expansion board can express are RepRapFirmware's: 1 (analog), 8 (unfiltered digital),
9 (BLTouch) and 11 (scanning analog), plus 0 (none) and 10 (motor stall), which watch no input at all.
Anything else is refused with the reason rather than accepted and quietly ignored.

Everything a probe knows is in `sensors.probes[]`, including the port, so a machine can be rebuilt
from the object model. Two fields were added for that: `port`, for the same reason the endstop gained
one, and `sensor`, which is the temperature sensor G31 H names. RepRapFirmware keeps the sensor on the
probe but neither reports nor saves it; without it, a machine that used temperature compensation could
not be recreated from the object model alone.

M401 and M402 run `deployprobe<K>.g`, falling back to `deployprobe.g`. RepRapFirmware passes the probe
number to the unnumbered macro in a `K` variable; meta G-code variables are not ported, so the
unnumbered macro runs without it. `deployedByUser` behaves as it does in RepRapFirmware - it is what
stops a probe the user deployed on purpose from being retracted by something else.

### Bed compensation (M557, M374, M375, M376, M561)

`move.compensation.probeGrid` is what M557 defines and `move.compensation.liveGrid` is what is
actually loaded; they differ whenever a map was measured over a grid that has since been redefined.
The heights themselves are in `heightmap.csv` rather than the object model, which is where
RepRapFirmware keeps them and where Duet Web Control reads them from.

`Motion/HeightMap.cs` reads and writes that file, including the two older label lines, so a bed
measured before this migration reloads afterwards. A point that was never probed is a bare `0` with no
decimal point, which is how the format distinguishes it from a measurement of zero; that distinction
is kept on the way in and on the way out, so a partial map stays partial.

`Motion/BedCompensation.cs` holds the loaded map and produces the Z correction.
`GCodeHandler.BuildRawMove` adds it and `CommitPositions` takes it back off, in the same place
babystepping is added and removed, so a client reads back the coordinate it asked for. The taper is
what makes the second of those more than a subtraction: below the taper height the correction is
scaled by how far up it the move is, so inverting it means solving for the requested height rather
than subtracting the correction. Both directions are RepRapFirmware's `BedTransform` and
`InverseBedTransform`.

### Homing (G28 and the `G1 H` moves inside it)

A homing move is an ordinary move that the controller cuts short. What makes it homing is what happens
afterwards: `GCodeHandler.FinishHomingMoveAsync` waits for the move, resynchronises the planner from
the engine's snapshot - which is where the drives actually are after the revert, not where the move was
planned to end - and then sets each axis that triggered to the coordinate of its switch. Only an axis
whose endstop actually triggered is homed, so a move that ran its full length leaves the axis unhomed
rather than confidently wrong.

Every other move is committed at its planned endpoint and the next code interpreted immediately, which
is what keeps the queue full. A homing move is the exception because it is where the machine finds out
where it is.

G28 itself knows nothing about homing. The machine's macros do, and G28 runs them: ask the kinematics
which macro comes next, run it, see which axes it homed, ask again. That loop is RepRapFirmware's
`homing1` and `homing2` states, and it is a loop rather than a list because a macro may home more axes
than it was asked for. A pass that homes nothing ends with an error, or a missing switch would spin
forever.

`KinematicsEngine.GetHomingFileName` carries the rules: `homeall.g` for everything, `home<letter>.g`
for the lowest axis otherwise, and a lower-case letter written as `home'a.g`. Homing Z with a probe
means driving the nozzle at the bed, so it waits until the axes named by `AxesToHomeBeforeProbing` are
homed - X and Y usually, all three towers on a delta, which has no axis that moves a motor of its own.
Delta homes every tower whichever axis was asked for, SCARA names its macros after the arm joints, and
polar names the radius arm.

### Probing (G30 and G29)

A probing move is a homing move armed on a probe handle instead of an endstop handle, so the mechanism
is the one the endstop work already built. Every drive watches the probe rather than only Z's: on a
delta the effector only comes down because all three towers do, so stopping one would tip it.

Around that is the tapping loop. A probe does not give the same answer twice, so G30 taps until two
consecutive readings agree within `M558 S`, and averages the two that agreed - earlier taps were the
probe settling and would drag the average towards them. Running out of taps is not an error; the mean
of what was collected is used, as in RepRapFirmware.

What G30 does with the result is the S parameter:

| S | Meaning |
|---|---|
| none, or ≤ -4 | The probe is trusted and Z is not, so Z is redefined: the nozzle is at the trigger height now, whatever the axis thought. This is what levels a machine |
| -1 | Report the stopped height and change nothing |
| -2 | Set the tool Z offset - refused, because tools are not ported |
| -3 | Z is trusted and the probe is not, so the probe takes the height the axis says it stopped at. This calibrates a probe against a homed machine |

G29 walks the grid in a serpentine so the head does not fly back across the bed between rows, skips the
points outside a circular grid's radius, and fills those in afterwards by least squares over the points
that were probed - the interpolation reads all four corners of whichever cell a move lands in, so an
unprobed corner would drag the correction to zero right where the bed is furthest from flat. The points
stay marked unmeasured, so the saved file still says which were guessed.

`G29 S1`, `S2` and `S3` are what M375, M561 and M374 already do; G29 says so rather than carrying a
second implementation of each. Without S it runs `mesh.g` if the machine has one, so that a bed needing
preparation can say so, and probes directly only if there is no such file.

### What is left in phase 5

- **G30 P** - probing into the bed levelling and mesh tables, which M671 and the multi-point levelling
  use. The tables themselves are not ported.
- **M585** and **M675** probe against a workpiece rather than the bed; both need G30 P.
- **M558.1** and **M558.2** calibrate a scanning probe, which needs the probe read back over CAN while
  it moves.
