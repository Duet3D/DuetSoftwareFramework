# Pausing and resuming a job

What happens between `M25` and the next segment the machine makes: where the head comes to rest, what
is recorded there, and how a line the machine is half way through is finished rather than repeated.

The parts that are unusual all come from one fact. RepRapFirmware pauses from inside the loop it is
interrupting, so the interpreter, the move queue and the pause are the same task. Here they are four
tasks that run at once, in two programs, and a pause has to interrupt three of them from outside.

| Program | Runs on | What it contributes to a pause |
|---|---|---|
| [DuetControlServer](src/DuetControlServer), DCS below | the SBC, managed C# | Everything that knows what a file is: which kind of pause this is, where to rewind to, how much of the interrupted line is owed, the restore point, the macros |
| [DuetSbcInterface](src/DuetSbcInterface) | the SBC, native C++ | Everything that knows what a move is: the DDA ring, the deceleration it plans, which queued moves it frees, and the ids it reports back |

They meet at one C ABI call and one result published beside it, and neither side reaches across.
`MoveParams` carries a move id and no file position, so the native side is told nothing about files;
a stop reports the id of the earliest move it dropped, and DuetControlServer is what turns that back
into a place in the job file. Anything below that names a `.cs` file is DuetControlServer, and
anything that names a `.cpp` file is DuetSbcInterface; §10 is the full map.

Related reading: [File management](file-management.md#print-jobs) covers the job loop and the seek
this hangs off; [G-Code flow](gcode-flow.md) covers the pipeline whose codes a pause cancels;
[Differences from RepRapFirmware](rrf-differences.md#8-a-pause-stops-the-machine-sooner-than-reprapfirmwares-does)
covers the one deliberate deviation, which is §3 below. The reasoning behind the design, and what is
still missing, is [JOB_LIFECYCLE.md](docs/devel/JOB_LIFECYCLE.md).

---

## 1. The shape of it

```mermaid
flowchart LR
    subgraph DCS["DuetControlServer, managed"]
        direction TB
        subgraph JOB["JobReader, one per stream"]
            J1["read-ahead loop"]
            J2["frozen, then rewound"]
        end
        subgraph CODE["Code task, one per code"]
            C1["SubmitMoveAsync builds once"]
            C2["queues segment by segment"]
        end
        subgraph PAUSE["Pause sequence, a task of JobController"]
            P1["JobSequences.PauseAsync"]
        end
        ST["MovementState and JobMoveIndex<br/>under the planner lock<br/>SegmentsLeft<br/>PurgeGeneration<br/>MoveFractionToSkip<br/>RestorePoints"]
    end
    subgraph SBCI["DuetSbcInterface, native"]
        direction TB
        N1["MotionService loop<br/>DrainFeedholds"]
        N2["DDARing ring 0<br/>Feedhold, PauseMoves"]
    end

    J1 -- executes --> C1
    C1 --> C2
    C2 -- notes each move's origin --> ST
    C2 -- queues moves, C ABI --> N2
    P1 -- DuetSbc_MotionRequestStop --> N1
    N1 --> N2
    N2 -. seqlock result, DuetSbc_MotionGetFeedholdResult .-> P1
    P1 -- reads the surviving move's origin --> ST
    P1 -- freezes and rewinds --> J2
    ST -- fraction and modal G --> J2
    J2 -- carries on --> J1
```

| Task | Where | Entered from | What it owns |
|---|---|---|---|
| `JobReader` | DCS, `Files/Job/JobReader.cs` | one per job stream, started by the controller | reading codes, the file position it publishes, the rewind it is told to make |
| `SubmitMoveAsync` | DCS, `Codes/Handlers/GCodeHandler.cs` | each `G0`/`G1` on the job channel | building the move once, queueing its segments, noting which job code each came from |
| `JobSequences.PauseAsync` | DCS, `Files/Job/JobSequences.cs` | a task of `JobController`, started by the transition `M25`, `M226`/`M600`/`M601` or an event asked for | the stop, the rewind point, the restore point, `pause.g` |
| `MotionService` loop | DuetSbcInterface, `src/Motion/MotionService.cpp` | the native motion thread | the DDA ring, and the only code allowed to free a queued move |

The job reads far ahead of the machine, so at the instant a pause arrives the file is typically some
lines past the move the head is making, and one of those lines is usually part-way into the queue.
Everything below is about naming that line and how much of it is already behind the machine.

---

## 2. Two kinds of pause

A pause is **synchronous** when the job file has itself reached the pause point, and **asynchronous**
when something else interrupts the job. The distinction is RepRapFirmware's `DoSynchronousPause` and
`DoAsynchronousPause`, and it decides one thing: whether the moves already queued are what has to
run, or what has to be dropped.

| Entry point | Path | Stop | Macro |
|---|---|---|---|
| `M25` from a console or interface | asynchronous | feedhold | `pause.g` |
| `M25` from the job file | synchronous | none, the queue is what has to run | `pause.g` |
| `M226`, `M601` | synchronous | none | `pause.g`, or none for `M226 P0` |
| `M600` | synchronous | none | `filament-change.g`, falling back to `pause.g` |
| `heater_fault`, `filament_error` default action | asynchronous | feedhold | `pause.g` |
| `driver_error` default action | asynchronous | feedhold | none, a driver in error is not asked to move |
| Any of the above during a macro that cannot restart | deferred | whichever it becomes when it runs | as asked for |

`M226`, `M600` and `M601` are refused outside a job file with "use M226/600/601 only within a file
being printed". `M25` on the second file channel returns without doing anything: the restore point
and the interpreter state belong to the first channel, so a fork of the job neither pauses itself nor
records anything.

**A deferred pause** waits for the job to leave a macro that has not declared itself pausable
(`M98 R1` from inside the macro, and `ChannelProcessor.CanRestartMacros`, which walks the channel's
whole macro stack). Abandoning such a macro part-way would
leave whatever it had already done with no way to put it back. The request is held by the controller
and the reader arms a barrier on the job file's own stack level, so the code that would have followed
the macro is cancelled where it would have been started and the pause lands at that boundary.
RepRapFirmware makes the same check in `StartNextGCode`, before it starts the next command from the
file.

---

## 3. How the machine comes to rest

An asynchronous pause plans its own stop. This is the one deliberate deviation from RepRapFirmware in
this subsystem, and [rrf-differences §8](rrf-differences.md#8-a-pause-stops-the-machine-sooner-than-reprapfirmwares-does)
is the entry for it: RepRapFirmware searches the queue for a junction that is already slow enough to
stop at and, during a print at speed, finds none, so the whole queue runs. Here the motion engine
takes the earliest boundary far enough away to decelerate by, forces the end speed there to zero,
re-plans backwards to the last move it has already committed, and frees the rest.

The stop itself is entirely DuetSbcInterface: `DDARing::Feedhold` chooses the boundary and re-plans,
`DDARing::PauseMoves` is RepRapFirmware's search kept beside it as the reference behaviour, and
`MotionService::DrainFeedholds` is what runs either of them on the motion thread. DuetControlServer's
half is a request, a poll, and what it makes of the answer.

```mermaid
sequenceDiagram
    autonumber
    participant M as M25 handler, DCS
    participant L as JobController loop, DCS
    participant P as Pause sequence, DCS
    participant PL as MovePlanner, DCS
    participant E as Motion thread, DuetSbcInterface
    participant S as SubmitMoveAsync, DCS
    participant J as JobReader, DCS

    M->>L: PauseAsync, synchronous false
    L->>L: Running -> Pausing, publish
    L->>P: start the sequence
    P->>J: Freeze: the generation is cancelled, nothing more is dispatched
    P->>PL: StopEarlyAsync, plannedDeceleration true
    PL->>E: DuetSbc_MotionRequestStop, the only call that crosses
    PL->>PL: State.NotePurge, under the planner lock
    E->>E: DrainFeedholds, then Feedhold on ring 0
    E-->>PL: DuetSbc_MotionGetFeedholdResult, polled<br/>stopped, lastSurvivingMoveId, movesPurged
    PL->>PL: ResyncFromEngine, SyncInterpreterToMachine, SegmentsLeft = 0<br/>MotionTracker.FailAfter, last submitted id rolled back
    PL-->>P: FeedholdOutcome
    S->>S: the purge generation moved on, throw OperationCanceledException
    P->>P: AbandonMacrosForPauseAsync on File
    P->>J: Drain: every code the stream started has ended
    P->>PL: JobRewindPointFor, from the surviving move
    PL-->>P: JobResumePoint, or nothing
    P->>J: RewindAsync to that point
    P->>PL: WaitForStandstillAsync
    P->>PL: SaveRestorePointAsync
    P->>P: RunPauseMacroAsync runs pause.g
    P->>L: SequenceCompleted
    L->>L: Pausing -> Paused, publish, answer the M25
```

Three parts of that order are load bearing:

- **The request and the answer are separate calls across the C ABI.** Freeing a move frees its
  segments, and only the native motion thread may do that, so the result cannot come back from the
  call that asks for the stop. It is published through a seqlock and polled every 2 ms, which is also
  why `DrainFeedholds` collapses several requests into one stop, the strongest winning.
- **`NotePurge` runs when the stop is requested, not when it is answered.** `PurgeGeneration` says
  one thing to every channel: the ring was emptied, so anything you were part-way through is void.
  Bumping it early is what keeps a segmented move from feeding a ring that is about to be emptied.
- **The freeze comes before the stop, and the drain after it.** Nothing may be dispatched onto a
  ring that is about to be emptied, so the read-ahead is cancelled first; but a code the stream
  already started may be a deferred one parked on a move the stop is about to drop, so waiting for
  the codes in flight comes after the stop that releases them.
- **The rewind point is read from what survives, never from what was purged.** The engine reports
  the last move it will make, and `JobMoveIndex` says which job code that move came from - or, when
  it came from a macro the job invoked, which job code invoked the macro.

**If the engine refuses, or reports that it did not stop**, nothing is purged and the queue drains as
it would have without the feedhold. The code that was going out is still truncated by the pause, and
still recorded, so the resume asks for the rest of that code and no more. RepRapFirmware does the same
in its own "no moves skipped, but there is a move waiting" branch.

Only ring 0 is stopped. A second motion system would need its own stopping point and its own restore
data, which is what `M596` brings into existence.

---

## 4. Where the job carries on from

A move the engine knows is one **segment**, not one line of the file: the height map and a
non-Cartesian geometry both need a line divided. So the boundary a stop lands on is usually inside a
G-code, and every segment of that code carries the same file position. Rewinding to it and reading
the line again plainly would ask for the whole line a second time.

All of this bookkeeping is DuetControlServer's, in `Motion/JobMoveIndex.cs`, and it exists because
the native side deliberately holds none of it. Each job movement code produces one `JobMoveOrigin`:
the file position, the code's length, the modal G command, the feed rate it was read with, the
fraction the build started from, and its segment count. `JobMoveIndex` maps each queued move id,
which is the only name the native side knows a move by, to that record and to the move's own place in
it. The file position and the fraction are fields of one record, so they cannot come to describe
different lines. A move made by a macro the job invoked is noted under the code that invoked the
macro, because the macro's own offsets are into the macro rather than into the job file.

`MovePlanner.JobRewindPointFor` reads it once, under the planner lock, and the id it looks up is the
last move the machine will make - never one that was dropped:

```mermaid
flowchart TD
    A{"the engine stopped"}
    A -- yes --> B{"JobMoves.TryGet<br/>lastSurvivingMoveId"}
    A -- no --> C{"JobMoves.TryGet<br/>lastSubmittedMoveId"}
    B -- found, a job move --> D["origin.PointAt(segment + 1)<br/>the segment after the one it rests on"]
    B -- found, a macro's move --> M["origin.PointAt(0) with macroRestarted<br/>the invocation runs again whole"]
    B -- not found --> E["nothing<br/>the move was another channel's"]
    C -- found --> F["origin.PointAt(segment + 1)<br/>everything submitted will run"]
    C -- not found --> G["nothing<br/>the reader's own position stands"]
    D --> H["JobResumePoint<br/>file position, proportion, G, F"]
    M --> H
    F --> H
    E --> I["null<br/>rewind to the last completed code, no fraction"]
    G --> I
```

Those are RepRapFirmware's three branches of `DoAsynchronousPause`, each of which fills in the file
position and the proportion together. Two cases are worth spelling out:

- **The earliest dropped move cannot be named.** It belonged to a macro. A macro's file position is
  an offset into the macro and the resume rewinds the job file, so recording one would send the job
  somewhere unrelated. The resume goes back to the last completed job code, which is the line that
  invoked the macro, and the macro runs again whole.
- **Every segment of the code reached the ring.** Nothing of that code is still owed, so the resume
  point is the code *after* it rather than its own start with all of it skipped.

The fraction is a fraction of the **whole code**, however many times the job has been stopped inside
it. A resume rebuilds only what is left, so a second stop inside the same code composes:
`fractionAtStart + (1 - fractionAtStart) x segmentsMade / segmentCount`.

### What the fraction applies to

| The line says | Owed after resuming | Why |
|---|---|---|
| an absolute axis target (G90) | the target, unscaled | the head is moved back to where it stopped, so the rest of the line is the rest of the move |
| a relative axis word (G91) | the word x `1 - proportionDone` | it is a distance to travel, and part of it has been travelled |
| extrusion, in either mode | the amount x `1 - proportionDone` | extrusion is an amount however the file expresses it |

The last row includes absolute extrusion. An axis has a start that the resume moves; an extruder does
not, because the engine carries the fraction of a step between moves, so the amount itself is what
shrinks.

Two more things travel with the fraction, because the line being resumed is one the file was already
reading rather than one it was about to start:

- **the modal G command**, since the line may be a bare `X100 Y100 E5` whose `G1` is several lines
  above the rewind point, and seeking throws the parser's modal state away;
- **the feed rate the line was read with**, unscaled by `M220`, since the line need not name `F`.
  Restoring the scaled one would fold the speed factor into the file's own feed rate on every pause.

Only the job file's own codes may spend the fraction. A macro invoked between the resume and the
job's next move runs on the same channel, and would otherwise be shortened by what the job is owed.

---

## 5. What a pause leaves behind

Once the machine has settled, `SaveRestorePointAsync` writes restore point 1, which is the pause
point (`RestorePoint.PauseNumber`; 2 is the tool change, and `G60 S<n>` writes 0 to 5):

| Field | From | Visible as |
|---|---|---|
| `Coords` | the interpreter position, put back in step with the machine by the stop | `move.motionSystems[0].restorePoints[1].coords`, and the deprecated `state.restorePoints[1]` |
| `FeedRate` | the resume point, else the channel's feed rate | `.feedRate` |
| `GCommandNumber` | the resume point | `.gCommandNumber` |
| `ToolNumber`, `FanSpeed` | the machine at the moment it stopped | `.toolNumber`, `.fanPwm` |
| `ProportionDone` | the resume point | not published: it is how to resume, not where the machine is |
| `FilePosition` | not written here: the job holds the position and seeks with it | `job.filePosition`, once the seek has happened |
| `VirtualExtruderPosition` | nothing yet, see §9 | `.extruderPos` |

Then `pause.g` runs, or `filament-change.g` for `M600` with `pause.g` as the fallback, on the channel
that asked for the pause. Neither runs unless every axis is homed: both are written to lift and park
the head, and neither is meaningful on a machine that does not know where it is. The reply is
`Printing paused at X... Y... Z...`, taken from the restore point.

The job task, meanwhile, has stopped reading, drained what it had read ahead, seeked to the pause
position, and is waiting. Block state does not survive that seek: an open `while` is re-parsed from
the rewound position, so its counter starts again ([file management](file-management.md#print-jobs)).

---

## 6. Resuming

```mermaid
sequenceDiagram
    autonumber
    participant M as M24 handler
    participant L as JobController loop
    participant R as Resume sequence
    participant MR as MacroRunner
    participant PL as MovePlanner
    participant J as JobReader
    participant S as next job move

    M->>L: StartOrResumeAsync, runMacro unless M24 P0
    L->>L: Paused -> Resuming, publish
    L->>R: start the sequence
    R->>MR: resume.g, only when every axis is homed
    R->>PL: MoveBackToRestorePointAsync
    R->>PL: RestoreInterpreterStateAsync, the restore point's F and distance modes
    R->>PL: MoveFractionToSkip = rp.ProportionDone
    R->>L: SequenceCompleted
    L->>L: Resuming -> Running, publish
    L->>J: RunAsync with the stream's rewind point
    J->>J: ModalGCommand = rp.GCommandNumber, FirstCommandAfterRestart
    J->>S: read the code at the rewound position again
    S->>PL: BuildRawMove scales relative words and extrusion by 1 minus the fraction
    S->>PL: MoveFractionToSkip = 0, spent by this one move
```

Nothing in that diagram crosses to the native side except the moves themselves: the resume is
DuetControlServer putting the machine, the interpreter and the file back into step, and the moves it
queues to do so are ordinary moves.

**The head goes back in one move or two.** When it is above the pause point the resume travels across
first and descends afterwards, so the nozzle does not drag through the print; when it is at or below
the pause point every axis moves together. That is RepRapFirmware's `resuming1` and `resuming2`, and
only the single-motion-system branch of it is ported.

**Nothing is replanned.** The purged moves are not stored, re-planned or re-submitted. The file is
read again from the recorded position and the moves are rebuilt from whatever state the machine is in
after `resume.g`, which may have changed the tool, the temperatures or the position. That is the only
correct thing to do, and it is RepRapFirmware's resume path unchanged.

`M24 P0` skips `resume.g`. `M24` on a file that was selected but never started runs `start.g` instead
and begins the job, which is also what `M32` does.

---

## 7. Job states

```mermaid
stateDiagram-v2
    direction LR
    Running --> Pausing: Pause accepted
    Pausing --> Paused: the sequence settles, whatever it did
    Paused --> Resuming: StartOrResume
    Resuming --> Running: resume.g done, the readers told to read
    Resuming --> Paused: the resume failed
    Paused --> Cancelling: Stop from a channel other than File
    Cancelling --> Finishing: cancel.g has run
    Finishing --> Idle: the teardown is published
```

The phase is one field, written only by the controller's own task, and every transition of it is a
declared one. What it changes:

| Phase | What it changes | Published as |
|---|---|---|
| `Pausing` | the readers stop at once, and a second `M25` is refused with "Printing is already paused!" | `state.status` = `pausing` |
| `Paused` | `M24` resumes, `M0`/`M1`/`M2` cancel | `paused` |
| `Resuming` | a repeated `M24` is ignored rather than refused: the machine is already going where it was asked | `resuming` |
| `Cancelling` | `cancel.g` is running after the file has already been closed | `cancelling` |
| `Finishing` | `stop.g` runs, then the teardown; no pause is accepted | `busy`, as RepRapFirmware reports it |

A pause settles to `Paused` on every outcome. Its first steps cannot be cancelled by the caller, so by
the time anything that can fail is reached the machine has been told to stop and has to say so; a
resume that fails settles back to `Paused` and reports the error rather than resuming anyway.

---

## 8. Starting part-way through a line: M26

`M26` is the same problem from the other end. `M26 S<offset>` seeks the job file, and the two
parameters that go with it describe how the line at that offset is to be read:

- `P<fraction>` is how much of the line the machine has already made,
- `C<number>` is the modal G command it was read under.

Both are held until `M24`, because that is what starts printing, and they take exactly the route a
resumed pause takes: `RestartMoveFractionDone` and `RestartGCommandNumber` become
`MoveFractionToSkip` and the file's modal command, and the first move built afterwards is scaled by
what is left. This is what a `resurrect.g` written after a power failure uses; writing that file is
not implemented (§9).

---

## 9. What is not there yet

| Gap | Effect |
|---|---|
| The virtual extruder position | The restore point publishes zero for `extruderPos`. What has to be recorded is the extruder position at the *start* of the interrupted line, since the resume rewinds the absolute-extrusion reference to it. It waits on the extrusion totals |
| Arcs, and firmware retraction | `G2`/`G3` and `G10`/`G11` are not implemented. When they land, an arc must not carry a restartable boundary except on its last segment, and a retraction must not carry one at all, or a resumed line would recompute the arc centre from the wrong start or retract twice |
| Power-fail resume | `resurrect.g`, `M911` and `M916`. `M26 P` and `C` are the half of it that exists |
| A second motion system | Only ring 0 is stopped, and only the first file channel records or spends a fraction. `M596` and `M598` widen both together |
| `M25.1` | A fraction on `M25` is accepted as `M25` rather than looked for as `sys/M25.1.g` |

[JOB_LIFECYCLE.md](docs/devel/JOB_LIFECYCLE.md) tracks all of these, along with the reasoning behind
the design above.

Two of those rows are gaps on the native side and the rest are DuetControlServer's. The arc and
retraction row is `DDA::IsRestartableBoundary` needing to be cleared for boundaries a print cannot be
restarted from; the second motion system row is `DrainFeedholds` stopping ring 0 only, and DCS having
one restore point and one interpreter state.

---

## 10. Where each piece lives

**DuetControlServer**, `src/DuetControlServer`, everything that knows what a file is:

| File | What is in it |
|---|---|
| `Files/Job/JobController.cs` | the transition table: what each of M0, M23, M24, M25, M26, M32, M37, M226 and M606 does from the phase the job is in |
| `Files/Job/JobSequences.cs` | `PauseAsync`, `ResumeAsync`, `StopAsync`, `SaveRestorePointAsync`, `MoveBackToRestorePointAsync` |
| `Files/Job/JobReader.cs` | the read-ahead loop, the freeze, the rewind, the published file position |
| `Codes/Handlers/MCodeHandler.cs` | `M25`, `M226`/`M600`/`M601`, `M24`, `M26` |
| `Motion/MovePlanner.cs` | `StopEarlyAsync`, `JobRewindPointFor`, `FeedholdOutcome` |
| `Motion/JobMoveIndex.cs` | `JobMoveOrigin` and its `PointAt`, `JobResumePoint`, the move id index |
| `Motion/MovementState.cs` | `CurrentJobMove`, `SegmentsLeft`, `PurgeGeneration`, `MoveFractionToSkip`, `RestorePoints` |
| `Motion/MoveInterpreter.cs` | the scaling of §4, and `SyncInterpreterToMachine` |
| `Codes/Handlers/GCodeHandler.cs` | `SubmitMoveAsync`: the record's creation, the per-segment checks, the unwind |
| `Link/Native/NativeLink.cs` | `RequestStop` and `TryGetFeedholdResult`, the managed side of the two calls |

**DuetSbcInterface**, `src/DuetSbcInterface`, everything that knows what a move is:

| File | What is in it |
|---|---|
| `src/Motion/MotionService.cpp` | `DrainFeedholds`, collapsing requests and publishing the result through the seqlock |
| `src/Motion/DDARing.cpp` | `Feedhold`, the planned deceleration; `PauseMoves`, RepRapFirmware's search kept as the reference |
| `src/Motion/DDA.cpp`, `DDA.h` | `IsRestartableBoundary`, `SetSpeedsForFeedhold` |
| `src/CApi.cpp` | `DuetSbc_MotionRequestStop` and `DuetSbc_MotionGetFeedholdResult`, the C ABI both sides meet at |
