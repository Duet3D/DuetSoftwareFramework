// SBC-side SPI transfer engine: a faithful C++ port of
// DuetControlServer/Link/Adapter/SPI.cs (SPI transport only, no USB/IAP/firmware-update).
//
// It owns the TfrRdy/DataAvailable GPIO lines and the spidev device, drives the header/data/response
// exchange state machine against RepRapFirmware, and exposes packet read/write helpers plus the
// RequestTransfer / WaitForTransferReason gating used by the interface loop.
#pragma once

#include "DuetSbc/Config.h"
#include "DuetSbc/GpioInputPin.h"
#include "DuetSbc/OutputGpioPin.h"
#include "DuetSbc/SpiDevice.h"
#include "DuetSpiProtocol/MessageFormats.h"

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

	// Recoverable timeout/cancellation (maps to C# OperationCanceledException): the interface loop
	// treats this as a lost connection and reconnects, unless a stop was requested.
	class TransferTimeout : public std::runtime_error
	{
	  public:
		explicit TransferTimeout(const std::string& what)
			: std::runtime_error(what)
		{
		}
	};

	// Fatal protocol error (maps to a plain C# Exception): propagates out of the transfer loop.
	class TransferError : public std::runtime_error
	{
	  public:
		explicit TransferError(const std::string& what)
			: std::runtime_error(what)
		{
		}
	};

	class SbcTransfer
	{
	  public:
		explicit SbcTransfer(const Config& config);
		~SbcTransfer();

		SbcTransfer(const SbcTransfer&) = delete;
		SbcTransfer& operator=(const SbcTransfer&) = delete;
		SbcTransfer(SbcTransfer&&) = delete;
		SbcTransfer& operator=(SbcTransfer&&) = delete;

		// Optional callback used to report recovery/resync events (thread: interface thread).
		using LogCallback = std::function<void(const std::string& message)>;
		void SetLogCallback(LogCallback cb) { m_logCallback = std::move(cb); }

		// Establish the initial connection (performs the first full transfer). Throws on failure.
		void Connect();

		// Perform a full data transfer synchronously. During normal operation this never throws for a
		// transfer error: it recovers internally by resynchronising with the controller (with backoff).
		// It only throws to unwind on Stop(), or from Connect() if the initial handshake fails.
		// `connecting` is true only for the very first one.
		void PerformFullTransfer(bool connecting = false);

		// Abandon the current connection and force a fresh handshake on the next transfer. Safe to call
		// from the interface loop after any unexpected error (e.g. while processing a malformed packet).
		void ResetConnection();

		// Number of times the connection has been resynchronised after an error (diagnostics).
		[[nodiscard]] int ResyncCount() const noexcept { return m_numResyncs; }

		[[nodiscard]] int ProtocolVersion() const noexcept { return m_protocolVersion; }

		// True once the handshake has completed and the link is up.
		[[nodiscard]] bool IsConnected() const noexcept { return m_connected; }

		// True if the controller has been reset (sequence number discontinuity).
		[[nodiscard]] bool HadReset() const noexcept;

		// The offset the read cursor is currently at, and the received data as a whole. Used to dump a
		// malformed packet for diagnostics (SPI.cs DumpMalformedPacket).
		[[nodiscard]] size_t RxPointer() const noexcept { return m_rxPointer; }
		[[nodiscard]] const uint8_t* RxBuffer() const noexcept { return m_rxBuffer.data(); }
		[[nodiscard]] uint16_t RxDataLength() const noexcept { return m_rxHeader.dataLength; }
		[[nodiscard]] const proto::PacketHeader& LastPacket() const noexcept { return m_lastPacket; }

		// --- Transfer gating (see SPI.cs WaitForTransferReason / RequestTransfer) ---

		// Block while idle until there is a reason to start a full transfer. Returns true if a transfer
		// should start now, false if the caller should re-stage data and call again.
		bool WaitForTransferReason();

		// Notify the transfer loop that there is a reason to start a full transfer.
		void RequestTransfer();

		// --- Reading incoming packets ---
		[[nodiscard]] int PacketsToRead() const noexcept { return m_rxHeader.numPackets; }
		// Read the next packet header, or return false if none remain. Advances to the payload.
		bool ReadNextPacket(proto::PacketHeader& packet);
		// The payload of the packet most recently returned by ReadNextPacket.
		[[nodiscard]] const uint8_t* PacketData() const noexcept { return m_packetData; }
		[[nodiscard]] uint16_t PacketDataLength() const noexcept { return m_packetDataLength; }

		// --- Writing outgoing packets (return false if the buffer is full) ---
		bool WriteEmergencyStop();
		bool WriteReset();
		bool WriteEnableCan(bool enable);
		bool WriteCanMessage(uint16_t txToken,
							 uint16_t msgType,
							 uint16_t replyType,
							 uint8_t dstAddress,
							 bool isResponse,
							 const uint8_t* payload,
							 size_t payloadLength);
		bool WriteMessage(uint32_t messageFlags, const std::string& message);

		// Resend a packet the firmware asked for. Throws TransferError if the id is unknown.
		void ResendPacket(const proto::PacketHeader& packet, proto::SbcRequest& sbcRequestOut);

		// --- IAP / firmware update (SPI.cs WriteIapSegment .. WaitForIapReset) ---
		//
		// These run the flashing handshake, which bypasses the regular header/data protocol: once IAP is
		// running, each segment is a bare full-duplex SPI transfer gated only by the TfrRdy pin. While
		// `_updating` is set the pin waits use the much longer IapTimeout, because IAP erases flash
		// between segments.

		// Send one chunk of the IAP binary as a WriteIap packet and perform a full transfer.
		// Returns false if `length` is zero (i.e. the binary has been sent in full).
		bool WriteIapSegment(const uint8_t* data, size_t length);

		// Tell the firmware to boot the IAP program, then wait for IAP to raise TfrRdy.
		void StartIap();

		// Clock out one firmware chunk to the running IAP program. Chunks shorter than
		// FirmwareSegmentSize are padded with 0xFF, as IAP itself does once complete.
		// Returns false if `length` is zero.
		bool FlashFirmwareSegment(const uint8_t* data, size_t length);

		// Send the firmware length + CRC16 to IAP and read back its verdict.
		bool VerifyFirmwareChecksum(uint32_t firmwareLength, uint16_t crc16);

		// Wait for IAP to reboot the controller and re-arm the handshake state.
		void WaitForIapReset();

		// Request cooperative shutdown of any in-progress wait.
		void Stop() noexcept;
		[[nodiscard]] bool StopRequested() const noexcept { return m_stop.load(std::memory_order_relaxed); }

		// --- Diagnostics ---
		double MaxFullTransferDelayMs()
		{
			const double v = m_maxFullTransferDelay;
			m_maxFullTransferDelay = 0;
			return v;
		}
		double MaxPinWaitDurationMs()
		{
			const double v = m_maxPinWaitDuration;
			m_maxPinWaitDuration = 0;
			return v;
		}
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
		void PrepareReconnect();
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
		int m_consecutiveErrors = 0;
		int m_numResyncs = 0;

		// Diagnostics
		std::chrono::steady_clock::time_point m_keepAliveStart;
		std::chrono::steady_clock::time_point m_fullTransferStart;
		bool m_fullTransferTimerRunning = false;
		double m_maxFullTransferDelay = 0;
		double m_maxPinWaitDuration = 0;
		int m_numTfrPinGlitches = 0;
		int m_maxRxSize = 0;
		int m_maxTxSize = 0;
	};

} // namespace Duet::Sbc
