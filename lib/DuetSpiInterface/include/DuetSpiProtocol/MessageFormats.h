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

// For StopAction, which ScheduleMoveDriver carries. The rules that read it are declared beside it so
// that the wire field and the meaning of its values cannot come apart.
#include <DuetSpiProtocol/StopRules.h>

namespace duet::spi::protocol {

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
inline constexpr uint16_t ProtocolVersion = 8;

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
    MasterClock = 4,       // Retired: the master clock rides in SpiTransferHeader. Not reused, so
                           // that a mismatched pair fails on the protocol version rather than here
    CANResponse = 5,       // Forwarded CAN message from expansion boards
    MotionStopped = 6,     // Drive(s) that have stopped
    CanMessageSent = 7,    // What became of the CAN messages the SBC asked to be sent
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
//
// masterClock and hiccupTime ride in the header rather than in a packet of their own. The SBC has no
// step clock - it fits one to these samples and schedules every move by absolute start time in the
// result - so the pairing between the tick count and the local time it is stamped with is what
// decides how well moves land. A packet is read after an unknown number of others, so that pairing
// varies by however long they took; the header arrives at a fixed point in every transfer, so it
// does not. crcHeader must stay last: the header checksum covers everything before it.
struct SpiTransferHeader {
    uint8_t formatCode;
    uint8_t numPackets;
    uint16_t protocolVersion;
    uint16_t sequenceNumber;
    uint16_t dataLength;
    uint32_t crcData;
    uint32_t masterClock; // the controller's step clock at the moment this transfer was armed
    uint32_t hiccupTime;  // total movement delay the controller has accumulated, in step clocks
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

// Bits of ScheduleMoveHeader::flags
namespace ScheduleMoveFlags {
// The move was planned expecting the boards to apply late input shaping
inline constexpr uint8_t UseInputShaping = 1u << 0;
// At least one extruder in this move wants pressure advance applied
inline constexpr uint8_t UsePressureAdvance = 1u << 1;
// The move monitors endstops, so the controller must set up its driver stop list before sending it
inline constexpr uint8_t CheckEndstops = 1u << 2;
// The last packet of this move: the controller sends the accumulated CAN messages when it sees this
inline constexpr uint8_t LastPacket = 1u << 3;
// Bit 4 is unused.
} // namespace ScheduleMoveFlags

// Schedule a move on the controller (SbcRequest::ScheduleMove).
//
// The SBC plans the move and its velocity profile; the controller fans it out to the expansion
// boards as CanMessageMovementLinearShaped. The fields below are exactly DuetCANMaster's PrepParams
// (see CanMotion.cpp), in the same units - step clocks and millimetres - so that the controller
// fills that struct by copying rather than converting. In particular the accelerations are NOT yet
// scaled to unit distance; CanMotion does that scaling as it always has.
//
// A move with more drivers than fit in one packet is split across several packets sharing a moveId,
// the last of which sets ScheduleMoveFlags::LastPacket. The controller accumulates and only sends
// once it sees that flag, so a split move still reaches the boards as one CAN message per board.
// If a packet arrives carrying a different moveId from the one being accumulated, the accumulated
// packets are discarded: that means the SBC abandoned the earlier move part way through, and half
// of it must not reach the boards.
struct ScheduleMoveHeader {
    uint32_t whenToExecute;     // master step-clock time at which the move starts
    uint32_t accelClocks;       // duration of the acceleration phase
    uint32_t steadyClocks;      // duration of the constant-speed phase
    uint32_t decelClocks;       // duration of the deceleration phase
    float acceleration;         // always positive, mm/clock^2
    float deceleration;         // always negative, matching PrepParams' sign convention
    float totalDistance;        // mm
    float accelDistance;        // mm travelled when acceleration ends
    float decelStartDistance;   // mm travelled when deceleration begins
    float startSpeed;           // mm/clock
    float topSpeed;             // mm/clock
    float endSpeed;             // mm/clock
    uint32_t moveId;            // SBC-chosen id, shared by every packet of a split move
    uint8_t numDrivers;         // ScheduleMoveDriver records that follow this header
    uint8_t flags;              // ScheduleMoveFlags
    uint16_t padding;
};

// Value of ScheduleMoveDriver::stopOnBoard meaning "this driver watches no endstop".
inline constexpr uint8_t NoEndstopBoard = 0xFF;

// One driver's share of a scheduled move. `steps` applies to axis drivers and `extrusion` to
// extruders; whichever does not apply is zero, so a receiver that trusts isExtruder and one that
// checks both agree.
//
// stopOnBoard and stopOnHandle are how an endstop move says what stops this driver. They are the
// CAN address and RemoteInputHandle of the input to watch, which is exactly what arrives in
// CanMessageInputChangedV2, so the controller matches an incoming change against them directly
// rather than looking anything up. Carrying it per driver rather than per move is what lets one
// move home several axes at once, each stopping on its own endstop.
//
// stopGroup and stopAction say what else goes when this driver's input fires. They are per driver
// for the same reason: RepRapFirmware picks one of three actions per endstop, not per move, so a
// move may home an axis whose endstop stops every drive alongside one whose endstop stops only its
// own. The group is the logical drive, which is what "stop this axis" means once the move has been
// flattened into drivers - the controller holds no axis-to-driver map and should not acquire one.
//
// The controller does the stopping because it is the only place close enough to the bus for the
// latency to be acceptable: by the time an input change reached the SBC and a stop came back, the
// axis would have travelled past the endstop.
struct ScheduleMoveDriver {
    uint8_t boardAddress;   // CAN address of the board carrying this driver
    uint8_t driverNumber;   // driver number on that board
    uint8_t isExtruder;     // non-zero if this driver is an extruder
    uint8_t stopOnBoard;    // CAN address of the board carrying the endstop, or NoEndstopBoard
    int32_t steps;          // net microsteps, for an axis driver
    float extrusion;        // microsteps including fractional parts, for an extruder
    uint16_t stopOnHandle;  // RemoteInputHandle of the endstop to stop on, if stopOnBoard is set
    uint8_t stopGroup;      // drivers stopped together by StopAction::group, or NoStopGroup
    StopAction stopAction;  // what a trigger on this driver's input stops
};

// Most drivers one ScheduleMove packet may carry. Chosen so that a full packet is a few hundred
// bytes and several moves fit in one transfer alongside everything else; more drivers than this
// simply take another packet.
inline constexpr size_t MaxScheduleMoveDrivers = 32;

// Most drivers one whole move may carry, across all of its packets: every logical drive of the
// machine moving, each with a full complement of drivers. This is what anything accumulating per
// move has to be sized by - MaxScheduleMoveDrivers bounds a packet, and a move split across several
// of them can carry many times that.
inline constexpr size_t MaxMoveDrivers = 32 * 8;

// Bits of SendCanMessageHeader::flags
namespace SendCanMessageFlags {
inline constexpr uint8_t IsResponse = 1u << 0;
} // namespace SendCanMessageFlags

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

// CAN bus message received by the SBC (FirmwareRequests/CanResponse.cs)
// One driver whose motion an endstop cut short (FirmwareRequest::MotionStopped).
struct MotionStoppedDriver {
    uint8_t boardAddress;  // CAN address of the board carrying the driver
    uint8_t driverNumber;  // driver number on that board
    uint16_t padding;
};

// Drives the controller stopped because an endstop fired, and when the endstop reported it.
//
// The controller does the stopping because it is the only place close enough to the CAN bus for the
// latency to be acceptable, but it cannot say where the drives should end up: it never generated the
// steps, so it does not know how far each one had travelled. The SBC does, because it evaluates the
// same motion anyway to report live positions, so it takes the timestamp from here, works out where
// each drive was at that instant, corrects its own position and sends CanMessageRevertPosition to the
// boards. That is what removes the overshoot between the endstop firing and the stop taking effect.
//
// MotionStoppedDriver records follow this header, numDrivers of them.
struct MotionStoppedHeader {
    uint32_t whenTriggered;  // master step-clock time the endstop reported
    // The move this stopped, as the SBC numbered it in MoveParamsHeader::moveId. Without it a report
    // that arrives after the next move has armed is applied to that move instead: the drives it
    // names belong to the move that really stopped, so the wrong axis is corrected and the one that
    // stopped keeps an endpoint it never reached. Nothing else can tell the two apart - the drives
    // are usually the same ones, and the timestamp is only comparable once it has been attributed
    uint32_t moveId;
    uint8_t numDrivers;  // MotionStoppedDriver records that follow this header
    uint8_t padding[3];
};

// Most drivers one MotionStopped packet may carry. A move cannot watch more endstops than it has
// drivers, so this matches the schedule packet's own limit.
inline constexpr size_t MaxMotionStoppedDrivers = MaxScheduleMoveDrivers;

// What became of the CAN messages the SBC asked to be sent, batched: the controller answers for
// everything it dealt with since the last transfer rather than a packet per message. A message the
// SBC expects no reply to has nothing else that could tell it, and one that does expects a reply
// that a failure here means will never come.
struct CanMessageSentHeader {
    uint16_t count;   // CanMessageSentEntry records that follow this header
    uint16_t padding;
};

struct CanMessageSentEntry {
    uint16_t txToken; // Token the SBC gave the message it asked to be sent
    uint8_t status;   // CanStatus: Ok means the CAN controller accepted it, not that it is on the wire
    uint8_t padding;
};

// Most entries one packet may carry, which is what bounds the ring the controller batches them in
inline constexpr size_t MaxCanMessagesSentPerTransfer = 64;

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
static_assert(sizeof(SpiTransferHeader) == 24, "SpiTransferHeader must be 24 bytes");
static_assert(offsetof(SpiTransferHeader, protocolVersion) == 2, "");
static_assert(offsetof(SpiTransferHeader, sequenceNumber) == 4, "");
static_assert(offsetof(SpiTransferHeader, dataLength) == 6, "");
static_assert(offsetof(SpiTransferHeader, crcData) == 8, "");
static_assert(offsetof(SpiTransferHeader, masterClock) == 12, "");
static_assert(offsetof(SpiTransferHeader, hiccupTime) == 16, "");
static_assert(offsetof(SpiTransferHeader, crcHeader) == 20, "");

// The header checksum covers everything up to itself, so both sides derive the length rather than
// writing it out; a hard-coded one is what breaks the next time a field is added
inline constexpr size_t SpiTransferHeaderCrcLength = offsetof(SpiTransferHeader, crcHeader);

// How many bytes a pre-version-4 peer exchanges for its header. That layout ended at a CRC16 pair,
// so it is shorter than this struct and is written out rather than derived
inline constexpr size_t LegacyTransferHeaderSize = 12;

static_assert(sizeof(PacketHeader) == 8, "PacketHeader must be 8 bytes");
static_assert(sizeof(MessageHeader) == 8, "MessageHeader must be 8 bytes");
static_assert(sizeof(StringHeader) == 4, "StringHeader must be 4 bytes");
static_assert(sizeof(EnableCanHeader) == 4, "EnableCanHeader must be 4 bytes");
static_assert(sizeof(ScheduleMoveHeader) == 56, "ScheduleMoveHeader must be 56 bytes");
static_assert(offsetof(ScheduleMoveHeader, acceleration) == 16, "");
static_assert(offsetof(ScheduleMoveHeader, moveId) == 48, "");
static_assert(offsetof(ScheduleMoveHeader, numDrivers) == 52, "");
static_assert(sizeof(MotionStoppedHeader) == 12, "MotionStoppedHeader must be 12 bytes");
static_assert(offsetof(MotionStoppedHeader, moveId) == 4, "");
static_assert(offsetof(MotionStoppedHeader, numDrivers) == 8, "");
static_assert(sizeof(MotionStoppedDriver) == 4, "MotionStoppedDriver must be 4 bytes");
static_assert(sizeof(ScheduleMoveDriver) == 16, "ScheduleMoveDriver must be 16 bytes");
static_assert(offsetof(ScheduleMoveDriver, stopOnBoard) == 3, "");
static_assert(offsetof(ScheduleMoveDriver, steps) == 4, "");
static_assert(offsetof(ScheduleMoveDriver, extrusion) == 8, "");
static_assert(offsetof(ScheduleMoveDriver, stopOnHandle) == 12, "");
static_assert(offsetof(ScheduleMoveDriver, stopGroup) == 14, "");
static_assert(offsetof(ScheduleMoveDriver, stopAction) == 15, "");
static_assert(sizeof(SendCanMessageHeader) == 12, "SendCanMessageHeader must be 12 bytes");
static_assert(sizeof(FlashVerify) == 8, "FlashVerify must be 8 bytes");
static_assert(sizeof(CodeBufferUpdateHeader) == 4, "CodeBufferUpdateHeader must be 4 bytes");
static_assert(sizeof(CanResponseHeader) == 12, "CanResponseHeader must be 12 bytes");
static_assert(sizeof(CanMessageSentHeader) == 4, "CanMessageSentHeader must be 4 bytes");
static_assert(sizeof(CanMessageSentEntry) == 4, "CanMessageSentEntry must be 4 bytes");

// Round a length up to the next 4-byte boundary, matching the padding rules used by both sides.
inline constexpr size_t AddPadding(size_t length) noexcept {
    const size_t extra = length & 3u;
    return (extra == 0) ? length : length + 4 - extra;
}

} // namespace duet::spi::protocol
