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
| `ChangeHandleThreshold` | 3 | `RemoteZProbe::SetProbing`, `RemoteZProbe::HandleG31` | **Gap** - §2 |
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

**And `G31` needs the message as well.** Reading the threshold when probing starts is not on its own
enough, and RepRapFirmware does not rely on it either: `RemoteZProbe::HandleG31` sends
`ChangeHandleThreshold` from `G31 P` for analog probes, and folds a refusal into that code's result.
The reason is that between the two codes the board is the only thing reading the probe. It reports a
change and nothing else, and `InputMonitor::AnalogInterrupt` decides what a change *is* by comparing
against the threshold it currently holds - so a threshold it has not been told about leaves
`sensors.probes[].value` frozen at whatever the old one last reported. Everything downstream reads
that stale value: `M558`'s "current reading", and the already-triggered check that a probing move
makes *before* it arms anything. A probe resting on the bed after `G31 P<lower>` would pass the check
that exists to catch exactly that, and record a height at the start position.

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

**One thing the round trip gives RepRapFirmware that DSF does not get, and it is not deliberate.** A
board that reset mid-job has forgotten its monitors. RRF finds out at the start of the next homing
move, because priming the axis fails and throws; DSF finds out never - the endstop simply stops
reporting, and the first anyone knows is a move that runs its full length. DSF raises
`expansion_reconnect` when a board re-announces while already `Running`
([ExpansionBoardManager.cs#L268](src/DuetControlServer/Link/Expansion/ExpansionBoardManager.cs#L268)),
so `expansion-reconnect.g` is where a machine recovers, but nothing re-creates the monitors on its own
and nothing fails if the macro does not. RRF has that same event *and* the safety net.

That is a gap, and a worse one than §2 or §3, because it is silent. It is §7's open question rather
than a phase, because the two candidate shapes differ in more than effort - see below.

**Action: document it in [rrf-differences.md](src/Documentation/articles/rrf-differences.md)** as a
deliberate departure, in the same form as §2.2's motor-stall Z probe, and give
`SwitchEndstopKind.PrepareAsync` a line saying there is nothing to prepare and why - so the empty
method reads as finished rather than pending.

[STALL_DETECTION.md §4.5](docs/devel/STALL_DETECTION.md) currently describes the switch half of
`PrimeAxis` as "simply absent, with nothing to notice it is missing". That was the right reading of
the code at the time and is the wrong conclusion; it should say the switch half is deliberately not
ported, and point at the article.

---

### 4.1 Open question: what should notice a board that lost its monitors

Two shapes, and the choice is not obvious enough to make silently.

**Port `PrimeAxis`'s round trip.** `SwitchEndstopKind.PrepareAsync` sends `actionDoMonitor` per
remote switch and throws if a board does not know the handle, exactly as RRF does. Faithful, and it
fails at the moment it matters - the start of a homing move. Costs a CAN round trip per switch per
homing move, and covers endstops only: a probe whose board reset is still silent, because probing does
not prime.

**Re-create monitors when a board announces itself.** `ApplyAnnouncementAsync` already knows a board
re-announced while it thought it was running. Re-sending the `CreateInputMonitor` for every endstop
and probe on that board would restore them without a per-move cost and would cover probes as well as
endstops. Not what RRF does, and it recovers rather than reports: a board silently losing and
regaining its monitors mid-job is arguably something the user should be told about, not something
papered over.

They are not exclusive - the announcement path is the recovery and the prime is the check - and the
honest answer may be both. Deferred rather than guessed.

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
| 5 | Check the finished work against the reference | ⬜ |
| - | A board that lost its monitors (§4.1) | ❓ open question, not planned |

Phase 1 first because it changes no behaviour and settles what the empty `PrepareAsync` means, which
is the question that produced this plan.

Phases 3 and 4 are separate commits but must land together: Phase 3 introduces the case Phase 4
handles, and Phase 3 alone would make a re-created endstop stop its first move.

### Phase 1 - document the divergences ✅

`rrf-differences.md` gained §2.3, on endstop state being kept current rather than fetched per move,
covering §4 and §5 here. §4.5 of `STALL_DETECTION.md` said the switch half of `PrimeAxis` was "simply
absent, with nothing to notice it is missing"; it now says the empty `PrepareAsync` is deliberate and
points at both. The `SwitchEndstopKind` comment says the same in the place someone reading the empty
method will be.

§2.3 also records the one thing the round trip buys that DSF does not get, per §4.1, so the article
does not claim more than is true.

No behaviour changes.

### Phase 2 - tell a probe when it is probing ✅

[ProbeArming](src/DuetControlServer/Motion/ProbeArming.cs) sends `actionChangeThreshold` (analog
types only) and `actionChangeMinInterval`, from the same `try`/`finally` around a tap that the stall
arming already used - so a probe is put back however the tap ended, for the same reason a driver is.

What it sends is captured under the object model lock as a `ProbeMonitor` and sent outside it. A live
`Probe` read outside the lock could be reconfigured half way through, and sending is a CAN round trip.
`RemoteProbes.TryGetMonitoredBoard` is now the single answer to "does this probe have a monitor at
all", shared with `TryGetStopInput`, so nothing can be sent to a handle that was never created.

Failing to arm throws and fails the tap; failing to disarm is logged. A probe left fast costs bus
traffic, which is where DSF already was, so it is not worth failing a probe that has already run.

**One deliberate departure from the reference.** RRF creates the monitor at
`ActiveProbeReportInterval` and only slows it down when its first probing operation *ends*, so a
configured but unused probe reports every change it sees. DSF creates at the inactive interval
instead. It is the state RRF reaches anyway, reached immediately, and creating fast would leave §2.2
half unfixed for exactly the machines that configure a probe and do not use it. What that departure
costs, and what Phase 5 does about it, is a probe used as the Z *endstop*.

Tested by [ProbeArmingTests](src/UnitTests/Motion/ProbeArmingTests.cs): an analog and a scanning probe
carry the threshold the object model holds now, a digital probe carries none, the board is the one
named by the port, a negative threshold does not become a huge unsigned one, and none of a stall
probe, a probe of type none, a probe with no port, or one with a blank port is armed at all.

The endstops article gained the probe half of its `minInterval` note, which previously described only
the endstop case.

### Phase 3 - delete abandoned monitors ✅

[InputMonitors](src/DuetControlServer/Motion/InputMonitors.cs) answers "what is this endstop or probe
having watched for it" from the object model, and `ReleaseAsync` sends `actionDelete` for everything
in the before list that is not in the after list. Both handlers capture the before list under the
model lock, before overwriting it, and release before creating - the order Phase 5 corrected, and
why.

A monitor is compared on `Handle.All` rather than on the handle struct: `RemoteInputHandle` is a union
of the whole and its bitfields, and only the whole is meaningful to compare.

One case the plan did not list turned out to matter, and has a test of its own: **moving an endstop to
a different board**. The handle is unchanged, so the new create replaces nothing on the *old* board,
which is left holding a pin for a switch that has moved. Same-board moves still delete nothing, which
is the case `Create`'s replace-first already covers.

Tested by [InputMonitorsTests](src/UnitTests/Motion/InputMonitorsTests.cs): all five abandonment cases,
the two board-move cases, that only a switch on a pin is monitored at all, and the probe equivalents.

### Phase 4 - clear a held input when its monitor goes ✅

Every CAN message DCS sends passes through `SbcInterface`'s request handler on its way to the bus, so
that is where a `createInputMonitorV1`, or a `changeInputMonitorV1` carrying `actionDelete`, clears
the level held for its handle. `setAddressAndNormalTiming` was already inspected there, so this is an
existing shape rather than a new one.

Clearing rather than re-reading is the right default: the replacement monitor reports only when it
*changes*, so nothing is known about its level until it does, and the window §4.6 of
`STALL_DETECTION.md` describes reopens for that handle until then - which is where DSF was before any
of this. Acting on the level a deleted monitor left behind is the outcome worth ruling out.

Only create and delete clear. A threshold or interval change leaves the monitor in place, and the
board re-evaluates and reports if that moved the input across the threshold, so the held level stays
answerable.

**A concurrency point the plan did not anticipate.** This gave `activeInputs` a second writing task -
the SBC task, alongside the CAN receiver task - and the update is read-modify-write on both the array
and its count. Losing the SBC task's half is the dangerous direction, because it leaves a level held
for a monitor that no longer exists, so `NoteInputState` and the replay now take a
`TaskCriticalSectionLocker`.

**Not host-tested, and it cannot be.** The rule that a create or a delete clears a handle is a
statement about CAN message types, and `StopRules.h` deliberately includes no CANlib, which is what
lets the host suite build it at all. What *is* tested there is the clearing itself, in
`TestAnInputIsHeldFromWhenItGoesActiveUntilItGoesInactive` and
`TestAnInputThatDoesNotFitIsSimplyNotHeld` - the call site is the untested part, and forcing it into
the shared header to change that would put a CANlib dependency somewhere it must not go.

**The guard on the call site was wrong and the whole thing was dead.** It required
`dataLength >= sizeof(CanMessageCreateInputMonitorV1)`, which is 64 bytes, but a create is sent
truncated to its terminating null - `GetActualDataLength()` is 10 plus the pin name, so 16 for
`io0.in`. The condition could never be true. It is `offsetof(..., pinName)` now, which is what
`GetMaxPinNameLength` measures from. Phase 5 found this; it is the reason a plan phase can be ✅ and
still not be doing anything.

### Phase 5 - check the finished work against the reference ✅

Phases 1-4 read RepRapFirmware for the shape of each message. This phase read it for the *behaviour*,
which turned up five things the earlier reading had missed and one claim in this document that was
simply false.

#### A probe standing in for the Z endstop is armed too

Phase 2's deliberate departure - creating at the inactive interval - has a cost Phase 2 did not name.
`M574 Z1 S2` homes on the probe, and a homing move is not a tap, so nothing raised the report rate:
the axis homed at 25 ms where RRF's first homing move gets 2 ms.

`ZProbeEndstopKind` now arms in `PrepareAsync` and releases in `ReleaseAsync`, through the same
`ProbeArming` a tap uses. RRF does *not* do this - `ZProbeEndstop::PrimeAxis` carries a `//TODO if the
Z probe is remote, check that the expansion board knows about it` - so this is the departure that
pays for the other one. What to send is captured into `EndstopPlan` under the model lock, beside the
drivers a stall watches, for the reason that file already gives: both halves of arming are handed one
answer rather than deriving their own.

#### The deletes have to go out first

§3 said deleting first "makes a failed create leave the axis with no monitor where it previously had
one". That is true of deleting *everything* and false of deleting only what is abandoned, which is
what `ReleaseAsync` does - a kept handle is never sent a delete, so a failed create cannot cost the
axis anything it was keeping.

Meanwhile the create-last order broke a case §3 listed as a leak and did not notice was also a
failure: reducing `P"1.io0.in+1.io1.in"` to `P"1.io1.in"` moves `1.io1.in` from handle `(0,1)` to
handle `(0,0)`, and the board refuses it as in use, because `(0,1)` still holds it. `SwitchEndstop::
Configure` opens with `ReleasePorts()` for exactly this reason.

Moving the release ahead of the create in both handlers fixes that, and incidentally fixes a second
defect: `M558`'s create-error path returned before reaching the release, so a rejected new port left
the old board holding the pin *and* still writing `sensors.probes[0].value`.

#### A half-created endstop gives its pins back

RRF's `SwitchEndstop::Configure` calls `ReleasePorts()` on *any* create failure, so a switch-per-driver
endstop whose second switch is refused ends up holding neither. DSF returned early and kept the first,
under a handle the object model now reads as the new port. `M574` now releases that axis' handles -
old and new, since the boards may differ - when `CreateEndstopMonitorAsync` fails.

#### `G31 P` sends the threshold

§2's fix was wrong about the reference and about the consequence; the corrected reasoning is in §2
above. `GCodeHandler.Probes` sends `actionChangeThreshold` when `G31 P` is seen, for probes with a
threshold to send, and fails the code if the board refuses.

#### Things that were already right, and two smaller corrections

`ProbeArming` matches `RemoteZProbe::SetProbing` exactly - threshold only while probing and only for
analog types, interval both ways - and DSF's supported remote probe types are RRF's set. The handle
layouts match. `InputMonitor::Create` really does delete a same-handle monitor first, so §3's
same-pin claim holds.

Two smaller things: `M558 C` now refuses a `+` in the port with RRF's message rather than sending the
pair to a board as one pin name, and `InputMonitor::Change` answers an unknown handle with
`GCodeResult::warning`, not silence as §3 assumed - so `ReleaseAsync` logs that at debug and keeps
the warning for a board that had the handle and would not let go.

**Not everything found was changed.** `EndstopMinReportInterval` is zero against RRF's 30 ms, which is
a real divergence and stays, but the comment justifying it was wrong: it claimed a nonzero interval
would delay the stop, when `inputChangedV2` carries the interrupt timestamp and `CommandProcessor`
corrects from that. The trade is chatter against travel-to-revert, and it is written down as that now.

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
