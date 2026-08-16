# Cleaning up the DuetSbcInterface motion engine

Plan for removing the RepRapFirmware compatibility scaffolding from
[`src/DuetSbcInterface`](src/DuetSbcInterface) and leaving behind code that reads as this project's
own — while keeping the feature switches that still mark real, intended work.

Baseline for everything below: commit `7fd2169`, `cmake --preset native` configures and builds clean
and `ctest` passes 10/10.

**Status: done.** Two things landed differently from what is written below, and the text is left as
it was so the reasoning stays readable:

- `src/Movement/` and `src/Motion/` are **one directory**, `src/Motion/`. Keeping them apart drew a
  line between "imported" and "written here" that stopped meaning anything once the import stopped
  being re-synced, and the names gave a reader no way to guess which held what. Paths below that say
  `src/Movement/` describe the tree as it was.
- §6.2's CI job is **not** added; see that section.

---

## 1. What this pass does and does not give up

`src/Movement/` was imported from RepRapFirmware and much of what surrounds it — `src/Compat/`, the
`CanMotion` namespace, the `reprap` global — exists so the import can be re-*merged* against a future
RRF release rather than diffed against it.
[`Compat/RepRapFirmware.h`](src/DuetSbcInterface/src/Compat/RepRapFirmware.h) states it outright:
*"will be re-synced against it, so they keep their original `#include` lines"*.

**The textual merge is already gone** — the whole-tree rename to `m_`-prefixed members ended it, and
every disabled branch that was kept "so those branches still have to compile" no longer does. So the
scaffolding whose only job is to preserve upstream's *spelling* (§3) goes.

**The feature switches are a different thing** and are treated differently. A switch that marks work
this project intends to do is a useful marker; a switch that marks a decision already taken is noise.
§2 splits them on exactly that line, per the disposition agreed for each one.

What does not change either way: the algorithms. `DoLookahead`, `RecalculateMove`, `MatchSpeeds`,
`AddSegment`'s list algebra and `Prepare`'s structure stay as they are. This is a readability and
naming pass, not a rewrite of the motion maths.

---

## 2. Phase 1 — feature switches

### 2.1 Delete outright — nothing behind them

Ten switches are referenced only by their own `#define`, or guard a block that is already empty.
Delete the definition and any block.

`SUPPORT_IOBITS`, `SUPPORT_SCANNING_PROBES`, `SUPPORT_PHASE_STEPPING`, `SUPPORT_CLOSED_LOOP`,
`SUPPORT_REMOTE_COMMANDS`, `SUPPORT_COORDINATE_ROTATION`, `HAS_SMART_DRIVERS`, `HAS_STALL_DETECT`,
`HAS_VOLTAGE_MONITOR`, `HAS_SBC_INTERFACE`.

Three of these guard *empty* `#if`/`#endif` pairs, which is all there is to delete:
`DDARing.cpp:472` (`HAS_VOLTAGE_MONITOR || HAS_STALL_DETECT`), plus the `#if 0 //SUPPORT_REMOTE_COMMANDS`
block at `MoveSegment.h:125` (`MoveSegment::IsRemote()`, reading an `isRemote` flag bit that does not
exist).

Also delete `USE_DOUBLE_MOTIONCALC` (0): `motioncalc_t` becomes `using motioncalc_t = float`, keeping
the "must round the same way as the boards" comment, which is the part that matters.

### 2.2 Delete the switch, keep the taken path

**`SUPPORT_CAN_EXPANSION`** is 1 and structurally cannot be 0: this SBC owns no driver, so every drive
is remote by definition. Delete the guards and inline the enabled path in `DDA.cpp`, `DDA.h`,
`DDARing.cpp` and `MoveTiming.h`. State the reason once, in `MotionSystem.h`'s header comment, instead
of ten times as a preprocessor condition.

### 2.3 Keep, and fix what is mechanically fixable

Four switches stay because each marks real intended work. I flipped each one on and compiled
`duet_motion` to find out what is actually wrong behind it. The results decide how far each can be
taken:

| Switch | Errors when enabled | Mechanical (naming, `m_` prefixes, `oneHalf`/`OneHalf`) | Genuinely missing | Outcome |
| --- | --- | --- | --- | --- |
| `SUPPORT_ASYNC_MOVES` | **0** | — | — | Already healthy; stays 1 |
| `SUPPORT_NONLINEAR_EXTRUSION` | 6 | 4 | `NonlinearExtrusion` type, `MotionSystem::GetExtrusionCoefficients` | **Implemented and defaulted to 1** — §2.3.1 |
| `SUPPORT_LASER` | 16 | 2 | `GCodesShim::GetMachineType`, `MachineType`, `Pwm_t`, `DDA::laserPwmOrIoBits` | Naming fixed; stays 0 |
| `SUPPORT_S_CURVE` | 36 | ~25 | `MovementProfile` (+ its header), `PrepParams`' 7-phase fields, `afterPrepare.peakAcceleration/peakDeceleration`, `DDA_3rdOrder.cpp`'s function bodies | Naming fixed; stays 0 |

**`SUPPORT_ASYNC_MOVES` is already healthy.** I built with it set to 0: clean build, 10/10 tests. Both
paths work today. Keep the switch; the only change is deleting the two *empty* `#if SUPPORT_ASYNC_MOVES`
/ `#endif` pairs (`DDA.cpp:271`, `DDARing.cpp:119`) that mark where upstream code used to be. Because
both paths compile, this one can be **locked in by CI** — see §6.2.

**`SUPPORT_NONLINEAR_EXTRUSION` is implemented and turned on by default** — see §2.3.1. Of its six
errors, four are naming (`totalDistance`, `flags`, `directionVector`, `clocksNeeded` → `m_`-prefixed);
the other two are the missing type and accessor, and the rest of the path already exists on both sides.

**`SUPPORT_LASER` cannot reach zero.** Two errors are naming (`topSpeed`, `requestedSpeed`); the rest
need a laser PWM field on `DDA`, a `Pwm_t`, a `MachineType` enum and a machine-type accessor — none of
which exist here, and the PWM value would have to travel in `MoveParamsHeader` from DCS. Fix the two
naming errors, delete the two *empty* `#if SUPPORT_LASER` / `#endif` pairs (`DDA.cpp:1008`,
`DDARing.cpp:484`), and record the remainder in §7.

**`SUPPORT_S_CURVE` cannot reach zero either**, but two thirds of its 36 errors are mechanical and
worth clearing because they are pure noise sitting on top of the real gap:

- 18 × `OneHalf` → `oneHalf` (the constant was renamed; the S-curve branches were not).
- `flags` → `m_flags` (×2), `startSpeed` → `m_startSpeed`, `planned` → `Planned`.
- `MoveSegment::SetParameters` / `Merge` arity (×3): with the switch on, `J_FORMAL_PARAMETER` adds a
  `j` parameter that **this project's own** `SegmentBuilder.cpp` does not pass. That is our code, not
  upstream's — it should pass `J_ACTUAL_PARAMETER(0)`.
- `'Move' does not name a type` (×2) on the two `SetSpeedRatioAndMaxJunctionSpeedFor*Moves(const Move&)`
  declarations — an include-order problem in `DDA.h`, fixable.
- `PrepParams::EnsureSpeedsSet` is defined in `DDA.cpp` but declared nowhere; add the declaration.

What remains after those is one coherent gap: **`MovementProfile` and `DDA_3rdOrder.cpp` were never
ported.** `MovementProfile.h` does not exist, `DDARing::PlanMoves` is declared and never defined, and
`afterPrepare` has no `peakAcceleration`/`peakDeceleration`. That is the honest state and §7 should say
so, rather than 18 renaming errors implying the port is further away than it is.

§7 below is where that is recorded.

### 2.3.1 Implementing nonlinear extrusion (M592)

`SUPPORT_NONLINEAR_EXTRUSION` becomes 1 by default, so this stops being a gap and becomes a feature.
It is the smallest of the four by a wide margin because **every part of the path already exists except
the coefficients themselves**:

| Piece | State today |
| --- | --- |
| Object model | ✅ `DuetAPI/ObjectModel/Move/ExtruderNonlinear.cs` — `A`, `B`, `UpperLimit` (defaulting to `0.2F`, matching RRF's `DefaultNonlinearExtrusionLimit`) |
| M592 parsing | ✅ `MCodeHandler.Motion.cs:1309`, writing `model.Move.Extruders[n].Nonlinear` |
| Per-extruder push-down loop | ✅ `MotionParameters.cs:466-489` — already fills `PressureAdvanceClocks` from `e.PressAdv.K0` in exactly the place these belong |
| `MotionConfig` field | ❌ both sides |
| `MotionSystem::GetExtrusionCoefficients` | ❌ |
| `DDA::Prepare` consumer | ⚠️ present, behind the switch, with 4 naming errors |

So the work is:

1. **Native**: add `struct NonlinearExtrusion { float a, b, limit; }` to `MotionConfig.h` and a
   `NonlinearExtrusion nonlinearExtrusion[maxExtruders]` member, plus
   `MotionSystem::GetExtrusionCoefficients(size_t extruder)` returning a reference (bounds-checked, as
   `GetExtruderDriver` already is). Keep RRF's struct shape rather than three parallel `float[]` arrays:
   the use site reads `nl.a`/`nl.b`/`nl.limit` and `MotionConfig` already carries one array-of-struct
   (`axisDrivers`), so the serialiser has the pattern.
2. **Layout**: append after `shapingTimeClocks`. It is 4-aligned and 12 bytes per extruder, so
   `sizeof(MotionConfig) % 4 == 0` still holds and no existing `static_assert` offset moves. Grouping it
   next to `pressureAdvanceClocks`, where it belongs semantically, would shift four asserts and the C#
   `SerializedLength` arithmetic for no benefit — `MotionConfig` is process-local and both ends are
   rebuilt from this repo together, so there is no compatibility reason to prefer either.
3. **Managed mirror**: `MotionConfig.cs` gains the property, the `SerializedLength` term
   (`12 * MotionLimits.MaxExtruders`), and a write loop beside the `AxisDrivers` one. Extend the layout
   test in `MotionConfigLayoutTests.cpp` and its C# counterpart.
4. **Push-down**: one line in the `MotionParameters.cs` extruder loop, beside the `PressureAdvanceClocks`
   assignment, copying `e.Nonlinear`.
5. **Reconfigure**: `HandleNonlinearExtrusionAsync` must call `ReconfigureAsync` — it does not today, so
   the object model value would never reach native. **No standstill flush is needed**: RRF's `case 592:`
   takes no movement lock (`GCodes2.cpp:3991`), and the coefficients are consumed in `Prepare`, which
   runs per move.
6. **Native switch**: `#define SUPPORT_NONLINEAR_EXTRUSION 1`, plus the four naming fixes in
   `DDA.cpp:905-916`.

Two divergences in the already-ported DCS half turned up while scoping this. Per the migration rules
(`MCODE_MIGRATION.md` §1.8, structural departures are the reader's call) they are flagged rather than
quietly settled — settled as D9, match RRF on both:

- **Partial vs. whole update.** RRF's `ConfigureNonlinearExtrusion` (`Move2.cpp:409`) declares
  `float a = 0.0, b = 0.0, limit = DefaultNonlinearExtrusionLimit` and writes **all three** whenever any
  parameter is seen, so `M592 D0 A0.01` resets B to 0 and the limit to 0.2. DCS's handler updates only
  the parameters present, leaving the others at their previous values. These give different machines
  after the same G-code.
- **Reply wording.** RRF: `"Drive %u nonlinear extrusion coefficients: A=%.3g, B=%.3g, limit=%.2f"`.
  DCS: `"Extruder {0} nonlinear extrusion A={1:F3} B={2:F3}, limit {3:F2}"`. Migration rule 5 says the
  reporting form is preserved because DWC, PanelDue and a decade of macros parse these strings.

### 2.4 Debug scaffolding — keep, fix the naming

Same treatment: compiled each with the macro on.

| Macro | Errors | Verdict |
| --- | --- | --- |
| `SEGMENT_DEBUG` | 32 | **All mechanical.** `debugPrintf` → `DebugPrintf` (×16), `p_distance` → `pDistance` (×8), `p_a` → `pA` (×8). Reaches zero. |
| `DDA_MOVE_DEBUG` | 10 | **All mechanical.** `accelDistance`/`decelDistance`/`targetNextSpeed` → `beforePrepare.*`; `totalDistance`/`topSpeed`/`startSpeed`/`endSpeed`/`requestedSpeed`/`flags` → `m_*`; `endStopsToCheck` → `m_flags.checkEndstops`. `MoveParameters::flags` is `uint16_t` and `m_flags.all` is 32 bits — widen it. Reaches zero. |
| `DDA_LOG_PROBE_CHANGES` | 7 | **Delete.** The 4 non-mechanical errors (`pddm`, `dm`, `DriveMovement`, `DMState`) are a step-ISR probe log with no counterpart here, and the probing it served is DCS's. Remove the macro, `DDA::LogProbePosition`, `loggedProbePositions`, `numLoggedProbePositions`, `maxLoggedProbePositions` and `probeTriggered`. |
| `CHECK_SEGMENTS` | n/a | Defined, and there is **no `#if CHECK_SEGMENTS` block anywhere** — upstream's live in `DriveMovement`, which was not ported. Nothing to keep; delete the dangling `#define`. |

Also mechanical, and to be fixed the same way:

- `LA_DEBUG`'s `#if 0` branch: `endSpeed`/`startSpeed`/`totalDistance`/`requestedSpeed`/`topSpeed` →
  `m_*`, `acceleration` → `m_maxAcceleration`, and `laDDA->DebugPrint()` now takes a tag argument.
- The two `#if 0` blocks in `DoLookahead` (`DDA.cpp:415`, `:503`): `moduleDda` → `Module::DDA`,
  `debugPrintf` → `DebugPrintf`, `targetNextSpeed` → `beforePrepare.targetNextSpeed`.
- The `#if 0` in `DDARing::PrepareMoves` (`DDARing.cpp:335`): `MoveDebugFlags::Lookahead` →
  `lookahead`, `debugPrintf` → `DebugPrintf`.
- `DDA.cpp:24-29`: `#ifdef DUET_NG` whose two branches both define `DDA_MOVE_DEBUG (0)`. Collapse to
  one line.

The two `#if 0 //SUPPORT_S_CURVE` blocks in `MoveSegment::CombinePrevious` stay as they are — upstream
disabled them deliberately and says why ("this causes speed changes, so it's disabled").

### 2.5 Whole files that go

- **`Compat/RTOSIface/RTOSIface.h`** — nothing includes it; `AtomicCriticalSectionLocker`,
  `BasePriorityBooster` and `NvicPriorityStep` have no users. Delete, and move its explanation of *why
  there is no lock on the segment chain* into `DriveTracker.h`, which is the class that relies on it
  and already points at the file.
- **`Compat/ObjectModel/ObjectModel.h`** — included once, for `INHERIT_OBJECT_MODEL` on `DDARing`,
  which expands to nothing. Delete the header and the empty base.
- **`Movement/README.md`** — its own first paragraph says it describes upstream and not this
  directory. Replace with a short README for what is here; the class headers already do it well.

Verify Phase 1 with: build clean, `ctest` 10/10, and — for the switches that reach zero — a build with
each one flipped.

---

## 3. Phase 2 — name things after what they are

Each of these removes an indirection whose only purpose was preserving upstream's spelling. Behaviour
is unchanged; the tests are the check.

### 3.1 `CanMotion` — delete the shim

`Compat/CAN/CanMotion.h` + `Motion/CanMotionShim.cpp` are five functions that do nothing but forward to
`ScheduleMoveBuilder`, reached through `reprap.GetMove().GetScheduleMoveBuilder()`. The SBC is not on a
CAN bus; the name describes the machine at the other end of the link. Delete both; `DDA::Prepare` calls
the builder directly.

### 3.2 `reprap` — remove the global facade

`RepRapShim` is a global named after a class that does not exist here, holding one `MotionSystem`, one
`Platform` that only formats strings, and a `GCodesShim` answering three questions off `MotionConfig`
(one of which, `GetVisibleAxes`, has no callers).

- Delete `RepRapShim`, `GCodesShim`, `Compat/Platform/RepRap.h`, `Compat/GCodes/GCodes.h`,
  `Compat/RepRap.cpp`, and the `using Move = MotionSystem` / `using AxisDriversConfig = ...` aliases.
- `GetTotalAxes()` / `GetNumExtruders()` become `MotionSystem` members reading the same fields.
  `GetVisibleAxes` goes.
- `RepRapShim::Debug` / `GetDebugFlags` always answer false/empty. The flag word survives the facade
  and moves onto `MotionSystem`; see §3.9 for why it is kept rather than deleted.
- **Ownership**: `MotionSystem` lives in the global, `DDARing[2]` in `MotionService`,
  `ScheduleMoveBuilder` inside `MotionSystem`. One engine, three owners. Make `MotionService` own the
  `MotionSystem` and pass it to the rings, so the engine has no static state and is constructible more
  than once. This is what lets §3.7 stop being static.

Note the interaction with §2.3: `SUPPORT_LASER`'s remaining errors include `GCodesShim::GetMachineType`.
Deleting `GCodesShim` does not make that worse — the accessor has to land on `MotionSystem` instead,
and §7 records it either way.

### 3.3 `Platform` — collapse to what it is

Two static functions that format a string and hand it to `DebugPrintf`, plus a `MessageType` enum
documented as not mattering. `Platform::MessageF` formats into a 256-byte buffer and then passes the
result through `DebugPrintf` as `"%s"`, formatting it a second time into another 256-byte buffer. Fold
into the log header (§3.6) as one `LogMessage(LogLevel, fmt, ...)`. Three call sites.

### 3.4 `PrepParams` — trim, but keep the type

`PrepParams` is `MoveProfile` plus `bool useInputShaping`, plus four accessors (`SteadyClocks()`,
`TotalAccelClocks()`, `TotalDecelClocks()`, `TotalAccelDistance()`) that re-spell public fields and have
no callers, plus `SetFromDDA`.

- Delete the four dead accessors.
- `SetFromDDA` becomes `DDA::BuildProfile()` returning the params, which is the direction the data
  actually flows, and removes `friend struct PrepParams` from `DDA`.
- **Keep `PrepParams` as a distinct type deriving from `MoveProfile`.** An earlier draft folded it into
  `MoveProfile`; keeping `SUPPORT_S_CURVE` reverses that. `MoveProfile` is deliberately shaped like the
  `ScheduleMove` wire packet, and the S-curve branch of `SetFromDDA` needs somewhere to put the 7-phase
  `phaseClocks[]`/`distances[]` data that must *not* go on the wire struct. `PrepParams` is that place.
  `useInputShaping` stays on it too (D2).

### 3.5 `Tasks` — rename to what it allocates

`Compat/Platform/Tasks.h` / `Compat/Tasks.cpp` implement a bump allocator over one `mmap`'d, `mlock`'d
region. Nothing about it is a task. Rename to `Platform/MotionArena.{h,cpp}` with
`Reserve`/`Release`/`Allocate`/`BytesFree`. The header comment's "why not malloc on a SCHED_FIFO thread"
explanation is the valuable part and stays.

`DDARing::Init` is documented as sizing itself from `GetNeverUsedRam()`; it does not, and the arena is a
fixed 4 MB. Fix the comment: the fixed arena is the deliberate choice (D4).

### 3.6 `Diagnostics` — split the two things in it

Holds the log sink *and* `Millis()`. `Millis()` has no callers at all — the ring's grace period uses
`StepTimer::GetTimerTicks()`, and `RepRapFirmware.h:264` claims otherwise. Delete `Millis()`; rename the
pair to `Platform/Log.{h,cpp}`. `RepRapFirmware.h`'s comment pointing at `Compat/Debug.cpp` names a file
that does not exist.

### 3.7 Statics that are statics because they reach a global

`MotionService::Configure` and `GetPositionAt` are `static` only because they call `reprap.GetMove()`
rather than touching `this`. That reaches the ABI: `DuetSbc_MotionConfigure(h, ...)` takes a handle and
ignores it, so two `DuetSbcHandle`s would silently share one motion system. §3.2's ownership change
makes both instance methods and the handle meaningful. The C ABI signatures do not change.

### 3.8 Misleading members

| Where | Problem | Action |
| --- | --- | --- |
| `DDA.h:105` `NoShaping()` | Returns `m_flags.isolatedMove`, identical to `IsIsolatedMove()` two lines above; no callers | Delete |
| `DDARing.h:94` `Diagnostics()` | Reports counters *and zeroes them*; a second call sees zeros. No callers, and no path to get the `StringRef` to DCS | Reshape into `GetStats()` / `ResetStats()` and wire to M122 — §3.9 |
| `MotionSystem::EnableDrivers` | Empty body, called twice per drive per move | Delete, with the call sites; say once in `MotionSystem.h` that drivers are enabled by the move message |
| `MoveSegment` members `protected` | Nothing derives from `MoveSegment` | `private` |
| `IrqSave`/`IrqRestore` | No-ops around the segment freelist | Delete; keep the ownership rule as a comment on `MoveSegment::Allocate` |

### 3.9 M122 — where the diagnostics go

The engine keeps a good set of counters and **not one of them can be read**: `DDARing::Diagnostics` and
`StepTimer::Diagnostics` both format into a `StringRef` that nothing passes in, and no CApi call
exposes either. The goal is M122 in DCS reporting them, so this is what the reshaping in §3.8 is for.

**Native exposes counters, DCS formats the text.** That is not a departure — it is the pattern already
working next door: `CApi.h` exposes `DuetSbcClockStats`, and
[`LinkInterface.cs`](src/DuetControlServer/Link/LinkInterface.cs)'s `PrintDiagnostics` reads it through
`Native.GetClockStats()` and renders "Step clock: synchronised, N samples, drift …". Marshalling a
`StringRef` across the ABI so that native can format a string DCS then has to parse would be the
departure.

The extension point exists too: implement `IDiagnostics` with a `[DiagnosticsPriority(n)]` attribute and
register it — `DiagnosticsProvider.PrintAsync()` picks it up and M122 prints it. Existing priorities run
`LinkInterface` (-5) through `CodeProcessor` (0), so motion belongs at about -4, next to
`EndstopCorrection`.

So:

1. **Native**: replace `DDARing::Diagnostics(StringRef&, unsigned)` with `GetStats()` returning a POD and
   an explicit `ResetStats()`. Same for `StepTimer::Diagnostics` (its `GetClockStats()` already exists
   and is already exposed — only the `StringRef` variant goes). Splitting the reset out is the point:
   the current "report and zero" is why a second M122 would show zeros.
2. **CApi**: one `DuetSbc_GetMotionStats(h, DuetSbcMotionStats*)` plus `DuetSbc_ResetMotionStats(h)`,
   mirroring how the clock stats are done.
3. **DCS**: a `IDiagnostics` provider formatting RRF's shape — `=== Move ===` then `=== DDARing n ===`
   per ring — so the output stays recognisable to anyone reading an M122 from a Duet.

What to report, taking RRF's `Move::Diagnostics` (`Move.cpp:992`) as the model for what a reader expects:

| Field | Source | RRF equivalent |
| --- | --- | --- |
| Segments created | `MoveSegment::NumCreated()` | "Segments created %u" |
| Movement delay / hiccups | `StepTimer::GetMovementDelay()` | "hiccups added" | 
| Scheduled / completed moves, per ring | `DDARing::GetScheduledMoves/GetCompletedMoves` | `DDARing::Diagnostics` |
| Lookahead errors, lookahead underruns, no-move underruns, per ring | `DDARing` counters | same |
| Dropped `ScheduleMove` packets | `ScheduleMoveBuilder::GetDroppedPackets` | none — SBC-specific, and non-zero means motion was lost |
| Submissions dropped, forced positions applied | `MotionService` | none — SBC-specific |
| Clock drift, residual, clamps, rejected samples | `StepTimer::GetClockStats` | none — already reported by `LinkInterface` |

This is what keeps `MoveSegment::NumCreated()` and `ScheduleMoveBuilder::GetDroppedPackets()` alive; both
would otherwise have gone in §4 as unreachable.

**Debug flags are a separate mechanism and M122 does not subsume them** (D3). The `reprap.Debug(Module::Move)`
branches do not feed M122 — they call `DebugPrintf`, which reaches DCS as a log event through the sink
that already exists. What is missing is only a way to turn them on.

The flag word moves onto `MotionSystem` with a `SetDebugFlags`, so there is one place to enable a topic
from. **No CApi export goes with it yet.** DCS's M111 handles `P-1` (its own log level) and does not
implement RRF's `P<module> S<0|1>`, so an export would have no caller — and an unreachable ABI entry
point is the same defect as the unreachable branches this pass is removing, just newer. It lands with
M111's module support; §7 records that.

---

## 4. Phase 3 — delete the unreachable API

Confirmed by grep across `src/`, `tests/` and `harness/` as having only their own declaration, or a
declaration and definition with no call. **Cross-check each against the managed side**
(`DuetControlServer/Link/Native/`, `DuetControlServer/Motion/`) before deleting: the C ABI is bound by
symbol name, so a grep of this repo is necessary but the ABI boundary deserves a second look.

**`DDA`**: `GetAverageExtrusionSpeed`, `HasForwardExtrusion`, `IsNonPrintingExtruderMove`,
`UsingStandardFeedrate`, `SetFeedRate`, `GetRequestedSpeedMmPerClock`.
`PrintMoves` stays — it is `DDA_MOVE_DEBUG`'s entry point, which §2.4 keeps.

**`DDARing`**: `Exit`, `GetLastEndpoints`, `ResetMoveCounters`, `ResetSimulationTime`,
`GetCurrentMoveDistance`, `GetCurrentMoveDuration`. (`Diagnostics` is reshaped by §3.9, not deleted.)

**`MotionSystem`**: `GetLogicalDriveForDriver`.

**`MoveSegment`**: `CalcLinearRecipU`, `AdjustLength`, `NormaliseAndCheckLinear` (35 lines of
commentary about a step ISR that does not exist here), `AppendDetails`, `minDuration`.
`DebugPrint` and `DebugPrintList` stay — `SEGMENT_DEBUG` uses them. **`NumCreated` stays** — it is
"Segments created" in M122 (§3.9).

**Three entries came off this list on a second pass**, because the first one only grepped for callers
and these have tests. Deleting tested code is a larger call than deleting unreached code, and none of
the three is misleading — they were only unused:

- `MoveSegment::IsLinear` / `IsAccelerating` — asserted in `MoveSegmentTests`.
- `DriveTracker::GetAndClearAccumulatedMovement` — four assertions in `DriveTrackerTests`. It is the
  natural hook for filament monitoring, which is not ported.
- `MotionSystem::GetPressureAdvanceK0ClocksForLogicalDrive` — asserted in `MotionSystemTests`, and
  `AddLinearSegments` was reading the same field directly beside it. Fixed in the other direction:
  the one reader goes through the accessor.

`MoveParams.h`'s `StopInputForDriver` did have callers — nine, all in tests — but it is
`StopInputForSwitch` with the arguments in the same order, so the tests call that instead.

**`MovementError`**: `GetMovementErrorText` — no caller; the error reaches DCS as a byte in
`MoveFailedEvent` and DCS renders the text. Delete it and `MovementError.cpp` with it.

**`Compat/RepRapFirmware.h`**: `Millis`, `Msquare`, `FilePosition`, `noFilePosition`,
`MovementSystemNumber`, `noAxis`, `xyzAxesBitmap`, `stepClocksToMillis`, `MicrosecondsToStepClocks`,
`ConvertSpeedFromMmPerSec`, `ConvertSpeedFromMmPerMin`, `InverseConvertSpeedToMmPerMin`, `Memcpyf`,
`Memcpyu32`, `unlikely`, and the `Tool`/`Platform` forward declarations.

**`MoveTiming.h`**: `minCalcInterval`, `minInterruptInterval`, `maxStepInterruptTime`, `hiccupIncrement`,
`maximumMoveStartAdvanceClocks`, `nominalRemoteDriverPositionUpdateInterval`,
`maxRemoteDriverPositionUpdateInterval` — all describe a step ISR that is not here. Keep `hiccupTime`,
`usualMinimumPreparedTime`, `absoluteMinimumPreparedTime`, `standardMoveWakeupInterval`,
`minimumExecutingSegmentDuration`.

**`MoveDebugFlags.h`**: keep `lookahead` and `printAllMoves` (both are used, and the `#if 0` blocks §2.4
fixes reference `lookahead`); the other nine name subsystems that were never ported.

**`MovementFlags`**: `combined` is set by `CombinePrevious` and read by nothing but `DebugPrint`'s
`flags.all` — keep, it is debug scaffolding. `Clear()` and `Init()` differ by one bit and both are
called; audit which call sites want which.

---

## 5. Phase 4 — what is left, made consistent

### 5.1 The `Compat` directory disappears

After §2–§4 what remains of `src/Compat/` is machine limits, the step clock, the arena, the log sink,
`SimulationMode` and `Float16Compat.h`. None of it is a compatibility shim.

```
src/Compat/RepRapFirmware.h        -> src/Config/MachineLimits.h  (limits, DriverId, bitmaps,
                                                                   and the SUPPORT_* switches §2 keeps)
                                   -> src/Movement/StepClock.h    (stepClockRate and its conversions)
src/Compat/Platform/Tasks.*        -> src/Platform/MotionArena.*
src/Compat/Diagnostics.*           -> src/Platform/Log.*
src/Compat/GCodes/SimulationMode.h -> src/Movement/SimulationMode.h
src/Compat/Float16Compat.h         -> src/Config/Float16Compat.h
```

`Float16Compat.h` is the one genuine compatibility shim and stays force-included, with its comment
corrected: the `DDA::originalFeedRate` it names as the only `float16_t` no longer exists, so the reason
it is still needed is RRFLibraries' own `Portability.h`, not this project's code.

The `#include <RepRapFirmware.h>` at the top of nine files becomes an include of the header that
actually supplies what each one uses. `Compat` comes off `duet_motion`'s
`target_include_directories`, so a stale `<RepRapFirmware.h>` fails the build rather than resolving.

### 5.2 Naming and formatting

- `DDA` and `DDARing` use `m_`-prefixed members; `MoveSegment`, `MoveProfile`, `MovementFlags` and
  `PrepParams` use bare names. Finish the convention `.clang-tidy`'s `PrivateMemberPrefix` already
  states. **Do this before §2.3's naming fixes, or immediately after** — otherwise the S-curve and
  debug branches get fixed to names that then change again.
- Run `clang-format` over the motion sources. `.clang-format` says Allman + tabs; the
  imported files carry upstream's mixed style and nothing enforces it here.
- `MoveSegment.h`'s 33-line header comment describes `DriveMovement` accumulating `s0`. Trim to what
  this class does, keeping the S-curve paragraph since the switch stays.
- **`SimulationMode` keeps all four values.** Only `Off` and `Normal` are reachable today, but
  `DDARing::Spin` and `DDA::Prepare` compare with `>=` and `<`, so the ordering is load-bearing and the
  two unreachable values cost nothing but a line each. Fix the comment instead: `Debug` and `Partial`
  are both labelled "simulating step generation", which is copied from a firmware that generates steps.
- **Drop the eCv annotations.** `_ecv_null`, `_ecv_array`, `_ecv_not_null` and `pre(...)` appear 39
  times, all in `src/Movement/`; they expand to nothing outside RRF's build and no verification runs
  here. Where one carried information worth keeping, keep the information rather than the annotation:
  `DDA::m_next`/`m_prev` are never null once `DDARing::Init` has run, which is worth an assertion in
  `Init` and a comment on the members.

### 5.3 Stale comments

Fix as encountered; these are the known ones:

- `ScheduleMoveBuilder.h:13` — "In step 9 a thin `namespace CanMotion` shim…" (a migration step that has
  landed, and is about to be deleted by §3.1).
- `DDA.h:21` — "Gone for Phase 1".
- `MoveParams.h:65` — "Two arrays follow it"; there are three (`endPoint`, `directionVector`,
  `stopInputs`).
- `MoveProfile.h:13` — "Fields map one-for-one onto the ScheduleMove packet"; `SendPacket` assigns twelve
  fields one at a time and casts each. Either say that, or add the `static_assert`s that make it true.
- `DDA.cpp:203` — "we process the M665 command in config.g"; there is no M665 on this side.
- `DDARing.cpp:307` — "prepare moves one tenth of a second ahead"; the code compares against whatever
  `prepareAdvanceTime` the caller passes, which is `usualMinimumPreparedTime` = 50 ms.
- `DDARing.cpp:90` — cites `ManageIOBitsAndFeedforward`, which does not exist here.
- `MotionService.cpp:31` — an orphaned comment ("The longest the motion thread sleeps…") with no
  declaration under it; the constant is gone and `Run()` hardcodes 1 ms.
- `DDARing.h:24` — two stacked comments saying the same thing about the grace period.
- `Compat/RepRapFirmware.h:4` — the re-sync premise §1 retires.

---

## 6. Phase 5 — stop it coming back

### 6.1 Turn linting on

[`src/CMakeLists.txt`](src/DuetSbcInterface/src/CMakeLists.txt) is what let this accumulate:

```cmake
set_target_properties(duet_motion PROPERTIES CXX_CLANG_TIDY "")     # no linting at all
target_compile_options(duet_motion PRIVATE   -Wno-unused-parameter)
target_compile_options(duet_motion INTERFACE -Wno-unused-parameter) # and for every consumer
```

Both are justified by "imported upstream source is linted by its own project" and "renaming them would
be churn in files that get re-synced against upstream" — the premise §1 retires.

- Turn `CXX_CLANG_TIDY` on for `duet_motion` with the same `.clang-tidy` as `duet_sbc`. Expect a
  substantial first-run backlog; fix it rather than suppressing it.
- Drop both `-Wno-unused-parameter` lines. The remaining unused parameters are the no-op
  `operator delete` pairs that go with arena allocation — name them `/*unused*/` in those two headers
  rather than disabling the warning for everyone who includes `duet_motion`.
- Once `Compat/` is gone, the explicit `DUET_MOTION_SOURCES` list can go back to the glob `duet_sbc`
  uses: the "unported tree" it was protecting against no longer exists. Keep the two libraries separate
  — the engine's independence from the link is what the offline tests rely on.

### 6.2 Compiling the disabled paths

The switches are `#ifndef`-guarded defaults, so a build can flip one with `-DSUPPORT_ASYNC_MOVES=0`
and check that the other path still compiles. Four configurations do:

| Configuration | |
| --- | --- |
| `SUPPORT_ASYNC_MOVES=0` | verified |
| `SUPPORT_NONLINEAR_EXTRUSION=0` | verified |
| `SEGMENT_DEBUG=1` | verified |
| `DDA_MOVE_DEBUG=1` | verified |
| `SUPPORT_LASER=1`, `SUPPORT_S_CURVE=1` | not possible — the missing pieces in §7 have to land first |

An earlier draft had CI run that loop on every build. It does not: nothing runs these today, so the
same rot that this pass had to repair can happen again, and the only guard against it is that
somebody flips them when they touch the engine. Being clear about that is better than implying a
check exists.

---

## 7. Gaps to record

Deleting a switch removes the marker that said a feature is missing, and keeping one that cannot
compile overstates how close it is. Both need one honest home, and this is it. It does **not** belong
in `src/Documentation/articles/` — those describe what the software does today, for people using it;
work that is planned but not done belongs here with the rest of the plans.

| Gap | What is missing here |
| --- | --- |
| S-curve / 3rd-order planning | `MovementProfile.{h,cpp}` and `DDA_3rdOrder.cpp`; `afterPrepare.peakAcceleration`/`peakDeceleration`; `PrepParams`' 7-phase arrays. `DDARing::PlanMoves` is declared and never defined. |
| Laser power scaling with speed | `MachineType`, `Pwm_t`, `DDA::laserPwmOrIoBits`, a machine-type accessor, and a PWM field in `MoveParamsHeader` for DCS to fill |
| Input shaping on this side | Deliberate — the boards shape. The consequence (tracked position leads real position during acceleration by `shapingTimeClocks`) is documented in `MotionConfig.h`; cross-reference it. |
| IOBits, scanning probes, coordinate rotation | Not ported; each needs a field on the move that DCS does not send. Switches deleted by §2.1. |
| Leadscrew adjustment moves, `InitAsyncMove`, babystepping into a queued move | The three named in `DDA.h`'s "Gone for Phase 1" |
| M111 driving the motion debug flags | DCS's M111 implements `P-1` only. `MotionSystem::SetDebugFlags` exists and nothing calls it; the CApi export and the `P<module> S<0\|1>` parsing land together, and until they do the `Module::Move` and `Module::DDA` branches stay compiled but switched off |

---

## 8. Order and verification

Each phase is a separate commit; each ends with `cmake --build --preset native && ctest`, 10/10.

| Phase | Content | Risk |
| --- | --- | --- |
| 1a | §5.2's `m_` prefix completion, alone | Low, mechanical. **Before** 1b, so the branch fixes are not done twice. |
| 1b | §2 — flag disposition, branch repairs, dead files (excluding §2.3.1) | Low. For deletions, confirm with a `-E` diff on `DDA.cpp`, `DDARing.cpp`, `MoveSegment.h`; for repairs, the check is the flipped-flag build. |
| 1c | §2.3.1 — nonlinear extrusion, on its own | **The only phase that changes behaviour**, and the only one touching DuetControlServer. Keep it a separate commit: it spans an ABI (`MotionConfig` + its C# mirror + both layout tests), it settles D9, and it is the one piece here a `git revert` should be able to take back on its own. Needs a print with `M592 D0 A0.01 B0.001` to verify end to end — the unit tests only cover the layout. |
| 2a | §3.1–3.8 — remove the shims, rename to what things are | Low. Mechanical, but the call graph genuinely changes. Tests touch `reprap.GetMove()` in 8 places and are the check that the ownership change is complete. |
| 2b | §3.9 — the motion stats struct, its CApi calls, and the DCS `IDiagnostics` provider | Low, additive. Second phase touching DCS; keep it separate from 2a so the rename diff stays readable. Verified by running M122 and reading the output. |
| 3 | §4 — delete unreachable API | Low. Compile-checked, plus the managed-side cross-check. |
| 4 | §5.1/§5.3 — move files, fix comments, format | Low, large diff. Do the moves as `git mv` in their own commit so the content diff stays readable. |
| 5 | §6 — linting, and the flipped-flag CI job | Medium. Backlog size unknown until it runs; budget for it landing across several commits. |

Beyond the unit tests: a jitter-harness run and a real move on hardware after Phase 2 and again after
Phase 5, since Phase 2 is where the call graph actually changes.

---

## 9. Decisions

All settled. Recorded here because each one changes what an earlier section says.

| | Question | Settled as | Where |
| --- | --- | --- | --- |
| **D1** | eCv annotations (`_ecv_null`, `pre(...)`) | **Drop them.** Keep the information they carried, not the annotation | §5.2 |
| **D2** | `useInputShaping` on `MoveProfile` or separate? | Moot — keeping `SUPPORT_S_CURVE` means keeping `PrepParams`, so it stays there | §3.4 |
| **D3** | Debug flags: delete or wire? | **Wire.** M122 does not subsume them — they are a different mechanism, reaching DCS through the log sink. Keep the flag word, add `DuetSbc_SetDebugFlags`, drive from M111 later | §3.9 |
| **D4** | `DDARing::Init` sizing | Fix the comment; the fixed arena is the deliberate choice | §3.5 |
| **D5** | `DDARing::Diagnostics` | **Keep and reshape** into `GetStats()`/`ResetStats()`, reported by M122 | §3.8, §3.9 |
| **D6** | `SimulationMode` values | **Keep all four.** The ordering is load-bearing (`>=`/`<` comparisons); fix the misleading comment instead | §5.2 |
| **D7** | Nonlinear extrusion | **Implement it and default the switch to 1** | §2.3.1 |
| **D8** | `DDA_LOG_PROBE_CHANGES` | **Delete the blocks.** A step-ISR probe log with no counterpart here; the probing is DCS's | §2.4 |
| **D9** | M592 divergences from RRF | **Match RRF** on both the whole-coefficient update and the reply wording | §2.3.1 |

Two of these run against the general direction of the pass and are worth stating plainly, so nobody
"tidies" them later: **D6 keeps two unreachable enum values**, because the enum is ordered and compared
with `>=`; and **D3/D5/§3.9 keep several members that §4's own criterion would delete**, because their
caller is the M122 path that does not exist yet. Both are recorded at the point of use.
