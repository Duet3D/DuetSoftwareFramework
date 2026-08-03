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

1. **The object model is the configuration.** An M-code that configures the machine writes
   `move.axes[]`, `move.extruders[]`, `move.kinematics`, `boards[].drivers[]`, `sensors.*` and so on.
   Nothing keeps a private authoritative copy. If the object model has nowhere to put a value,
   extend the object model (§6) rather than storing it beside it.

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
| 🔵 | **SBC half only** — implemented in `MCodeHandler`, but still defers the machine half to RRF. See §2 |
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

**Consequence for this migration:** with RRF gone there is no second half. Every 🔵 row below needs
its RRF-side behaviour absorbed into DSF, and every `CodeExecutedAsync` hook needs re-examining — the
ones that patch up an RRF reply become dead code once DSF produces the reply itself.

This is additional to the ⬜ rows. Do not read 🔵 as "nearly done".

---

## 3. Progress summary

| Group | ✅ Done | 🔵 SBC half | Rows (excl. ⛔) |
|---|---|---|---|
| §5.1 Motion — drives and axes | 11 | 0 | 26 |
| §5.2 Motion — kinematics and geometry | 0 | 0 | 12 |
| §5.3 Motion — compensation and probing | 0 | 0 | 14 |
| §5.4 Motion — queue, sync and shaping | 0 | 1 | 9 |
| §5.5 Heat | 0 | 0 | 19 |
| §5.6 Fans | 0 | 0 | 3 |
| §5.7 Tools and filament | 0 | 0 | 14 |
| §5.8 Spindles, laser and machine mode | 0 | 0 | 9 |
| §5.9 Job, files and SD | 17 | 4 | 29 |
| §5.10 Network | 4 | 1 | 13 |
| §5.11 I/O, expansion and miscellaneous | 5 | 5 | 38 |
| **Total** | **37** | **11** | **186** |

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
| **Non-Cartesian kinematics not ported** | M665, M666, M667, part of M669 | `MotionParameters.BuildGeometry` falls back to Cartesian for delta, SCARA, polar and hangprinter. Only `CoreKinematicsEngine` exists |

Because of these, **§5.1-§5.4 (motion) is the tractable scope on this branch**; the rest is gated on
subsystems that do not exist yet.

---

## 5. Inventory

RRF line numbers refer to `lib/RepRapFirmware/src/GCodes/GCodes2.cpp`.

### 5.1 Motion — drives and axes

| M-code | RRF | Purpose | Object model home | Standstill | Status |
|---|---|---|---|---|---|
| M17 | 910 | Motors on | `move.axes[].drivers` → CAN `MultipleDrivesRequestDriverStateControl` | no | ⬜ |
| M18 / M84 | 911 | Motors off, set idle timeout | `move.idle.timeout`, `move.idle.factor` | yes | ⬜ |
| M82 | 1600 | Absolute extruder positioning | `inputs[].drivesRelative = false` | no | ⬜ |
| M83 | 1605 | Relative extruder positioning | `inputs[].drivesRelative = true` | no | ⬜ |
| M85 | 1612 | Set inactive time | `move.idle.timeout` | no | ⬜ |
| M92 | 1615 | Steps per mm | `move.axes[].stepsPerMm`, `move.extruders[].stepsPerMm` → CAN `MultipleDrivesRequestStepsPerUnitAndMicrostepping` | yes | ✅ |
| M114 | 1945 | Report position | reads `move.axes[].machinePosition` / `userPosition` | no | ⬜ |
| M120 | 2210 | Push machine state | `inputs[].stack` | no | ⬜ |
| M121 | 2214 | Pop machine state | `inputs[].stack` | no | ⬜ |
| M201 | 2527 | Axis/extruder accelerations | `move.axes[].acceleration`, `move.extruders[].acceleration` | yes | ✅ |
| M201.1 | 2527 | Reduced accelerations for probing and stall homing | `move.axes[].reducedAcceleration` | yes | ✅ |
| M203 | 2612 | Min/max feedrates | `move.axes[].speed`, `move.extruders[].speed`, `move.minimumMovementSpeed` | yes | ✅ |
| M204 | 2670 | Print/travel acceleration | `move.motionSystems[].printingAcceleration` / `.travelAcceleration` | no | ✅ |
| M205 / M566 | 3782 | Jerk (mm/s and mm/min forms), jerk policy | `move.axes[].jerk` / `.printingJerk`, `move.jerkPolicy` | yes | ✅ |
| M208 | 2701 | Axis minima/maxima | `move.axes[].min` / `.max` | no | ✅ |
| M220 | 2705 | Speed factor override | `move.speedFactor` | no | ⬜ |
| M221 | 2734 | Extrusion factor override | `move.extruders[].factor` | no | ⬜ |
| M350 | 2996 | Microstepping | `move.axes[].microstepping` → CAN `MultipleDrivesRequestStepsPerUnitAndMicrostepping` | yes | ✅ |
| M400 | 3120 | Wait for moves to finish | — (flush and drain) | n/a | ✅ |
| M569 | 3878 | Driver configuration (direction, mode, timings) | `boards[].drivers[]` → CAN generic `M569Params` (+ `.1`/`.2`/`.4`/`.6`/`.7`) | yes | ⬜ |
| M584 | 3956 | Axis/extruder → driver mapping | `move.axes[].drivers`, `move.extruders[].driver` | yes | ✅ |
| M906 | 4377 | Motor currents | `move.axes[].current` → CAN `MultipleDrivesRequestMotorCurrents` | no | ✅ |
| M913 | 4378 | Motor current percentage | `move.axes[].percentCurrent` | no | ⬜ |
| M915 | 4539 | Stall detection | `boards[].drivers[]` → CAN generic `M915Params`, `CanMessageEnableStallEndstop` | no | ⬜ |
| M917 | 4380 | Standstill current percentage | `move.axes[].percentStstCurrent` → CAN `MultipleDrivesRequestStandstillCurrentFactor` | no | ⬜ |
| M970 | 4639 | Phase stepping mode | `move.axes[].phaseStep` → CAN generic `M959Params` | yes | ⬜ |

### 5.2 Motion — kinematics and geometry

| M-code | RRF | Purpose | Object model home | Standstill | Status |
|---|---|---|---|---|---|
| M290 | 2812 | Babystepping | `move.axes[].babystep` | no | ⬜ |
| M425 | 3223 | Backlash compensation | `move.axes[].backlash`, `move.backlashFactor` | yes | ⬜ |
| M556 | 3653 | Axis skew compensation | `move.compensation.skew` | no | ⬜ |
| M579 | 3925 | Scale Cartesian axes | needs new field — §6 | no | ⬜ |
| M665 | 4052 | Delta configuration | `move.kinematics` (`DeltaKinematics`) | yes | ⬜ blocked |
| M666 | 4082 | Delta endstop adjustments | `move.kinematics` (`DeltaKinematics`) | yes | ⬜ blocked |
| M667 | 4099 | CoreXY mode (legacy, superseded by M669) | `move.kinematics` (`CoreKinematics`) | yes | ⬜ |
| M669 | 4104 | Kinematics selection and parameters | `move.kinematics` | yes | ⬜ only `CoreKinematics` has an engine |
| M671 | 4152 | Z leadscrew positions | `move.kinematics.tiltCorrection` | no | ⬜ |
| M673 | 4168 | Align plane on rotary axis | `move.rotation` | yes | ⬜ |
| M674 | 4275 | Set Z to centre point | — | yes | ⬜ |
| M675 | 4311 | Find centre of cavity | — | yes | ⬜ |

### 5.3 Motion — compensation and probing

| M-code | RRF | Purpose | Object model home | Standstill | Status |
|---|---|---|---|---|---|
| M119 | 2206 | Report endstop status | `sensors.endstops[]` | no | ⬜ blocked |
| M374 | 3089 | Save height map to file | `move.compensation.file` | no | ⬜ |
| M375 | 3093 | Load height map and enable compensation | `move.compensation` | no | ⬜ |
| M376 | 3102 | Set taper height | `move.compensation.fadeHeight` | no | ⬜ |
| M401 | 3131 | Deploy Z probe | `sensors.probes[]` | no | ⬜ blocked |
| M402 | 3144 | Retract Z probe | `sensors.probes[]` | no | ⬜ blocked |
| M557 | 3686 | Probe grid definition | `move.compensation.probeGrid` | no | ⬜ |
| M558 | 3690 | Z probe type/configuration; `.1`/`.2` scanning probe calibration | `sensors.probes[]` | no | ⬜ blocked |
| M561 | 3730 | Identity transform, disable height map | `move.compensation.type` | no | ⬜ |
| M574 | 3897 | Endstop configuration | `sensors.endstops[]` | no | ⬜ blocked |
| M577 | 3919 | Wait for endstop trigger | `sensors.endstops[]` | no | ⬜ blocked |
| M585 | 3960 | Probe tool | `sensors.probes[]`, tools | yes | ⬜ blocked |
| M672 | 4164 | Program Z probe | CAN to the probe's board | no | ⬜ blocked |
| M851 | 4357 | Z probe offset (Marlin compatibility) | `sensors.probes[].offsets` | no | ⬜ blocked |

### 5.4 Motion — queue, sync and shaping

| M-code | RRF | Purpose | Object model home | Standstill | Status |
|---|---|---|---|---|---|
| M572 | 3891 | Pressure advance | `move.extruders[].pressureAdvance` → CAN `MultipleDrivesRequestPressureAdvanceV1` | yes | ⬜ |
| M592 | 3992 | Nonlinear extrusion | `move.extruders[].nonlinear` | yes | ⬜ |
| M593 | 3997 | Input shaping | `move.shaping` → CAN `CanMessageSetInputShapingV1` | yes | ⬜ |
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
| M564 | 3758 | Limit axes / allow movement before homing | `move.limitAxes`, `move.noMovesBeforeHoming` | ⬜ |
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
2. **Phase 2 — driver detail.** M17, M18/M84, M85, M569, M913, M915, M917, M970, M572, M593, M592.
   All of these are CAN-message-shaped and the generated helpers already exist.
3. **Phase 3 — interpreter state and reporting.** M82, M83, M114, M120, M121, M220, M221, M290, M425,
   M556, M564.
4. **Phase 4 — geometry.** M667, M669 (Cartesian and CoreXY first), M671, M673-M675; then the delta
   kinematics engine and M665/M666.
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
