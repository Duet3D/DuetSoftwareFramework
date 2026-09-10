# Fast pause: DSF and RepRapFirmware

Both DuetSoftwareFramework and RepRapFirmware can stop a print sooner than the movement queue would
drain on its own, and both call it a fast pause or feed hold. They arrived at it separately and the
two algorithms are not the same. This records where they differ and why, so that a change to either
can be judged against the other rather than assumed to be a port of it.

DSF's is `DDARing::Feedhold` in `src/DuetSbcInterface/src/Motion/DDARing.cpp`, reached from
`StopKind::PlannedDeceleration`. RepRapFirmware's is `DDARing::PauseMoves` with
`DDARing::MakeDeceleratingChain` and the four `DDA::TurnInto*` helpers, added in `b9c868bef`
("Implemented fast pause (feed hold)") on `3.7-dev`.

## What each one is solving

The starting point is the same and is RepRapFirmware's older `PauseMoves`, which DSF still carries
as `StopKind::AtExistingJunction`: walk the queue for a junction whose end speed is already at or
below the instantaneous speed change every drive allows, stop there, and drop the rest. It changes no
move's profile, which is what makes it safe, and it is also what makes it useless during a fast
print: lookahead has raised every junction speed above jerk precisely so the machine does not slow
down, so there is no such junction and the whole queue drains. A pause then takes as long as the
lookahead depth.

Both new algorithms answer that by making a stopping point rather than looking for one. They disagree
about where to put it and how to reshape the moves that lead to it.

## Where the stop goes

**DSF walks forward for a boundary that is both restartable and far enough away.** It accumulates
distance and tracks the lowest maximum acceleration across the moves it passes, and takes the first
boundary where `startSpeed² <= 2 · deceleration · distance` and `IsRestartableBoundary()` holds. If no
boundary satisfies both, it gives up and returns false, and the caller waits for the queue to drain
as before.

**RepRapFirmware starts at the first uncommitted move and grows a chain until the speed reaches
zero.** `MakeDeceleratingChain` extends the chain one move at a time, subtracting
`2 · maxAcceleration` from the squared speed at each step, and stops when that would go negative. It
does not ask whether the boundary is restartable.

The restartable condition is the substantive difference and it comes from DSF's architecture rather
than from a different view of the motion. A DSF pause has to be resumable from a file position, so
the stop has to land where the job can be re-entered: a boundary inside an arc or a retraction is not
one, however much room there is to stop in. RepRapFirmware updates its pause restore point from the
move it stopped on and has the same requirement, but it is expressed elsewhere, in what
`canPauseAfter` is allowed to be set on.

## What happens when there is not enough distance

**DSF refuses.** No boundary satisfying both conditions means the machine runs what it has, and the
stop falls back to draining.

**RepRapFirmware decelerates anyway.** When the chain reaches the end of the queue before the speed
reaches zero it sets `notEnoughDistance` and turns every move in the chain into a deceleration from
its own start speed, accepting whatever speed remains at the end. The code says so: "Start
decelerating immediately. There will be some instantaneous speed change at the end", with an
`ideally we would allow some instantaneous speed change here` on both of the places that assume zero.

So RepRapFirmware will always stop, at the cost of a jerk violation at the stopping point, and DSF
will only stop where it can do so cleanly. This is the difference most likely to matter to a user:
RepRapFirmware pauses in a bounded time and DSF sometimes does not.

## How the profiles are rewritten

**DSF re-plans the existing moves.** It forces the end speed at the chosen boundary to zero, walks
backwards capping each move's end speed at what the moves after it can still decelerate from
(`sqrt(endSpeed² + 2 · a · d)`, the same relation `DoLookahead` propagates, run the other way because
the constraint is at the end rather than the start), then walks forwards taking each start speed from
its predecessor and calling `RecalculateMove`. The first move keeps the start speed it had, because
its predecessor is committed.

**RepRapFirmware reshapes each move explicitly.** It walks backwards from the end of the chain
assigning one of four shapes per move: `TurnIntoDeceleratingMoveWithEndSpeed` while the move can
absorb the deceleration, `TurnIntoSteadyThenDecelMove` for the one move that cannot, and
`TurnIntoSteadySpeedMove` for everything before it, or `TurnIntoDeceleratingMoveWithStartSpeed` for
every move when there was not enough distance.

The results are similar in shape. DSF's goes through the ordinary planner, so a move ends up with
whatever profile `RecalculateMove` gives it for the speeds it now has; RepRapFirmware's writes the
profile directly. DSF's is less code and inherits any planner fix automatically; RepRapFirmware's is
explicit about the trapezoid it wants and does not depend on the planner agreeing.

## Committed moves

Neither can touch a move whose segments have been generated and sent to the boards. DSF skips to the
first move that `IsProvisional()`, and RepRapFirmware skips while `IsCommitted()`.

They differ in `DDA::CanPauseAfter`. RepRapFirmware removed the `&& !next->IsCommitted()` condition
in the same commit, with its comment about not being able to cancel moves already sent to CAN
expansion boards, because the skip loop above it has already dealt with them. DSF's `CanPauseAfter`
in `src/DuetSbcInterface/src/Motion/DDA.h` still carries both the condition and that comment. It is
only consulted by `StopKind::AtExistingJunction`, which is the faithful port of the old algorithm, so
removing it there would make that port diverge from the RepRapFirmware release it was ported from
rather than converge on the new one. It is worth revisiting when `AtExistingJunction` is next
touched.

## Where the result goes

**DSF reports and lets DuetControlServer decide.** `PurgeAfter` fills in a `FeedholdOutcome`
(`firstPurgedMoveId`, `movesPurged`, `lastSurvivingMoveId`, `stopped`), the motion thread publishes it
through a seqlock, and the job actor polls for it and rewinds the file to the move that survived. The
native side knows nothing about files or restore points. See `JOB_CONTROL_CONCURRENCY.md`.

**RepRapFirmware fills in the restore point itself.** `PauseMoves` takes a `MovementState&` and writes
the coordinates, feed rate and file position into it directly, because in RepRapFirmware the motion
queue and the G-code interpreter are the same program.

This is the difference that cannot be reconciled and should not be: it is the split the architecture
is built on. Everything above it is an algorithm that either side may adopt from the other.

## Threading

DSF's `Feedhold` must run on the motion thread, because freeing a move frees its segments. It is
reached through the feedhold request queue that `DrainFeedholds` services once per `SpinOnce`.

RepRapFirmware's runs on the caller's task under a `TaskCriticalSectionLocker`, with a
`BasePriorityBooster` around the part that reads `getPointer` and frees moves to lock out the step
interrupt.

## What to consider taking

- **Deceleration when there is not enough distance.** The bounded pause time is worth having, and the
  residual speed change at the stopping point is a decision the RepRapFirmware author has now made
  deliberately. Taking it would mean deciding what a job resume does with a stop that did not land on
  a restartable boundary, which is the reason DSF refuses today.
- **The four explicit `TurnInto*` shapes**, if `RecalculateMove` ever proves to disagree with what the
  re-plan intends. There is no evidence of that today and the current approach is smaller.

Nothing here is a defect on either side. The two were written against the same problem, six months
apart, without either seeing the other.
