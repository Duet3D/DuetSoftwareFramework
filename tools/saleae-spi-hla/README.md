# Duet SBC SPI — Saleae High Level Analyzer

A [Saleae Logic 2](https://www.saleae.com/) High Level Analyzer (HLA) that decodes
the DuetSoftwareFramework SBC ↔ RepRapFirmware SPI link. It annotates the framing
of every transfer:

- **Transfer headers** — `formatCode`, `numPackets`, `protocolVersion`,
  `sequenceNumber`, `dataLength`, header/data CRC, and from protocol 8 the
  controller's step clock and accumulated movement delay (24 bytes for
  protocol ≥ 8, 16 bytes for protocol 4–7, 12 bytes for the legacy CRC16
  protocol).

  The step clock is shown raw, in the controller's tick units. What a capture is
  usually being read for is whether it advances smoothly from one transfer to
  the next: the SBC fits its own clock to these readings and schedules every move
  by absolute start time in the result, so a reading that jumps or stalls is a
  reading that will put moves in the wrong place.
- **Header / data responses** — `Success`, `BadFormat`, `BadProtocolVersion`,
  `BadDataLength`, `BadHeaderChecksum`, `BadDataChecksum`, `BadResponse`, and the
  `LowPin`/`HighPin` stuck-line values.
- **Data packet headers** — `Request` (decoded to its name per direction), `Id`,
  `Length`, `ResendPacketId`.
- **Request-specific payload headers** — the fixed header that immediately
  follows each packet header, decoded per request type:

  | Direction | Request | Payload header decoded |
  | --------- | ------- | ---------------------- |
  | MOSI | `ConfigCAN` | `ConfigCanHeader` (channel, useFd, rateMul) |
  | MOSI | `EnableCAN` | `EnableCanHeader` (channel, enable) |
  | MOSI | `ScheduleMove` | `ScheduleMoveHeader` (moveId, driver count, flags, start time, phase durations, distances, speeds, accelerations) plus every `ScheduleMoveDriver` that follows it |
  | MOSI | `SendCANMessage` | `SendCanMessageHeader` (txToken, CAN type, replyType, len, dst, isResponse) |
  | MOSI | `Message` | `MessageHeader` (destination flags, length) |
  | MISO | `CodeBufferUpdate` | `CodeBufferUpdateHeader` (bufferSpace) |
  | MISO | `Message` | `MessageHeader` (destination flags, length) |
  | MISO | `CANResponse` | `CanResponseHeader` (txToken, CAN type, len, src, flags, status) |

  CAN message types (`CanMessageType`), message destination flags
  (`MessageTypeFlags`), CAN statuses (`CanStatus`) and schedule-move flags
  (`ScheduleMoveFlags`) are decoded to their names.

The variable data *after* each payload header (CAN payload bytes, message text,
etc.) is left undecoded, which keeps the annotation readable. `ScheduleMove` is
the exception: its `ScheduleMoveDriver` records are a fixed-size array, so each
one gets its own annotation. Requests without a fixed payload header
(`EmergencyStop`, `Reset`, `WriteIap`, `StartIap`, `MotionStopped`,
`ResendPacket`) show just the packet header.

Both directions are decoded from the single full-duplex capture:

| Line | Direction | Request names |
| ---- | --------- | ------------- |
| MOSI | SBC (DuetControlServer) → controller (DuetCANMaster) | `SbcRequests.Request` |
| MISO | controller → SBC | `FirmwareRequests.Request` |

## How it maps to the wire

A full transfer is up to four sub-exchanges, each framed by its own chip-select
assertion and gated by `TfrRdy` (see
[`spi-state-machine.md`](../../src/Documentation/articles/spi-state-machine.md)):

| # | Sub-exchange | Size | Decoded as |
| - | ------------ | ---- | ---------- |
| 1 | Header | 24 B (16 B if protocol 4–7, 12 B if protocol < 4) | `header` |
| 2 | Header response | 4 B | `response` |
| 3 | Data | `max(rxLength, txLength)` | one `packet` per packet header |
| 4 | Data response | 4 B | `response` |

Sub-exchanges 3 and 4 are skipped when neither side has data.

Classification is by size plus a positive format-code test: a header's first
`uint32` is `0x0007nn5F`, whose low byte is the format code (`0x5F`/`0x60`). No
response code and no packet request aliases that, so a header is never confused
with a response or a data phase.

## Requirements / wiring

Each sub-exchange is a **separate chip-select assertion**, so the underlying SPI
analyzer **must have its Enable (CS) line connected** — the HLA uses the
enable/disable frames to find sub-exchange boundaries. Configure the built-in
**SPI** analyzer as:

- **8 bits per transfer**, **MSB first**
- **Enable** line connected to the Duet SPI CS/NSS pin
- CPOL/CPHA to match the link (mode 0)

## Installation

1. In Logic 2, open **Extensions** → the **⋮** menu → **Load Existing Extension…**
2. Select `tools/saleae-spi-hla/extension.json` from this repository.

## Usage

1. Add the built-in **SPI** analyzer to your capture and configure it as above.
2. Add the **Duet SBC SPI** analyzer and set its input to the SPI analyzer.
3. Set **Direction to decode** to `MOSI (SBC→RRF)` or `MISO (RRF→SBC)`.
   Annotations are exactly byte-aligned to the SPI bytes for that one line.
   Because SPI is full-duplex, both streams share the same byte times and a
   Saleae HLA can only emit one non-overlapping stream, so each instance decodes
   a single direction — add the analyzer twice (one MOSI, one MISO) to see both.

Decoded frames also appear in the **Data** table, where every field of each
header / response / packet is listed.

## Notes

- Constants are mirrored from the protocol source of truth. If the protocol
  changes, update the tables at the top of
  [`HighLevelAnalyzer.py`](HighLevelAnalyzer.py) from:
  - `lib/DuetSpiInterface/include/DuetSpiProtocol/MessageFormats.h` — the wire
    structs (`SpiTransferHeader`, `PacketHeader`, and the per-request payload
    headers), both request enums, `ScheduleMoveFlags` and `CanStatus`. This one
    header is shared by DuetCANMaster and DuetSbcInterface, so it is the only
    place a layout is defined
  - `src/DuetControlServer/Link/Protocol/Shared/Consts.cs` — format codes and
    transfer responses
  - the `CanMessageType` and `MessageTypeFlags` enums used to name fields
  - `lib/DuetSpiInterface/include/DuetSpiProtocol/MessageFormats.h` for
    `ScheduleMoveHeader`, `ScheduleMoveDriver` and `ScheduleMoveFlags`
- If a capture starts in the middle of a data phase (no preceding header seen),
  the analyzer walks packet headers until the buffer is consumed instead of using
  the header's `numPackets`/`dataLength`.
