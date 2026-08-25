// Socket framing for the SBC link: the transfer protocol of MessageFormats.h carried over a stream
// socket instead of SPI. This is what connects DuetControlServer to a virtual controller - the
// scriptable fake endpoint of the system test bench, and later the Renode link peripheral - so the
// framing is defined here, beside the wire structs it carries, where both sides of the link and the
// C# mirror can share it.
//
// The mapping from the SPI exchange (see Interface/Transport.h for the three problems it names):
//
//   - The fixed-size full-duplex lockstep exchange becomes one Transfer frame per direction per
//     exchange. Each carries the SpiTransferHeader and the data block verbatim, CRCs included, so
//     the real validation, resync and resend logic runs on both sides and a test can corrupt a CRC
//     deliberately.
//   - The out-of-band TfrRdy pin becomes the Ready frame: the controller sends one whenever it is
//     armed for the next exchange, and the SBC consumes exactly one Ready per Transfer frame it
//     sends. Withholding Ready is how a test makes a transfer time out.
//   - The DataAvailable pin becomes the DataAvailable frame: the controller sends it when it has
//     staged data and wants the SBC to start an exchange. It is a prompt, not a gate; the SBC
//     treats it like the pin level and clears its note of it once an exchange completes.
//
// One exchange over the socket:
//
//   1. The SBC waits for a Ready frame (its transfer/connect timeout applies).
//   2. The SBC sends a Transfer frame; the controller always answers with its own Transfer frame,
//      whatever it thinks of the one it received. Validation failures are reported in step 3
//      rather than by withholding the reply, which keeps the two frame streams in step.
//   3. Both sides send exactly one Response frame carrying their verdict (a TransferResponse code)
//      on the frame they received, then read the other's. Success/Success completes the exchange;
//      a checksum verdict makes the sender retry the same exchange from step 1 (the transfer
//      headers are unchanged, exactly as an SPI retry re-clocks the same data); BadResponse
//      restarts the whole transfer; the fatal codes (BadFormat, BadProtocolVersion, BadDataLength)
//      abort it. A protocol version change is negotiated as on SPI: the side that adapts answers
//      BadResponse and the exchange restarts with the adopted version.
//
// A Response frame in place of an expected Transfer frame is legal only for BadResponse, which
// means "abandon this exchange and start over" exactly as it does on SPI.
//
// Firmware update: WriteIap and StartIap are ordinary packets and need nothing here. Once IAP would
// be running, each firmware segment is an IapData frame gated by Ready like any exchange, the
// verification request is an IapVerify frame, and the flasher answers with a single-byte IapVerdict
// frame (FlashVerifyOk on success). The stage 1 fake accepts and discards the data; flashing
// against emulated flash is stage 2's to test.
#pragma once

#include <cstddef>
#include <cstdint>

#include <DuetSpiProtocol/MessageFormats.h>

namespace duet::spi::protocol {

enum class SocketFrameType : uint8_t {
    Ready = 1,         // controller -> SBC: armed for the next exchange (TfrRdy analog)
    DataAvailable = 2, // controller -> SBC: staged data is waiting (DataAvailable pin analog)
    Transfer = 3,      // both directions: SpiTransferHeader + data block, verbatim
    Response = 4,      // both directions: one uint32_t TransferResponse code
    IapData = 5,       // SBC -> controller: one bare firmware segment while IAP runs
    IapVerify = 6,     // SBC -> controller: FlashVerify request
    IapVerdict = 7,    // controller -> SBC: one byte, FlashVerifyOk on success
};

#pragma pack(push, 1)

// Prefixes every frame. The payload length excludes this header.
struct SocketFrameHeader {
    uint8_t type;    // SocketFrameType
    uint8_t padding;
    uint16_t padding2;
    uint32_t length; // payload bytes that follow
};

#pragma pack(pop)

static_assert(sizeof(SocketFrameHeader) == 8, "SocketFrameHeader must be 8 bytes");

// The largest payload either side accepts: a full transfer header plus a full data block. Anything
// longer is a malformed frame, not a big one, and reading on would desynchronise the stream.
inline constexpr size_t MaxSocketFramePayload = sizeof(SpiTransferHeader) + BufferSize;

} // namespace duet::spi::protocol
