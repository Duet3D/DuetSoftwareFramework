// SBC-side SPI transfer engine: a faithful C++ port of
// DuetControlServer/Link/Adapter/SPI.cs (SPI transport only, no USB/IAP/firmware-update).
//
// It owns the TfrRdy/DataAvailable GPIO lines and the spidev device, drives the header/data/response
// exchange state machine against RepRapFirmware, and exposes packet read/write helpers plus the
// RequestTransfer / WaitForTransferReason gating used by the interface loop.
#pragma once

#include <Config/Configuration.h>
#include <DuetSpiProtocol/MessageFormats.h>
#include <Hardware/GpioInputPin.h>
#include <Hardware/OutputGpioPin.h>
#include <Hardware/SpiDevice.h>
#include <Interface/Transport.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <functional>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

namespace Duet::Sbc
{

	namespace proto = duet::spi::protocol;

	class SpiTransfer final : public Transport
	{
	  public:
		explicit SpiTransfer(const Config& config);
		~SpiTransfer();

		SpiTransfer(const SpiTransfer&) = delete;
		SpiTransfer& operator=(const SpiTransfer&) = delete;
		SpiTransfer(SpiTransfer&&) = delete;
		SpiTransfer& operator=(SpiTransfer&&) = delete;

		// Optional callback used to report recovery/resync events (thread: interface thread).
		using LogCallback = std::function<void(const std::string& message)>;
		void SetLogCallback(LogCallback cb) override { m_logCallback = std::move(cb); }

		// Called when a live connection is first seen to have dropped, from the thread that saw it.
		// Reporting it here rather than after PerformFullTransfer returns is the whole point: that
		// call does not return until the link is back, so anything waiting on it learns too late
		using ConnectionLostCallback = std::function<void(const std::string& reason)>;
		void SetConnectionLostCallback(ConnectionLostCallback cb) override { m_connectionLostCallback = std::move(cb); }

		// Whether the last transfer completed after an outage, i.e. this is a reconnection
		[[nodiscard]] bool HadTimeout() const noexcept { return m_hadTimeout; }

		// Establish the initial connection (performs the first full transfer). Throws on failure.
		void Connect() override;

		// Perform a full data transfer synchronously. During normal operation this never throws for a
		// transfer error: it recovers internally by resynchronising with the controller (with backoff).
		// It only throws to unwind on Stop(), or from Connect() if the initial handshake fails.
		// `connecting` is true only for the very first one.
		void PerformFullTransfer(bool connecting = false) override;

		// Abandon the current connection and force a fresh handshake on the next transfer. Safe to call
		// from the interface loop after any unexpected error (e.g. while processing a malformed packet).
		void ResetConnection() override;

		// Number of times the connection has been resynchronised after an error (diagnostics).
		[[nodiscard]] int ResyncCount() const noexcept override { return m_numResyncs; }

		[[nodiscard]] int ProtocolVersion() const noexcept override { return m_protocolVersion; }

		// True once the handshake has completed and the link is up.
		[[nodiscard]] bool IsConnected() const noexcept override { return m_connected; }

		// True if the controller has been reset (sequence number discontinuity).
		[[nodiscard]] bool HadReset() const noexcept override;

		// The offset the read cursor is currently at, and the received data as a whole. Used to dump a
		// malformed packet for diagnostics (SPI.cs DumpMalformedPacket).
		[[nodiscard]] size_t RxPointer() const noexcept override { return m_rxPointer; }
		[[nodiscard]] const uint8_t* RxBuffer() const noexcept override { return m_rxBuffer.data(); }
		[[nodiscard]] uint16_t RxDataLength() const noexcept override { return m_rxHeader.dataLength; }
		[[nodiscard]] const proto::PacketHeader& LastPacket() const noexcept override { return m_lastPacket; }

		// --- Transfer gating (see SPI.cs WaitForTransferReason / RequestTransfer) ---

		// Block while idle until there is a reason to start a full transfer. Returns true if a transfer
		// should start now, false if the caller should re-stage data and call again.
		bool WaitForTransferReason() override;

		// Notify the transfer loop that there is a reason to start a full transfer.
		void RequestTransfer() override;

		// --- Reading incoming packets ---
		[[nodiscard]] int PacketsToRead() const noexcept override { return m_rxHeader.numPackets; }

		// The controller's step clock as of the transfer just completed, and the movement delay it
		// has accumulated. Both ride in the header rather than in a packet so that the delay between
		// the reading and its local timestamp does not depend on what else the transfer carried
		[[nodiscard]] uint32_t RxMasterClock() const noexcept override { return m_rxHeader.masterClock; }
		[[nodiscard]] uint32_t RxHiccupTime() const noexcept override { return m_rxHeader.hiccupTime; }
		// Read the next packet header, or return false if none remain. Advances to the payload.
		bool ReadNextPacket(proto::PacketHeader& packet) override;
		// The payload of the packet most recently returned by ReadNextPacket.
		[[nodiscard]] const uint8_t* PacketData() const noexcept override { return m_packetData; }
		[[nodiscard]] uint16_t PacketDataLength() const noexcept override { return m_packetDataLength; }

		// --- Writing outgoing packets (return false if the buffer is full) ---
		bool WriteEmergencyStop() override;
		bool WriteReset() override;
		bool WriteEnableCan(bool enable) override;
		// Stage a prepared move for the controller. `packet` is a whole ScheduleMove payload - the
		// header and its driver records - which is built by the motion engine and copied through
		// unaltered. Returns false if it does not fit in this transfer, in which case the caller
		// keeps it and offers it again.
		bool WriteScheduleMove(const uint8_t* packet, size_t length) override;

		bool WriteCanMessage(uint16_t txToken,
							 uint16_t msgType,
							 uint16_t replyType,
							 uint8_t dstAddress,
							 bool isResponse,
							 const uint8_t* payload,
							 size_t payloadLength);
		bool WriteMessage(uint32_t messageFlags, const std::string& message) override;

		// Resend a packet the firmware asked for. Throws TransferError if the id is unknown.
		void ResendPacket(const proto::PacketHeader& packet, proto::SbcRequest& sbcRequestOut) override;

		// --- IAP / firmware update (SPI.cs WriteIapSegment .. WaitForIapReset) ---
		//
		// These run the flashing handshake, which bypasses the regular header/data protocol: once IAP is
		// running, each segment is a bare full-duplex SPI transfer gated only by the TfrRdy pin. While
		// `_updating` is set the pin waits use the much longer IapTimeout, because IAP erases flash
		// between segments.

		// Send one chunk of the IAP binary as a WriteIap packet and perform a full transfer.
		// Returns false if `length` is zero (i.e. the binary has been sent in full).
		bool WriteIapSegment(const uint8_t* data, size_t length) override;

		// Tell the firmware to boot the IAP program, then wait for IAP to raise TfrRdy.
		void StartIap() override;

		// Clock out one firmware chunk to the running IAP program. Chunks shorter than
		// FirmwareSegmentSize are padded with 0xFF, as IAP itself does once complete.
		// Returns false if `length` is zero.
		bool FlashFirmwareSegment(const uint8_t* data, size_t length) override;

		// Send the firmware length + CRC16 to IAP and read back its verdict.
		bool VerifyFirmwareChecksum(uint32_t firmwareLength, uint16_t crc16) override;

		// Wait for IAP to reboot the controller and re-arm the handshake state.
		void WaitForIapReset() override;

		// Request cooperative shutdown of any in-progress wait.
		void Stop() noexcept override;
		[[nodiscard]] bool StopRequested() const noexcept { return m_stop.load(std::memory_order_relaxed); }

		// --- Diagnostics ---
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
		// Not on Transport: these count edges on a GPIO line, which only a transport that has one can
		// answer. The CApi asks for them through this class, and reports zero for a transport that is
		// not this one.
		[[nodiscard]] int TfrPinGlitches() const noexcept { return m_numTfrPinGlitches; }
		[[nodiscard]] int MissedEdges() const noexcept { return m_transferReadyPin->MissedEdges(); }

	  private:
		// State-machine steps (mirrors SPI.cs)
		void WaitForTransfer(bool inTransfer = true);
		void WriteCrc();
		bool ExchangeHeader();
		uint32_t ExchangeResponse(uint32_t response);
		bool ExchangeData();
		bool ExchangeDataResponse(bool& success);

		// Packet writing internals
		void WritePacketHeader(proto::SbcRequest request, size_t dataLength = 0);
		uint8_t* GetWriteBuffer(size_t dataLength);
		[[nodiscard]] bool CanWritePacket(size_t dataLength = 0) const noexcept;

		void ThrowIfStopped();

		// Recovery: put the link back into the "reconnecting" state so the next transfer re-handshakes.
		void PrepareReconnect(const char* reason);
		// Sleep up to `ms`, returning early if Stop() is called (used to pace error retries).
		void InterruptibleSleep(int ms);

		const Config config;
		const size_t bufferSize;

		std::unique_ptr<GpioInputPin> m_transferReadyPin;
		std::unique_ptr<GpioInputPin> m_dataAvailablePin;
		// Optional scope trigger: high while data is staged, low once the transfer completes
		std::unique_ptr<OutputGpioPin> m_sbcDataAvailablePin;

		// The rising-edge sequence number already consumed for the previous exchange. A sub-exchange must
		// wait for a rising edge newer than this rather than trusting a possibly stale high level.
		uint32_t m_consumedRisingEdgeSeq = 0;

		// eventfds used to wake the interface thread out of poll(). The request fd is only watched between
		// transfers (WaitForTransferReason); the stop fd is watched everywhere so shutdown is prompt. Keeping
		// them separate means a RequestTransfer during a transfer does not spuriously wake the TfrRdy wait.
		int m_requestEventFd = -1;
		int m_stopEventFd = -1;

		std::unique_ptr<SpiDevice> m_spiDevice;

		bool m_waitingForFirstTransfer = true;
		bool m_connected = false;
		bool m_hadTimeout = false;
		bool m_resetting = false;
		// True between StartIap() and WaitForIapReset(): the controller is running the IAP program, so the
		// regular transfer protocol is suspended and pin waits use IapTimeout
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

		std::vector<uint8_t>& CurrentTxBuffer() { return m_txBuffers[m_txBufferIndex]; }

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
		int m_numTfrPinGlitches = 0;
		uint16_t m_maxRxSize = 0;
		uint16_t m_maxTxSize = 0;
	};

} // namespace Duet::Sbc
