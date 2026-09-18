// SBC-side SPI transfer engine: a faithful C++ port of
// DuetControlServer/Link/Adapter/SPI.cs (SPI transport only, no USB).
//
// The packet buffers, CRC bookkeeping and retry/recovery skeleton live in FullDuplexExchangeTransport; this class
// owns what is SPI about the link: the TfrRdy/DataAvailable GPIO lines, the spidev device, the
// header/data/response exchange state machine against the controller, and the bare-transfer IAP
// flashing handshake.
#pragma once

#include <Config/Configuration.h>
#include <DuetSpiProtocol/MessageFormats.h>
#include <Hardware/GpioInputPin.h>
#include <Hardware/OutputGpioPin.h>
#include <Hardware/SpiDevice.h>
#include <Interface/FullDuplexExchangeTransport.h>

#include <cstdint>
#include <memory>

namespace Duet::Sbc
{

	class SpiTransfer final : public FullDuplexExchangeTransport
	{
	  public:
		explicit SpiTransfer(const Config& config);
		~SpiTransfer() override;

		SpiTransfer(const SpiTransfer&) = delete;
		SpiTransfer& operator=(const SpiTransfer&) = delete;
		SpiTransfer(SpiTransfer&&) = delete;
		SpiTransfer& operator=(SpiTransfer&&) = delete;

		// --- Transfer gating (see SPI.cs WaitForTransferReason / RequestTransfer) ---

		// Block while idle until there is a reason to start a full transfer. Returns true if a transfer
		// should start now, false if the caller should re-stage data and call again.
		bool WaitForTransferReason() override;

		// Notify the transfer loop that there is a reason to start a full transfer.
		void RequestTransfer() override;

		// --- IAP / firmware update (SPI.cs FlashFirmwareSegment .. WaitForIapReset) ---
		//
		// These run the flashing handshake, which bypasses the regular header/data protocol: once IAP is
		// running, each segment is a bare full-duplex SPI transfer gated only by the TfrRdy pin. While
		// `m_updating` is set the pin waits use the much longer IapTimeout, because IAP erases flash
		// between segments.

		// Clock out one firmware chunk to the running IAP program. Chunks shorter than
		// FirmwareSegmentSize are padded with 0xFF, as IAP itself does once complete.
		// Returns false if the segment is empty.
		bool FlashFirmwareSegment(std::span<const uint8_t> segment) override;

		// Send the firmware length + CRC16 to IAP and read back its verdict.
		bool VerifyFirmwareChecksum(uint32_t firmwareLength, uint16_t crc16) override;

		// Wait for IAP to reboot the controller and re-arm the handshake state.
		void WaitForIapReset() override;

		// Not on Transport: these count edges on a GPIO line, which only a transport that has one can
		// answer. The CApi asks for them through this class, and reports zero for a transport that is
		// not this one.
		[[nodiscard]] int TfrPinGlitches() const noexcept { return m_numTfrPinGlitches; }
		[[nodiscard]] int MissedEdges() const noexcept { return m_transferReadyPin->MissedEdges(); }

	  protected:
		// One attempt at the SPI exchange: header exchange, then the data exchange if either side has
		// data. See FullDuplexExchangeTransport::PerformExchange for the contract.
		bool PerformExchange() override;

		void OnPrepareReconnect() noexcept override;
		void OnTransferCompleted() noexcept override;

	  private:
		// State-machine steps (mirrors SPI.cs)
		void WaitForTransfer(bool inTransfer = true);
		bool ExchangeHeader();
		uint32_t ExchangeResponse(uint32_t response);
		bool ExchangeData();
		bool ExchangeDataResponse(bool& success);

		std::unique_ptr<GpioInputPin> m_transferReadyPin;
		std::unique_ptr<GpioInputPin> m_dataAvailablePin;
		// Optional scope trigger: high while data is staged, low once the transfer completes
		std::unique_ptr<OutputGpioPin> m_sbcDataAvailablePin;

		// The rising-edge sequence number already consumed for the previous exchange. A sub-exchange must
		// wait for a rising edge newer than this rather than trusting a possibly stale high level.
		uint32_t m_consumedRisingEdgeSeq = 0;

		std::unique_ptr<SpiDevice> m_spiDevice;

		int m_numTfrPinGlitches = 0;
	};

} // namespace Duet::Sbc
