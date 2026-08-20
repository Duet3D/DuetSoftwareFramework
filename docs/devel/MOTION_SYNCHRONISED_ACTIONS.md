# Motion-synchronised actions

Plan for effects that are not motion, a fan speed, a heater setpoint, an output pin, but that must
happen where the G-code put them rather than as soon as DuetControlServer reads the line.

The companion to [MOTION_CONFIG_ORDERING.md](MOTION_CONFIG_ORDERING.md). That one solved *ordering*:
a value a queued move is going to read must not change underneath it, so the value travels on the
move. This one solves *timing*: an effect the machine performs must land at the point in the path
where the file placed it.

The worked example throughout:

```gcode
G1 X100 F3000   ; move A
G1 X200 F3000   ; move B
M106 S255       ; fan to full
G1 X300 F3000   ; move C
```

The fan must reach full as the head arrives at X200, without the machine stopping, and without the
fan going to full while move A is still running.

Two implementations are specified in full (§6 and §7) and compared (§8). Everything in §5 is needed
by both and can land before the choice is made.

---

## 1. What happens today

A DuetControlServer handler has two tools, and both are wrong for this:

- **Apply it now.** The effect happens as the code reaches `ProcessInternally`, which may be a full
  queue ahead of where the machine physically is. `MCodeHandler.Fans.cs`, `MCodeHandler.Ports.cs` and
  `MCodeHandler.Heat.cs` call `FanManager.SetSpeedAsync`, `GpioManager.WriteAsync` and
  `HeatManager.SetTemperatureAsync` with no flush and no wait: in the example the fan reaches full
  during move A.
- **`FlushAndWaitForStandstillAsync`** ([MCodeHandler.Motion.cs:2367](../../src/DuetControlServer/Codes/Handlers/MCodeHandler.Motion.cs)).
  Correct ordering, at the cost of stopping the machine: a blob, a ringing mark, a longer print.

The codes divide into four classes, and only two of them are about stopping:

| Class | Meaning | Examples | Mechanism |
| --- | --- | --- | --- |
| **Immediate** | no relation to motion | M115, M409, M122, M550 | act now, without waiting for the channel's pending codes |
| **Deferred** | the physical effect belongs at a point in the path | M106/107, M42, M280, M300, M150, M117, M3/M4/M5, M568, M104/140/141, M144, `G10` without axis letters (§9) | this document, from a job file or its macros only (§5.2) |
| **Ordered** | applies to moves built after it, must not reach moves already built | M201/203/205/566, M204, M592, M425, M572's value | solved: the move carries it (MOTION_CONFIG_ORDERING) |
| **Barrier** | changes what an already-queued move *means*, or needs the board's reply to produce its own | M92, M584, M350, M208, M669/M665, M574, M558, tool change, homing | standstill |

Today the class is implicit in whether a handler happens to call the flush, and nothing can test
that. M115 and M122 do not flush; M409 does; nothing states which is intended. §5.1 makes the class
declarative.

M572 sits across the boundary. Its *value* is Ordered and already rides the move on the SBC side,
but the handler still pushes the coefficient to the drivers, so it keeps a standstill marked `TODO`
in [MCodeHandler.Motion.cs:1268](../../src/DuetControlServer/Codes/Handlers/MCodeHandler.Motion.cs).
§7.7 explains why neither implementation fully removes that wait and what would.

---

## 2. What RepRapFirmware does

RepRapFirmware defers whole codes through `GCodeQueue`
([GCodes/GCodeQueue.cpp](../../lib/RepRapFirmware/src/GCodes/GCodeQueue.cpp)). The mechanism, verified
against the source:

- A `QueuedCode` is the raw command bytes (up to `BufferSizePerQueueItem` = 64) plus one number:
  `executeAtMove = GetScheduledMoves()`, the count of moves queued at the moment the code was read.
  There are `maxQueuedCodes` = 16 items; a full queue stalls the file channel, which retries.
- The gates (`GCodeBuffer::CanQueueCodes`, `ShouldQueueMCode`, the call sites in GCodes2.cpp):
  the channel is reading a file; the code contains no expression (it would be re-evaluated too late);
  the command has no fraction; it fits 64 bytes; moves are actually in flight
  (`GetScheduledMoves() != GetCompletedMoves()`); and `segmentsLeft == 0`, because a segment that
  turns out to command no movement is discarded and would throw the count out.
- The originating channel is answered `ok` immediately. The queue is the input of the dedicated
  `Queue` channel (`Queue2` for the second motion system), which executes the head entry once
  `completedMoves >= executeAtMove`. Replies go to `GenericMessage`, detached from the line that
  asked; empty replies are suppressed.
- **Firing is loose.** `completedMoves` is advanced by the Move task, whose wakeup interval is up to
  `StandardMoveWakeupInterval` = 500 ms unless the ring is full, and the queued code then waits for
  the main task's round robin. The effect lands somewhere inside the following move(s), not at the
  boundary.
- **Pause and rewind are coupled by one counter.** `DDARing::PausePrint` decrements `scheduledMoves`
  once per discarded DDA; `GCodeQueue::PurgeEntries` then drops every entry whose `executeAtMove`
  exceeds the reduced count. The file rewinds to the first discarded move's position, so the purged
  codes are exactly the ones the replay re-reads. Stop, abort and M112 call `Clear()`.
- **Standstill waits drain the queue.** `LockMovementSystemAndWaitForStandstill` requires
  `codeQueue->IsIdle()` and the Queue channel itself idle, so M400, M0/M1/M2, file end and
  M190/M191 all wait for deferred codes as well as for moves. The Queue channels skip that wait
  themselves, so a queued code cannot deadlock on its own queue.
- M291 is deliberately never queued: a non-blocking message box released late can overwrite a later
  blocking one.

One structural difference matters when porting any of this: on a Duet 3 running RepRapFirmware the
spindle pins, the buzzer and the PanelDue port are on the main board, so M3/M4/M5, M300 and M117 are
local writes. Here board 0 runs DuetCANMaster and has no ports of its own
(`CanAddresses.HasNoHardware`), so a spindle is up to three `CanMessageWriteGpio` sends and every
fan, heater and LED strip is remote by definition. M117 and M300 remain object-model writes.

---

## 3. Every message the main board sends

The universe both implementations work over, verified against RepRapFirmware and CANlib.
`CanMessageType` declares 63 values that travel main board to expansion board; RepRapFirmware sends
55 of them through the 33 layouts below. Sizes are `sizeof` for the target; a variable-length message
shows its maximum.

*RRF defers* means the generating code is in `ShouldQueueMCode`/`ShouldQueueG10`. *RRF standstill*
means the generating path calls a `WaitForStandstill` variant before sending. *Class here* is what
DSF should do: **defer** if a user expects the effect where they wrote it, **standstill** if it
changes what a queued move means or needs the board's reply to produce its own, **immediate** if it
has no relation to motion, and *n/a* where no code generates it.

| Message | Generated by | RRF defers | RRF standstill | Reply expected | Size | Class here |
| --- | --- | --- | --- | --- | --- | --- |
| `MovementLinearShaped` | every move with motion for that board, from `DDA::Prepare`; discarded for boards with none | never | n/a | none: motion FIFO | 60 max, trimmed to the drivers used | n/a: it is the motion, and carries its own time |
| `StopMovement` | an endstop or probe trigger stopping drivers mid-move (`CanMotion::StopDriverWhenExecuting`); **not** pause and not M112 | never | n/a | none: urgent buffer | 2 | n/a |
| `RevertPosition` | behind `StopMovement` when stopped drivers must wind back (probing) | never | n/a | none: urgent buffer | 40 max | n/a |
| `EmergencyStop` | M112, local M999, trigger 0, serial and SBC emergency stops; unicast to each running board, then a broadcast backstop | never | no | none | 1 | immediate, absolutely |
| `TimeSync` | periodic broadcast from `CanClockLoop` | n/a | n/a | none | 12/16/20 | n/a: defines the clock |
| `SetHeaterTemperatureV1` | M104, M109, M140, M141, M144, M190, M191, M568, `G10`, M562; heater-off from M0/M1/M81/M502 and print end (`Heat::SwitchOffAll`); suspend/unsuspend from pause, resume and probing with M558 B1 | M104, M140, M141, M144, M568, `G10` without axis letters | M109, M190, M191 and M116 wait *before* setting the target, then unlock so pausing still works | `StandardReply` | 9 | **defer** the queueable set; **standstill** for the waiting codes, whose block depends on the target being in force |
| `HeaterModelV3` | M307 (M501 via config-override.g), and M303 completion | never | no | `StandardReply` | 60 | immediate |
| `SetDefaultHeaterModel` | `Heater::SetFunction` when no model was set by the user: M140 H, M141 H, tool creation M563 | never | no | custom `HeaterModelReport`; `StandardReply` on error | 5 | immediate |
| `SetHeaterFaultDetectionParameters` | M570 | never | no | `StandardReply` | 16 | immediate |
| `SetHeaterMonitors` | M143 | never | no | `StandardReply` | 60 | immediate |
| `HeaterTuningCommand` | M303 | never | no | `StandardReply`; unsolicited `heaterTuningReport` follows | 22 | immediate |
| `HeaterFeedForwardV1` | M106/M107 on a fan mapped to the current tool; the planner's extrusion feedforward, from the Move task | with the M106/M107 that carries it | no | none: `SendMessageNoReplyNoFree` | 16 | **defer, to the same anchor as the fan speed it accompanies**; the extrusion terms belong to the move |
| `SetFanSpeed` | M106, M107 (all mapped-fan forms); M303 tuning fans; the display | M106, M107 | no | `StandardReply` | 8 | **defer**: the worked example |
| `FanParameters` | M106 carrying `T`, `B`, `L`, `X`, `H` or `C`, its only generator | always | no | `StandardReply` | 34 | **defer** |
| `WriteGpio` | M42 and M280, the only generators | always | no | `StandardReply` | 7 | **defer**; spindles (M3/M4/M5) land here too, see §2 |
| `CreateInputMonitorV1` | M574, M558, M950 J | never | M574 when setting; M558 and M950 J no | `StandardReply`, `extra` = input state | 64 max (trailing pin name) | **standstill** for M574/M558: no move that consults an endstop or probe may be queued or running, and both need the reply. Immediate for M950 J |
| `ChangeInputMonitorV1` | port replacement by the same three codes; endstop/probe priming for homing and probing moves; per-tap probe setup; pin-name reporting; M558.2/.3/.4 | never | teardown under M574's standstill; priming under the standstill `G1 H` takes; the rest no | `StandardReply`, `extra` = state | 9 | **standstill** where it deletes or reassigns; priming is part of its move |
| `ReadInputsRequest` | scanning-probe reads: M558 create, M558.1 calibration, G29 grid scan, the laser task | never | n/a | custom `ReadInputsReplyV0` to a callback | 8 | immediate: a read wants the value now |
| `CreateFilamentMonitor` | M591 | never | no | `StandardReply`; periodic `filamentMonitorsStatusReportV2` broadcasts follow | 5 | immediate |
| `DeleteFilamentMonitor` | M591 | never | no | `StandardReply` | 5 | immediate |
| `MultipleDrivesRequest<T>` (5 subtypes: driver states, motor currents, standstill current, steps/mm + microstepping, `setPressureAdvanceV2`) | M17/M18/M84, M92, M350, M572, M906/M913/M917, M584; driver states also from the idle timeout and `DDA::Prepare` on the Move task | never | yes, except: the power-fail script skips it, `M906 I` alone takes none, and the Move-task driver states cannot | `StandardReply`, except Move-task driver states: `CanRequestIdNoReplyNeeded`, motion FIFO | 20 to 52 by `T` | **standstill** for currents, steps, microstepping and enables. Pressure advance: §7.7 |
| `EnableStallEndstop` | priming and disarming stall homing (M574 S3) inside G28 / `G1 H1` | never | armed inside the homing sequence, itself a barrier | `StandardReply` | 8 | n/a: part of the homing move |
| `SetInputShapingV1` | M593 with parameters, to every board with drivers | never | yes when setting | `StandardReply` per board | 60 | **standstill**: queued moves were shaped with the old filter |
| `StartAccelerometer` | M956 | never | no | `StandardReply`; unsolicited `accelerometerData` | 12 | immediate |
| `StartClosedLoopDataCollection` | M569.5 | never | no | `StandardReply`; unsolicited `closedLoopData` | 11 | immediate |
| `ReturnInfo` | M115 B, M122 B, M997's board probe | never | only M997 | `StandardReply`, fragmented via `moreFollows`; `extra` = UF2 flag | 3 | immediate |
| `DiagnosticTest` | M122 P>1 | never | no | `StandardReply`, which "may not actually arrive if the test crashes the expansion board" | 20 | immediate |
| `Reset` | M999 B | never | **no** | `StandardReply`, sent before the reset | 2 | **standstill** here: a board must not reset with moves in its queue. RRF not waiting reads as a gap, not a precedent |
| `SetAddressAndNormalTiming` | M952 | never | **no** | `StandardReply` from the old address | 16 | **standstill** here: it changes the bus the moves travel on |
| `AcknowledgeAnnounce` | answers a board's `announce` | never | n/a | none | 1 | n/a |
| `UpdateYourFirmware` | M997 | never | yes: `UpdateFirmware` locks everything first | `StandardReply`, then the board's `firmwareBlockRequest` cycle | 4 | **standstill** |
| `FirmwareUpdateResponse` | answers `firmwareBlockRequest` | never | n/a | none: the next block request acknowledges | 64 max | n/a |
| `Generic` (19 subtypes: four `m950*`, `m308V1`, six `m569*`, `m915`, `m111`, `m655`, `accelerometerConfig`, `configureFilamentMonitor`, `setConnectionTimeout`, `testReport`, `writeLedStrip`) | M111, M122 P1, M150, M308, M569.x, M591, M655, M915, M950, M955, M959 | `writeLedStrip` (M150), when the strip needs no standstill | M569 setting `D/R/S/V`, M569.1 setting, M569.6 first call, M150 when `MustStopMovement`; the rest never | `StandardReply`, `extra` where asked | 64 max; actual = paramLength + 4 | **defer** for M150; **standstill** for M569 and M950 reassigning a driver or port a queued move may use; immediate for the rest |

Four observations the table forces:

- **The class is a property of the code, not the layout.** The same nine bytes of
  `SetHeaterTemperatureV1` carry a deferrable setpoint for M104 and a barrier for M109; no field in
  the message distinguishes them. The declaration in §5.1 is therefore keyed on codes.
- **M106 breaks one-message-per-code.** It emits `SetFanSpeed` plus `HeaterFeedForwardV1` (and
  `FanParameters` when it carries configuration), and feedforward that lands before the fan change
  has a heater compensating for a fan that is not running. A deferred unit is a *code's set of
  sends*, anchored once.
- **Deferred and Barrier are disjoint in RRF**: no code both queues and waits for standstill, and
  where a layout shows both (the heater row, M150) the generating codes split cleanly.
- The 8 declared types RRF never sends (`startup`, `controlledStop`, `powerFailing`, `insertHiccup`,
  `setFastTiming`, `setDateTime`, `updateDeltaParameters`, `setPressureAdvanceV1`, the last
  receive-only) need nothing, but any generated per-type table (§7.1) must decide their entries
  explicitly.

---

## 4. What the motion side already provides

Both implementations anchor on the same facts about the tree:

- **Move ids.** `MovePlanner.QueueMove` assigns a per-ring monotonic `MoveId` (never zero) to every
  submitted move ([MovePlanner.cs:328](../../src/DuetControlServer/Motion/MovePlanner.cs)), and
  `JobMoveIndex` maps ids of job-file moves to file positions.
- **Retirement is predicted, and reported per move.** The native engine retires a move when the
  fitted step-clock model passes `moveStartTime + clocksNeeded`, on a 1 ms tick; endstop moves retire
  when their drives actually stop. Each retirement posts a `MoveCompletedEvent` carrying the
  `moveId`, which the DCS `LinkService` dispatcher receives immediately and records in
  `MotionTracker` ([MotionTracker.cs](../../src/DuetControlServer/Motion/MotionTracker.cs)), which
  currently has no reader.
- **A pause purges provisional moves only.** `DDARing::Feedhold` plans a deceleration among the
  *uncommitted* DDAs and `PurgeAfter` reports `FirstPurgedMoveId`
  ([DDARing.cpp](../../src/DuetSbcInterface/src/Motion/DDARing.cpp)); committed moves (segments
  generated and dispatched, up to `usualMinimumPreparedTime` = 50 ms ahead) always run to
  completion. Nothing is ever recalled from a board. The rewind point is computed from
  `FirstPurgedMoveId` (`MovePlanner.TakeJobResumePoint`).
- **Times on the wire are in the movement timebase**: master step-clock ticks less the shared
  movement delay. The SBC stamps `whenToExecute` from `GetMovementTimerTicks()`, and a board
  schedules at `whenToExecute + movementDelay + localTimeOffset`
  ([Duet3Expansion StepTimer.h:167](../../src/Duet3Expansion/src/Movement/StepTimer.h)), so
  everything timed this way slips with the path when the machine hiccups.
- **A committed move's duration is unbounded.** `CanAddMove` limits the total duration of
  *provisional* moves (2 s), but a single long move is prepared whole and dispatched ~50 ms before it
  starts. Anything waiting for that move's end waits its full duration.

---

## 5. Shared groundwork

Required by both implementations, and useful on its own.

### 5.1 The class table

Today the class of a code is implicit in whether its handler happens to call
`FlushAndWaitForStandstillAsync`, and each of the 13 call sites carries its own guard
(`SetsAnyDrive` for M92/M350/M906, "parameters present" for M584, and so on). The table makes the
class a declared fact, and moves the enforcement out of the handlers into the pipeline, so "does
this code need a standstill" becomes one reviewable line instead of an invisible omission.

Dispatch has the same shape as the class: the `MajorNumber` switches in the handlers enumerate
exactly the key space the table does, and keeping them as a second structure would mean two
enumerations of the same facts, policed by a diff test. The table therefore carries the handler
alongside the class: one row declares that a code exists, what class it is, and what runs it, so "a
handler without a row" becomes impossible to write rather than a test failure.

The fractional part of a code number is a second instance of the same implicitness, and today it is
a bug: dispatch switches on `MajorNumber` alone
([MCodeHandler.cs:116](../../src/DuetControlServer/Codes/Handlers/MCodeHandler.cs)), so `M906.1`
executes as `M906`. The handlers that do honour minors carry their own scattered guards, each with
its own wording for an invalid minor: M36, M505, M581 and M586 in `MCodeHandler.cs`, M201 and M569
in `MCodeHandler.Motion.cs`, M558 in `MCodeHandler.Probes.cs`. Nothing lists which fractional codes
exist. RepRapFirmware gates fractions once, at the top of each switch
([GCodes2.cpp:200](../../lib/RepRapFirmware/src/GCodes/GCodes2.cpp) for G,
[GCodes2.cpp:742](../../lib/RepRapFirmware/src/GCodes/GCodes2.cpp) for M): a fraction on a major
with no hardcoded fractional parts diverts to `TryMacroFile`, which runs
`<letter><major>.<minor>.g` if it exists and answers `warningNotSupported` otherwise; inside a
major that does implement fractions, an invalid minor is whatever its developer wrote. The table
takes the stronger, uniform form: every fractional code DSF implements is an explicit row, and a
fraction without a row takes the macro-then-unsupported path, never the bare major's handler, so
`M906.1` and `M558.5` stop depending on which handler they happen to hit.

**Added:**

- `Codes/CodeClass.cs`:

  ```csharp
  public enum CodeClass
  {
      Immediate,   // act now, do not wait for the channel's pending codes
      Ordered,     // flush the pipeline (order + expressions), no standstill; the move carries the value
      Deferred,    // the effect belongs at a point in the path (§5.2, §5.3)
      Barrier      // flush, then wait for standstill before the handler runs
  }
  ```

- `Codes/CodeTable.cs`: each handler class declares the codes it implements as a static
  `CodeTable<THandler>`, one entry per row, an entry being the code number(s), the class, and the
  handler; the letter comes from the table's constructor:

  ```csharp
  public static readonly CodeTable<MCodeHandler> Rows = new(CodeType.MCode)
  {
      { [0, 1, 2],  Barrier,   (h, c, ct) => h.HandleStopAsync(c, ct) },
      { 104,        Deferred,  (h, c, ct) => h.SetTemperaturesAsync(c, wait: false, ct) },
      { 109,        Barrier,   (h, c, ct) => h.SetTemperaturesAsync(c, wait: true, ct) },
      { [569, (569, 1), (569, 2), (569, 4), (569, 6), (569, 7)],
                    Barrier,   (h, c, ct) => h.SendDriverConfigAsync(c, c.Minor ?? 0, ct) },
      { 906,        c => c.SetsAnyDrive() ? Barrier : Immediate,
                               (h, c, ct) => h.HandleMotorCurrentsAsync(c, ct) },
  };
  ```

  An entry names its code by major number (`104`), by `(major, minor)` for a fractional code, or
  by a list of numbers sharing one row, which is how codes that shared a switch arm (`0 or 1 or
  2`, `17 or 18 or 84`) and a handler's internal minor switch (M569) are written; arms that passed
  computed arguments (M104 and M109 differ only in `wait:`) become lambdas that pass them. Tables
  exist only in `GCodeHandler` and `MCodeHandler`: `TCodeHandler` handles every T code the same
  way, the tool number is a value with no number to key on, so it answers with plain expressions
  instead (its class is Immediate for the bare `T` report and Barrier for a tool change).
  The class is either fixed or a resolver `Func<Commands.Code, CodeClass>` for rows
  whose class depends on the parameters; the resolvers are today's handler guards, relocated:
  `M92/M350/M906/M913/M917` are Barrier when a drive letter is present and Immediate when bare (a
  report, which DWC polls mid-print); `M584` and `M593` are Barrier only when setting; `G10` is
  Barrier with an axis letter and Deferred without; `M150` asks the strip whether it needs a
  standstill (the `MustStopMovement` capability) and is Barrier then, Deferred otherwise. Every
  fractional code a handler implements is its own row (today: M36.1/.2, M201.1, M505.1,
  M569.1/.2/.4/.6/.7, M581.1, M586.4), so the tables are the complete list of codes DSF supports,
  fractional and bare. There is no merged classifier: `ICodeHandler` gains `Classify(code)`,
  returning the code's class or null for "no such code" (below), beside its existing
  `ProcessAsync(code)`, which becomes a one-liner over the handler's own table where one exists,
  and the pipeline routes by code letter exactly as `Code.ProcessInternallyAsync` does today; the
  letters partition the key space, so merging the tables would buy nothing. Lookup is exact: a
  code with a fraction looks up `(type, major,
  minor)` and never falls back to the bare-major row. `Classify` is side-effect free, and nothing
  allocates per code: the key is a struct, each table freezes its dictionary on first lookup, and
  the entries and their lambdas are singletons created at static initialisation, which also keeps
  the class column (`Rows.ClassColumn`) readable without instantiating anything. A runnable
  demonstration of the whole shape, tables, resolvers, shared entries, the miss path and the class
  test, is [examples/CodeClassifierExample.cs](examples/CodeClassifierExample.cs)
  (`dotnet run CodeClassifierExample.cs`).

**Changed:**

- `Pipelines/ProcessInternally.cs` routes each G/M/T code to its handler by letter, as
  `Code.ProcessInternallyAsync` does today, asks it `Classify(code)`, performs the class's
  synchronisation, and calls `ProcessAsync(code)` (diagram below). Keywords and comments are not
  classified. A flush that reports "not ready" cancels and retries the code exactly as the
  in-handler flushes do today.
- The `MajorNumber` switches in `MCodeHandler.ProcessAsync`
  ([MCodeHandler.cs:116](../../src/DuetControlServer/Codes/Handlers/MCodeHandler.cs)) and
  `GCodeHandler` are deleted (`TCodeHandler` has none); the simulation gate at
  [MCodeHandler.cs:109](../../src/DuetControlServer/Codes/Handlers/MCodeHandler.cs) remains,
  wrapped around the invocation of M-code rows. The `_ => throw new NotSupportedException` default
  arms go with the switches: an unknown code is a lookup miss, not an exception. M22 and M998,
  whose arms exist only to throw, get no rows and share the miss path.
- A code with no row, an unknown bare major and an unlisted fraction alike, has no handler. The
  pipeline runs the macro named after the code via the existing `TryRunCodeMacroAsync`
  ([Code.cs](../../src/DuetControlServer/Commands/Generic/Code.cs)), which already builds the
  `<letter><major>.<minor>.g` name and passes the code's parameters as `param.*`; if no such macro
  exists, the code resolves as unsupported (`Command is not supported`, a warning, RRF's wording).
  This restores a fallback that is dead code today: the `NotSupportedException` catch in
  `Code.ProcessInternallyAsync` ([Code.cs:313](../../src/DuetControlServer/Commands/Generic/Code.cs))
  resolves the code as unsupported before `TryRunCodeMacroAsync` at
  [Code.cs:349](../../src/DuetControlServer/Commands/Generic/Code.cs) can run, so `M1234` never
  tries `sys/M1234.g` despite the comment there saying it must. Ending every miss in
  macro-then-unsupported is what RepRapFirmware's `default:` case does
  ([GCodes2.cpp:4789](../../lib/RepRapFirmware/src/GCodes/GCodes2.cpp)).
- The per-handler invalid-minor guards are deleted: their knowledge is now the absence of a row, and
  a handler can no longer be reached with a minor it does not implement. That removes the M201
  and M569 rejections in `MCodeHandler.Motion.cs`, the M558 rejection in `MCodeHandler.Probes.cs`,
  and the equivalents in M36, M505, M581 and M586. Handlers keep no minor logic at all: each
  fractional row's lambda passes what the minor decided (M36.1 against M36.2 is a boolean
  argument).
- `MCodeHandler.FlushAndWaitForStandstillAsync` and its 13 call sites
  (`MCodeHandler.Motion.cs`, `MCodeHandler.Compensation.cs`) are deleted; their guards become the
  resolvers above. Multi-phase codes that wait *inside* their sequence (G28, G30, a tool change,
  M400 itself) keep those internal waits: their row reads Barrier, and the pipeline's pre-dispatch
  wait subsumes only the wait a handler performed first thing.
- Both deferral implementations later hook the one `Deferred` branch; until one lands, that branch
  dispatches immediately, which is today's behaviour.

```mermaid
flowchart TD
    A[code reaches ProcessInternally] --> B{G / M / T code?}
    B -- no: keyword, comment --> D0[dispatch handler as today]
    B -- yes --> C["handler.Classify(code)"]
    C -- "null: no row" --> M0{"macro named<br/>after the code<br/>exists?"}
    M0 -- yes --> M1[run the macro]
    M0 -- no --> M2["resolve as unsupported"]
    C -- Immediate --> D1[dispatch handler, no flush]
    C -- Ordered --> F1["CodeProcessor.FlushAsync(code)<br/>(pipeline order + expressions)"] --> D2[dispatch handler]
    C -- Barrier --> F2["FlushAsync(code)"] --> W["planner.WaitForStandstillAsync()"] --> D3[dispatch handler]
    C -- Deferred --> F3["FlushAsync(code)"] --> G{"file channel and<br/>anchor exists? (§5.2, §5.3)"}
    G -- no --> D4[dispatch handler: applies now]
    G -- yes --> H["defer: §6 queue / §7 action"]
```

**Tests (`UnitTests`):**

- a data-driven test holding the expected class of every code in §3 and §9, fractional forms
  included, diffed both ways against the row tables' class columns, read statically. There is no
  handler side to diff: a row cannot be written without a handler, and a handler method without a
  row is unreachable, so the only drift left is between the tables and this document;
- a fractional code with no row never reaches its major's handler: `M906.1` runs `sys/M906.1.g`
  when the file exists, answers `Command is not supported` when it does not, and in neither case
  executes as `M906`;
- a regression pin for the mechanical move: the set of codes classified Barrier equals the set
  whose handlers waited before the move, commit for commit.

**Landing order.** First the types, the row tables and dispatch through them, deleting the
switches. This commit is behaviour-preserving: every row invokes what its switch arm invoked, and
the miss path temporarily reproduces today's outcomes (a fraction with no row falls back to the
bare-major row; an unknown major resolves as unsupported without the macro attempt). Then the
pipeline's class enforcement and the deletion of the 13 flush sites, behaviour-preserving by the
pin test. Then, each as its own visible commit, the changes in behaviour: the miss path proper
(the bare-major fallback goes, so `M906.1` and every other unlisted fraction take the
macro-then-unsupported path, and unknown majors regain the macro attempt); M409 loses a flush it
never needed; M999 B and M952 gain the standstill RRF never took (§3).

### 5.2 Only job-file channels defer, macros included

A code is deferred only when it comes from the job file or a macro the job invoked. From every other
channel it applies immediately:

- `M106 S128` typed into DWC mid-print is a manual intervention; the operator means *now*.
- Only a file code can be replayed. Surviving a pause depends on the purged work being exactly what
  the rewind re-reads; a code with no file position behind it cannot be re-created.

A job streamed over IPC (an external host feeding codes) is therefore **not** deferred, and keeps
today's behaviour. That is a decision, recorded here: such a stream has no rewind to replay against,
so the purge rule cannot hold for it.

Macros are included because a layer-change macro's `M106` belongs where the macro was called.
A pause inside a macro abandons it (`AbandonMacrosForPauseAsync`) and the resume re-runs it whole, so
every side effect in a macro is at-least-once, deferred or not; deferral adds no new failure mode.
The guard is `state.macroRestarted`, which exists in the object model and which nothing writes:
setting it on macro re-run after a pause is part of this work, so a macro can skip what must not
repeat.

### 5.3 The anchor

The anchor of a deferred code is the `MoveId` of the last move submitted on the channel's ring when
the handler runs. `MovePlanner` gains a `LastSubmittedMoveId(ring)` property, set in `QueueMove`
under the planner lock. If no move is in flight (nothing submitted, or everything submitted has
completed), the code executes immediately: with an empty queue there is nothing to synchronise with.
Ids skip zero on wrap, so comparisons use signed distance.

Codes that produce endstop-terminated moves (homing, probing, `G1 H`) are Barrier class and hold the
channel to standstill, so no deferred code can anchor to a move that may end early; §5.1's test
asserts that.

### 5.4 Purge and rewind are one number

`FeedholdOutcome.FirstPurgedMoveId` is already the source of the rewind point. It is also the
deferral purge boundary: deferred work anchored at or past it is dropped, because the rewind re-reads
those codes; work anchored before it is owed, because a feedhold never purges committed moves, so
those anchors will run.

| Pause purges | Deferred M106 anchored to B | Job rewinds to | Re-read? | Net |
| --- | --- | --- | --- | --- |
| C only | kept: B runs | C's position, after the M106 | no | fires once |
| B and C | dropped: B never runs | B's position, before the M106 | yes | fires once |

The awkward middle case, an anchor that ran but a code after the rewind point, cannot arise: the
rewind point *is* the first purged anchor. Stop, abort, M112 and `LinkService.Invalidate()` (link
loss, controller reset) discard everything pending; the moves the work was anchored to either drain
or no longer exist. Nothing pending is replayed on resume; machine state after a resume comes from
the restore point, which is the mechanism that already knows about tool state.

The safety invariant: **a purge must never be the reason the machine is unsafe.** Spindle and laser
shutdown belong to `stop.g` / `cancel.g`, which run afterwards, never to a deferred action the same
event just discarded.

### 5.5 M400 waits for deferred work

`HandleWaitForMovesAsync` is `FlushAndWaitForStandstillAsync`, and `MovePlanner.IsMoving` asks only
about submissions and the ring counters. It gains a third term: no deferred work pending. Without it,
`M106 S255` followed by `M400` returns before the fan changed, and M400 stops meaning "everything up
to here has happened". RepRapFirmware's standstill wait already includes `codeQueue->IsIdle()`.
The per-implementation mechanics are in §6 and §7.

### 5.6 Validate early, deliver late

The handlers already validate locally before sending (`GpioManager.WriteAsync` fails "Output 5 is
not configured" before any CAN traffic). That split is kept: configuration and parameter errors fail
the code synchronously on its own line; only what the board alone can know arrives late. Late
outcomes are uniform in both implementations:

- board acted but complained: `Model.Messages`, tagged with the originating code,
  `M106 S255 (deferred): fan 3 not configured`;
- delivery failure or no reply in time: a `MachineEvent`, the existing escalation path with a
  default action and a machine-overridable macro;
- a late continuation never writes the requested side of the object model: a later code may already
  have moved the same field.

For a deferred code, a successful reply means *accepted and scheduled*, not *done*, and
`fans[].requestedValue` means "requested and scheduled" rather than "requested and sent". The
requested/actual split the object model already carries absorbs this; it is stated here so it is a
decision rather than a discovery.

### 5.7 Emergency-stop output handling in Duet3Expansion

Verified current behaviour, and it is a prerequisite for deferring anything (and a gap today):

- `Platform::EmergencyStop` only schedules a deferred reset ~200 ms later
  ([Platform.cpp:1422](../../src/Duet3Expansion/src/Platform/Platform.cpp)); until the reset,
  `CommandProcessor::Spin` keeps executing arriving commands unconditionally.
- `ShutdownAll` switches off heaters and drivers only. **Fans and GPIO/PWM ports are never
  commanded off**; they hold their PWM until the processor reset returns the pins to reset state.

Work: on receiving `emergencyStop`, drive heaters, fans and GPIO outputs to their safe state
immediately, latch them so a command arriving in the window cannot undo the stop, and stop executing
command messages until the reset. This is needed whichever implementation is chosen, and it is
needed even if neither is.

---

## 6. Implementation A: the deferred-code queue

**Hold the whole code in DuetControlServer; run it when its anchor retires.** RepRapFirmware's
design rebuilt on this tree's primitives. No change outside DuetControlServer.

```mermaid
sequenceDiagram
    participant F as File channel
    participant Q as DeferredCodeQueue (DCS)
    participant M as Motion engine (native)
    participant B as Expansion board
    F->>F: M106 read, FlushAsync evaluates expressions, local validation
    F->>Q: enqueue {code, anchor = MoveId of move B}
    F-->>F: original code completes "ok" (accepted and scheduled)
    M->>M: move B retires (fitted clock, 1 ms tick)
    M->>Q: MoveCompletedEvent(moveId = B)
    Q->>B: code executes on the Queue channel, handler sends CanMessageSetFanSpeed
    B-->>Q: StandardReply, errors to Model.Messages, tagged with the origin
```

### The pieces

- **`Codes/DeferredCodeQueue.cs`**: a per-ring FIFO of `{Code, anchorMoveId, origin}`. `origin` is
  the code's short form, channel and file position, for tagging late messages.
- **Insertion**, in the `ProcessInternally` stage before handler dispatch: if the class table says
  Deferred, the channel is `File`, and `LastSubmittedMoveId` names a move that has not completed,
  then `FlushAsync(code)` (this evaluates expressions, so the queued parameters are literals),
  clone the code with `Channel = CodeChannel.Queue` and `File = null` (the pipeline matches stack
  items by file; null targets the Queue channel's base item), enqueue the clone, and resolve the
  original as `ok`. The gate requires `Channel == File`, so a released code cannot re-queue itself.
- **Release**: a hook on `MotionTracker.MoveCompleted`. A worker drains the queue head while
  `anchorMoveId` is at or before the completed id, executing each clone via
  `CodeProcessor.StartCodeAsync` and awaiting it before the next, so deferred codes stay FIFO. The
  class table guarantees Deferred handlers never block on motion, so the worker cannot stall the
  queue; the Queue channel exists in the enum and already has a full pipeline
  ([CodeProcessor.cs:45](../../src/DuetControlServer/Codes/CodeProcessor.cs)), currently fed by
  nothing.
- **Replies**: the Queue channel's `Executed` stage routes errors to `Model.Messages`, prefixed from
  `origin`. The handler is alive when the board answers, so a handler that needs to branch on the
  reply still can, which is a capability implementation B gives up.
- **Purge**: after `StopEarlyAsync`, drop entries with `anchorMoveId >= FirstPurgedMoveId`
  (§5.4); on stop/abort/M112/`Invalidate()`, clear. A synchronous pause (M25/M226 in the file)
  purges nothing; the queue drains under the standstill it already takes.
- **M400**: the §5.5 term is `queue.IsEmpty && Queue channel idle`, checked in the
  `WaitForStandstillAsync` poll loop.

### Timing

Release fires at the anchor's *predicted* end on the fitted clock (1 ms retirement tick), then the
event hop, the handler (which takes the object-model lock), the outbound ring, one SPI transfer and
one CAN frame. Typically **2 to 10 ms after the path point, always late, never early**. The tail is
unbounded: a GC pause or lock contention lands the effect further into the following move. At
50 mm/s, 10 ms is 0.5 mm. Endstop-terminated anchors are handled correctly for free, because their
retirement follows the actual stop.

### Semantics accepted

- The code's own result is `ok` at parse; failures the board reports arrive detached (§5.6). This is
  identical in implementation B.
- Plugins and IPC clients see the code twice: once resolving on `File`, once executing on `Queue`.
  This was the visible behaviour of the RRF SBC era.
- `fans[].requestedValue` updates at fire time, so the requested side of the object model lags the
  parser by the queue depth.

---

## 7. Implementation B: timestamped effects, parked on the board

**Run the code now; give its CAN messages a `whenToExecute`; the board executes them at that tick.**
The mechanism moves already use, extended to effects. Touches the schema, DuetControlServer,
DuetSbcInterface, Duet3Expansion, and one field DuetCANMaster honors without parsing.

```mermaid
sequenceDiagram
    participant H as Handler (DCS)
    participant L as Action list (DuetSbcInterface)
    participant C as DuetCANMaster
    participant B as Expansion board
    H->>H: M106 read, validated, object model written, frames built
    H->>L: DuetSbc_MotionSubmitAction {anchor = B, frames, txToken}
    H-->>H: code completes "ok"
    Note over L: move B prepared: due = moveStartTime + clocksNeeded
    L->>C: frames stamped whenToExecute = due, emitted with move B's dispatch
    C->>B: forwarded unchanged
    Note over B: parked in the command ring, CanMessageBuffer freed in the same pass
    B->>B: executes at the due tick (movement timebase)
    B-->>H: StandardReply, routed by txToken to the origin map
```

### 7.1 Protocol

`Schema/can-messages.json` is the single source; the generators emit the CANlib header, the C#
mirror and the conformance harnesses on both sides, so this is one schema change:

- **`whenToExecute` (uint32, movement timebase) on the six layouts whose generators can defer**:
  `SetFanSpeed`, `WriteGpio`, `SetHeaterTemperatureV1`, `FanParameters`, `HeaterFeedForwardV1`,
  and `Generic` (for `writeLedStrip`). Messages that can never defer do not carry the field; the
  board's gate treats an absent entry as "execute on arrival".
- A named sentinel, `CanWhenToExecuteImmediate = 0`, meaning execute on arrival; the SBC never emits
  a computed due time of 0 (it bumps to 1).
- `CanMessageGeneric.Data` shortens from 60 to 56 bytes to make room; real instances are variable
  length and far smaller, so only a parameter table packing more than 56 bytes is affected.
- **A generated offset table**, message type to `whenToExecute` offset, emitted like
  `CanMessageGenericTables` for both languages, with explicit no-entry rows for every other type
  including the eight never-sent ones. The board's gate and the SBC's stamping both read it, so the
  offset cannot disagree with the layout.
- A new broadcast, `CanMessageDropParkedCommands` (1 byte), see §7.4.

Wire growth, against CAN-FD DLC quantisation (0-8, 12, 16, 20, 24, 32, 48, 64):

| Message | Now | +4 | Wire cost |
| --- | --- | --- | --- |
| `WriteGpio` | 7 | 11 | DLC 8 → 12 |
| `SetFanSpeed` | 8 | 12 | DLC 8 → 12 |
| `SetHeaterTemperatureV1` | 9 | 13 | DLC 12 → 16 |
| `HeaterFeedForwardV1` | 16 | 20 | DLC 16 → 20 |
| `FanParameters` | 34 | 38 | free (DLC 48) |
| `Generic` | 64 max | 64 max | free (array shortened) |

### 7.2 DuetControlServer

- A Deferred handler validates and writes the object model exactly as today, builds its CAN frames
  (all of them: M106's fan speed and feedforward are one action), allocates the txToken as
  `LinkInterface.SendCanMessageAsync` does now, and calls
  `planner.SubmitAction(ring, anchorMoveId, frames, txToken, origin)` instead of sending. The code
  then completes.
- A deferred txToken maps to an `origin`, not a `TaskCompletionSource`: nothing awaits it. The reply
  handler routes a matched late reply to §5.6's outcomes; no reply by
  `whenToExecute + UsualResponseTimeout` raises the delivery-failure event. On any purge the
  outstanding mappings are dropped locally, or they leak.
- Effects with no CAN message (M117, M300, object-model-only writes) cannot ride this path. They are
  applied on the anchor's `MoveCompletedEvent`, which is implementation A's release hook: a reduced
  form of the queue exists inside implementation B regardless.

### 7.3 DuetSbcInterface

- `DuetSbc_MotionSubmitAction(handle, ring, anchorMoveId, header, payload, length)`: a lock-free
  submission ring beside `SubmitMove`, drained by `MotionService::SpinOnce` into a per-ring action
  list ordered by anchor id.
- **Resolution at the anchor's `DDA::Prepare`**: due = `m_afterPrepare.moveStartTime +
  m_clocksNeeded`, the end of the anchor. The end of the preceding move is correct where the start
  of the next is not: across a gap in the queue, an `M107` must not wait for the next move to be
  issued. The timestamp is patched at the generated offset and the frame emitted down the same
  outbound ring, in the same pass as the anchor's own dispatch, so it inherits the movement path's
  lead time.
- An action whose anchor is already committed when it is submitted resolves immediately from the
  committed DDA's times; a due time already in the past goes out as-is (the board executes it on
  arrival). An action whose anchor no longer exists goes out with the sentinel.
- **Purge**: `DDARing::PurgeAfter` already knows the purged ids; entries whose anchor was purged are
  dropped from the list in the same operation. Nothing needs to reach the boards on a pause or stop:
  every action already sent has a committed anchor, committed moves always run (§4), so every parked
  command is owed and fires at its tick before the machine reaches standstill.
- **M400 term**: `DuetSbc_MotionActionsPending(ring)` = the list is non-empty, or the last emitted
  due time has not yet passed `GetMovementTimerTicks()`. The check lives here because only the
  native side has the movement-timebase clock.
- Discarded whole on link loss or controller reset, with the move ring.

### 7.4 Duet3Expansion

- **The gate**, at the top of `CommandProcessor::Spin`: look the message type up in the offset
  table; no entry or the sentinel or a past time means dispatch as today. A future time (signed
  comparison against `StepTimer::GetMovementTimerTicks()`, the same call the motion path uses, so
  parked commands slip with the path) means **copy into the parked ring and free the
  `CanMessageBuffer` in the same pass**. That is the invariant `Move::TaskLoop` already keeps for
  movement messages, and it is what protects the buffer pool: 40 buffers, 10 on a SAMC21, and a
  receiver that blocks rather than drops when the pool empties, after which the hardware FIFO (32
  deep, 16 on SAMC21) overruns silently.
- **The parked ring**: fixed depth `ParkedCommandRingSize` (16, 8 on SAMC21), each entry
  `{dueTime, msgType, requestId, dataLength, payload[64]}`. Every `Spin` pass executes entries whose
  due time has passed, in due order, ties broken by insertion order; the reply is built at execution
  from the entry. The parked set at any instant is the actions inside the committed window, so depth
  is driven by action *rate*; a long move parks its trailing action for its whole duration
  (unbounded, §4), which consumes a slot but not more.
- **Overflow**: execute the parked entry with the nearest due time and park the arrival in its slot;
  if the arrival is itself nearest due, execute it and park nothing. Executing the *arrival* would be
  wrong: arrivals are normally the latest-due of the set, and firing one ahead of an earlier-due
  command that addresses the same output ends in a state no line of the file asked for
  (fan 100% due later overtaken by fan 50% due sooner ends at 50%). Nearest-due keeps the sequence;
  the error is one command early, the one closest to firing anyway. Early and late executions are
  counted and reported in M122.
- **`DropParkedCommands`**: empties the ring, idempotent, handled inline in the receiver task like
  `emergencyStop`. Sent (repeatedly, the bus guarantees nothing) only with an emergency stop, as
  belt and braces for the window before the board's reset; pauses never send it (§7.3).

### 7.5 DuetCANMaster

One change, and it stays ignorant of message layouts. `pendingRequests` (32 slots) expires a slot at
`whenStarted + UsualResponseTimeout` (1000 ms), so a reply to a command parked longer than a second
would be orphaned. `SendCanMessageHeader` has three spare padding bytes; two become
`uint16 replyTimeoutExtra` (units of 100 ms, 0 = today's behaviour), filled by DCS with the parked
lifetime it already knows. Slot expiry becomes `whenStarted + UsualResponseTimeout +
replyTimeoutExtra`. The transport honors a number without understanding the payload, and the struct
does not grow.

### 7.6 Timing

Exact: the effect lands on the step clock, in the movement timebase, immune to SBC scheduling, GC
and lock contention, because nothing on the SBC is in the firing path once the frame is sent.
The lead a board sees is the movement path's own (~50 ms, already measured by the boards'
`minAdvance`/`maxAdvance` diagnostics).

### 7.7 The M572 limit, in either implementation

The board bakes pressure advance into segments at message *arrival*
([Move.cpp:1158](../../src/Duet3Expansion/src/Movement/Move.cpp)), and movement messages arrive up to
the send-ahead horizon early. A PA push timed exactly at a move boundary therefore still misses every
move whose message already arrived: up to ~50 ms of motion. Implementation A's release a few
milliseconds after the boundary has the same property. So deferring the push shrinks M572's error
from "everything queued board-side" to "at most one horizon", in both implementations equally, and
removing its standstill is a separate decision: accept that staleness, or carry the coefficient on
the movement message as MOTION_CONFIG_ORDERING did on the SBC side. Until decided, the standstill
stays.

---

## 8. Comparison

| | A: deferred-code queue | B: board timestamps |
| --- | --- | --- |
| Accuracy | 2-10 ms late, one-sided; unbounded tail (GC, object-model lock) | exact to the step clock |
| Firing path | managed runtime, the process the native library exists to keep out of timing | board ISR-adjacent, nothing on the SBC in the path |
| Handler can branch on the board's reply | yes, it is alive when the reply arrives | no: reply is attributed to the origin after the fact |
| Code's own result | `ok` at parse, late errors detached | identical |
| Plugins / IPC stream order | code visible twice (File, then Queue) | preserved: one code, one execution |
| Requested object model | written at fire time, lags the parser | written at parse, runs ahead of the machine |
| Expressions | evaluated at parse, frozen into the queued code | evaluated at parse, frozen into the frames |
| Endstop-terminated anchors | correct by construction (retirement follows the stop) | excluded by the Barrier rule (§5.3) |
| Local effects (M117, M300) | same mechanism as everything else | need A's release hook anyway |
| Purge | one list, in-process | SBC list plus the estop broadcast plus the CANMaster expiry field |
| Codebases touched, one-time | DuetControlServer | schema, DuetControlServer, DuetSbcInterface, Duet3Expansion, DuetCANMaster (one field) |
| Per new deferred code | DuetControlServer only | DuetControlServer only; plus a schema field if the message type lacks `whenToExecute` |
| Multi-board simultaneity | no (N sends, serialised) | yes: same tick on every board; a broadcast "all fans off at T" is one frame |
| Headroom | anything content with ~10 ms | per-segment effects (laser pixels, M42-triggered hardware), effects that must not jitter |

What decides it:

1. **Does anything need better than ~10 ms, one-sided?** Everything in §9 today is content: a fan
   takes ~100 ms to spin up, a heater ramps over seconds, a servo transits in tens of milliseconds.
   The two candidates that would not be content are per-pixel laser data (open, §11) and `M42`
   triggering external hardware (hypothetical). If either becomes real, only B serves it.
2. **Are managed-runtime tails acceptable in the firing path?** A GC pause moves an effect further
   into the following move. For a fan, invisible; as a matter of architecture, it re-enters the
   process the native split was designed to exclude.
3. **Cost and blast radius.** A is one codebase and reversible; B is four plus the schema, and its
   lifecycle (parked rings, overflow, the estop window) is where the subtle cases live.

The designs compose. They share §5 entirely; A's release hook is B's local-effect mechanism; the
class table is where a code's mechanism is recorded. A can ship first and individual codes can be
promoted to B when a customer needs exactness, message type by message type.

---

## 9. The codes to defer

RepRapFirmware's queue list, verified, as the reference to tick off. A code deferred here that RRF
applies immediately, or the reverse, is a chosen difference, not a gap.

| Code | RRF condition | What RRF sends | What DSF sends | Done |
| --- | --- | --- | --- | --- |
| M3 | only when not in laser mode | nothing: local spindle pins | up to three `WriteGpio` via `GpioManager` | ⬜ |
| M4 | always | nothing: local pins | `WriteGpio` | ⬜ |
| M5 | only when not in laser mode | nothing: local pins | `WriteGpio` | ⬜ |
| M42 | always | `WriteGpio` when the port is remote | `WriteGpio` | ⬜ |
| M104 | always | `SetHeaterTemperatureV1` per remote heater | the same | ⬜ |
| M106 | always | `SetFanSpeed`, or `FanParameters` when it carries `T/B/L/X/H/C`, plus `HeaterFeedForwardV1` for a tool fan | the same, one anchor for the set | ⬜ |
| M107 | always | `SetFanSpeed` pwm 0 (+ feedforward) | the same | ⬜ |
| M117 | always | nothing: object model and PanelDue | nothing: object model, applied at the anchor | ⬜ |
| M140 | always | `SetHeaterTemperatureV1` | the same | ⬜ |
| M141 | always | `SetHeaterTemperatureV1` | the same | ⬜ |
| M144 | always | `SetHeaterTemperatureV1` | the same | ⬜ |
| M150 | when the strip does not need standstill | `Generic`/`writeLedStrip` when remote | the same, always remote | ⬜ |
| M280 | always | `WriteGpio` with `isServo` | the same | ⬜ |
| M300 | always | nothing: local buzzer | nothing: `state.beep`, applied at the anchor | ⬜ |
| M568 | always | `SetHeaterTemperatureV1`; spindle RPM local | `SetHeaterTemperatureV1` + `WriteGpio` for the RPM | ⬜ |
| `G10` | tool temperatures, no axis letter | `SetHeaterTemperatureV1` | the same | ⬜ |

RRF's remaining gates and their counterparts: `DoingFile()` is §5.2; `!ContainsExpression()` is not
needed (parameters are evaluated before the work is captured, §6/§7); the 64-byte limit is not
needed; `scheduledMoves != completedMoves` is the anchor-exists rule (§5.3); `segmentsLeft == 0`
falls out of anchoring by move id rather than by count. M291 does not exist here yet; RRF's reasons
for never queueing it are about deferring a *blocking* code and must be revisited when it lands.
M109, M190, M191 and M116 are barriers by definition: they block later G-code on a condition derived
from the target, so the target must be in force before the wait begins.

---

## 10. Verification

Shared, offline:

- the class table matches §3, asserted by a unit test, and the same code from a non-file channel is
  never deferred;
- purge equals rewind: for a pause at an arbitrary point, every deferred unit either fires once or
  is re-created by the replay, never both and never neither, driven by `FirstPurgedMoveId` from the
  existing `DdaRingTests` feedhold states;
- M400 does not return while deferred work is pending.

Implementation A: release order and timing against synthetic `MoveCompletedEvent`s; the
Queue-channel FIFO property; the purge hooks.

Implementation B, in `DdaRingTests` (no hardware, fake clock):

- an action resolves to its anchor's `moveStartTime + clocksNeeded` and is emitted in the anchor's
  own prepare pass;
- a third move chained after the anchor leaves the due time unchanged, and a *gap* before the third
  move also leaves it unchanged (this distinguishes end-of-anchor from start-of-next, which coincide
  for chained moves);
- an action submitted after the last move in the queue still resolves; an action whose anchor a stop
  purges is dropped, one whose anchor ran is not.

Implementation B, board side (`CommandProcessor` is ordinary C++): past/sentinel times dispatch on
arrival; a future time parks, frees the buffer in the same pass, and fires at its tick; a full ring
executes nearest-due and parks the arrival; two fan speeds end in the state the latest-due asked
for however the ring overflowed; the drop broadcast empties the ring and is idempotent.

On hardware: `M106 S255` mid-print, confirming the machine does not pause and the fan changes at the
right point in the path.

---

## 11. Order of work and open decisions

1. **The class table and dispatch through it** (§5.1). DuetControlServer only;
   behaviour-preserving until the final commits, which land one change each: the miss path
   (fractional enforcement and the restored macro fallback), then the rows that flip (M409,
   M999 B, M952).
2. **Emergency-stop output handling in Duet3Expansion** (§5.7). A live gap independent of this plan.
3. **`state.macroRestarted`** written on macro re-run after a pause (§5.2).
4. **The implementation decision** (§8), then:
   - A: the queue store, release hook, purge hooks, M400 term; then convert the §9 codes, M106
     first.
   - B: the schema change and offset table; the parked ring; `SubmitAction` and resolution in
     DuetSbcInterface; the DCS action path and late-reply routing; the lifecycle rules; the
     CANMaster expiry field; then convert the codes.

Open decisions:

| | Question | Why it matters |
| --- | --- | --- |
| D1 | Which implementation, or A now with promotion to B per code later (§8)? | The whole of §6 vs §7 |
| D2 | Do per-pixel laser segments need the action timeline, or does pixel data ride the move record? Laser *power* must scale with the move's actual top speed, so it belongs on the move either way (`MoveBuilder` carries a `TODO` for `controlLaserOrIoBits`); the question is the pixel stream. | If pixel data needs per-segment actions, only B serves it, and the parked ring must be sized for segment rate |
| D3 | M572: accept ≤ one horizon of stale pressure advance and drop the standstill, or carry the coefficient on the movement message (§7.7)? | Closes the `TODO` in `MCodeHandler.Motion.cs` |
| D4 | Two motion systems: deferred work belongs to one ring, and the feedhold today stops only ring 0 (a shared `TODO` with M596). | Answer together with M596, not separately |
