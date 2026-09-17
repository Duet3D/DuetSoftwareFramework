# Handling what an expansion board replies

Plan for making every G-code handler treat an expansion board's reply the same way, so that the
result a user sees does not depend on which handler happened to send the message.

The rule this works towards is one sentence: **an error stops the handler, a warning is reported
alongside whatever else the code produced, and a success carries on.** Nothing here changes what a
board is asked; it changes only what is done with the answer.

---

## 1. The problem

Thirty-two places took a `CanResponse`, in six shapes, and the shape was chosen per call site rather
than per intent.

| | Shape | Sites |
| --- | --- | --- |
| **A** | `return response.ToMessage();` and nothing after it | 17 |
| **B** | `reply.Type == MessageType.Error ? reply : null` | 6 |
| **C** | error stops the handler, success does follow-up work, the reply is carried either way | 3 |
| **D** | error throws `GCodeException` | 3 |
| **E** | log only, because the caller is teardown | 2 |
| **F** | `replies.ToMessage()` over several boards | 11 |

Shape F consumes what the others produce rather than reading a `CanResponse` itself, so the counts
overlap by design.

**A** is correct as it stands. **C** is the shape the rule above describes, and it is the smallest
group. **D** is correct and deliberate: a move must not run against an endstop that was never armed.
**E** is correct: `InputMonitors.ReleaseAsync`, `StallArming.ReleaseAsync` and `ProbeArming.StopAsync`
run after the move they clean up after, so there is nobody left to report to.

**B is the defect.** Every one of its six sites discards a warning:

| Site | What is lost |
| --- | --- |
| `MCodeHandler.Fans.cs` `SendFanParametersAsync` | a warning about a fan's configuration |
| `MCodeHandler.Motion.cs` `SendPhaseSteppingAsync` | a warning about a phase stepping value (M970) |
| `MCodeHandler.Spindles.cs` `CreateSpindlePortAsync` | a warning about a spindle's port |
| `FanManager.SetSpeedAsync` | a warning about a fan speed (M106) |
| `GpioManager.WriteAsync` | a warning about an output (M42, M280) |
| `HeatManager.SetTemperatureAsync` | a warning about a setpoint (M104, M140, M141, M143) |

The last three cannot do otherwise: they return `string?`, so a warning has nowhere to go even in
principle. `SpindleManager.SetSpeedAsync` and `StopAsync` return `string?` for the same reason,
inherited from `GpioManager`.

### 1.1 Four spellings of one test

```csharp
reply.Type == MessageType.Error              // most of B and C
reply.Type != MessageType.Error              // MCodeHandler.Probes.cs
response.Severity != MessageType.Success     // InputMonitors, StallArming.ReleaseAsync
response.Severity == MessageType.Warning     // InputMonitors
```

The first two are one test and its negation. The third is a **different** test: `Severity` maps
`Warning` and `WarningNotSupported` to `Warning`, so `!= Success` also fails a board that did what it
was asked and had something to say. `StallArming.ReleaseAsync` used it where it meant the first, and
so logged a warning about a board that had in fact disarmed.

`CodeResultExtensions.Succeeded()` is the predicate all four are reaching for. It mirrors
`GCodeResult::Succeeded()` in CANlib's `GCodeResult.h`, and it has no callers.

### 1.2 Two send shapes, one of them unused

`LinkInterface.SendCanRequestAsync` returns a `Message` and its own remarks call it "the shape a
request takes". It has five callers, all in `RemoteDrivers.cs`. The other seventeen sites of shape A
hand-roll `SendCanMessageAsync(...).ToMessage()` instead, which is the same thing written out.

### 1.3 No stated rule for a loop over several boards

Three loops, three behaviours, all defensible and none written down:

| Code | Behaviour |
| --- | --- |
| M915 (`ConfigureStallDetectionAsync`) | collects every board's reply and continues past a refusal |
| M574 (`CreateEndstopMonitorsAsync`) | collects, continues, and releases the axis' ports on a refusal |
| M122 B (`ReportBoardDiagnosticsAsync`) | returns at the first refusal |

---

## 2. The two levers, which are not the same lever

"An error stops the command" and "an error stops the file" are separate, and the distinction decides
what shape D means.

Returning `Message(MessageType.Error, ...)` from a handler stops the handler, sets `result` to 2 for
meta G-code (`LastCodeResult`), and records `state.startupError` while a start-up file is running. It
does **not** abort the macro: `MacroFile.cs` reports the error and reads the next code. Only a thrown
exception reaches `Abort()`.

So the rule is:

- **Return** the error. This is what "an error stops the command" means, and it is right for every
  handler whose work ends with the reply.
- **Throw** only where carrying on would be unsafe, which today is arming an endstop, a probe or a
  stall detector before a move. That is shapes D, and it stays three sites.

---

## 3. Design

### 3.1 One predicate

```csharp
// CanResponse
public bool Succeeded => ResultCode.Succeeded();

// CanReplies, for a handler that has only the message by the time it judges it
public static bool Succeeded(this Message reply) => reply.Type != MessageType.Error;
```

Every one of the four spellings in §1.1 becomes one of these two, and no call site writes the
comparison out. Both are named for the question being asked rather than for the value being compared,
so a reader does not have to work out which of `Type` or `Severity` means what.

Two are needed because a reply is a `CanResponse` when it arrives and a `Message` by the time several
of them have been collected, or where the refusal is this side's own rather than a board's:
`Message(MessageType.Error, "Fan 3 not found")` never reached the bus and has no result code to ask.
Where a handler holds the `CanResponse`, that is the one to use: it reads the board's own result code
rather than the message it was rendered into.

### 3.2 Three send shapes, one per intent

| Intent | Method | Returns |
| --- | --- | --- |
| The board's answer is the code's answer | `SendCanRequestAsync` | `Message` |
| As above, for a G-code repackaged as a generic message | `SendCodeRequestAsync` | `Message` |
| The caller needs `Extra`, the payload, or the result code itself | `SendCanMessageAsync` | `CanResponse` |

`SendCodeRequestAsync` is new and is to `SendCodeAsync` what `SendCanRequestAsync` is to
`SendCanMessageAsync`. `ReportCanConfigAsync` returns `Message` for the same reason: both of its
callers immediately convert.

Shape A then reads as one line at every site, and a handler that keeps a `CanResponse` is doing so
because it needs something only a `CanResponse` carries.

### 3.3 The throw stays at the call site

There is no send method that throws, because at every one of the three sites something must be
recorded *before* the reply is judged:

- `StallArming.ArmAsync` adds the board to `state.ArmedBoards` before testing the reply, because a
  board that refused one driver may already have armed another and the release has to reach it.
- `ZProbeEndstopKind.PrepareAsync` adds the monitor to `state.ArmedProbes` before sending, for the
  same reason.
- `ProbeArming.ChangeAsync` is shared by `StartAsync`, which must throw, and `StopAsync`, which must
  log, so the send itself cannot decide.

A send that threw would skip the recording and leak an armed board. What the three sites share is the
refusal, not the send, so that is what is factored out:

```csharp
// CanReplies
public static Message OrRefuse(this Message reply)
{
    if (reply.Type == MessageType.Error) { throw new GCodeException(reply.Content); }
    return reply;
}
```

### 3.4 Shape B is deleted

Returning `null` for "the board said something but it was only a warning" is the bug. The three
handler sites return the `Message`. `CanReplies.ToMessage()` already drops empty successes when it
collects several replies, and an empty success returned on its own already means "done, nothing to
report", so returning the message costs no extra output on the success path and stops losing the
warning on the other one.

### 3.5 The managers return `Message`

`FanManager.SetSpeedAsync`, `GpioManager.WriteAsync`, `HeatManager.SetTemperatureAsync`,
`SpindleManager.SetSpeedAsync` and `SpindleManager.StopAsync` change from `string?` to `Message`.
Their own validation failures become `Message(MessageType.Error, ...)`, an empty `Message` means
nothing to report, and a board's warning now survives the return type.

Their fourteen call sites change from `is string error` to testing `Type`, or to collecting into the
reply list the handler already has.

### 3.6 The loop rule, stated

- **A warning is always collected and carried.** Whichever board raised it, the code it came from
  reports it. This is the half that is wrong today, and it is the half that does not vary by code.
- **Whether an error stops the loop follows RepRapFirmware for that code**, and is stated in the
  method's remarks rather than left to be read off the control flow. The three loops above already
  match RRF; what changes is that the choice is written down.

M574's port release is neither of those: it is what an axis that could not be set up whole has to do,
and it stays where it is.

---

## 4. What landed

| | Change |
| --- | --- |
| ✅ | System tests for the warning path of each of §1's six B sites, written first |
| ✅ | `CanResponse.Succeeded`, `CanReplies.Succeeded` and `CanReplies.OrRefuse`; the four spellings of §1.1 replaced, and no call site compares a `MessageType` |
| ✅ | `SendCodeRequestAsync`, `ReportCanConfigAsync` answering with a `Message`, shape A migrated |
| ✅ | The five manager signatures and their call sites |
| ✅ | Shape B deleted; the loop rule written into the three loops' remarks |

`CodeResultExtensions.Succeeded()` is `result < CodeResult.Error`, which is CANlib's own range
(`rslt <= warningNotSupported` in `GCodeResult.h`) rather than a list of values.

That leaves one value the two predicates in that file disagree about, which §5's D4 records.

The tests are in `FanPortCodeTests` (M106 speed, M106 configuration, M42), `HeatCodeTests` (M140),
`ToolSpindleModeCodeTests` (M950 R) and `StallDetectionAndPhaseStepTests` (M970). Each scripts the
fake controller to answer one message type with `CodeResult.Warning` and asserts the warning is the
code's result. Every one of them would have passed silently before, because the code returned an
empty success.

---

## 5. Decisions

**D1. A failed result code returns, it does not throw, except when arming.** §2. This keeps the RRF
parity chosen for `CanStatus.ResponseTimeout` and `CanStatus.DispatchTimeout`, which report a board
that did not answer rather than aborting the file it was asked from.

**D2. `Severity` stays, `Succeeded` is what code branches on.** `Severity` is how a reply is
*reported* and has to distinguish warning from error for that. `Succeeded` is whether the board did
what it was asked, which is a two-way question, and branching on a three-way value to answer a
two-way question is what produced §1.1.

**D3. The managers return `Message`, not `CanResponse`.** A manager's failure is not always a board's:
"Fan 3 not found" never reached the bus. `Message` is the type that can carry both.

**D4. `Succeeded` follows CANlib's range and therefore parts company with `ToMessageType` on
`NotFinished`.** CANlib's `Succeeded()` is `rslt <= warningNotSupported`, so `notFinished` counts as
success; `ToMessageType` deliberately reports it as an error, because a board cannot answer with it
and a reply that carried it would mean the board had not dealt with the request. A reply carrying it
would therefore be reported as an error while the handler carried on: `M558` would record a monitor
that was never created, and `M122 B` would keep asking for parts of a report that does not exist. It
also splits the two predicates of §3.1, since `Message.Succeeded()` reads `ToMessageType`'s answer and
`CanResponse.Succeeded` reads CANlib's. No board sends it, so this is a latent disagreement rather
than a live defect. Open: either exclude it from `Succeeded` or let `ToMessageType` treat it as CANlib
does, but the two should not differ.

---

## 6. Out of scope

`LinkInterface.ConfigCanAsync` discards its `CanResponse`. The message is sent with
`CanMessageType.NoReply`, because a board changing its own CAN address cannot answer on the address
the request went to, so there is no board answer to discard. It is left alone.
