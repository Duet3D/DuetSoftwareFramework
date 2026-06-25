# CAN messages over the link interface

DSF can tunnel CAN messages to/from Duet expansion boards through the SBC↔firmware link
(SPI or USB). This document describes the runtime path that exists today and the planned
single-source-of-truth code generation for the CAN message bodies.

## Runtime path (implemented)

A CAN message is a CANlib message *body* wrapped in a link-layer envelope and sent as an SBC
packet.

### Sending

1. Callers use `LinkInterface.SendCanMessageAsync<TReq>(dstAddress, message, replyType, flags, ct)`
   (or the raw `SendCanMessageAsync(messageType, replyType, dstAddress, payload, …)` overload).
   The typed overload marshals the `TReq` struct to a byte payload and derives the
   `CanMessageType` from `TReq.MessageType`.
2. A `CanRequest` ([`Link/Requests/CanRequest.cs`](../../src/DuetControlServer/Link/Requests/CanRequest.cs))
   is created with a monotonically increasing `TxToken` and queued in `LinkInterface.CanRequests`.
3. On the transfer thread, `LinkService.SendCanMessages()` drains unsent requests, calling
   `ILinkAdapter.WriteCanMessage(...)` which serializes a
   [`SendCanMessageHeader`](../../src/DuetControlServer/Link/Protocol/SbcRequests/SendCanMessageHeader.cs)
   (request type `SendCANMessage`) plus the payload via `Writer.WriteCANMessage`.
4. Requests with `ReplyType == CanMessageType.NoReply` (`0xFFFF`) complete as soon as they are
   written. Requests expecting a reply stay pending until their reply is fully received.

### Receiving and reassembly

The firmware forwards expansion-board replies as `CANResponse` packets
([`CanResponseHeader`](../../src/DuetControlServer/Link/Protocol/FirmwareRequests/CanResponse.cs)).
`LinkService.HandleCanResponse()`:

1. Reads the header + single-frame payload via `ILinkAdapter.ReadCanResponse(...)`.
2. Matches the `TxToken` to a pending `CanRequest`. Messages that are not a reply to one of our
   requests (e.g. events, status reports, announcements) carry the reserved token
   `LinkInterface.UnsolicitedTxToken` (`0xFFFF`) and are routed to `HandleUnsolicitedCanMessage(...)`.
   Token allocation (`LinkInterface.NextCanTxToken()`) always skips `0xFFFF` so a real request can
   never collide with an unsolicited message.
3. Validates the reply's `MsgType` equals the request's `ReplyType` (mismatch faults the request).
4. Propagates a non-`Ok` `CanStatus` immediately.
5. Otherwise decodes the fragment via `CanFragmentation.GetFragmentInfo(...)`, appends it with
   `CanRequest.AddFragment(...)`, and completes the request once the final fragment
   (`moreFollows == false`) has arrived.

> **The HAT no longer reassembles fragmented replies.** Each `CANResponse` packet carries exactly
> one CAN frame (`DataLength <= 64`); DSF stitches fragments back together. Fragmentation is
> message-type specific (see `CanFragmentation`); `CanMessageStandardReply` carries
> `fragmentNumber`/`moreFollows` in its header word, and reply types without an explicit scheme are
> treated as a single, final fragment.

## CAN message bodies

The message body structs in [`Link/Protocol/Can/`](../../src/DuetControlServer/Link/Protocol/Can)
mirror the definitions in CANlib's `CanMessageFormats.h` (vendored as a git submodule under
[`lib/CANlib`](../../lib/CANlib)). Layout conventions — these are also the **output contract for the
generator below**:

- `[StructLayout(LayoutKind.Sequential, Pack = 1)]` to match `__attribute__((packed))`.
- C++ bitfields → a private backing integer field plus C# properties doing shift+mask. The lowest
  declared C++ field occupies the least-significant bits (matches little-endian targets — both the
  SBC and the expansion boards are little-endian).
- Fixed C arrays → blittable `[InlineArray(N)]` buffers, so the whole struct stays unmanaged and can
  be (de)serialized with `MemoryMarshal.Read`/`Write` and `Unsafe.SizeOf`.
- Each body implements `ICanMessage` exposing a static `MessageType`.

Only a representative subset is hand-written today (`CanMessageReset`, `CanMessageStandardReply`,
`CanMessageAnnounceV1`) to prove the end-to-end path and give the generator a concrete target.
`src/UnitTests/SPI/CanMessages.cs` asserts each struct's size matches the CANlib `sizeof` and that
the bitfield/reassembly behaviour is correct.

## Single source of truth (planned)

The long-term goal is to avoid maintaining the CAN message definitions in two places. The chosen
direction is a **neutral schema that generates both** the C++ header and the C# structs:

- A YAML schema describes each message: name, `CanMessageType`, ordered fields with type/bit-width,
  arrays, and the expected reply type.
- A small standalone generator (Python fits the existing toolchain — cf.
  [`scripts/create_settings.py`](../../scripts/create_settings.py)) emits **both** a
  `CanMessageFormats.h`-style C++ header and the C# structs in `Link/Protocol/Can/`, using the
  conventions above.
- The [`lib/CANlib`](../../lib/CANlib) submodule (pinned at tag `3.7-docker`) is the validation
  oracle: a CI check compares the generated C++ against the submodule and asserts each C#
  `Unsafe.SizeOf<T>()` matches the corresponding `sizeof`, until the schema fully supersedes the
  hand-written header.

Note the existing `// TODO either auto generate this from CANlib or autogenerate CANlib from this`
marker in
[`CanMessageType.cs`](../../src/DuetControlServer/Link/Protocol/Shared/CanMessageType.cs); the
`CanMessageType` enum (CANlib's `CanId.h`) should be generated by the same pipeline.
