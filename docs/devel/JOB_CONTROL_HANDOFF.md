# Job control concurrency: hand-over

Where the pause, resume, stop and restore workflow got to, and what to do next. Delete this note
once step 1 of [JOB_CONTROL_CONCURRENCY.md](JOB_CONTROL_CONCURRENCY.md) §7.13 is done and the
covering documents carry the status.

## 1. What is in the tree

Steps 2, 3 and 4 of §7.13 are done. `JobProcessor.cs`, `JobProcessor.Lifecycle.cs` and
`PauseState.cs` are gone; `src/DuetControlServer/Files/Job/` is the job actor.

| Path | What it is |
|---|---|
| `Files/Job/JobController.cs` | The command loop, the transition table, the published `JobState`, the sequence in flight and its token |
| `Files/Job/JobState.cs` | `JobPhase`, `JobState`, `JobFile`, `JobStream`, and the `state.status` mapping |
| `Files/Job/JobCommand.cs` | The commands, `PauseRequest`, `PauseMacro`, `SequenceOutcome`, `StreamRewind` |
| `Files/Job/JobReader.cs` | One read-ahead loop per stream, the freeze, the drain, the rewind, the published position |
| `Files/Job/JobSequences.cs` | `start.g`, the pause, the resume, `cancel.g`, `stop.g`, the teardown |
| `Motion/MovePlanner.cs` | `JobRewindPointFor` (§7.7), `FeedholdCompletedAsync`, `StandstillAsync`, `QueueAndWaitAsync` |
| `Motion/MotionTracker.cs` | `FailAfter` and `WaitForRetirementAsync`, so every move id terminates |
| `Motion/JobMoveIndex.cs` | `IsMacroInvocation`, and the index kept across a pause |

§7.13 records five departures from the letter of §7.5 and §7.10 and why each was made.

## 2. What is not done

**Step 1, the scenarios of §7.12.** They are the acceptance test for the whole of §7 and they are
blocked on steps 1 to 5 of [DETERMINISTIC_BENCH.md](DETERMINISTIC_BENCH.md), because a scenario that
answers differently on each run records nothing. Nothing in `SystemTests` was added or changed by
steps 2 to 4.

**The stepped pause sweep** in `SystemTests/Scenarios/JobControl/SteppedPauseTests.cs` is what R1 and
R2 are ticked against in [KNOWN_BUGS.md](KNOWN_BUGS.md), and it has not been run on a bench whose
results are a function of the scenario. Until it has, those ticks record the structure being gone
rather than the behaviour being measured.

**Hardware.** None of this has been run on a real machine yet.

## 3. The three job-control scenarios that fail, and why

All three fail identically on the commit before step 2, so none of them is a regression. Two of them
encode a timing assumption the bench cannot honour, which is the same problem WS11 exists to fix:

- `DeferredPauseTests.PauseDuringPlainMacroDefers` and `PauseDuringToolChange` expect the pause to
  land while a macro is running. A movement code completes as soon as its move is *queued*, so a
  macro of four slow moves finishes in about 13 ms while the machine spends eight seconds making
  them; by the time the scenario's `M25` arrives the `File` channel is inside no macro, so there is
  nothing to defer and the pause is an ordinary asynchronous one. The bench log shows the macro
  finishing 1.5 seconds before the `M25`. What the scenarios need is a way to hold a macro open, not
  a longer sleep.
- `CancelRestartTests.RestartWithFractionAndModalCommand` expects a bare `X65` line to be read as a
  move under `M26 C1`. `CodeParserBuffer.MayRepeatCode` is set from `state.machineMode`, and only
  CNC and Laser repeat the last G command; the bench runs in FFF, so the line is not a move at all
  and `M26 C` has nothing to apply. Either the scenario configures a mode that repeats codes, or the
  behaviour it is asserting is one FFF does not have.

## 4. Where to continue

1. Land [DETERMINISTIC_BENCH.md](DETERMINISTIC_BENCH.md) steps 1 to 5, then write the scenarios of
   §7.12. The stepped sweep reporting an empty `wrong` list at every pause point is the acceptance
   test for R1 and R2.
2. Run the whole thing on hardware. [pause-and-resume.md](../../src/Documentation/articles/pause-and-resume.md)
   describes what it should do.
3. Keep §7.13, [KNOWN_BUGS.md](KNOWN_BUGS.md) and [PROJECT_OVERVIEW.md](PROJECT_OVERVIEW.md) current
   in the same commit as each change.

## 5. Outside this work

[JOB_LIFECYCLE.md](JOB_LIFECYCLE.md) carries over 100 em dashes, and its §2 describes the tree as it
stood before the port rather than as it stands. The sections §7.13 named are updated; the file needs
a pass of its own.
