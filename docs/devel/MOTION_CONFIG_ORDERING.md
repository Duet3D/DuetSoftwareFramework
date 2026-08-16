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

Both are in the tree, and the split does not match RepRapFirmware's:

| Code | RRF waits for standstill | DSF waits for standstill | |
| --- | --- | --- | --- |
| M92 steps/mm | yes | yes | agrees |
| M584 driver mapping | yes | yes | agrees |
| M201 accelerations | yes | no | see §2 — needs neither |
| M203 max feedrates | no | no | but applies retroactively today |
| M205 / M566 jerk | no | no | but applies retroactively today |
| M592 nonlinear extrusion | no | no | but applies retroactively today |
| **M425 backlash** | no | **yes** | regression |
| **M572 pressure advance** | no | **yes** | regression |
| **M593 input shaping** | no | **yes** | regression |

So there are two defects, not one. Three codes stop the machine where RRF does not — and the ones
that do *not* stop it are quietly wrong, because overwriting the live configuration applies the new
value to every move that is queued but not yet prepared. In the example above, DDA 1 and 2 would be
extruded with the new pressure advance if they had not yet been prepared, which is the same bug the
standstill was added to avoid.

---

## 2. Where each value is actually consumed

The fix has to be per-value, because the values are not read at the same point in the pipeline. Every
read of `MotionConfig` from the planner sits in one of two places:

| Consumed in | Values | When that is, relative to the G-code |
| --- | --- | --- |
| **Add time** — `InitFromParams`, `RecalculateMove`, `MatchSpeeds`, `DoLookahead` | `jerkPolicy`, `instantDvs`, `printingInstantDvs` | as the move is queued |
| **Prepare time** — `DDA::Prepare` | `pressureAdvanceClocks`, `nonlinearExtrusion`, `backlashSteps`, `backlashCorrectionDistanceFactor`, `shapingTimeClocks` | ~50 ms before the move runs |
| **Prepare time, but not safely changeable** | `driveStepsPerMm`, `axisDrivers`, `extruderDrivers`, `numTotalAxes`, `numExtruders`, `continuousRotationAxes`, `controllingDrives` | — |

That third row is the answer to "are there any that must wait for standstill". Yes, and it is exactly
the set that changes what an already-queued move *means*. A DDA holds endpoints in microsteps that
DuetControlServer computed under the old steps/mm, and `Prepare` turns them into driver steps by
differencing against the previous DDA's endpoints. Change steps/mm between those two and the
difference is not a distance any more. No ordering rescues that, so M92 and M584 keep the flush.

**M201 and M203 need nothing.** Acceleration and requested speed are not in `MotionConfig` at all —
DuetControlServer works them out per move and sends them in `MoveParamsHeader`. Every move already
carries its own, so they are ordered by construction. The fact that DSF does not lock for M201 where
RRF does is therefore not a weakness; it is a consequence of the split, and worth recording as such
rather than "fixing".

### The two application points

Because there are two consumption points there must be two application points, and this is the part
that is easy to get subtly wrong:

- An **add-time** value applied at prepare time would land too late — the move it was supposed to
  affect was planned before it arrived.
- A **prepare-time** value applied at add time would land too early — it would reach moves that are
  already in the ring and have not been prepared yet. That is precisely today's bug.

---

## 3. Proposed mechanism

### 3.1 Configuration splits in two

`MotionConfig` becomes two structs, so which update path a field takes is visible in the type rather
than remembered:

```
struct MachineConfig     // pushed whole, requires standstill
{
    numTotalAxes, numExtruders, numVisibleAxes
    numRings, numDdasPerRing
    driveStepsPerMm[]
    axisDrivers[], extruderDrivers[]
    continuousRotationAxes, controllingDrives[]     // kinematics results
}

struct TuningConfig      // initial values pushed with the above; updated afterwards by delta
{
    instantDvs[], printingInstantDvs[], jerkPolicy
    pressureAdvanceClocks[], nonlinearExtrusion[]
    backlashSteps[], backlashCorrectionDistanceFactor
    shapingTimeClocks
    gracePeriodMs
}
```

`DuetSbc_MotionConfigure` pushes both at startup and whenever the machine half changes. After that,
only the tuning half moves, and it moves one field at a time.

### 3.2 Deltas travel in the move queue

This is the whole of the ordering guarantee, and it costs nothing: **a configuration change is a
record in the same submission ring the moves use**. `MotionService::DrainSubmissions` already
consumes that ring strictly in order, so a delta between move 2 and move 3 is dequeued between move 2
and move 3. There is no second queue to keep in step, and no sequence numbers to reconcile.

The ring currently carries only `MoveParamsHeader` records, so it needs a discriminator:

```
struct SubmissionHeader     // 4 bytes, in front of every record
{
    uint16_t type;          // Move | ConfigDelta
    uint16_t length;
};
```

**Decision D1.** The cheaper alternative is to spend the `moveId == 0` case, which `MoveParams.h`
already documents as never occurring, and treat such a record as a delta. That needs no change to the
`MoveParamsHeader` layout or its two layout tests. It is less honest — a reader has to know the
invariant to see the discriminator — so the explicit envelope is the recommendation, with the caveat
that it touches `MoveParamsLayoutTests` and the C# writer.

A delta is small and names exactly one thing:

```
struct ConfigDelta
{
    uint8_t  ring;          // which DDARing this is ordered into
    uint8_t  padding;
    uint16_t field;         // ConfigField enum
    uint16_t index;         // drive or extruder, or 0 for a machine-wide field
    uint16_t padding2;
    float    values[3];     // nonlinear extrusion needs three; the rest use one
};
```

Twenty bytes. `M572 D0 S0.02` is one record.

### 3.3 Add-time deltas apply on dequeue

`jerkPolicy`, `instantDvs` and `printingInstantDvs` are applied by `DrainSubmissions` the moment the
record is dequeued, before the next move is added. That is the correct point by construction.

**One property to be aware of rather than hide:** `DoLookahead` reaches *backwards* and re-plans
moves that are still provisional. A jerk change therefore also influences the re-planning of moves
queued before it. Making that strictly ordered would mean snapshotting the jerk limits into every
DDA, and RepRapFirmware has the same property, so this plan does not. It is recorded here so the next
person to look does not think it was missed.

### 3.4 Prepare-time deltas apply at the prepare cursor

The rest are held per ring and applied as preparation reaches them:

- `DDARing` gains a small FIFO of pending deltas, a monotonic `m_deltasQueued`, and `m_deltasApplied`.
- `DDA` gains `uint32_t m_deltaSeq`. `AddMove` stamps it with the ring's current `m_deltasQueued`.
- `PrepareMoves`, before preparing a move, applies pending deltas while
  `m_deltasApplied < dda.GetDeltaSeq()`.

Walking the example: DDA 1 and 2 are stamped 0. The delta is dequeued, pushed onto ring 0's FIFO, and
`m_deltasQueued` becomes 1. DDA 3 is stamped 1. When preparation reaches DDA 1 and 2 nothing is
applied, so they use the old pressure advance; when it reaches DDA 3, `0 < 1`, so the delta is applied
first and DDA 3 uses the new one. Which is the requirement, exactly.

Three cases the mechanism has to answer:

- **A delta with no move behind it.** `m_deltasApplied` would never catch up. `Spin` applies any
  outstanding deltas once the ring holds no provisional move — they were ordered after everything in
  it, so that point is correct and prompt.
- **The FIFO fills.** `DrainSubmissions` stops taking records and leaves them in the submission ring,
  the same backpressure a full DDA ring already gets. Never a silent drop.
- **Emergency stop.** `CancelStepping` abandons queued motion; pending deltas are applied rather than
  discarded, because DuetControlServer's object model already reports them as in effect.

### 3.5 Two rings

A delta names a ring because a per-drive value belongs to whichever motion system owns that drive, and
`SUPPORT_ASYNC_MOVES` gives two. The `MotionConfig` behind them is shared, which is safe: a drive is
owned by exactly one motion system at a time, so two rings can never hold conflicting pending deltas
for the same drive. A machine-wide field (`jerkPolicy`, `shapingTimeClocks`, `gracePeriodMs`) is
emitted as one delta per ring, so it is ordered correctly within each.

---

## 4. DuetControlServer side

- `ReconfigureAsync` splits into `ReconfigureMachineAsync` — the standstill path, for M92, M584 and
  the kinematics codes — and `QueueTuningChangeAsync(ring, field, index, values)`, which writes a
  delta into the same submission stream as moves.
- `HandlePressureAdvanceAsync`, `HandleBacklashAsync` and `HandleInputShapingAsync` lose their
  `FlushAndWaitForStandstillAsync`, which is what removes the regression against RRF.
- `HandleJerkAsync`, `HandleMaxFeedratesAsync` and `HandleNonlinearExtrusionAsync` keep their absence
  of a flush but stop being retroactive, because they now emit deltas instead of overwriting.
- The object model stays the source of truth and is written first, as now; the delta is what carries
  the change down. Nothing keeps a second authoritative copy.
- Which ring a drive belongs to comes from the movement state DCS already tracks for `ringNumber` on
  a move.

---

## 5. Verification

The offline `DdaRingTests` can prove the property directly, with no hardware: queue two moves, queue a
pressure-advance delta, queue a third move, spin, and assert that the `ScheduleMove` packets for the
first two carry the old extrusion and the third the new. That test is the reason to prefer this design
over anything relying on timing — the guarantee is checkable rather than probable.

Worth adding alongside:

- a delta arriving with an empty ring takes effect promptly;
- a full delta FIFO reports backpressure instead of dropping;
- an add-time delta (jerk) changes the planning of the move after it and not the move before it;
- the layout tests extended to the new envelope and delta records, on both sides.

---

## 6. Order of work

| Step | Content | Risk |
| --- | --- | --- |
| 1 | Split `MotionConfig` into `MachineConfig` + `TuningConfig`, both still pushed whole under standstill. No behaviour change; this is the ABI move, with both layout tests updated. | Low, but touches the C# mirror |
| 2 | Submission envelope and the `ConfigDelta` record; native drains and applies deltas, add-time first since it needs no ring changes. | Low |
| 3 | The per-ring FIFO, `m_deltaSeq`, and application at the prepare cursor. The `DdaRingTests` case above lands with this. | Medium — this is the part that is subtly wrong if it is wrong |
| 4 | DCS: split `ReconfigureAsync`, move the six handlers onto deltas, drop the three flushes. | Medium — user-visible; M572 mid-print is the thing to try on hardware |
| 5 | Record in `rrf-differences.md` only what ends up permanently different from RRF, which on current reading is nothing: the aim is to match RRF's standstill behaviour and be strictly ordered where RRF is approximately ordered. | Low |

---

## 7. Decisions

| | Question | Recommendation |
| --- | --- | --- |
| **D1** | Discriminate submission records with an explicit 4-byte envelope, or by spending the documented `moveId == 0` case? | Explicit envelope. It costs an ABI change and two layout tests; the alternative hides the discriminator behind an invariant a reader has to already know. |
| **D2** | Strict ordering for add-time values too, by snapshotting jerk limits into each DDA? | No. Lookahead re-plans backwards, so the snapshot would have to cover moves already queued, and RRF does not do this either. Recorded in §3.3 instead. |
| **D3** | Should a delta whose ring is idle apply immediately, or wait for the next move? | Immediately, once the ring holds no provisional move (§3.4). Waiting would leave M572 with no visible effect until the next move arrived, which is worse than either current behaviour. |
| **D4** | Keep `numVisibleAxes` in `MachineConfig`? It has had no native reader since `GCodesShim` was deleted. | Drop it. It is one field, and carrying a value nothing reads is what this pass has been removing. |
