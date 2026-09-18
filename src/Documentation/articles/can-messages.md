# CAN messages over the link interface

Every driver, heater, fan and sensor lives on a CAN-connected expansion board, and
DuetControlServer talks to all of them by tunnelling CANlib messages through the SBC↔controller
link. This article covers the runtime path a message takes and how the message definitions are kept
in step with CANlib.

Board 0 is the controller itself and owns no hardware, so a message always addresses an expansion
board; see [Differences from RepRapFirmware](rrf-differences.md) for what that rules out.

## The runtime path

A CAN message is a CANlib message *body* wrapped in a link-layer envelope and handed to the native
link, which stages it into the next SPI transfer. DuetCANMaster puts it on the bus.

### Sending

1. Callers use
   [`LinkInterface.SendCanMessageAsync<TReq>(dstAddress, message, replyType, …)`](src/DuetControlServer/Link/LinkInterface.cs).
   The typed overload marshals the `TReq` struct to a byte payload and derives the `CanMessageType`
   from `TReq.MessageType`; a raw overload takes the payload directly.
2. A [`CanRequest`](src/DuetControlServer/Link/Requests/CanRequest.cs) is created with a
   monotonically increasing `TxToken` and added to `LinkInterface.CanRequests`.
3. When a reply is expected, the request id placeholder in the first two payload bytes is set to
   `0x7FF`, which is how the controller is asked to allocate a real request id over it. A message
   that expects no reply carries no request id at all.
4. `NativeLink.QueueCanMessage(...)` puts it in the native outbound ring and returns the **sequence
   number** the command was given there. The transfer loop stages it into the next transfer.

### What "sent" means, in two hops

The distance between the SBC and the bus is a memcpy, an SPI transfer, the controller's packet
handler and the CAN peripheral, so a request is acknowledged twice:

| Hop | Established by | Resolves |
|---|---|---|
| **Delivered over SPI** | A transfer completing with the command in it, reported once per transfer as `OutboundDelivered(seq)` | Every outbound command. `NativeLink.WaitForDeliveryAsync(seq)` is what a no-reply CAN request awaits |
| **Accepted by the CAN controller** | The controller reporting `{txToken, status}` batches back over SPI, handled by `LinkInterface.CompleteCanMessageSent` | CAN messages. A non-`Ok` status fails the request immediately, including one still waiting for a reply |

The outbound path is FIFO end to end, which is what lets one sequence number describe every command
up to it rather than costing an event per message — the move stream could not afford the latter.

Hop 2 means *accepted by the peripheral*, not *on the wire*: that is what `CanDevice::SendMessage`
can report, and the difference only shows on a bus where nothing answers. It is worth being exact
about, because it is the failure the four silent paths in the controller's `SendCanRequest` used to
hide — CAN never enabled, a bad request id placeholder, no free pending-request slot, and an older
message cancelled to make room.

When the link drops, whatever is staged or queued is **dropped rather than replayed**: a controller
that rebooted has no state to receive it. Everything holding a request id is completed as
`Cancelled`, and `LinkInterface.Invalidate()` cancels the CAN requests still outstanding.

### Receiving and reassembly

The controller forwards expansion-board traffic to DCS as `CanResponse` events, handled by
[`LinkService.HandleCanResponse`](src/DuetControlServer/Link/LinkService.cs):

1. Read the `CanResponseEvent` header and the single-frame payload behind it.
2. Match the `TxToken` to a pending `CanRequest`. Traffic that is not a reply to one of our requests
   — events, status reports, announcements — carries the reserved token
   `LinkInterface.UnsolicitedTxToken` (`0xFFFF`) and goes to `HandleUnsolicitedCanMessage`. Token
   allocation always skips `0xFFFF`, so a real request can never collide with an unsolicited
   message.
3. Check the reply's `MsgType` against the request's `ReplyType`; a mismatch faults the request.
4. Propagate a non-`Ok` `CanStatus` immediately.
5. Otherwise decode the fragment with
   [`CanFragmentation.Parse(replyType, payload)`](src/DuetControlServer/Link/Protocol/CanMessages/CanFragmentation.cs),
   append it with `CanRequest.AddFragment(...)`, and complete the request once a fragment arrives
   with `moreFollows` clear.

> **The controller does not reassemble fragmented replies.** Each `CanResponse` carries exactly one
> CAN frame; DSF stitches the fragments back together. Fragmentation is message-type specific:
> `CanMessageStandardReply` carries `fragmentNumber`/`moreFollows` in its header word, and a reply
> type with no explicit scheme is treated as a single, final fragment.

Unsolicited messages are deserialized through `CanMessageSerializer` and routed to the manager that
owns them — board announcements, status reports, input changes and events all arrive this way; see
[`ExpansionBoardManager`](src/DuetControlServer/Link/Expansion/ExpansionBoardManager.cs).

### Deserializing a payload

[`CanMessageSerializer`](src/DuetControlServer/Link/Protocol/CanMessages/CanMessageSerializer.cs) is
the one way raw payload bytes become a message body:

- `Deserialize<T>(payload)` when the type is known,
- `Deserialize(messageType, payload)` when only the `CanMessageType` is,
- `TryDeserialize(messageType, payload, out message)` for a non-throwing lookup.

The mapping is discovered by reflection over every value type implementing `ICanMessage`, reading
each type's static `MessageType`, so a newly generated body becomes deserializable without anyone
editing a switch table.

## The message definitions are generated

The message bodies used to exist twice: as packed C++ structs in CANlib's `CanMessageFormats.h`, and
as a hand-written C# mirror. Keeping two independent transcriptions of ~60 bit-packed wire formats in
step is a mistake that shows up as corrupted messages on the bus rather than as a compile error, so
both are now generated from one neutral description.

The schema is [`can-messages.json`](src/DuetCanMessage.SourceGenerators/Schema/can-messages.json) and
the generator is [`DuetCanMessage.SourceGenerators`](src/DuetCanMessage.SourceGenerators), a
standalone C# tool. It emits:

| Output | For |
|---|---|
| `generated/cpp/CanMessageFormats.h`, `CanMessageGenericTables.h`, `CanSettings.h` | Drop-in replacements for CANlib's own headers |
| [`Link/Protocol/CanMessages/Generated/*.cs`](src/DuetControlServer/Link/Protocol/CanMessages/Generated) | The message bodies, generic parameter tables and buffers DCS uses |
| [`Link/Protocol/Shared/CanMessageType.g.cs`](src/DuetControlServer/Link/Protocol/Shared/CanMessageType.g.cs) and the other `*.g.cs` enums beside it | CANlib's `CanId.h` and the enums that travel in messages |
| `src/UnitTests/Link/CanMessageLayout.g.cs`, `CanGenericTableLayout.g.cs`, and the C++ layout probes | Conformance harnesses asserting the emitted layouts are what both sides compute |

```sh
make can-messages          # regenerate everything from the schema
make can-messages-check    # fail if the checked-in output is stale
make can-messages-verify   # check the generated C++ layouts against CANlib's own header
```

The generated files are checked in, so neither the DSF build nor the CANlib build runs the tool. A
DCS build does run `--check` first and fails if the checked-in output no longer matches the schema,
so editing the schema cannot quietly leave the generated files behind; regenerating stays a
deliberate `make can-messages`, because a build that rewrote tracked source during every publish and
deploy would be worse than the problem.

The [`lib/CANlib`](lib/CANlib) submodule is the validation oracle rather than the source:
`verify-cpp-layout.sh` compiles both and compares the layouts, `compare-enums.py` compares the
enumerations in both directions, and `compare-constants.py` compares the constant groups —
including string tables, which have no layout to compare and are checked entry by entry.

### The layout conventions

These are the generator's output contract, and the reason the C# side can be read and written with
`MemoryMarshal`:

- `[StructLayout(LayoutKind.Sequential, Pack = 1)]`, matching `__attribute__((packed))`.
- C++ bitfields become a private backing integer plus properties doing shift and mask. The lowest
  declared C++ field occupies the least-significant bits, which is what a little-endian target does —
  both the SBC and the expansion boards are little-endian.
- Fixed C arrays become blittable `[InlineArray(N)]` buffers, so the struct stays unmanaged.
- Each body implements `ICanMessage` and exposes a static `MessageType`.
- A body ending in a trailing array may arrive shorter than the struct's maximum size; the missing
  tail bytes are read as zeroes.

The generator computes the byte and bit layout itself rather than trusting a compiler: a bit cursor
runs through the struct, a bitfield advances it by its width with no padding and no regard for its
declared storage type, and a non-bitfield member first rounds the cursor up to a byte boundary. That
is what makes bitfields straddling byte boundaries — which several Duet messages do — come out the
same on both sides.

## Further reading

- [Endstops](endstops.md) — the one path where CAN latency is the design constraint
- [Firmware link](firmware-link.md) — the transport these messages ride on
- [Differences from RepRapFirmware](rrf-differences.md) — why only the CAN path exists here
