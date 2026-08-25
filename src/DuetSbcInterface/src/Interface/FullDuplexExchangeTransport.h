// The shared engine of the transports that keep the lockstep exchange shape: a fixed-size transfer
// each way per exchange, validated by the header/data CRCs and acknowledged with response codes.
// SpiTransfer and SocketTransport both are that shape and derive from this; a stream transport
// (USB, TCP without the framing) is not, and would implement Transport directly instead.
//
// Both derived transports share one implementation of what a transfer *contains* and differ only
// in how the bytes cross to the controller.
//
// What lives here:
//   - the TX/RX data buffers, packet cursors and the three-deep TX history that serves resends
//   - the transfer headers, their CRCs, and the sequence-number bookkeeping behind HadReset()
//   - PerformFullTransfer's retry/recovery skeleton: timeout handling, resync counting, backoff,
//     and the connection-lost reporting that runs from PrepareReconnect
//   - the outgoing packet writers, ReadNextPacket, ResendPacket
//   - WriteIapSegment/StartIap, which are ordinary packets plus a state change
//   - the stop/request eventfds and the cancellation they provide
//
// What a transport supplies:
//   - PerformExchange(): one attempt at exchanging the staged headers and data. Returns false to
//     retry, throws TransferTimeout (recoverable) or TransferError (resync) like the loop expects.
//   - WaitForTransferReason(): the idle gate, because what "the controller has data" looks like is
//     the transport's business (a pin level, a frame on a socket).
//   - the bare-transfer IAP steps (FlashFirmwareSegment and friends), which bypass the packet
//     protocol entirely.
#pragma once

#include <Config/Configuration.h>
#include <DuetSpiProtocol/MessageFormats.h>
#include <Interface/Transport.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <span>
#include <string_view>
#include <vector>

namespace Duet::Sbc
{

	class FullDuplexExchangeTransport : public Transport
	{
	  public:
		explicit FullDuplexExchangeTransport(const Config& config);
		~FullDuplexExchangeTransport() override;

		void SetLogCallback(LogCallback cb) override { m_logCallback = std::move(cb); }
		void SetConnectionLostCallback(ConnectionLostCallback cb) override
		{
			m_connectionLostCallback = std::move(cb);
		}

		// Establish the initial connection (performs the first full transfer). Throws on failure.
		void Connect() override;

		// Perform a full transfer synchronously. During normal operation this never throws for a
		// transfer error: it recovers internally by resynchronising with the controller (with
		// backoff). It only throws to unwind on Stop(), or from Connect() if the handshake fails.
		void PerformFullTransfer(bool connecting = false) override;

		// Abandon the current connection and force a fresh handshake on the next transfer.
		void ResetConnection() override;

		[[nodiscard]] int ResyncCount() const noexcept override { return m_numResyncs; }
		[[nodiscard]] int ProtocolVersion() const noexcept override { return m_protocolVersion; }
		[[nodiscard]] bool IsConnected() const noexcept override { return m_connected; }
		[[nodiscard]] bool HadReset() const noexcept override;

		// Whether the last transfer completed after an outage, i.e. this is a reconnection
		[[nodiscard]] bool HadTimeout() const noexcept { return m_hadTimeout; }

		[[nodiscard]] size_t RxPointer() const noexcept override { return m_rxPointer; }
		[[nodiscard]] std::span<const uint8_t> RxData() const noexcept override
		{
			return {m_rxBuffer.data(), m_rxHeader.dataLength};
		}
		[[nodiscard]] const proto::PacketHeader& LastPacket() const noexcept override { return m_lastPacket; }

		[[nodiscard]] int PacketsToRead() const noexcept override { return m_rxHeader.numPackets; }
		[[nodiscard]] uint32_t RxMasterClock() const noexcept override { return m_rxHeader.masterClock; }
		[[nodiscard]] uint32_t RxHiccupTime() const noexcept override { return m_rxHeader.hiccupTime; }
		bool ReadNextPacket(proto::PacketHeader& packet) override;
		[[nodiscard]] std::span<const uint8_t> PacketData() const noexcept override
		{
			return {m_packetData, m_packetDataLength};
		}

		bool WriteEmergencyStop() override;
		bool WriteReset() override;
		bool WriteEnableCan(bool enable) override;
		bool WriteScheduleMove(std::span<const uint8_t> packet) override;
		bool WriteCanMessage(uint16_t txToken,
							 uint16_t msgType,
							 uint16_t replyType,
							 uint8_t dstAddress,
							 bool isResponse,
							 std::span<const uint8_t> payload) override;
		bool WriteMessage(uint32_t messageFlags, std::string_view message) override;

		void ResendPacket(const proto::PacketHeader& packet, proto::SbcRequest& sbcRequestOut) override;

		// The packet-borne half of a firmware update. The bare-transfer half that follows StartIap
		// bypasses the packet protocol and stays with the transport.
		bool WriteIapSegment(std::span<const uint8_t> segment) override;
		void StartIap() override;

		// Tell the loop there is a reason to start a transfer. Callable from any thread. A transport
		// with more to do (SpiTransfer raises its scope-trigger line) overrides and calls this.
		void RequestTransfer() override;

		// Request cooperative shutdown of any in-progress wait.
		void Stop() noexcept override;
		[[nodiscard]] bool StopRequested() const noexcept { return m_stop.load(std::memory_order_relaxed); }

		double MaxFullTransferDelayMs() override
		{
			const double v = m_maxFullTransferDelay;
			m_maxFullTransferDelay = 0;
			return v;
		}
		double MaxPinWaitDurationMs() override
		{
			const double v = m_maxPinWaitDuration;
			m_maxPinWaitDuration = 0;
			return v;
		}

	  protected:
		using clock = std::chrono::steady_clock;

		// One attempt at exchanging the staged transfer with the controller: headers, data and the
		// response codes that acknowledge them. Returns true when both sides accepted the exchange,
		// false to retry it (the staged headers and data are left untouched, so a retry re-offers
		// the same transfer). Throws TransferTimeout for a recoverable timeout and TransferError for
		// a fatal protocol error, which the skeleton in PerformFullTransfer turns into a resync.
		virtual bool PerformExchange() = 0;

		// Transport-specific cleanup when the connection is abandoned (drain pin edges, drop a
		// scope-trigger line, flush a socket). Runs from PrepareReconnect, so it must not throw.
		virtual void OnPrepareReconnect() noexcept {}

		// Called once a full transfer has completed successfully, after the buffers have rotated.
		virtual void OnTransferCompleted() noexcept {}

		void ThrowIfStopped();

		// Recovery: put the link back into the "reconnecting" state so the next transfer
		// re-handshakes, and abandon whatever was staged for the transfer that did not happen.
		void PrepareReconnect(const char* reason);

		// Sleep up to `ms`, returning early if Stop() is called (used to pace error retries).
		void InterruptibleSleep(int ms);

		// (Re)write the CRC fields of the staged TX header for the staged data.
		void WriteCrc();

		// Packet writing internals
		void WritePacketHeader(proto::SbcRequest request, size_t dataLength = 0);
		uint8_t* GetWriteBuffer(size_t dataLength);
		[[nodiscard]] bool CanWritePacket(size_t dataLength = 0) const noexcept;

		std::vector<uint8_t>& CurrentTxBuffer() { return m_txBuffers[m_txBufferIndex]; }

		const Config config;
		const size_t bufferSize;

		// eventfds used to wake the interface thread out of poll(). The request fd is only watched
		// between transfers (WaitForTransferReason); the stop fd is watched everywhere so shutdown
		// is prompt. Keeping them separate means a RequestTransfer during a transfer does not
		// spuriously wake a wait for the controller.
		int m_requestEventFd = -1;
		int m_stopEventFd = -1;

		bool m_waitingForFirstTransfer = true;
		bool m_connected = false;
		bool m_hadTimeout = false;
		bool m_resetting = false;
		// True between StartIap() and WaitForIapReset(): the controller is running the IAP program,
		// so the regular transfer protocol is suspended and waits use IapTimeout
		bool m_updating = false;
		int m_protocolVersion = 0;
		uint16_t m_lastTransferNumber = 0;

		// Headers
		proto::SpiTransferHeader m_rxHeader{};
		proto::SpiTransferHeader m_txHeader{};
		uint8_t m_packetId = 0;

		// Data buffers: three TX buffers so resend requests can be served
		static constexpr int kNumTxBuffers = 3;
		std::vector<std::vector<uint8_t>> m_txBuffers;
		int m_txBufferIndex = 0;
		std::vector<uint8_t> m_rxBuffer;
		size_t m_rxPointer = 0;
		size_t m_txPointer = 0;

		// Most recently read packet payload
		proto::PacketHeader m_lastPacket{};
		const uint8_t* m_packetData = nullptr;
		uint16_t m_packetDataLength = 0;

		// Requests currently being resent (avoid duplicates)
		std::vector<proto::SbcRequest> m_packetsBeingResent;

		std::atomic<bool> m_stop{false};

		// Error recovery
		LogCallback m_logCallback;
		ConnectionLostCallback m_connectionLostCallback;
		int m_consecutiveErrors = 0;
		int m_numResyncs = 0;

		// Diagnostics
		std::chrono::steady_clock::time_point m_keepAliveStart;
		std::chrono::steady_clock::time_point m_fullTransferStart;
		bool m_fullTransferTimerRunning = false;
		double m_maxFullTransferDelay = 0;
		double m_maxPinWaitDuration = 0;
		uint16_t m_maxRxSize = 0;
		uint16_t m_maxTxSize = 0;
	};

} // namespace Duet::Sbc
