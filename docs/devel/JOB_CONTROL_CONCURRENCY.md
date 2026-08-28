# Job control concurrency: how pause, resume and stop run today, and the plan to make them robust

[JOB_LIFECYCLE.md](JOB_LIFECYCLE.md) records what the job lifecycle has to do and the order it was
ported in. This document is about how the code that does it is *scheduled*: which threads and tasks
take part, what state they share, where the windows between them are, and why the same loop has
needed one race fix after another ([KNOWN_BUGS.md](KNOWN_BUGS.md) lists eight already fixed and
the open ones below, and the stepped pause sweep in `SystemTests` still fails). Section 5 is the
catalogue of the races that remain, each with the interleaving that produces it. Section 7 is the
replacement: a job actor written from scratch in place of `JobProcessor`, with one owner for the
state, one message to the file reader, one rule for the resume point, and the flags that cover
ordering windows deleted along with the windows.

The code under discussion is [JobProcessor.cs](../../src/DuetControlServer/Files/JobProcessor.cs),
[JobProcessor.Lifecycle.cs](../../src/DuetControlServer/Files/JobProcessor.Lifecycle.cs),
[MovePlanner.cs](../../src/DuetControlServer/Motion/MovePlanner.cs) (`StopEarlyAsync`,
`TakeJobResumePoint`), [JobMoveIndex.cs](../../src/DuetControlServer/Motion/JobMoveIndex.cs),
`SubmitMoveAsync` in [GCodeHandler.cs](../../src/DuetControlServer/Codes/Handlers/GCodeHandler.cs),
the M0/M23/M24/M25/M226 handlers in
[MCodeHandler.cs](../../src/DuetControlServer/Codes/Handlers/MCodeHandler.cs), and the deferred-code
list in [PipelineBase.cs](../../src/DuetControlServer/Codes/Pipelines/PipelineBase.cs).

---

## 1. The actors

Everything below runs concurrently with everything else in the table. There is no thread that owns
the job; the job's state is a set of fields on `JobProcessor` that each actor reads and writes in
its own lock windows.

| Actor | What it is | Started by | Lives for | Touches the job through |
|---|---|---|---|---|
| `JobProcessor.ExecuteAsync` | Hosted-service task | Host startup | The process | Waits on `_resume` for a job to start, starts the file tasks, awaits them, runs the end-of-job stop, tears the job down |
| `DoFilePrint(File)` | Task, awaited only by `ExecuteAsync` | `ExecuteAsync` | One job run, across every pause of it | Reads the file ahead, starts each code on the `File` channel, tracks completion, parks and rewinds on a pause, runs the deferred-pause check |
| `DoFilePrint(File2)` | The same for a forked job | `ExecuteAsync` or `StartSecondJob` (`M606 S1`) | The fork | As above on `File2`; shares `_pausePending` with the first |
| Pipeline stage tasks | One task per stage (`Start`, `Pre`, `ProcessInternally`, `Post`, `Executed`) per stack level per channel, `Task.Factory.StartNew` in `PipelineStackItem` | `Push` | Until `Pop` | `ProcessInternally` runs every handler: `SubmitMoveAsync` for the job's moves, and the M0/M24/M25/M226 handlers that call into `JobProcessor` |
| Macro tasks | `Task.Run(MacroFile.RunAsync)` | `MacroRunner`, `M98`, tool changes, `pause.g`/`resume.g`/`stop.g`/`cancel.g` | Until the macro ends or is aborted | Reads the macro and starts its codes on the invoking channel, one stack level up |
| Deferred code tasks | `RunDeferredCodeAsync`, unawaited, one per Deferred-class code (`M106`, `M3`, ...) | The `File` channel's `ProcessInternally` stage | Until the anchor move retires and the handler runs | Own cancellation source, detached from the channel's; counted by the standstill wait, not by flushes |
| The pause, resume and stop sequences | Not tasks of their own: `PauseAsync`, `ResumeAsync` and `StopAsync` run inline on whichever task called them | The `ProcessInternally` task of the channel that issued `M25`/`M24`/`M0` (`HTTP` from DWC, `File` for a synchronous pause), `EventProcessor` for an event, `DoFilePrint` for a deferred pause, `ExecuteAsync` for the end-of-job stop | The call | Every field on `JobProcessor`, the planner, the code processor, the macro runner |
| `EventProcessor` | Hosted-service task | Host startup | The process | `PauseAsync` on `Autopause` for a heater fault, filament error or driver error |
| `MotionService` managed thread | `Thread`, highest priority | `MotionService.ExecuteAsync` | The process | Publishes `machinePosition` every 50 ms; nothing else |
| Native motion thread | `std::thread` in `libDuetSbcInterface` | `StartMotion` | The process | `SpinOnce` every 1 ms: `DrainFeedholds`, `DrainForcedPositions`, `DrainSubmissions`, then each ring's `Spin`. Acts on a stop request and publishes the result through a seqlock |
| `LinkService` dispatcher thread | `Thread` | `LinkService.ExecuteAsync` | The process | Turns `MoveCompleted` records into `MotionTracker.MoveCompleted`, which wakes the deferred codes waiting on that move |
| `MachineStatusService` | Hosted-service task | Host startup | The process | Every 250 ms derives `state.status` from `PauseState`, `IsProcessing` and `IsMoving`, without the job lock |
| `JobMonitor` | Hosted-service task | Host startup | The process | Every 200 ms reads `IsProcessing`, `PauseState`, `IsSimulating` under the job lock, then the file position under the object model write lock |
| IPC connection tasks | `Task.Run` per connection | `IPC.Server` | The connection | Where DWC's `M25` and `M24` enter, as codes on the `HTTP` channel |

Two consequences of the table drive everything in §5:

- **The sequences run on borrowed tasks.** `PauseAsync` for an `M25` from DWC runs on the `HTTP`
  channel's `ProcessInternally` task, under that code's cancellation token, while the `File`
  channel's stage tasks and `DoFilePrint` carry on. The pause and the thing being paused are peers;
  neither can assume the other is at a known point.
- **The file reader parks itself.** `DoFilePrint` decides when the job has stopped reading, where
  to rewind to and when to carry on, from fields the sequences set in separate lock windows. The
  reader infers the sequence's progress; the sequence never tells it.

---

## 2. The shared state and its locks

| State | Lock | Written by | Read by |
|---|---|---|---|
| `PauseState` | `JobProcessor._lock` | `PauseAsync` (`Pausing`, then `Paused` in its `finally`), `ResumeAsync` (`Resuming`, `NotPaused`), `StopAsync` (`Cancelling`, `NotPaused`), `Resume()`, `Cancel()`, `Abort()`, `SelectFileAsync`, the teardown in `ExecuteAsync` | `DoFilePrint` under the lock; `MachineStatusService`, `EventProcessor`'s pre-check and the M-code handlers partly without it |
| `IsProcessing` | `_lock` | `ExecuteAsync` at start and teardown, `DoFilePrint` when it parks and unparks | `PauseAsync`, `ResumeAsync`, `CheckForDeferredPauseAsync`, `StartSecondJob`, `MachineStatusService` (no lock), M23/M27/M37 handlers, `EventProcessor`, `JobMonitor` |
| `IsCancelled`, `IsAborted` | `_lock` | `Cancel()` (`IsCancelled = IsPaused`), `Abort()`, `SelectFileAsync` | `DoFilePrint`, `ExecuteAsync` |
| `_pausePending`, `_pausePosition`, `_pausePosition2`, `_pauseReason` | `_lock` | `StopReadingForPause`, `DoFilePrint` (clears the flag when it parks), `SelectFileAsync` | `DoFilePrint` |
| `_cancellationTokenSource` | `_lock` | Replaced, and the old one cancelled and disposed, by `StopReadingForPause`, `Cancel()` and `Abort()` | `DoFilePrint` captures the token at six points and passes it to every code it starts |
| `_stopped` | `_lock` | `StopAsync`, `SelectFileAsync` | `StopAsync`, `ExecuteAsync` |
| `_deferredPause` | `_lock` | `TryDeferPause`, `CheckForDeferredPauseAsync`, `SelectFileAsync` | `CheckForDeferredPauseAsync`, `IsPauseDeferred` |
| `_pausedInMacro` | None | `PauseAsync` | `ResumeAsync` |
| `_resume`, `_finished` | `AsyncConditionVariable`s on `_lock`. A notify wakes only the tasks already waiting; one given before the wait is lost | `_resume`: `Resume()`, `Cancel()`, `Abort()`, `ResumeAsync`'s `finally`. `_finished`: the teardown | `ExecuteAsync` (a job to start), `DoFilePrint` (a pause to end), `SelectFileAsync` (the previous job to end) |
| `CodeFile.Position`, `NextFilePosition`, `ModalGCommand`, `FirstCommandAfterRestart` | The file's own `AsyncLock` | `ReadCodeAsync`, `SetFilePositionAsync`, `RestoreModalStateForResume`, `ApplyRestartStateAsync`, `ResumeAsync` | `ReadCodeAsync`, `GetFilePositionAsync`, `JobMonitor` |
| `CodeFile.IsClosed` | Volatile, no lock | `Cancel()`, `Abort()` | `ReadCodeAsync`, `ExecuteAsync` |
| `MovementState.CurrentJobMove`, `SegmentsLeft`, `MoveFractionToSkip`, `PurgeGeneration`, `RestorePoints[1]`, `RestartMoveFractionDone`, `RestartGCommandNumber` | `MovePlanner.Lock()` | `SubmitMoveAsync`, `StopEarlyAsync`, `TakeJobResumePoint`, `SaveRestorePointAsync`, `RestoreModalStateForResume`, `ApplyRestartStateAsync`, M26 | `SubmitMoveAsync`, `MoveInterpreter`, the resume |
| `MovePlanner.JobMoves` | `MovePlanner.Lock()` | `SubmitMoveAsync` (`Note` per segment), `TakeJobResumePoint` (`Clear`) | `TakeJobResumePoint` |
| The channel's stack, its job file and `_deferredCodes` | `lock (_stack)`, `lock (_deferredCodes)` | `Push`/`Pop`, `SetJobFile`, `DeferCode`, `Cancel...DeferredCodes` | Flushes, the standstill wait, `IsDoingMacro`, `CanRestartMacros` |
| `MotionTracker` waiters and completion counts | Its own `lock` | The `LinkService` dispatcher | Deferred codes, `ShouldDefer`, `IsMoving` |
| Native: the feedhold request queue, the feedhold result, the submission queue | Lock-free ring buffers and a seqlock | `RequestStop` from DCS; `DrainFeedholds` on the motion thread | `TryGetFeedholdResult` polled by `StopEarlyAsync` |

Lock order is not defined anywhere. The orders taken today, each by at least one path:

- `_lock` → object model write → planner (`SelectFileAsync`, `SaveRestorePointAsync` in reverse)
- `_lock` → file lock (`SetFilePositionAsync`, `GetFilePositionAsync` from M26/M27, `ResumeAsync`)
- file lock → object model read (`ReadCodeAsync`)
- file lock → flush → any handler → `_lock` (`ReadCodeAsync` at the end of a block with local
  variables, waiting for codes that may be an `M226`)
- object model write → file lock (`JobMonitor.PublishAsync`)
- object model → planner (everywhere; this pair is consistent)

`file → model` and `model → file` both exist, which is a deadlock waiting for the right timing
(§5, R9).

---

## 3. The sequences as they run

### 3.1 Starting a job

`M32` or `M23`+`M24` on any channel. The handler holds `_lock` around `SelectFileAsync`, which
cancels and waits out any previous job, then `ResumeAsync` runs `start.g` on the `File` channel,
applies the M26 restart state, and calls `Resume()`, which sets `NotPaused` and notifies `_resume`.
`ExecuteAsync` wakes, sets `IsProcessing`, and starts `DoFilePrint`.

### 3.2 The reader

```mermaid
flowchart TD
    A[Take job token under _lock] --> B{Pool has a free Code?}
    B -- yes --> C{PauseState >= Pausing or IsAborted?<br/>under _lock}
    C -- yes --> H
    C -- no --> D[file.ReadCodeAsync<br/>holds the file lock]
    D -- null: EOF or closed --> H
    D -- code --> E[code.ExecuteAsync with the job token<br/>starts it on the File pipeline]
    E -- OperationCanceledException and a pause or cancel is in progress --> F[Re-read the job token]
    E -- other exception --> G[Log, Abort]
    E --> B
    F --> B
    G --> B
    H{Oldest started code?}
    H -- yes --> I[await code.Task<br/>currentFilePosition = end of it]
    I --> J[CheckForDeferredPauseAsync<br/>may run a whole PauseAsync here]
    J --> B
    H -- none --> K[PurgeSyncRequestsFor: also SetJobFile null<br/>FlushAsync file]
    K --> L{Job stopping?}
    L -- no --> M[WaitForStandstillAsync with the job token]
    L -- yes --> N
    M --> N{PauseState >= Pausing or _pausePending<br/>under _lock}
    N -- no --> Z[break: the job is over]
    N -- yes --> O[Rewind to _pausePosition ?? currentFilePosition<br/>IsProcessing = false]
    O --> P{PauseState != NotPaused?}
    P -- yes --> Q[await _resume]
    P -- no --> R
    Q --> R{Cancelled or aborted?}
    R -- yes --> Z
    R -- no --> S[Take the live token, IsProcessing = true<br/>SetJobFile, RestoreModalStateForResume]
    S --> B
```

The loop reads up to `BufferedPrintCodes` (32) codes ahead of the oldest one still running. A
movement code completes when its move has been *submitted*, so `currentFilePosition` runs ahead of
the machine by the whole queue; for a short file it reaches the end of the file while the machine
is still on the first line.

### 3.3 An asynchronous pause

`M25` from DWC, or an event. The whole of this runs on the caller's task.

```mermaid
sequenceDiagram
    participant H as HTTP ProcessInternally task
    participant P as JobProcessor
    participant M as MovePlanner / native motion thread
    participant S as File ProcessInternally task<br/>(SubmitMoveAsync)
    participant R as DoFilePrint task
    H->>P: PauseAsync
    P->>P: lock: PauseState = Pausing
    Note over R: next read-ahead check sees Pausing and stops reading
    P->>M: StopEarlyAsync: NotePurge, RequestStop
    Note over S: sees PurgeGeneration changed, throws OperationCanceledException
    Note over R: the cancelled code completes, EOF, parks with _pausePosition = null
    M-->>P: feedhold result (polled every 2 ms)
    P->>M: resync the interpreter, SegmentsLeft = 0
    P->>P: CancelDeferredCodesAfter(lastSurviving + 1)
    P->>P: lock: TakeJobResumePoint, StopReadingForPause<br/>(new token, _pausePosition, _pausePending = true)
    P->>P: AbandonMacrosForPauseAsync (caller's token)
    P->>P: FlushAsync(File, all), WaitForStandstillAsync (shutdown token)
    P->>P: SaveRestorePointAsync, pause.g
    P->>P: finally: lock: Pausing -> Paused
```

The two notes on `R` are the race in §5 R1: the reader can park before the fourth step has told it
where to rewind to.

### 3.4 A synchronous pause

`M25`, `M226`, `M600` or `M601` read from the job file. The handler flushes the stages ahead of the
code, then `PauseAsync(synchronous: true, pausingCode: code)` runs on the `File` channel's own
`ProcessInternally` task, under the job token. `StopEarlyAsync` is skipped. `StopReadingForPause`
cancels the job token, which is the token this very sequence was called with; the code's own
property is re-armed, but the sequence's local `cancellationToken` is only replaced *after*
`AbandonMacrosForPauseAsync` has been awaited with the cancelled one (§5, R4).

A deferred pause (`M25` while a non-restartable macro runs) is the same call made from
`DoFilePrint` after each job code completes, on the reader's task and token.

### 3.5 Resume

`M24` on any channel. `ResumeAsync` sets `Resuming` under the lock, then, without it, runs
`resume.g` on the caller's channel, queues the two restore moves and waits for standstill, restores
the channel's feed rate and distance modes, and in its `finally` sets `NotPaused` and notifies
`_resume` if the reader has parked (`!IsProcessing`). If the reader has not parked yet, the notify
is not given and the reader relies on finding `NotPaused` when it gets there. The reader then
rewinds (again) to `_pausePosition`, re-arms, and puts back the modal G command and the fraction
to skip from the restore point.

### 3.6 Stop and cancel

- **`M0` from the file**: the handler calls `Cancel()` (closes the file, replaces the token,
  `IsCancelled = IsPaused` which is false) and `StopAsync(NormalCompletion)`, which runs `stop.g`.
  The reader reaches EOF, waits for standstill, and exits; `ExecuteAsync` sees `_stopped` and skips
  the stop.
- **`M0` while paused**: `Cancel()` sets `IsCancelled` and notifies `_resume`; the parked reader
  wakes and exits; `StopAsync(UserCancelled)` sets `Cancelling` and runs `cancel.g`. Meanwhile
  `ExecuteAsync`'s teardown runs to completion and resets `PauseState` to `NotPaused`, `_file` to
  null, and notifies `_finished`, while `cancel.g` may still be running (§5, R7).
- **End of file**: the reader waits for standstill, exits; `ExecuteAsync` runs
  `StopAsync(NormalCompletion)` and tears down.
- **Abort**: `Abort()` from a code error, `AbortAllFilesAsync`, or `LinkService` shutting down.

---

## 4. What each mechanism is for, and the window it leaves

| Mechanism | Introduced to close | Window still open |
|---|---|---|
| `PauseState` ordering (`>= Pausing` stops the reader) | A second `M25` starting a second sequence | The reader stops on `Pausing`, which is set before the pause knows where to rewind to |
| `_pausePending` | A resume landing before the reader parked, which put the state back to `NotPaused` and made the reader read EOF as the job ending | Set in the same late lock window as the position, so it does not gate the park either; a second park after the resume then rewinds a second time (R1) |
| Re-reading the job token in six places | Codes read after a resume being cancelled by the token the pause had cancelled | Each re-read is a separate lock window; a cancel between two of them still hands the next code a dead token |
| `PurgeGeneration` | A segmented submission continuing to feed a ring that had just been emptied | It aborts the submission *before* the pause has recorded anything, which is what parks the reader early |
| `CurrentJobMove` taken by reference identity | A pause adopting a record left by a submission that ended on its own | A record released the moment the last segment is submitted, so a stop between full submission and execution has no record (R2) |
| `JobMoveIndex` keyed on move id | Mapping a purged move back to its line | Nothing purged but the submission queue discarded: no index lookup is made (R2) |
| `CancelDeferredCodesAfter(lastSurviving + 1)` | Deferred codes anchored to purged moves holding the standstill wait for ever | Runs before the read-ahead is cancelled, so a deferred code created in between anchors to a move that will never retire (R3) |
| `catch (OperationCanceledException)` around the deferred-pause check | A pause cancelling the token the check was made with and taking the reader down | The check itself calls `PauseAsync` with that token (R4) |
| `_stopped` | `stop.g` running twice for a file ending in `M0` | Never cleared after a job ends, so `M0` with no job runs nothing (R5) |
| `ResumeAsync`'s conditional `_resume.NotifyAll()` | Waking a reader that had not parked | The parked reader reports `IsProcessing = false` until it re-acquires the lock after the notify, and in that window the job can be selected over and cancelled (R16) |
| `_stopped` and `IsCancelled = IsPaused` | `stop.g` running twice; telling a cancel from a normal end | Neither is a state of the job: one is never cleared (R5), the other is a snapshot of `IsPaused` at an instant that `Cancel()`'s other callers do not control (R11, R14) |

---

## 5. The race catalogue

Each entry is an interleaving that the code admits today. "Evidence" names the scenario that shows
it or the log that was read. Every one is reproducible in the `SystemTests` bench once the stepped
timeline covers it; the plan in §7 starts by writing the missing ones.

### R1. The reader parks before the pause has published its rewind point

**Confirmed.** `SteppedPauseTests.AnAbsoluteJobEndsAtItsLastTargetFromEveryPausePoint` and the
relative sweep fail at 6 and 3 of 22 points respectively on this tree, with travel of 445 to
800 mm for a 400 mm job.

Interleaving, from the bench log of the absolute job paused at 120 mm:

1. `PauseAsync` sets `Pausing` and calls `StopEarlyAsync`, which raises `PurgeGeneration` before
   the stop request has even been acted on.
2. `SubmitMoveAsync`, part-way through the file's last line, sees the generation change and throws
   (`Abandoning 91 remaining segment(s) of G1 X400`). The code completes as cancelled.
3. `DoFilePrint` awaits that completion, finds `Pausing`, stops reading, reaches the end of the
   file, and parks: `Job on File has been paused at byte 44 (no fpos from firmware), reason 0`.
   `_pausePosition` is still null, so it rewinds to `currentFilePosition`, the end of the last
   *completed* code, which is the line before the interrupted one.
4. Only then does `PauseAsync` reach its second lock window: `Stopped the machine early, dropping
   23 queued move(s)`, `TakeJobResumePoint` names byte 28, `StopReadingForPause` stores it and
   sets `_pausePending`.
5. `M24`: the reader continues from byte 44 with the fraction and modal state of the line at
   byte 28. At the end of the file it finds `_pausePending` still set, rewinds to byte 28 (`Job on
   File has been paused at byte 28, reason User`), does not wait because the state is `NotPaused`,
   and runs the last three lines again: 800 mm travelled.

Whether step 3 or step 4 comes first depends on how long the motion thread takes to answer the
stop, which is why the same point passes on one run and fails on the next. The reader's park
condition is `PauseState >= Pausing || _pausePending`; the first half is true before the pause
knows anything, and the flag that was added to survive the *reverse* ordering does not gate this
one.

### R2. The resume point is chosen from what was purged, not from what survives

**Confirmed by reading.** `DDARing::Feedhold` may pick the last provisional DDA as the stopping
point, so `PurgeAfter` reports `stopped` with `movesPurged == 0`, and `DrainFeedholds` then
discards every move DuetControlServer had submitted for that ring but the motion thread had not
taken up (`DiscardSubmissionsFor(0)` runs whenever `stopped`, against the comment above it). Those
moves are counted nowhere. `TakeJobResumePoint` branches on `MovesPurged` and never reads
`LastSurvivingMoveId`, the one number `FeedholdOutcome`'s own comment says is the boundary, so:

- with the interrupted code's record already released (`SubmitMoveAsync`'s `finally` clears
  `CurrentJobMove` once every segment is *submitted*), it returns null, the reader rewinds to
  `currentFilePosition`, the end of the file for a short job, and the discarded segments are never
  made: the job ends short. The uncommitted pause points 365 and 380 in `SteppedPauseTests` aim at
  this window; the run above found nothing to purge at 380 and ended correctly, which says the
  window is narrow, not closed;
- with the record still live, `SegmentsQueued` counts the discarded segments and the fraction
  over-reports.

The same branch structure fails from the other side when the earliest purged move belongs to a
macro (tool-change or `M98` moves are never noted in `JobMoves`): the lookup misses, the result is
null, `JobMoves.Clear()` discards the entries of the job moves purged behind it, and the reader
falls back to `currentFilePosition`. That is *past* the macro invocation, because `M98` completes
when its macro has dispatched its codes and every job `G1` after it completes when submitted, so
the purged job lines are skipped on resume rather than re-read. The comment in
`TakeJobResumePoint` that the fallback "is the last job code that completed, which is the macro
invocation" holds only when no job code completed after the macro was invoked.

### R3. Read-ahead codes dispatched between the stop and the read-ahead cancel

**Confirmed by reading.** `StopEarlyAsync` returns, `CancelDeferredCodesAfter` runs, and only in the next lock
window does `StopReadingForPause` cancel the job token. In between, the `File` channel's
`ProcessInternally` task is free (the interrupted `SubmitMoveAsync` has just thrown) and dispatches
the next read-ahead code:

- a `G1` builds from the resynced position and `QueueMove` accepts it, so the machine sets off again
  after the stop, and `WaitForStandstillAsync` waits for it; the restore point is then where that
  move ended, and the resume replays it;
- an `M106` defers with an anchor that the purge, or the discard of the submission queue, has made
  unretirable, under its own cancellation source that `CancelDeferredCodesAfter` has already run
  past. `MovePlanner._lastSubmittedMoveId` is never wound back after a purge and
  `MotionTracker.HasRetired` compares against the last *completed* id, so a purged id reads as in
  flight until some later, higher id retires; nothing posts `MoveFailed` for purged or discarded
  moves. `AbandonMacrosForPauseAsync` waits on `LastDeferredCodeTask()` before the standstill wait
  is even reached, so `M25` never returns and the state stays `Pausing`, from which `M24` is
  ignored and `M0` is refused. This is the hang recorded in `KNOWN_BUGS.md` ("A pause could wait
  for ever on codes the stop had orphaned"), reopened through a narrower window.

Nothing in the window stops the `File` pipeline: `DoFilePrint`'s `Pausing` test only stops *new*
reads, `PipelineStackItem` skips a code only if its token is already cancelled, and no handler
consults the job state. The usual trigger is a `G1` sleeping in its ring-full retry that wakes when
the purge frees the ring.

### R4. A synchronous or deferred pause runs its macro unwind under the token it has just cancelled

**Confirmed by reading.** `PauseAsync` is called with the code's token, which for `M226`, `M600`,
`M601` and `M25` from the job file is the job token (`Code.ProcessInternally` passes
`CancellationToken`, and `DoFilePrint` set that to the job token). `StopReadingForPause` cancels
it. `pausingCode.ResetCancellationToken()` re-arms the *code's* property; the sequence's local
`cancellationToken` is only replaced at the line after `AbandonMacrosForPauseAsync` has been
awaited with the cancelled one. `AbandonMacrosForPauseAsync` awaits `LastDeferredCodeTask()` with
that token, so a job file containing `M106 ...` followed by `M226` while moves are queued throws
`OperationCanceledException` at once: the `finally` settles `Paused`, but no flush, no standstill
wait, no restore point, no `pause.g`, no "Printing paused at" message, and `_pausedInMacro` is not
set. `M24` then travels to the previous pause's restore point. The deferred pause
(`CheckForDeferredPauseAsync`, reader's token, `pausingCode: null`) fails the same way and the
exception is swallowed at the call site.

### R5. `_stopped` is never cleared, so `M0` with no job runs nothing after any completed job

**Confirmed by reading.** `StopAsync` returns at once if `_stopped`, and sets
`_stopped = IsFileSelected` otherwise. The end-of-job stop runs while the file is still selected,
so `_stopped` becomes true, and only `SelectFileAsync` clears it. Every `M0` from a console
between two jobs, which the comment above the guard says must run `stop.g` every time, returns
without running it.

### R6. `M32` or `M23` from inside the job file deadlocks the job

**Confirmed by reading.** `HandleSelectFileAsync` allows a file-channel `M32` and calls `SelectFileAsync`,
which sees a selected file, calls `Cancel()` and awaits `_finished`. `_finished` is notified by
`ExecuteAsync` after `fileTask` completes; `fileTask` is `DoFilePrint`, which is awaiting
`code.Task` of the `M32` code; that completes when the handler returns from `SelectFileAsync`. The
`File` channel's `ProcessInternally` task is held for ever, and every later `M23`, `M32` and `M0`
from any channel blocks on the job lock or the same wait. The wait is on `ApplicationStopping`, so
cancelling the job token does not release it. An `M32` inside `stop.g` run by the end-of-job stop
hangs the same way, because that stop is awaited before `_finished` is notified. No scenario
issues `M32` from a file channel.

### R7. `M2` while paused leaves the reader parked and the job never torn down

**Confirmed by reading.** `HandleStopAsync` calls `Cancel()` (`NotPaused`, notify) and then
`StopAsync(UserCancelled)`, which sets `Cancelling` and runs `cancel.g`. The parked reader wakes on
the notify, exits the pause branch, reads the closed file, and reaches the park test again while
`Cancelling` is set: it parks a second time because `PauseState != NotPaused`. `StopAsync`'s
`finally` puts `NotPaused` back without a notify. `CodeExecutedAsync` calls `Resume()` for `M0` and
`M1` only, so after `M2` nothing wakes the reader: `ExecuteAsync` never passes `await fileTask`,
`job.file` is never cleared, `lastFileCancelled` is never written, and the status reads `idle`
with a job still selected until an `M23` cancels it again or an `M24` runs `start.g` on the dead
job. `CancelWhilePausedRunsCancelMacro` passes only because it uses `M0`, whose `Executed`-stage
hook is the sole wake-up.

The other ordering is no better: if the reader reaches its park test before `StopAsync` has set
`Cancelling`, it exits, and `ExecuteAsync`'s teardown runs to completion, disposing `_file` and
resetting `PauseState` to `NotPaused`, while `cancel.g` is still running on the `M0`'s channel.
`state.status` then reads `idle` during `cancel.g`, `StopAsync`'s `finally` finds nothing to
settle, and `HandleSelectFileAsync`'s refusal no longer refuses, so `M23`/`M32` from DWC selects a
new job while `cancel.g` is still moving the head. Nothing in `ExecuteAsync` awaits the
`StopAsync` the handler started.

### R8. `M24` during `Cancelling` starts the job

**Confirmed by reading.** `ResumeAsync` ignores `Pausing` and `Resuming` but not `Cancelling`, and
`PauseState == Paused` is false, so it takes the "start a selected file" path: `start.g` runs on
the `File` channel while `cancel.g` runs on the caller's, `Resume()` overwrites `Cancelling` with
`NotPaused`, the reader wakes and finishes on the closed file, and the teardown runs under the
still-executing `StopAsync`.

### R9. Lock-order inversion between the job monitor and the reader

**Confirmed by reading; a deadlock when the timing lands.** `JobMonitor.PublishAsync` holds the
object model write lock (a Nito `AsyncReaderWriterLock`, exclusive and writer-preferring) and then
takes the file lock through `GetFilePositionAsync`. `CodeFile.ReadCodeAsync` holds the file lock
and then takes the object model read lock to read `MachineMode`. Once per code read and once every
200 ms respectively; when the write lock is granted between the reader's two acquisitions, both
wait for ever, with the reader's token being `default`, and every other reader of the object model
stalls behind the writer that cannot proceed. (`ReadCodeAsync`'s block-end `FlushAsync(file)` is
outside its lock window, so the `M26` path does not add a second inversion.)

### R10. The deferred-pause decision is made outside the lock and stored after the last check

**Plausible.** `HandlePausePrintAsync` reads `IsProcessing`, `IsDoingMacro` and
`CanRestartMacros` without the job lock, then calls `TryDeferPause`. Between the read and the
store the macro can end and `DoFilePrint` can run `CheckForDeferredPauseAsync`, which finds
nothing. If the `M98` was the file's last code the check never runs again: `M25` reports success
and the job runs to completion. Otherwise the pause lands one code later than the point the
operator saw.

### R11. Cancelling a running job by selecting another reports it as finished

**Confirmed by reading.** `SelectFileAsync` calls `Cancel()`, which records `IsCancelled = IsPaused`. For a running job that
is false, so `ExecuteAsync` logs "Finished job file", runs `stop.g` rather than `cancel.g`, and
writes `lastFileCancelled = false` for a job the operator replaced. The reason is derived three
times (here, in the `M0` handler, and in the teardown) from different inputs.

### R12. The end-of-job `Resume()` hook can start a newly selected job without `start.g`

**Plausible.** `CodeExecutedAsync` calls `Resume()` after every successful `M0`/`M1`, guarded only by
`IsFileSelected && !IsProcessing`. If the teardown has finished and another channel has already
selected the next file, that is exactly the condition `ExecuteAsync`'s wait treats as "start the
job", and the job starts without `ResumeAsync` having run `start.g` or applied the M26 state. It
needs the `M23` to come from a channel other than the `M0`'s, which is the DWC case.

### R13. A pause is accepted while the end-of-job `stop.g` runs

**Confirmed by reading.** `PauseAsync`'s only liveness test is `IsProcessing`, and `IsProcessing`
stays true from the reader's final `break` until the teardown, across the awaited
`StopAsync(File, NormalCompletion)` that runs `stop.g`. An `M25` from DWC, or an event, while
`stop.g` is parking the head is accepted: the feedhold purges `stop.g`'s moves,
`StopReadingForPause` sets `_pausePending` with no reader left to consume it,
`AbandonMacrosForPauseAsync(File)` aborts `stop.g` part-way, `pause.g` runs, and `M25` reports
"Printing paused at". The teardown then sets `NotPaused` and `_file = null`, so `M24` answers
"Cannot print, because no file is selected!". An `M25` that finds `stop.g` non-restartable defers
instead, into a `_deferredPause` nothing will ever consume.

### R14. An abort or cancel during `Pausing` leaves the pause running against the stop

**Confirmed by reading.** `Abort()` and `Cancel()` test only `IsFileSelected` before writing
`NotPaused` and notifying `_resume`. Landing between `PauseAsync`'s first lock window and its
`finally`, they leave `pause.g` running on the commanding channel under `ApplicationStopping`
(which nothing cancels) while the woken reader exits and `ExecuteAsync` runs `StopAsync(Abort)`:
heaters and spindles off under a macro that is parking the head. `PauseAsync`'s `finally` finds
`NotPaused` and settles nothing. Reached from a read-ahead code failing with a non-cancellation
exception during a pause, from `LinkService` shutting down, or from a file-channel `M0` inside
`pause.g` when the pause was deferred (the `!IsPaused` refusal is skipped for file-channel codes,
and `IsCancelled = IsPaused` then records false).

### R15. The first simulation since start-up never finishes

**Confirmed by reading.** With `M37 F1` (the default) `ExecuteAsync` waits, after the file task,
for `job.lastDuration` to become non-null. `JobMonitor.FinishAsync` is the only writer, and it runs
only once `JobMonitor` observes `IsProcessing` false and `NotPaused`, which is what the teardown
*after* this wait clears. The first simulation therefore waits for ever (the `UpTime` wrap test
never fires), `_finished` is never notified, and every later `M23`, `M32` and `M37` is refused as
"already being printed". A later simulation exits on the first model update with the *previous*
job's `lastDuration` and writes that into the file. `M37SimulatesAFile` uses `F0` and skips the
wait; `M37WritesTheSimulatedTimeIntoTheFile` is tagged `KnownGap`.

### R16. The resume leaves a window in which the job looks like no job

**Plausible.** The parked reader holds `IsProcessing = false`. `ResumeAsync`'s `finally` sets
`NotPaused`, notifies and releases the lock; until the reader's continuation re-acquires it and
sets `IsProcessing = true`, every other lock holder sees `NotPaused` and not processing:
`HandleSelectFileAsync` accepts an `M23` from another channel, `SelectFileAsync` calls `Cancel()`
(`IsCancelled = IsPaused` is false) and waits on `_finished`; the reader wakes, resumes into a
closed file, reads null and exits; `ExecuteAsync` logs "Finished job file" and runs `stop.g`. The
paused-then-resumed print is lost with `lastFileCancelled = false`. In the same window
`PauseAsync` and the event pause answer "no file is being printed" and `MachineStatusService`
reports `idle`.

### R17. A pause that throws before its second lock window rewinds to the previous pause

**Confirmed by reading.** Between setting `Pausing` and `StopReadingForPause`, `PauseAsync` awaits
on the caller's channel token: the feedhold poll, the object model read lock, the job lock. If that
token is cancelled (`M112`, `M999`, the connection dropping) the sequence throws, the `finally`
promotes to `Paused`, but no resume point was taken, the job token was not cancelled, `_pausePending`
is false, and `_pausePosition` still holds the previous asynchronous pause's value, because only
`SelectFileAsync` clears it. The engine has meanwhile executed the queued stop. The reader stops
reading on `Pausing`, parks, and rewinds to the previous pause's position; `M24` then drives the
head to the previous pause's restore point.

### R18. The deferred pause skips the flush its synchronous flag assumes

**Plausible.** `CheckForDeferredPauseAsync` calls `PauseAsync(synchronous: true, pausingCode:
null)`. `synchronous` skips the feedhold and the `FlushAsync(File, flushAll)`, on the assumption
that the pausing code's handler flushed everything ahead of it. There is no pausing code: the
reader has up to 32 read-ahead codes dispatched, one possibly inside `SubmitMoveAsync`.
`StopReadingForPause` cancels them and the sequence goes straight to the standstill wait and
`SaveRestorePointAsync`; the cancelled `G1`'s `finally`, which puts the interpreter position back
with `SyncInterpreterToMachine`, has no ordering against `SavePosition` other than the two locks
they both take, so the restore point can be taken from a position at the end of a move that will
never be made.

### R19. A failed resume still resumes the reader

**Plausible.** If `resume.g` throws, the restore move is `Rejected`, or the caller's channel token
is cancelled, `ResumeAsync` skips `RestoreInterpreterStateAsync` but its `finally` still sets
`NotPaused` and notifies. The reader spends `MoveFractionToSkip` and re-reads the interrupted line
in whatever distance mode and feed rate the channel happens to hold, with the head wherever the
failure left it. The reader cannot tell a completed resume from an abandoned one because it did
not run it.

### R20. The cost the structure carries even when nothing races

Not races, but the same structure's price, each confirmed from the code:

- `DoFilePrint` takes the job lock three times per job code (before each read, after each
  completion, and inside the deferred-pause check), each to read two fields and copy a token: 3N
  acquisitions per N-line file, contending with every sequence that holds the lock across an
  await.
- The job lock is held across `_fileInfoParser.ParseAsync` and the `_finished` wait in
  `SelectFileAsync`, across `ApplyRestartStateAsync` and the file lock in `ResumeAsync`, and
  across `SetFilePositionAsync` in the reader's park.
- `WaitForStandstillAsync` polls every 5 ms with up to `1 + 2 × MaxRings` P/Invokes per poll, and
  `CodeProcessor.WaitForStandstillAsync` wraps it in a second loop over every channel;
  `StopEarlyAsync` polls every 2 ms, copying the rest endpoints across the boundary on each poll,
  into an array allocated per call; `MotionTracker` already receives the per-move completion
  events that could complete a task instead.
- The end-of-file flush in `DoFilePrint` runs *after* `PurgeSyncRequestsFor` has cleared the
  channel's job file, so `FlushAsync(file)` finds no stack item and returns false every time
  (`Failed to flush file codes on stage Start` at every job end in the bench log); the flush "in
  case plugins inserted codes at the end of a print file" is dead.
- The "cancel, dispose, recreate the linked source" sequence is written out in
  `StopReadingForPause`, `Cancel()` and `Abort()`, beside `CodeProcessor.CancelPending` which is
  the same three lines; the reader re-reads the resulting token in seven places.
- `RestoreAxesAsync` is a copy of the queue-retry-standstill loop in `SubmitMoveAsync` and the
  probing move, with `RingFullRetryDelay` declared a second time; the feed-rate unit conversion is
  done with a second set of `MmPerInch`/`SecondsPerMinute` constants against
  `MoveInterpreter.ModalFeedRateMmPerSec`, so the pause saves under one rule and the resume
  restores under another.
- The reason a job ended is derived three times from different inputs: `Cancel()` stores
  `IsCancelled = IsPaused`, `HandleStopAsync` computes it from the channel, `ExecuteAsync` from
  the flags. They agree only because `M0` from a non-file channel is refused unless `IsPaused`.
- "Is a job in the way" is spelled `IsProcessing || IsPausedOrChanging` in three handlers,
  `IsProcessing || PauseState != NotPaused` in `JobMonitor` and negated in `EventProcessor`;
  `IsReallyPrinting` has no callers; the reader uses `PauseState >= Pausing` where everything else
  uses `!= NotPaused`.
- `HandlePausePrintAsync`'s remarks carry a `TODO` saying the deferred pause is not implemented,
  above the code that implements it.

---

## 6. Why the structure produces these

The fixes in `KNOWN_BUGS.md` are each correct and each local, and the reason the sweep still fails
is that the structure has seven properties that generate new windows as fast as old ones are closed:

1. **No single owner of the job state.** Nine fields on `JobProcessor` are written from seven
   call paths on five different tasks. Every writer takes the lock, but each takes it for one
   assignment at a time, so every reader sees intermediate combinations: `Pausing` with no pause
   position, `NotPaused` with `_pausePending`, `IsProcessing` false with the file open.
2. **The reader infers, it is not told.** `DoFilePrint` derives "stop reading", "park",
   "rewind to", "carry on" from the fields above. The pause sequence never sends it a message; it
   changes fields and the reader notices in whatever order its own loop happens to check them.
3. **The sequences run on borrowed tasks under borrowed tokens.** `PauseAsync` runs on the `HTTP`
   or `File` pipeline task and inherits that code's cancellation token, which for a synchronous
   pause is the very token the pause cancels. The reader's token is replaced by disposing the old
   source, so every holder of the old token has to notice and re-read.
4. **Cancellation is the signal.** "Stop the read-ahead" is expressed as cancelling a token that
   every read-ahead code, the standstill wait, the deferred-pause check and the file lock all
   share. The same exception therefore means "a pause landed", "a cancel landed", "the machine is
   shutting down" and "this code failed", and the reader's catch blocks re-derive which from the
   state fields.
5. **The resume point is assembled from three classes.** `SubmitMoveAsync` creates and releases
   the record, `StopEarlyAsync` purges and reports ids, `TakeJobResumePoint` chooses among three
   branches, `StopReadingForPause` stores the position, `DoFilePrint` falls back to its own
   `currentFilePosition`, and `SaveRestorePointAsync` copies the fraction. Each transfer is a
   separate lock window and the record's lifetime is tied to submission rather than execution.
6. **Polling everywhere the motion thread is involved.** The feedhold result (every 2 ms),
   standstill (every 5 ms, with `1 + 2 × MaxRings` native calls per poll, wrapped in a second loop
   over every channel's deferred codes), the ring having room (every 5 ms) and the machine status
   (every 250 ms) are all polled on timers by different tasks, so "the machine has stopped" is
   observed at different times by the pause, the reader and the status service, while
   `MotionTracker` already receives a completion event per move.
7. **The job lock on the reader's hot path.** The reader takes `_lock` three times per job code on
   the normal path (before each read, after each completion, and inside the deferred-pause check),
   only to copy two fields and a token; every acquisition queues behind whichever sequence is
   holding the lock across an await. A million-line file is three million acquisitions.

---

## 7. The replacement

Every property in §6 is a property of the *structure*, not of a line of code: the state is a set of
fields with many writers, the reader infers, the sequences borrow tasks and tokens, cancellation
carries four meanings, the resume point is assembled across four transfers, and the motion thread is
polled. None of them can be removed while
[JobProcessor.cs](../../src/DuetControlServer/Files/JobProcessor.cs) and
[JobProcessor.Lifecycle.cs](../../src/DuetControlServer/Files/JobProcessor.Lifecycle.cs) keep their
shape, so the plan is to write the job actor from scratch, delete both files, and repoint their
callers. The behaviour the new code produces is the behaviour [JOB_LIFECYCLE.md](JOB_LIFECYCLE.md)
records, unchanged (§7.14).

### 7.1 The rules the design holds to

1. **One task owns the job state.** A single `JobController` task performs every transition. No
   other code writes job state and no caller holds a job lock. Readers take an immutable snapshot
   from one volatile field, so a read cannot land inside a half-finished transition and cannot
   block anything.
2. **Every change is a declared transition.** A table of (phase, command) says what is accepted,
   what is refused and with which message, and what is held until later. No state is reachable only
   by an interleaving.
3. **The reader is told, never infers.** `JobReader` has an input channel and an output event
   stream. It does not read the job state, does not choose a rewind point and does not decide when
   a job is over. The controller sends it a rewind point only once it holds one.
4. **Cancellation means the run is over.** One `CancellationTokenSource` per job run, cancelled
   once, when the run ends. Stopping the read-ahead is a gate on the `File` channel, not a token
   cancel. Nothing is disposed while a code holds its token and no token is ever re-read.
5. **Every move id terminates.** The engine reports every id it drops, so a wait on a move always
   ends and "the machine has stopped" is a signal rather than a poll.

### 7.2 The pieces

| File | Type | Responsibility |
|---|---|---|
| `Files/Job/JobController.cs` | `JobController : BackgroundService` | The command loop, the transition table, the state snapshot, the streams |
| `Files/Job/JobState.cs` | `record JobState`, `enum JobPhase` | The immutable state and the projections callers read |
| `Files/Job/JobCommand.cs` | `abstract record JobCommand` and its cases | The commands, each carrying its reply completion |
| `Files/Job/JobSequences.cs` | The sequence bodies | `start.g`, the pause, the resume, `stop.g`/`cancel.g`, the restore point |
| `Files/Job/JobReader.cs` | `JobReader` | One read-ahead loop per stream, driven by commands, reporting events |
| `Files/Job/JobStream.cs` | `record JobStream` | The per-motion-system part: the `CodeFile`, its reader, its rewind point |
| `Motion/MotionEvents.cs` | `MotionEvents` | `FeedholdCompletedAsync`, `StandstillAsync`, raised from move events rather than timers |

`JobMoveIndex` keeps its API and changes its lifetime rule (§7.7). `JobMonitor` keeps its clock and
loses its own view of the job (§7.10). `MachineStatusService.Derive` switches on `JobPhase`, so
`PauseState.cs` goes with `JobProcessor.cs` and `JobProcessor.Lifecycle.cs`.

### 7.3 The state

```csharp
internal sealed record JobState
{
    public JobPhase Phase { get; init; }
    public JobFile? File { get; init; }               // virtual and physical path, length, simulating
    public ImmutableArray<JobStream> Streams { get; init; }
    public PrintStoppedReason? StopReason { get; init; }
    public PauseRequest? PendingPause { get; init; }  // held for a non-restartable macro
    public JobFile? NextFile { get; init; }           // an M32 read from the job file
    public JobSequence? Sequence { get; init; }       // the sequence in flight and its token
}
```

`JobPhase` is `Idle`, `Selected`, `Starting`, `Running`, `Pausing`, `Paused`, `Resuming`,
`Cancelling`, `Finishing`, `Aborting`. Everything the rest of DCS asks about the job is a function
of this record: `IsProcessing` is `Phase is Starting or Running or Pausing or Resuming`, "a job is
in the way" is `Phase is not (Idle or Selected)`, `state.status` is a switch over `Phase`, and the
reason a run ended is `StopReason`, written once by the transition that ended it instead of being
derived three times from three inputs (R11, R14).

The controller publishes the new record into a `volatile JobState` field as the last act of each
transition. Callers read `controller.State`: one field read, no lock, on the code hot path as well
(`IsSimulating` is consulted for every M-code from the file). No combination is ever published that
a single transition did not write, which is what removes the window in which the job looks like no
job (R16).

### 7.4 The transitions

The loop dequeues one command at a time. A command either completes inside the loop (a validation,
a refusal, a state change) or starts a *sequence*: a child task, owned by the controller, that runs
the macros and the motion steps. The loop keeps dequeuing while a sequence runs, so `M112` never
waits behind `pause.g`. A sequence writes no state; it posts `SequenceCompleted(outcome)` and the
loop performs the settling transition. That discipline is what keeps single ownership while a pause
takes seconds.

```mermaid
stateDiagram-v2
    [*] --> Idle
    Idle --> Selected: SelectFile
    Selected --> Starting: Start
    Starting --> Running: start.g done
    Running --> Pausing: Pause
    Pausing --> Paused: pause.g done
    Pausing --> Running: pause failed before the stop
    Paused --> Resuming: Resume
    Resuming --> Running: resume.g done, readers told to Run
    Resuming --> Paused: resume failed
    Paused --> Cancelling: Cancel(UserCancelled)
    Cancelling --> Finishing: cancel.g done
    Running --> Finishing: readers Finished, or Cancel(NormalCompletion)
    Running --> Aborting: Abort
    Pausing --> Aborting: Abort
    Paused --> Aborting: Abort
    Resuming --> Aborting: Abort
    Cancelling --> Aborting: Abort
    Aborting --> Finishing: sequence unwound
    Finishing --> Idle: stop.g done, teardown published
```

| Command | From | Effect | Refused elsewhere with |
|---|---|---|---|
| `SelectFile` | `Idle`, `Selected` | `Selected`; the file is parsed before the command is posted | "Cannot set file to print, because a file is already being printed!" |
| `SelectFile` from the `File` channel (`M32` in a job file) | any running phase | Stored as `NextFile`, replied to at once, current run transitions to `Finishing` | |
| `Start` (`M24` on a selected file, `M32`, `M37`) | `Selected` | `Starting`; sequence: `start.g`, the M26 restart state, then `Run` to each reader | "Cannot print, because no file is selected!" |
| `Pause` asynchronous (`M25`, an event) | `Running` | `Pausing`; the pause sequence with the feedhold | "Cannot pause print, because no file is being printed!" |
| `Pause` synchronous (`M226`, `M600`, `M601`, `M25` in the file) | `Running` | `Pausing`; the pause sequence without the feedhold | as above |
| `Pause` while the `File` channel is in a non-restartable macro | `Running` | Held as `PendingPause`, replied to at once, armed on the reader's next boundary | |
| `Resume` (`M24`) | `Paused` | `Resuming`; the resume sequence | ignored in `Pausing` and `Resuming`; refused in `Cancelling`, `Finishing`, `Aborting` (R8) |
| `Cancel(UserCancelled)` (`M0`, `M1`, `M2`) | `Paused` | `Cancelling`; sequence `cancel.g`, then `Finishing` | "Pause the print before attempting to cancel it" |
| `Cancel(NormalCompletion)` (`M0` in the job file, `M0` with no job) | `Running`, `Idle` | `Finishing`; sequence `stop.g` | |
| `Abort` | any but `Idle` | `Aborting`: cancel the sequence, wait for it to unwind, then `Finishing` | |
| `SetFilePosition` (`M26`) | `Selected`, `Paused` | The stream's file position | "Not printing a file" |
| `Fork` (`M606 S1`) | `Running` | A second `JobStream`, its reader started by the same command | "No file is selected" |
| `GetFilePosition` (`M27`, `JobMonitor`) | any | The stream's published position | |
| `ReaderStopped(stream, position)` | `Pausing` | That stream is parked; the last one continues the sequence | |
| `ReaderFinished(stream)` | `Running` | The last stream: `Finishing` with `NormalCompletion` | |
| `ReaderFailed(stream, error)` | any | `Aborting` | |
| `ReaderBoundary(stream)` | `Running` with a `PendingPause` | Re-checks the macro condition; starts the pause or re-arms | |
| `SequenceCompleted(outcome)` | the phase that started it | The settling transition, or the failure transition | |

Four consequences are worth naming, because each is a race in §5 with no expression here:

- A sequence that fails is a transition the controller chooses from the outcome, not a `finally`
  reading fields it did not write. A failed resume settles back to `Paused` and reports the error
  (R19); a pause that fails before it stopped the machine settles back to `Running` (R17). Neither
  can settle into a phase whose invariants it did not establish.
- `Cancel` and `Abort` during `Pausing` are transitions of the same machine, so they are ordered
  against the pause instead of racing it: `Abort` cancels the sequence and waits for it to unwind
  before `Aborting` is published (R14).
- `M32` from inside the job file stores `NextFile` and replies at once, so no handler waits for the
  run it is part of to finish (R6). The chained print starts from `Idle` after the teardown, by the
  same `SelectFile`/`Start` pair every other caller uses.
- The pause held for a non-restartable macro is a field of the state written by the same transition
  that decided to hold it, so the decision cannot be overtaken by the macro ending (R10). If the
  job ends while a pause is held, the transition to `Finishing` drops it and says so, rather than
  leaving it for the next job to find.

### 7.5 The reader

```csharp
// input
abstract record ReaderCommand
{
    sealed record Run(long FromPosition, JobModalState Modal) : ReaderCommand;
    sealed record Freeze : ReaderCommand;                  // stop reading and dispatching
    sealed record Rewind(long ToPosition) : ReaderCommand;  // discard what is held, then report
    sealed record Close : ReaderCommand;
}

// output, posted to the controller
abstract record ReaderEvent
{
    sealed record Stopped(int Stream, long Position) : ReaderEvent;
    sealed record Finished(int Stream) : ReaderEvent;
    sealed record Failed(int Stream, Exception Error) : ReaderEvent;
    sealed record Boundary(int Stream) : ReaderEvent;       // only while the controller has armed it
}
```

The reader owns its `CodeFile`, its code pool and its read-ahead window, and nothing else touches
them. It publishes its position into a volatile field, which is what `M27` and `JobMonitor` read.

`Freeze` and `Rewind` are separate because the rewind point is not known until the machine has
stopped. `Freeze` stops the read-ahead and closes the dispatch gate on the `File` channel's job
stack level (§7.8); `Rewind` discards what the gate holds, waits for the codes that are already
running, sets the file position and reports `Stopped`. Because the controller sends `Rewind` only
once it holds the point, the reader cannot park at the wrong position (R1); because a stream counts
as parked only on `Stopped`, there is no second park and no lost notification (R7); and because
each stream carries its own rewind point in `JobStream`, a forked job pauses correctly, which the
single `_pausePending` flag cannot do today.

### 7.6 The sequences

The pause, with the feedhold skipped for a synchronous one:

```mermaid
sequenceDiagram
    participant L as JobController loop
    participant Q as Pause sequence (controller-owned task)
    participant R as JobReader
    participant M as MotionEvents / engine
    L->>L: Running -> Pausing, publish
    L->>Q: start
    Q->>R: Freeze (closes the File job gate)
    R-->>Q: frozen
    Q->>M: RequestStop
    M-->>Q: FeedholdCompletedAsync: outcome
    Q->>Q: rewind point = JobMoveIndex[outcome.LastSurvivingMoveId]
    Q->>R: Rewind(point)
    R-->>Q: Stopped(position)
    Q->>Q: abandon macros, flush, StandstillAsync
    Q->>Q: SaveRestorePoint, run pause.g
    Q->>L: SequenceCompleted(paused at, reply)
    L->>L: Pausing -> Paused, publish, complete the M25
```

The resume sequence runs `resume.g`, the two restore moves and the interpreter state, and ends by
sending `Run(position, modal)` to each reader, so there is no window in which the job has been
resumed and the reader has not been told, and none in which the reader reads before the head is
back. The stop sequence (`stop.g` or `cancel.g`, heaters and spindles off, `lastFileName`,
`lastFileCancelled`, the file closed, the simulated time written) runs once per run with the reason
the transition gave it, so the guard that is never cleared today (R5) has nothing to guard: `M0`
with no job selected is the same sequence from `Idle`.

The simulated time comes from `JobMonitor`, which is asked for the run's duration as a step of the
stop sequence. `JobMonitor` stops deciding for itself when a job has ended, which is what makes the
current wait circular (R15).

### 7.7 The resume point

The one number the engine reports that is always right is `LastSurvivingMoveId`: the last move it
will make. `DDARing::PurgeAfter` sets it before it purges anything and reports `stopped` afterwards,
so it is valid in the case that has no branch today, a stop that purges nothing from the ring while
`DrainFeedholds` discards the submissions behind it. `JobMoveIndex` maps a move id to
`(JobMoveOrigin, segment)`, and the rewind point is `origin.PointAt(segment + 1)`: the next segment
of that line, or the next code when it was the last. When the surviving move is not a job move (a
macro's, a tool change's) the point is the position the reader reports in `Stopped`, and the
controller knows which case it is because the index says so rather than because a count was zero.

The rule never asks what was dropped, only what survives, which is what makes it right in all four
cases that are wrong today (R2). Two changes support it:

- **The index is not cleared by a pause.** Its lifetime rule becomes its capacity, which is already
  twice a ring (2000 entries), so a lookup of a surviving move id always hits. It is cleared when a
  job is selected and when the link is invalidated, the two events after which a move id from the
  previous run means nothing. Clearing on a pause is what discards the entries the *next* lookup
  needs, and retiring an entry when its move completes would be worse: the surviving move has
  usually completed by the time DCS reads the feedhold result.
- **`CurrentJobMove` goes.** The record that today is handed over by reference identity, and
  released when the last segment is *submitted*, is not part of the rule: the index entry carries
  everything the resume point needs, for every submitted segment, whether or not the code that
  submitted it has finished.

### 7.8 Cancellation, the dispatch gate, and move ids that terminate

- **One `CancellationTokenSource` per run.** Codes read from the file get its token. It is cancelled
  once, in `Finishing`, and disposed after every stream has reported. There is no replacement, no
  disposal under a live code and no re-read, so the sequence cannot cancel the token it is running
  under (R4, R17) and no path can forget to refresh a local copy (R20).
- **Sequences run under their own token**, linked to `ApplicationStopping` and the sequence's
  source. A handler that dies while awaiting its reply does not affect the sequence it asked for, so
  a pause requested from a dropped HTTP connection still finishes (R17).
- **The dispatch gate** is a flag on the `File` channel's job stack level, closed by `Freeze` and
  opened by `Run`. `PipelineStackItem` already reads its pending codes on a single task and already
  drops a code whose token is cancelled before dispatch; the gate adds "hold instead of dispatch",
  and `Rewind` drains what is held and completes those codes as cancelled. Read-ahead is in file
  order, so everything held is at or after the rewind point; the position is an assertion, not a
  filter, and no predicate-based cancellation API is needed.
- **The purge generation is captured when a code enters its handler**, not when it starts building
  its move. A code dispatched before the freeze then always sees the pre-purge value and aborts on
  the change, and a code dispatched after it does not exist. Between them there is no code that can
  queue a move onto a ring that has just been stopped (R3).
- **Every move id terminates.** `DDARing::PurgeAfter` frees purged DDAs without reporting them and
  `MotionService::DiscardSubmissionsFor` consumes discarded submissions silently, so today a waiter
  on such an id is released only if a later id happens to complete. The engine knows exactly which
  ids it drops: it reports them, as `MoveFailed` with a "purged" reason, from both paths.
  `MotionTracker.MoveFailed` then fails that move's waiters instead of only logging, which is a
  change of four lines beside the sweep `MoveCompleted` already performs. A deferred code anchored
  to a purged move then fails instead of hanging the pause (R3), and "everything submitted has
  reached an end" becomes derivable rather than polled.
- **Standstill becomes a signal.** `MotionEvents` sits where both halves of the comparison are
  known: the completed and failed ids from `MotionTracker`, the submitted id from `MovePlanner`. It
  raises `StandstillAsync` when they meet and `FeedholdCompletedAsync` when the feedhold result is
  published, replacing the 5 ms and 2 ms polls; the polls remain only as a watchdog behind the
  signal, logged when they fire.

### 7.9 Locks and lock order

The controller has no lock, so the locks that remain are the object model, the planner and the file.
Their order is written here and asserted in debug builds by a check in each `Lock()`:

```
object model  ->  planner  ->  file
```

`JobMonitor` reads the reader's published position and the controller's snapshot *before* it takes
the object model write lock, which removes the model-then-file order and R9 with it.
`CodeFile.ReadCodeAsync` reads `MachineMode` once, before it takes the file lock, rather than under
it for every code parsed, which removes the file-then-model order and one read lock acquisition per
code with it. Nothing takes a job lock from inside a flush, because there is no job lock,
and the reader's hot path costs no lock acquisition per code against three today (R20).

### 7.10 The surface the rest of DCS sees

```csharp
internal interface IJobController
{
    JobState State { get; }                                     // snapshot, no lock
    ValueTask<Message> SelectFileAsync(JobFile file, CancellationToken ct);
    ValueTask<Message> StartAsync(CancellationToken ct);
    ValueTask<Message> PauseAsync(PauseRequest request, CancellationToken ct);
    ValueTask<Message> ResumeAsync(CancellationToken ct);
    ValueTask<Message> CancelAsync(PrintStoppedReason reason, CancellationToken ct);
    ValueTask AbortAsync();
    ValueTask<Message> ForkAsync(CancellationToken ct);
    ValueTask<long> GetFilePositionAsync(int stream, CancellationToken ct);
    ValueTask<Message> SetFilePositionAsync(int stream, long position, CancellationToken ct);
}
```

Each method posts a command and awaits its reply, so a handler reads as one call with one result and
the refusal messages live in the transition table rather than in five handlers. What leaves the
surface: `Lock()` and `LockAsync()` in all four forms, and with them the thirteen call sites outside
the class that take the job lock, most of them to read two fields; `Resume()`, `Cancel()` and
`Abort()` as public mutators;
`StartSecondJob()`, which exists only because `ForkAsync` cannot start the stream itself;
`IsProcessing`, `IsCancelled`, `IsAborted`, `IsPaused`, `IsPausedOrChanging`, `IsReallyPrinting`,
`PauseState`, `NumJobStreams`, `FileLength`, `IsPauseDeferred`, `TryDeferPause` and
`CheckForDeferredPauseAsync`. The projections callers do use become properties of `JobState`.

The cut-over therefore touches: `MCodeHandler` (M0/M1/M2, M23, M26, M27, M32, M36, M37, M25,
M226/M600/M601, M606, and the two `CodeExecutedAsync` hooks, which go), `GCodeHandler` (`IsSimulating`
for G92), `CodeProcessor` (the abort path and its service-locator resolution), `LinkService` (abort
on shutdown and on invalidation), `EventProcessor` (the autopause pre-check becomes one call),
`JobMonitor`, `MachineStatusService`, `DiagnosticsProvider` through `IAsyncDiagnostics`, and the DI
registration in `Files/Extensions.cs`. Nothing outside `DuetControlServer` references
`JobProcessor`, so the API, DWC and the plugins are unaffected.

### 7.11 How each race is answered

| Race | Answered by |
|---|---|
| R1 reader parks before the rewind point exists | §7.5 the reader is told where to rewind and reports when it has |
| R2 resume point from what was purged | §7.7 from `LastSurvivingMoveId`, with the index kept across a pause |
| R3 codes dispatched after the stop | §7.8 the gate, the generation captured at dispatch, purged ids reported |
| R4 macro unwind under a cancelled token | §7.8 one token per run, sequences on their own |
| R5 `_stopped` never cleared | §7.6 one stop sequence per run, no guard flag |
| R6 `M32` from the job file deadlocks | §7.4 stored as `NextFile`, replied to at once |
| R7 `M2` leaves the reader parked | §7.4 and §7.5 `Cancelling` is a transition and the reader is told to close |
| R8 `M24` during `Cancelling` | §7.4 not in the table |
| R9 monitor and reader lock inversion | §7.9 one order, asserted in debug builds |
| R10 deferred pause decided outside the lock | §7.4 the decision and the store are one transition |
| R11 cancel reported as a normal end | §7.3 `StopReason` written once by the transition |
| R12 `Resume()` hook starts a new job | §7.10 the hook and the method are gone |
| R13 pause accepted during `stop.g` | §7.4 `Finishing` accepts no pause |
| R14 abort during `Pausing` | §7.4 `Abort` cancels the sequence and waits for it |
| R15 first simulation never finishes | §7.6 the duration is asked for, not waited for |
| R16 the window where the job looks like no job | §7.3 one published record per transition |
| R17 pause throwing before its second window | §7.4 and §7.8 the outcome decides, under the sequence's own token |
| R18 deferred pause skips its flush | §7.6 one pause sequence whose steps are the same either way |
| R19 failed resume resumes anyway | §7.4 the outcome settles back to `Paused` |
| R20 the standing cost | §7.9 no hot-path lock, §7.8 no polls, §7.2 one copy of each rule |

### 7.12 Scenarios first

Per [system-tests-first](SYSTEM_EMULATION.md) the scenarios come before the code. They are written
against the current tree, where they record the defect, and they are the acceptance test for the
replacement. The stepped bench is what makes them deterministic: the same pause scenario against the
wall clock stops somewhere different on every run, and a fix cannot be told from a scheduling
accident.

- The stepped sweep at every point in `SteppedPauseTests.PausePoints`, for the relative and the
  absolute job, reports an empty `wrong` list (R1, R2).
- A pause whose earliest purged move is a tool-change macro's, with job lines queued behind it: the
  purged job lines are re-read on resume (R2).
- `M226` from a job file with an `M106` anchored to a move still in flight: `pause.g` runs, the
  restore point is written, the job resumes (R4).
- `M25` landing while the reader is executing an `M106` between two moves: the pause returns within
  the standstill time, no deferred code is left owed, and the head does not move again before
  `pause.g` (R3).
- `M0` from the console after a job has finished on its own: `stop.g` runs (R5).
- `M32` from inside a job file, and inside `stop.g`: the first job is torn down, the second starts,
  nothing hangs (R6).
- `M2` from DWC while paused: `cancel.g` runs, the job is torn down, `lastFileCancelled` is written
  (R7).
- `M0` while paused with a slow `cancel.g`: `state.status` reads `cancelling` until it ends, and
  `M23` and `M24` during it are refused (R7, R8, R12).
- `M23` from another channel at the instant `M24` reports success: refused, the job resumes once,
  and `state.status` never reads `idle` between (R16).
- `M25` and a filament-out event while `stop.g` runs after the last line: both refused with "no file
  is being printed", and `stop.g` runs to its end (R13).
- A read-ahead code failing while `pause.g` runs: `pause.g` completes before the abort switches the
  heaters off (R14).
- `M37` with the default `F1` on a fresh bench: the simulated time is written and a second job can
  be selected (R15).
- `M25` deferred into a non-restartable macro whose last code is followed by a segmented `G1`: the
  restore point is where the machine stopped (R18). With that macro as the file's last code: the
  job ends and the reply says the pause was dropped (R10).
- `M24` with a `resume.g` that fails: the job stays paused and reports the error (R19).
- A job of 100 000 short lines with `JobMonitor` running: completes, with a bench hook that delays
  `ReadCodeAsync` between its two lock acquisitions (R9).
- `M606 S1` and then a pause: both streams stop, each rewinds to its own point, both resume.

### 7.13 Build order

Steps 1 to 3 are additions that stand on their own; step 4 is the cut-over and is one commit. Each
step leaves the tree building and the bench no worse.

1. **The scenarios** of §7.12, tagged with the defect each shows. They fail on the current tree,
   which is the record of what is being fixed.
2. **The motion prerequisites**, each useful on its own: the engine reports purged and discarded
   move ids instead of dropping them silently; `MotionEvents` raises the feedhold and standstill
   completions the two polls stand in for; `JobMoveIndex` is cleared on job selection and link
   invalidation rather than on a pause.
3. **The dispatch gate** on the `File` channel's job stack level, and the purge generation captured
   at handler entry, with their own scenarios. Nothing uses the gate yet.
4. **The controller, the reader and the sequences**, written whole, and the cut-over: the DI
   registration, the call sites listed in §7.10, and the deletion of `JobProcessor.cs`,
   `JobProcessor.Lifecycle.cs` and `PauseState.cs`. The sequence bodies are ported step for step
   from the current ones, which are the record of the RepRapFirmware behaviour; what changes is who
   runs them, in what order, and under which token.
5. **The removals** the cut-over makes dead: the duplicated queue-retry loop and the second set of
   feed-rate constants, the deferral predicate spelled out in three places, and the two comments and
   the stale `TODO` in R20's last entries.
6. **The documentation**: [JOB_LIFECYCLE.md](JOB_LIFECYCLE.md) §2.9 and §3.5 rewritten against the
   controller, the lock order added to [DCS_INTERNALS.md](DCS_INTERNALS.md) §3, and `gcode-flow.md`
   and `object-model.md` in the articles checked against what the new `state.status` projection
   publishes.

Steps 1 to 3 can be worked in parallel with the writing of step 4, since they touch different files.

### 7.14 What does not change

The behaviour RepRapFirmware defines and [JOB_LIFECYCLE.md](JOB_LIFECYCLE.md) ported: which macros
run and on which channel, the feedhold, the two-move return to the restore point, the modal state a
resumed line is read with, the fraction composition across two stops inside one line, the deferred
pause, the simulation path, and every refusal message. This plan changes how the machine reaches
those outcomes, not the outcomes.
