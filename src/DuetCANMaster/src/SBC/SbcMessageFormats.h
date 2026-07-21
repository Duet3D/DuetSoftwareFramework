/*
 * SbcMessageFormats.h
 *
 *  Created on: 29 Mar 2019
 *      Author: Christian
 *
 * The wire format itself now lives in lib/DuetSpiInterface, shared with the SBC side
 * (DuetSbcInterface and, via P/Invoke, DuetControlServer). This header only adds the
 * firmware-side spellings of those definitions plus the constants and structures that
 * are local to RepRapFirmware and never cross the SPI link.
 *
 * Do not redeclare wire structs here: add them to
 * lib/DuetSpiInterface/include/DuetSpiProtocol/MessageFormats.h so both sides move together.
 */

#ifndef SRC_SBC_MESSAGEFORMATS_H_
#define SRC_SBC_MESSAGEFORMATS_H_

#if HAS_SBC_INTERFACE

#  include <cstddef>
#  include <cstdint>
#  include <ctime>

#  include <RepRapFirmware.h>

#  include <Platform/PrintPausedReason.h>

#  include <DuetSpiProtocol/MessageFormats.h>

namespace SbcProtocol = duet::spi::protocol;

// ---------------------------------------------------------------------------
// Firmware-side names for the shared protocol definitions
// ---------------------------------------------------------------------------

constexpr uint8_t SbcFormatCode = SbcProtocol::FormatCode;
constexpr uint8_t SbcFormatCodeStandalone = SbcProtocol::FormatCodeStandalone;
constexpr uint8_t InvalidFormatCode = SbcProtocol::InvalidFormatCode;

constexpr uint16_t SbcProtocolVersion = SbcProtocol::ProtocolVersion;

constexpr size_t SbcTransferBufferSize = SbcProtocol::BufferSize;
static_assert(SbcTransferBufferSize <= UINT16_MAX, "SBC buffer size exceeds usbd_edpt_xfer uint16_t limit");

namespace SpiTransferResponse = SbcProtocol::TransferResponse;

using SbcRequest = SbcProtocol::SbcRequest;
using FirmwareRequest = SbcProtocol::FirmwareRequest;
using CanStatus = SbcProtocol::CanStatus;

using SpiTransferHeader = SbcProtocol::SpiTransferHeader;
using PacketHeader = SbcProtocol::PacketHeader;
using MessageHeader = SbcProtocol::MessageHeader;
using StringHeader = SbcProtocol::StringHeader;
using EnableCANHeader = SbcProtocol::EnableCanHeader;
using CANRequestHeader = SbcProtocol::SendCanMessageHeader;
using CANResponseHeader = SbcProtocol::CanResponseHeader;
using CodeBufferUpdateHeader = SbcProtocol::CodeBufferUpdateHeader;
using MasterClockHeader = SbcProtocol::MasterClockHeader;

// ---------------------------------------------------------------------------
// Firmware-local constants and structures (never sent over SPI as-is)
// ---------------------------------------------------------------------------

constexpr uint32_t SbcTransferTimeout =
	500; // maximum allowed delay between data exchanges during a full transfer (in ms)
constexpr uint32_t SbcMaxTransferTime = 50;		// maximum allowed time for a single SPI transfer
constexpr uint32_t SbcConnectionTimeout = 4000; // maximum time to wait for the next transfer (in ms)
constexpr uint32_t SbcTxDrainTimeout =
	250; // maximum time to wait for CDC TX FIFO to drain before entering direct mode (in ms)

// Transport carrying the protocol. Chosen at runtime, not part of the wire format.
enum class SbcTransportType : uint8_t
{
	spi,
	usb
};

// Framing for the USB transport. The USB link has its own header because it does not need the
// format code, protocol version or CRCs that the SPI transfer header carries.
struct UsbTransferHeader
{
	uint8_t numPackets;
	uint8_t padding;
	uint16_t dataLength;
	uint32_t padding2;
};

struct BooleanHeader
{
	bool value;
	uint8_t paddingA;
	uint16_t paddingB;
};

#endif

#endif /* SRC_SBC_MESSAGEFORMATS_H_ */
