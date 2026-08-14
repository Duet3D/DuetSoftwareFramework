# Stall detection: plan to make `M574 S3`/`S4` work

A plan, not a description of what is there. It says what stall detection has to do, what the four
programs do about it today, which five things are wrong, and in what order to fix them. Everything in
§5 is a proposal that needs sign-off before it is written, because it changes the SPI wire format, as
does the shared rules header in §7.1.

Read [endstops.md](src/Documentation/articles/endstops.md) first. Stall detection rides on the endstop path described
there - the same arming, the same stop, the same wind-back - and this document only covers where the
two diverge. §13 of that document lists the endstop limits that stall detection turns from
theoretical into routine.

---

## 1. What a stall endstop is, and why it is not a switch

A switch is an input on a pin. Some board owns that pin, `M574` asks it to watch it, and from then on
the board reports the pin changing state under a handle derived from the axis. The move only has to
name the handle.

A stall is not an input. The **driver** decides it has stalled, by comparing the back-EMF it measures
against the speed it was told to expect, so nothing can be watched until the driver has been told
what that speed is. Three consequences follow, and all of the difficulty comes from them:

1. **Arming is per move.** A driver has to be told the expected speed of *this* move before it runs,
   and untold afterwards. A driver left armed reports a stall during an ordinary move.
2. **One handle for a whole board.** A board reports every driver that stalled under the single
   handle `(typeStallEndstop, 0, 0)` = `0x5000`, with the stalled drivers as a **bitmap** in the
   field an analogue input would use for its reading. The handle says nothing about which driver,
   which axis, or which endstop; only the bitmap does.
3. **One report per arming.** The board clears a driver's armed bit as it reports it
   ([TMC22xx.cpp:919](src/Duet3Expansion/src/Movement/StepperDrivers/TMC22xx.cpp#L919)). The other
   armed drivers stay armed. Nothing has to re-arm mid-move, and nothing may rely on a second report
   from the same driver.

---

## 2. The reference: what RepRapFirmware does

[StallDetectionEndstop](lib/RepRapFirmware/src/Endstops/StallDetectionEndstop.cpp) is an `Endstop`
subclass alongside `SwitchEndstop`, so the step interrupt treats the two alike.

| Step | Where | What happens |
|---|---|---|
| Arm | [`PrimeAxis`](lib/RepRapFirmware/src/Endstops/StallDetectionEndstop.cpp#L55) | Asks the kinematics for the controlling drives, sets `stopAll` if drives other than the axis' own are involved, and sends `CanMessageEnableStallEndstop{driver, speed}` per remote driver at `abs(speed × stepsPerMm)`. Throws and disarms if any board refuses. |
| Report | [`HandleStalledRemoteDrivers`](lib/RepRapFirmware/src/Endstops/StallDetectionEndstop.cpp#L293) | Fed from [CommandProcessor.cpp:194](lib/RepRapFirmware/src/CAN/CommandProcessor.cpp#L194) with the board address, **the reading as a bitmap of that board's stalled drivers**, and the trigger timestamp. Records only the drivers it is monitoring. |
| Decide | [`GetResult`](lib/RepRapFirmware/src/Endstops/StallDetectionEndstop.cpp#L144) | Extruder endstop → `stopAll`; `stopAll` flag → `stopAll`; `individualMotors && numDriversLeft > 1` → `stopDriver`; otherwise → `stopAxis`. |
| Escalate | [`Acknowledge`](lib/RepRapFirmware/src/Endstops/StallDetectionEndstop.cpp#L230) | After a `stopDriver`, drops that driver from the monitored set and decrements `numDriversLeft`, so the **last** remaining motor escalates to `stopAxis`. |
| Disarm | `DisableRemoteStallEndstops` | One `CanMessageEnableStallEndstop{driverNumber = disableAll}` per board. |

Three actions, and which one is taken depends on the geometry and on `M574 S3` vs `S4`:

| | Independent axis | Coupled axis (CoreXY, delta, …) |
|---|---|---|
| `S3` MotorStallAny | `stopAxis` — every motor of the axis stops | `stopAll` — the whole move stops |
| `S4` MotorStallIndividual | `stopDriver` while more than one motor is left, then `stopAxis` | `stopAll` — `stopAll` outranks `individualMotors` |

Separately, and nothing to do with endstops, [`Move::PollOneDriver`](lib/RepRapFirmware/src/Movement/Move.cpp#L3632)
raises a `driver_stall` event for drivers configured with `M915 R1`. That is the "stalled during a
print" path.

**Duet3Expansion** validates the speed against the driver's stall window before arming
([`CheckStallDetectionEnabled`](src/Duet3Expansion/src/Movement/StepperDrivers/TMC22xx.cpp#L1307)),
enables the DIAG interrupt, and puts stall notifications **first** in the outgoing
`CanMessageInputChangedV2` so they cannot be crowded out
([CanInterface.cpp:1017](src/Duet3Expansion/src/CAN/CanInterface.cpp#L1017)). None of this needs to
change; the expansion side is already what RRF talks to.

---

## 3. What DSF has today

More than it looks. The configuration and arming halves are ported and correct:

- `M574 S3`/`S4` map to `EndstopType.MotorStallAny`/`MotorStallIndividual`, and
  [`CreateEndstopMonitorAsync`](src/DuetControlServer/Codes/Handlers/MCodeHandler.Motion.cs#L2249)
  correctly asks for no input monitor, because there is no pin.
- `M915` reaches every addressed board and is recorded under `boards[].drivers[].config.stallDetection`
  ([MCodeHandler.Motion.cs:1105](src/DuetControlServer/Codes/Handlers/MCodeHandler.Motion.cs#L1105)),
  including `R`, which is what makes the board raise `driver_stall`.
- [`StallEndstopKind.PrepareAsync`](src/DuetControlServer/Motion/EndstopKinds.cs) sends what
  `PrimeAxis` sends, run before the move and released in a `finally`
  ([GCodeHandler.cs](src/DuetControlServer/Codes/Handlers/GCodeHandler.cs)). Because `G1 H1` awaits
  `FinishSpecialMoveAsync`, the arming really does span the move.
- The `driver_stall` event already runs `driver_stall.g` through
  [EventProcessor](src/DuetControlServer/Events/EventProcessor.cs).
- The move carries the stall handle per driver, and DuetCANMaster's
  [`StopDriversWatchingInput`](src/DuetCANMaster/src/CAN/CanMotion.cpp#L577) is on the right side of
  the bus to act on it in time.

So the pieces are in place and wired together. What is wrong is what happens between a board saying
"driver 2 stalled" and the right motors stopping.

---

## 4. The five defects

The first four are behavioural. The fifth is structural, and is why §4.4 happened.

### 4.1 The stalled-driver bitmap is discarded ✅ Phase 3

[`HandleInputStateChanged`](src/DuetCANMaster/src/CAN/CommandProcessor.cpp#L162) reads `msg.states`
and `results[i].handle`, and never `GetEntryReading(i)`. For a switch that is right - the handle
identifies the switch. For a stall the handle is `0x5000` for every driver on every board, so
discarding the reading discards the only thing that says *which motor stalled*.

What it costs:

- Every armed driver on the reporting board stops, whichever one stalled.
- `S4` cannot be told from `S3` for motors sharing a board. Phase 3 makes that expressible; §4.3 is
  what still has to act on it.
- **Homing two stall-homed axes together silently mis-homes one of them.** `G1 H1 X-300 Y-300` with
  `S3` on both and the motors on one board: a stall on X stops Y as well, both are reported in
  `MotionStopped`, and [`TrySendRevert`](src/DuetControlServer/Motion/EndstopCorrection.cs#L522) marks
  **both** axes triggered. Y is recorded as homed wherever it happened to be. No error is produced.

### 4.2 There is no `stopAxis` on the wire ✅ Phase 4

The only escalation the protocol has is `ScheduleMoveFlags::StopAllDrivers`, which stops every driver
of the whole move and is set only for coupled kinematics
([EndstopArming.cs:116](src/DuetControlServer/Motion/EndstopArming.cs#L116)). RRF's middle action,
`stopAxis` - stop every motor of *this drive* - cannot be expressed.

For a dual-motor stall-homed axis with its motors on **different boards**, a stall reported by board 1
stops only board 1's motor. Board 2's motor runs to the full commanded length. And because
[`NoteDriverStopped`](src/DuetControlServer/Motion/EndstopCorrection.cs#L386) waits for every driver
of a drive before adopting a corrected position, the drive is never adopted, the axis never enters
`EndstopsTriggered`, and the move concludes `armed but never triggered`. This is the case that reads
as "stall detection does nothing at all".

This is the same gap §13 of [endstops.md](src/Documentation/articles/endstops.md) records for switches ("stopping
every driver *of one drive* has no flag, only stopping every driver of the move"). Stall detection
makes it routine rather than rare, because a stall-homed axis is usually the multi-motor one.

### 4.3 `MotorStallAny` and `MotorStallIndividual` are not distinguished ✅ Phase 4

One [`StallEndstopKind`](src/DuetControlServer/Motion/EndstopKinds.cs) answers `Handles` for both
types and treats them identically. Nothing ports `individualMotors`, `numDriversLeft`, or the
escalation to `stopAxis` on the last remaining motor. `S4` therefore cannot square a gantry by stall homing, which
is the reason `S4` exists.

### 4.4 The board a driver watches is not always its own

[`TryGetStallStopInput`](src/DuetControlServer/Motion/RemoteEndstops.cs#L193) fills `boards[]` from
the drivers of every *controlling* drive of the axis, but the native builder hands those boards out
to the drivers of each drive - by index for an ordinary move, and round-robin across the move's
drivers for a `stopAll` one ([DDA.cpp:864](src/DuetSbcInterface/src/Movement/DDA.cpp#L864)). That
round-robin is right for switches, where any port of the endstop stopping everything is the whole
point, and wrong for stalls, where a driver can only ever be stopped by *its own* stall.

It does no harm today, because `stopAll` stops everything anyway and the bitmap is ignored. It
becomes a correctness bug the moment §4.1 is fixed, so it has to be fixed first.

### 4.5 The two kinds of endstop are armed in different places ✅ Phase 1

A stall endstop was armed from `HandleMoveAsync`, before the move was built. A switch was armed from
`EndstopArming.TryArmAxis`, while it was being built. Two files, two call sites, and nothing that
said the pair existed.

RepRapFirmware has one seam - `Endstop::PrimeAxis`, virtual, and both subclasses do their CAN work in
it. So that was a divergence from the reference rather than a port of it, and it was not free: §4.4 is
precisely what the split produced. "Which drivers does this axis' stall endstop watch" was worked out
twice, once for the boards and once for the move, from the object model at two different moments,
with nothing comparing the answers. The same shape is what leaves the switch half of `PrimeAxis` -
re-enabling a remote handle and re-reading its state per move - simply absent, with nothing to notice
it is missing; that is now an unimplemented `PrepareAsync` on
[SwitchEndstopKind](src/DuetControlServer/Motion/EndstopKinds.cs) rather than a code path nobody
thought to add.

§5.4 was the fix and Phase 1 did it, before anything else touched either site.

---

## 5. Design decisions

§5.2 changes the SPI wire format between DuetSbcInterface and DuetCANMaster, so it wants agreeing
before Phase 4 is written. §5.1, §5.3 and §5.4 are done and are kept here because every phase after
them reads the shapes they describe.

### 5.1 A driver watching a stall watches its own board

**Proposal.** When the stop handle's type is `typeStallEndstop`, the native builder ignores
`MoveStopInput::boards[]` and uses the board of the driver it is emitting. `boards[]` stays as it is
for switches and Z probes.

*Why not fix it in `TryGetStallStopInput` instead:* the pairing depends on which drive a driver
belongs to, and on `stopAll` that mapping is rewritten after the arming decision has been made. The
board of the driver being emitted is known exactly where the record is built and nowhere earlier.

The rule then holds in every arrangement without a special case: a single-motor axis watches its own
board, each motor of a multi-motor axis watches its own, and on coupled kinematics every driver of
the move watches its own while `StopAllDrivers` makes any of them stop everything - which is RRF's
`stopAll`.

### 5.2 Stop groups, to express `stopAxis`

**Proposal.** Spend the two spare bytes at the end of `ScheduleMoveDriver` (offset 14, currently
`padding`; the struct stays 16 bytes and every existing `static_assert` still holds):

```c
struct ScheduleMoveDriver {
    uint8_t boardAddress;
    uint8_t driverNumber;
    uint8_t isExtruder;
    uint8_t stopOnBoard;
    int32_t steps;
    float extrusion;
    uint16_t stopOnHandle;
    uint8_t stopGroup;      // NEW: drivers stopped together, or NoStopGroup
    StopAction stopAction;  // NEW: none | driver | group | all
};
```

- `stopGroup` is the logical drive the driver belongs to, or `NoStopGroup = 0xFF`.
- `stopAction` is RRF's `EndstopHitAction`, one byte: `driver` stops only the matched motor while its
  group has others still running, `group` stops every driver of the group, `all` stops every driver
  of the move.

**`stopAction` absorbs `ScheduleMoveFlags::StopAllDrivers`, which is deleted.** The action belongs to
the endstop that fired, not to the move: that is what RRF models, and a per-move flag only works
because `EndstopArming` refuses to home a coupled axis alongside another axis - a DSF limitation
rather than a wire one, and Phase 6 lifts it. It also removes a field rather than adding one. The SBC keeps `MoveFlags::StopAllDrivers` for the DDA's round-robin switch assignment,
which never crossed the wire.

`NoStopGroup` and `StopAction` are declared in `StopRules.h` beside the rules that read them, not in
`MessageFormats.h` - see §7.1.

DuetCANMaster's `StopDriversWatchingInput` then resolves a trigger in three steps, which is exactly
RRF's `GetResult`:

1. The matched watch's action is `all` → stop every driver of the move (`stopAll`).
2. It is `driver` and its group has more than one driver still running → stop only that driver
   (`stopDriver`).
3. Otherwise → stop every driver sharing its `stopGroup` (`stopAxis`).

That decision is `DecideStop` in §7.1, so it is one function that the firmware calls and the tests
call, rather than a rule written out here and again in `CanMotion.cpp`. The escalation reads
`stillRunning`, which the controller clears as it stops each driver.

The action needs a per-drive field on the SBC side too, so `MoveStopInput` gains `stopAction` and the
padding byte the compiler would insert after it, taking it from 12 to 14. Both the C# mirror in
[MoveParams.cs](src/DuetControlServer/Motion/Native/MoveParams.cs) and the layout tests move with it.

*Why a group id rather than a second "stop this drive" flag:* the group is what the switch case needs
as well. An axis with fewer switches than drivers currently keeps only its first switch, because the
alternative was stopping the whole move (§13 of endstops.md); with groups it can watch every switch
and stop the drive. One mechanism closes both.

*Alternative considered and rejected:* sending the axis number and having the controller look up its
drivers. The controller has no axis-to-driver map and should not acquire one - keeping it free of
machine configuration is what lets every configuration decision stay in DuetControlServer.

### 5.3 Matching a stall report against a watch

With §5.1, a stall watch is always `(the driver's own board, 0x5000)`. A trigger from board *B* with
bitmap *M* matches a watch when

```
watch.inputHandle == 0x5000 && watch.driver.boardAddress == B && (M & (1 << watch.driver.localDriver))
```

Handles of any other type keep the existing `(board, handle)` comparison and ignore the reading.
`CanMessageInputChangedV1` carries a reading too, so both versions work; V1 still carries no
timestamp, as §13 of endstops.md records.

This is `WatchMatches` in §7.1 - written once, in the header both sides compile.

### 5.4 One seam for both kinds of endstop

A stall endstop was armed in `GCodeHandler.HandleMoveAsync` and a switch in
`EndstopArming.TryArmAxis`, two files and two call sites apart. That is not RepRapFirmware's shape.

**RepRapFirmware has one seam.** `PrimeAxis` is pure virtual on
[`Endstop`](lib/RepRapFirmware/src/Endstops/Endstop.h#L107) and *both* subclasses do CAN work in it:
[`SwitchEndstop::PrimeAxis`](lib/RepRapFirmware/src/Endstops/SwitchEndstop.cpp#L147) calls
`CanInterface::EnableHandle` per remote port and refreshes each switch's state,
`StallDetectionEndstop::PrimeAxis` calls `EnableRemoteStallEndstop`.
[`EnableAxisEndstops`](lib/RepRapFirmware/src/Endstops/EndstopsManager.cpp#L195) loops the axes,
calls the virtual, and collects `ShouldReduceAcceleration`. One loop, one call, whatever the kind.

**Why DSF cannot copy that literally.** [`MovePlanner.Lock()`](src/DuetControlServer/Motion/MovePlanner.cs#L138)
returns a `Lock.Scope` - a synchronous `System.Threading.Lock` - and `BuildRawMove` is a synchronous
method called inside both it and `await model.AccessReadWriteAsync(...)`. Nothing reachable from
`EndstopArming.Arm` can await, so the CAN round trip cannot happen there. This is a compile-time
fact, not a policy.

**Proposal: keep the two phases, but dispatch both from one place.** A strategy per endstop kind,
carrying both halves, so a developer adding a kind implements both on one type and the compiler is
what notices if they do not:

```csharp
internal interface IEndstopKind
{
    // A predicate rather than a property, because one kind covers both S3 and S4 until §4.3 is fixed
    bool Handles(EndstopType type);
    bool ReducesAcceleration { get; }

    // The CAN half. Runs before the move is built, with no locks held.
    // A no-op for a switch and a Z probe today - see below for why that is worth keeping.
    ValueTask<Message> PrepareAsync(EndstopPlan plan, EndstopArmingState state, LinkInterface link,
                                    CancellationToken cancellationToken);

    // Undo PrepareAsync, however the move ended. Per move rather than per axis: one message
    // disables every stall endstop on a board, so two axes must not release twice
    ValueTask ReleaseAsync(EndstopArmingState state, LinkInterface link, ILogger logger,
                           CancellationToken cancellationToken);

    // The decision half. Runs inside EndstopArming.Arm, where nothing may await
    string? TryArm(EndstopPlan plan, MoveStopInput stopInput);
}
```

The live definition is [EndstopKinds.cs](src/DuetControlServer/Motion/EndstopKinds.cs); this is here
for the argument, not as a second copy to keep in step.

The two phases read one `EndstopPlan` per named axis - the axis, its kind, the drivers to watch and
the steps per second they should expect - computed **once**, under one read lock, before either
phase runs. That is what removes the duplication behind §4.4: the arming that went over the bus and
the arming written into the move each worked out "which drivers does this axis' stall endstop watch"
from the object model independently, at different moments, with nothing making them agree.

The speeds go into the plan with the drivers, so there is one estimate rather than one per phase.
It is still taken from the code rather than from the built move, because the arming has to reach the
boards before the move exists - RepRapFirmware does the same and calls its own calculation an
approximation, in [GCodes.cpp:2498](lib/RepRapFirmware/src/GCodes/GCodes.cpp#L2498).

*Why a strategy type rather than methods on `Endstop`:* `DuetAPI.ObjectModel.Endstop` is a
serialised, observed object-model class. Behaviour on it would cross the API boundary and be visible
to every client. The dispatch is therefore a `switch` on `EndstopType` in one factory, which is the
same single point of failure RRF's vtable is.

*What this also buys:* `PrepareAsync` being a no-op for switches is a gap rather than a fact.
`SwitchEndstop::PrimeAxis` re-enables each remote handle and re-reads its state every move, which is
what makes a board that reset mid-job fail loudly instead of silently never reporting - the second
Known Limit in §13 of endstops.md. With the seam in place that becomes an implementation of an
existing method rather than a new code path someone has to think to add.

*Alternative considered and rejected:* make `BuildRawMove` async and `planner.Lock()` an async lock,
so one method really can do both. It would hold the planner lock across a CAN round trip of several
milliseconds, and that lock serialises move building for every input channel. Correct-looking, and it
would make every other channel wait on the bus.

---

## 6. The work, in order

Each phase is meant to be committable on its own and to leave the tree no worse than it found it.
The order is forced: §4.1 cannot be fixed before §4.4, and §4.3 cannot be fixed before §4.2. Phase 1
comes first because every later phase edits one or both of the two places it merges.

Kept current as the work lands, not afterwards: a phase moves to ✅ in the same commit that does it,
and anything it turned out to need that this document did not predict is written into that phase
rather than left for a reader to find in the diff.

Phases are named by their commit subject rather than by a hash, so that a phase can be ticked in the
same commit that does it; `git log --grep` finds them.

| Phase | What | Status |
|---|---|---|
| 1 | One seam for both kinds | ✅ `refactor: arm both kinds of endstop through one seam` |
| 2 | A driver watches its own board | ✅ `fix: make a stall watch name the driver's own board` |
| 3 | The controller reads the stalled-driver bitmap | ✅ `fix: stop only the driver that stalled` |
| 4 | Stop groups and the three stop actions | ✅ `feat: give a move's endstops the three stop actions` |
| 5 | `S3` and `S4` told apart | ✅ folded into Phase 4 |
| 5a | The move id in `MotionStopped` | ✅ same commit as Phase 4 - see below |
| 6 | A group is the coupling set, not the drive | ✅ `feat: home any number of axes whose drives do not overlap` |
| 7 | The motor-stall Z probe | ⬜ |
| 8 | Diagnostics | ⬜ |

**Nothing is fixed on a machine until Phase 3.** Phase 1 changed no behaviour, and Phase 2 only makes
the bitmap safe to act on. `M574 S3`/`S4` behave exactly as §4 describes until then.

### Phase 1 — one seam for both kinds (§5.4) ✅

| | |
|---|---|
| Touches | new [EndstopKinds.cs](src/DuetControlServer/Motion/EndstopKinds.cs) and [EndstopPlan.cs](src/DuetControlServer/Motion/EndstopPlan.cs) beside [EndstopArming.cs](src/DuetControlServer/Motion/EndstopArming.cs), [GCodeHandler.Endstops.cs](src/DuetControlServer/Codes/Handlers/GCodeHandler.Endstops.cs), [GCodeHandler.cs](src/DuetControlServer/Codes/Handlers/GCodeHandler.cs), [MoveInterpreter.cs](src/DuetControlServer/Motion/MoveInterpreter.cs), [RemoteEndstops.cs](src/DuetControlServer/Motion/RemoteEndstops.cs) |
| Wire format | unchanged |
| Behaviour | none intended, and none observed - the suite passed unchanged |
| Tests | `EndstopArmingTests` and `MoveInterpreterTests` go through the planner rather than hand-built inputs, so they exercise both phases; new `EndstopPlannerTests` covers what the seam guarantees. 897 passing, from 887 |

`IEndstopKind` dispatches on `Handles(EndstopType)` rather than exposing a `Type`, because one kind
covers both `S3` and `S4` today: telling them apart in Phase 5 is splitting one class rather than
changing the table. `EndstopArming.TryArmAxis` and its `switch` are gone.

Two things the phase needed that this document had not predicted, both kept:

- **Planning is separate from arming.** `PlanEndstopsAsync` runs before `PrepareEndstopsAsync` sends
  anything. The old shape took the boards it had armed from the *return* of the arming call, so a
  board refusing part way through threw with the boards already armed unrecorded and the `finally`
  released nothing - a driver left armed reports a stall during the next ordinary move. Deriving the
  plans first is what lets the release always know what to undo.
- **The two derivations disagreed about how far to iterate**, one bounded by
  `move.Axes.Count && MaxAxes` and the other by `numAxes && MaxAxesPlusExtruders`. The plan uses
  `numAxes`, which is `min(configured axes, object model axes)` and so is the tighter of the two;
  it can only exclude a drive the planner holds no parameters for.

### Phase 2 — a driver watches its own board (§5.1) ✅

| | |
|---|---|
| Touches | [MoveParams.h](src/DuetSbcInterface/src/Motion/MoveParams.h) `StopInputForSwitch`, [DDA.cpp](src/DuetSbcInterface/src/Movement/DDA.cpp), [RemoteEndstops.cs](src/DuetControlServer/Motion/RemoteEndstops.cs), [MoveParams.cs](src/DuetControlServer/Motion/Native/MoveParams.cs) |
| Wire format | unchanged |
| Behaviour | a stall watch now always names the board carrying the driver. It was already right whenever the plan's driver order happened to match the drive's; it is now right unconditionally |
| Tests | `MoveParamsLayoutTests` gains `TestStallWatchesTheDriversOwnBoard`; `RemoteEndstopsTests` says a stall entry names no board. 9 ctest suites and 897 NUnit tests passing |

`StopInputForSwitch` takes the emitting driver's CAN address and returns it for a stall handle before
it reads `boards[]` or the switch index, because neither means anything for a stall. `boards[]` and
the round-robin still decide a switch, unchanged.

`TryGetStallStopInput` therefore writes no board at all - `MoveStopInput.SetStall` sets the handle and
a `numSwitches` of one, which is what everything downstream reads it for. It cannot be a board list:
which drive an entry ends up on is settled *after* the arming, since a coupled move rewrites every
drive's entry to the one axis', and the boards were then handed out round-robin across the move's
drivers. A driver could be given another driver's board to watch its own stall on. That is invisible
today because the controller ignores the bitmap and `stopAll` stops everything anyway, and would
become a motor that never stops the moment Phase 3 lands - which is why this goes first.

### Phase 3 — the controller reads the stalled-driver bitmap (§5.3) ✅

| | |
|---|---|
| Touches | new [StopRules.h](lib/DuetSpiInterface/include/DuetSpiProtocol/StopRules.h), [CommandProcessor.cpp](src/DuetCANMaster/src/CAN/CommandProcessor.cpp), [CanMotion.cpp](src/DuetCANMaster/src/CAN/CanMotion.cpp) and [CanMotion.h](src/DuetCANMaster/src/CAN/CanMotion.h), [SbcMessageFormats.h](src/DuetCANMaster/src/SBC/SbcMessageFormats.h), [MoveParams.h](src/DuetSbcInterface/src/Motion/MoveParams.h) |
| Wire format | unchanged - the reading is already on the CAN bus and already in the buffer |
| Fixes | the false "Y homed" of §4.1 |
| Tests | new `stop_rules_tests`, 10 native suites and 897 NUnit tests passing; both firmware variants link |

`StopDriversWatchingInput` takes the entry's reading and matches through `WatchMatches`, and
`CanMotion`'s watch array is now an array of `DriverStopWatch` rather than a struct of its own - so
the controller's state is the tested type and there is no copy to drift. `DriverId` is built from the
two bytes at the one place that stops a driver, which is what keeps the header free of CANlib.

The handle-type constants moved into `StopRules.h` too, and `MoveParams.h` now uses them rather than
its own copies. They are what Phase 2 added on the SBC side and what this phase needs on the
controller side, and two definitions of "what a stall handle looks like" is the same class of drift
this document is about.

This does not make `S4` work: telling it from `S3` still needs the stop groups of Phase 4, because a
`stopDriver` that leaves the axis' other motors running is only correct if the last of them can still
stop the axis. What it does fix is `S3` on one board no longer stopping - and recording as homed - an
axis that never stalled.

### Phase 4 — stop groups and the three stop actions (§5.2) ✅

| | |
|---|---|
| Touches | [StopRules.h](lib/DuetSpiInterface/include/DuetSpiProtocol/StopRules.h), [MessageFormats.h](lib/DuetSpiInterface/include/DuetSpiProtocol/MessageFormats.h), [MoveParams.h](src/DuetSbcInterface/src/Motion/MoveParams.h), [ScheduleMoveBuilder](src/DuetSbcInterface/src/Motion/ScheduleMoveBuilder.cpp), [DDA.cpp](src/DuetSbcInterface/src/Movement/DDA.cpp), [CanMotion.cpp](src/DuetCANMaster/src/CAN/CanMotion.cpp), [EndstopKinds.cs](src/DuetControlServer/Motion/EndstopKinds.cs), [EndstopArming.cs](src/DuetControlServer/Motion/EndstopArming.cs), [MoveParams.cs](src/DuetControlServer/Motion/Native/MoveParams.cs) |
| Wire format | `ScheduleMoveDriver` gains `stopGroup` and `stopAction` in its padding and stays 16 bytes; `ScheduleMoveFlags::StopAllDrivers` is deleted; `MoveStopInput` goes from 12 to 14 |
| Tests | `stop_rules_tests` for the three actions and the escalation; `MoveParamsLayoutTests` and `MoveParamsLayout` for the layouts; `EndstopArmingTests` for the action each kind asks for |

Which action each endstop asks for is `IEndstopKind.TryArm`'s answer, and `EndstopArming` overrides
it to `all` on coupled kinematics - RRF's `GetResult` tests `stopAll` before `individualMotors`:

| Endstop | Action |
|---|---|
| Coupled kinematics, whatever the endstop | `all` |
| Switch, one for the axis | `group` |
| Switch, one per driver | `driver`, escalating |
| Z probe | `group` |
| `M574 S3` MotorStallAny | `group` |
| `M574 S4` MotorStallIndividual | `driver`, escalating |

The escalation belongs to the controller, because it is the side that knows how many motors of a
drive are still running: `DriverStopWatch::stillRunning` is cleared as each is stopped, and
`DecideStop` resolves `driver` to `group` once the matched driver is the last of its group. That is
RRF's `Acknowledge` decrementing `numDriversLeft`; without it the last motor of a gantry squaring
itself stops alone and the move runs on with nothing to end it.

Both halves of the SPI link ship together, so no version negotiation is needed - the `static_assert`
blocks are what make a half-updated build fail to compile rather than mis-parse.

### Phase 5 — `S3` and `S4` in DuetControlServer (§4.3) ✅ folded into Phase 4

| | |
|---|---|
Its own defect, but nothing was left to do once Phase 4 landed: telling `S3` from `S4` **is**
choosing between `StopAction::group` and `StopAction::driver`, and `StallEndstopKind` is the one
place that choice can be made. Splitting it into two kinds was considered and rejected - the two
differ in one expression, and a second class would duplicate the arming, the release and the speed
handling to express it.

### Phase 5a — the move id in `MotionStopped` ✅

Not a stall defect, and not on the schedule path: it is the second Known Limit of §13 of
endstops.md, taken while the wire was open.

Committed with Phase 4 rather than on its own. The two are separable in purpose but not in the
source: `ScheduleMoveBuilder::FinishMovement` already takes the move id, and in `CanMotion.cpp` the
id is read at the top of the function Phase 4 rewrites, so either half alone does not compile.

| | |
|---|---|
| Touches | [MessageFormats.h](lib/DuetSpiInterface/include/DuetSpiProtocol/MessageFormats.h), [LinkEvents.h](src/DuetSbcInterface/src/SBC/LinkEvents.h) and its C# mirror, [SbcInterface](src/DuetCANMaster/src/SBC/SbcInterface.cpp), [MotionService](src/DuetSbcInterface/src/SBC/MotionService.cpp), [LinkService.cs](src/DuetControlServer/Link/LinkService.cs), [EndstopCorrection.cs](src/DuetControlServer/Motion/EndstopCorrection.cs) |
| Wire format | `MotionStoppedHeader` gains `moveId`, 8 bytes to 12; `MotionStoppedEvent` follows, 12 to 16 |
| Tests | `LinkEventsLayout` for the record size. The guard itself is not covered - see §7 on why `EndstopCorrection` is not |

A stop report carried no move id, so one arriving after the next move had armed was applied to that
move: the drives it names belong to the move that really stopped, so the wrong axis is corrected and
the one that stopped keeps an endpoint it never reached. `_currentMoveConcluded` catches the report
that arrives after its move finished; it cannot catch the one that arrives after the *next* move has
armed, and nothing else can tell the two apart, because the drives are usually the same ones and the
timestamp only becomes comparable once the report has been attributed.

DCS already numbers the move - `MoveParamsHeader::moveId`, assigned by `MovePlanner` and shared by
every segment of one code - so the controller only has to quote it back. `EndstopCorrection` learns
the id through `NoteMoveId` as the move is queued, which is under the planner lock, so no report can
find the move armed but unnamed.

A zero on either side means the id could not be checked rather than that it did not match: a
controller that sent none, or a move armed and not yet queued. Those are applied, because refusing
them would throw away stops that are almost certainly the armed move's. `M122` counts the mismatches
separately from the too-late ones.

### Phase 6 — a group is the coupling set, not the drive ✅

`stopGroup` is filled with the logical drive, derived in
[DDA.cpp](src/DuetSbcInterface/src/Movement/DDA.cpp) from the drive being emitted, and a coupled axis
is given `StopAction::all` instead. That makes two axes with disjoint couplings impossible to home
together, and [EndstopArming](src/DuetControlServer/Motion/EndstopArming.cs) refuses the move:

```
M584 X1.0 Y1.1 U1.2 V1.3
M669 K1 X1:1:0:0 Y1:-1:0:0 U0:0:1:1 V0:0:1:-1   ; CoreXYUV: X/Y coupled, U/V coupled, pairs independent
G1 H1 X100 U100                                  ; Cannot home X and U together
```

RepRapFirmware accepts that move and half-homes it. `EnableAxisEndstops` primes both endstops without
complaint and both get `stopAll`, so whichever switch closes first runs
[`Move::StopAllDrivers`](lib/RepRapFirmware/src/Movement/Move.cpp#L2973), which iterates *every*
logical drive. X reaches its switch and is set from it; U and V stop wherever they are, unhomed, and
U's endstop never fires. No error is reported.

Both behaviours are wrong, and the group can express the right one: X's endstop stops `{X, Y}`, U's
stops `{U, V}`, both axes home in the one move and neither disturbs the other. `GetControllingDrives`
already returns exactly those sets - `{X,Y}` for X and `{U,V}` for U on the configuration above.

| | |
|---|---|
| Touches | [EndstopArming.cs](src/DuetControlServer/Motion/EndstopArming.cs), [MoveParams.h](src/DuetSbcInterface/src/Motion/MoveParams.h) and [MoveParams.cs](src/DuetControlServer/Motion/Native/MoveParams.cs), [DDA.cpp](src/DuetSbcInterface/src/Movement/DDA.cpp) and [DDA.h](src/DuetSbcInterface/src/Movement/DDA.h), [MoveBuilder.cs](src/DuetControlServer/Motion/MoveBuilder.cs), [RawMove.cs](src/DuetControlServer/Motion/RawMove.cs) |
| Wire format | unchanged. `MoveStopInput` carries the group in the byte Phase 4 left as padding, so it stays 14 |
| Tests | `EndstopArmingTests` gains the CoreXYUV pair - `X + U` armed as two groups, `X + Y` refused - and the CoreXY cases move to the new rule |

**The rule is that the armed axes' controlling-drive sets must not overlap.** Any number of disjoint
sets may be homed in one move; two axes that share a drive are refused, naming the drive they collide
on. That replaces both of the old refusals, and it is the honest statement of the constraint: a drive
carries one watch, so two axes needing the same drive would have the second overwrite the first and
leave one endstop watched by nobody.

Refusing rather than stopping everything is not only a data-structure limit. Two axes of one coupling
share their motors, so the first trigger has to stop them and the second axis can never reach its
endstop in that move. Whatever the move then reported would be wrong: `stoppedAxes` is derived from
the drives that stopped, so the axis that never triggered would be recorded as homed.

What each of the three parts does:

- `EndstopArming` assigns the group and copies the axis' entry to **every drive of its controlling
  set**, not just the axis' own. The copy is what the old `stopAll` path did by copying to every
  drive; without it a CoreXY homing X leaves Y's drive with no watch at all, so it never becomes a
  `DriverStopWatch` and its motor runs on.
- A coupled axis asks for `group` rather than `all`, overriding whatever its kind chose - RRF's
  `GetResult` tests the coupling before `individualMotors`, because moving one motor of a coupled
  axis is not something the kinematics can express.
- `DDA.cpp` passes `m_stopOnInput[drive].stopGroup`, and its round-robin switch counter becomes one
  counter **per group**. A single counter across the move would hand one set's switches to another
  set's drivers now that a move can carry several sets.

`MoveFlags::StopAllDrivers` is renamed `SharedSwitches`, and `ArmedMove.StopsEveryDrive` to
`SharesSwitchesAcrossDrives`. It never crossed the SPI link; all it ever told the native side was to
spread an axis' switches over more drivers than the axis has, which is what it still says.

`StopAction::all` now has no user. It stays because RepRapFirmware makes an extruder endstop
`stopAll`, which is the one case where the answer really is "the whole move" rather than a set of
drives; §8 records why extruder endstops are out of scope.

One consequence to read carefully: a group stop marks every drive of the set as stopped, so homing X
alone on a CoreXY puts Y in `stoppedAxes`. That is harmless because `FinishSpecialMoveAsync` only
inspects the axes the code named, but `stoppedAxes` is "axes whose drives stopped", not "axes that
reached an endstop".

### Phase 7 — the motor-stall Z probe ⬜

[`RemoteProbes.TryGetStopInput`](src/DuetControlServer/Motion/RemoteProbes.cs#L62) returns false for
`ProbeType.ZMotorStall`, so a `G30` against one silently watches nothing and runs its full length.
RRF has a `MotorStallZProbe` class for this. With the earlier phases done it is a small addition: a kind
whose `PrepareAsync` arms the Z drivers as `StallEndstopKind` does and whose `TryArm` gives the
probe's drive a stall stop input.

Deferred behind the rest because it fails loudly enough to notice, where §4.1 and §4.2 do not.

### Phase 8 — diagnostics ⬜

[`ApplyInputState`](src/DuetControlServer/Link/Expansion/ExpansionBoardManager.cs#L595) drops
`typeStallEndstop` silently. RepRapFirmware does not set `sensors.endstops[].triggered` for a stall
either, so the object model should stay as it is - but nothing counts stall reports, and `M122` has
nothing to say when a stall move goes wrong, which is the one thing §12 of endstops.md exists to
provide for switches. Add counters alongside the existing endstop ones in
[EndstopCorrection](src/DuetControlServer/Motion/EndstopCorrection.cs#L580): stall reports received,
drivers named, drivers not armed.

`M119` reporting a stall endstop as "not stopped" unconditionally is RRF's `Stopped()` for
`StallDetectionEndstop` unported. Worth doing here or leaving; it is a report, not a mechanism.

---

## 7. How this gets tested

**Unit tests cover Phases 1, 2, 4 and 5.** `EndstopArmingTests`, `ScheduleMoveBuilderTests` and
`MoveParamsLayoutTests` all already exist and all take the machine description as an argument, which
is what makes the arming rules testable without a printer.

**Phases 3 and 4 decide which motors stop, and that has no harness today.**
[`StopDriversWatchingInput`](src/DuetCANMaster/src/CAN/CanMotion.cpp#L577) is compiled into the
firmware for an ARM target and pulls in `CanMessageBuffer.h`, `CanInterface.h`, `Platform/Platform.h`
and `FreelistManager.h` ([CanMotion.cpp:12](src/DuetCANMaster/src/CAN/CanMotion.cpp#L12)), so it
cannot be built on the host as it stands. Leaving it untested is what the rest of `CanMotion` does,
on the grounds that a stop which does not happen is obvious - and that grounds does not hold here,
because §4.1 and §4.2 are both *silent* wrong answers.

### 7.1 What moves, and why it is not a copy

The two decisions in §5.2 and §5.3 are pure: given the move's watches and an incoming trigger, which
drivers should stop. They touch no buffer, no mutex and no clock. They move into a new leaf header
beside the one the two sides already share:

```
lib/DuetSpiInterface/include/DuetSpiProtocol/StopRules.h
```

That directory is already on the include path of both builds - both add the same
`duet_spi_protocol` INTERFACE target - so a new header there is visible to each without any build
change. A sibling header rather than more of `MessageFormats.h` because that file is wire layout and
this is behaviour; nothing else about it differs. The firmware picks it up through
[SbcMessageFormats.h](src/DuetCANMaster/src/SBC/SbcMessageFormats.h), which is its single point for
shared protocol definitions, and the test links `duet_spi_protocol` alone.

The header holds one struct and the rules that read it, and they are the **only** definitions of
either:

```c
// A watched driver, reduced to what the rules need. No DriverId: that is CANlib, which is
// firmware-side, and depending on it is what would stop this header building on the host.
struct DriverStopWatch {
    uint8_t driverBoard;    // board carrying the driver this would stop
    uint8_t driverNumber;   // its number on that board
    uint8_t inputBoard;     // board carrying the input it watches
    uint16_t inputHandle;
    uint8_t stopGroup;      // the logical drive, or kNoStopGroup
    StopAction stopAction;  // what a trigger matching this watch stops
    bool stillRunning;      // false once this move has already stopped this driver
};

// §5.3. A switch compares (board, handle); a stall also requires its own bit in the reading.
constexpr bool WatchMatches(const DriverStopWatch&, uint8_t inputBoard, uint16_t inputHandle,
                            uint32_t reading) noexcept;

enum class StopAction : uint8_t { none, driver, group, all };

// §5.2. The matched watch's action, with `driver` escalated to `group` once it is the last motor of
// its group still running, and then which drivers that comes to.
constexpr StopDecision DecideStop(std::span<const DriverStopWatch> watches, uint8_t inputBoard,
                                  uint16_t inputHandle, uint32_t reading) noexcept;
constexpr bool StopsDriver(std::span<const DriverStopWatch>, const StopDecision&, size_t index) noexcept;
```

**Nothing is duplicated, because nothing is mirrored.** `CanMotion`'s `endstopWatches[]` is declared
as an array of `DriverStopWatch` and its local `EndstopWatch` struct goes away, so the firmware's own
state *is* the tested type rather than a copy of it - no adapter, and no per-trigger conversion in a
path where the latency is the point. `DriverId` is constructed from the two bytes at the one place
that needs it, which is where a driver is actually stopped. The test constructs the same struct and
calls the same two functions the firmware calls.

Two rules keep it that way, and a reviewer should reject the change if either is broken: the header
includes nothing from RepRapFirmware, CANlib or FreeRTOS, and it contains no `#if` that makes the
firmware and the host see different code. Either one would turn a shared definition back into two.

### 7.2 What stays in the firmware

Everything that is not a decision: holding the watch array across `ScheduleFromSbc` packets, the
`stopListMutex`, choosing between `StopDriverWhenProvisional` and `StopDriverWhenExecuting`, filling
the `MotionStopped` report and waking the async sender. These are untested rather than duplicated -
they exist once, in `CanMotion.cpp`, and hardware commissioning is what covers them.

### 7.3 Where the test lives ✅ Phase 3

`src/DuetSbcInterface/tests`, target `stop_rules_tests`, because it is the only host-side C++ suite
in the tree and standing up a second one under DuetCANMaster would mean a host build of a project
that is otherwise cross-compiled only. The suite tests a controller rule from an SBC-side directory,
which is odd enough that its file header says so. If a `src/DuetCANMaster/tests` is wanted later that
is the alternative, and it costs a CMake target and a CI entry.

**Hardware commissioning** is what proves the whole path, and needs at minimum:

| Case | Expects |
|---|---|
| Single-motor axis, `S3` | Stops on stall; position corrected; axis homed |
| Dual-motor axis, motors on one board, `S3` | Both motors stop on either stall |
| Dual-motor axis, motors on two boards, `S3` | Both motors stop; both reported; drive adopted |
| Dual-motor axis, `S4` | Each motor stops at its own stall; the gantry squares; the last one stops the axis |
| Two stall-homed axes homed together on one board | Only the stalled axis is recorded as homed |
| CoreXY, `S3` | Whole move stops |
| CoreXYUV, `G1 H1 X100 U100` (Phase 6) | Both axes home in the one move; X's endstop stops X and Y only, U's stops U and V only |
| Ordinary move after a stall-homed one | No spurious stall - the disarm ran |

The last one is the regression that catches a `finally` that stopped running.

---

## 8. Explicitly not in scope

- **Extruder stall endstops.** `G1 H1` refuses a move naming both an axis and an extruder, as RRF
  does. RRF's `extrudersEndstop` needs the move's total extrusion to work out the speeds, which is
  the reason for the refusal, and nothing here changes that.
- **Local stall detection.** RRF's `HAS_STALL_DETECT` path is for drivers on the main board. Board 0
  runs DuetCANMaster and has no drivers ([CanAddresses.cs:22](src/DuetControlServer/Link/CanAddresses.cs#L22)),
  so every driver is remote and only the CAN path exists.
- **Pausing a print on a stall.** That is the `driver_stall` event, which already runs
  `driver_stall.g`. What it cannot yet do - raise a message box and pause without a macro - is
  tracked in §3.5 of [EVENTS_MIGRATION.md](docs/devel/EVENTS_MIGRATION.md) along with every other
  event that pauses.
- **Re-arming a driver mid-move.** The board disarms a driver as it reports it and leaves the others
  armed, which is what the three actions need and no more. Nothing should add a re-arm.
