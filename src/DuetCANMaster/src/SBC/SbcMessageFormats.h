/*
 * SbcMessageFormats.h
 *
 *  Created on: 29 Mar 2019
 *      Author: Christian
 */

#ifndef SRC_SBC_MESSAGEFORMATS_H_
#define SRC_SBC_MESSAGEFORMATS_H_

#if HAS_SBC_INTERFACE

#include <cstddef>
#include <cstdint>
#include <ctime>

#include <RepRapFirmware.h>
#include <Platform/PrintPausedReason.h>

enum class SbcMessageType : uint8_t
{
	Header = 0x01,
	HeaderResponse = 0x02,
	Data = 0x04,
	DataResponse = 0x08,
};
constexpr uint8_t SbcFormatCode = 0x5F;				// standard format code for RRF SPI protocol
constexpr uint8_t SbcFormatCodeStandalone = 0x60;	// used to indicate that RRF is running in stand-alone mode
constexpr uint8_t InvalidFormatCode = 0xC9;			// must be different from any other format code

constexpr uint16_t SbcProtocolVersion = 7;

constexpr size_t SbcTransferBufferSize = 8192;		// maximum length of a data transfer. Must be a multiple of 4 and kept in sync with Duet Control Server!
static_assert(SbcTransferBufferSize % sizeof(uint32_t) == 0, "SbcTransferBufferSize must be a whole number of dwords");
static_assert(SbcTransferBufferSize <= UINT16_MAX, "SBC buffer size exceeds usbd_edpt_xfer uint16_t limit");


constexpr size_t MaxGCodeBinaryLength = 384;			// maximum length of a G/M/T-code in binary encoding
static_assert(MaxGCodeBinaryLength % sizeof(uint32_t) == 0, "MaxGCodeBinaryLength must be a whole number of dwords");
static_assert(MaxGCodeBinaryLength >= MaxGCodeStringLength, "MaxGCodeBinaryLength must be at least as big as MAxGCodeStringLength");

constexpr size_t MaxSbcExpressionLength = 256;		// maximum length for incoming expressions

constexpr uint32_t SbcMaxRequestTime = 3000;		// maximum time to wait a blocking request (like macros or file requests, in ms)
constexpr uint32_t SbcTransferTimeout = 500;		// maximum allowed delay between data exchanges during a full transfer (in ms)
constexpr uint32_t SbcMaxTransferTime = 50;			// maximum allowed time for a single SPI transfer
constexpr uint32_t SbcConnectionTimeout = 4000;		// maximum time to wait for the next transfer (in ms)
constexpr uint32_t SbcTxDrainTimeout = 250;			// maximum time to wait for CDC TX FIFO to drain before entering direct mode (in ms)
constexpr uint16_t SbcCodeBufferSize = 4096;		// number of bytes available for G-code caching

// Shared structures

struct MessageHeader
{
	MessageType messageType;
	uint16_t length;
	uint16_t padding;
};

struct PacketHeader
{
	uint16_t request;
	uint16_t id;
	uint16_t length;
	uint16_t resendPacketId;
};

struct StringHeader
{
	uint16_t length;
	uint16_t padding;
};

struct SpiTransferHeader
{
	uint8_t formatCode;
	uint8_t numPackets;
	uint16_t protocolVersion;
	uint16_t sequenceNumber;
	uint16_t dataLength;
	uint32_t crcData;
	uint32_t crcHeader;
};

enum SpiTransferResponse : uint32_t
{
	Success = 1,
	BadFormat = 2,
	BadProtocolVersion = 3,
	BadDataLength = 4,
	BadHeaderChecksum = 5,
	BadDataChecksum = 6,

	BadResponse = 0xFEFEFEFEu
};

enum class SbcTransportType : uint8_t
{
	spi,
	usb
};

struct UsbTransferHeader
{
	uint8_t numPackets;
	uint8_t padding;
	uint16_t dataLength;
	uint32_t padding2;
};

/* Sbc to HAT */

enum class SbcRequest : uint16_t
{
    EmergencyStop = 0,							// Perform immediate emergency stop
    Reset = 1,									// Reset the controller
	EnableCAN = 3,								// Enable/disable the CAN interface
	ScheduleMove = 4,							// Schedule a move
	SendCANMessage = 5,							// Send a CAN message to specific address
	WriteIap = 6,								// Write another chunk of the IAP binary
	StartIap = 7,								// Launch the IAP binary
	Message = 8,								// Send an arbitrary message
	InvalidRequest
};

struct BooleanHeader
{
	bool value;
	uint8_t paddingA;
	uint16_t paddingB;
};

// Not used during data transfers
struct BufferedCodeHeader // TODO remove?
{
	bool isPending;
	uint8_t padding;
	uint16_t length;
};

struct EnableCANHeader
{
	uint8_t channel;
	uint8_t enable;
	uint8_t padding[2];
};

struct ScheduleMoveHeader
{
	uint8_t queue;
	uint8_t padding[3];

};

struct CANRequestHeader
{
	uint16_t txToken;				// SBC-chosen token to map responses to the request. Not sent in the CAN message.
	CanMessageType msgType;			// CanMessageType to place in the CAN id
	CanMessageType replyType;		// Reply to expect from the expansion board. If no reply is expected, set to 0xFFFF
	uint8_t dataLength;				// CAN payload bytes that follow the header. Must be <= 64
	uint8_t dstAddress;				// CAN destination: 0..126, or 127 for broadcast
	uint8_t isResponse : 1,
			unused : 7;				// reserved for future use
	uint8_t reserved;
	uint16_t padding;
	// uint8_t data[dataLength];	// CAN payload bytes that follow the header. Must be <= 64
};


/* HAT to Sbc */

enum class FirmwareRequest : uint16_t
{
	ResendPacket = 0,						// Request the retransmission of the given packet
	CodeBufferUpdate = 2,					// Update about the available code buffer size
	Message = 3,							// Message from the firmware
	MasterClock = 4,						// Send the current master clock time to the SBC
	CANResponse = 5,						// Forwarded CAN message from expansion boards
	MotionStopped = 6,						// Drive(s) that have stopped
};

struct CodeBufferUpdateHeader // TODO remove
{
	uint16_t bufferSpace;
	uint16_t padding;
};

enum class CanStatus : uint8_t
{
	Ok = 0, // reply received and forwarded to SBC
	Timeout = 1, // no reply received within the timeout period
	BusError = 2, // transmit failed or the request was malformed
	NoBuffer = 3, // the HAT could not allocate a CAN buffer for the request
	Overflow = 4, // reply larger than the SBC could be given
};

struct MasterClockHeader
{
	uint32_t masterClock;
	uint32_t hiccupTime;
};

struct CANResponseHeader
{
	uint16_t txToken;				// SBC-chosen token to map responses to the request. 0 if not a response.
	CanMessageType msgType;				// CanMessageType of received message
	uint16_t dataLength;			// CAN payload bytes that follow the header. May be > 64 because of reply reassembly
	uint8_t srcAddress;				// CAN source: 0..126
	uint8_t flags;
	CanStatus status;					// CanStatus of the request. 0 if not a response.
	uint8_t reserved;
	uint16_t padding;
	// uint8_t data[dataLength];	// CAN payload bytes that follow the header.
};

#endif

#endif /* SRC_SBC_MESSAGEFORMATS_H_ */
