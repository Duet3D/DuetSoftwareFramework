// Record formats carried by the two RingBuffers between the native interface thread and the managed
// DuetControlServer link service.
//
// This header is the single source of truth for that boundary. The C# mirror lives in
// DuetControlServer/Link/Native/LinkEvents.cs and MUST be kept byte-for-byte identical -- the managed
// side reads these structs straight out of the ring with MemoryMarshal, so a layout change on one
// side silently corrupts the other. Every struct is packed and asserted below.
//
// Direction of travel:
//   Inbound  (native -> managed): everything the transfer loop observed. Drained by the managed
//            dispatcher thread, which then runs the ordinary DCS handlers.
//   Outbound (managed -> native): work for the transfer loop to stage into the next SPI transfer.
//
// Variable-length tails (message text, CAN payloads) follow the fixed header inside the same ring
// record; the record length tells the reader how many bytes of tail there are.
#pragma once

#include <cstddef>
#include <cstdint>

namespace Duet::Sbc
{

	// ---------------------------------------------------------------------------
	// Inbound: native -> managed
	// ---------------------------------------------------------------------------
	// Link/Native/LinkEvents.cs mirrors this as `ushort` and the record layouts must stay
	// byte-for-byte identical across the P/Invoke boundary
	// NOLINTNEXTLINE(performance-enum-size) - the width is ABI: DuetControlServer's
	enum class InboundEventType : uint16_t
	{
		// MessageEvent + UTF-8 text tail
		Message = 1,
		// CanResponseEvent + CAN payload tail
		CanResponse = 2,
		// CodeBufferEvent, no tail
		CodeBufferUpdate = 3,
		// No payload. The controller's sequence number jumped, i.e. it restarted: the managed side must
		// invalidate every pending resource (LinkService.Invalidate)
		ControllerReset = 4,
		// UTF-8 reason tail. The link dropped; managed side invalidates and reports it
		ConnectionLost = 5,
		// ConnectionEstablishedEvent, no tail. Sent on first connect and after every reconnect
		ConnectionEstablished = 6,
		// RequestCompletedEvent + optional UTF-8 error tail. Completes a managed TaskCompletionSource
		RequestCompleted = 7,
		// LogEvent + UTF-8 text tail. Diagnostics from the transfer loop (resyncs, glitches, warnings)
		Log = 8,
		// MalformedPacketEvent + raw packet bytes. The managed side owns the on-disk dump
		MalformedPacket = 9,
		// UTF-8 message tail. Unrecoverable; the managed side terminates the link service
		FatalError = 10,
		// MoveCompletedEvent, no tail. A queued move finished executing
		MoveCompleted = 11,
		// MoveFailedEvent, no tail. A move was rejected or could not be sent
		MoveFailed = 12,
		// MotionStoppedEvent + MotionStoppedDriverEntry[] tail. An endstop cut a move short. DCS
		// works out where the drives were when it fired and tells the boards to wind back, which is
		// why this carries the raw report rather than a conclusion
		MotionStopped = 13,
		// OutboundSeqEvent, no tail. Every command up to and including this sequence number reached
		// the controller in a transfer that completed
		OutboundDelivered = 14,
		// OutboundSeqEvent, no tail. Every command up to and including this sequence number was
		// abandoned instead, because the controller went away before it could be sent
		OutboundDropped = 15,
	};

	// Severity for InboundEventType::Log, mirroring the subset of MessageType DCS logs at.
	enum class LogLevel : uint8_t
	{
		Debug = 0,
		Info = 1,
		Warning = 2,
		Error = 3,
	};

	// Why a request could not be completed (RequestCompletedEvent::result).
	enum class RequestResult : uint8_t
	{
		Success = 0,
		// The connection dropped or the resource was invalidated before the request was served
		Cancelled = 1,
		// The request failed; an error message tail explains why
		Failed = 2,
	};

#pragma pack(push, 1)

	// Common leading field of every inbound record. Readers switch on `type` then reinterpret.
	struct InboundEventHeader
	{
		uint16_t type; // InboundEventType
		uint16_t reserved;
	};

	struct MessageEvent
	{
		InboundEventHeader header;
		uint32_t flags; // MessageTypeFlags
						// UTF-8 text follows
	};

	struct CanResponseEvent
	{
		InboundEventHeader header;
		uint16_t txToken;
		uint16_t msgType;
		uint16_t dataLength;
		uint8_t srcAddress;
		uint8_t flags;
		uint8_t status; // protocol::CanStatus
		uint8_t padding;
		uint16_t padding2;
		// CAN payload follows (dataLength bytes)
	};

	struct CodeBufferEvent
	{
		InboundEventHeader header;
		uint16_t bufferSpace;
		uint16_t padding;
	};

	struct ConnectionEstablishedEvent
	{
		InboundEventHeader header;
		uint16_t protocolVersion;
		// Non-zero when the controller had reset while it was away, rather than resuming what it was
		// doing. The SBC cannot tell the two apart afterwards: the sequence numbers that said so have
		// been reset with everything else
		uint16_t hadReset;
	};

	// How far the outbound queue has got. The queue is FIFO end to end - commands leave the ring in
	// order and are written into a transfer in order - so one number says what happened to any number
	// of them, which is what keeps this off the per-command hot path.
	struct OutboundSeqEvent
	{
		InboundEventHeader header;
		uint32_t sequenceNumber;
	};

	struct RequestCompletedEvent
	{
		InboundEventHeader header;
		uint32_t requestId;
		uint8_t result; // RequestResult
		uint8_t padding;
		uint16_t padding2;
		// Optional UTF-8 error text follows when result == Failed
	};

	struct LogEvent
	{
		InboundEventHeader header;
		uint8_t level; // LogLevel
		uint8_t padding;
		uint16_t padding2;
		// UTF-8 text follows
	};

	struct MalformedPacketEvent
	{
		InboundEventHeader header;
		uint16_t packetId;
		uint16_t request;
		uint16_t length;
		uint16_t offset;
		// Raw packet bytes follow
	};

	struct MoveCompletedEvent
	{
		InboundEventHeader header;
		uint32_t moveId;
		uint32_t completedMoves; // the ring's running total, so a missed event is detectable
		uint8_t ring;
		uint8_t padding;
		uint16_t padding2;
	};

	struct MoveFailedEvent
	{
		InboundEventHeader header;
		uint32_t moveId;
		uint8_t ring;
		uint8_t error; // MovementError
		uint16_t padding;
	};

	// One driver an endstop stopped, as the controller named it. Mirrors MotionStoppedDriver in the
	// SPI protocol; repeated here so the event tail is described where the other events are.
	struct MotionStoppedDriverEntry
	{
		uint8_t boardAddress;
		uint8_t driverNumber;
		uint16_t padding;
	};

	struct MotionStoppedEvent
	{
		InboundEventHeader header;
		uint32_t whenTriggered; // master step-clock time the endstop reported, 0 if it sent none
		uint8_t numDrivers;
		uint8_t padding[3];
		// MotionStoppedDriverEntry driver[numDrivers] follows
	};

	// ---------------------------------------------------------------------------
	// Outbound: managed -> native
	// ---------------------------------------------------------------------------
	// Link/Native/LinkEvents.cs mirrors this as `ushort` and the record layouts must stay
	// byte-for-byte identical across the P/Invoke boundary
	// NOLINTNEXTLINE(performance-enum-size) - the width is ABI: DuetControlServer's
	enum class OutboundCommandType : uint16_t
	{
		// MessageCommand + UTF-8 text tail
		Message = 1,
		// CanMessageCommand + CAN payload tail
		CanMessage = 2,
		// EnableCanCommand, no tail
		EnableCan = 3,
		// RequestCommand, no tail
		EmergencyStop = 4,
		// RequestCommand, no tail
		Reset = 5,
		// OutboundCommandHeader + one whole ScheduleMove packet as the tail. Unlike the others there
		// is no fixed body: the packet is duet::spi::protocol::ScheduleMoveHeader followed by its
		// driver records, already laid out for the wire, so the transfer loop copies it through
		// rather than rebuilding it. Queued by the motion thread, not by the managed side
		ScheduleMove = 6,
	};

	struct OutboundCommandHeader
	{
		uint16_t type; // OutboundCommandType
		uint16_t reserved;
	};

	// Commands that the managed side awaits carry a request id; the loop reports the outcome back with
	// a RequestCompletedEvent quoting the same id. Fire-and-forget commands use kNoRequestId.
	inline constexpr uint32_t kNoRequestId = 0;

	struct MessageCommand
	{
		OutboundCommandHeader header;
		uint32_t flags; // MessageTypeFlags
						// UTF-8 text follows
	};

	struct CanMessageCommand
	{
		OutboundCommandHeader header;
		uint16_t txToken;
		uint16_t msgType;
		uint16_t replyType;
		uint8_t dstAddress;
		uint8_t isResponse;
		// CAN payload follows
	};

	struct EnableCanCommand
	{
		OutboundCommandHeader header;
		uint32_t requestId;
		uint8_t enable;
		uint8_t padding;
		uint16_t padding2;
	};

	struct RequestCommand
	{
		OutboundCommandHeader header;
		uint32_t requestId;
	};

#pragma pack(pop)

	// ---------------------------------------------------------------------------
	// Layout guarantees. The C# mirror declares the same sizes with [StructLayout(Pack = 1, Size = ...)].
	// ---------------------------------------------------------------------------
	static_assert(sizeof(InboundEventHeader) == 4, "InboundEventHeader must be 4 bytes");
	static_assert(sizeof(MessageEvent) == 8, "MessageEvent must be 8 bytes");
	static_assert(sizeof(CanResponseEvent) == 16, "CanResponseEvent must be 16 bytes");
	static_assert(sizeof(CodeBufferEvent) == 8, "CodeBufferEvent must be 8 bytes");
	static_assert(sizeof(ConnectionEstablishedEvent) == 8, "ConnectionEstablishedEvent must be 8 bytes");
	static_assert(sizeof(RequestCompletedEvent) == 12, "RequestCompletedEvent must be 12 bytes");
	static_assert(sizeof(LogEvent) == 8, "LogEvent must be 8 bytes");
	static_assert(sizeof(MalformedPacketEvent) == 12, "MalformedPacketEvent must be 12 bytes");
	static_assert(sizeof(MoveCompletedEvent) == 16, "MoveCompletedEvent must be 16 bytes");
	static_assert(sizeof(MoveFailedEvent) == 12, "MoveFailedEvent must be 12 bytes");
	static_assert(sizeof(MotionStoppedDriverEntry) == 4, "MotionStoppedDriverEntry must be 4 bytes");
	static_assert(sizeof(MotionStoppedEvent) == 12, "MotionStoppedEvent must be 12 bytes");

	static_assert(sizeof(OutboundCommandHeader) == 4, "OutboundCommandHeader must be 4 bytes");
	static_assert(sizeof(MessageCommand) == 8, "MessageCommand must be 8 bytes");
	static_assert(sizeof(CanMessageCommand) == 12, "CanMessageCommand must be 12 bytes");
	static_assert(sizeof(EnableCanCommand) == 12, "EnableCanCommand must be 12 bytes");
	static_assert(sizeof(RequestCommand) == 8, "RequestCommand must be 8 bytes");

} // namespace Duet::Sbc
