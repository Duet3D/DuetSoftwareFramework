# Changing motion configuration without stopping the machine

Plan for applying motion configuration changes in order with the moves around them, instead of
pushing the whole machine description and waiting for standstill.

The worked example throughout is:

```gcode
G1 X100 E1      ; DDA 1
G1 X200 E1      ; DDA 2
M572 D0 S0.02   ; pressure advance for extruder 0
G1 X300 E1      ; DDA 3
```

DDA 1 and 2 must be executed with the old pressure advance and DDA 3 with the new one, without the
machine ever coming to a stop.

---

## 1. What happens today

`MovePlanner.ReconfigureAsync` serialises the **whole** `MotionConfig` and `MotionSystem::Configure`
memcpys it over the live one. There is one update path and it is all-or-nothing, so every caller has
to choose between two wrong answers: stall the print, or overwrite configuration that queued moves
are still going to read.

Both are in the tree:

| Code | RRF waits for standstill | DSF waits for standstill | |
| --- | --- | --- | --- |
| M92 steps/mm | yes | yes | agrees |
| M584 driver mapping | yes (`DoDriveMapping`) | yes | agrees |
| M201 accelerations | no¹ | no | agrees |
| M203 max feedrates | no | no | but applies retroactively today |
| M205 / M566 jerk | no | no | but applies retroactively today |
| M592 nonlinear extrusion | no | no | but applies retroactively today |
| **M425 backlash** | no | **yes** | regression |
| **M572 pressure advance** | no | **yes** | regression |
| **M593 input shaping** | no | **yes** | regression |

¹ M201 locks only for its `T` parameter, and only inside `#if SUPPORT_3RD_ORDER` — the S-curve
acceleration time, which is not ported. For everything M201 actually does here, RRF takes no lock.

So there are two defects, not one. Three codes stop the machine where RRF does not — and the ones
that do *not* stop it are quietly wrong, because overwriting the live configuration applies the new
value to every move that is queued but not yet prepared. In the example above, DDA 1 and 2 would be
extruded with the new pressure advance if they had not yet been prepared, which is the same bug the
standstill was added to avoid.

---

## 2. Where each value is consumed

This is what decides the design, because the values are not read at the same point in the pipeline.
Every read of `MotionConfig` from the planner sits in one of two places:

| Consumed in | Values | When that is, relative to the G-code |
| --- | --- | --- |
| **Add time** — `InitFromParams`, `RecalculateMove`, `MatchSpeeds`, `DoLookahead` | `jerkPolicy`, `instantDvs`, `printingInstantDvs` | as the move is queued |
| **Prepare time** — `DDA::Prepare` | `pressureAdvanceClocks`, `nonlinearExtrusion`, `backlashSteps`, `backlashCorrectionDistanceFactor`, `shapingTimeClocks` | ~50 ms before the move runs |
| **Prepare time, and not safely changeable** | `driveStepsPerMm`, `axisDrivers`, `extruderDrivers`, `numTotalAxes`, `numExtruders`, `continuousRotationAxes`, `controllingDrives` | — |

Any scheme that updates a *shared* configuration has to hit two different application points, because
a prepare-time value applied at add time reaches moves already in the ring — today's bug — and an
add-time value applied at prepare time lands after the move it should have shaped was planned.

**The third row is the answer to "are there any that must wait for standstill".** Yes, and it is
exactly the set that changes what an already-queued move *means*. A DDA holds endpoints in microsteps
that DuetControlServer computed under the old steps/mm, and `Prepare` turns them into driver steps by
differencing against the previous DDA's endpoints. Change steps/mm between those two and the
difference is not a distance any more. No ordering rescues that, so M92 and M584 keep the flush.

**M201 and M203 already need nothing.** Acceleration and requested speed are not in `MotionConfig` at
all — DuetControlServer works them out per move and sends them in `MoveParamsHeader`. Every move
carries its own, so they are ordered by construction and no code has to remember to synchronise
anything.

That last row is the whole idea, and it already works.

---

## 3. The design: tuning travels with the move

Extend what `maxAcceleration` and `requestedSpeed` already do. **Every move carries the tuning values
it is to be executed with**, so the configuration a move uses is fixed at the moment
DuetControlServer builds it and cannot be changed afterwards by anything.

Applied to the example: DDA 1 and 2 are built while pressure advance is 0.0 and carry that; M572
writes the object model; DDA 3 is built afterwards and carries 0.02. Nothing has to be applied at any
cursor, because nothing is shared.

### What this removes

Everything an ordered-delta scheme would need:

- no delta record type, and no envelope to discriminate it from a move in the submission ring;
- no per-ring FIFO of pending changes, no `m_deltaSeq` on the DDA, no application at the prepare
  cursor;
- **no two application points** — the add-time/prepare-time distinction in §2 dissolves, because the
  value is in the DDA from the moment it is created and is read from there at whichever point needs
  it;
- no ring routing, and no question about what a machine-wide value means when there are two rings —
  a move belongs to one ring and carries its own values;
- no "a change arrived and no move followed it" case, because a change with no following move affects
  no move, which is the correct answer rather than an edge case to handle;
- no `ReconfigureAsync` call from the six tuning codes at all. They write the object model and stop.

### What it costs

`MoveParamsHeader` and its per-drive arrays grow. Per drive, the values a move needs are
`instantDv`, `printingInstantDv`, `pressureAdvanceClocks`, `backlashSteps`, and nonlinear
extrusion's three coefficients; on the header, `jerkPolicy`, `shapingTimeClocks` and
`backlashCorrectionDistanceFactor`.

| | now | with tuning |
| --- | --- | --- |
| header | 28 B | 40 B |
| per drive | 22 B | 50 B |
| record at 32 drives | **732 B** | **1640 B** |

About 2.2×. In absolute terms that is small: the submission ring is process-local memory, not link
bandwidth, so this is a memcpy of 1.6 KB per move — a couple of MB/s even at a thousand moves a
second. The ring holds ~350 records today and would hold ~156, which is a burst absorber rather than
a queue-depth guarantee and is already covered by backpressure; raising it to 512 KB restores the
headroom if it is ever wanted.

An axis never needs the extruder values and an extruder never needs `backlashSteps`, so the two can
share storage in a union and take the per-drive figure to 46 B. Worth doing, not worth complicating
the layout further for.

### What it makes explicit

Junction limits are a property of a *pair* of moves, not of one: `MatchSpeeds` and `RecalculateMove`
compare move N against move N+1. With a shared configuration, whichever value happened to be live
when lookahead ran was used, and that was never written down. Carrying the value per move forces the
rule to be stated:

> **The later move of a junction governs.** `MatchSpeeds` and `RecalculateMove` read the tuning of
> the move being added or re-planned, not of its predecessor.

That is what reading the live configuration at add time already amounted to, so it is not a change in
behaviour — it is the same behaviour, now testable.

The one property this does not fix is that `DoLookahead` reaches backwards and re-plans moves that are
still provisional, using their own carried limits. That is correct and is what RRF does; it is
recorded here so nobody reads it as an oversight.

---

## 4. What is left in the pushed configuration

`MotionConfig` splits, so which update path a field takes is visible in the type rather than
remembered:

```
struct MachineConfig      // pushed whole; requires standstill
{
    numTotalAxes, numExtruders
    numRings, numDdasPerRing
    driveStepsPerMm[]
    axisDrivers[], extruderDrivers[]
    continuousRotationAxes, controllingDrives[]     // kinematics results
    gracePeriodMs                                   // ring behaviour, not a move property
}
```

Everything else moves onto the move. `numVisibleAxes` goes entirely: it has had no native reader
since `GCodesShim` was deleted.

`gracePeriodMs` is the one field that is neither. It governs how long a ring waits before committing
its first move, which is not a property of any move. It is also harmless to change at any time, so it
stays in the pushed half and needs no standstill of its own — worth saying explicitly, because
"pushed whole" and "requires standstill" stop being the same statement.

---

## 5. DuetControlServer side

- `MoveBuilder` copies the tuning values into each move it builds, from the `MotionParameters`
  snapshot it already holds. That snapshot is already refreshed from the object model, so there is
  one source of truth and no new plumbing.
- `HandlePressureAdvanceAsync`, `HandleBacklashAsync` and `HandleInputShapingAsync` lose their
  `FlushAndWaitForStandstillAsync` — this is what removes the regression against RRF.
- Those three, plus `HandleJerkAsync`, `HandleMaxFeedratesAsync` and `HandleNonlinearExtrusionAsync`,
  also lose their `ReconfigureAsync`: they write the object model and the next move carries the
  result. Six handlers get shorter.
- `ReconfigureAsync` narrows to the machine half and keeps its standstill, for M92, M584 and the
  kinematics codes.
- The snapshot has to be refreshed before the next move is built rather than on a timer, or a change
  would take effect some moves late. `MotionParameters.FromObjectModel` is already the refresh; what
  needs checking is that every tuning handler marks the snapshot stale.

---

## 6. Verification

The offline `DdaRingTests` prove the property directly, with no hardware and no timing: submit two
moves carrying PA 0.0, a third carrying 0.02, spin, and assert that the `ScheduleMove` packets for
the first two carry the old extrusion and the third the new. Because the value rides on the move,
this is a pure input/output test — there is no cursor or queue state to arrange.

Worth adding alongside:

- a move carrying different jerk limits from its predecessor is melded against its own, per §3's rule;
- backlash accumulator state survives a change of `backlashSteps` between moves without a step
  discontinuity;
- the layout tests on both sides extended to the grown record, including the axis/extruder union.

An end-to-end check on hardware is still wanted for §5: `M572 D0 S0.02` in the middle of a print,
confirming both that the machine does not pause and that the change lands on the right move.

---

## 7. Order of work

| Step | Content | Risk |
| --- | --- | --- |
| 1 | Split `MotionConfig` into `MachineConfig` and the tuning fields, both still pushed whole under standstill. No behaviour change; this is the ABI move, with both layout tests updated. | Low, touches the C# mirror |
| 2 | Grow `MoveParamsHeader` and the per-drive array with the tuning values; `DDA::InitFromParams` stores them; `Prepare` and the planning functions read them from the DDA instead of from `MotionSystem`. Tuning stays in the pushed config as the default a move inherits. | Medium — the mechanical heart of it |
| 3 | `MoveBuilder` fills them; drop the six handlers' `ReconfigureAsync` and the three flushes. | Medium — user-visible |
| 4 | Remove the tuning fields from the pushed config, and `numVisibleAxes` with them. | Low |

Steps 1 and 2 are behaviour-preserving and can land before anything user-visible does.

---

## 8. Decisions

| | Question | Recommendation |
| --- | --- | --- |
| **D1** | Tuning inline in every move, or a small table of tuning generations that a move indexes into with two bytes? | Inline. The index is ~900 bytes a move cheaper but brings back a lifetime question — when a generation may be reused — and an eviction failure mode under repeated changes, which is most of the complexity this design exists to avoid. `MoveParams.h` already made this trade once for the endpoint and direction arrays and wrote down why; this follows it. Revisit only if the record size is measured to matter. |
| **D2** | Which move's jerk limits govern a junction? | The later one (§3). It matches what reading the live configuration at add time already did. |
| **D3** | Keep `numVisibleAxes`? | Drop it — no native reader since `GCodesShim` went. |
| **D4** | Should `gracePeriodMs` stay in the pushed config? | Yes, and say that pushing it needs no standstill. It is ring behaviour rather than a move property, and it is safe to change at any moment. |
