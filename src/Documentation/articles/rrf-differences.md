# Where DSF deliberately differs from RepRapFirmware

Most of DuetSoftwareFramework's machine control is a port of RepRapFirmware. Where the port is
faithful there is nothing to say; this article is the other list — the places where DSF was *chosen*
to behave differently, and why.

**This is not a list of gaps.** A feature that is not ported yet is missing, not different: those are
tracked with their blockers in [MCODE_MIGRATION.md](docs/devel/MCODE_MIGRATION.md),
[EVENTS_MIGRATION.md](docs/devel/EVENTS_MIGRATION.md) and
[STALL_DETECTION.md](docs/devel/STALL_DETECTION.md). Everything below is present, works, and does
something other than what RepRapFirmware does.

The distinction is load-bearing, because the porting contract makes divergence expensive on purpose:
a gap is a `// TODO` naming what it waits for, never an invention, and a structural departure is
asked about rather than chosen by whoever is typing. A departure that produces the right answer today
is the hardest kind of bug to find later, because the code looks considered and the difference only
surfaces when the missing piece lands and nobody remembers it was traded away. So each entry here is
a decision somebody had to argue for.

---

## 1. The architecture that forces most of it

RepRapFirmware is one program on one board. It parses the G-code, plans the motion, generates the
steps and reads the pins, all inside one address space and one step interrupt. Here the same work is
four programs across three boards:

| Program | Runs on | What it is for |
|---|---|---|
| [DuetControlServer](src/DuetControlServer) | the SBC, managed | Interprets G-code, owns the object model, decides what a move means |
| [DuetSbcInterface](src/DuetSbcInterface) | the SBC, native | Plans motion, holds the segment chain and the DDA ring |
| [DuetCANMaster](src/DuetCANMaster) | the Duet 3 main board | Bridges SPI to CAN, and is close enough to the bus to stop a move in time |
| [Duet3Expansion](src/Duet3Expansion) | each expansion board | Owns the pins and the drivers |

Two consequences run through everything else.

**Only the CAN path is ported.** DuetControlServer is the sole main board, and every driver, heater,
fan and sensor lives on a CAN-connected expansion board. RepRapFirmware branches on local versus
remote hardware throughout; here every `#if SUPPORT_CAN_EXPANSION` branch is the one that is kept and
the local-hardware branch is dropped. In practice this makes the ports *smaller* than the originals.
What it removes:

| Dropped | Why |
|---|---|
| `M954` configure as expansion board | DSF is always the main board |
| `M576` SPI communications parameters | The DSF/RRF SPI link is gone |
| `M970` phase stepping | RRF refuses it for any remote driver, because the mode drives the coils from the main board. Every driver is remote, so the code answers `Phase stepping is not supported on CAN-connected drivers` rather than pretending to configure it |
| Local stall detection (`HAS_STALL_DETECT`) | Board 0 has no drivers |
| Local heater, driver and filament faults | They arrive from the board as CAN events instead, which is the same event by a different door |

**A port name has to name an expansion board.** Board 0 runs DuetCANMaster and owns no pins, so
`io1.in` and `0.io1.in` — the same port named by omission and named explicitly — are both refused,
with the reason travelling with the refusal rather than being composed by the caller. RepRapFirmware
has no equivalent rule because its main board does have ports. One function,
[IoPorts.RemoveBoardAddress](src/DuetControlServer/Link/IoPorts.cs), knows the grammar and
`TrySplitPort` is the policy on top of it, because two readers of one grammar diverge silently and
did: a normally-closed endstop on an expansion board, which is how most machines are wired, was
rejected outright.

---

## 2. There is no step interrupt, so the endstop path is a different shape

RepRapFirmware generates the steps, so it knows the instant a switch fired and where every drive was
at that instant, and it can act on both inside one interrupt. Nothing here can. Every item in this
section is what that costs.

**The stop is decided on the controller.** An axis at 100 mm/s covers a millimetre every 10 ms, so a
round trip out to DuetControlServer and back would overrun the switch visibly. DuetCANMaster matches
an incoming input change against the move's own per-driver stop inputs and stops the drivers itself,
needing no notion of what an endstop is. The change is still forwarded to DCS, because the object
model has to see it whether or not anything was moving.

**An endstop that is already closed is handled where the state is known.** The controller stops a
move when an input *changes*, and a switch that is already closed never changes. So DCS holds
`sensors.endstops[].triggered` and commands an axis that is already at its switch to stay where it
is. RepRapFirmware reaches the same place from the other direction — its step interrupt tests the
endstop before the first step — and the axis is latched as triggered by the arming code, because
nothing moved and no stop will ever be reported for it.

**Which endstops stopped a move is latched, not read back.** The wind-back unwinds the drives to
where they were at the trigger instant, which is the point at which the switch had *just* closed, so
reading the switch afterwards asks the question with the axis balanced on the threshold.
[MovementState.EndstopsTriggered](src/DuetControlServer/Motion/MovementState.cs) accumulates the
stopped drives as each stop is reported, which is RRF's `ms.endstopsTriggered` — the same answer, and
the only one that works for probe and stall endstops, which report under handles that never touch
`sensors.endstops[].triggered` at all.

**The step clock has to be shared.** Moves are scheduled by absolute time in the controller's step
clock and a stop report carries the tick the switch fired at, but the SBC has no such counter.
`StepTimer` fits a linear model of the controller's clock onto `CLOCK_MONOTONIC`, disciplined by a
reading in every SPI transfer header rather than in a packet, because what the fit rests on is the
pairing between the reading and the local time it is stamped with. Until the fit is trusted, the
trigger timestamp is ignored and the drives are corrected to where the report found them — the
overshoot the timestamp exists to remove is a small error, and an unsynchronised clock gives a
position with no relation to where the move stopped. `M122` reports whether the clock is synchronised
and what the movement delay is, because nothing else shows either and an unfitted clock breaks
nothing until an endstop fires.

**The stop and the position update are not the same event, so both ends of that window are closed.**

| Rule | Why RepRapFirmware needs no equivalent |
|---|---|
| A concluded move refuses a stop report that arrives late, counted as `too late` in `M122` | Its stop and its position update are one interrupt |
| A move that has seen no stop waits 50 ms for one before concluding | Same |
| A stop may only correct a drive the move actually armed, counted as `unarmed` | A report resolving to any other drive means the two sides disagree about driver numbering, which cannot happen inside one program |
| The report quotes the move id it belongs to | A report for move 6 arriving after move 7 armed would otherwise be applied to move 7, whose drives are usually the same ones |
| The move is held complete for `TotalDriverPositionRevertMillis` after the reverts go out | RRF waits for the same thing through `CanMotion::RevertStoppedDrivers`, but its ring counters can see the corrective move; here the boards synthesise it, so the SBC's counters know nothing about it |

One race is inherent rather than fixed: if a switch fires within a transfer or two of the move's
natural end, standstill can be reached before the correction arrives. An axis that triggered is still
put right, because its coordinate comes from its switch; an axis in the same move that did not
trigger keeps its planned endpoint.

### 2.1 The three stop actions cross a wire

RepRapFirmware decides `stopAxis` / `stopDriver` / `stopAll` inside `GetResult` and acts on the answer
immediately. Here the decision has to be expressible to a program on another board, so it is a field:
`ScheduleMoveDriver` carries a `stopGroup` and a `StopAction`, and the rules that read them live in
[StopRules.h](lib/DuetSpiInterface/include/DuetSpiProtocol/StopRules.h) — a leaf header both builds
compile, so the controller's own watch array *is* the tested type rather than a copy of it. The
escalation from `driver` to `group` on the last motor of a set, which is RRF's `Acknowledge`
decrementing `numDriversLeft`, belongs to the controller because it is the side that knows how many
motors are still running.

Two differences fall out of having named the group:

- **A stall watch names the driver's own board.** A driver can only ever be stopped by its own stall,
  so the native builder ignores the move's board list for a `typeStallEndstop` handle. The
  round-robin that hands an axis' switches out across the drives is right for switches and wrong for
  stalls.
- **Any number of disjoint coupling sets can be homed in one move.** On a CoreXYUV, `G1 H1 X100 U100`
  homes both pairs: X's endstop stops `{X, Y}` and U's stops `{U, V}`. RepRapFirmware accepts the
  same move and half-homes it — both endstops get `stopAll`, so whichever switch closes first stops
  every drive, U and V stop wherever they are, and no error is reported. Two axes that share a drive
  are refused here, naming the drive they collide on, because a drive carries one watch and the
  second axis could never reach its endstop in that move.

### 2.2 A motor-stall Z probe works, which RepRapFirmware's does not

`M558 P10` probes by driving Z into the bed until its motors stall. In RepRapFirmware the probe
object is a stub — no port, `SetProbing` returns `true` — and the detection lives in
`ZProbe::GetReading`, which for `zMotorStall` reads
`GetAxisDriversConfig(Z_AXIS).GetLocalDriversBitmap()`. That bitmap is filtered to `IsLocal()`
drivers, and nothing anywhere arms a *remote* stall endstop for a probe. So on a machine whose
drivers are all on CAN boards — which is every machine here — RepRapFirmware's motor stall probe
reads an empty bitmap and can never trigger.

Rather than port a feature that cannot fire, the probing move arms the drivers, exactly as a
stall-homed axis does: `StallArming` tells each driver that moves Z what speed to expect before the
tap and releases it afterwards, the move watches the board-wide stall handle with an action of
`all`, and the drivers to arm come from the same `EndstopPlanner.DriversMoving` a stall-homed axis
uses — so on a CoreXZ or a delta the probe watches every motor that brings Z down, not only the one
called Z.

Two consequences are worth knowing. The speed is per tap, because it is what the driver compares the
back-EMF against and `M558 F` may give the taps different speeds. And "was the probe triggered?" is
answered from `MovementState.EndstopsTriggered` rather than from `sensors.probes[].value`: nothing
writes a reading for a stall, because it is not an input on a pin. The latch is cleared before each
tap so it can only describe that one, and the "already triggered before the move started" check is
skipped, since a stall is a judgement about a move that is running and there is nothing for it to be
already triggered by.

---

## 3. The object model has to be able to recreate the machine

RepRapFirmware generates its object model on demand: `GetObjectValue` walks a lookup table at read
time, so the reported value cannot be stale and settings the firmware keeps in its own variables cost
nothing to omit. DSF's object model is a materialised tree that is diffed and patched out to clients,
and it is the only description of the machine that survives a restart. So the rule here is stronger
than RRF's: **sending a setting to a board is not storing it.** If the process restarted and had to
rebuild the machine from `model` alone, whatever would be lost belongs in the object model.

That adds fields RepRapFirmware does not keep, does not report, or both:

| Field | Why |
|---|---|
| `fans[].port`, `spindles[].port`, `state.gpOut[].port`, `heat.heaters[].port` and `.frequency` | A device whose board is forgotten cannot be driven after a restart. Each of these hid behind an address derived from something else — a fan's from a dictionary, a heater's from its sensor |
| `sensors.probes[].port` and `.sensor` | RRF keeps the temperature-compensation sensor on the probe but neither reports nor saves it, so a machine using it could not be recreated |
| `boards[].drivers[].config` — direction, mode, timings, thresholds | M569 forwarded everything over CAN and stored nothing. The object model already said this was wrong: `DriverConfig` is documented as "Configured (M569) settings of a driver" |
| `boards[].drivers[].config.stallDetection` | M915 had no home at all |
| `move.extruders[].pressAdv.K1` and `.D` | The CAN message carries only the first coefficient, so the second and its transition speed are held here alone — which is exactly what the rule is for |

Three further differences follow from the same rule.

**The kinematics engine is authoritative and the object model is a projection of it.** Kinematics is
the one place where two objects genuinely have to exist: `KinematicsEngine` holds precomputed state —
a delta's tower positions, a SCARA's arm-length squares, a core geometry's inverted matrix — that the
object model does not and should not carry. When two representations exist, one has to be
authoritative, and a hand-written translation *into* the planner is a coverage problem: `M669 S`/`T`
was write-only for exactly that reason, changing the object model, changing nothing about how the
machine moved, and reporting success. Each engine now owns `Configure` / `WriteTo` / `AppendReport`
next to its transforms, reports come from the authoritative side, and
[KinematicsRoundTripTests](src/UnitTests/Motion/KinematicsRoundTripTests.cs) asserts that configuring
and projecting loses nothing. RRF needs none of this because its projection is generated at read time.

**Axes and extruders stay object-model authoritative, deliberately.** `move.axes[]` is a flat list of
scalars with no derived state and nothing else holds a copy of it, so inverting it would mean adding
a second representation that can drift in order to solve a problem that does not exist. The ownership
is mixed on purpose.

**`MotionParameters` is a snapshot RepRapFirmware has no equivalent of.** It exists because two
consumers — the endstop correction and the live position publisher — run with no object model lock and
cannot take one, and because the planner needs dense `float[NumDrives]` arrays it would otherwise
materialise on every move. Nothing in it is authoritative and nothing is a second copy of a setting; a
divergence between it and the object model refuses the move rather than being clamped away.

### 3.1 Where DSF refuses what RepRapFirmware accepts

| Refusal | RepRapFirmware |
|---|---|
| `M584` naming one driver on two drives | Checks only that the driver exists, then lets the two owners fight over its steps |
| A driver claimed by an axis *and* an extruder | Same. Here the first claim wins — axes are walked before extruders — and any further claim is recorded and logged, because last-writer-wins was a real fault that corrupted the reverse map |
| `M669 K6` with parameters | Hangprinter's anchors need array handling the other geometries do not, so it says so rather than accepting the parameters and behaving as something else |
| Homing two axes whose coupling sets overlap | See §2.1 |

And one thing DSF allows that RRF does not: **`M584 X` releases the drivers of X.** RRF never shrinks
its axis count, so a drive refused a driver had no way to give one up. The axis stays in `move.axes[]`
— positions and axis indices do not move — but owns nothing until it is mapped again.

---

## 4. Events

The whole event system lives in DuetControlServer. The consumer is the AutoPause channel, and that
channel is here; leaving `Event.cpp` on the controller would have left a queue nothing drains and two
producers whose events could never reach the macros named after them. The controller's copy, its
board-timeout sweep and its diagnostics line are deleted rather than kept beside the ports.

| Difference | Detail |
|---|---|
| **Two event types RRF does not have** | `controller_disconnect` and `controller_reconnect`, raised when the SPI link to DuetCANMaster drops and returns, running `sys/controller-disconnect.g` and `sys/controller-reconnect.g`. They are numbered **128 and 129**, outside CANlib's range, so an upstream addition can never silently collide |
| **Priority is its own property** | In RRF the priority *is* the enum value, which works because RRF owns the numbering. Values 0-10 are fixed here by expansion firmware already in the field, so pinning priority to them would sort the two most consequential events last. The schema carries a `priority` per value and the generator emits the lookup beside the enum |
| **`controller_reconnect`'s default action runs `config.g`** | The largest deliberate departure in the events work. A rebooted controller has lost every setting, so *something* must reconfigure it, and a macro a machine can delete is not a safe home for that. When `controller-reconnect.g` exists it replaces the default entirely, as every other event's macro does, and is expected to call `M98 P"config.g"` itself |
| **`controller_disconnect` logs only** | A pause would need the link that just failed, and the job has already been aborted by the invalidation that precedes the event |
| **The queue is capped at 64** | RRF's producers are interrupt-driven and bounded by the similarity rule; `M957` is not. The *lowest-priority* entry is dropped with a warning naming it, because a queue that silently truncates reads as one that kept up |
| **The board watchdog moved rather than being duplicated** | Nothing on the controller read the `TimedOut` state it produced and the event it raised had no consumer there, while DCS already receives every message the sweep is derived from. Two timers that can disagree about whether a board is alive is the outcome that was avoided |
| **`M957` may raise the link events** | RRF validates the type and nothing further, and keeping that is what makes `controller-disconnect.g` testable without pulling a cable. It raises the *event* only — nothing touches the link — so a simulated disconnect runs the macro against a live machine |

---

## 5. Interpreter and move path

**An unrecognised code is an error, not a pass-through.** There is nowhere to forward it to. A code
no handler claims first looks for `sys/<letter><number>.g`, exactly as RRF's `TryMacroFile` does, and
only then replies `<code>: Command is not supported` — as a *warning*, in RRF's wording, because
macros and user interfaces have been reading that string for years. The practical consequence is
worth knowing: a config.g written for RepRapFirmware will report errors for every code not yet
ported, which is the point rather than a regression.

**`M290` babystepping takes effect on the next move.** RRF pushes the change into moves already
queued (`DDARing::PushBabyStepping`), so it is felt immediately; here it is applied as each move is
built, so it waits for the look-ahead to drain. It is the one user-visible difference in the
babystepping port and is marked at the point of use.

**`LimitPosition` is not re-applied per segment.** RRF applies it inside `ReadMove`, which is also
where it generates *arc* segments — and an arc's intermediate points are not on the line between its
ends, so they can leave the reachable region when the endpoints do not. A straight move's segments all
lie on a line whose ends have already been limited, so there is nothing left to find. It goes back in
with arcs.

**A move's interpolation base is derived, not stored.** RepRapFirmware keeps `ms.initialCoords` and
the ring's `startCoordinates` as two arrays with different transforms baked into them. Collapsing them
into one array put the previous move's mesh correction into the base every segment was interpolated
from — nearly two corrections high on the first segment of every printing move with a height map
loaded. The fix was not to keep a second copy but to evaluate the base through the same forward
transform the target goes through, so a term added to the transform reaches both ends of the line at
once. A stored copy would have needed updating in five places and been one forgotten line from the
bug it was meant to fix.

**Segments are emitted outside the planner lock, one channel at a time.** Giving the ring up is the
point of segmenting a long move, so the loop cannot hold a lock across the wait; instead
`MovementState.SegmentsLeft` — RRF's `ms.segmentsLeft` — is claimed when the move is *built* and
released in a `finally`, so a second channel cannot measure from a `StartCoordinates` part-way through
the first move.

**Smaller ones, each with a reason:**

| Difference | Reason |
|---|---|
| `G93` inverse time is carried as `RawMove.DurationSec` | Deliberately not a second meaning for `FeedRateMmPerSec`; the quantity is a duration, which is also why the speed factor divides rather than multiplies |
| `inputs[].feedRate` holds the raw `F` value | The inch conversion depends on which axes the move mentions, which is not known when `F` is read — and this is what the field's documentation already said |
| M906, M913 and M917 are one handler; M665, M666 and M669 are another | They differ only in which quantity they address, which is true in RRF too. Which parameters mean what is the geometry's business |
| `M669 K` numbers are mapped explicitly | RRF's `KinematicsType` is ordered differently from the object model's `KinematicsName`, and the mapping is part of the interface a config.g depends on |
| Rotary delta and five-bar SCARA are built with defaults | Nothing in the object model configures them yet, so the engines take RRF's and the M669 documentation's values rather than inventing a home for the settings |
| Hangprinter uses the constant-spool-radius model | Line buildup and flex compensation need `move.kinematics` fields that do not exist; this is the branch RRF itself takes when the buildup factor is zero |
| The interpreter state stack is capped at 10, macro nesting at 10 | RRF caps its own stack; a macro looping over `M120` without `M121` would otherwise grow it without bound |

---

## 6. Meta G-code and expressions

**A variable cannot hold an object model reference.** `var a = move.axes` and `global a = move` are
refused, where RepRapFirmware stores pointers into its own model and lets `var.a[0].letter` work.
Holding such a reference here means holding a model object that the update task mutates and that a
reconfiguration — `M584`, or the invalidation a lost link performs — can detach from the model
entirely, so the variable would read stale values rather than failing. A `global` could not hold one
at all, because globals are serialised for clients. What fits instead is a *symbolic* reference: store
the path and resolve it on each read, so it serialises, holds no lock across time, and cannot go
stale. That is not built yet, and it would diverge from RRF only where RRF is arguably wrong.

**An expression that cannot be produced is an error, not a null.** `cannot evaluate '<expression>'`
rather than a value that reads as a valid answer, which is how two whole branches of the object model
being unresolvable stayed invisible.

**A collection of model objects is refused where a collection of scalars is not.**
`move.axes[0].workplaceOffsets` resolves as an array, copied under the read lock;
`move.axes` says so rather than handing out live elements the update task mutates.

---

## 7. Dropped by build switch, not by divergence

For a given set of move parameters the SBC-side DDA ring produces the same output RepRapFirmware's
does — `DDA::InitFromParams` onward is upstream verbatim. What is absent is absent by a switch or a
deletion, and it is worth separating the permanent from the pending:

| Dropped | Status |
|---|---|
| `SUPPORT_S_CURVE 0` — trapezoidal profiles only | Permanent for now; `DDA_3rdOrder` and `MovementProfile` are not ported |
| `SUPPORT_LASER 0`, `SUPPORT_IOBITS 0` | Pending — both need machine mode and a wire-format field |
| `SUPPORT_NONLINEAR_EXTRUSION 0` | Pending — M592 |
| `DDARing::PushBabyStepping` | Deliberate, see §5 |
| `DDARing::PauseMoves`, `LowPowerOrStallPause` | Pending — follows restore points and pause/resume |
| `DDARing::AddSpecialMove` | Pending — bed levelling and leadscrew adjustment moves |

---

## 8. What is deliberately *not* different

The divergences above work inside constraints that were treated as fixed, and they are worth stating
because they explain why several of the entries are shaped the way they are:

- **Reporting strings are preserved exactly.** DWC, PanelDue and a decade of macros parse them, so a
  code with no parameters reports in RepRapFirmware's format down to the wording of the punctuation —
  including the segmentation clause on `M669` and the SCARA report, both of which were added back
  when they were found missing.
- **The event macro contract is preserved exactly.** The filename convention, `param.D/B/P/S`, and
  the rule that a macro replaces the default action entirely rather than running alongside it.
- **Wire values are preserved exactly.** `EventType` 0-10, the CAN message layouts, and
  `CanMessageRevertPosition.finalStepCounts` meaning the same quantity on both sides — which is why
  moving the endstop correction out of native and into DCS was a layering change with no behavioural
  one.
- **Error wording follows RepRapFirmware** where RRF has a message for the same situation, down to
  `Probe was not triggered during probing move`.

---

## Further reading

- [Endstops](endstops.md) — the endstop path as it stands, in the order it runs
- [MCODE_MIGRATION.md](docs/devel/MCODE_MIGRATION.md) — the M-code inventory, the porting contract, and the
  reasoning behind §2, §3 and §5 above
- [EVENTS_MIGRATION.md](docs/devel/EVENTS_MIGRATION.md) — the event system, and §7 there for the decisions
  summarised in §4 above
- [STALL_DETECTION.md](docs/devel/STALL_DETECTION.md) — stall homing, and where §2.1's stop groups came from
