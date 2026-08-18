# Doing things at a point in the path without stopping

Plan for effects that are not motion — a fan speed, a laser power, an output pin — but that must
happen where the G-code put them, rather than as soon as DuetControlServer reads the line.

The companion to [MOTION_CONFIG_ORDERING.md](MOTION_CONFIG_ORDERING.md). That one solved *ordering*:
a value a queued move is going to read must not be changed underneath it. This one solves *timing*:
an effect the machine performs must land where the path says it does.

The worked example throughout is:

```gcode
G1 X100 F3000   ; move A
G1 X200 F3000   ; move B
M106 S255       ; fan to full
G1 X300 F3000   ; move C
```

The fan must reach full as the head arrives at X200, without the machine coming to a stop, and
without the fan going to full while move A is still running.

---

## 1. What happens today

A handler has exactly two tools, and both are wrong for this:

- **Apply it now.** The effect happens as DuetControlServer processes the code, which may be a full
  queue ahead of where the machine physically is. In the example the fan reaches full during move A.
- **`FlushAndWaitForStandstillAsync`.** Correct ordering, at the cost of stopping the machine — a
  blob, a ringing mark, a layer line, and a print that takes longer for every fan change in it.

What is missing is a third option: place the effect on the machine's timeline and let the queue keep
running.

The codes divide into four classes, and only two of them are about stopping:

| Class | Meaning | Examples | Mechanism |
| --- | --- | --- | --- |
| **Immediate** | no relation to motion | M115, M409, M122, M550 | act now, without waiting for the channel's pending codes |
| **Deferred** | the physical effect belongs at a point in the path | M106/107, M42, M280, M300, M150, M117, M3/M4/M5, M568, M104/140/141, M144, `G10` without axis letters — §9 is the list | this document — **from a job file or its macros only**, see §3 |
| **Ordered** | applies to moves built after it, must not reach moves already built | M201/203/205/566, M204, M592, M425, M572 | solved: the move carries it |
| **Barrier** | changes what an already-queued move *means* | M92, M584, M350, M208, M669/M665, tool change, homing | standstill, honestly |

Today the class is implicit in whether a handler happens to call the flush, and nothing can test
that. M115 and M122 do not flush; M409 does; nothing states which is intended. §8 makes the class
declarative, which is also what makes the Immediate row a decision rather than an accident.

M572 sits across the boundary and is worth stating separately. Its *value* is Ordered — each move
carries the pressure advance it was built with — but the handler still pushes the coefficient to the
drivers, and a board applies what it is sent to the moves already in its own queue. So M572 keeps a
standstill it should not need, marked with a `TODO` in `MCodeHandler.Motion.cs`. It is the first
customer of this plan: once the push is a scheduled action, the wait goes.

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
of execution. **The machine already has "do this at time T" as a first-class primitive.** It is
simply reserved for motion; everything else is "do this now".

This plan generalises that: time becomes the currency for every effect, not just for moves.

One property makes it safe here that would not be safe in the firmware: the speed factor is applied
when the move is *built* (`MoveInterpreter`), not retroactively to queued moves. So M220 cannot
retime a move whose action has already been scheduled against it.

---

## 3. The design: schedule the effect, not the code

**The code runs now. Only its physical effect is placed on the timeline.**

The handler validates, replies, and writes the object model exactly as it does today; instead of
sending the CAN message, it posts an *action* onto the motion timeline. The code then completes
normally.

Everything else follows from that: expressions work, replies work, plugins see codes in stream order,
the requested side of the object model keeps up with the parser, and there is no list of which codes
may be queued.

### Only the file channels defer

**A code is deferred only when it comes from a job file. From every other channel it applies
immediately, as it does today.**

Two reasons, and the second makes it a requirement rather than a simplification:

- It is what the user means. `M106 S128` typed into DWC or sent over HTTP during a print is a manual
  intervention — the operator wants the fan to change *now*, not at some point in the path they
  cannot see.
- **Only a file code can be replayed.** §5 shows that surviving a pause depends on the purged actions
  being exactly the codes that will be re-read when the file rewinds. A code with no file position
  can never be re-read, so it can never be safely purged — it would have to fire or be reported lost,
  a third case with no good answer. Restricting to file channels removes the case rather than
  handling it.

RepRapFirmware reaches the same place from the same direction: `CanQueueCodes()` requires
`machineState->DoingFile()`.

**Macros invoked from a job file are included.** A layer-change or tool-change macro is part of the
job, and its `M106` belongs at the point in the path where the macro was called; refusing to defer it
would put back exactly the defect this plan removes, for the codes most likely to matter.

That is safe because a pause inside a macro abandons the macro and re-runs it whole, so a deferred
code there can execute twice — and **macro re-run repeats every side effect equally**. A direct
`M106` is duplicated exactly as a deferred one is, so deferring introduces no failure mode that was
not already there. The unit of recovery is the macro, not the line, and that is a property of macro
restart rather than of this design. §5 works it through.

### The anchor

An action is `{ afterMoveId, payload }` — the id of the move it was submitted behind, and the CAN frame
to send. Where the timestamp goes in that frame is a lookup on the message type rather than a field on
the action (§4). It carries no file position and no offset into the move; §5 shows why neither is
needed.

### Resolving the anchor to a time

**The time is the end of the preceding move**: `m_afterPrepare.moveStartTime + m_clocksNeeded`,
computed when that move is prepared.

The alternative is the start of the *following* move, and for two chained moves the two are
identical — `DDA::Prepare` sets a chained move's start time to exactly
`prev.moveStartTime + prev.clocksNeeded`. They differ across a **gap**, where the machine came to rest
and the next move was issued later, and there start-of-next is wrong: an `M107` between two moves
separated by five seconds of code-stream latency would keep the fan running for those five seconds.
End-of-previous is right in both cases.

It is also simpler. Anchoring forward needs the following move to exist, and an action at the end of
the queue then needs a special case. Anchoring backward needs only the move the action was submitted
behind, which is by definition already there.

**The time is in the movement timebase**, which is what `moveStartTime` is in: the controller's step
clock less the shared movement delay
([StepTimer.h](../../src/DuetSbcInterface/src/Motion/StepTimer.h)). That is not a detail to gloss.
The movement delay is how the machine slips *everything* when some part of it falls behind, and it is
shared so that boards do not lose sync with each other. An action expressed in the raw step clock
would keep its original time while the path around it slipped, and would land in the wrong place by
exactly the amount the machine had fallen behind. Taking the number from `moveStartTime` puts the
action in the same timebase as the path for free.

**The action is emitted with the preceding move's own message**, from the same `Prepare` pass, down
the same sink. So it inherits the movement path's lead time rather than needing one of its own, and
its parked lifetime on a board is bounded by that lead plus the preceding move's duration —
`CanAddMove` already bounds unprepared time to two seconds, so that is the bound.

### How far ahead that is

For movement the answer is a horizon, not a constant. `DDARing::Spin` prepares moves while less than
`MoveTiming::usualMinimumPreparedTime` — **50 ms** — of prepared motion remains, and `DDA::Prepare`
then sets the start time one of two ways:

| Case | Start time | Effective lead |
| --- | --- | --- |
| Chained onto a committed predecessor | `prev.moveStartTime + prev.clocksNeeded` | whatever is still prepared ahead of it, up to the 50 ms horizon |
| Starting from rest | `now + prepareAdvanceTime` | 50 ms |

So: **50 ms**, less the transfer and CAN latency by the time a message reaches a board. The boards
already report `minAdvance`/`maxAdvance` — how far ahead messages actually arrive — so the realised
figure is measurable rather than assumed.

### Delivery: one mechanism

**Every command message gains a `whenToExecute`, and the board acts on it.** A message with a time in
the past — or with the "now" sentinel — is acted on as it arrives, which is every message the machine
sends today. A message with a future time is parked on the board and acted on at that tick.

The alternative is to hold the message on the SBC and release it just in time, so that nothing
downstream changes. That looks like the cheap option and is not:

| | Hold on the SBC | Time it on the board |
| --- | --- | --- |
| Accuracy | one transfer interval + jitter + CAN latency | exact |
| SBC | a due-time-ordered hold list, per-destination FIFO release, a **measured lead time**, and a path to recall a released-but-unsent message from the outbound ring | nothing: the message goes out with the moves |
| Controller | nothing | nothing — it forwards frames |
| Boards | nothing | one gate in `CommandProcessor::Spin` and a parked-command ring |
| Protocol | nothing | a 4-byte field per command message |
| Purge | a local list operation | a broadcast (§4) |

The hold list is the thing to notice. It needs ordering, per-destination FIFO discipline so effects
cannot be silently reordered, backpressure tied to move preparation, a policy for a deadline that
passes while a message is held, and a recall path for the window between release and transmission.
**All of it exists to reproduce, less accurately, what a timestamp gives for free** — on a machine
whose motion path already works exactly this way. Moves are not held on the SBC and released just in
time; they are sent early *with a time on them*. There is no reason for an effect on that same path
to be different.

An effect local to DuetControlServer — the object model, M117, a plugin notification — has no message
and no timestamp. It is applied when the anchoring move is prepared, and shares nothing with the
above but the anchor.

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
apart they can drift: `requestedValue` means "requested and sent" today, and "requested and scheduled"
afterwards. Nothing in the model has to move, but that shift has to be said once, out loud, rather
than discovered.

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

Eighteen message types are at 61 bytes or more and cannot take an appended field. Fifteen are reports
travelling *from* the boards, where a command timestamp is meaningless, and `FirmwareBlockResponse`
never needs one. The two that matter both carry a trailing array whose capacity is larger than
anything real uses, so both take the field by **shortening the array**:

| Message | Trailing array | Shorten to | Note |
| --- | --- | --- | --- |
| `CanMessageGeneric` | `ByteArray60 Data` at offset 4 | 56 | Instances are already variable — `GetActualDataLength(paramLength) = paramLength + 4` — so a real `M950 P0 C"out1" Q500` is well under 20 bytes. Only an instance packing more than 56 bytes of parameters, meaning one carrying a very long string, is affected |
| `CanMessageCreateInputMonitorV1` | `CharArray54 PinName` at offset 10 | 50 | A 50-character pin name is far beyond anything a port syntax produces |

`CanMessageGeneric` could instead carry a time as an extra entry in its parameter table, with no
layout change at all. It should not: one message type would then have the time somewhere different
from every other, the board's dispatch gate could not read it without unpacking parameters first, and
"every command carries a time" would acquire an exception. Shorten the array.

### All of this is one schema edit

The wire formats are not written twice. `Schema/can-messages.json` is the single description, and
`DuetCanMessage.SourceGenerators` emits from it the CANlib header, the C# mirror, and a conformance
harness on each side that asserts the two agree. So:

- adding `whenToExecute` is a field in the schema, and both languages follow;
- shortening an array is one character in the schema — `"length": "60"` becomes `"56"`;
- the layouts cannot drift, because nothing transcribes them.

That also removes a field from the action. The board finds the timestamp by knowing its own message
layouts, but the SBC has to patch the time into an opaque frame, which means knowing the offset for
that message type. Rather than carrying the offset in every action, **generate the offset table** —
message type to `whenToExecute` offset — the way `CanMessageGenericTables` is already generated from
the same schema. The action is then `{ afterMoveId, payload }` and the offset is a lookup that cannot
disagree with the layout it describes.

Two numbers to name rather than write twice while doing it: the shortened array lengths belong in the
schema and nowhere else, and the "now" sentinel needs a constant in it too, so that the board's
dispatch gate and the SBC's default are the same value by construction.

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
by distance — then sets `expectedSeq = seq + 1` and carries on. Separately, movement is only queued at
all `if (StepTimer::IsSynced())`; otherwise it is discarded and counted as `messagesIgnored`, on the
reasoning that unsynced moves would "just get queued and not executed within a reasonable time".

**The decisive detail** is what the Move task does with a movement message: it copies it into the
board's own ring and **frees the CAN buffer immediately**. A `CanMessageBuffer` is held for transit
only, never until `whenToExecute`; the timed holding happens in a separate, purpose-sized structure.

### Where the message waits: on the board

**The message is sent as soon as it is built, carrying the time it is due, and the board parks it
until then.** The buffer analysis above is what makes this safe rather than what forbids it, and the
rule it yields is one line:

> A parked command is copied into a ring of its own and its `CanMessageBuffer` freed in the same pass,
> exactly as `Move::TaskLoop` already does with a movement message.

Obey that and the pool pressure never arises: a `CanMessageBuffer` is held for transit only, which is
the invariant the boards already keep for the only timed thing they handle today. Break it — park the
message *in* its buffer — and lead time × rate comes out of a pool of ten on a SAMC21, the receive
task blocks, FIFO 0 overruns, and the thing lost is motion.

The parked ring is small and fixed. It needs no allocator, no flow control and no reply path, because
a parked command is one that has already been accepted; what it needs is a bound, and a bound that is
reached must be a full ring rather than a stalled receiver.

**When the ring is full, execute the parked command with the nearest due time, and park the arrival in
the slot that frees.** If the arrival is itself the nearest due, execute that instead and park nothing.
Nothing is dropped and nothing is reordered: everything still fires in due-time order, and the error
is that one command fired early — the one that was closest to being due anyway.

The obvious alternative, executing the *arrival* immediately, is wrong in a way worth spelling out
because it looks harmless. Commands arrive in path order, so an arrival is normally the **latest** due
of the set. Executing it first puts it ahead of parked commands that are due earlier and that address
the same thing, and they then overwrite it:

```
parked:  fan → 50%   due T=100
arrives: fan → 100%  due T=200, ring full

execute the arrival:      100% now, then 50% at T=100   → ends at 50%
execute the nearest due:  50% now,  then 100% at T=200  → ends at 100%
```

The first leaves the machine in a state no line of the file asked for. That is a different class of
failure from an effect landing a few milliseconds early, and it is why the rule is about due time
rather than about arrival.

**Nearest due, not head of the ring.** The two coincide while arrivals are in due-time order, which is
the normal case — but different message types carry different CAN IDs and therefore different
arbitration priority, so a later-due command of a high-priority type can overtake an earlier-due one.
The ring is a set executed by due time, and the overflow rule picks its minimum.

Under sustained pressure this degrades to "execute in order, as fast as they arrive", which is the
right thing to degrade to: the effects are early but their sequence is intact.

How deep the ring has to be follows from §3: an action is emitted with the move it trails, so the
parked set at any instant is the actions falling within the prepared window plus one move's duration.
A print changing a fan once a layer parks one entry at a time; the pathological case is an `M42`
toggling on every segment, and that is what the overflow rule is for. Early executions are counted, so
a ring that is too shallow for a real machine is visible in the diagnostics the boards already report
rather than being inferred from a print that came out wrong.

**The controller is unchanged.** It forwards frames and holds no state, so it has nothing to
invalidate on stop, pause, abort or reset. `SendCanMessageHeader` does not have to grow.

**The SBC gains no holding machinery either.** An action is resolved to a time when its anchoring move
is prepared and goes out with that move's own dispatch. Ordering is submission order, down the same
single-producer queue the moves use — so effects cannot overtake each other without moves overtaking
each other first, which is a stronger guarantee than a per-destination FIFO on a hold list, and it is
free.

### What a late message means

A due time can pass before the message is acted on — a delayed transfer, a board that was busy. **A
late command is acted on immediately and counted**, with the count in the diagnostics the boards
already report, so a machine that is consistently late is visible before it is a surprise.

That is the whole policy. A per-action choice between "send anyway", "send and raise" and "abort" is
worth adding when something needs it: a fan wants the first, a laser the second, an interlock the
third — and laser power belongs on the move record rather than on this timeline (§10), while interlocks
do not exist. Two bits beside a four-byte timestamp is cheap to add later, with a customer whose
requirements are known.

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
and lands one entry in every board's parked ring rather than one in the addressed board's. That is the
same multiplication as for moves, on a much smaller stream — and the drop broadcast below is an
example of the shape working.

### Purging reaches the boards

Scheduling on the board means a purge has to reach the board. A parked "laser on at T" that survives
an emergency stop and then fires is the failure that would make this feature dangerous rather than
merely wrong.

**A broadcast "drop every parked command", sent on every purge.** No id arithmetic, no per-action
bookkeeping, no partial state to get wrong — and correct in every case, because *every* purge is
followed by the machine stopping:

- a pause purges, stops, and then either resumes — which replays from the file and re-creates whatever
  is still owed — or is cancelled;
- a stop or abort purges and the path is over;
- an emergency stop purges and everything is over.

There is never a case where some parked commands should survive a purge and others should not, so the
mechanism does not need to express one.

Two prerequisites, both to be verified rather than assumed:

- **The boards must latch outputs off on an emergency stop**, rather than setting them off once. A
  command sent just before M112 can arrive just after it, and a latch is what stops it undoing the
  halt. This needs checking in `Duet3Expansion`.
- **The drop must not be lost.** It is a broadcast on a bus with no delivery guarantee, so it must be
  idempotent and repeated — the boards already handle repeated emergency stops — rather than sent once
  and assumed.

---

## 5. M400, pause, stop and emergency stop

An action outlives the code that created it, so every event that abandons part of the path has to say
what becomes of the actions anchored to it. Getting this wrong is not cosmetic: a purged "laser off"
never happens, and a surviving "spindle on" fires into a machine somebody has just halted.

The rule underneath all of it: **an action belongs to a point in the path. If the machine travelled
that point, the action is owed. If it never will, the action is void.**

### M400 must wait for actions too

`HandleWaitForMovesAsync` is `FlushAndWaitForStandstillAsync`, and `MovePlanner.IsMoving` asks only
about submissions and the ring's scheduled/completed counts. **It has to gain a third term: no action
is pending.**

An action is pending from the moment it is submitted until the tick it is due, which spans two places
— the SBC's unresolved list and the boards' parked rings. Only the first is directly observable, so
the term is "no unresolved action, and the last resolved one's due time has passed". That is exact
rather than approximate: the due time is a step-clock instant the SBC computed, so it knows when it is
over without asking.

Without that, `M106 S255` followed by `M400` returns before the fan has changed — and §7 tells users
to reach for exactly that sequence when they need what has been asked for and what the machine has
done to coincide. An M400 that does not drain the actions makes that advice quietly false, and would
make M400 mean "the machine has stopped" rather than "everything up to here has happened".
RepRapFirmware already does this: its standstill wait includes `ms.codeQueue->IsIdle()`.

There is no deadlock risk in the other direction. An action is resolved when the move it was submitted
behind is prepared, and that move is already in the ring, so waiting for the list to empty never waits
for a move that has not been issued.

### Pause: the purge boundary and the rewind boundary are one number

A pause stops the machine before the queue has run and drops the moves after the stopping point
([JOB_LIFECYCLE.md](JOB_LIFECYCLE.md) §3.5). It reports the first move it dropped, and the job file is
rewound to the position that move's code came from.

**So the rewind point is computed from the purge point.** Purging the actions anchored to purged moves
is therefore already exactly purging what the replay will re-create — not by agreement between two
rules, but because there is only one rule. That is why an action needs no file position of its own.

Work the cases through on the example at the top:

| Pause purges | Action anchored after B | Job rewinds to | Is M106 re-read? | Net |
| --- | --- | --- | --- | --- |
| C only | kept — B ran, so the point was travelled | C's position, after the M106 | no | fires once |
| B and C | purged — B never ran | B's position, before the M106 | yes | fires once |

The awkward middle case — an action whose anchor ran but whose code sits after the rewind point —
cannot arise, because the rewind point *is* the first purged anchor.

Two things follow, and both remove work rather than add it:

- **There is no partial move to replay.** The pause stops at a move boundary and never truncates one,
  so nothing corresponds to RepRapFirmware's `proportionDone` / `moveFractionToSkip`.
- **The purge hook already exists.** `MovePlanner.StopEarlyAsync` is where the moves are dropped and
  where the action list is dropped with them.

**The purged list is not the re-apply list.** An action dropped at pause may describe state the machine
should still be in when it resumes — a fan speed, a spindle. Restoring that is the restore point's job.
Deleting an action is not the same as undoing its intent.

### Pausing inside a macro: at-least-once, not exactly-once

A pause that lands inside a macro abandons the macro. `AbandonMacrosForPauseAsync` unwinds the macro
levels and leaves the job file in place, and the resume rewinds the job file to before the macro was
invoked, so **the macro runs again from the beginning**.

That the rewind lands there rather than somewhere arbitrary is a property worth naming, because it is
what the table above depends on: a move whose code came from a macro records **no** origin, so a pause
that purges one falls back to the last completed job-file code — which is the macro invocation, still
executing. A position is meaningful only against the file it was measured in, and the resume rewinds
the job file.

For actions that means the exactly-once property degrades to **at-least-once** inside a macro: an
action whose anchoring moves ran is kept and fires, and then the macro re-runs and creates it again.

That is not introduced here. Macro restart repeats *every* side effect in the macro — a direct `M106`
is duplicated exactly as a deferred one is — so the unit of recovery is the macro rather than the line,
for deferred and immediate codes alike. Deferring adds nothing, which is why §3 includes macros rather
than excluding them.

The guard exists and should be used rather than invented: RepRapFirmware sets
`firstCommandAfterRestart`, which surfaces as **`state.macroRestarted`** so a macro can tell it is a
re-run and skip what must not repeat. DuetSoftwareFramework carries the field and **nothing writes
it**, so making it true is part of this work.

### Stop and abort

Everything pending is void, because the rest of the path will not be travelled.

The invariant to state alongside that: **a purge must never be the reason the machine is left unsafe.**
Turning the spindle and the laser off belongs to `stop.g` / `cancel.g`, which run afterwards, and must
not be delegated to an action that the same event has just discarded.

### Emergency stop

Everything parked is dropped by the broadcast in §4, and nothing further is scheduled.

The sharp case is what has already been sent. A command in flight can reach a board *after* the
emergency stop has shut its outputs down, and turn one back on. The answer is the prerequisite in §4:
**the boards must latch outputs off rather than set them off once.**

### Link loss and controller reset

`LinkService.Invalidate()` already resets `motionTracker` and `expansionBoardManager` when the
connection drops or the controller restarts. The pending actions join them and are discarded whole: the
moves they were anchored to no longer exist. Nothing has to reach the boards — they have reset, or the
link they would be told over is the thing that is gone. No event is raised for the individual actions;
the link loss is the event.

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
error, and only genuinely late failures — bus error, board gone, buffer refusal — become asynchronous.
A user's mistake still gets an immediate error on the offending line; only a hardware fault escalates
later.

### What is left escalates as an event

Two residual failures, and they are not the same:

- **Delivery failure** — the controller or the bus refused it (`CanStatus::NoBuffer`, `BusError`,
  `Timeout`). The effect never happened. Raise a `MachineEvent`: the `Events` subsystem is already the
  port of `GCodes::ProcessEvent` and already runs a macro named after the event with a default action,
  so this needs no new escalation path and stays overridable by the machine.
- **The board acted but complained** — "fan 3 not configured". Diagnostic. `Model.Messages`, tagged
  with the originating code so it is traceable: `M106 S255 (deferred): fan 3 not configured`.

### Needing a reply is the test for "not deferrable"

Deferred actions are sent with `replyType = NoReply`. Asking for a reply buys nothing when the code has
already completed and nothing is waiting on it, and the `txToken` → `CanMessageSent` path already
reports the half that matters — whether the message was accepted for transmission.

That gives a rule instead of a maintained list:

> **If a code needs the board's answer to produce its own reply, it is not a deferrable code.**

M106 needs no answer. M950 does — it reports the port it took — and M950 is configuration rather than
path-positioned, so it stays synchronous anyway. The classification falls out of the semantics.
`SendCanMessageHeader.txToken` still matches a late reply to its request; it simply has no code left to
attach to, so it goes to `Model.Messages`.

---

## 7. Expressions

**Expressions are evaluated at parse time, as they are today, and the resulting value is frozen into
the action payload.** No new rule, and no restriction.

RepRapFirmware refuses to queue any code containing an expression, and has to: it defers the whole
code, so the expression would be evaluated at release time, tens of moves later, against a machine that
has moved on. Because this plan runs the code now and schedules only the effect, that problem does not
arise and the restriction never needs porting.

One thing to protect. `CodeProcessor.FlushAsync` couples two jobs — waiting for the channel's pending
codes and evaluating expressions — and the standstill is the *separate* second half of
`FlushAndWaitForStandstillAsync`. A converted handler must keep calling the flush and drop only the
standstill. That is already how the code is factored; it is written down here so that nobody later
removes the flush as redundant and silently breaks expression evaluation in exactly the handlers this
plan touched.

### Position in expressions

The two position fields answer two different questions, and a deferred code makes the difference matter
where it previously did not:

- **`userPosition`** is the parser's position — the target of the last move fed into lookahead, with
  offsets applied. It is the position *at the point in the file where the expression sits*.
- **`machinePosition`** is the live position, interpolated within the segment each drive is running. It
  lags the parser by however much is queued.

**For a deferred code, `userPosition` is the one that means what the author almost certainly intends**,
because the point in the file where the code sits is also the point in the path where its effect will
land. Parse-time evaluation gives exactly that, with no special rule for deferred codes:

```gcode
M42 P0 S{move.axes[2].userPosition > 10 ? 1 : 0}
```

`machinePosition` in a file is a different thing: read at parse time it is a race against the queue,
returning wherever the machine happened to have got to when the line was read. That is legitimate — it
is how you ask "where is the machine *now*" — but it is rarely what a positional condition in a part
program wants, and it was never well defined against a deep queue.

A user who wants the machine actually to be at the position they are testing writes `M400` first, which
is what RepRapFirmware users already do and what makes `machinePosition` catch up with `userPosition`.
Evaluating later instead would reintroduce the staleness problem in §2 and, worse, would let an
expression fail *after* its code had reported success.

That rests on M400 draining the actions as well as the moves, which is why §5 makes it a requirement
rather than an improvement.

No object model change is needed. The requested/actual split this plan widens is already modelled
throughout, so the work is to document which field answers which question, not to add fields.

---

## 8. Making the class declarative

The four classes in §1 should be declared per code rather than implied by which handler remembered
which call. One table, asserted by a test, diffable against RepRapFirmware's own behaviour.

The value is not the taxonomy. It is that "does this code need a standstill" becomes a reviewable
statement instead of an invisible omission; that "does this code need to wait for the channel at all"
becomes one too, which is what the Immediate class is for; and that the *Ordered* class acquires a name,
so the next person to read a handler with no flush in it can tell that this was decided rather than
forgotten.

---

## 9. The codes RepRapFirmware defers

`GCodeQueue::ShouldQueueMCode` and `ShouldQueueG10` are the whole of RepRapFirmware's list. It is
hand-maintained rather than derived, so this is a copy of it to tick off — a code deferred here that
RRF applies immediately, or the reverse, is a difference somebody chose rather than a gap.

Tick a row when the code defers in DuetControlServer through §3's mechanism.

| Code | What it does | RRF condition | Done |
| --- | --- | --- | --- |
| M3 | Spindle on, clockwise | only when `machineType != laser` — in laser mode it sets the power for the next `G1` | ⬜ |
| M4 | Spindle on, counter-clockwise | always | ⬜ |
| M5 | Spindle off | only when `machineType != laser` | ⬜ |
| M42 | Set output pin | always | ⬜ |
| M104 | Set tool temperature, no wait | always | ⬜ |
| M106 | Fan speed | always | ⬜ |
| M107 | Fan off | always | ⬜ |
| M117 | Display message | always | ⬜ |
| M140 | Set bed temperature, no wait | always | ⬜ |
| M141 | Set chamber temperature, no wait | always | ⬜ |
| M144 | Bed standby | always | ⬜ |
| M150 | Set LED colours | only when the strip does not need a standstill — a DMA-less strip does, and `MustStopMovement` says so | ⬜ |
| M280 | Set servo position | always | ⬜ |
| M300 | Beep | always | ⬜ |
| M568 | Tool settings — spindle RPM and temperatures | always | ⬜ |
| `G10` | Set tool temperatures | only when it modifies a *tool* and mentions **no axis letter**, because a tool offset change moves the axes and cannot be deferred | ⬜ |

Four gates apply on top of the list, and they are the reason it is short. Each has a counterpart here:

| RRF gate | Where it lands here |
| --- | --- |
| `machineState->DoingFile()` | §3, "only the file channels defer" — the same rule for the same reason |
| `!ContainsExpression()` | **Not needed.** RRF must refuse expressions because it defers the *code*; deferring only the effect evaluates them at parse time (§7) |
| `GetScheduledMoves() != GetCompletedMoves()` — nothing is queued unless the machine is actually moving | Falls out of the anchor: with nothing in the ring there is no preceding move to anchor to, so the effect applies now |
| `gb.DataLength() <= BufferSizePerQueueItem` — it must fit a fixed queue item | **Not needed.** The payload is the CAN frame the handler would have sent, which is bounded by the protocol rather than by a text buffer |
| `segmentsLeft == 0` — never queue mid-segmentation | Falls out of anchoring by move id rather than by a move *count*: a segment that turns out to command no movement cannot throw the anchor out |

Two entries are worth reading as decisions rather than omissions:

- **M291 is deliberately not queued**, and the comment in `ShouldQueueMCode` explains why at length: a
  non-blocking M291 sitting in the queue can be displayed *after* a later blocking one, overwriting it,
  leaving the blocking one unacknowledgeable except by a manual M292. That reasoning is about deferring
  the *code*. Whether it applies to deferring only the effect is an open question and M291 does not
  exist here yet, so the row is absent rather than unticked.
- **M116, M109 and M190 are absent** from RRF's list because they wait for temperature by definition. A
  code that waits is a barrier (§1), not a deferred code.

---

## 10. Order of work

1. **Declare the classes** (§8). Pure DuetControlServer, no behaviour change, and it makes the rest
   reviewable. This is where "some codes execute immediately" lands: M115 and M122 do not flush today,
   M409 does, and nothing says which is intended.

2. **`whenToExecute` on the command messages** — one edit to `Schema/can-messages.json`: the field, the
   two shortened arrays, the "now" sentinel and the generated offset table (§4). The generator emits
   the CANlib header, the C# mirror and both conformance harnesses, so the layouts cannot drift. No
   behaviour change: everything sends "now" and every board acts on arrival, exactly as now. Landing
   the protocol change on its own, with nothing depending on it yet, is what makes the next step's
   failures legible.

3. **The parked-command ring in `Duet3Expansion`** — one gate in `CommandProcessor::Spin`, copy out and
   free the buffer in the same pass, and on a full ring execute the nearest-due command and park the
   arrival in its place. Still no behaviour change, because nothing sends a future time yet.

4. **Action entries in `MotionService`** — an ordered list of `{ afterMoveId, payload }`, resolved as
   the anchoring move is prepared and emitted with it. A side list keyed by move id; the DDA ring does
   not have to change.

5. **`DuetSbc_MotionSubmitAction`**, down the *same* single-producer queue as `SubmitMove`. If actions
   travel a different path from moves, the race this plan exists to remove comes back.

6. **The lifecycle rules** (§5) — M400 waits for the list to drain, the purge hangs off
   `MovePlanner.StopEarlyAsync` beside the move purge, the drop broadcast on every purge, discard on
   `LinkService.Invalidate()`. Set `state.macroRestarted`, which nothing writes today. This belongs with
   step 5 rather than after the conversions: a half-built lifecycle is how a stale action reaches a
   halted machine.

7. **Verify the emergency-stop latch** in `Duet3Expansion` (§4). A prerequisite for shipping, not a step
   that can follow the conversions.

8. **Convert the deferred codes**, M106 first because it is the easiest to see and to test, then
   M42/M280, the spindle codes, and the heater setpoints.

9. **Remove M572's standstill**, once the driver push is a scheduled action. This closes the `TODO` in
   `MCodeHandler.Motion.cs`.

Laser is deliberately not in this list. Laser power must track the move's actual top speed — the native
`DDA` already scales it by `topSpeed / requestedSpeed` — so it belongs *on the move record*, like the
tuning in [MOTION_CONFIG_ORDERING.md](MOTION_CONFIG_ORDERING.md), not on the action timeline.
`MoveBuilder` carries a `TODO` for the `controlLaserOrIoBits` flag that this needs.

---

## 11. Verification

The property is testable offline, with no hardware and no timing, in the same way `DdaRingTests` proves
the tuning and stop properties:

- submit two moves and an action; spin; assert the action resolves to the second move's
  `moveStartTime + clocksNeeded` and is emitted in that move's own prepare pass;
- add a third move that chains onto the second, and assert the action's time is unchanged — the two
  candidate anchors coincide for chained moves, so a test that only ever chains cannot tell which one
  was implemented;
- separate the second and third moves by a gap, so the third starts from rest, and assert the action
  still fires at the second's end rather than being dragged out to the third's start;
- an action submitted after the last move in the queue still resolves, because it anchors backwards;
- an action anchored to a move a stop abandons is purged; one anchored to a move that ran is not.
  `DdaRingTests`' feedhold tests already build the ring states this needs;
- `WaitForStandstillAsync` does not return while an action is pending, and does return when the last one
  has gone;
- the purge boundary and the rewind boundary agree — which, since one is computed from the other (§5),
  is a test that the *computation* is used rather than that two rules were kept in step: for a pause at
  an arbitrary point, every action either survives and fires or is re-created by the replay, never both
  and never neither;
- the same code from a non-file channel is not deferred at all.

On the board side, and needing no hardware either — `CommandProcessor` is ordinary C++:

- a command with a past time or the "now" sentinel is dispatched on arrival, which is every message the
  machine sends today;
- a command with a future time is parked, its `CanMessageBuffer` freed in the same pass, and dispatched
  at that tick;
- a full parked ring executes the nearest-due command, parks the arrival in the freed slot, and counts
  the early execution — rather than dropping silently or blocking;
- a full parked ring whose nearest-due command *is* the arrival executes that and parks nothing;
- commands whose effects overwrite each other — two fan speeds — end in the state the latest-due one
  asked for, however the ring overflowed. This is the check that distinguishes the rule from executing
  the arrival, and the one a test of timing alone would pass either way;
- the drop broadcast empties the ring, and is idempotent when repeated.

What needs hardware is the end-to-end check: `M106 S255` mid-print, confirming both that the machine
does not pause and that the fan changes at the right point in the path.

---

## 12. Open questions

- Does an action between two moves inhibit their junction meld? The default must be **no** — actions do
  not influence planning — because that is what RepRapFirmware does and it is what keeps the print
  moving. `GCodeQueue` gets that answer by construction: it holds code text and creates no DDA, so
  lookahead never learns the command existed. The effect then lands somewhere on the rounded arc after
  the junction, at a point set by latency rather than by geometry.

  Anchoring in-band means the same answer by *choice*, and an option RepRapFirmware structurally cannot
  have: an action could ask for its junction to be planned to a stop when the effect must land at a
  place rather than approximately. Nothing has asked for it, and the feedhold already shows how to force
  a junction speed if it is ever wanted, so the default is no and there is no flag.

- What does an action mean on a machine running two motion systems? An action belongs to one ring. The
  feedhold has the same gap and names it the same way — only ring 0 is stopped, with a `// TODO` for
  M596 — so the two should be answered together rather than separately.
