# SBC

`src/SBC/` is the firmware side of the link to DuetSoftwareFramework (DSF). It runs the transfers, dispatches the packets DSF sends, and queues the reports going back.

## What This Module Owns

- The connection and transfer lifecycle.
- SPI framing, and USB framing where `SUPPORTS_SBC_OVER_USB` is enabled.
- Dispatching `SbcRequest` packets from DSF.
- Generating `FirmwareRequest` packets for DSF.
- Recovery from timeouts, resets, and checksum failures.

## Key Files

| File | Purpose |
|---|---|
| [SbcInterface.cpp](SbcInterface.cpp) / [SbcInterface.h](SbcInterface.h) | Main SBC task loop, request dispatch, and recovery. |
| [DataTransfer.cpp](DataTransfer.cpp) / [DataTransfer.h](DataTransfer.h) | Transport state machine, SPI/USB transfer execution, packet read/write helpers, and CRC validation. |
| [SbcMessageFormats.h](SbcMessageFormats.h) | Firmware-side names for the shared protocol, and the constants local to this firmware. |

The wire format is not defined here. It lives in `lib/DuetSpiInterface/include/DuetSpiProtocol/MessageFormats.h`, shared with the SBC side so that both move together.

## Transfers

DataTransfer handles the transport mechanics, and SbcInterface gives the packets their meaning. DSF is the SPI master. A full transfer exchanges headers, header responses, data, and data responses, and each step is gated by the transfer ready pin.

The link is data-gated: a transfer happens only when one side has something to send. `SbcDataAvailablePin` tells DSF that the firmware has data, so DSF can clock a transfer to fetch it when it has nothing of its own to send.

The [SPI state machine](../../../Documentation/articles/spi-state-machine.md) article is the full description of both sides of this exchange, including the retry and reset behaviour.

## SbcInterface Task Flow

`SbcInterface::TaskLoop` runs as a dedicated firmware task:

1. Start a transfer.
2. Wait for it to complete, time out, or reset.
3. On completion, process the incoming packets, queue the outgoing ones, and arm the next transfer.
4. On a timeout or reset, mark the link disconnected, invalidate what depended on it, and reset the transport.

## Incoming Requests (DSF -> Firmware)

| `SbcRequest` | Effect |
|---|---|
| `EmergencyStop` | Performs an emergency stop. |
| `Reset` | Performs an emergency stop, then resets the controller. |
| `EnableCAN` | Enables or disables the CAN interface. |
| `ScheduleMove` | Hands a move DSF has planned to `CanMotion`. |
| `SendCANMessage` | Sends a CAN message for DSF. A timing message addressed to this board configures its own CAN timing instead of reaching the bus. |
| `WriteIap` | Stops the machine, and hands over to `ReceiveAndStartIap`, which loads the in-application programmer into RAM. |
| `Message` | Delivers a message from DSF. |

`ConfigCAN` has no handler, so its content is skipped, and `StartIap` is only acted on by `ReceiveAndStartIap`. A packet that cannot be processed yet is requested again with `ResendPacket`.

## Outgoing Requests (Firmware -> DSF)

| `FirmwareRequest` | Purpose |
|---|---|
| `ResendPacket` | Asks for a packet to be sent again. |
| `CodeBufferUpdate` | Reports the available code buffer size. |
| `Message` | Text output and replies. |
| `CANResponse` | A CAN message forwarded from the bus. |
| `CanMessageSent` | What became of the CAN messages DSF sent. |
| `MotionStopped` | Drivers that have stopped on an endstop. |
| `BoardInfo` | Which board this is and what firmware it runs. |
| `BoardStatus` | This board's own voltages, MCU temperature, and free memory. |

## Error Handling And Recovery

Recovery paths include:

- Header and data CRC mismatches, handled with explicit error responses.
- Rejection of a bad format, protocol version, or length.
- Transfer restart on bad responses.
- Connection reset detection, using discontinuity in the transfer sequence number.
- Connection timeouts in the SBC task, which invalidate pending replies and queued CAN responses.

## Interfaces

- [../CAN/README.md](../CAN/README.md): receives moves and CAN messages, and supplies what is reported back.
- [../Platform/README.md](../Platform/README.md): provides the task, message, and output buffer facilities.

## Related Docs

- [Firmware link](../../../Documentation/articles/firmware-link.md)
- [SPI state machine](../../../Documentation/articles/spi-state-machine.md)
- [DuetControlServer](../../../DuetControlServer/README.md)
