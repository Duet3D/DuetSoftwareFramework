/*
 * Transport.h
 *
 * What the interface loop needs from whatever carries the link to the controller.
 *
 * LinkService owns the loop: it stages outgoing work, asks for a transfer, and walks the packets that
 * come back. None of that is about SPI. This is the contract it drives, so that a second transport
 * can be added by implementing it rather than by editing the loop.
 *
 * ---------------------------------------------------------------------------------------------
 * What this contract is, and what it is not
 * ---------------------------------------------------------------------------------------------
 *
 * It is an extraction, not a design: every method here is one LinkService already calls, so the
 * split is exactly where the code already divided. There is one implementation - SPI/SpiTransfer -
 * and until there is a second, the shape below describes SPI's needs and no others.
 *
 * Three things a second transport will run into, named here because finding them one at a time is
 * worse:
 *
 *   - The framing is a fixed-size full-duplex exchange. Both sides send a header and a data block of
 *     the same length at the same moment, and PerformFullTransfer is one such exchange. A stream
 *     transport (USB, TCP) has no such lockstep and would either emulate it or need this contract
 *     widened to admit a streaming shape.
 *   - Flow control is out of band. WaitForTransferReason blocks until the controller raises a line;
 *     a stream transport signals readiness in the stream itself.
 *   - Firmware update bypasses the protocol. Once IAP is running, each segment is a bare transfer
 *     gated only by that same line, so a transport that does not have one has to answer for IAP
 *     differently.
 *
 * Diagnostics that only make sense for one transport are not here - pin glitches and missed edges
 * belong to SpiTransfer, and the CApi reads them from it directly. Only what every transport can
 * answer is on this contract.
 *
 * The virtual calls cost a few nanoseconds against a transfer that takes tens of microseconds, so
 * the indirection is free in the only place that matters.
 */

#ifndef SRC_INTERFACE_TRANSPORT_H_
#define SRC_INTERFACE_TRANSPORT_H_

#include <DuetSpiProtocol/MessageFormats.h>

#include <cstdint>
#include <functional>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>

namespace Duet::Sbc
{
	namespace proto = duet::spi::protocol;

	// What a transport throws, and what the interface loop does about it. Part of the contract: the
	// loop catches both by type, so a second transport reports failures as these rather than as
	// something of its own.

	// Recoverable timeout or cancellation. The loop treats it as a lost connection and reconnects,
	// unless a stop was requested.
	class TransferTimeout : public std::runtime_error
	{
	  public:
		explicit TransferTimeout(const std::string& what)
			: std::runtime_error(what)
		{
		}
	};

	// Fatal protocol error. Propagates out of the transfer loop.
	class TransferError : public std::runtime_error
	{
	  public:
		explicit TransferError(const std::string& what)
			: std::runtime_error(what)
		{
		}
	};

	class Transport
	{
	  public:
		Transport() = default;
		virtual ~Transport() = default;

		Transport(const Transport&) = delete;
		Transport& operator=(const Transport&) = delete;
		Transport(Transport&&) = delete;
		Transport& operator=(Transport&&) = delete;

		// --- Reporting -------------------------------------------------------------------------

		// Recovery and resync events, from the interface thread. The view is only valid for the
		// duration of the call; a receiver that keeps the text copies it.
		using LogCallback = std::function<void(std::string_view message)>;
		virtual void SetLogCallback(LogCallback cb) = 0;

		// Called when a live connection is first seen to have dropped, from the thread that saw it.
		// Reporting it here rather than after PerformFullTransfer returns is the point: that call
		// does not return until the link is back, so anything waiting on it learns too late.
		using ConnectionLostCallback = std::function<void(std::string_view reason)>;
		virtual void SetConnectionLostCallback(ConnectionLostCallback cb) = 0;

		// --- Connection ------------------------------------------------------------------------

		// Establish the initial connection. Throws on failure: a controller that is absent or
		// fundamentally incompatible at startup is worth surfacing rather than looping on.
		virtual void Connect() = 0;

		// Perform one full transfer synchronously. During normal operation this does not throw for a
		// transfer error - it recovers by resynchronising, with backoff - only to unwind on Stop(),
		// or from Connect() if the initial handshake fails.
		virtual void PerformFullTransfer(bool connecting = false) = 0;

		// Abandon the current connection and force a fresh handshake on the next transfer. Safe to
		// call from the loop after any unexpected error, such as a malformed packet.
		virtual void ResetConnection() = 0;

		// Unblock anything waiting and stop accepting transfers.
		virtual void Stop() = 0;

		[[nodiscard]] virtual bool IsConnected() const = 0;

		// True if the controller has been reset since the last transfer.
		[[nodiscard]] virtual bool HadReset() const = 0;

		[[nodiscard]] virtual int ProtocolVersion() const = 0;

		// --- Transfer gating -------------------------------------------------------------------

		// Block while idle until there is a reason to start a transfer. True to start one now, false
		// if the caller should re-stage its outgoing data and ask again.
		virtual bool WaitForTransferReason() = 0;

		// Tell the loop there is a reason to start a transfer. Callable from any thread.
		virtual void RequestTransfer() = 0;

		// --- Incoming --------------------------------------------------------------------------

		// The controller's step clock as of the transfer just completed, and the movement delay it
		// has accumulated. Both ride in the header rather than in a packet, so the gap between the
		// reading and its local timestamp does not depend on what else the transfer carried.
		[[nodiscard]] virtual uint32_t RxMasterClock() const = 0;
		[[nodiscard]] virtual uint32_t RxHiccupTime() const = 0;

		// Read the next packet header, or false if none remain. Advances to the payload.
		virtual bool ReadNextPacket(proto::PacketHeader& packet) = 0;
		// The payload of the packet most recently returned by ReadNextPacket. Valid until the next
		// full transfer overwrites the receive buffer.
		[[nodiscard]] virtual std::span<const uint8_t> PacketData() const = 0;
		[[nodiscard]] virtual int PacketsToRead() const = 0;

		// Where the read cursor is, and the received block as a whole, for dumping a malformed
		// packet to the log.
		[[nodiscard]] virtual size_t RxPointer() const = 0;
		[[nodiscard]] virtual std::span<const uint8_t> RxData() const = 0;
		[[nodiscard]] virtual const proto::PacketHeader& LastPacket() const = 0;

		// Resend a packet the controller asked for. Throws if the id is unknown.
		virtual void ResendPacket(const proto::PacketHeader& packet, proto::SbcRequest& sbcRequestOut) = 0;

		// --- Outgoing --------------------------------------------------------------------------
		//
		// All of these return false if the staged data does not fit in this transfer, in which case
		// the caller keeps it and offers it again on the next one. None of them blocks.

		virtual bool WriteEmergencyStop() = 0;
		virtual bool WriteReset() = 0;
		virtual bool WriteEnableCan(bool enable) = 0;
		virtual bool WriteScheduleMove(std::span<const uint8_t> packet) = 0;
		virtual bool WriteCanMessage(uint16_t txToken,
									 uint16_t msgType,
									 uint16_t replyType,
									 uint8_t dstAddress,
									 bool isResponse,
									 std::span<const uint8_t> payload) = 0;
		virtual bool WriteMessage(uint32_t messageFlags, std::string_view message) = 0;

		// --- Firmware update -------------------------------------------------------------------
		//
		// The flashing handshake bypasses the regular protocol; see the note at the top of this file
		// for why that is a problem a second transport has to answer.

		virtual bool WriteIapSegment(std::span<const uint8_t> segment) = 0;
		virtual void StartIap() = 0;
		virtual bool FlashFirmwareSegment(std::span<const uint8_t> segment) = 0;
		virtual bool VerifyFirmwareChecksum(uint32_t length, uint16_t crc16) = 0;
		virtual void WaitForIapReset() = 0;

		// --- Diagnostics every transport can answer --------------------------------------------

		// How many times the connection has been resynchronised after an error.
		[[nodiscard]] virtual int ResyncCount() const = 0;

		// The longest a transfer spent waiting for the controller to say it was ready, in
		// milliseconds. Zero for a transport whose readiness is not signalled out of band.
		virtual double MaxPinWaitDurationMs() = 0;

		// The longest gap between one transfer completing and the next, in milliseconds.
		//
		// Reading it clears it, which is why it is not const. That is the wrong shape - a second
		// reader sees zero however bad the first reading was - and it is kept only because the
		// harness and the CApi both read it exactly once. Splitting it into a read and a reset is
		// the same fix DDARing::GetStats/ResetStats had.
		virtual double MaxFullTransferDelayMs() = 0;
	};
}

#endif /* SRC_INTERFACE_TRANSPORT_H_ */
