# Doing things at a point in the path without stopping

Plan for effects that are not motion — a fan speed, a laser power, an output pin — but that must
happen where the G-code put them, rather than as soon as DuetControlServer reads the line.

The companion to [MOTION_CONFIG_ORDERING.md](MOTION_CONFIG_ORDERING.md). That one solved *ordering*:
a value a queued move is going to read must not be changed underneath it. This one solves *timing*:
an effect the machine performs must land where the path says it does.

The worked example throughout is:

```gcode
G1 X100 F3000   ; DDA 1
G1 X200 F3000   ; DDA 2
M106 S255       ; fan to full
G1 X300 F3000   ; DDA 3
```

The fan must reach full as the head arrives at X200, without the machine coming to a stop, and
without the fan going to full while DDA 1 is still running.

---

## 1. What happens today

A handler has exactly two tools, and both are wrong for this:

- **Apply it now.** The effect happens as DuetControlServer processes the code, which may be a full
  queue ahead of where the machine physically is. In the example the fan reaches full during DDA 1.
- **`FlushAndWaitForStandstillAsync`.** Correct ordering, at the cost of stopping the machine — a
  blob, a ringing mark, a layer line, and a print that takes longer for every fan change in it.

Neither is what the user asked for. What is missing is a third option: place the effect on the
machine's timeline and let the queue keep running.

The codes divide into four classes, and only two of them are about stopping:

| Class | Meaning | Examples | Mechanism |
| --- | --- | --- | --- |
| **Immediate** | no relation to motion | M115, M409, M122, M550 | act now |
| **Deferred** | the physical effect belongs at a point in the path | M106/107, M42, M280, M300, M150, M117, M3/M4/M5, M568, M104/140/141 | this document — **from a job file or its macros only**, see §3 |
| **Ordered** | applies to moves built after it, must not reach moves already built | M201/203/205/566, M204, M592, M425, M572 | solved: the move carries it |
| **Barrier** | changes what an already-queued move *means* | M92, M584, M350, M208, M669/M665, tool change, homing | standstill, honestly |

Today the class is implicit in whether a handler happens to call the flush, and nothing can test
that. §8 makes it declarative.

M572 is the code that sits across the boundary and is worth stating separately. Its *value* is now
Ordered — each move carries the pressure advance it was built with — but the handler still pushes the
coefficient to the drivers, and a board applies what it is sent to the moves already in its own
queue. So M572 keeps a standstill it should not need, marked with a `TODO` in
`MCodeHandler.Motion.cs`. It becomes the first customer of this plan: once the push is a scheduled
action, the wait goes.

---

## 2. What we have that RepRapFirmware does not

RepRapFirmware's answer is `GCodeQueue`: defer the whole code, tag it with
`executeAtMove = GetScheduledMoves()`, release it when `completedMoves` catches up. It works, and its
limits are structural rather than incidental:

- move-boundary granularity, and it fires on the *completion* of the preceding move, after which the
  message still has to travel;
- only codes that carry no expression, expect no reply, and fit a fixed buffer may be queued —
  `ShouldQueueMCode` is a hand-maintained switch statement that someone has to keep correct;
- the object model lags, because the code itself has not run yet.

DuetControlServer has something stronger. The SBC owns a fitted model of the controller's step clock,
every move already carries an absolute `whenToExecute` to the boards, and moves are dispatched ahead
of execution by `MoveTiming::usualMinimumPreparedTime` — 50 ms. **The machine already has "do this at
time T" as a first-class primitive.** It is simply reserved for motion; everything else is "do this
now".

The plan is to generalise that: make time the currency for every effect, not just for moves.

One property makes this safe here and would not be safe in the firmware: the speed factor is applied
when the move is *built* (`MoveInterpreter`), not retroactively to queued moves. So M220 cannot
retime a move whose action has already been scheduled against it.

---

## 3. The design: schedule the effect, not the code

**The code runs now. Only its physical effect is placed on the timeline.**

The handler validates, replies, and writes the object model exactly as it does today; instead of
sending the CAN message, it posts an *action* onto the motion timeline. The code then completes
normally.

This is the whole difference from `GCodeQueue`, and everything else follows from it: expressions
work, replies work, plugins see codes in stream order, the requested side of the object model keeps
up with the parser, and there is no list of which codes may be queued.

### Only the file channels defer

**Decision: a code is deferred only when it comes from a job file. From every other channel it applies
immediately, as it does today.**

Two reasons, and the second is the one that makes it a requirement rather than a simplification:

- It is what the user means. `M106 S128` typed into DWC or sent over HTTP during a print is a manual
  intervention — the operator wants the fan to change *now*, not at some point in the path they cannot
  see. Deferring it would be surprising and unhelpful.
- **Only a file code can be replayed.** §5 shows that surviving a pause depends on the purged actions
  being exactly the codes that will be re-read when the file rewinds. A code with no file position can
  never be re-read, so it can never be safely purged — it would have to fire or be reported lost, a
  third case with no good answer. Restricting to file channels removes the case rather than handling
  it.

RepRapFirmware reaches the same place from the same direction: `CanQueueCodes()` requires
`machineState->DoingFile()`.

**Macros invoked from a job file are included.** A layer-change or tool-change macro is part of the
job, and its `M106` belongs at the point in the path where the macro was called; refusing to defer it
would put back exactly the defect this plan removes, for the codes most likely to matter. This is also
what RepRapFirmware does — `CanQueueCodes()` is true inside a macro.

The reason that is safe is §5: a pause inside a macro abandons the macro and re-runs it whole, so a
deferred code there can execute twice. But **macro re-run repeats every side effect equally** — a
direct `M106` is duplicated exactly as a deferred one is — so deferring introduces no failure mode
that was not already there. The unit of recovery is the macro, not the line, and that is a property of
macro restart rather than of this design.

What the macro case does change is which file position an action must carry. See §5.

### The anchor

An action is `{ anchor, filePos, payload, policy }`. The anchor is a **sequence position, not a
time**: it sits between two moves in the same submission stream the moves travel down. `filePos` is
the position in the **job file** that a resume would rewind to for this code — its own line, or the
macro invocation if it came from inside a macro. It takes no part in deciding when the action fires,
and exists solely so that a pause can purge exactly the set the resume will replay (§5).

**Resolve to time at prepare.** When the engine prepares the move that follows the action, that
move's `moveStartTime` is known, so the action's absolute time falls out of the same number the
boards get for the move itself. An action and the move it precedes cannot then disagree, and nothing
upstream can invalidate the time because it is computed 50 ms before it is needed.

Make the anchor `(moveId, offsetIntoMove)` from the start, even though the offset is always zero at
first. That one field is what later buys "change the fan halfway through this long move" and
per-pixel laser power without redesigning anything, and it costs nothing to carry now.

### Delivery, in three tiers

The anchoring is one mechanism; only the last hop differs by what the target can do.

| Tier | How | Accuracy | Cost |
| --- | --- | --- | --- |
| **D1** SBC-timed release | the SBC holds the message and releases it shortly before it is due; the controller forwards it on receipt | transfer jitter plus CAN latency | **none** — no protocol change anywhere |
| **D2** board-timed | the message carries `whenToExecute` and the board acts on that tick | exact | a field per message type, plus a scheduled-action ring in the expansion firmware |
| **D3** sequence point | the effect is local to DuetControlServer — object model, M117, a plugin notification, or a genuine barrier | — | none |

**D1 is the default and the starting point.** It mirrors what moves already do — held on the SBC,
released shortly before they are needed — except that a move is released 50 ms early *with* a
timestamp, while a D1 action is released just-in-time *without* one. It needs nothing from CANlib,
nothing from the controller, and nothing from the expansion boards, and §4 shows it is also the only
option that keeps the boards' scarce buffer pool out of the picture entirely.

D2 is the escalation for effects where transfer jitter is too coarse — laser being the expected
customer, and possibly the only one. Promoting an individual message to D2 is a local change that does
not touch the anchoring or the scheduling, so it can be done one message type at a time, and §4 lists
which ones have the room.

An earlier draft had a middle tier where the controller held frames until due. §4 records why that was
dropped.

### What this costs

A deferred code widens the gap between what has been *asked for* and what the machine has *done*. The
object model already carries both sides of that and does so consistently, which is why this costs less
than it first appears:

| Asked for, at the parser | Done, at the machine |
| --- | --- |
| `move.axes[].userPosition` — the target of the last move fed into lookahead | `move.axes[].machinePosition` — the live position, interpolated within the running segment |
| `fans[].requestedValue` | `fans[].actualValue` |

The requested side is written by the handler at parse time and the actual side comes back from the
board once the effect has landed, which is exactly the behaviour wanted. What changes is only how far
apart they can drift: today `requestedValue` means "requested and sent", and afterwards it means
"requested and scheduled". Nothing in the model has to move, but that shift has to be said once, out
loud, rather than discovered.

The same applies to the code's own result: for a deferred code a successful reply means *accepted and
scheduled*, not *done*.

---

## 4. Where the timestamp goes

### The CAN messages have room

Every message that carries a deferrable effect can take a 4-byte `whenToExecute`, and because CAN-FD
payloads quantise to 0–8, 12, 16, 20, 24, 32, 48 and 64 bytes, most take it for free:

| Message | Now | +4 | Wire cost |
| --- | --- | --- | --- |
| `CanMessageWriteGpio` (M42, M280, and all spindle control via `GpioManager`) | 7 | 11 | +4 (DLC 8→12) |
| `CanMessageSetFanSpeed` (M106/107) | 8 | 12 | +4 (DLC 8→12) |
| `CanMessageSetHeaterTemperatureV1` (M104/140/141/568) | 9 | 13 | +4 (DLC 12→16) |
| `…PressureAdvanceV1` (M572), `…MotorCurrents`, `FanParameters` | 34–36 | 38–40 | free (DLC 48) |
| `…StepsPerUnitAndMicrostepping`, `SetInputShapingV1`, `HeaterModelV3`, `SetHeaterMonitors` | 52–60 | 56–64 | free (DLC 64) |

`CanMessageMovementLinearShaped` is 60 bytes with `whenToExecute` already at offset 0. The precedent
is in the protocol.

Eighteen message types are at 61 bytes or more and cannot take an appended field. Fifteen of them are
reports or replies travelling from the boards, where a command timestamp is meaningless.
`FirmwareBlockResponse` never needs one. That leaves two real cases: `CanMessageCreateInputMonitorV1`,
which is configuration rather than path-positioned, and `CanMessageGeneric`.

`CanMessageGeneric` is the one worth knowing about. The struct is 64 bytes but instances are variable
— `GetActualDataLength(paramLength) = paramLength + 4`, over a 20-bit parameter map and 60 bytes of
packed data — so a real `M950 P0 C"out1" Q500` is well under 20 bytes. A time can ride as an extra
parameter in the table, with no layout change and no new message type. Only an instance already
packing more than 56 bytes of parameters, which in practice means one carrying a long string, would
fail. It matters strategically, because generic is where every new parameterised command lands, but
it is not a wall.

### What an expansion board does with a message it cannot take

This is what decides where a message waits, so it is worth stating in full. **An expansion board has
no flow control.** It cannot refuse a message, delay one, or ask for a resend. Its defences are
buffering, detection and counting, in that order, and the point at which a message is actually lost is
invisible to everyone at the time it happens.

| Layer | What it is | What happens under pressure |
| --- | --- | --- |
| Hardware RX FIFO 0 | 32 entries on SAME5x, **16 on SAMC21**, 3 on STM32H5 | overruns; `errs.rxFifoOverlow` and `stats.messagesLost` count it |
| `CanMessageBuffer` pool | **40 buffers, 10 on SAMC21** ("to save RAM") | exhausted |
| `CanReceiverLoop` | `CanMessageBuffer::BlockingAllocate()` | **blocks** rather than dropping |
| `PendingMoves` / `PendingCommands` | unbounded linked lists | bounded only by the pool above |

The chain runs downward, and the last link is the trap: because the receive task blocks rather than
discarding, FIFO 0 stops being drained, and the CAN peripheral discards instead. The software layer
never drops a message; the hardware does, silently, and nothing tells the sender.

Time sync is filtered into its own dedicated buffer (FIFO 1 on RP2040), so it can never be starved by
traffic. That priority split is the precedent for anything else that must not be crowded out.

For movement there is detection but no recovery. `CanMessageMovementLinearShaped.seq` is four bits,
the board tracks `expectedSeq`, and it counts `duplicateMotionMessages` and out-of-sequence messages
by distance — then sets `expectedSeq = seq + 1` and carries on. No resend, no stop. A lost move is
noticed afterwards and counted. Separately, movement is only queued at all `if (StepTimer::IsSynced())`;
otherwise it is discarded and counted as `messagesIgnored`, on the reasoning that unsynced moves would
"just get queued and not executed within a reasonable time".

**The decisive detail** is what the Move task does with a movement message: it copies it into the
board's own ring and **frees the CAN buffer immediately**. A `CanMessageBuffer` is held for transit
only, never until `whenToExecute`; the timed holding happens in a separate, purpose-sized structure.

That is the rule to obey. A timed message occupies a buffer for its whole lead time rather than for
one task pass, and *lead time × rate* comes out of a pool of 40 — or **10** — that is **shared with
movement**. Starving it stalls the receiver, overruns the FIFO, and the thing lost is motion. Timed
non-motion messages must never wait in a `CanMessageBuffer`.

### Where the message waits: on the SBC

**Decision: the SBC holds every scheduled message until shortly before it is due, then releases it,
and the controller forwards it on receipt.** This is tier D1, and it is the default.

It is the same shape as the move path — held on the SBC, released shortly before it is needed — and
it puts the queue where the memory is, where the fitted clock is, and where a 1 ms motion thread
already runs. Everything downstream stays as it is today:

- **The controller needs no buffer of its own.** It is not a scheduler and holds no state, so it has
  nothing to invalidate on stop, pause, abort or reset. `SendCanMessageHeader` does not have to grow,
  and RepRapFirmware does not have to change at all.
- **The expansion boards are untouched.** A D1 message arrives when it is due, is consumed at once,
  and its `CanMessageBuffer` is freed in the same pass — exactly like every command they take today.
  The pool pressure above never arises.
- **Overflow stops being a case to handle.** There is no hold buffer downstream to overflow. The only
  remaining resource is the SBC's outbound ring, which already has `OutboundHasHeadroom()` and the
  rule `LinkScheduleMoveSink::CanAccept` states for moves: stop producing rather than fill the ring
  and have something refused halfway.

The cost is accuracy: release time is quantised to the transfer the message catches, so the error is
one transfer interval plus jitter plus CAN latency. That is comfortably enough for fans, heaters,
GPIO and spindles, and not enough for a laser — which is what D2 is for.

An earlier draft had the controller hold frames until due, with `whenToExecute` in
`SendCanMessageHeader` and credit-based flow control reporting its free slots. It was dropped once the
expansion-board findings were in: it makes the controller a second scheduler with its own lifetime
rules for no accuracy that D1 does not already give, since it still cannot beat CAN latency. If a
middle tier is ever wanted, the three spare padding bytes in `SendCanMessageHeader` are still there.

**Lead time is a measurement, not a guess.** It should be derived from the observed transfer interval
and jitter, and the boards already report the telemetry to check the result: `minAdvance`/`maxAdvance`
(how far ahead messages actually arrive), `maxMotionProcessingDelay`, and `GetFreeBuffers()` /
`GetAndClearMinFreeBuffers()` — a buffer-pool low-water mark. Run a worst-case file on a SAMC21-based
board and read the low-water mark before choosing anything.

**Ordering still has to be preserved on release.** Held messages are released in due-time order per
destination, and a release that cannot proceed must halt the ones behind it rather than let them
overtake. Otherwise an action that is due earlier is delayed while a later one goes out, and the
effects have been silently reordered.

Because actions are anchored to moves, throttling *move preparation* throttles action production for
free, so one backpressure signal covers both. The threshold has to bite well before the prepared
window drains, or the backpressure is itself an underrun.

### If movement becomes a broadcast

Recorded here because it lands on the same constraint. Filter element 2 already routes broadcast into
FIFO 0, so no filter change is needed — but every board would then receive every move, including
boards with no drivers in it, and per-board receive volume would multiply by the number of boards.
That pressure feeds straight into the `BlockingAllocate` → FIFO-overrun path above, and **SAMC21 is
where it breaks first**: 16-deep FIFO, 10 buffers. Bus load falls, per-board CPU and buffer pressure
rise, and the boards are the constrained side.

Two further consequences worth carrying into that decision:

- `seq` changes meaning, mostly for the better. Today each board sees a contiguous run of *its own*
  moves; broadcast makes it one global sequence, so any board can detect any lost move and boards can
  be cross-checked against each other. The cost is that a lost frame is lost by every board at once —
  correlated rather than independent failure, so one glitch becomes a whole-machine event.
- The eight `PerDriveValues` in a 64-byte message bound how many drivers one broadcast move can
  address.

For scheduled actions the same applies: a broadcast "all fans off at T" is one frame instead of N,
with the same per-board receive cost. D1 is unaffected either way, since nothing is held on the
boards.

### The late-deadline policy

Holding on the SBC removes the overflow case. It does not remove a due time passing while a message
is held — a delayed transfer alone can cause that — and the right response differs per effect:

- a fan or a heater setpoint: **send anyway**, a few milliseconds late is invisible;
- a laser or a spindle: late is wrong, and a missed *off* is a safety event;
- an interlock: **abort and stop the machine**.

Carry a two-bit policy on the action — `sendAnyway` / `sendAndRaiseEvent` / `abort`. It costs nothing
and forces the decision at the point the action is built, where the caller knows what the effect is,
instead of leaving it to a global default that is wrong half the time. Under D1 the policy is read on
the SBC at release time, so it needs no room in the message itself; under D2 it travels with the
timestamp.

**Stop, pause and abort must purge held messages.** A held "laser on at T" that survives an emergency
stop and then fires is the failure that makes this feature dangerous rather than merely wrong. Under
D1 this is straightforward and is most of the argument for it: everything unreleased is on the SBC,
in one list, owned by the component that also knows which moves were abandoned. Nothing is in flight
on the boards to chase.

---

## 5. M400, pause, stop and emergency stop

An action outlives the code that created it, so every event that abandons part of the path has to say
what becomes of the actions anchored to it. Getting this wrong is not a cosmetic bug: a purged "laser
off" never happens, and a surviving "spindle on" fires into a machine somebody has just halted.

The rule underneath all of it is that **an action belongs to a point in the path**. If the machine
travelled that point, the action is owed. If it never will, the action is void.

### M400 must wait for actions too

`HandleWaitForMovesAsync` is `FlushAndWaitForStandstillAsync`, and `MovePlanner.IsMoving` asks only
about submissions and the ring's scheduled/completed counts. **It has to gain a third term: no action
is held or in flight.**

Without that, `M106 S255` followed by `M400` returns before the fan has changed — and §7 tells users
to reach for exactly that sequence when they need what has been asked for and what the machine has
done to coincide. An M400 that does not drain the actions makes that advice quietly false, and it
would make M400 mean "the machine has stopped" rather than "everything up to here has happened".
RepRapFirmware already does this: its standstill wait includes `ms.codeQueue->IsIdle()`.

There is no deadlock risk in the other direction. An action with no following move degrades to
immediate (§11), so waiting for the list to empty cannot wait for a move that never comes.

### Pause: the purge and the rewind are one boundary

A pause is always part way through a G-code move. RepRapFirmware has two paths and both land there:

- **`Move::PausePrint`** (M25) stops at a *DDA* boundary where `CanPauseAfter()` holds. A single G1 is
  split into many DDAs, so a DDA boundary is normally somewhere in the middle of the G1 the user
  wrote. This is what `proportionDone` exists for — it is literally
  `(totalSegments - segmentsLeft) / totalSegments`, recorded on every segment handed to the ring, and
  restored on resume as `moveFractionToSkip` so the parent G1 is replayed from where it got to.
- **`Move::LowPowerOrStallPause`** (power fail, stall) additionally aborts the executing DDA with
  `CancelStepping()`, so it can stop part way through a single segment as well.

Both decrement `scheduledMoves` for every move that will not fully run — the aborted one included, so
a partly-executed move counts as **not run**.

On resume the file is rewound with `fgb.RestartFrom(rp.filePos)` and everything after that point is
re-read, deferred codes included. `PurgeEntries` drops entries whose `executeAtMove` exceeds the
reduced `scheduledMoves`, and the two boundaries line up exactly:

| Code's position in the file | Purge | Re-read on resume | Net |
| --- | --- | --- | --- |
| before `rp.filePos` | kept — its anchors all ran | no | fires once |
| after `rp.filePos` | purged — an anchor was skipped | yes | fires once |

**That correspondence is the whole invariant, and it is what this design has to reproduce.** Neither
lost nor duplicated, and it holds only because the set of purged codes is exactly the set that will be
re-read.

So the naive rule — purge by anchor, keep what ran — is necessary and **not sufficient**. Purging by
anchor gets the firing right and says nothing about replay; derive the two boundaries separately and
every pause produces double-fires or silent losses.

**Each action therefore carries the job-file position that a resume would rewind to, and the purge is
`filePos >= rp.filePos`.** The two boundaries become the same number rather than two things that have
to be kept in agreement, the partial-move case falls out for free, and the move id keeps its single
job of deciding *when* the action fires.

Note *job-file* position, not the position of the line that created the action. For a code inside a
macro those differ: the code's own offset is in the macro file, while `rp.filePos` is an offset in the
job file, and comparing them is meaningless. What the action must carry is the position the job file
would restart from — which for a macro-sourced code is the position of the **macro invocation**. This
is RepRapFirmware's own split:

```cpp
FilePosition GCodeBuffer::GetJobFilePosition() const noexcept
{
	return (IsFileChannel() && !IsDoingFileMacro()) ? GetFilePosition() : noFilePosition;
}

const FilePosition pos = IsDoingFileMacro()
		? printFilePositionAtMacroStart		// the position before we started executing the macro
		: GetJobFilePosition();
```

With that, the boundary correspondence holds — at line granularity for job-file codes, and at macro
granularity for codes inside a macro.

Anchoring by move id still pays off in the other half of the problem: the engine knows exactly which
move ids it abandoned, so nothing depends on counter arithmetic that a discarded no-motion segment can
throw out. That fragility is real, and is why RepRapFirmware needs `segmentsLeft == 0` before it will
queue anything — `executeAtMove` counts segments, so queueing mid-segmentation would anchor to a count
that does not correspond to a G-code boundary.

Timing needs no repair. An action's absolute time comes from its anchoring move's `moveStartTime`, and
a pause does not retime the moves *before* it, so every action still owed still has a valid time. One
that is owed but whose time passes while the machine sits paused falls to the `onLate` policy in §4.

**The purged list is not the re-apply list.** An action dropped at pause may describe state the
machine should still be in when it resumes — a fan speed, a spindle. Restoring that is the restore
point's job. Deleting an action is not the same as undoing its intent, and nothing about the purge
should be read as handling resume.

### Pausing inside a macro: at-least-once, not exactly-once

A pause that lands inside a macro **abandons the macro outright.** RepRapFirmware's
`DoAsynchronousPause` captures the restore point and then unwinds the whole stack:

```cpp
ms.GetPauseRestorePoint().filePos = fgb.GetPrintingFilePosition(true);
while (fgb.LatestMachineState().doingFileMacro)   // must call this after GetFilePosition
{                                                 // because this changes IsDoingFileMacro
    ms.pausedInMacro = true;
    fgb.PopState(false);
}
```

The macro does not resume where it stopped. `rp.filePos` is the job-file position *before the macro
was invoked*, so on resume the job restarts there and **the macro runs again from the beginning**.

The consequence for actions is worth stating plainly: **inside a macro the exactly-once property above
degrades to at-least-once.** An action whose anchoring moves ran is kept by the purge and fires, and
then the macro re-runs and creates it again.

That is not a defect introduced here. Macro restart repeats *every* side effect in the macro — a
direct `M106` is duplicated exactly as a deferred one is — so the unit of recovery is the macro rather
than the line, for deferred and immediate codes alike. Deferring adds nothing to the problem, which is
why §3 includes macros rather than excluding them.

The guard already exists and should be documented rather than invented: RepRapFirmware sets
`firstCommandAfterRestart`, which surfaces through `GCodes::GetMacroRestarted()` as
**`state.macroRestarted`**, so a macro can detect that it is a re-run and skip what must not repeat.
DuetSoftwareFramework already carries the field — `State.MacroRestarted` — but nothing in
DuetControlServer sets it today, so making it true is part of this work rather than something to rely
on.

Two details that shape our version of the pause path, both of which are ours to define because the SBC
path differs from the standalone one:

- When `PausePrint` skips moves that came from a *macro*, those moves carry `noFilePosition`, so
  `rp.filePos` is unknown and RepRapFirmware guards the rewind with
  `if (rp.filePos != noFilePosition)`. The purge still runs, so in that narrow case queued codes are
  dropped and never replayed. Whatever we build must not have a state where the purge runs and the
  rewind does not: **the purge must be driven by the resume position actually adopted**, not run
  unconditionally alongside it.
- On the SBC path RepRapFirmware does not rewind at all — it calls `fgb.Init()` and `UnlockAll`, and
  the file positioning is DuetSoftwareFramework's, driven by the pause offset it is sent. So the
  boundary this section depends on is one we own end to end, which is the good case: there is no
  second implementation to keep in agreement.

Worth being explicit: DuetControlServer has no move-abandon path today. Pause stops the code feed and
the queue drains, so nothing is abandoned and nothing would need purging. The rules above are
requirements on whatever pause-with-deceleration is built later, not a description of a hook that
already exists.

### Stop and abort

Everything pending is void, because the rest of the path will not be travelled.

The invariant to state alongside that: **a purge must never be the reason the machine is left unsafe.**
Turning the spindle and the laser off belongs to `stop.g` / `cancel.g`, which run afterwards, and must
not be delegated to an action that the same event has just discarded.

Purging is silent for a `sendAnyway` action and should raise an event for the other two policies. An
action that was marked `abort` because losing it matters has just been lost, and that is worth the
same words as failing to deliver it.

### Emergency stop

Everything held is discarded and nothing further is released. On the SBC that is trivial, because
under D1 the whole list is there.

The sharp case is what has already gone. The release window is small but not zero, so an action
released shortly before M112 can reach a board *after* the emergency stop has shut its outputs down,
and turn one back on. Two things bound it:

- an action that has been released but has not yet gone out on a transfer can still be pulled from the
  outbound ring, so the real exposure is one transfer, not one lead time;
- the boards' emergency-stop handling must **latch** outputs off rather than set them off once, or a
  late frame undoes the halt. That needs verifying in `Duet3Expansion` rather than assuming — it is
  recorded here as a prerequisite for shipping D1, not as a known-good property.

### Link loss and controller reset

`LinkService.Invalidate()` already resets `motionTracker` and `expansionBoardManager` when the
connection drops or the controller restarts. The action list joins them and is discarded whole:
the moves it was anchored to no longer exist, and the boards have reset regardless. No event is
raised for the individual actions — the link loss is the event.

### Resume

Actions are not replayed. What the machine should be doing after a resume comes from the restore
point, which is the mechanism that already exists for it and the one that knows about tool state.

---

## 6. Failure and replies

Deferring an effect converts a synchronous failure into an asynchronous one. That is unavoidable: the
thing that would let a code report the board's answer *and* land the effect at the right point is the
standstill this plan exists to remove.

### Validate early, deliver late

Most of what a board's reply says is knowable when the handler runs. Whether the fan exists, whether
the port is configured, whether the value is in range — DuetControlServer holds all of it, and
`GpioManager.WriteAsync` already performs exactly that lookup before sending.

So the handler fails the code **synchronously** on anything that is a configuration or parameter
error, and only genuinely late failures — bus error, board gone, buffer refusal — can become
asynchronous. A user's mistake still gets an immediate error on the offending line; only a hardware
fault escalates later.

### What is left escalates as an event

Two residual failures, and they are not the same:

- **Delivery failure** — the controller or the bus refused it (`CanStatus::NoBuffer`, `BusError`,
  `Timeout`). The effect never happened. Raise a `MachineEvent`, with the severity taken from the
  same policy field: a message for a fan, a pause for a laser, an emergency stop for a safety output.
  The `Events` subsystem is already the port of `GCodes::ProcessEvent` and already runs a macro named
  after the event with a default action, so this needs no new escalation path and stays overridable
  by the machine.
- **The board acted but complained** — "fan 3 not configured". Diagnostic. `Model.Messages`, tagged
  with the originating code so it is traceable: `M106 S255 (deferred): fan 3 not configured`.

### Needing a reply is the test for "not deferrable"

Deferred actions are sent with `replyType = NoReply`. Asking for a reply buys nothing when the code
has already completed and nothing is waiting on it, and the `txToken` → `CanMessageSent` path already
reports the half that matters — whether the message was accepted for transmission.

That gives a rule instead of a maintained list:

> **If a code needs the board's answer to produce its own reply, it is not a deferrable code.**

M106 needs no answer. M950 does — it reports the port it took — and M950 is configuration rather than
path-positioned, so it stays synchronous anyway. The classification falls out of the semantics.
`SendCanMessageHeader.txToken` still matches a late reply to its request; it simply has no code left
to attach to, so it goes to `Model.Messages`.

---

## 7. Expressions

**Expressions are evaluated at parse time, as they are today, and the resulting value is frozen into
the action payload.** No new rule, and no restriction.

RepRapFirmware refuses to queue any code containing an expression, and has to: it defers the whole
code, so the expression would be evaluated at release time, tens of moves later, against a machine
that has moved on. Because this plan runs the code now and schedules only the effect, that problem
does not arise and the restriction disappears rather than being ported.

One thing to protect. `CodeProcessor.FlushAsync` couples two jobs — waiting for the channel's pending
codes and evaluating expressions — and the standstill is the *separate* second half of
`FlushAndWaitForStandstillAsync`. A converted handler must keep calling the flush and drop only the
standstill. That is already how the code is factored; it is written down here so that nobody later
removes the flush as redundant and silently breaks expression evaluation in exactly the handlers this
plan touched.

### Position in expressions

The two position fields answer two different questions, and a deferred code makes the difference
matter where it previously did not:

- **`userPosition`** is the parser's position — the target of the last move fed into lookahead, with
  offsets applied. It is the position *at the point in the file where the expression sits*.
- **`machinePosition`** is the live position, interpolated within the segment each drive is running.
  It lags the parser by however much is queued.

**For a deferred code, `userPosition` is the one that means what the author almost certainly intends**,
because the point in the file where the code sits is also the point in the path where its effect will
land. Parse-time evaluation gives exactly that, with no special rule for deferred codes:

```gcode
M42 P0 S{move.axes[2].userPosition > 10 ? 1 : 0}
```

`machinePosition` in a file is a different thing: read at parse time it is a race against the queue,
returning wherever the machine happened to have got to when the line was read. That is legitimate —
it is how you ask "where is the machine *now*" — but it is rarely what a positional condition in a
part program wants, and it was never well defined against a deep queue even before this plan.

**Decision: expressions are evaluated at parse time, and this stays as it is.** A user who wants the
machine actually to be at the position they are testing writes `M400` first, which is what
RepRapFirmware users already do and what makes `machinePosition` catch up with `userPosition`.
Evaluating later instead would reintroduce the staleness problem in §2 and, worse, would let an
expression fail *after* its code had reported success.

That rests on M400 draining the held actions as well as the moves, which is why §5 makes it a
requirement rather than an improvement. An M400 that waits only for motion would leave the advice
above true for position and false for every deferred effect.

No object model change is needed. The requested/actual split this plan widens is already modelled
throughout — `userPosition`/`machinePosition`, `fans[].requestedValue`/`actualValue` — so the work is
to document which field answers which question, not to add fields. Where a deferred effect has no
actual-side counterpart today, adding one is the same shape as what already exists rather than a new
idea.

---

## 8. Making the class declarative

The four classes in §1 should be declared per code rather than implied by which handler remembered
which call. One table, asserted by a test, diffable against RepRapFirmware's own behaviour.

The value is not the taxonomy. It is that "does this code need a standstill" becomes a reviewable
statement instead of an invisible omission, and that the *Ordered* class acquires a name — so the
next person to read a handler with no flush in it can tell that this was decided rather than
forgotten.

---

## 9. Order of work

1. **Declare the classes** (§8). Pure DuetControlServer, no behaviour change, and it makes the rest
   reviewable.
2. **Action entries in `MotionService`** — an ordered list of `{ afterMoveId, offset, filePos, payload,
   policy }`, drained as the anchoring move is prepared. A side list keyed by move id is enough; the
   DDA ring itself does not have to change.
3. **`DuetSbc_MotionSubmitAction`**, down the *same* single-producer queue as `SubmitMove`. If
   actions travel a different path from moves, the race this plan exists to remove comes back.
4. **Timed release on the SBC** (D1) — a due-time-ordered hold list, released by the motion thread at
   `T − leadTime`, with FIFO release per destination and the `onLate` policy applied at release.
   **No controller or expansion firmware change.**
5. **The lifecycle rules** (§5) — M400 waits for the list to drain, purge driven by the resume
   position actually adopted, discard on emergency stop and on `LinkService.Invalidate()`, and
   cancellation of a released-but-unsent action from the outbound ring. Set `state.macroRestarted`,
   which DuetControlServer never writes today. This belongs with step 4 rather than after the
   conversions: a half-built lifecycle is how a stale action reaches a halted machine.
6. **Measure the lead time.** Instrument the transfer interval and its jitter, then confirm against
   the boards' own `minAdvance`/`maxAdvance` and the `GetAndClearMinFreeBuffers()` low-water mark on a
   SAMC21-based board under a worst-case file. Confirm at the same time that the expansion firmware
   latches outputs off on emergency stop.
7. **Convert the deferred codes**, M106 first because it is the easiest to see and the easiest to
   test, then M42/M280, the spindle codes, and the heater setpoints.
8. **Remove M572's standstill**, once the driver push is a scheduled action. This is the item that
   closes the `TODO` left in `MCodeHandler.Motion.cs`.
9. **Per-message `whenToExecute` fields** (D2), only where step 6's measurement shows D1 is not
   accurate enough. Laser is the expected first and possibly only customer, and this step is what
   requires a scheduled-action ring on the boards — with the buffer freed on entry, as `Move::TaskLoop`
   already does for movement.

Laser is deliberately not in this list. Laser power must track the move's actual top speed — the
native `DDA` already scales it by `topSpeed / requestedSpeed` — so it belongs *on the move record*,
like the tuning in [MOTION_CONFIG_ORDERING.md](MOTION_CONFIG_ORDERING.md), not on the action
timeline. `MoveBuilder` carries a `TODO` for the `controlLaserOrIoBits` flag that this needs.

---

## 10. Verification

The property is testable offline, with no hardware and no timing, in the same way `DdaRingTests`
proves the tuning property:

- submit two moves, an action, and a third move; spin; assert the action is emitted with the third
  move's `moveStartTime` and not before the first two have been scheduled;
- an action submitted when the queue is empty is emitted immediately, rather than waiting for a move
  that never comes;
- a refused action halts the actions behind it and does not reorder them;
- an action anchored to a move that is abandoned is purged, and one anchored to a move that ran is
  not — including the case where the move ran only partly;
- each `onLate` policy does what it says when the due time is forced into the past;
- `WaitForStandstillAsync` does not return while an action is held, and does return when the last one
  has gone;
- an emergency stop leaves nothing in the hold list and nothing releasable, and a released-but-unsent
  action is pulled back out of the outbound ring;
- purging a `sendAnyway` action is silent and purging an `abort` action raises;
- the purge boundary and the rewind boundary agree: for a pause at an arbitrary file position, every
  action either survives and fires or is re-created by the replay — never both, never neither;
- the same code from a non-file channel is not deferred at all;
- an action created inside a macro carries the job-file position of the macro invocation, not an
  offset in the macro, so it is purged when the resume rewinds to before the macro call;
- with a fake clock, an action is released at `T − leadTime` and not before, and two actions due in
  one release window leave in due-time order.

What needs hardware is the end-to-end check: `M106 S255` mid-print, confirming both that the machine
does not pause and that the fan changes at the right point in the path.

---

## 11. Open questions

- Does an action between two moves inhibit their junction meld? The default must be **no** — actions
  do not influence planning — because that is what RepRapFirmware does and it is what keeps the print
  moving. `GCodeQueue` gets that answer by construction: it holds code text and creates no DDA, so
  lookahead never learns the command existed and the corner is planned as if it were absent. The
  effect then lands somewhere on the rounded arc after the junction, at a point set by latency rather
  than by geometry.

  Anchoring in-band means we get the same answer by *choice*, and gain an option RepRapFirmware
  structurally cannot have: an individual action could ask for its junction to be planned to a stop,
  or to a bounded speed, when the effect must land at a place rather than approximately. That is
  proposed as a per-action flag defaulting to "no effect on planning" — `MoveFlags.CanPauseAfter`
  already sits in that family — and not as a global rule. RepRapFirmware can only choose between
  queueing and accepting the smear, or not queueing and stopping the whole machine.
- Which reference point does the anchor mean: the start of the following move, or the end of the
  preceding one? They coincide only for contiguous moves. Start-of-next is proposed, because that is
  the number the boards already receive.
- What does an action mean on a machine running two motion systems? An action belongs to one ring;
  whether anything needs to span both is undecided.
- How does an action interact with a pause that later resumes from a restore point? Purging is
  correct for the abandoned moves, but an effect that was purged may need reapplying on resume, and
  that is not the same list.
