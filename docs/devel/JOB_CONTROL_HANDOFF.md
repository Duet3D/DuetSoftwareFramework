# Job control concurrency: hand-over

Where the review of the pause, resume, stop and restore workflow got to, and what to do next.
Delete this note once step 1 of [JOB_CONTROL_CONCURRENCY.md](JOB_CONTROL_CONCURRENCY.md) §7.13 is
under way and the covering documents carry the status.

## 1. What exists

All of it is in the working tree and none of it is committed. Some paths are staged; leave the index
as it is and commit named paths when the user asks.

| Path | What it is |
|---|---|
| `docs/devel/JOB_CONTROL_CONCURRENCY.md` | The deliverable. §1 to §4 are how the code works today: every thread and task, the shared state with the lock orders actually taken, the sequences as they run, and what each flag was added for. §5 is the catalogue of twenty races with their interleavings, §6 the seven structural causes, §7 the replacement. |
| `docs/devel/KNOWN_BUGS.md` | The confirmed open races as unticked entries: R1 to R9, R11, R13, R14, R15, R17. |
| `docs/devel/PROJECT_OVERVIEW.md` | The WS10 row, its task table and the dependency graph edge. |
| `docs/devel/JOB_LIFECYCLE.md` | A cross-reference to the concurrency document. |
| `src/SystemTests/Scenarios/JobControl/SteppedPauseTests.cs` | Pause points 365 and 380 added to the sweep; they target R2. |

Evidence gathered for the catalogue: both `SteppedPauseTests` sweeps were run on this tree and fail
at 6 of 22 and 3 of 22 points, travelling 445 to 800 mm for a 400 mm job. The bench log shows the R1
ordering directly, `paused at byte 44 (no fpos from firmware), reason 0` logged before `Stopped the
machine early`. The log was written to a session scratchpad and is gone; re-run the sweep to
reproduce it.

## 2. The plan in one paragraph

`JobProcessor.cs`, `JobProcessor.Lifecycle.cs` and `PauseState.cs` are replaced by a job actor
written from scratch: a `JobController` whose single task performs every transition of a declared
state machine and publishes the state as an immutable snapshot, a `JobReader` driven by commands
that never reads job state, sequences that run as child tasks of the controller under its own token,
a rewind point derived from the move the engine says survives, a dispatch gate on the `File` channel
in place of cancelling the read-ahead token, and move ids that always terminate because the engine
reports the ones it drops. §7.11 maps each of the twenty races to the part of the design that
answers it.

## 3. Where to continue

1. Read §7 in full. §7.13 is the build order: scenarios, motion prerequisites, the dispatch gate,
   then the controller and the cut-over in one commit, then the removals, then the documentation.
2. Ask before committing the documents; the commit permission for this work has not been given.
3. Start with §7.12: the scenarios go in first, against the current tree, where they fail. The
   stepped sweep reporting an empty `wrong` list at every pause point is the acceptance test for
   R1 and R2.
4. Before writing the controller, confirm these facts still hold, since the design rests on them:
   `DDARing::PurgeAfter` sets `lastSurvivingMoveId` before it purges and reports `stopped`
   afterwards, so the id is valid when nothing was purged from the ring; `JobMoveIndex` is cleared
   only by `MovePlanner.TakeJobResumePoint`, which is what the new lifetime rule changes; purged
   DDAs and discarded submissions are reported to nobody, which is what step 2 of the build order
   changes; `PipelineStackItem` reads its pending codes on one task and already drops a code whose
   token is cancelled before dispatch, which is where the gate goes.
5. Keep §7.13, `KNOWN_BUGS.md` and `PROJECT_OVERVIEW.md` current in the same commit as each change.

## 4. Outside this work

`JOB_LIFECYCLE.md` carries over 60 em dashes and two passages describing earlier revisions of the
plan (a withdrawn `M25.1`, and `MovementState.AbandonedJobMove`, a type no longer in the tree). Both
break standing rules. The file was outside the review request and needs a pass of its own.
