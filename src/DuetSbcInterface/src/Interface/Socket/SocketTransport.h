// Socket transport: the transfer protocol carried over a Unix domain stream socket to a virtual
// controller instead of over SPI to a real one. This is the SBC side of the framing defined in
// DuetSpiProtocol/SocketLinkFormats.h; the peer is the system test bench's fake controller today
// and the Renode link peripheral in stage 2 of docs/devel/SYSTEM_EMULATION.md.
//
// The transfer content, CRCs and recovery skeleton are FullDuplexExchangeTransport's; what this class supplies is
// how the exchange crosses the link:
//
//   - one Transfer frame per direction per exchange, followed by one Response frame per direction
//     carrying each side's verdict, so the validation/resync/resend logic runs for real
//   - readiness as a Ready frame from the peer in place of the TfrRdy pin edge, and a withheld
//     Ready times an exchange out exactly as a low pin does
//   - the peer's "I have data" prompt as a DataAvailable frame in place of the pin level
//
// Errors on the socket itself (peer closed, refused, reset) surface as TransferTimeout, so the
// loop's reconnect path runs; the socket is re-dialled on the next exchange. Malformed frames are
// TransferError, so they count as resyncs like any other protocol violation.
#pragma once

#include <Config/Configuration.h>
#include <DuetSpiProtocol/SocketLinkFormats.h>
#include <Interface/FullDuplexExchangeTransport.h>

#include <cstdint>
#include <span>

namespace Duet::Sbc
{

	class SocketTransport final : public FullDuplexExchangeTransport
	{
	  public:
		explicit SocketTransport(const Config& config);
		~SocketTransport() override;

		SocketTransport(const SocketTransport&) = delete;
		SocketTransport& operator=(const SocketTransport&) = delete;
		SocketTransport(SocketTransport&&) = delete;
		SocketTransport& operator=(SocketTransport&&) = delete;

		bool WaitForTransferReason() override;

		// --- IAP over the framed link ---
		//
		// The packet-borne WriteIap/StartIap half is FullDuplexExchangeTransport's. These carry the bare-transfer
		// half as IapData/IapVerify frames, each gated by Ready like any exchange. The stage 1 fake
		// accepts and discards them; real flashing against emulated flash is stage 2's to test.
		bool FlashFirmwareSegment(std::span<const uint8_t> segment) override;
		bool VerifyFirmwareChecksum(uint32_t firmwareLength, uint16_t crc16) override;
		void WaitForIapReset() override;

	  protected:
		bool PerformExchange() override;
		void OnPrepareReconnect() noexcept override;

	  private:
		// Dial the configured endpoint if there is no live connection. Throws TransferTimeout on
		// failure, after pacing the retry so a dead peer does not spin the recovery loop.
		void EnsureConnected();
		void CloseSocket() noexcept;

		// Exact-count socket I/O, honouring the stop fd and the given deadline. Both throw
		// TransferTimeout on timeout, cancellation or a socket-level failure (closing the socket
		// first, so the next attempt re-dials).
		void ReadExact(std::span<uint8_t> buffer, int timeoutMs);
		void WriteAll(std::span<const uint8_t> data);

		void SendFrame(proto::SocketFrameType type,
					   std::span<const uint8_t> payload = {},
					   std::span<const uint8_t> tail = {});

		// Read the next frame header, absorbing Ready/DataAvailable notifications into their flags
		// on the way: the peer may interleave them with the exchange (an injection can prompt for a
		// transfer while one is already running), so they are handled wherever they appear.
		proto::SocketFrameHeader ReadContentFrameHeader(int timeoutMs);

		// Block until the peer has armed the next exchange (a Ready frame), then consume that
		// readiness. Throws TransferTimeout when it is withheld past the applicable timeout.
		void WaitForReady();

		// Validate the received transfer and answer the exchange's response codes. Returns true when
		// both sides accepted it.
		bool ValidateAndRespond(uint16_t receivedDataLength);

		uint32_t ReadResponseCode(int timeoutMs);

		[[nodiscard]] int ReadinessTimeout() const noexcept;

		int m_socketFd = -1;

		// One Ready frame consumed from the stream but not yet spent on an exchange, and the level
		// of the peer's data-available prompt.
		bool m_ready = false;
		bool m_dataAvailable = false;
	};

} // namespace Duet::Sbc
