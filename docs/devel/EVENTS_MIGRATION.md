# Porting RepRapFirmware's event system into DuetControlServer

Tracking document for migrating RepRapFirmware's event mechanism —
[`lib/RepRapFirmware/src/Platform/Event.cpp`](lib/RepRapFirmware/src/Platform/Event.cpp) plus its
producers and its consumer in `GCodes` — into DuetControlServer, and for adding the two link events
this architecture needs and RepRapFirmware has no equivalent of.

It is the sibling of [MCODE_MIGRATION.md](MCODE_MIGRATION.md) and follows the same contract (§1
there): faithful port of the `SUPPORT_CAN_EXPANSION` path, gaps marked `// TODO` rather than
invented, structural departures asked about rather than chosen. Where this document departs from
RepRapFirmware, §7 says so and why.

**The two new events are `controller_disconnect` and `controller_reconnect`**, raised when the SPI
link to DuetCANMaster drops and comes back, running `sys/controller-disconnect.g` and
`sys/controller-reconnect.g`. §4 is their specification; §1 to §3 are what has to exist first.

---

## 1. What an event is in RepRapFirmware

> An event is an occurrence reported by a machine sensor that may need to be reported or may require
> action to be taken. — [Event.h](lib/RepRapFirmware/src/Platform/Event.h)

An event is not a trigger and not a message. It is a *queued* fact about the machine that has a macro
file named after it, a default action if that file is absent, and a suppression rule so that a fault
reporting itself ten times a second produces one macro run rather than ten.

### 1.1 The queue

[`Event`](lib/RepRapFirmware/src/Platform/Event.cpp) is a singly-linked list of heap-allocated
events, ordered by priority, where **priority is the numeric value of the event type** — lower is more
urgent. Four properties matter to the port:

1. **Insertion is priority-ordered** (`AddEventV`, Event.cpp:37). The new event is walked past every
   pending event of the same or higher priority and linked in after them. RepRapFirmware *conflates*
   priority with the wire value, which DSF cannot do — see §3.6.
2. **The head stays put while it is being processed.** `isBeingProcessed` makes the walk skip the head
   unconditionally, so a higher-priority event that arrives mid-macro queues *behind* the one running.
   An event's macro therefore never has the queue reordered under it.
3. **Similar events are dropped, not queued.** Two events are similar when type, device number, CAN
   address and parameter all match — *the text is not compared*. A repeated fault is suppressed for as
   long as the first one is pending or running, which is what stops a stuck driver from queueing
   thousands of entries.
4. **Nothing ever expires.** An event leaves the queue only when its processing finishes.

`eventsQueued` / `eventsProcessed` are reported by `M122` as `Events: %u queued, %u completed`.

### 1.2 Who raises events

Every `AddEvent` call site in RepRapFirmware, with the DSF verdict:

| Event | Raised by | RRF source | In DSF |
|---|---|---|---|
| `expansion_reconnect` | A board re-announces while already `Running` | ExpansionManager.cpp:180 | ✅ raised where the announcement is applied |
| `expansion_timeout` | No status report for `StatusMessageTimeoutMillis` | ExpansionManager.cpp:578 | ✅ the sweep moved here from the controller (§3.3.1) |
| `heater_fault` | `LocalHeater` gave up on a heater | LocalHeater.cpp:1044 | ⛔ local hardware; arrives from the board as a CAN event instead |
| `driver_error`, `driver_warning`, `driver_stall` | Local driver status | Move.cpp:3613/3627/3648 | ⛔ local hardware; arrives as a CAN event |
| `filament_error` | `FilamentMonitor` | FilamentMonitor.cpp:412 | ⛔ local hardware; arrives as a CAN event |
| any | `CanMessageEvent` from an expansion board | CommandProcessor.cpp:727 | ✅ decoded and queued |
| any | `M957` | GCodes3.cpp:1306 | ⬜ not implemented — [MCODE_MIGRATION.md](MCODE_MIGRATION.md) §5.11 |

So in this architecture there are **three** producers, not seven: CAN event messages from expansion
boards, board-connectivity events DCS derives itself, and `M957`. Plus the two new link events.

### 1.3 Who consumes them

One consumer, on one channel. `GCodes::SpinGCodeBuffer` reaches this only when the AutoPause channel
is otherwise idle and is not waiting for an `M291` acknowledgement (GCodes.cpp:613):

```cpp
else if (&gb == AutoPauseGCode() && !gb.LatestMachineState().waitingForAcknowledgement)
{
    if (Event::StartProcessing())
    {
        ProcessEvent(gb);
    }
}
```

`GCodes::ProcessEvent` (GCodes3.cpp:1339) is the whole of the policy:

```
GetTextDescription()  -> text + MessageType (severity)
GetMacroFileName()    -> "<event_type>.g" with '_' replaced by '-'
  macro exists?
    yes -> set state finishedProcessingEvent, run macro with vars {D, B, P, S}
    no  -> GetDefaultPauseReason()
             dontPause -> platform.Message(mt, text); FinishedProcessing()
             else      -> log to HTTP+log, SendSimpleAlert(text, printing ? "Printing paused"
                                                                          : "Event notification")
                          printing ? state = processingEvent : FinishedProcessing()
```

The pause is a two-state dance because it has to wait for the movement lock:
`processingEvent` → `DoAsynchronousPause` → `eventPausing1` (locks, runs `pause.g`) or `eventPausing2`
(locks, **no** `pause.g`, used for `driver_error`) → `finishedProcessingEvent` →
`Event::FinishedProcessing()` (GCodes4.cpp:559, 602, 1965, 1982). `AbortStateMachine` also calls
`FinishedProcessing()` for both event states, so an aborted file cannot strand the queue head
(GCodes4.cpp:2054).

### 1.4 The macro contract

This is the part users write against, and it is the part that must be preserved exactly.

| | Rule |
|---|---|
| Filename | The event type name with `_` → `-` and `.g` appended, in `/sys` — `heater-fault.g`, `expansion-timeout.g` |
| `param.D` | Device number: heater, extruder or **local** driver number (no board address) |
| `param.B` | CAN address of the board it came from. Always passed, "so that the same macros can be used on all Duets" |
| `param.P` | Event-specific parameter: heater fault subtype, encoded driver status, filament sensor status |
| `param.S` | The full text description — the same string the default action would have printed |
| Macro found | The default action does **not** run. The macro is the whole response |
| Macro missing | The default action runs |
| While it runs | Further similar events are suppressed; other events queue behind it |

### 1.5 Default actions

Severity comes from `GetTextDescription`, the pause from `GetDefaultPauseReason`
(Event.cpp:115, 153):

| Event | Severity | Default pause | Message |
|---|---|---|---|
| `main_board_power_fail` | error | none | (bypasses the event system entirely) |
| `expansion_reconnect` | error | none | `Expansion board %u reconnected` |
| `expansion_timeout` | error | none | `Expansion board %u stopped sending status` |
| `heater_fault` | error | `HeaterFault`, via `pause.g` | `Heater %u fault: %s%s` |
| `driver_error` | error | `DriverError`, **without** `pause.g` | `Driver %u.%u error: <status><text>` |
| `filament_error` | error | `Filament`… `FilamentError` | `Filament error on extruder %u: %s` |
| `driver_stall` | warning | none | `Driver %u.%u stall` |
| `driver_warning` | warning | none | `Driver %u.%u warning: <status><text>` |
| `mcu_temperature_warning` | warning | none | `MCU temperature warning from board %u: temperature %.1fC` |
| `overvoltage` | warning | none | `overvoltage on board %u: voltage %.1fV` |
| `undervoltage` | warning | none | `undervoltage on board %u: voltage %.1fV` |

A pausing event with no macro also raises a **message box** (`SendSimpleAlert`, mode 1, no timeout),
titled `Printing paused` while printing and `Event notification` otherwise. If the print is already
paused or pausing, no second pause is added.

---

## 2. What DuetControlServer has today

| Piece | Where | State |
|---|---|---|
| `EventType` enum, CANlib numbering | [EventType.g.cs](src/DuetControlServer/Link/Protocol/Shared/EventType.g.cs), generated from [can-messages.json](src/DuetCanMessage.SourceGenerators/Schema/can-messages.json) | ✅ 0-10, matches `RRF3Common.h` |
| `CanMessageEvent` decode | [CanMessageFormats.g.cs:4454](src/DuetControlServer/Link/Protocol/CanMessages/Generated/CanMessageFormats.g.cs#L4454) | ✅ |
| Board events arriving from CAN | [ExpansionBoardManager.cs:189](src/DuetControlServer/Link/Expansion/ExpansionBoardManager.cs#L189) | 🟡 `logger.LogWarning(...)` and nothing else |
| Event queue, priority, suppression | `Events/EventQueue.cs` | ✅ phase B |
| Event text and macro names | `Events/EventText.cs`, `Events/DriverStatusText.cs` | ✅ phase B (§3.5.1) |
| Event macros | — | ⬜ nothing runs them yet: the processor is what does |
| `M957` | — | ⬜ |
| `Autopause` code channel | [CodeChannel.cs:71](src/DuetAPI/CodeChannel.cs#L71) = 11 | ✅ has a `ChannelProcessor` like every other channel; nothing puts codes on it |
| Macro runner | [MacroRunner.cs:72](src/DuetControlServer/Files/MacroRunner.cs#L72) | ✅ runs a macro on a channel, with parameters (§3.4) |
| Variables (`var`, `set`, `global`, `param`) | `Codes/Meta/VariableSet.cs`, `VariableStore.cs` | ✅ phase A — they did not exist at all before it |
| Message box | `state.messageBox` exists in the model | ⬜ `M291`/`M292` not ported |
| Pause | `JobProcessor.Pause(...)` exists | ⬜ nothing calls it; `M25` and `pause.g` not ported |
| `M122` events line | — | ⬜ |

Three of those are hard prerequisites and are tracked as phases in §5: **macro parameters**
(without `param.S`/`D`/`B`/`P` an event macro cannot see the event — done, §3.4), **message box** and
**pause** (without them two default actions cannot be written faithfully).

### 2.1 DuetCANMaster still has an event queue, and it leaks

The fork kept [`Event.cpp`](src/DuetCANMaster/src/Platform/Event.cpp) but dropped `GetParameters` (it
has no `VariableSet`), and it has no `GCodes`, so **nothing ever calls `StartProcessing()` or
`FinishedProcessing()`**. Two producers remain:
[ExpansionManager.cpp:88](src/DuetCANMaster/src/CAN/ExpansionManager.cpp#L88) (`expansion_reconnect`)
and [:275](src/DuetCANMaster/src/CAN/ExpansionManager.cpp#L275) (`expansion_timeout`). Each allocates
an `Event` that is never freed and never seen; only the similarity rule stops it growing without
bound, and `expansion_timeout` for a board that flaps produces one entry per flap.

`Event::Add(const CanMessageEvent&)` is dead in the fork: board events are forwarded to the SBC as
raw CAN messages instead. **The event system belongs on the DCS side of the link**, and the
controller's copy should be deleted once §5's phase C lands — see §7.

### 2.2 The link never tells the managed side it went down

This blocks the two new events, so it is scoped here rather than left as background.

`SbcInterface::Execute` checks for a connection-state change *after* `PerformFullTransfer()`
returns ([SbcInterface.cpp:398](src/DuetSbcInterface/src/SBC/SbcInterface.cpp#L398)), but
`PerformFullTransfer` reconnects internally and only returns once a transfer has **succeeded**
([SbcTransfer.cpp:237](src/DuetSbcInterface/src/SBC/SbcTransfer.cpp#L237)), at which point
`m_connected` is true again. `m_wasConnected` is only ever assigned `true`, so:

- `InboundEventType::ConnectionLost` ([:412](src/DuetSbcInterface/src/SBC/SbcInterface.cpp#L412)) is
  **unreachable** while the loop runs. `LinkService.HandleConnectionLost` → `Invalidate()` never runs
  during an outage: the object model keeps its pre-failure boards, `state.status` never becomes
  `disconnected`, the job is not aborted and pending codes are left hanging. The only symptom is one
  `LogWarning`.
- `ConnectionEstablished` is posted **once, at startup**, so `RunStartupFilesAsync` — the only thing
  that clears `IsDisconnected` and the only thing that runs `config.g` — can never run again. After a
  controller reboot, `ControllerReset` → `Invalidate()` pins the status at `disconnected` and leaves
  the rebooted board unconfigured until DCS is restarted.

Phase C fixes both, because `controller_disconnect` has nothing to fire from otherwise.

---

## 3. The design for DuetControlServer

### 3.1 `Events/EventQueue.cs`

A direct port of `Event.cpp`'s data structure and rules, not of its memory management. One
`SortedSet`/linked list guarded by a lock, holding:

```csharp
internal sealed record class MachineEvent(EventType Type, ushort Param, byte BoardAddress,
                                          byte DeviceNumber, string Text)
```

Ported behaviour, one-for-one with §1.1: priority-ordered insert; head pinned while
`IsBeingProcessed`; similarity on `(Type, DeviceNumber, BoardAddress, Param)` ignoring `Text`;
`TryStartProcessing()` / `FinishedProcessing()`; `Queued` and `Processed` counters for `M122`.

The one change to the *rules* is that ordering asks `EventPriority.Of(type)` rather than comparing
enum values, because the values are fixed by Duet3Expansion compatibility and the priorities are not
(§3.6).

The one addition RRF has no need for is a **cap** on queue length, because RRF's producers are
interrupt-driven and bounded by the similarity rule while DSF's `M957` is not. A cap that drops the
*lowest-priority* entry, with a `logger.LogWarning`, keeps §1's "no silent truncation" honest.

### 3.2 `Events/EventProcessor.cs`

A `BackgroundService`, replacing `GCodes::SpinGCodeBuffer`'s poll of the AutoPause channel. RRF has to
poll because it has one thread and a state machine; DCS awaits instead:

```
loop:
  await queue.WaitForEventAsync(stoppingToken)      // signalled by Raise()
  if (!queue.TryStartProcessing(out MachineEvent ev)) continue
  try     { await ProcessAsync(ev, stoppingToken) }
  finally { queue.FinishedProcessing() }            // == AbortStateMachine's guarantee
```

`ProcessAsync` is the port of `GCodes::ProcessEvent` (§1.3), with the same order of decisions:

1. `EventText.Describe(ev)` → `(string text, MessageType severity)` — a port of `GetTextDescription`,
   including the `Driver %u.%u` / `Heater %u fault: %s` formats. These strings are parsed by DWC,
   PanelDue and a decade of macros, so they are preserved exactly (MCODE_MIGRATION §1.5).
2. Macro name: `ev.Type.ToString()` in RRF's snake case with `_` → `-` and `.g`. The generated enum
   uses PascalCase members, so the mapping is an explicit table or a `[Description]` attribute on the
   schema — **not** a regex over the C# name, which would be a second grammar for one rule
   (MCODE_MIGRATION §1.9).
3. `macroRunner.TryRunAsync(CodeChannel.Autopause, name, parameters: …)` — returns false when the file
   does not exist, which is exactly the `SysFileExists` branch.
4. If it did not run, the default action (§1.5).

The finally-block is what `AbortStateMachine` gives RRF: an event whose macro throws or is cancelled
still leaves the queue.

### 3.3 Where events are raised

| Source | Change |
|---|---|
| Expansion board CAN event | [ExpansionBoardManager.cs:189](src/DuetControlServer/Link/Expansion/ExpansionBoardManager.cs#L189): replace the `LogWarning` with `events.Raise(canEvent.EventType, canEvent.EventParam, report.Source, canEvent.DeviceNumber, canEvent.TextString)` — the port of `Event::Add(const CanMessageEvent&)` |
| Board re-announced while running | `ApplyAnnouncementAsync`: raise `ExpansionReconnect` when the board's `State` is already `Running`, mirroring ExpansionManager.cpp:180 |
| Board stopped reporting | A watchdog **moved** from the controller, not added beside it — §3.3.1 |
| SPI link down / up | §4 |
| `M957` | New case in `MCodeHandler`, port of GCodes3.cpp:1306 |

### 3.3.1 Where the expansion-board watchdog belongs

DuetCANMaster runs one today: `ExpansionManager::Spin()` sweeps the board table every cycle and, for a
board that has been `Running` without a status report for `StatusMessageTimeoutMillis`, sets
`BoardState::TimedOut` and raises `expansion_timeout`
([ExpansionManager.cpp:258-279](src/DuetCANMaster/src/CAN/ExpansionManager.cpp#L258-L279)). Adding a
second sweep in DCS would give the machine two timers that can disagree about whether a board is
alive, so the question is which one to keep — and the code answers it:

- **Nothing on the controller reads `TimedOut`.** `BoardState` is declared in
  [ExpansionManager.h:22](src/DuetCANMaster/src/CAN/ExpansionManager.h#L22) and appears nowhere else
  in DuetCANMaster. No motion, CAN or timing path branches on it. The state that *is* used —
  `Flashing`, `FlashFailed`, `Resetting` — belongs to the firmware-update flow and stays.
- **The event it raises has no consumer there.** §2.1: DuetCANMaster has no `GCodes`, so the queue is
  never drained and the entry leaks.
- **DCS already receives everything the sweep is derived from.** Announcements and board status
  reports are processed locally *and* forwarded
  ([CommandProcessor.cpp:313-330](src/DuetCANMaster/src/CAN/CommandProcessor.cpp#L313-L330)), and
  `ExpansionBoardManager` already turns both into `boards[]`. The last-seen timestamp is a field DCS
  can keep beside the state it is already writing.
- **`boards[]` is where board state has to live anyway.** Reporting it is DCS's job, and the object
  model must be able to describe the machine.

So the watchdog **moves**: DCS gains `whenLastStatusReportReceived` per board and a periodic sweep
that sets `BoardState.TimedOut` and raises `expansion_timeout`; DuetCANMaster loses `Spin()`'s sweep
along with both `Event::AddEvent` calls and, with them, `Event.cpp` entirely (§7).

The one thing the controller's timer had that DCS's does not is independence from the SPI link. DCS
only sees reports while the link is up, so a link outage would otherwise time out every board at once.
It cannot: `controller_disconnect` invalidates `boards[]` before that (§4.5), so there is nothing left
to sweep, and the boards re-announce after the reconnect. The sweep must nonetheless *rebase* its
timestamps when the link comes back rather than compare against readings taken before the outage.

The alternative — leaving the timer on the controller and having it tell DCS — needs a new SPI message
to carry a fact DCS can already compute from messages it already receives, and leaves the board state
split across two owners. It is the wrong side of "the object model must recreate the machine".

### 3.4 Macro parameters, and the variables underneath them

Phase A was scoped as "give `MacroRunner` a parameter channel". It was larger than that: **DCS had no
variables at all.** `var`, `set` and `global` parsed and validated their arguments and then threw the
value away behind a `// TODO save the variable`, and the expression evaluator documented `var`/`param`
as "owned by the firmware" and refused them. Every one of those paths was a leftover from the split
architecture, where RepRapFirmware held the variables and DCS forwarded to it.

So `param` could not be added on its own, and what phase A built is the whole mechanism:

| Piece | Where |
|---|---|
| A named set of values, locals and parameters side by side | `Codes/Meta/VariableSet.cs` |
| Which set a code sees - its file, or its channel when it has no file | `Codes/Meta/VariableStore.cs` |
| `global`, stored in the object model where it is visible over IPC | `VariableStore.TryCreateGlobalAsync` / `TryAssignGlobalAsync` |
| `var` / `set` / `global` statements | `Codes/Handlers/KeywordHandler.cs` |
| `var.x`, `param.x`, `global.x` and `exists()` in expressions | `Expressions.ExpressionContext.TryResolveVariable` |
| Parameters at macro start, and a code's own parameters to the macro named after it | `MacroRunner.TryRunAsync`, `Code.TryRunCodeMacroAsync` |

Storage is per file and lifetime is per block, which is RepRapFirmware's split: it keeps one
`VariableSet` per machine state and tags each variable with the block nesting it was created at.
DCS already recorded the block half - `CodeBlock.LocalVariables` names what a block created, and the
block ending or a `while` restarting deletes exactly those. Per-block *sets* would have made an inner
`var x` shadow an outer one, which RRF refuses outright (`variable 'x' already exists`, from a flat
lookup), and would put a parent-chain walk in the expression path.

RRF's semantics are kept as they are what macros are written against: `var` and `global` create and
refuse to overwrite, `set` assigns and refuses to create, parameters are read-only because `set`
accepts only the `var.` and `global.` prefixes, and reading one that does not exist is an error rather
than a null - `unknown variable 'x'` / `unknown parameter 'x'`.

A variable holds a scalar or an array of them, indexed to any depth: `var.grid[1][0]` reads,
`set var.speeds[var.i] = 0` assigns, `#var.speeds` measures, and `exists(var.speeds[5])` answers
`false` where reading the same element is an error. Where the brackets are is decided in one place -
`VariableStore.TrySplitIndexedName` - but what is *inside* them is handed back as written, because the
two callers do not agree on that: the expression parser has already evaluated its indices to integers
by the time it asks, while `set` arrives with whatever the operator typed and evaluates it itself.
That is what makes an index a computed expression rather than a literal.

### 3.4.1 Expressions no longer resolve half the machine

Variables were the second thing an event macro needs. The first is the machine itself, and asking for
it did not work either: the expression evaluator resolved only object model branches carrying the
SBC-property flag - `volumes`, `messages`, `directories`, `plugins`, `job` - because everything else
was the firmware's to answer. `move`, `heat`, `state`, `sensors`, `boards` and `tools` fell through to
a fallback that used to be the RepRapFirmware round trip and had become `return null`.

That is not a small gap for this work. `heater-fault.g` reads `heat.heaters[param.D]`; a conditional
evaluating to `null` throws `invalid conditional result 'null'`, so `if move.axes[0].homed` - the
shape most machine macros are built from - failed outright, and outside a conditional it silently
produced `null`.

So the gate is gone: the evaluator resolves the whole object model, which DuetControlServer now owns
in full. The two-pass shape that remains is not about ownership but about what a *synchronous*
evaluation can do — `fileexists()`, `fileread()` and `exists()` need asynchronous lookups, so the
first pass evaluates everything else, the second substitutes those as literals and re-evaluates what
comes back.

**An expression that still cannot be produced is now an error**, `cannot evaluate '<expression>'`,
rather than a null. A null reads as a valid answer, which is how the two gaps above stayed invisible.

A collection of scalars is copied while the read lock is held, so `move.axes[0].workplaceOffsets`
resolves as an array rather than being refused. A collection of model objects still is: copying it
would hand out the live elements it holds, which the update task mutates, and `move.axes` says so
rather than producing something that looks like an answer.

### 3.5 Default actions in DSF terms

| RRF | DSF |
|---|---|
| `platform.MessageF(mt, ...)` | `eventLogger.LogOutput(messageType, text)` |
| `SendSimpleAlert(...)` | `state.messageBox` — **blocked** on `M291`/`M292` (MCODE_MIGRATION §5.11) |
| `DoAsynchronousPause(..., eventPausing1)` | `JobProcessor.Pause(...)` + `pause.g` — **blocked** on `M25` |
| `eventPausing2` (pause without `pause.g`) | same, minus the macro |
| `IsReallyPrinting()` | `jobProcessor.IsProcessing && !IsSimulating` |

Until those land, the pausing branch is a `// TODO` naming what it waits for, and the logging branch
runs for every event. That is a *known* missing default, not an invented one (MCODE_MIGRATION §1.7):
the macro path — which is what machines actually configure — is complete from phase B.

### 3.5.1 Where the text of an event lives

Three of the messages in §1.5 are not one string but a format with a decoded value in the middle: a
heater fault's type, a driver's status word, a filament monitor's status. CANlib carries all three
tables, and the question each one asks is the same: does anything other than DuetControlServer render
this? If a board renders it too, two copies drift and the same fault reads differently depending on
which of them reported it.

The answers differ, so the three are handled differently:

| Table | Rendered by a board? | Where it lives |
|---|---|---|
| `StandardDriverStatus::BitMeanings` and the severity masks | **Yes** — Duet3Expansion answers a status request with this text ([CommandProcessor.cpp:559](src/Duet3Expansion/src/CommandProcessing/CommandProcessor.cpp#L559)) | The schema, emitted to C# and compared against CANlib |
| `HeaterFaultText` | No — a board sends the fault type; only RRF and the controller `Event.cpp` §7 deletes render it | DuetControlServer, in `EventText` |
| `FilamentSensorStatus` | The value is an enum DuetAPI already declares | Nowhere new; it is already in the schema as `checkOnly` |

`HeaterFaultType` itself is in the schema either way, because Duet3Expansion decides the value and
sends it: the ordinals are a wire contract even though the words are not. CANlib is left alone in both
cases, so RepRapFirmware still compiles against it.

Checking the shared table needed one thing the generator did not have. `verify-cpp-layout.sh` proves
the schema matches CANlib by compiling both and comparing layouts, and a string array has no layout;
`compare-constants.py` compares numbers. So a constant group may now declare **string tables**, and
that comparison reads CANlib's `constexpr` string arrays and reports the entry that differs rather
than the array. C# cannot assert an array's length at compile time as CANlib does, so both of the
`static_assert`s CANlib makes - one string per fault type, and the three severity masks disjoint and
together covering exactly the bits with meanings - are asserted in the tests instead.

That check earned itself immediately: the first transcription of the three masks into hex was wrong,
and comparing them against CANlib is what said so.

### 3.6 Numbering, and priority as its own property

`EventType` mirrors CANlib and travels on the wire as 8 bits. **Values 0-10 are fixed**: Duet3Expansion
firmware already in the field sends them, and a machine must be able to run a mix of board firmware
versions. Nothing here may renumber them.

The two new events never touch CAN — they are raised by the SBC about the SBC's own link — so they go
in the same enum at **128 and 129**, in a block documented as never appearing on the wire. CANlib
keeps 0-127 to grow into, so an upstream addition can never silently collide, and one enum keeps one
macro-naming rule and one `M957` grammar.

```json
{ "name": "controller_disconnect", "value": 128,
  "doc": "the SPI link to the controller stopped responding. Raised by DuetControlServer, never sent over CAN" },
{ "name": "controller_reconnect", "value": 129,
  "doc": "the SPI link to the controller came back. Raised by DuetControlServer, never sent over CAN" }
```

The generator emits into `EventType.g.cs`; the layout tests in `CanMessageLayout.g.cs` are unaffected
because the field is still one byte.

Two schema mechanics matter here. The local block must not be emitted into the generated C++ header —
`MessageTypeDef.Emit` already takes a per-language set, so `"emit": ["csharp"]` covers it — and
`compare-enums.py` compares the schema against CANlib **in both directions**, so it has to learn to
skip entries that are not emitted for C++. Without that, adding 128/129 fails the build with a
mismatch that is not one.

**Priority is separated from the value, and the schema owns it.** In RepRapFirmware the two are the
same number, which only works because RRF owns the numbering. Here the numbering is a compatibility
constraint and the priority is a design choice, and pinning one to the other would put the two most
consequential events last.

So `MessageTypeDef` gains a `priority` field, written on each `EventType` value, and the generator
emits the lookup beside the enum — an `EventTypeExtensions.Priority(this EventType)` from
`CSharpTablesEmitter`, in the same generated-file discipline as `CanGenericTables.g.cs`. One
declaration, checked by the same validator, with no second list to drift. The order:

| Priority | Event | Why here |
|---|---|---|
| 0 | `controller_disconnect` | Nothing else in the queue can be acted on: the link every other event's macro would use is down |
| 1 | `controller_reconnect` | Restores the machine (§4.3); whatever queued during the outage should run against a configured machine |
| 2 | `main_board_power_fail` | RRF's order from here down, unchanged |
| 3 | `expansion_reconnect` | |
| 4 | `expansion_timeout` | |
| 5 | `heater_fault` | |
| 6 | `driver_error` | |
| 7 | `filament_error` | |
| 8 | `driver_stall` | |
| 9 | `driver_warning` | |
| 10 | `mcu_temperature_warning` | |
| 11 | `overvoltage` | |
| 12 | `undervoltage` | |

Declaration order would express the same thing without a new field, but only while the two orders
happen to coincide: the values are grouped by CANlib's numbering and a priority change would mean
moving a line for reasons unrelated to what it declares. An explicit number says what it is.

Two mechanics turned out differently from what this section first assumed. `compare-enums.py` needed
no teaching at all - it already skips what the schema emits for C# alone, which is what the local
block is. `can-messages.schema.json` did: it validates the schema file itself and rejects a property
it has not been told about, so `priority` had to be declared there as well.

---

## 4. The two link events

### 4.1 Native prerequisites

Both events need §2.2 fixed first. The fix is to report the transition **where it happens** rather
than after `PerformFullTransfer` returns:

- In `PrepareReconnect()`, at the point it sets `m_hadTimeout` on a live connection
  ([SbcTransfer.cpp:285](src/DuetSbcInterface/src/SBC/SbcTransfer.cpp#L285)), post `ConnectionLost`
  with the reason text that is currently only logged.
- On the transfer that succeeds after `m_hadTimeout`
  ([SbcTransfer.cpp:211](src/DuetSbcInterface/src/SBC/SbcTransfer.cpp#L211)), post
  `ConnectionEstablished`, and carry whether the controller had reset (`HadReset()`) so the managed
  side can tell "the same controller resumed" from "a rebooted controller".
- Delete the now-dead transition check at
  [SbcInterface.cpp:398-417](src/DuetSbcInterface/src/SBC/SbcInterface.cpp#L398-L417), or reduce it to
  the startup post it still performs correctly.

Both posts must remain on the interface thread's event ring, not a callback, so the transfer loop is
never blocked by managed work.

**Ordering matters.** `HadReset()` is evaluated at the top of the *next* loop iteration
([SbcInterface.cpp:347](src/DuetSbcInterface/src/SBC/SbcInterface.cpp#L347)), so posting
`ConnectionEstablished` from inside `PerformFullTransfer` would put it *before* the `ControllerReset`
it belongs after, and §4.5's sequence would run backwards. `ConnectionLost` has to be posted from the
transfer engine because that is where the outage is observed; `ConnectionEstablished` must stay behind
the reset check. Track the outage with a flag or generation counter the interface loop can read
(`m_transfer.HadTimeout()`) and post it there, after `ControllerReset`.

### 4.1.1 Staged data does not survive the outage

`PrepareReconnect()` deliberately keeps the staged TX buffer today, and `m_outbound` — the ring of
commands not yet written into a transfer — is never cleared by anything. Both are replayed at a
controller that may have rebooted since, with no state to receive them.

They must be dropped instead:

- `PrepareReconnect()` clears `m_txPointer`, `m_packetId` and `m_packetsBeingResent`, i.e. what
  `ResetConnection()` already does ([SbcTransfer.cpp:327](src/DuetSbcInterface/src/SBC/SbcTransfer.cpp#L327)).
- The `m_outbound` ring is drained and discarded in the same place, which nothing currently does.

Reporting the drops is mostly existing machinery, and it is worth being precise about what it does and
does not reach:

| Outbound work | Reported today? |
|---|---|
| Commands carrying a request id (enable CAN, e-stop, reset, firmware update) | ✅ `CompleteRequest(id, RequestResult::Cancelled)` → `TrySetCanceled()` ([NativeLink.cs:448](src/DuetControlServer/Link/Native/NativeLink.cs#L448)); `Cancelled` is documented as exactly this case ([LinkEvents.h:76](src/DuetSbcInterface/src/SBC/LinkEvents.h#L76)) |
| CAN requests expecting a reply | ✅ cancelled by `LinkInterface.Invalidate()`, and by the 2 s `CanRequestTimeout` |
| Scheduled moves | ✅ no `MoveCompleted` arrives; `motionTracker.Invalidate()` discards the moves they refer to |
| **Fire-and-forget CAN messages** | ❌ today: resolved as *sent* the moment the native loop takes them ([LinkInterface.cs:263-270](src/DuetControlServer/Link/LinkInterface.cs#L263-L270)), so a dropped one cannot be reported — DCS already believes it succeeded. Fixed by §4.1.2 |

So the drop needs a `Cancelled` completion for anything holding a request id, and — once §4.1.2 lands —
for every unsent CAN message too.

### 4.1.2 Everything sent is acknowledged, in two hops

`SendCanMessageAsync` resolves a message that expects no reply as soon as the native loop has copied
it out of the outbound ring. That is a statement about a memcpy, not about the machine: between there
and the bus sit the SPI transfer, the controller's packet handler and the CAN peripheral, and **every
failure in that stretch is currently silent**.

There are two distinct facts to report, and they need separate mechanisms because they are established
in different places:

| Hop | Established by | Applies to |
|---|---|---|
| **1. Delivered over SPI** | A full transfer completing with the packet in it | *Every* outbound command |
| **2. Accepted by the CAN controller** | The controller handing the message to the CAN peripheral | CAN messages only |

#### Hop 1: delivery over SPI, for every command

Today a command is consumed from `m_outbound` when it is written into the TX buffer
([SbcInterface.cpp:578](src/DuetSbcInterface/src/SBC/SbcInterface.cpp#L578)) — before the transfer
carrying it has happened, let alone succeeded. Commands with a request id are completed on their own
terms (`EnableCan` reports `Success` at staging time, which is the same overstatement); the rest —
`Message`, `CanMessage`, `ScheduleMove` — report nothing at all.

A per-command completion event would be one event per message on the hot path, which the move stream
cannot afford. It is not needed: **the outbound path is FIFO end to end.** Commands leave the ring in
order and are written into the transfer in order, so one number describes any number of them:

- Every command gets a monotonic `outboundSeq` when it enters `m_outbound`.
- The transfer engine records the highest seq staged into the transfer it is about to perform.
- On success, post `OutboundDelivered(seq)` — *everything up to and including* `seq` reached the
  controller.
- On a drop (§4.1.1) or a failed transfer, post `OutboundDropped(from, to)`.

That is O(1) per transfer rather than O(1) per command, and the managed side resolves it against an
ordered map of `seq` → completion. Commands that already carry a request id keep it: the id says
*which* request, the seq says *when it got there*, and `RequestCompleted` can be posted from the seq
sweep instead of at staging time.

#### Hop 2: acceptance by the CAN controller

The controller's `CanInterface::SendCanRequest`
([CanInterface.cpp:669](src/DuetCANMaster/src/CAN/CanInterface.cpp#L669)) has four of them:

| Failure | Today |
|---|---|
| `can0dev == nullptr` (CAN never enabled) | `return` — nothing sent, nothing said |
| Request id placeholder not `0xFFF` | `WarningMessage` on the controller's console only |
| No free pending-request slot | Sent, but no reply can ever be forwarded. A `TODO` says the SBC should be told; it is not, so the code waits out `CanRequestTimeout` |
| `CanDevice::SendMessage` cancelled an older message to make room | Counted in `txTimeouts` / `lastCancelledId`, never reported |

The fix is an acknowledgement from the controller, keyed by the `txToken` the SBC already puts in
every `SendCanMessageHeader` ([MessageFormats.h:247](lib/DuetSpiInterface/include/DuetSpiProtocol/MessageFormats.h#L247)).

**Protocol.** A new firmware→SBC request, `CanMessageSent = 7`, carrying a count and that many
`{ uint16 txToken; uint8 status; uint8 padding; }` entries — one packet per transfer rather than one
per message, because the controller can batch everything it sent since the last transfer. `status`
reuses `CanStatus` (`Ok`, `BusError`, `NoBuffer`), which already names all four failures above.

The alternative is to reuse `CANResponse` with a zero-length payload and a new `CanStatus::Sent`. It
is less code — the response ring, the resend path and DCS's `HandleCanResponse` matcher all exist —
but it is a packet per message, and `msgType`/`srcAddress` would be meaningless fields describing a
message that is not a response. Preference is the dedicated request; the reuse is worth having in
mind if the batching turns out not to be worth its complexity.

**What the ack means: accepted by the CAN controller, not on the wire.** `CanDevice::SendMessage`
returns once the peripheral has accepted the message into a tx buffer or FIFO, and its return value is
*the id of a message it cancelled to make room*
([CanDevice.h:229](lib/CoreN2G/src/CanDevice.h#L229)) — which is how a drop becomes visible at all.
Transmit-complete would need the tx event FIFO, which `SendMessage` does not expose, and would differ
only on a bus where nothing acknowledges. The ack's documentation has to say which of the two it is,
because "sent" is exactly the word that has already been over-claimed twice in this path.

Reporting the *cancelled* message is the fiddly half: the return value identifies it by CAN id, not by
token, so the controller needs a small in-flight table mapping the id it sent to the token it sent it
for. Without that, a cancellation can only be reported as an unattributed counter.

**DCS side.**

- `LinkInterface`: stop resolving a no-reply request at queue time
  ([:263-270](src/DuetControlServer/Link/LinkInterface.cs#L263-L270)); leave it in `CanRequests` until
  its token comes back in a `CanMessageSent`. Hop 1 alone is not enough for a CAN message — reaching
  the controller is not reaching the bus — but a hop-1 *drop* fails it immediately.
- Give the ack the same `CanRequestTimeout` bound a reply gets, so a lost ack fails the code instead
  of hanging it, and keep `Invalidate()` cancelling whatever is still outstanding.
- A **reply-expecting** request must not be completed by its ack — it is still waiting for the reply.
  But a non-`Ok` ack should fail it immediately rather than after 2 s, which is the second thing this
  buys: `NoBuffer` on a request whose reply can never be forwarded is exactly the controller's
  unimplemented `TODO`.

**Version.** This changes the SPI protocol, so `ProtocolVersion` goes to 9 in
[MessageFormats.h:29](lib/DuetSpiInterface/include/DuetSpiProtocol/MessageFormats.h#L29). Note that
`Consts.ProtocolVersion` in C# still reads **7** while both C++ sides read 8, and it is compared
against what the controller reports at
[LinkService.cs:280](src/DuetControlServer/Link/LinkService.cs#L280) — so DCS warns "Incompatible
firmware, please upgrade as soon as possible" on every connection today. Fix that in the same change
rather than adding a second stale copy.

### 4.2 `controller_disconnect` → `sys/controller-disconnect.g`

| | |
|---|---|
| Raised by | `LinkService.HandleConnectionLost` **and** `HandleControllerReset`, in both cases after `Invalidate()` |
| Trigger | `SbcConnectTimeout`/`SbcTransferTimeout` (500 ms) mid-transfer, `SbcConnectionTimeout` (4 s) between transfers, or a controller reboot detected by a sequence-number jump |
| `param.D` | `0` — no device |
| `param.B` | `0` (`CanId.MasterAddress`) — RRF always passes `B` so one macro works everywhere |
| `param.P` | `0` = transfer timeout, `1` = controller reset |
| `param.S` | The reason string from the native layer, e.g. `Transfer timeout` |
| Text | `Lost connection to the controller: <reason>` |
| Severity | error, matching `expansion_timeout` |
| Default action | Log the text. No pause: the link that would carry a pause is the thing that failed |

**Once per outage, whichever signal arrives first.** A reboot fast enough to fit inside one
`SbcConnectionTimeout` window produces `ControllerReset` with no preceding `ConnectionLost`, so both
have to be able to raise it; a slow outage produces both, and the second must not run the macro again.
`LinkService` therefore holds a `_controllerDown` flag: the first of the two signals raises the event
and sets it, the other is a no-op, and `HandleConnectionEstablished` clears it. The queue's similarity
rule is not enough on its own — it suppresses a duplicate only while the first is still pending, and a
long outage will have finished running the macro by then.

### 4.3 `controller_reconnect` → `sys/controller-reconnect.g`

| | |
|---|---|
| Raised by | `LinkService.HandleConnectionEstablished`, on every re-establishment after the first |
| `param.D` | `0` |
| `param.B` | `0` |
| `param.P` | `0` if the controller resumed without resetting, `1` if it had reset (from `HadReset()`) |
| `param.S` | Text as below |
| Text | `Connection to the controller re-established` (`, it had reset` when `P` is 1) |
| Severity | warning |
| **Default action** | **Run `config.g`** (falling back to `config.g.bak`), i.e. today's `RunStartupFilesAsync` |

The default action is the departure from RepRapFirmware, and it is deliberate: a rebooted controller
has lost every setting, so *something* must reconfigure it, and §2.2 shows what happens when nothing
does. If `controller-reconnect.g` exists it replaces that entirely — the macro is expected to call
`M98 P"config.g"` itself, and a machine that writes one is taking responsibility for the recovery
(homing, resuming, notifying) in exchange.

`IsDisconnected` is cleared and `runonce.g` is *not* re-run either way: it deletes itself on first
use and is not part of recovery.

### 4.4 What these macros can actually do

While the link is down, a code that needs the controller cannot succeed:

- Codes that expect a CAN reply fail after `CanRequestTimeout` (2 s,
  [LinkInterface.cs:286](src/DuetControlServer/Link/LinkInterface.cs#L286)).
- Fire-and-forget CAN messages are staged and, once §4.1.1 lands, **dropped** at the reconnect rather
  than replayed — and once §4.1.2 lands, the code that sent one is told so rather than being left to
  believe it worked.

`controller-disconnect.g` is therefore for SBC-side work: logging, `M291`-style notification once it
exists, plugin or webhook calls, tidying job state. The documentation for it has to say so, because a
macro full of `M568`/`G1` will simply time out one code at a time. `controller-reconnect.g` runs with
the link up and has no such restriction.

### 4.5 Relationship to `Invalidate()` and `ControllerReset`

The link layer's own recovery is unchanged and stays *ahead* of the event. Invalidation is a *fact
about the link* and must not wait for a macro; the event is the *notification and hook*.

```
slow outage                             fast reboot (inside one timeout window)
-----------                             ---------------------------------------
ConnectionLost                          (no ConnectionLost)
  Invalidate()                            —
  Raise(controller_disconnect) P=0        —
  _controllerDown = true                  —
...                                     ...
ControllerReset (if it rebooted)        ControllerReset
  Invalidate()                            Invalidate()
  already down -> no second raise         Raise(controller_disconnect) P=1
ConnectionEstablished                   ConnectionEstablished
  _controllerDown = false                 _controllerDown = false
  Raise(controller_reconnect)             Raise(controller_reconnect)
    macro, else config.g                    macro, else config.g
```

Both columns end with the same pair of events in the same order, which is what a macro author has to
be able to rely on. It also depends on the native ordering constraint in §4.1: `ControllerReset` must
be posted before `ConnectionEstablished`, or the right-hand column raises the disconnect *after* the
reconnect.

Ordering within the queue is no longer incidental either. `controller_disconnect` is priority 0 and
`controller_reconnect` priority 1 (§3.6), so a driver error queued just before the link dropped runs
after the machine has been restored rather than into a dead link.

---

## 5. Phases

Each phase is independently useful and independently testable.

### Phase A — variables and macro parameters ✅

- [x] A variable set per file, and per channel for codes without one (§3.4)
- [x] `var`, `set` and `global` statements store what they evaluate
- [x] `var.x`, `param.x`, `global.x`, `exists()` and `#` resolve in expressions
- [x] Block-scoped deletion, which no longer takes an SPI round trip
- [x] Carry a parameter set through `MacroRunner.TryRunAsync` into the macro's own set
- [x] Reuse it for MCODE_MIGRATION §9's code-named-after-itself macros (`M1234 X5` → `param.X`)
- [x] Unit tests: scoping, parameters beside a local of the same name, unknown-variable errors,
      global round-tripping
- [x] Resolve the whole object model in expressions, not just the SBC-flagged branches (§3.4.1)
- [x] Fail loudly on an expression that cannot be produced, instead of evaluating it to null
- [x] Arrays: `var.x[2]` as a value and as an assignment target, computed indices, and object model
      collections of scalars

### Phase B — the event system ✅

- [x] Schema: a `priority` per `EventType` value, and the local block at 128; the generator emits
      `EventTypePriority` beside the enum (§3.6)
- [x] `Events/EventQueue.cs`: priority order from the schema, head pinning, similarity suppression,
      counters, cap
- [x] `Events/EventText.cs`: port of `GetTextDescription` and `GetMacroFileName`, strings verbatim
- [x] `HeaterFaultType` in the schema, its text in DCS; `StandardDriverStatus` and its bit meanings in
      the schema, rendered by `Events/DriverStatusText.cs` (§3.5.1)
- [x] `Events/EventProcessor.cs`: hosted service on `CodeChannel.Autopause`
- [x] `Events/Extensions.cs` + registration in `Program.cs`
- [x] Route board CAN events into the queue (replacing the `LogWarning`)
- [x] Move the board watchdog from the controller: last-seen timestamp per board, sweep raising
      `expansion_timeout` and setting `BoardState.TimedOut`, forgotten on invalidation (§3.3.1)
- [x] Raise `expansion_reconnect` when a board announces while already `Running`
- [x] Delete the controller's `Event.cpp`, its sweep and its diagnostics line (§7)
- [x] `M122` line: `Events: %u queued, %u completed`
- [x] Unit tests: ordering, suppression, head pinning, macro-name mapping for all 13 types, and the
      two invariants CANlib asserts of the driver status masks

### Phase C — the link events ⬜

- [ ] Native: post `ConnectionLost` from `PrepareReconnect`, `ConnectionEstablished` on recovery
      **after** the `HadReset` check, carry `HadReset` (§4.1)
- [ ] Delete the unreachable transition check in `SbcInterface::Execute`
- [ ] Native: drop the staged TX buffer and the `m_outbound` ring in `PrepareReconnect`; complete
      request-bearing commands as `Cancelled`, count and log the rest (§4.1.1)
- [ ] Native: `outboundSeq` on every command, `OutboundDelivered(seq)` after a successful transfer and
      `OutboundDropped(from, to)` on a drop; move `EnableCan`'s completion off the staging path
      (§4.1.2 hop 1)
- [ ] Protocol: `FirmwareRequest::CanMessageSent = 7`, batched `{txToken, status}` entries;
      `ProtocolVersion` → 9 on both sides, and correct the stale C# `Consts.ProtocolVersion` (hop 2)
- [ ] Controller: acknowledge every SBC-originated CAN message from `SendCanRequest`, including the
      four paths that currently fail silently; map a cancelled CAN id back to its token
- [ ] DCS: resolve fire-and-forget CAN requests on the ack rather than at queue time, bound by
      `CanRequestTimeout`; fail reply-expecting requests early on a non-`Ok` ack or a hop-1 drop
- [ ] Test: send with CAN disabled and with a full tx buffer; expect the code to fail, not to succeed
      or to hang for 2 s. Pull the link mid-transfer; expect the staged commands to report dropped
- [ ] Schema: `controller_disconnect` = 128, `controller_reconnect` = 129, `"emit": ["csharp"]`,
      priorities 0 and 1; teach `compare-enums.py` to skip C++-excluded values; regenerate
- [ ] Raise `controller_disconnect` from both `HandleConnectionLost` and `HandleControllerReset`,
      once per outage via `_controllerDown`, with `param.P` = 0/1 (§4.2, §4.5)
- [ ] Raise `controller_reconnect` from `HandleConnectionEstablished`; default action = today's
      `RunStartupFilesAsync`
- [ ] Delete `Event.cpp`/`Event.h` and their two call sites from DuetCANMaster (§2.1, §7)
- [ ] Ship example macros and document the link-down restriction (§4.4)
- [ ] Test: pull the controller's power mid-print; expect one disconnect event, one macro run, and a
      reconnect that reconfigures the board
- [ ] Test: reboot the controller inside one `SbcConnectionTimeout` window; expect the same two
      events in the same order, with `param.P` = 1 on the disconnect

### Phase D — `M957` ⬜

- [ ] Port GCodes3.cpp:1306, including `-` → `_` on the `E` parameter and the
      `a similar event is already queued` warning
- [ ] Validate the event type (`Invalid event type` when the name is not one), but allow **any** valid
      type including the 128+ block — that is how the link macros get tested (§7)

### Phase E — the blocked default actions ⬜

Gated on MCODE_MIGRATION's `M291`/`M292` and `M25`:

- [ ] Message box for pausing events, titled `Printing paused` / `Event notification`
- [ ] `HeaterFault` / `FilamentError` pause via `pause.g`; `DriverError` pause without it

---

## 6. Status by event type

| Event | Macro | Arrives at DCS | Queued | Macro runs | Default action |
|---|---|---|---|---|---|
| `main_board_power_fail` | — | ⛔ never raised in RRF either | — | — | — |
| `expansion_reconnect` | `expansion-reconnect.g` | 🟡 announcement seen, state not compared | ⬜ | ⬜ | ⬜ log |
| `expansion_timeout` | `expansion-timeout.g` | ⬜ no watchdog | ⬜ | ⬜ | ⬜ log |
| `heater_fault` | `heater-fault.g` | 🟡 logged only | ⬜ | ⬜ | ⬜ blocked: pause |
| `driver_error` | `driver-error.g` | 🟡 logged only | ⬜ | ⬜ | ⬜ blocked: pause |
| `filament_error` | `filament-error.g` | 🟡 logged only | ⬜ | ⬜ | ⬜ blocked: pause |
| `driver_stall` | `driver-stall.g` | 🟡 logged only | ⬜ | ⬜ | ⬜ log |
| `driver_warning` | `driver-warning.g` | 🟡 logged only | ⬜ | ⬜ | ⬜ log |
| `mcu_temperature_warning` | `mcu-temperature-warning.g` | 🟡 logged only | ⬜ | ⬜ | ⬜ log |
| `overvoltage` | `overvoltage.g` | 🟡 logged only | ⬜ | ⬜ | ⬜ log |
| `undervoltage` | `undervoltage.g` | 🟡 logged only | ⬜ | ⬜ | ⬜ log |
| `controller_disconnect` | `controller-disconnect.g` | ⬜ blocked: §2.2 | ⬜ | ⬜ | ⬜ log |
| `controller_reconnect` | `controller-reconnect.g` | ⬜ blocked: §2.2 | ⬜ | ⬜ | ⬜ `config.g` |

Legend as [MCODE_MIGRATION.md](MCODE_MIGRATION.md) §1: ✅ done, 🟡 partial, ⬜ not started, ⛔ out of
scope.

Phase B finished all three middle columns for every event a board can send: the text and macro name
exist (§3.5.1, a test walks all thirteen), the CAN event message is decoded and queued, and the
processor runs the macro named after it with `param.D/B/P/S`. What is left per event is the **default
action** where a macro is absent, and only for the three that pause — they wait on `M291` and `M25`
(§3.5). The two `controller_*` rows wait on phase C, which is what raises them.

---

## 7. Decisions

**The event system moves to DCS entirely; DuetCANMaster's copy is deleted.** The consumer is the
AutoPause G-code channel, and that channel now lives in DCS. Leaving `Event.cpp` on the controller
would leave a queue nothing drains (§2.1) and two producers whose events cannot reach the macros named
after them. Both become DCS-side derivations (§3.3, §3.3.1) — not copies beside the originals, which
is why the controller's board-timeout sweep goes with them.

**`controller_disconnect` and `controller_reconnect` are event types, not a bespoke callback.** They
get the queue, the suppression rule, the macro-name convention, the `param.D/B/P/S` contract, `M957`
and the `M122` counters for free, and a machine configures them the same way it configures a heater
fault. The cost is the numbering question, answered in §3.6.

**They are numbered 128 and 129, outside CANlib's range, and priority is a separate property.** The
0-10 values are fixed by Duet3Expansion firmware already in the field, so priority cannot be the enum
value the way it is in RepRapFirmware — the two events that matter most would sort last. §3.6 holds
the ordering.

**`M957` validates the type but may raise any of them, including the link events.** RRF's check is
`EventType(name).IsValid()` and nothing further, and keeping that is what makes
`controller-disconnect.g` testable without pulling a cable. `M957` raises the *event* only: it does not
touch the link, invalidate anything or set `_controllerDown`, so a simulated disconnect runs the macro
against a live machine. That is the point, and it is worth saying in the user documentation.

**A controller reset raises `controller_disconnect` too.** A reboot fast enough to fit inside one
timeout window is a disconnect the timeout never saw, and a macro should not have to infer it from the
absence of an event. §4.5 has the two sequences and the once-per-outage rule that keeps them the same
shape.

**Staged outgoing data is dropped at the reconnect, not replayed.** §4.1.1. A controller that rebooted
has no state to receive it, and a controller that merely stalled is about to be reconfigured by
`config.g` anyway.

**Every outbound command is acknowledged, in two hops.** §4.1.2. "Sent" currently means a memcpy on
the SBC, which is why a dropped message cannot be reported — and why four failure paths in the
controller are silent today, including CAN never having been enabled. Hop 1 (delivered over SPI)
applies to every command and rides on a monotonic sequence number, so it costs one number per transfer
rather than an event per command; hop 2 (accepted by the CAN controller) applies to CAN messages and
needs the controller to answer. Hop 2 deliberately means *accepted by the peripheral*, not *on the
wire*: that is what `CanDevice::SendMessage` can report, and the difference only shows on a bus where
nothing answers.

**The expansion-board watchdog moves to DCS rather than being duplicated there.** §3.3.1. Nothing on
the controller reads the `TimedOut` state it produces, the event it raises has no consumer there, and
DCS already receives every message the sweep is derived from. Deriving it from the controller instead
would mean a new SPI message for a fact DCS can compute, and board state owned in two places.

**`controller_reconnect`'s default action is to run `config.g`.** RepRapFirmware has no equivalent
because it *is* the controller; here, recovery has to happen somewhere and a macro a machine can
delete is not a safe home for it. When the macro exists it replaces the default entirely — consistent
with every other event — and is expected to run `config.g` itself (§4.3).

**The default action for `controller_disconnect` is to log only.** Following §1.5's mechanism rather
than inventing a pause: a pause needs the link, and the job is already aborted by `Invalidate()`
before the event is raised (§4.5).

**Event macros run on `CodeChannel.Autopause`.** RRF uses its AutoPause buffer; DSF already has the
channel with a full pipeline behind it, and using it keeps event macros from consuming the job or
trigger channels — the same reason `config.g` runs on `Trigger`.

---

## 8. Open questions

- **What is `StatusMessageTimeoutMillis` on the SBC side?** The controller's value is chosen against
  its own cycle time. DCS sees the same reports one SPI transfer later, jittered by the transfer
  cadence, so the moved watchdog (§3.3.1) needs a value chosen against *that* — and a rebase rule
  after a reconnect precise enough that a board which reported just before an outage is not timed out
  the moment the link returns.
- **Does hop 1 give the move stream anything `MoveCompleted` does not?** Scheduled moves are the one
  outbound command with an existing completion path, so `OutboundDelivered` may be redundant for them
  — or may be the cheaper signal, since it costs nothing per move. Worth measuring rather than
  assuming, because it decides whether the seq sweep can replace anything or only adds to it.

---

## 9. After the migration

Not part of this work, recorded here because the migration is what found them.

### 9.1 A variable that refers to the object model

`var a = move.axes` is refused, and so is `global a = move`, because an object in an expression is a
stand-in that holds nothing (§3.4.1). RepRapFirmware stores an object model array as pointers into
its own model, which makes this work there:

```
var a = move.axes
echo var.a[0].letter
```

Doing the same here means holding a reference to a model object that the update task mutates and that
a reconfiguration - `M584`, or the invalidation a lost link performs - can detach from the model. The
variable would then read stale values rather than failing, which is the worst of the available
behaviours. A `global` could not hold one at all: it is serialised for the clients.

**What fits instead is a symbolic reference**: store the path, `move.axes[0]`, and resolve it on each
read. It serialises, so locals and globals behave alike; it holds no lock across time; and it cannot
go stale, because a path that stops resolving is an error rather than a wrong number. It diverges from
RepRapFirmware only where RRF is arguably wrong - after the machine is reconfigured under a stored
reference.

The work is mostly in one place. `VariableStore.TrySplitIndexedName` accepts a name and indices;
a symbolic reference needs it to accept a field suffix too, so that `var.a[0].letter` parses, and
`#` and `exists()` have to route through the same resolution. That grammar has two readers today and
this would be the third, which is the argument for doing it once rather than growing another.

### 9.2 `input`

RepRapFirmware's `input` constant is the value entered in an `M291` message box
([ExpressionParser.cpp:1836](lib/RepRapFirmware/src/GCodes/GCodeBuffer/ExpressionParser.cpp#L1836)).
`M291` is not ported - it is what §3.5's message-box default action waits for - so `input` is left to
forward. `result`, the other constant RRF has and this did not, is implemented: `LastCodeResult`
records how the last code on each channel ended, set where the Executed stage handles the reply,
which is where RepRapFirmware sets its own.
