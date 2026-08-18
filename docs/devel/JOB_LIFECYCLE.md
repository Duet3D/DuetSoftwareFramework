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
stand-in.

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
capture, five macros to call, and one native entry point to write.

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
| M25 from elsewhere | `DoAsynchronousPause(user, pausing1)` | GCodes2.cpp:1312 |
| M25 while a non-restartable macro runs | deferred — §2.8 | GCodes2.cpp:1298 |
| M226 | `DoSynchronousPause(gcode, pausing1)`; `M226 P0` → `pausing2`, skipping `pause.g` | GCodes2.cpp:1269 |
| M600 | `DoSynchronousPause(filamentChange, filamentChangePause1)` — runs `filament-change.g`, falling back to `pause.g` | GCodes4.cpp:571 |
| M601 | as M226 | |
| A trigger firing | `DoAsynchronousPause(trigger, pausing1)` | GCodes.cpp:954 |
| A heater fault, filament error or driver error | `DoAsynchronousPause(...)` from the event handler | GCodes4.cpp:1981 |
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
starts and phase 4 finishes.

Moves already handed to an expansion board are a separate question, and the answer is the same as
RRF's: they are not recalled. RRF skips moves in its own ring only, and so should this.

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

### 3.4 Which channel each macro runs on

`pause.g`, `resume.g`, `cancel.g` and `stop.g` run on the channel that commanded the operation in
RRF, except when an event caused it, in which case they run on `Autopause`.
`EventProcessor` already runs its macros on `CodeChannel.Autopause`, so the pause sequence takes the
channel as a parameter and the event path passes `Autopause`. `start.g` runs on `File`, because RRF
is explicit about why: "so that any M82/M83 codes will be executed in the correct context"
(GCodes.cpp:3846).

---

## 4. The plan

### Phase 0 — delete the two dead awaits ⬜

`SetPrintFileInfo` and `StopPrintAsync` and their `TaskCompletionSource`s go, along with
`InvalidateCodes`' handling of them. `SelectFileAsync` ends after logging; `ExecuteAsync` keeps the
three log lines and the `PrintStoppedReason` it picks, which phase 3 turns into the argument to
`StopPrint`.

Nothing else in this document can be tested until this lands: today a job can neither be selected nor
finish.

- [ ] Remove `SetPrintInfoRequest`, `StopPrintRequest`, `StopPrintReason`, `PrintStateLock` and the
      two methods
- [ ] `SelectFileAsync` no longer awaits the firmware
- [ ] `ExecuteAsync` reaches its teardown
- [ ] Correct the stale comment "Prints are cancelled by M0/M1/M2 which is processed by RRF"

### Phase 1 — the state and the restore point ⬜

- [ ] `PauseState` on `JobProcessor`, in RRF's order
- [ ] `MachineStatusService` gains Pausing, Resuming and Cancelling, and its `// TODO` goes
- [ ] `RestorePoint[]` on `MovementState`, `SavePosition`, and the projection into
      `state.restorePoints[]` and `move.motionSystems[].restorePoints[]`
- [ ] G60

### Phase 2 — pause and resume, without skipping queued moves ⬜

The `movesSkipped == false` branch of `DoAsynchronousPause`: flush to standstill, save the restore
point from where the machine actually stopped, take the file position from the job file.

- [ ] `JobProcessor.PauseSequenceAsync(channel, reason, runPauseMacro)` — flush, save the restore
      point, cancel the file channel's in-flight codes, set `Pausing`, run `pause.g` (or
      `filament-change.g` falling back to it), settle to `Paused` in a `finally`
- [ ] `M25` — from a file, from elsewhere, "Printing is already paused!", "Cannot pause print,
      because no file is being printed!"
- [ ] `M226`, `M600`, `M601`, including `M226 P0` skipping `pause.g` and the "use M226/600/601 only
      within a file being printed" refusal
- [ ] A synchronous pause supplies no file position (§2.9)
- [ ] `M24` — refuse while `Pausing` or `Resuming`; `resuming1`/`2`/`3` equivalent moving the head
      back with Z last and restoring the feed rate; `M24 P0` skips `resume.g`; `pause.g` and
      `resume.g` only when all axes are homed
- [ ] `M24` on a selected-but-not-started file runs `start.g`

### Phase 3 — stopping ⬜

- [ ] `JobProcessor.StopPrint(reason)` carrying `PrintStoppedReason` properly, replacing
      `IsCancelled = IsPaused`
- [ ] `M0/M1/M2` from inside the file → normal completion → `stop.g`, or all heaters off if absent
- [ ] `M0/M1/M2` while paused → user cancelled → `cancel.g`, *or* `stop.g` and the heater switch-off
      if there is no `cancel.g`
- [ ] Abort → heaters off, spindles stopped
- [ ] The G10 Z hop unwind, the temperature-wait cancellation and the job file's local variables
- [ ] `Cancelling` is observable while `cancel.g` runs

### Phase 4 — pausing part-way through the queue ⬜

- [ ] `DDARing::PauseMoves` ported into `src/DuetSbcInterface`, plus `DuetSbc_MotionPauseMoves`
- [ ] `MovePlanner.PauseMovesAsync` returning the skipped-move restore data, or nothing if no move
      could be skipped
- [ ] The pause sequence uses it, falling back to phase 2's behaviour when it returns nothing
- [ ] MCODE_MIGRATION §11.6's row for `PauseMoves` updated

### Phase 5 — pausable macros and the deferred pause ⬜

- [ ] A `CanRestartMacro` equivalent walking the channel's stack
- [ ] A pause unwinds only the pausable macros, cancelling their buffered and suspended codes
- [ ] `deferredPauseCommandPending` and its injection point, including the tool-change exclusion
- [ ] `M25` during a non-restartable macro defers instead of refusing

### Phase 6 — the callers that were waiting ⬜

- [ ] EVENTS_MIGRATION phase E's pausing default actions (still needs M291 for the message box)
- [ ] `trigger<n>.g` and the pause a trigger can request
- [ ] `M37` starts the simulation it selects, and the simulation restore point

### Phase 7 — job progress ⬜

The `PrintMonitor` port. Its own phase because nothing above depends on it.

- [ ] `job.duration`, `warmUpDuration`, `pauseDuration`, `lastFileName`, `lastDuration`
- [ ] `job.layer`, `job.layerTime`, `job.layers[]` — `UpdateService.UpdateLayers` is the existing
      code and can be lifted out of the `#if false`
- [ ] `job.timesLeft` — the file, filament and slicer estimates
- [ ] `job.filePosition`, `job.rawExtrusion`
- [ ] `M73`
- [ ] `M27` reports "Not SD printing." when a file is selected but not printing

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

## 6. Open questions

1. **Should the resume move go through `resuming1`/`resuming2` as two moves?** RRF splits it so that
   Z is restored last when moving down and first when moving up, so the nozzle does not drag. The
   split is the behaviour, not an artefact of the state machine, so the port keeps it — but it
   needs the axis allocation that `SUPPORT_ASYNC_MOVES` uses, and only the single-motion-system
   branch is required until M596 lands. Porting only the `#else` branch is a departure worth
   confirming.
2. **Where does `PauseState` live** — on `JobProcessor`, or on `MovementState` next to the restore
   points? RRF has it on `GCodes` and the restore points on `MovementState`, which argues for
   `JobProcessor`. That is what §3.1 assumes.
