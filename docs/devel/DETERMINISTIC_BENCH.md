# The system test bench: deterministic, and fast enough to run constantly

The stage 1 bench of [SYSTEM_EMULATION.md](SYSTEM_EMULATION.md) runs the whole of
DuetControlServer and the real `libduet_sbc` against a scripted controller, which is what makes the
job lifecycle testable at all. Two properties it is documented as having, it does not have: the
same scenario does not produce the same result twice, and the scenarios are slow enough that nobody
runs them while working. Both come from the same line of code, and both are fixed by the same
change.

This plan says what to build. It does not add scenarios; the scenarios it has to support are the
ones [JOB_CONTROL_CONCURRENCY.md](JOB_CONTROL_CONCURRENCY.md) §7.12 lists, which are the acceptance
test for the job control rewrite and therefore have to mean something.

---

## 1. What the bench claims, and what it does

[SteppedTimeline](../../src/SystemTests/ScriptedCanMaster/SteppedTimeline.cs) owns the motion
timeline: it pins the SBC's local clock through `DuetSbc_PinLocalClock` and advances the master step
clock the fake controller reports, so the machine moves only while the test is asking it to. Its own
doc comment says what that is for: "the machine's position is a function of how far the timeline was
advanced and of nothing else".

It is not. `Advance` moves the clock by 2 ms and then calls `Thread.Sleep(1)`, and that millisecond
of real time is the only thing that lets DuetControlServer, the native motion thread and the fake
controller act on the step. How much they get done in it is a property of the host's scheduler.

The same line is the bench's dominant cost: one millisecond of sleeping per two milliseconds of
timeline, before any work is done at all.

Only [SteppedPauseTests](../../src/SystemTests/Scenarios/JobControl/SteppedPauseTests.cs) uses the
timeline. Every other scenario runs against `FreeRunningClock` and therefore against the wall clock.

## 2. The measured baseline

`dotnet test --no-build --filter FullyQualifiedName~SteppedPauseTests`, eight tests, on the
development container:

| Test | Time |
|---|---|
| `AnAbsoluteJobEndsAtItsLastTargetFromEveryPausePoint` | 1 m 09 s |
| `ARelativeJobMakesItsDistanceFromEveryPausePoint` | 1 m 04 s |
| the other six | 2 to 3 s each |
| whole fixture | 2 m 31 s |

The two sweeps are 88% of it. They are also the tests §7.12 names as the acceptance measure for the
rewrite, so they are the ones that will be run most often.

Both sweeps fail on the current tree, which is intended: they are the record of R1 and R2. What is
not intended is that they fail differently every time. `ARelativeJobMakesItsDistanceFromEveryPausePoint`,
same binary, three consecutive runs, listing the pause points whose job did not travel its 400 mm:

```
run A   10 -> 28    80 -> 103   130 -> 148   140 -> 165   160 -> 184
run B   10 -> 89    30 -> 46     40 -> 65     70 -> 93     90 -> 112
run C   10 -> 90   100 -> 120   140 -> 156   170 -> 194
```

The left number is where the test asked for the pause and the right is where the machine stopped.
Only one pause point appears in all three runs, and it stops 62 mm apart between them. The overshoot
is how far the machine ran between the `M25` being issued and the stop taking effect, which is real
time the other threads were given, not deceleration distance. A fix cannot be told from a scheduling
accident, which is the thing the timeline was built to prevent.

## 3. Why the outcome is not a function of the timeline

Four actors make progress on real time rather than on the timeline:

| Actor | Where |
|---|---|
| The native motion thread: `SpinOnce` and a 1 ms real sleep | [MotionService.cpp:118](../../src/DuetSbcInterface/src/Motion/MotionService.cpp#L118) |
| The native link thread, and the fake controller answering it over a real socket | [LinkService.cpp:103](../../src/DuetSbcInterface/src/Interface/LinkService.cpp#L103) |
| DCS's live position loop, `Thread.Sleep(LivePositionInterval)` | [MotionService.cs:189](../../src/DuetControlServer/Motion/MotionService.cs#L189) |
| Roughly thirty `Task.Delay` and `PeriodicTimer` sites, of which `MovePlanner`'s standstill and feedhold polls, `MachineStatusService`, `JobMonitor`, the ring-full retries and `HeatManager` are on the job path | across `DuetControlServer` |

There is no `TimeProvider` anywhere in the tree, so none of the managed ones can be redirected
today.

To that add the bench's own waits, every one of them a real-time poll against a `DateTime.UtcNow`
deadline: 50 ms in `WaitForConfigDoneAsync`, 25 ms in `WaitForStatusAsync` and `WaitForPauseAtAsync`,
10 ms in `ScriptedCanMaster.WaitUntilAsync`.

## 4. Where the wall time goes

In the order it matters:

1. **The dwell.** Half of every scenario's timeline, spent asleep, before the work.
2. **Real seconds in the scenarios that have no timeline.** 230 of the 240 scenarios run on
   `FreeRunningClock`, so a move takes as long as the move takes.
3. **The bench's polls.** Each observed transition costs half a poll interval.
4. **Host startup per test.** Every test builds and starts the whole DI host, IPC server included,
   then runs `config.g` and waits for its marker.
5. **Runner overhead.** `dotnet test` costs seconds before a test runs, and the project rebuilds
   `libduet_sbc` on every invocation.

## 5. Two properties, priced separately

**Reproducibility.** The same scenario gives the same result on every run and every machine. This is
what the §7.12 sweep needs to be an acceptance test rather than a sample.

**Controlled interleaving.** The test places an event at an exact point inside another actor: an
`M25` arriving while the reader is between two segments of a `G1`, or while a code sits between the
two lock acquisitions of `ReadCodeAsync`. This is what the race catalogue needs, and §7.12 already
asks for one such hook by hand for R9.

Reproducibility comes from removing real time. Interleaving needs explicit rendezvous. Doing only the
first leaves the race scenarios probabilistic, which is how they got written as sweeps in the first
place.

## 6. The design

### 6.1 One clock, and nothing reads another

A `TimeProvider` singleton in DuetControlServer's DI, `TimeProvider.System` in production and the
timeline's in the bench. Convert the sites on the job, motion, status and heat paths; leave the
plugin, firmware update and IPC ones on the system provider, since no scenario waits on them. The
bench's own polls take the same provider, which is what turns "poll every 25 ms until the status
changes" into "settle, then read".

On the native side the pinned local clock already exists. Extend it to cover `LinkService`'s
`NowNs()` so that in bench mode no native code reads the real clock either.

### 6.2 The native threads are pumped, not run

`MotionService::SpinOnce` is already separate from `Run`, so `DuetSbc_StepMotion()` plus a start mode
that creates no thread is a small change. Split `LinkService::Execute` the same way into a
`TransferOnce` behind `DuetSbc_StepLink()`. The bench then owns every actor in the process.

Each pump reports whether it did anything: `SpinOnce` whether it prepared, drained or retired
anything, the link whether it moved a transfer, the fake controller whether it had anything to send.

### 6.3 Quiescence replaces the dwell

`Advance` becomes: settle, then step the clock. Settling is a loop over pump link, pump motion, pump
managed, repeated until a whole round reports no change. Only then does virtual time move.

The managed half has two levels, and they are worth doing in this order:

- **Quiescence only.** Keep the thread pool, count outstanding work items and in-flight awaits, and
  advance only when the count is zero. This fixes the outcome at step granularity and is what makes
  the sweep reproducible. Two actors that are both runnable inside one settle can still interleave
  differently, which no §7.12 scenario currently depends on.
- **A single-threaded scheduler.** Run DuetControlServer's tasks on a `TaskScheduler` the bench pumps
  in FIFO order, with the pipeline `ProcessorTask`s and the reader re-pointed at it. One runnable
  thread means one possible interleaving. The cost is finding every place that blocks a thread rather
  than awaiting, and every `Task.Factory.StartNew` that makes its own.

The IPC server and any plugin process keep real threads and are covered by quiescence alone.

This is the change that buys the speed as well. A 4 s job is 2000 settle rounds rather than 2 s of
sleeping, and a settle round with nothing to do is a handful of microseconds.

### 6.4 Every scenario on the timeline

Once the timeline is deterministic and free, there is no reason for a scenario to run on the wall
clock. Move the remaining fixtures onto it and delete `FreeRunningClock`. This is mechanical for
most of them: they wait on a status or a code completing, and those waits become settles.

### 6.5 Named gates for the interleaving scenarios

An `IBenchGate` resolved from DI, a no-op in production, with named checkpoints at the points the
race catalogue names: the reader before it dispatches a code, `SubmitMoveAsync` between segments,
`PipelineStackItem` before its cancellation check, which is also where the dispatch barrier of §7.5
lands, the controller loop before it publishes a transition, and `CodeFile.ReadCodeAsync` between its
two lock acquisitions.

A test parks one actor at a gate, settles everything else, injects its event, then releases. That is
what turns "the pause probably lands in the window" into "the pause lands in the window by
construction", and it is the only way to test the one-code residual window of the boundary pause at
all.

### 6.6 Exact position addressing

`RunToPositionAsync` advances until the reported position passes a value, which depends on when the
position snapshot was published. With quiescence the head's position is an exact function of the
ticks advanced, so the bench can advance to a computed tick instead. The sweep's pause points then
mean exactly what they say.

## 7. Parallelism, and the native globals in the way

Nothing sets `[Parallelizable]`, so the suite is serial. In-process parallelism is blocked by native
global state rather than by the clock pin alone: `StepTimer` is entirely static, so two
`DuetSbcHandle`s in one process share one clock model, even though `DuetSbc_Create` is otherwise
handle-based.

Two routes, in order:

1. **Shard across processes.** Safe precisely because the globals are per-process. A script that
   partitions by fixture and runs N `dotnet test` processes needs no product change.
2. **Make `StepTimer` state per handle.** The real fix, and it also removes a latent defect if two
   links ever coexist in one process. Only worth doing if sharding turns out not to be enough.

## 8. Budgets, and how they are held

Targets, stated so they can be tested rather than hoped for:

- a scenario under 250 ms;
- the whole system test suite under 30 s on one core, under 10 s sharded;
- the two §7.12 sweeps under a second each, since they are pure timeline and are the tests that
  prove the design.

Held by two mechanisms:

- a teardown that records each test's duration and fails it over budget unless it carries the slow
  category, so a test that regresses into slowness fails rather than quietly costing everyone;
- a guard test asserting the number of slow-categorised tests against a recorded figure, so adding
  one is a deliberate act with a review attached.

`scripts/test.sh` is the entry point: build once, then run the unit tests and the system tests with
`--no-build`, excluding `Slow` and `KnownGap`, sharded across processes, printing the ten slowest
tests so the budget stays visible.

## 9. The slow category, as a last resort

`Category("KnownGap")` already exists, so the mechanism is in place and `Category("Slow")` joins it.

The rule: a test may carry `Slow` only with a documented reason in its doc comment saying what real
time it is waiting for and why that time cannot be virtual. It may never be used to avoid converting
a test to the timeline, and the guard of §8 is what makes adding one visible. After §6 there should
be no candidates at all: everything a scenario waits for is either virtual time or work, and both
are pumped.

## 10. Build order

1. `TimeProvider` through DuetControlServer, the pinned clock over the whole native side, and the
   bench's own polls on the same provider. No behaviour change, no test changes.
2. `DuetSbc_StepMotion` and `DuetSbc_StepLink`, and the fake controller pumped by the test.
3. `Settle()` replacing the dwell, with quiescence on the managed side. **Determinism and most of the
   speed arrive here.**
4. Every scenario onto the timeline; `FreeRunningClock` deleted. A bench profile that starts only the
   services a scenario needs.
5. `scripts/test.sh`, the sharding, the per-test budget and the slow-count guard.
6. The single-threaded scheduler, then `IBenchGate` and the scenarios that need it.
7. `StepTimer` per handle, if in-process parallelism is still wanted after sharding.

Steps 1 to 5 deliver the fast, reproducible bench and are a prerequisite of
[JOB_CONTROL_CONCURRENCY.md](JOB_CONTROL_CONCURRENCY.md) §7.13 step 1, which makes the §7.12
scenarios the acceptance test for the rewrite. Steps 6 and 7 deliver the interleaving control the
race scenarios need in order to be assertions rather than samples.

## 11. Acceptance for the bench itself

The bench needs its own tests, because nothing else will catch a regression in it:

- the two sweeps run twenty times give byte-identical results, including the `wrong` list, the
  captured packet stream and the object model asserted at each transition;
- the same under artificial load, one spinning thread per core;
- the same with the timeline advanced in a different step size, which is what proves the outcome is a
  function of the timeline and not of the step;
- a scenario that asks for an interleaving fails, rather than passing by luck, when its gate is
  removed;
- the whole suite inside the §8 budget, measured by the script rather than by hand.

The baseline of §2 is what these are measured against, and the three-run spread is the regression
this plan exists to remove.

## 12. What this changes elsewhere

- [SYSTEM_EMULATION.md](SYSTEM_EMULATION.md) §3 says stage 1 gives "a real feedhold whose purge
  outcome the clock policy makes deterministic". It does not yet; the entry becomes a pointer here.
- [JOB_CONTROL_CONCURRENCY.md](JOB_CONTROL_CONCURRENCY.md) §7.12 opens by saying the stepped bench is
  what makes the scenarios deterministic. It says instead that this plan is what makes it so, and
  that steps 1 to 5 land first.
- The `SteppedTimeline` doc comment claims the property the class does not have. It says what the
  class does and points here for what is missing.
