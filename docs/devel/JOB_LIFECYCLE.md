# The job lifecycle: porting pause, resume, cancel and stop into DuetControlServer

Tracking document for the last big hole in
[MCODE_MIGRATION.md](MCODE_MIGRATION.md): §4's "No job lifecycle hooks", §7's item 9, §9's ⬜ half,
§11.4 phase F item 29, and [EVENTS_MIGRATION.md](EVENTS_MIGRATION.md)'s phase E all name the same
missing subsystem from different sides. This is that subsystem written down in one place: what
RepRapFirmware does, what [JobProcessor.cs](../../src/DuetControlServer/Files/JobProcessor.cs) does
today, what is missing, and the order to close it in.

The reference is `GCodes::DoSynchronousPause` / `DoAsynchronousPause`
([GCodes.cpp:1000, :1064](../../lib/RepRapFirmware/src/GCodes/GCodes.cpp)), the pause/resume state
machine in [GCodes4.cpp:558-760](../../lib/RepRapFirmware/src/GCodes/GCodes4.cpp), and the M-codes
that drive them in [GCodes2.cpp](../../lib/RepRapFirmware/src/GCodes/GCodes2.cpp) — M0/M1/M2 at :755,
M24 at :1160, M226/M600/M601 at :1249, M25 at :1281.

The contract in MCODE_MIGRATION §1 applies unchanged: port the behaviour, keep the CAN branch and
drop the local-hardware one, and leave a `// TODO` naming the missing piece rather than inventing a
stand-in. **One deviation is approved**, and §1.8 is why it is written down at this length rather
than decided while typing: an asynchronous pause plans a controlled deceleration instead of searching
for a stopping point that already exists. That is §3.5, and it is what phase 4 builds.

---

## 1. Where things stand

`JobProcessor` already has the shape of the whole feature. It has `Pause`, `Resume`, `Cancel` and
`Abort`; it has `IsPaused`, `IsCancelled`, `IsAborted`, `_pausePosition`, `_pausePosition2` and
`_pauseReason`; `DoFilePrint` already stops reading codes when `IsPaused`, rewinds the file to the
pause offset and waits on `_resume`. `MacroRunner` can run any system macro on any channel.
`MachineStatusService` already derives `state.status`. The object model already carries
`state.restorePoints[]`, `move.motionSystems[].restorePoints[]`, `job.build` and every `job` progress
field.

What is missing is almost entirely the **callers and the state between them**. Nothing anywhere calls
`JobProcessor.Pause`. The class was driven by an SPI notification from RepRapFirmware
(`Request.PrintPaused` → `HandlePrintPaused` in the old `SPI/Interface.cs`), and when that link was
deleted the caller went with it. The same deletion left two `await`s in the job path that can now
never complete — §2.1, which has to be fixed before anything else here can be tested at all.

So this is not a subsystem to build from nothing. It is a state machine to add, a restore point to
capture, five macros to call, and one native entry point to write — the feedhold of §3.5, which is
where the only deliberate divergence from RepRapFirmware lives.

---

## 2. The gaps

### 2.1 Two dead SPI vestiges block the job path outright

`LinkInterface.SetPrintFileInfo` and `LinkInterface.StopPrintAsync`
([LinkInterface.cs:369, :388](../../src/DuetControlServer/Link/LinkInterface.cs)) each `await` a
`TaskCompletionSource` that **nothing ever completes**. `SetPrintInfoRequest` and `StopPrintRequest`
are created by those two methods and are only ever touched again by `InvalidateCodes` /
`InvalidateCodesAsync`, which cancel them. There is no other reference in the tree.

Both are on the job path:

- `JobProcessor.SelectFileAsync` awaits `SetPrintFileInfo` as its last step
  ([JobProcessor.cs:305](../../src/DuetControlServer/Files/JobProcessor.cs)), so **M23, M32 and M37
  never return** — the await ends only by cancellation, which surfaces as the code being cancelled
  rather than as a selected file.
- `ExecuteAsync` awaits `StopPrintAsync` once the file task has finished
  ([JobProcessor.cs:600, :605](../../src/DuetControlServer/Files/JobProcessor.cs)), so a job that
  runs to the end **never completes its teardown**: the files are not disposed, `_finished` is never
  notified, `job.lastFileAborted` / `lastFileCancelled` / `lastFileSimulated` are never written, and
  `SelectFileAsync` — which waits on `_finished` when a file is already selected — can never start a
  second job.

Both notifications existed to tell RepRapFirmware what the SBC had decided. There is no second
program to tell. They are deletions, not implementations.

`PrintStoppedReason` survives them as the reason the job ended, which §2.7 needs; `PrintPausedReason`
is likewise still the right enum for §2.2 even though the firmware request that carried it is gone.

### 2.2 Nothing pauses a job

There is no M25, no M226, no M600 and no M601 in `MCodeHandler`'s switch, and no other caller of
`JobProcessor.Pause`. Every way RepRapFirmware can pause a print is therefore absent:

| Entry point | RRF | Notes |
|---|---|---|
| M25 from a file | `DoSynchronousPause(user, pausing1)` | GCodes2.cpp:1294 |
| M25 from elsewhere | `DoAsynchronousPause(user, pausing1)` | GCodes2.cpp:1312. Stops by feedhold — §3.5 |
| M25 while a non-restartable macro runs | deferred — §2.8 | GCodes2.cpp:1298 |
| M226 | `DoSynchronousPause(gcode, pausing1)`; `M226 P0` → `pausing2`, skipping `pause.g` | GCodes2.cpp:1269 |
| M600 | `DoSynchronousPause(filamentChange, filamentChangePause1)` — runs `filament-change.g`, falling back to `pause.g` | GCodes4.cpp:571 |
| M601 | as M226 | |
| A trigger firing | `DoAsynchronousPause(trigger, pausing1)` | GCodes.cpp:954. Takes the feedhold — §3.5 |
| A heater fault, filament error or driver error | `DoAsynchronousPause(...)` from the event handler | GCodes4.cpp:1981. Takes the feedhold — §3.5 |
| Low voltage or a stall | `DoEmergencyPause` → `LowPowerOrStallPause` | GCodes.cpp:1292 |

The event rows are [EVENTS_MIGRATION.md](EVENTS_MIGRATION.md) phase E, which is blocked on this
document and on M291. The trigger row is blocked on nothing running `trigger<n>.g` (MCODE_MIGRATION
§9). The last row is out of scope — see §5.

### 2.3 There is no pause *state*, only paused and not paused

RepRapFirmware's `PauseState` has five values — `notPaused`, `pausing`, `paused`, `resuming`,
`cancelling` ([GCodes.h:68](../../lib/RepRapFirmware/src/GCodes/GCodes.h)) — and the ordering is load
bearing: `notPaused < pausing < {paused, resuming, cancelling}`. `JobProcessor` has a single
`IsPaused` bool, so the three transitional states cannot be represented.

That is exactly what `MachineStatusService.Derive` says it is missing: its `// TODO` names Pausing,
Resuming and Cancelling as "gaps in what can be observed rather than missing branches here"
([MachineStatusService.cs:136](../../src/DuetControlServer/Model/MachineStatusService.cs)). Once the
state exists, the branches are three lines, and they go in the order
`RepRap::GetStatusIndex` tests them: pausing, resuming, paused, cancelling.

The distinction is not cosmetic. `pausing` is what makes a second M25 answer "Printing is already
paused!" rather than starting a second pause sequence; `resuming` is what makes M24 ignore a repeat;
`cancelling` is what keeps the status honest while `cancel.g` runs after the file has already been
closed.

### 2.4 There is no restore point

`ms.SavePosition(PauseRestorePointNumber, ...)` fills a `RestorePoint` with the user coordinates, the
feed rate, the virtual extruder position, the file position, the modal G0/G1/G2/G3 number, the active
tool and the fan speed
([RestorePoint.h](../../lib/RepRapFirmware/src/GCodes/RestorePoint.h)). `resuming1`/`resuming2` move
the head back to `moveCoords` — Z last, so the nozzle does not drag across the print — and
`resuming3` restores the feed rate and restarts the file from `filePos`.

DSF has the object model classes (`state.restorePoints[]`,
`move.motionSystems[].restorePoints[]`, `DuetAPI.ObjectModel.RestorePoint` — including
`GCommandNumber`) and nothing that writes them. `Motion/MovementState.cs` has no restore points and
no feed rate or tool tracking for one.

RRF numbers them: 1 is the pause point, 2 the tool change point, then the simulation and
resume-object points ([RawMove.h:86-91](../../lib/RepRapFirmware/src/Movement/RawMove.h)). G60 saves
a user one, and is also not implemented (`GCodeHandler.ProcessAsync` has no case 60).

### 2.5 The motion queue cannot be paused part-way through

MCODE_MIGRATION §11.6 records `DDARing::PauseMoves` and `LowPowerOrStallPause` as deliberately not
ported. They are what make an asynchronous pause stop at a sensible place: `PauseMoves`
([DDARing.cpp:592](../../lib/RepRapFirmware/src/Movement/DDARing.cpp)) walks the ring for the first
move it can pause *before* — a move whose predecessor has `CanPauseAfter` — frees everything from
there on, and hands back the end coordinates of the last move that will run plus the file position,
feed rate and proportion-done of the first move that will not.

The native side has the input for this: `DDA::CanPauseAfter()` exists and `MoveFlags.CanPauseAfter`
is already set from `RawMove.CanPauseAfter`. What is missing is the ring operation and a C entry
point for it — there is no `DuetSbc_MotionPauseMoves` in
[NativeLink.cs](../../src/DuetControlServer/Link/Native/NativeLink.cs).

Without it, the only pause available is "stop feeding the ring and let it drain", which is RRF's
`movesSkipped == false` branch: correct, but it means the head keeps moving until every queued move
has run. That is a usable first step and RRF has to handle that case anyway, so it is where phase 2
starts.

Moves already handed to an expansion board are a separate question, and the answer is the same as
RRF's: they are not recalled. RRF skips moves in its own ring only, and so should this.

**Phase 4 does not port `PauseMoves` as it stands.** It replaces it with a feedhold, which is the one
approved deviation in this document — §3.5.

### 2.6 None of the lifecycle macros run

MCODE_MIGRATION §9 lists them: `start.g`, `stop.g`, `cancel.g`, `pause.g`, `resume.g`,
`filament-change.g`. `MacroRunner.TryRunAsync` can run any of them today — a missing macro is not an
error there, which is exactly the contract these five need — so this is entirely a matter of calling
it from the right place with the right channel.

Where RepRapFirmware runs each:

| Macro | Run from | Condition |
|---|---|---|
| `start.g` | `StartPrinting(fromStart)`, GCodes.cpp:3846 | only when starting from the beginning, not when resurrecting |
| `pause.g` | `pausing1`, GCodes4.cpp:565 | only if all axes are homed; `M226 P0` goes straight to `pausing2` and skips it |
| `filament-change.g` | `filamentChangePause1`, GCodes4.cpp:576 | falls back to `pause.g` if absent |
| `resume.g` | M24, GCodes2.cpp:1193 | only if all axes are homed and not `M24 P0` |
| `cancel.g` | M0/M1/M2 while paused, GCodes2.cpp:787 | if it exists, it runs *instead of* `stop.g` and the heater switch-off |
| `stop.g` | `stopping`/`stoppingFromCode`, GCodes4.cpp:844, and M0 while paused, GCodes2.cpp:792 | if absent, all heaters are switched off instead |

The `cancel.g` / `stop.g` exclusivity and the "no macro means switch the heaters off" fallback are
both easy to lose and both visible on a real machine.

### 2.7 M0/M1/M2 do only the job half, and M24 only the resume half

`HandleStopAsync` says so in a comment: "The machine-side of a stop — heaters off, spindles off,
motors idle — belongs to subsystems that are not ported yet". Those subsystems have since landed —
`HeatManager`, `SpindleManager` and the M18/M84 driver path are all present — so the comment is now
out of date and the work is unblocked.

What is missing against `GCodes::StopPrint` ([GCodes.cpp:4700](../../lib/RepRapFirmware/src/GCodes/GCodes.cpp)):

- the distinction between `normalCompletion` (M0 from inside the file), `userCancelled` (M0 from
  elsewhere while paused) and `abort`. `JobProcessor` collapses this to `IsCancelled = IsPaused`,
  which happens to land on the right answer for both M0 cases but does not say so, and
  `PrintStoppedReason.UserCancelled` is never used;
- on abort: heaters off, spindles stopped, laser off;
- on normal completion: run `stop.g`, or switch all heaters off if there is none;
- clearing the tool's un-undone G10 Z hop back into the user position;
- `CancelWaitForTemperatures`, `buildObjects.Init()`, and clearing the job file's local variables;
- deleting `resurrect.g` on normal completion (out of scope — §5).

`HandleResumePrintAsync` likewise says "resume.g is not run: macro execution is not wired up yet",
which was true when it was written and is not any more. It also does not distinguish starting a
selected file from resuming a paused one, has no `P0`, and does not refuse a resume while pausing or
resuming is in progress.

### 2.8 A pausable macro is flagged and never acted on

`MacroFile.IsPausable` is set by `M98 R1` ([MCodeHandler.cs:950](../../src/DuetControlServer/Codes/Handlers/MCodeHandler.cs))
and read by nothing. In RepRapFirmware the flag is the whole basis of two behaviours:

- **A pause aborts the pausable macros.** The old DSF did this in `Channel.Processor.PrintPaused()`:
  pop every `MacroFile` off the stack, cancel the buffered and suspended codes, and resolve the
  pending lock requests. `ChannelProcessor.AbortAllFilesAsync` is the closest thing today, and it
  unwinds *every* macro regardless of the flag — which is the wrong rule for a pause, though the
  right one for an abort.
- **A pause during a non-restartable macro is deferred.** `GCodeMachineState::CanRestartMacro` walks
  the stack and returns false if any level is a macro that is not restartable; M25 then stashes
  `"M226"` (or `"M226 P0"`) in `deferredPauseCommandPending` and `CheckForDeferredPause` injects it
  once the file channel is back out of macros and not mid-tool-change
  ([GCodes.cpp:1223](../../lib/RepRapFirmware/src/GCodes/GCodes.cpp)). Nothing here does this, so a
  pause requested during, say, a tool change would either be lost or would tear the tool change in
  half.

### 2.9 In-flight codes, and where the file resumes from

`JobProcessor.Pause` already cancels `_cancellationTokenSource`, which cancels the codes the file
channel has in flight, and `DoFilePrint` already rewinds to `_pausePosition`. Two details are worth
keeping from the deleted SPI path, because both are non-obvious and both were deliberate:

- **A synchronous pause must not supply a file position.** The old `HandlePrintPaused` passed `null`
  for `PrintPausedReason.GCode` and `FilamentChange` with the comment "that would lead to an endless
  loop" — the file has already advanced past the `M226`, so rewinding to the position that produced
  it would re-run it forever. `DoFilePrint`'s fallback to `currentFilePosition` is the right
  behaviour for those two reasons and the wrong one for the rest.
- **Comments must not advance the position.** `DoFilePrint` already handles this
  (`!code.IsNonFirmwareComment`), and the reason is the same one: comments resolve internally and
  finish even while the job is paused.

RRF also cancels temperature waits on the file channel when it pauses
(`CancelWaitForTemperatures(true)`, and only for macros that can be restarted).
`HeatManager.WaitForTemperaturesAsync` takes a cancellation token, so the job's token cancellation
covers most of this, but the "only if the macro can be restarted" half needs §2.8's stack walk.

### 2.10 Nothing tracks job progress

RepRapFirmware's `PrintMonitor` ([PrintMonitor.cpp](../../lib/RepRapFirmware/src/PrintMonitor/PrintMonitor.cpp))
produces `job.duration`, `job.warmUpDuration`, `job.pauseDuration`, `job.layer`, `job.layerTime`,
`job.timesLeft` (file, filament and slicer estimates), `job.rawExtrusion` and `job.lastFileName`.
None of these is written anywhere in DuetControlServer: the only class that ever did is
[UpdateService.cs](../../src/DuetControlServer/Model/UpdateService.cs), which is wrapped in
`#if false` because it worked by querying RepRapFirmware over SPI with M409.

This matters to the job lifecycle in one specific way beyond the missing display: `IsReallyPrinting`
— which RRF uses to decide whether a pause is even possible, and which the filament monitors use to
decide whether to arm — is defined as `printMonitor->IsPrinting() && pauseState == notPaused`. The
DSF equivalent is `jobProcessor.IsProcessing`, which the plan below uses, so the port is not blocked
on `PrintMonitor`. But `M27` currently reports "SD printing byte …" whenever a file is *selected*
rather than *printing*, which is not what RRF does, and the progress fields stay empty until this is
built.

It is large enough to be its own phase, and it is the one part of this document that could reasonably
become its own file.

### 2.11 What else touches the lifecycle and is not here

- **M486 object cancellation** — `ObjectTracker`, `buildObjects`, `job.build` and the
  `ResumeObjectRestorePointNumber` restore point. Tracked as MCODE_MIGRATION §11.4 item 34; it needs
  the restore points from §2.4 first.
- **M73** slicer time hints — feeds `PrintMonitor::SetSlicerTimeLeft`, so it belongs with §2.10.
- **M291/M292** message boxes — needed for the *default* action of a pausing event
  (EVENTS_MIGRATION §3.5), not for the pause itself.
- **Power fail, `resurrect.g`, M911, M916, `SaveResumeInfo`** — see §5.

---

## 3. The design

### 3.1 One state, in `JobProcessor`

Replace `IsPaused` with a `PauseState` enum matching RRF's, keeping `IsPaused` as
`PauseState is Paused or Pausing or Resuming or Cancelling` for the existing readers, or updating
them. `MachineStatusService.Derive` reads the new property and gains its three branches in RRF's
order.

`IsCancelled` and `IsAborted` stay as they are — they describe how the *last* job ended, which is a
different question from what the job is doing now, and `job.lastFileCancelled` / `lastFileAborted`
are derived from them.

### 3.2 A restore point, captured where the pause is decided

Add `RestorePoint[]` to `Motion/MovementState.cs`, indexed as RRF indexes them, and a `SavePosition`
that fills one from the current user position, the channel's feed rate, the active tool and the fan
speed. Publish it to `state.restorePoints[]` and `move.motionSystems[].restorePoints[]` — the object
model classes exist, so this is a projection, in the sense §14 of MCODE_MIGRATION uses the word:
`MovementState` is authoritative and the model follows.

The pause path fills index 1; `resuming1`/`resuming2`/`resuming3` read it back. G60 fills a
user-numbered one and is cheap to add once the array exists.

### 3.3 The sequence as an async method, not a state machine

RepRapFirmware's `pausing1 → pausing2 → resuming1 → resuming2 → resuming3` states exist because
`GCodes::Spin` cannot block. `JobProcessor` runs on its own task and can `await`, so the equivalent
is a straight-line async method — `PauseSequenceAsync`, `ResumeSequenceAsync`,
`CancelSequenceAsync` — with the same steps in the same order and the same standstill waits between
them. This is the same substitution `EventProcessor` already made for RRF's `AutoPause` channel
polling, and it is recorded here so the next reader does not go looking for the missing states.

The two things the state machine buys that an `await` does not, and how each is kept:

- **`PauseSequenceAborted`** ([GCodes.cpp:3492](../../lib/RepRapFirmware/src/GCodes/GCodes.cpp)): if
  `pause.g` is aborted mid-way, the frame that would have advanced `pausing` to `paused` goes with
  it, and the machine hangs reporting "pausing" forever. The `await` equivalent is a `finally` that
  settles the state to `Paused` however the macro ended.
- **Re-entrancy**: a second M25 arriving while `pause.g` runs must be refused, which is what the
  `pausing` state is for (§2.3).

**The resume still moves the head back in two moves, not one.** Collapsing `resuming1` and
`resuming2` into a single move is the obvious simplification once the states are gone, and it is
wrong: the split is behaviour, not bookkeeping. RRF restores Z last when the head has to come down
and first when it has to go up, so the nozzle never drags across the print on its way back to the
pause point. `ResumeSequenceAsync` keeps both moves and the condition that orders them.

Only the single-motion-system branch of `resuming1` is ported — the `#else` at
[GCodes4.cpp:665](../../lib/RepRapFirmware/src/GCodes/GCodes4.cpp). The `SUPPORT_ASYNC_MOVES` branch
above it allocates each axis to whichever motion system owns it before restoring it, which needs
axis ownership that does not exist here yet. That is a `// TODO` at the point of use naming M596, not
a silent simplification, and it has to be revisited when multiple motion systems land: with two
systems the restore is per-system and the Z ordering has to hold across both.

### 3.4 Which channel each macro runs on

`pause.g`, `resume.g`, `cancel.g` and `stop.g` run on the channel that commanded the operation in
RRF, except when an event caused it, in which case they run on `Autopause`.
`EventProcessor` already runs its macros on `CodeChannel.Autopause`, so the pause sequence takes the
channel as a parameter and the event path passes `Autopause`. `start.g` runs on `File`, because RRF
is explicit about why: "so that any M82/M83 codes will be executed in the correct context"
(GCodes.cpp:3846).

---

### 3.5 The feedhold: an asynchronous pause plans its own stop — *approved deviation*

This is the one place the port deliberately does something RepRapFirmware does not, so it is set out
in full: what RRF does, why it is not enough, what replaces it, and what that costs.

It is a **substitution, not an addition**. Every asynchronous pause stops this way — `M25` from a
console or an interface, and the default action of the three events that pause. The machine comes to
rest in a different place from RepRapFirmware's, and sooner. That is a difference an operator can
see, so it is the entry in
[rrf-differences.md](../../src/Documentation/articles/rrf-differences.md) §8 as well as being written
up here.

What is *not* substituted is the synchronous pause. `M25` from inside the job file, and `M226`,
`M600` and `M601`, which may only appear there, all wait for standstill exactly as RepRapFirmware
does — see the end of this section for why there is nothing else they could do.

#### What RepRapFirmware does

`DDARing::PauseMoves` ([DDARing.cpp:592](../../lib/RepRapFirmware/src/Movement/DDARing.cpp)) does not
*create* a stopping point. It **searches for one that already exists**: it walks the ring for the
first DDA whose predecessor returns `CanPauseAfter()`, frees everything from there on, and reports
the endpoint of the last move that will run.

`CanPauseAfter` is true only when both of these hold
([DDA.cpp:1092](../../lib/RepRapFirmware/src/Movement/DDA.cpp),
[DDA.h:367](../../lib/RepRapFirmware/src/Movement/DDA.h)):

- the move's end speed, projected onto every drive, is at or below that drive's instantaneous speed
  change — i.e. the toolpath already happens to slow to a full stop's worth of jerk at that junction;
- the following DDA is not committed, because a move already sent to an expansion board cannot be
  recalled.

The first condition is the problem. In a continuous print at speed, lookahead has deliberately raised
every junction speed above jerk — that is what lookahead is *for* — so `canPauseAfter` is false at
essentially every junction in the ring. `PauseMoves` then finds nothing, returns false, and
`DoAsynchronousPause` takes its `movesSkipped == false` branch: **no moves are skipped and the whole
ring drains**. The head carries on to the end of everything queued before it stops.

RRF accepts that because the alternative it has is the emergency path, `LowPowerOrStallPause`
([DDARing.cpp:687](../../lib/RepRapFirmware/src/Movement/DDARing.cpp)), which stops abruptly by
cancelling the step interrupt mid-move. That is a correct response to a power failure and the wrong
one to a user pressing pause, so a normal pause gets the conservative search and lives with the
overshoot.

#### What the feedhold does instead

There is a third option RRF does not take: rather than looking for a junction that is already slow
enough, **make one**. Force the end speed at the chosen point to zero and let the existing profile
planner produce the deceleration ramp that gets there.

The earliest point at which this is possible is the **first uncommitted DDA**. A committed move has
had its segments generated and dispatched to the expansion boards
([DDARing.cpp:29-31](../../src/DuetSbcInterface/src/Motion/DDARing.cpp)), which fixes both its
profile and — because the next move's start speed is the committed move's end speed — the speed the
hold has to start from. Moves are committed `MoveTiming::usualMinimumPreparedTime` ahead, which is
50 ms, so a feedhold costs at most 50 ms of already-dispatched motion plus one deceleration ramp,
against RRF's "however long the rest of the ring takes".

The mechanism needs less new code than it sounds like, because **the ring already decelerates to zero
constantly**. A newly added DDA is created with its end speed at zero and only has it raised when a
successor arrives and `DoLookahead` propagates backwards
([DDARing.cpp:24-27](../../src/DuetSbcInterface/src/Motion/DDARing.cpp)). Every trailing edge of every
print is already this operation. A feedhold is therefore:

1. take the first uncommitted DDA;
2. choose the stopping point at or after it (see the fork below);
3. set that DDA's end speed to zero and re-run `RecalculateMove` backwards over the uncommitted DDAs
   in front of it, exactly as adding a move re-runs it forwards;
4. `Free()` everything after the stopping point;
5. report back what was purged.

Step 3 is bounded on the near side: the backward pass stops at the last committed DDA, whose end
speed cannot change. If the distance between there and the stopping point is not enough to decelerate
to zero, the stopping point has to move further out — which is the fork.

#### The fork: stop at a boundary, or truncate a move

Two variants were considered. **(a) is the decision** — recorded in §6, and what phase 4 builds:

**(a) Boundary.** Walk forward from the first uncommitted DDA accumulating distance until there is
enough to decelerate to zero at the most restrictive deceleration of the drives involved, and stop at
that DDA's *end*. No DDA is truncated, so the machine stops where a move was always going to end.

**(b) Truncating.** Stop the instant the ramp reaches zero, part-way through a DDA, shortening that
DDA and recording how much of it was done. This is the theoretical minimum distance, and it is what
"earliest possible opportunity" strictly means.

(b) stays available as a later refinement. The two reasons (a) was chosen are the two things (b)
would have to pay for first:

- (b) reopens the correctness exclusions `canPauseAfter` encodes. It is cleared for arc segments
  ("the arc centre gets recomputed incorrectly when we resume",
  [GCodes.cpp:3213](../../lib/RepRapFirmware/src/GCodes/GCodes.cpp)), for retractions ("that could
  cause too much retraction", GCodes.cpp:4557) and for endstop, probing and `G1 H` moves. Stopping at
  a boundary lets those exclusions be honoured by choosing the next permitted boundary; stopping
  inside a move has to argue each case afresh;
- the overshoot (a) accepts is small in practice, because the moves are already segmented
  (MCODE_MIGRATION §11.4 phase E), so boundaries are dense along exactly the long fast moves where
  the ramp is longest.

#### A boundary is a segment boundary, so the resume carries a fraction

The density that makes (a) cheap is the same fact that decides what the resume has to do. **A move
the engine knows is one segment, not one line of the file** — segmentation is what the height map and
a non-Cartesian geometry require — so the boundary the stop lands on is usually *inside* a G-code, and
every segment of that code carries the same file position. Rewinding to it and re-reading the line
plainly would ask for the whole line a second time.

What the machine still owes is `1 - proportionDone` of it, and which of the line's words that applies
to follows from what a word means:

| The line says | Owed after resuming from the stop point | Why |
|---|---|---|
| an absolute axis target (G90) | the target, unscaled | the machine restarts from where it stopped, so the rest of the line *is* the rest of the move |
| a relative axis word (G91) | the word × `1 - proportionDone` | it is a distance to travel, and part of it has been travelled |
| extrusion, in either mode | the movement × `1 - proportionDone` | extrusion is an amount however the file expresses it |

The last row includes *absolute* extrusion, and the asymmetry with the row above it is deliberate
rather than an oversight. An absolute axis target needs no scaling because the resume moves the
axes' **start** — the head goes back to where it stopped and the interpreter position with it — so
what the line names is already what is left. An extruder has no start to move: the resync is
axes-only, because the engine carries the fraction of a step between moves. RepRapFirmware therefore
moves the *reference* instead, rewinding `latestVirtualExtruderPosition` to the extruder position at
the start of the interrupted line, so that `E250` again means the whole line's extrusion and the
scale factor takes the rest. Both E modes then behave identically, which is the property worth
having. That rewind is the one part not yet built here, because nothing tracks the absolute extruder
position at all (§15.2): what lands with it must restore the line's *start* value, not the stop
point, or the same filament is counted twice.

The fraction is a fraction of the **whole code**, however many times the job has been stopped inside
it. A resume rebuilds the code scaled by `1 - proportionDone`, so a second stop inside the same code
measures its segments against a move that is already the remainder of one, and what is recorded is
`fractionAtStart + (1 - fractionAtStart) × segmentsMade / segmentCount`, where `fractionAtStart` is
what that build itself skipped. RepRapFirmware needs no such composition because it re-reads the
whole code and walks all of its segments, emitting only from `segmentsLeftToStartAt` onwards, so its
`totalSegments` is always the whole code's. Scaling the move instead is the shorter route and is
exact here, because the stop is always *on* a segment boundary and there is no partial segment to
re-enter, which is why `segmentsLeftToStartAt` and `firstSegmentFractionToSkip` have no counterpart;
composing the fraction is what that route costs.

The fraction travels from the record of the interrupted code to the restore point and then to
[`MovementState.MoveFractionToSkip`](../../src/DuetControlServer/Motion/MovementState.cs), which the
first job-file move read afterwards spends and clears. The first file channel's throughout: there is
one interpreter state and one pause restore point, both of them `File`'s, so a fork of the job
neither records a fraction nor may spend one, and a stream that cannot have recorded it must not
consume what the other is owed. That widens with M596 and M598, both halves together. Only the job
file's own codes may spend it, by the same test that decides whether a move is recorded at all: a
macro invoked between the resume and the job's next move runs on the `File` channel too, and would
otherwise shorten its own move by the fraction the job is owed.

Two things the resume needs for the same reason, since the line it lands on is a line the file was
already reading rather than one it was about to start:

- **the modal G command**, because that line may be a bare `X100 Y100 E5` whose G1 is several lines
  above the rewind point. Seeking throws the parser's `LastGCode` away, so the resume puts it back —
  RRF's `SetModalGCommand`;
- **the feed rate the line was read with**, unscaled by M220, because the line need not name F.

`InitialUserC0` / `InitialUserC1` are the one part of RRF's set with no counterpart here yet. They
exist so that a re-read *arc* reconstructs its centre from where the arc began rather than from where
the machine stopped, and until G2/G3 is implemented there is no arc to reconstruct — at which point
either they land with it or arc segments stop carrying `canPauseAfter`, which is the exclusion RRF
uses. The same goes for firmware retraction.

Either way, `canPauseAfter` stays and keeps the meaning it has: **not** "the junction is slow enough"
— the feedhold no longer cares about that — but "this is a junction a print can be restarted from".
The jerk half of the test at `DDA.cpp:667` becomes dead for the feedhold path and should be left
alone rather than deleted, because `LowPowerOrStallPause` will want it if the power-fail work ever
lands (§5).

#### What DuetControlServer has to be told

The purge invalidates state on both sides of the C ABI, and the native side knows nothing about files
— which is the right split and should stay that way. So the native call reports motion facts and DCS
maps them back to the file itself:

| Reported | Used for |
|---|---|
| Drive endpoints where motion actually stops | `ResyncFromEngine`, and `RestorePoint.Coords` |
| `MoveId` of the first purged move | The code to rewind to, and how much of it was made |
| Number of moves purged | Diagnostics, and which of the two sources names the resume point |

`MoveParams` carries a `MoveId` and no file position, and it should stay that way. DCS already keeps
side tables keyed on that id: `EndstopCorrection.NoteMoveId`
([GCodeHandler.cs:351](../../src/DuetControlServer/Codes/Handlers/GCodeHandler.cs)) is the precedent,
and `MotionTracker` already tracks `LastCompletedMoveId` per ring. The feedhold adds one more,
[`JobMoveIndex`](../../src/DuetControlServer/Motion/JobMoveIndex.cs), which maps a `MoveId` to the
job code the move came from and to the move's own place in that code. What it maps to, and why that
is one record per code rather than a copy of the fields per move, is below.

Two pieces of DCS state are stale the moment the purge happens and must be corrected before anything
else runs:

- **`MovementState.CurrentUserPosition`**, which is the interpreter's position and has run ahead of
  the machine by however many moves were queued. It becomes the reported stop position and the
  restore point the resume moves back to, so it is put back into step with the machine — through the
  builder's endpoints and the inverse transform, `MoveInterpreter.SyncInterpreterToMachine`. RRF's
  equivalent is setting `ms.positionMayBeInaccurate = true` so the position is re-read at the next
  standstill; here it is read back directly, because `ResyncFromEngine` already exists for this.
  Getting this wrong is not a reporting detail: the restore point would name the end of the queue
  that was just discarded, and the resume would drive the head there before reading the file again.

  **Under the same lock as the purge**, which is why `StopEarlyAsync` is given the interpreter rather
  than left to the caller to correct afterwards. Everything between the purge and the restore point —
  the flush, abandoning the macros, waiting for standstill — is time in which another channel can
  build a move, and a move built from the end of a discarded queue starts from a place the machine
  has never been.
- **`MovementState.SegmentsLeft`**, if the interpreter was mid-way through submitting a segmented
  move. Those segments must be abandoned, not submitted into a ring that has just been emptied —
  queueing them after the purge would start the machine moving again once it had come to rest. The
  claim is dropped here and the loop that holds the segments is told by `PurgeGeneration`, which it
  compares against what it saw when it built the move. RepRapFirmware needs no equivalent because
  its pause runs in the same task as the loop it is interrupting.

  The generation is bumped when the stop is *requested*, not when its result is read back: the motion
  thread acts in its own time, and that window is exactly when a submission would otherwise keep
  feeding a ring that is about to be emptied. It says one thing, and it is global because a purge is
  global: a macro's segmented move is as void as the job's. Voiding one costs nothing even where the
  engine turns out not to have stopped, because the only caller is a pause and a pause abandons the
  macros immediately afterwards. What the generation does not say is anything about the job file.
  That is the record's job, below.

#### The resume point is one record, taken once

Where to rewind the file to and how much of that code the machine has already made are two halves of
one fact, so nothing derives them separately. One record describes the job code the interpreter is
part-way through, and every consumer reads that record.

[`JobMoveOrigin`](../../src/DuetControlServer/Motion/JobMoveIndex.cs) is it, one per job-file
movement code rather than one per queued move: the code's file position, the modal G command it was
read under, the feed rate it was read with unscaled by M220, the fraction the build itself started
from, how many segments that build produced, and how many of them have gone to the ring. That is
RepRapFirmware's `ms.raw` together with `ms.totalSegments` and `ms.segmentsLeft`, which is the set
`DoAsynchronousPause` reads in its `segmentsLeft != 0` branch
([GCodes.cpp:1092](../../lib/RepRapFirmware/src/GCodes/GCodes.cpp)). `MovementState` holds the
current one, as `MovementState` holds `raw` there, and `JobMoveIndex` maps each queued move id to
that record and to the move's own index within it. A file position and a fraction read from one
record cannot describe different codes, which is what the arrangement is for.

The record is cleared by whatever ends the code: the submission, when it is done with it, or the
pause, which **takes** it. The take is one call under the planner lock and it produces the whole
resume point:

| What the stop did | Where the resume point comes from |
|---|---|
| Moves were purged and the earliest is a job move | The index: that move's record, at that move's segment |
| Moves were purged and the earliest cannot be named | Nothing. The earliest was a macro's, so the job's own code had not started; the resume rewinds to the last completed job code, which is the macro invocation |
| Nothing was purged | The current record, at the segments it has queued. Everything queued was already committed and will run, so the first segment not queued is the boundary |
| No job code was in flight | Nothing. The last completed code is the pause point, which is every synchronous pause |

Those are RepRapFirmware's three branches of `DoAsynchronousPause`
([GCodes.cpp:1086](../../lib/RepRapFirmware/src/GCodes/GCodes.cpp)), each of which fills in the file
position and the proportion together and then calls `ClearMove`. The result is one nullable value
carrying a file position, a fraction, a G command number and a feed rate, so a fraction that names no
position cannot be expressed. Three things read it, and all three read that one value: the position
`DoFilePrint` seeks to, the restore point's `ProportionDone`, `GCommandNumber` and `FeedRate`, and
through the restore point the modal state `RestoreModalStateForResume` puts back.

A code every segment of which reached the ring resumes at the code *after* it, not at its own start
with all of it skipped. Everything queued is committed and will run, so nothing of that code is still
owed, and rewinding to it would ask the machine for a move of no length. RepRapFirmware reaches the
same place from the other side, with a proportion of one that skips every segment when the code is
read again.

Taking the record is also what fixes the segment count in it. A submission whose record has been
taken queues no more segments of that code, so what the take read stays true; the take therefore
comes before the read-ahead is cancelled rather than after, since the cancellation would otherwise
end the submission somewhere the take had not looked. Between the stop and the take nothing can add
to the index either, because `PurgeGeneration` is already raised and only a job move is ever
recorded. A submission ended this way reports its code as cancelled rather than as done, which is
what keeps `DoFilePrint`'s own position, the fallback in the two rows above that name nothing, at the
end of the last code that really completed.

An interrupted code is truncated whether or not the engine stopped early, and the truncation is
recorded rather than silent. That is what makes a refused stop safe: the machine drains the queue as
phase 2's pause does, the code that was going out ends where it ends, and the record says how much of
it was made, so the resume asks for the rest of that code and no more. RepRapFirmware does the same
in the branch above, discarding the waiting move after recording it.

The one race left is RepRapFirmware's own, and it notes it at the same place
([GCodes.cpp:1112](../../lib/RepRapFirmware/src/GCodes/GCodes.cpp)): a submission that ends for its
own reasons in the same instant as the take leaves nothing to take, and the resume re-reads the code
from its start. Here that costs one code re-run rather than a wrong fraction, because a record is
never left behind for another pause to adopt.

#### Resuming needs no replan

Worth stating plainly, because "replan the purged moves" is the natural thing to expect and it is not
what happens. The purged DDAs are not stored, re-planned or re-submitted. Resume rewinds the job file
to the recorded position and the codes are read again from there, so `MoveInterpreter` and
`MoveBuilder` rebuild the moves from scratch — through whatever the machine's state is *after*
`resume.g`, which is the only correct thing to do anyway, since `resume.g` may have changed the tool,
the temperatures or the position. This is exactly RRF's resume path (`fgb->RestartFrom(filePos)`,
GCodes4.cpp:723) and the feedhold inherits it unchanged.

What resume gains is that its restore-point coordinates describe a point the toolpath passes through
mid-decel rather than the end of a line — under variant (a) still a real segment endpoint, which is
what makes re-reading the line from there exact once the already-made fraction of it is taken off (see
above).

#### What the stop shares with a pause that drains

Only the stopping is different. Everything past it is the pause that was already there:

- **`pause.g` runs**, unchanged. A feedhold stops sooner but is otherwise the same event — the machine
  is paused, at a restore point, waiting to resume — so it takes the same macro, the same restore
  point and the same resume path. A macro that had to ask *how* the machine stopped would be asking
  about something already finished by the time it runs.
- **The pause reason is unchanged.** `M25` is still `PrintPausedReason.User` and an event still
  carries its own. The reason set describes *why* the job paused and the feedhold is a fact about
  *how*; conflating them would put a transport detail into an enum that `pause.g`, the object model
  and the event system all read.
- **Resuming is unchanged**, because it never depended on how the machine stopped — see "Resuming
  needs no replan" above.

**The stop is asynchronous only, and that is not a policy but an observation.** A synchronous pause —
`M25` from inside the job file, `M226`, `M600`, `M601` — waits for standstill by definition
(`LockCurrentMovementSystemAndWaitForStandstill`), because the job file has itself reached the pause
point and everything queued ahead of it is what has to run. There is nothing left to purge, so there
is nothing for a feedhold to do. `PauseAsync` therefore skips the stop entirely when `synchronous` is
set rather than asking for one that would find nothing.

#### 3.5.1 Which pauses are feedholds

Every asynchronous pause is a feedhold. What the rule still has to be narrow about is the event
system: **where RepRapFirmware would pause for an event, that pause becomes a feedhold** — and
nothing else about events moves. Which events pause, and which of them run `pause.g`, stays exactly
as RepRapFirmware decides it. The deviation changes *how the machine comes to a stop*, not *what
stops it*.

| Pause | Path | Why |
|---|---|---|
| `M25` from a console or an interface | **Feedhold** | Nothing is queued ahead of it that has to run |
| `M25` from the job file, `M226`, `M600`, `M601` | Faithful | Synchronous: the queue ahead of the pause point is what has to run, so there is nothing to purge |
| `heater_fault`, `filament_error` default action | **Feedhold** | RRF pauses here, so this pause is a feedhold |
| `driver_error` default action | **Feedhold**, without `pause.g` | RRF pauses here too, and already skips the macro |
| Trigger 1 firing (M581) | **Feedhold** | RRF pauses here as well — see the note below |
| `driver_stall`, `driver_warning`, `mcu_temperature_warning`, `overvoltage`, `undervoltage` | — | RRF does not pause for these, so neither does this. They log, as they do today |
| `expansion_reconnect`, `expansion_timeout` | — | Likewise |

Stating it this way rather than as "an event should stop as soon as it can" is the point.
`Event::GetDefaultPauseReason` ([Event.cpp:115](../../lib/RepRapFirmware/src/Platform/Event.cpp))
stays the one place that decides whether an event pauses at all, and the feedhold is one more
property hanging off that decision rather than a reason to revisit it. A rule phrased around urgency
would invite exactly that revisiting — someone would notice that an undervoltage warning is urgent
too — and the deviation would grow a new pause RepRapFirmware does not have, in a codebase where
[EVENTS_MIGRATION.md](EVENTS_MIGRATION.md) §1.5 is the only record of which events pause.

**Trigger 1 is an inference, not an event.** It is a separate call site
([GCodes.cpp:954](../../lib/RepRapFirmware/src/GCodes/GCodes.cpp)), not one of `Event.cpp`'s types,
so "where RRF would pause for an event" does not literally reach it. It is included because it is the
same kind of pause — asynchronous, nobody typed it, RRF pauses — and excluding it would leave one
async pause source on the faithful path for no reason anyone could reconstruct later. If trigger 1
should stay faithful, this is the row to change.

Three things this does **not** change, each easy to assume otherwise:

- **The macro path still wins.** These are *default* actions. An event whose macro exists runs the
  macro and pauses nothing (EVENTS_MIGRATION §1.5), and that is the path machines actually configure.
  The feedhold applies only where RepRapFirmware would have paused by itself.
- **`driver_error` still skips `pause.g`.** RRF routes it to `eventPausing2` rather than
  `eventPausing1` ([GCodes4.cpp:1981](../../lib/RepRapFirmware/src/GCodes/GCodes4.cpp)), because
  `pause.g` typically lifts and parks the head and a driver in error cannot be trusted to move.
  "Stop by a controlled deceleration" and "then run `pause.g`" are separate flags on the pause
  sequence and stay separate — a feedhold does not imply the macro. The feedhold is still the right
  stop for it: it asks the erroring driver for strictly *less* motion than draining the ring would.
- **This is not the emergency path.** `DoEmergencyPause` / `LowPowerOrStallPause` cancel stepping
  mid-move and accept the position loss, which is right for a power failure and wrong for everything
  here. They stay out of scope (§5). The feedhold sits between them and the faithful pause: sooner
  than RRF's, still under full control of the motion planner.

`DDARing::PauseMoves` — RepRapFirmware's search — is ported and tested alongside
`DDARing::Feedhold`, and the two differ only in how they choose the stopping point and share the
purge. **Nothing in DuetControlServer asks for it any more**, now that every asynchronous pause
feedholds. It is kept rather than deleted because it is the reference behaviour this deviation is
measured against and because the power-fail work (§5) will want a stop that changes no profile, but
it is unreachable code and should be recorded as such rather than assumed live.

The consequence for the plan is that **phase 6 depends on phase 4**, where before it only depended on
phase 2. Landing the event pauses first would ship them on the faithful path and then change their
behaviour underneath machines that had started relying on it.

#### Recording the deviation

`src/Documentation/articles/rrf-differences.md` gets an entry **when phase 4 lands and not before** —
that article is for deviations that are present and working, and an entry written ahead of the code
would read as a settled behaviour that does not exist yet.

## 4. The plan

### Phase 0 — delete the two dead awaits ✅

`SetPrintFileInfo` and `StopPrintAsync` and their `TaskCompletionSource`s go, along with
`InvalidateCodes`' handling of them. `SelectFileAsync` ends after logging; `ExecuteAsync` keeps the
three log lines and the `PrintStoppedReason` it picks, which phase 3 turns into the argument to
`StopPrint`.

Nothing else in this document can be tested until this lands: today a job can neither be selected nor
finish.

- [x] Remove `SetPrintInfoRequest`, `StopPrintRequest`, `StopPrintReason`, `PrintStateLock` and the
      two methods
- [x] `SelectFileAsync` no longer awaits the firmware
- [x] `ExecuteAsync` reaches its teardown
- [x] Correct the stale comment "Prints are cancelled by M0/M1/M2 which is processed by RRF"
- [x] `InvalidateCodesAsync` / `InvalidateAsync` went with them: the print lock was the only thing
      async about either, so they had become duplicates of the synchronous pair

### Phase 1 — the state and the restore point ✅

- [x] `PauseState` on `JobProcessor`, in RRF's order
- [x] `MachineStatusService` gains Pausing, Resuming and Cancelling, and its `// TODO` goes
- [x] `RestorePoint[]` on `MovementState`, `SavePosition`, and the projection into
      `state.restorePoints[]` and `move.motionSystems[].restorePoints[]`
- [x] G60
- [x] `MovementState.VirtualFanSpeed`, which the restore point's fan speed comes from and which
      nothing tracked

The three transitional states are produced by phase 2; phase 1 is what makes them representable and
observable. `Motion.RestorePoint` is a separate class from `DuetAPI.ObjectModel.RestorePoint` because
RepRapFirmware's object model table publishes six of its eleven members and the other five - the file
position, the proportion done and the arc start coordinates - are how to resume rather than where the
machine is. C++ selects members with a table; here the model class *is* the wire format, so the split
is between two classes rather than between a class and its table. It is qualified as
`Motion.RestorePoint` in the two files that see both, which is what `Model.ObjectModel` already does.

### Phase 2 — pause and resume, without skipping queued moves ✅

The `movesSkipped == false` branch of `DoAsynchronousPause`: flush to standstill, save the restore
point from where the machine actually stopped, take the file position from the job file.

- [x] `JobProcessor.PauseAsync(channel, reason, macro, synchronous, reportPosition, pausingCode)` — flush, save the restore
      point, cancel the file channel's in-flight codes, set `Pausing`, run `pause.g` (or
      `filament-change.g` falling back to it), settle to `Paused` in a `finally`
- [x] `M25` — from a file, from elsewhere, "Printing is already paused!", "Cannot pause print,
      because no file is being printed!"
- [x] `M226`, `M600`, `M601`, including `M226 P0` skipping `pause.g` and the "use M226/600/601 only
      within a file being printed" refusal
- [x] A synchronous pause supplies no file position (§2.9)
- [x] `M24` — refuse while `Pausing` or `Resuming`; `resuming1`/`2`/`3` equivalent restoring the feed
      rate and moving the head back in **two** moves, Z ordered last or first by direction (§3.3);
      `M24 P0` skips `resume.g`; `pause.g` and `resume.g` only when all axes are homed
- [x] A `// TODO` on the resume moves naming M596 — the multi-motion-system branch of `resuming1` is
      not ported and the Z ordering has to be revisited across both systems when it is
- [x] `M24` on a selected-but-not-started file runs `start.g`, and so does M32

Three things the port had to get right that reading the RRF state machine alone does not show:

- **The pause sequence must not flush the channel it is running on.** `FlushAsync(channel, flushAll)`
  drains every pipeline stage including the pausing code's own, so a synchronous pause would wait for
  itself. Only the asynchronous path flushes the job channel; the synchronous one relies on the
  handler's `FlushAsync(code, ...)`, which by construction only flushes stages ahead of the code.
- **Cancelling the read-ahead cancels the pausing code with it.** A code's cancellation token is the
  job's, so `M226` cancels its own token and every step after it. The code is re-armed the way
  `HandleStopAsync` already re-arms one, and the rest of the sequence runs on
  `ApplicationStopping` rather than the caller's token.
- **The cancel has to come before the flush**, not after. A job code waiting on a temperature would
  otherwise hold the flush up for as long as the heater takes. RepRapFirmware reaches the same place
  from the other side, with `CancelWaitForTemperatures(true)` inside `DoAsynchronousPause`.

`resuming1`'s two moves are conditional, not unconditional: RepRapFirmware moves every axis together
when the head is at or below the pause height, and only splits the move - travel across, then descend
- when the head is above it, which is where the dragging risk actually is.

### Phase 3 — stopping 🟡

- [x] `JobProcessor.StopAsync(channel, reason)` carrying `PrintStoppedReason` properly
- [x] `M0/M1/M2` from inside the file → normal completion → `stop.g`, or all heaters off if absent
- [x] `M0/M1/M2` while paused → user cancelled → `cancel.g`, *or* `stop.g` and the heater switch-off
      if there is no `cancel.g`
- [x] Abort → heaters off, spindles stopped
- [x] A job that simply runs out of codes goes through the same sequence, guarded so that a file
      ending in M0 does not run `stop.g` twice - and the guard applies only to a selected job, so
      `M0` with no job still works every time
- [x] `HeatManager.SwitchOffAllAsync`, which did not exist
- [x] The G10 Z hop unwind
- [x] `Cancelling` is observable while `cancel.g` runs
- [ ] The temperature-wait cancellation and the job file's local variables
- [ ] The laser is switched off on abort — `// TODO` at the point of use, waiting on M452

### Phase 4 — feedhold 🟡

§3.5. Not a port of `DDARing::PauseMoves`; the approved deviation replaces it.

- [x] `JobMoveIndex`, bounded rather than pruned on completion: a job queues faster than the engine
      runs, so forgetting only what completed still grows without limit
- [x] One record per interrupted job code rather than a copy of its fields per queued move. The file
      position, the modal G command, the feed rate, the fraction the build started from and its
      segment count, held by `MovementState` while the code is in flight and indexed by move id and
      segment
- [x] `JobResumePoint` and the take: one call under the planner lock, before the read-ahead is
      cancelled, yielding the file position and the fraction together or neither. It replaced
      `MovementState.AbandonedJobMove`, whose generation key still admitted a record left by a pause
      sequence that made no stop of its own
- [x] The fraction composes over the whole code rather than over the part the build was given, so a
      second stop inside one code reports what the machine has made and not what the remainder had
- [x] A submission the take ends reports its code as cancelled rather than as done, so the position
      `DoFilePrint` falls back to stays the end of the last code that completed
- [x] Only the job file's own codes spend `MoveFractionToSkip`, by the same test that decides whether
      a move is recorded at all; a macro on the `File` channel does not
- [x] Tests for the accounting: a stop inside a segmented code, a second stop inside the same code, a
      stop the engine refuses, a purge whose earliest move is a macro's, and a synchronous pause
      following an aborted pause sequence
- [x] `DDARing::Feedhold` in `src/DuetSbcInterface`: pick the stopping point at or after the first
      uncommitted DDA, force its end speed to zero, re-run the backward pass to the last committed
      DDA, free the rest
- [x] Honour the `canPauseAfter` exclusions when choosing the boundary — arcs, retractions, endstop,
      probing and `G1 H` moves, through `DDA::IsRestartableBoundary`
- [x] `DuetSbc_MotionRequestStop` and `DuetSbc_MotionGetFeedholdResult` — a request carrying which
      kind of stop it is, and a seqlock-published result, because freeing a move frees its segments
      and only the motion thread may do that, so the answer cannot come back from the call that asks
- [x] `MovePlanner.StopEarlyAsync`, resyncing from the engine and dropping `SegmentsLeft`
- [x] `M25` from a console or an interface stops by feedhold; from the job file it is synchronous and
      unchanged (§3.5)
- [x] The pause sequence takes the feedhold as a flag **independent of** the run-`pause.g` flag
      (§3.5.1), and falls back to phase 2's drain-the-ring behaviour when nothing could be purged
- [x] `DDARing::PauseMoves` ported and tested as the reference behaviour, though nothing in
      DuetControlServer now asks for it — see §3.5
- [x] MCODE_MIGRATION §11.6's row for `PauseMoves` records that it is ported
- [x] `rrf-differences.md` §8, now that there is shipped behaviour to describe
- [x] Native tests for both stops — the boundary search, the committed-move floor, the indivisible-run
      refusal and the ring being usable afterwards. They caught a design bug on their first run: the
      feedhold was reading `canPauseAfter`, which `RecalculateMove` overwrites with "...and already at
      or below jerk", so it could only stop where RepRapFirmware could. `restartableBoundary` is now a
      flag of its own, kept as DuetControlServer sent it
- [ ] Only ring 0 is stopped; a `// TODO` in `DrainFeedholds` names M596
- [ ] **`M25` with a fraction is silently accepted as `M25`.** RepRapFirmware guards this centrally
      ([GCodes2.cpp:737](../../lib/RepRapFirmware/src/GCodes/GCodes2.cpp)): an M-code carrying a
      fraction that is not in its allow-list goes to `TryMacroFile`, and M25 is not in that list — so
      `M25.1` there looks for `sys/M25.1.g` and otherwise reports the code unsupported. Here it now
      reaches `HandlePausePrintAsync` and pauses. The fix is one line, throwing
      `NotSupportedException` when `MinorNumber >= 0` so the code reaches the same fallback, but
      whether `M25.1` should error or stay an alias is a decision rather than an oversight

### Phase 5 — pausable macros and the deferred pause 🟡

- [x] `ChannelProcessor.CanRestartMacros`, walking the channel's stack as
      `GCodeMachineState::CanRestartMacro` walks its own
- [x] A pause unwinds only the macros, cancelling their codes, and leaves the job file in place -
      `AbandonMacrosForPauseAsync`, deliberately not `AbortAllFilesAsync`, which is right for an
      abort and wrong for a pause
- [x] `_deferredPause` and its injection point in the job loop, with a filament change taking
      priority over an ordinary pause
- [x] `M25` and `M226`/`M600`/`M601` during a non-restartable macro defer instead of acting
- [ ] The tool-change exclusion — RepRapFirmware also waits for `!doingToolChange`, and nothing here
      says a tool change is in progress. `// TODO` at the point of use; it is the same gap
      `MachineStatusService` names for `ChangingTool`

### Phase 6 — the callers that were waiting 🟡

**Depends on phase 4**, not just phase 2: every pause RepRapFirmware makes here becomes a feedhold
(§3.5.1). Landing them on the faithful path first would change their behaviour underneath machines
that had started relying on it. No event gains or loses a pause — only the stop changes.

- [x] EVENTS_MIGRATION phase E's pausing default actions, as feedholds
- [x] `driver_error` pauses as a feedhold **without** `pause.g`; `heater_fault` and `filament_error`
      as feedholds **with** it
- [x] `M37` starts the simulation it selects, and the simulation restore point
- [ ] The message box RepRapFirmware raises alongside an event's pause — `// TODO` at the point of
      use, waiting on M291
- [ ] `trigger<n>.g`, and trigger 1's built-in pause. **Blocked, and further back than it looked**:
      plain `M581` is not implemented at all. Only the expression form `M581.1` exists, so there is
      no pin-trigger system for a trigger number to come from and nothing for `trigger1` to mean.
      That is MCODE_MIGRATION §5.11's gap rather than this one's, and it belongs with the input
      monitors rather than here

### Phase 7 — job progress 🟡

The `PrintMonitor` port, as `JobMonitor`. Its own phase because nothing above depends on it.

- [x] `job.duration`, `warmUpDuration`, `pauseDuration`, `lastFileName`, `lastDuration`,
      `lastWarmUpDuration`
- [x] `job.timesLeft` — the file, filament and slicer estimates
- [x] `job.filePosition`
- [x] `M73`
- [x] `M27` reports "Not SD printing." when a file is selected but not printing
- [x] `HeatManager.IsWaitingForTemperatures`, which is what separates warm-up from printing and
      which nothing tracked
- [ ] `job.layer`, `job.layerTime`, `job.layers[]`. `UpdateService.UpdateLayers` is the existing
      code, but it works from `job.layer` having already been set — in the split architecture
      RepRapFirmware decided when a layer changed and DSF only recorded the statistics. Nothing
      decides it now, so the layer-change detection has to be ported before that code can be lifted
      out of its `#if false`
- [ ] `job.rawExtrusion`, which the filament estimate reads and which stays null until the extrusion
      totals exist — MCODE_MIGRATION §15.2. The estimate is written to return nothing rather than a
      wrong answer while it does

The whole of it turns on separating the time a job spent printing from the time it spent waiting: a
job paused for ten minutes has not printed for ten minutes, and an estimate that counted them would
report the job as having slowed down. Warm-up and pause time are accumulated apart and taken back
off, which is what `PrintMonitor::Spin` does and why it has the flags it has.

---

## 5. Explicitly not in scope

- **Power-fail resume.** `SaveResumeInfo`, `resurrect.g`, `resurrect-prologue.g`, M911 and M916.
  RepRapFirmware writes `resurrect.g` on every pause, which makes it look like part of this work, but
  it depends on `M916`, on the low-voltage monitor, and on a main-board power-fail path that this
  architecture does not have — `main_board_power_fail` is marked "never raised in RRF either" in
  EVENTS_MIGRATION §6. The `SaveResumeInfo` call in the pause path is a `// TODO` naming this, not an
  omission.
- **`DoEmergencyPause` / `LowVoltagePause` / `LowVoltageResume`** — same dependency, and
  `LowPowerOrStallPause` with them.
- **M486**, **M597**, **M599** — MCODE_MIGRATION §11.4 item 34.
- **The laser**: `laserPixelData.Clear()` on pause and `SetLaserPwm(0)` on abort are one line each
  and go in when the laser does.

---

## 6. Decisions taken

1. **The resume moves the head back in two moves.** `resuming1` and `resuming2` are kept as two, so
   Z is restored last coming down and first going up. Only the single-motion-system branch is
   ported, with a `// TODO` naming M596; §3.3.
2. **`PauseState` lives on `JobProcessor`**, as RRF has it on `GCodes` and the restore points on
   `MovementState`; §3.1.
3. **Feedhold variant (a)** — stop at the first DDA boundary with enough deceleration distance, no
   truncation. A DDA is a segment, so that boundary is usually inside a G-code and the resume takes
   the already-made fraction of the line off it. (b) stays available later; §3.5.
4. **The feedhold runs `pause.g`** and keeps the pause reason it would have had. The feedhold and the
   run-`pause.g` flag are independent, which is what lets `driver_error` have one without the other;
   §3.5, §3.5.1.
5. **Every asynchronous pause feedholds**, `M25` included. It was briefly a separate code, `M25.1`,
   with `M25` kept faithful; that was withdrawn, so the deviation is a change to `M25` rather than an
   addition beside it and an operator sees the machine stop in a different place. A synchronous pause
   is untouched, because there is nothing queued past its stopping point to purge; §3.5.
6. **Where RepRapFirmware pauses for an event, that pause is a feedhold** — the default action of a
   heater fault, filament error or driver error, and by extension trigger 1. Which events pause and
   which run `pause.g` is unchanged; only the manner of stopping differs. Those are the deviation's
   only changes to existing behaviour, and they make phase 6 depend on phase 4; §3.5.1.
7. **The resume point is one record, taken once.** The file position, the fraction of the code
   already made, the modal G command and the feed rate all come from one record of the interrupted
   code, which the pause takes under the planner lock before the read-ahead is cancelled. Nothing is
   left in a shared slot for a later pause to find, and a fraction that names no file position cannot
   be expressed; §3.5.

Nothing is open. The questions this document opened are answered above, and what is left is the work
in §4.
