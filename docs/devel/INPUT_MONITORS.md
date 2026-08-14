# Input monitors: plan to finish `CanMessageChangeInputMonitorV1`

DuetControlServer creates input monitors on expansion boards and never speaks to them again.
`CanMessageChangeInputMonitorV1` is generated, layout-tested, and never sent. Some of what it does
DSF genuinely does not need; some of what it does is missing and shows.

This plan sorts the eight actions into the three cases, fixes what is wrong, and writes down the
divergences so they are not mistaken for gaps again.

---

## 1. Where things stand

DCS sends `CanMessageCreateInputMonitorV1` from two places and nothing else:

- [`CreateProbeMonitorAsync`](src/DuetControlServer/Codes/Handlers/MCodeHandler.Probes.cs#L288), from
  `M558`
- [`CreateEndstopMonitorAsync`](src/DuetControlServer/Codes/Handlers/MCodeHandler.Motion.cs#L2241),
  from `M574`

Both are called once the object model already agrees with what is being asked for, and both derive
the board from the port string the user typed. Neither has a counterpart that changes or removes what
it created.

RepRapFirmware wraps each action in a named `CanInterface` call
([CanInterface.cpp#L1435](lib/RepRapFirmware/src/CAN/CanInterface.cpp#L1435) onwards), and there are
only six call sites in the whole of it. They are the evidence for everything below:

| RRF call | Action | Called from | DSF |
|---|---|---|---|
| `ChangeHandleThreshold` | 3 | `RemoteZProbe::SetProbing`, `SetTargetAdcValue` | **Gap** - §2 |
| `ChangeHandleResponseTime` | 4 | `RemoteZProbe::SetProbing` | **Gap** - §2 |
| `DeleteHandle` | 2 | `RemoteZProbe`, `SwitchEndstop`, `GpInPort` reconfigure | **Gap** - §3 |
| `EnableHandle` | 1 | `SwitchEndstop::PrimeAxis` | Divergence - §4 |
| `GetHandlePinName` | 5 | `SwitchEndstop::AppendDetails`, `RemoteZProbe`, `GpInPort` | Not needed - §5 |
| `ChangeHandleSetTouchMode`, `SetHandleDriveLevel` | 7, 6 | `RemoteZProbe`, `M558.2` | Out of scope - §6 |

`actionDontMonitor` is never sent by RepRapFirmware at all. `EnableHandle` has one caller and it
passes `true`.

---

## 2. A probe is never told anything after `M558`

RepRapFirmware pushes two things to the board every time probing starts and stops, in
[`RemoteZProbe::SetProbing`](lib/RepRapFirmware/src/Endstops/RemoteZProbe.cpp#L64):

```cpp
if (isProbing && (type == ZProbeType::scanningAnalog || type == ZProbeType::analog))
{
    rslt = CanInterface::ChangeHandleThreshold(boardAddress, handle, targetAdcValue, ...);
}
if (rslt == GCodeResult::ok)
{
    rslt = CanInterface::ChangeHandleResponseTime(boardAddress, handle,
                                                  (isProbing) ? ActiveProbeReportInterval : InactiveProbeReportInterval, ...);
}
```

DSF sends both once, in the `CreateInputMonitor` that `M558` builds, and never again. That produces
two distinct defects.

### 2.1 `G31 P` does not reach the board

`G31 P<threshold>` writes `probe.Threshold` into the object model
([GCodeHandler.Probes.cs#L85](src/DuetControlServer/Codes/Handlers/GCodeHandler.Probes.cs#L85)) and
stops there. `CreateProbeMonitorAsync` has exactly one caller, on the `M558` path, so nothing
re-sends it.

The two halves then disagree about the same probe:

- the **board** decides when to report a change, and therefore when the move stops, against the
  threshold it was given at `M558` time
- **DCS** decides whether the probe counts as triggered against `probe.Threshold` as it is now
  ([`IsProbeTriggeredAsync`](src/DuetControlServer/Codes/Handlers/GCodeHandler.Probing.cs#L434)), and
  `M119` reports it the same way
  ([`DescribeProbeState`](src/DuetControlServer/Codes/Handlers/MCodeHandler.Motion.cs#L2158))

So after `G31 P<new>` a probing move stops at the old threshold and is then judged by the new one.
Analog probes only - a digital probe is created with threshold zero, which is what tells the board to
read the pin digitally - but `G31 P` after `M558` is ordinary usage.

### 2.2 A probe reports at the probing rate permanently

`ProbeReportInterval` is 2 ms, which is RepRapFirmware's `ActiveProbeReportInterval`. RRF only uses
that value **while probing**, and puts the handle back to `InactiveProbeReportInterval` - 25 ms -
when it stops. DSF has no inactive rate at all.

An analog probe near its threshold changes reading constantly, so this is a CAN message every 2 ms
from every configured probe, for the entire time the machine is doing anything else. Twelve times the
traffic RRF would produce, on a bus that a move already shares.

### The fix

Give DSF the seam RRF has: something that runs when probing starts and stops, per probing move,
which pushes the threshold and the report interval. There is no such seam today - the arm/release
pair around a tap exists only for the motor-stall probe.

Details that decide its shape:

- **The board is derived from the port, so a probe with no port has nowhere to send it.** The same
  `WatchableProbePort` test that decides whether to create a monitor decides whether to change one: a
  probe of type none, a motor-stall probe, or one with no port has no monitor. A motor-stall probe
  matters here because it is the one Phase 7 of `STALL_DETECTION.md` added, and it has no input
  handle at all.
- **The threshold only means anything to an analog probe**, which is the condition RRF writes
  explicitly. `M558` sends zero for every other type on purpose, and sending a nonzero threshold for a
  digital probe would switch the board to analog reads and stop it reporting. The report interval, by
  contrast, is sent for every type.
- **It must be released however the probing move ends**, like the stall arming beside it. A probe
  left at 2 ms is the state DSF is in today, so a leak here is not a regression, but it is the whole
  point of 2.2.

Doing it this way fixes 2.1 without touching `G31` at all: the threshold is read when probing starts,
so whatever last wrote it - `G31 P`, `M558`, a restored configuration - is what the board is given.
That is why RRF has no `G31`-side CAN call either.

---

## 3. An abandoned monitor keeps its pin

Nothing ever deletes a monitor. The board keeps reporting the pin and keeps it claimed as
`PinUsedBy::endstop`, so reassigning that pin to a fan, a heater or a GPIO fails with the pin already
in use, and the board goes on reporting an input nobody reads. RRF deletes in all three of its
reconfigure paths.

Reconfiguring to a **different pin under the same handle** is already safe:
[`InputMonitor::Create`](src/Duet3Expansion/src/InputMonitors/InputMonitor.cpp#L317) deletes any
existing monitor with that handle before assigning the new port. The leak is only for handles that
are **abandoned** rather than reused:

| Code | What happens now |
|---|---|
| `M574 X0` | `EndstopPosition.None`: the slot is set to null and the `foreach` that creates monitors `continue`s past it ([Motion.cs#L2026](src/DuetControlServer/Codes/Handlers/MCodeHandler.Motion.cs#L2026)) |
| `M574 X1 S3` | switch to stall: `CreateEndstopMonitorAsync` returns early because the type is not `InputPin` |
| `M574 X1 S1 P""` | clearing the port, which `ValidateEndstopPorts` calls "how an endstop is given up" |
| `M574 X1 S1 P"a"` after `P"a+b"` | the removed switch's handle is abandoned while the axis keeps its endstop |
| `M558 P0` | type none: `WatchableProbePort` returns null and no create is sent |

RRF's third `DeleteHandle` caller is `GpInPort`, for `M950 J`. DSF has no `M950 J`, so it creates no
general-purpose input monitors and cannot leak one. Endstops and probes are the whole surface.

### The fix

Delete what the previous configuration created, before creating whatever the new one asks for.

The handles are all derivable, so **no registry of created monitors is needed**: an endstop's handles
are `RemoteEndstops.HandleFor(axis, switchIndex)` for each port in `PortsOf(endstop)`, and a probe's
is `RemoteProbes.HandleFor(probeNumber)`. What is needed is the **previous** value, which means
capturing the old ports inside the model lock before they are overwritten, in both handlers. Both
already write the model under the lock and then do their CAN work outside it, so this is one more
value carried across that boundary rather than a new shape.

Rules:

- Delete only handles the new configuration will not re-create. Deleting and re-creating the same
  handle would work, because `Create` deletes first anyway, but it doubles the CAN traffic of every
  `M574` and makes a failed create leave the axis with no monitor where it previously had one.
- A delete is sent to the board named by the **old** port, which need not be the board named by the
  new one.
- A board that refuses a delete is logged, not fatal. The monitor being gone is what the next
  `M950` needs, but the code that asked for it is `M574`/`M558`, and failing those because a stale
  handle could not be dropped would turn a tidy-up into a configuration error. A refused delete does
  leave the pin claimed, so it is worth a warning rather than silence.
- Deleting a handle the board does not have must not be an error. The board answers `Delete` for an
  unknown handle by doing nothing, and DSF cannot always know what a board is holding - it may have
  been restarted since.

### What this means for the held input levels

The controller now holds the last reported level of every endstop and probe input
([STALL_DETECTION.md §4.6](docs/devel/STALL_DETECTION.md)). A monitor that is deleted stops
reporting, so its held level is **never cleared** and stays active forever if it was active when it
was dropped.

That is harmless today: a held level only stops a move that names the same handle in a watch, and a
handle DSF has abandoned is one no move will name. It stops being harmless if a handle is abandoned
while active and later re-created - `M574 X0` then `M574 X1 S1 P"..."` - because the new monitor
reports only when it *changes*, so an open switch would leave the stale active level in place and the
first homing move would stop immediately.

`Create` on the board replaces the monitor but says nothing to `CanMotion`, so this has to be handled
on the controller: **the replay in `ScheduleFromSbc` must not outlive the monitor that fed it.** The
cheapest correct answer is for the controller to clear a held input when it sees a
`createInputMonitor` or a `changeInputMonitor`-delete pass through for that handle. That is a change
to `CommandProcessor`, not to `CanMotion`'s rule, and it belongs in the same phase as the delete.

This is the one place where this plan interacts with work already done, and it is why §3 is not
purely a tidy-up.

---

## 4. `actionDoMonitor` is a deliberate divergence

RRF calls `EnableHandle(board, handle, true, &states[i], ...)` once, in
[`SwitchEndstop::PrimeAxis`](lib/RepRapFirmware/src/Endstops/SwitchEndstop.cpp#L136), with the
comment *"check that the expansion board knows about it, and make sure we have an up-to-date state"*.
The enable is incidental - nothing ever disables a handle, and `actionDontMonitor` is dead in RRF.
What the call is for is the **out parameter**: it refreshes `states[i]`, the endstop's cached level,
at the start of every homing move.

DSF has no such cache to refresh. `sensors.endstops[].triggered` is maintained continuously from the
change reports, and three things read it at moments no move chose:

- `M119`
- `EndstopArming`, which refuses to drive an axis into a switch that is already closed
- the controller's held input levels, so that a move armed on an input that went active while it was
  in flight is stopped before it starts

RRF can afford to fetch on demand because its endstops are evaluated in the step interrupt on the
same board. DSF's endstop state crosses CAN and then SPI before anything reads it, so it is kept
current instead of fetched - and a per-move round trip per switch would add latency to the start of
every homing move to learn something DSF already knows.

This is the piece most likely to be mistaken for an unfinished port, because
`SwitchEndstopKind.PrepareAsync` is empty and reads like a to-do.

**Action: document it in [rrf-differences.md](src/Documentation/articles/rrf-differences.md)** as a
deliberate departure, in the same form as §2.2's motor-stall Z probe, and give
`SwitchEndstopKind.PrepareAsync` a line saying there is nothing to prepare and why - so the empty
method reads as finished rather than pending.

[STALL_DETECTION.md §4.5](docs/devel/STALL_DETECTION.md) currently describes the switch half of
`PrimeAxis` as "simply absent, with nothing to notice it is missing". That was the right reading of
the code at the time and is the wrong conclusion; it should say the switch half is deliberately not
ported, and point at the article.

---

## 5. `actionReturnPinName` is not needed

Despite the name, two of RRF's three callers use it for its *other* return value - the current level,
in the same `&states[i]` out parameter `EnableHandle` fills. Only
[`RemoteZProbe`](lib/RepRapFirmware/src/Endstops/RemoteZProbe.cpp#L43) wants the name alone.

DSF needs neither half:

- **The name** is the string the user gave to `M574` or `M558`. It is stored in
  `sensors.endstops[].port` and `sensors.probes[].port`, and `DescribeEndstop` reports it from there.
  RRF needs the round trip because its object model is assembled on the main board from what the
  expansion boards say about themselves; DSF's is assembled from the configuration that created them.
  `SwitchEndstop::AppendDetails` does one CAN round trip **per switch** to render `M119`, which DSF
  answers from memory.
- **The level** is §4's answer.

Nothing to do. Worth one line in the article alongside §4, since it is the same divergence seen from
the other side.

---

## 6. Scanning Z probes are out of scope

`actionSetDriveLevel` and `actionSelectTouchMode` configure a scanning inductive probe. DSF accepts
`ProbeType.ScanningAnalog` (`M558 P11`) as a type and creates a monitor for it, so a scanning probe
can be configured, but there is no `M558.1`/`M558.2` handler at all, which is where drive level,
touch mode and calibration are set. RRF also branches on `useTouchMode` inside the same `SetProbing`
that §2 is about, so the seam §2 builds is where this would later attach.

Adding these two actions without those codes would give the firmware a capability nothing can reach.
The gap is real but it is a scanning-probe gap, not an input-monitor one, and it wants its own plan.
Recorded here so the connection is not lost.

---

## 7. The work, in order

Phases are named by their commit subject rather than by a hash, so that a phase can be ticked in the
same commit that does it; `git log --grep` finds them.

| Phase | What | Status |
|---|---|---|
| 1 | Document the divergences (§4, §5) | ⬜ |
| 2 | Tell a probe when it is probing (§2) | ⬜ |
| 3 | Delete abandoned monitors (§3) | ⬜ |
| 4 | Clear a held input when its monitor goes (§3) | ⬜ |

Phase 1 first because it changes no behaviour and settles what the empty `PrepareAsync` means, which
is the question that produced this plan.

Phases 3 and 4 are separate commits but must land together: Phase 3 introduces the case Phase 4
handles, and Phase 3 alone would make a re-created endstop stop its first move.

### Phase 1 - document the divergences ⬜

`rrf-differences.md` gains a section on continuously monitored inputs covering §4 and §5, §4.5 of
`STALL_DETECTION.md` is corrected, and `SwitchEndstopKind.PrepareAsync` says why it is empty.

No behaviour changes.

### Phase 2 - tell a probe when it is probing ⬜

A start/stop seam on the probing path that sends `actionChangeThreshold` (analog types only) and
`actionChangeMinInterval`, with an active and an inactive interval matching RRF's 2 ms and 25 ms.

Tested by: the message is built from the object model, so this is testable without a board. That an
analog probe with a port produces both messages; that a digital probe produces only the interval;
that a motor-stall probe, a probe of type none, and a probe with no port produce neither; and that
the inactive interval is restored when the probing move ends however it ended.

`ProbeReportInterval` becomes two constants, and the article's note about probe report latency needs
re-checking against the inactive value - a probe that is not probing will now report more slowly,
which is what RRF does, but the article currently describes the 2 ms figure without qualification.

### Phase 3 - delete abandoned monitors ⬜

Old ports captured under the model lock in both handlers, deletes sent for handles the new
configuration will not re-create, per §3.

Tested by: the five abandonment cases in §3, each asserting which handles are deleted and which are
not; and that reconfiguring an axis to a different pin under the same handle deletes nothing, because
`Create` already replaces it.

### Phase 4 - clear a held input when its monitor goes ⬜

`CommandProcessor` clears the held level for a handle when a create or a delete for it passes
through, so the replay in `ScheduleFromSbc` cannot act on a level from a monitor that no longer
exists.

Tested by: a host-side case in `StopRulesTests`, the clearing being expressed as a rule in
`StopRules.h` for the same reason `NoteInputState` is - it is the tested copy.

---

## 8. Explicitly not in scope

- **Anything in Duet3Expansion.** The boards already implement all eight actions; this is entirely
  about what DSF sends.
- **`actionDontMonitor`.** RepRapFirmware never sends it either.
- **Scanning probe calibration** (§6), which needs `M558.1`/`M558.2` first.
- **`M950 J`.** DSF has no general-purpose input ports, which is why §3 has no `GpInPort` case. That
  is a separate gap and a larger one.
- **A registry of created monitors.** Every handle DSF creates is derivable from the object model,
  and a second record of them is a second thing that can be wrong.
