# Job control concurrency: hand-over for the next session

This note records where the review of the pause, resume, stop and restore workflow stopped and
what the next agent should do first. Delete it once the work it describes has been picked up and
[JOB_CONTROL_CONCURRENCY.md](JOB_CONTROL_CONCURRENCY.md) §7.4 carries the status instead.

## 1. What exists

All of this is in the working tree and none of it is committed. The user staged some paths
themselves; leave the index as it is and commit named paths only when asked.

| Path | State | What it is |
|---|---|---|
| `docs/devel/JOB_CONTROL_CONCURRENCY.md` | new, partly staged, further edits unstaged | The deliverable: actors and tasks (§1), shared state and lock orders (§2), the sequences as they run (§3), the purpose and window of each flag (§4), the race catalogue R1 to R20 (§5), the six structural causes (§6), and the plan: `JobController` state machine, `JobReader` as a driven component, cancellation by file position, resume point from `LastSurvivingMoveId`, motion signals instead of polls, a written lock order; scenarios first, six phases (§7). |
| `docs/devel/KNOWN_BUGS.md` | staged and further unstaged edits | The confirmed open races added as unticked entries. Another session edited this file concurrently and left duplicates; the duplicates were removed, but re-check the file for repeated entries before committing. |
| `docs/devel/PROJECT_OVERVIEW.md` | staged | WS10 row, task-table entry and graph edge for the concurrency plan. |
| `docs/devel/JOB_LIFECYCLE.md` | staged | Cross-reference to the concurrency document only. |
| `src/SystemTests/Scenarios/JobControl/SteppedPauseTests.cs` | staged | Pause points 365 and 380 added to the sweep; they target R2. |

Evidence from this session, in the scratchpad (session-local, will be lost):
`scratchpad/stepped.log` is a run of both `SteppedPauseTests` sweeps on this tree. They fail at
6 of 22 and 3 of 22 points. The log shows the R1 ordering directly: `paused at byte 44 (no fpos
from firmware), reason 0` is logged before `Stopped the machine early`, and after `M24` the job
rewinds a second time, travelling 800 mm for a 400 mm job. Re-run the sweep to regenerate this;
the command is the normal system-test run filtered to `SteppedPauseTests`.

## 2. Findings that arrived after the document was written

The review's fan-out agents reported after §5 was closed. Most of their findings are already
R1 to R20. The ones below are not in the document yet; verify each against the code before
adding it, since the line numbers are from this session's tree.

Cleanups that belong in §5 R20 or §7.2 (what the rewrite removes):

- `StopReadingForPause`, `Cancel` and `Abort` in `JobProcessor.cs` each inline the same
  cancel/dispose/recreate of the linked `CancellationTokenSource`; `CodeProcessor.CancelPending`
  already expresses it. `DoFilePrint` re-reads the token under the lock in seven places.
- `RestoreAxesAsync` in `JobProcessor.Lifecycle.cs` copies the queue-retry-standstill loop of
  `GCodeHandler.SubmitMoveAsync` and `GCodeHandler.Probing.MoveToPointAsync`, and redeclares
  `RingFullRetryDelay`.
- `SaveRestorePointAsync` and `RestoreInterpreterStateAsync` recompute the feed-rate conversion
  with private `MmPerInch` and `SecondsPerMinute` constants; `MoveInterpreter.ModalFeedRateMmPerSec`
  is the forward conversion. `MCodeHandler.SaveSimulationRestorePointAsync` hardcodes `25.4f` and
  `60.0f` for the same thing.
- The deferral rule `IsDoingMacro(ch) && !CanRestartMacros(ch)` is spelled out in
  `HandlePausePrintAsync`, `HandleSynchronousPauseAsync` and half of it again in
  `CheckForDeferredPauseAsync`.
- `IsProcessing || IsPausedOrChanging` is written out in three `MCodeHandler` handlers and in two
  other spellings in `JobMonitor` and `EventProcessor`; `IsReallyPrinting` has no callers.
- `SelectFileAsync` parses the whole new file with the `JobProcessor` lock held; `ResumeAsync`
  holds the same lock across the file lock; `StopEarlyAsync` allocates an endpoint array per call
  and polls at 2 ms. These fit the §7 phase 5 work on lock scope and motion signals.

Convention breaches to fix in passing (memory rules: comments describe present code, plans
describe the current state, no em dashes, no magic numbers):

- `JobProcessor.cs` around line 411: comment narrates the removed firmware macro-stack copy.
- `MCodeHandler.cs` around line 2080: comment narrates the `M24`/`M32`/`M37` cases removed from
  `CodeExecutedAsync`.
- `MCodeHandler.cs` around line 635: the `HandlePausePrintAsync` remarks carry a TODO saying the
  deferred pause is not implemented; lines 660 to 667 implement it.
- `JOB_LIFECYCLE.md`: over 60 em dashes, and two revision-history passages (decision 5 near line
  1018 mentioning a withdrawn `M25.1`; near line 878 naming `MovementState.AbandonedJobMove`, a
  type no longer in the tree). This file was outside the review request and was not rewritten;
  it needs a pass of its own.

## 3. Where to continue

1. Read [JOB_CONTROL_CONCURRENCY.md](JOB_CONTROL_CONCURRENCY.md) in full; §7.4 is the ordered
   list of phases and every phase names the races it closes.
2. Fold the §2 items above into the document after verifying them, then ask the user whether to
   commit the documents (the commit permission for this work has not been given).
3. Begin phase 1 with its scenarios: per the system-tests-first rule, each race gets its §7.3
   scenario before the code moves. The first acceptance test is the existing stepped sweep
   reporting an empty `wrong` list at every pause point for both jobs; R1 (publish the rewind
   point before the reader can park) is the fix it measures. R4, R5, R7, R8 and R15 each have a
   scenario listed in §7.3 and a one-to-three-line fix listed in §7.4.
4. Phase 2 (resume point from `LastSurvivingMoveId`) needs the native side confirmed: purged and
   discarded submissions are not posted as `MoveFailed`, so `MotionTracker` waiters on them are
   released only by the by-id cancel list in the pause sequence. That is the mechanism phase 2
   replaces.
5. Keep §7.4 statuses, `KNOWN_BUGS.md` and `PROJECT_OVERVIEW.md` current in the same commit as
   each change.
