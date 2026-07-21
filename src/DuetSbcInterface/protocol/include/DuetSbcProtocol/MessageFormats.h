// SPI/SBC wire protocol definitions shared between the SBC side (this project / DuetControlServer)
// and the device side (DuetCANMaster / RepRapFirmware).
//
// Every struct here is a wire format: the layout must match the C# definitions in
// DuetControlServer/Link/Protocol/** byte-for-byte, and the equivalent structs in
// DuetCANMaster/src/SBC/SbcMessageFormats.h. Do not reorder fields or change padding.
//
// This header is deliberately free of any firmware- or OS-specific dependency so both sides
// (and a future C# P/Invoke consumer) can share it.
#pragma once

#include <cstddef>
#include <cstdint>

namespace duet::sbc::protocol {

// ---------------------------------------------------------------------------
// Transfer-level constants (see DuetControlServer/Link/Protocol/Shared/Consts.cs)
// ---------------------------------------------------------------------------

// Unique format code for binary SPI transfers (0x3E = DuetWiFiServer, must differ)
inline constexpr uint8_t FormatCode = 0x5F;
// Format code indicating that RRF is operating in standalone mode
inline constexpr uint8_t FormatCodeStandalone = 0x60;
// Unique format code that is not used anywhere else
inline constexpr uint8_t InvalidFormatCode = 0xC9;

// Protocol version. Incremented whenever the protocol details change. CRC32 is used for version >= 4.
inline constexpr uint16_t ProtocolVersion = 7;

// Default size of a data transfer buffer. Must be a multiple of 4 and kept in sync with both sides.
inline constexpr size_t BufferSize = 8192;

static_assert(BufferSize % sizeof(uint32_t) == 0, "BufferSize must be a whole number of dwords");
static_assert(BufferSize <= UINT16_MAX, "BufferSize must fit in the uint16_t dataLength field");

// ---------------------------------------------------------------------------
// IAP / firmware update constants (Shared/Consts.cs)
// ---------------------------------------------------------------------------

// Size of a single chunk of the IAP binary sent via a WriteIap packet
inline constexpr size_t IapSegmentSize = 1536;
// Time to wait for the IAP program to raise TfrRdy (ms). IAP erases flash between segments, so this
// is far longer than a regular transfer timeout.
inline constexpr int IapTimeout = 8000;
// Size of a single firmware chunk clocked out to the running IAP program
inline constexpr size_t FirmwareSegmentSize = 2048;
// Time to wait after the last firmware segment before sending the verification request (ms)
inline constexpr int FirmwareFinishedDelay = 750;
// Time to wait for IAP to reboot the controller once the firmware has been verified (ms)
inline constexpr int IapRebootDelay = 2000;
// Byte IAP sends back to confirm that the written firmware matches the supplied CRC16
inline constexpr uint8_t FlashVerifyOk = 0x0C;

// ---------------------------------------------------------------------------
// Result codes for header and data transfers (Shared/TransferResponse.cs)
// ---------------------------------------------------------------------------
namespace TransferResponse {
inline constexpr uint32_t Success = 1;
inline constexpr uint32_t BadFormat = 2;
inline constexpr uint32_t BadProtocolVersion = 3;
inline constexpr uint32_t BadDataLength = 4;
inline constexpr uint32_t BadHeaderChecksum = 5;
inline constexpr uint32_t BadDataChecksum = 6;
// Special: can follow a response exchange
inline constexpr uint32_t BadResponse = 0xFEFEFEFEu;
// Error responses when the MISO line is stuck
inline constexpr uint32_t LowPin = 0x00000000u;
inline constexpr uint32_t HighPin = 0xFFFFFFFFu;
} // namespace TransferResponse

// ---------------------------------------------------------------------------
// Request indices SBC -> firmware (SbcRequests/Request.cs)
// ---------------------------------------------------------------------------
enum class SbcRequest : uint16_t {
    EmergencyStop = 0,   // Perform an immediate emergency stop
    Reset = 1,           // Reset the controller
    ConfigCAN = 2,       // Configure the CAN bus interface
    EnableCAN = 3,       // Enable/disable the CAN bus interface
    ScheduleMove = 4,    // Schedule a move on the controller
    SendCANMessage = 5,  // Send a CAN message to the controller
    WriteIap = 6,        // Write another chunk of the IAP binary
    StartIap = 7,        // Launch the IAP binary
    Message = 8,         // Send an arbitrary RepRapFirmware message
};

// ---------------------------------------------------------------------------
// Request indices firmware -> SBC (FirmwareRequests/Request.cs)
// ---------------------------------------------------------------------------
enum class FirmwareRequest : uint16_t {
    ResendPacket = 0,      // Request retransmission of the given packet
    CodeBufferUpdate = 2,  // Update about the available code buffer size
    Message = 3,           // Message from the firmware
    MasterClock = 4,       // The current master clock time
    CANResponse = 5,       // Forwarded CAN message from expansion boards
    MotionStopped = 6,     // Drive(s) that have stopped
};

// Status of a forwarded CAN message (FirmwareRequests/CanStatus.cs)
enum class CanStatus : uint8_t {
    Ok = 0,       // Reply received without error
    Timeout = 1,  // No reply received within the timeout period
    BusError = 2, // Transmit failed or the request was malformed
    NoBuffer = 3, // The HAT could not allocate a CAN buffer for the request
    Overflow = 4, // Reply larger than the SBC could handle
};

// ---------------------------------------------------------------------------
// Wire structs. Layouts verified against the C# structs with static_asserts below.
// ---------------------------------------------------------------------------
#pragma pack(push, 1)

// Header describing the content of a full SPI transfer (Shared/TransferHeader.cs).
// For protocol version >= 4 the checksum fields carry CRC32 values (crcData/crcHeader).
struct SpiTransferHeader {
    uint8_t formatCode;
    uint8_t numPackets;
    uint16_t protocolVersion;
    uint16_t sequenceNumber;
    uint16_t dataLength;
    uint32_t crcData;
    uint32_t crcHeader;
};

// Header used for single packets in both directions (Shared/PacketHeader.cs)
struct PacketHeader {
    uint16_t request;        // SbcRequest or FirmwareRequest
    uint16_t id;             // Packet identifier
    uint16_t length;         // Length of the packet payload in bytes
    uint16_t resendPacketId; // Packet to resend (0 by default)
};

// Header for arbitrary messages (Shared/MessageHeader.cs). messageType is a MessageTypeFlags bitmap.
struct MessageHeader {
    uint32_t messageType;
    uint16_t length;
    uint16_t padding;
};

// Body for a request that only contains a string value (Shared/StringHeader.cs)
struct StringHeader {
    uint16_t length;
    uint16_t padding;
};

// Enable/disable a CAN bus interface (SbcRequests/EnableCanHeader.cs)
struct EnableCanHeader {
    uint8_t channel; // channel number (0 or 1)
    uint8_t enable;  // non-zero to enable
    uint16_t padding;
};

// Send a CAN message to the controller (SbcRequests/SendCanMessageHeader.cs).
// The 'flags' byte carries isResponse in bit 0.
struct SendCanMessageHeader {
    uint16_t txToken;    // SBC-chosen token to map responses to the request
    uint16_t msgType;    // CanMessageType to place in the CAN id
    uint16_t replyType;  // Expected reply type, or 0xFFFF for none
    uint8_t dataLength;  // CAN payload bytes that follow (<= 64)
    uint8_t dstAddress;  // 0..126, or 127 for broadcast
    uint8_t flags;       // bit0 = isResponse
    uint8_t padding;
    uint16_t padding2;
};

// Final message to the IAP program, checking whether the firmware was flashed successfully
// (SbcRequests/FlashVerify.cs). The C# struct declares Size = 8, so the two trailing padding bytes
// after crc16 are part of the wire format and must be transmitted.
struct FlashVerify {
    uint32_t firmwareLength;
    uint16_t crc16;
    uint16_t padding;
};

// Update about the available code buffer size (FirmwareRequests/CodeBufferUpdateHeader.cs)
struct CodeBufferUpdateHeader {
    uint16_t bufferSpace;
    uint16_t padding;
};

// The current master clock time (FirmwareRequests/MasterClockHeader.cs)
struct MasterClockHeader {
    uint32_t masterClock;
    uint32_t hiccupTime;
};

// CAN bus message received by the SBC (FirmwareRequests/CanResponse.cs)
struct CanResponseHeader {
    uint16_t txToken;    // Token mapping the response back to its request (0 if unsolicited)
    uint16_t msgType;    // CanMessageType of the received message
    uint16_t dataLength; // CAN payload bytes that follow (<= 64)
    uint8_t srcAddress;  // 0..126
    uint8_t flags;
    uint8_t status;      // CanStatus
    uint8_t padding;
    uint16_t padding2;
};

#pragma pack(pop)

// ---------------------------------------------------------------------------
// Compile-time layout guarantees. These must match the [StructLayout(Size=...)] in C#.
// ---------------------------------------------------------------------------
static_assert(sizeof(SpiTransferHeader) == 16, "SpiTransferHeader must be 16 bytes");
static_assert(offsetof(SpiTransferHeader, protocolVersion) == 2, "");
static_assert(offsetof(SpiTransferHeader, sequenceNumber) == 4, "");
static_assert(offsetof(SpiTransferHeader, dataLength) == 6, "");
static_assert(offsetof(SpiTransferHeader, crcData) == 8, "");
static_assert(offsetof(SpiTransferHeader, crcHeader) == 12, "");

static_assert(sizeof(PacketHeader) == 8, "PacketHeader must be 8 bytes");
static_assert(sizeof(MessageHeader) == 8, "MessageHeader must be 8 bytes");
static_assert(sizeof(StringHeader) == 4, "StringHeader must be 4 bytes");
static_assert(sizeof(EnableCanHeader) == 4, "EnableCanHeader must be 4 bytes");
static_assert(sizeof(SendCanMessageHeader) == 12, "SendCanMessageHeader must be 12 bytes");
static_assert(sizeof(FlashVerify) == 8, "FlashVerify must be 8 bytes");
static_assert(sizeof(CodeBufferUpdateHeader) == 4, "CodeBufferUpdateHeader must be 4 bytes");
static_assert(sizeof(MasterClockHeader) == 8, "MasterClockHeader must be 8 bytes");
static_assert(sizeof(CanResponseHeader) == 12, "CanResponseHeader must be 12 bytes");

// Round a length up to the next 4-byte boundary, matching the padding rules used by both sides.
inline constexpr size_t AddPadding(size_t length) noexcept {
    const size_t extra = length & 3u;
    return (extra == 0) ? length : length + 4 - extra;
}

} // namespace duet::sbc::protocol
