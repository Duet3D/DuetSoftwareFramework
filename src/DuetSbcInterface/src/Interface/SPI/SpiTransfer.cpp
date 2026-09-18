#include <Interface/SPI/SpiTransfer.h>

#include <Storage/Crc.h>

#include <poll.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <cstring>
#include <system_error>

namespace Duet::Sbc
{

	namespace
	{

		inline uint32_t ReadU32(const uint8_t* p)
		{
			uint32_t v = 0;
			std::memcpy(&v, p, sizeof(v));
			return v;
		}

		inline uint16_t ReadU16(const uint8_t* p)
		{
			uint16_t v = 0;
			std::memcpy(&v, p, sizeof(v));
			return v;
		}

		using clock = std::chrono::steady_clock;

		double ElapsedMs(clock::time_point start)
		{
			return std::chrono::duration<double, std::milli>(clock::now() - start).count();
		}

	} // namespace

	SpiTransfer::SpiTransfer(const Config& config)
		: FullDuplexExchangeTransport(config)
	{
		// GPIO lines. The interface thread waits on these fds directly with poll(); there is no separate
		// monitor thread, so a pin edge wakes the interface thread in a single hop.
		m_dataAvailablePin = std::make_unique<GpioInputPin>(
			config.gpioChipDevice, config.dataAvailablePin, "sbc-dap-" + std::to_string(config.dataAvailablePin));
		m_transferReadyPin = std::make_unique<GpioInputPin>(
			config.gpioChipDevice, config.transferReadyPin, "sbc-trp-" + std::to_string(config.transferReadyPin));

		// Optional scope-trigger output line (low = idle)
		if (config.sbcDataAvailablePin >= 0)
		{
			m_sbcDataAvailablePin =
				std::make_unique<OutputGpioPin>(config.gpioChipDevice,
												config.sbcDataAvailablePin,
												"sbc-sbcdap-" + std::to_string(config.sbcDataAvailablePin),
												false);
		}

		// Open the SPI device
		m_spiDevice = std::make_unique<SpiDevice>(config.spiDevice, config.spiFrequency, config.spiTransferMode);
	}

	SpiTransfer::~SpiTransfer()
	{
		Stop();
	}

	// ---------------------------------------------------------------------------
	// One exchange attempt (the SPI half of SPI.cs PerformFullTransfer)
	// ---------------------------------------------------------------------------
	bool SpiTransfer::PerformExchange()
	{
		// Exchange transfer headers (also deals with transfer responses)
		if (!ExchangeHeader())
		{
			return false;
		}

		// Exchange data if there is anything to transfer
		return (m_rxHeader.dataLength == 0 && m_txPointer == 0) || ExchangeData();
	}

	void SpiTransfer::OnPrepareReconnect() noexcept
	{
		// Start the next handshake against a clean edge state (ignore errors while recovering)
		try
		{
			while (m_transferReadyPin->ReadEvent())
			{
			}
			m_consumedRisingEdgeSeq = m_transferReadyPin->RisingSequenceNumber();
			while (m_dataAvailablePin->ReadEvent())
			{
			}
		}
		catch (...)
		{
			// @intentional: this is the reconnect path draining stale GPIO edges. If the
			// drain itself fails there is nothing further to fall back to, and the caller is
			// already handling a lost connection.
		}

		if (m_sbcDataAvailablePin)
		{
			try
			{
				m_sbcDataAvailablePin->Write(false);
			}
			catch (...)
			{
				// @intentional: dropping the scope-trigger line is best-effort during
				// reconnect and must not mask the original failure.
			}
		}
	}

	void SpiTransfer::OnTransferCompleted() noexcept
	{
		// Transfer completed: drop the scope trigger low now that no data remains staged
		if (m_sbcDataAvailablePin)
		{
			try
			{
				m_sbcDataAvailablePin->Write(false);
			}
			catch (...)
			{
				// @intentional: the scope trigger is a debug aid; a failed write must not fail
				// the transfer that just completed.
			}
		}
	}

	// ---------------------------------------------------------------------------
	// Wait for the TfrRdy pin (SPI.cs WaitForTransfer)
	// ---------------------------------------------------------------------------
	void SpiTransfer::WaitForTransfer(bool inTransfer)
	{
		const bool needFreshEdge = inTransfer && !m_waitingForFirstTransfer;

		// Sub-exchanges require a rising edge newer than the one consumed for the previous exchange;
		// the header and the first transfer run against the steady "ready" high level.
		auto isReady = [&]() -> bool
		{
			if (needFreshEdge)
			{
				const auto diff =
					static_cast<int32_t>(m_transferReadyPin->RisingSequenceNumber() - m_consumedRisingEdgeSeq);
				if (diff <= 0)
				{
					return false;
				}
				// A fresh rising edge occurred; confirm the pin is still high (else it was a glitch)
				if (m_transferReadyPin->Read())
				{
					return true;
				}
				m_numTfrPinGlitches++;
				m_consumedRisingEdgeSeq = m_transferReadyPin->RisingSequenceNumber();
				return false;
			}
			return m_transferReadyPin->Read();
		};

		// Drain any queued edge events (keeps the rising-edge seqno current and lets poll() block properly)
		while (m_transferReadyPin->ReadEvent())
		{
		}

		if (!isReady())
		{
			// While IAP is running every wait uses IapTimeout: it erases flash between segments and can
			// leave TfrRdy low for far longer than any regular transfer timeout allows
			const int timeout = [&]() -> int
			{
				if (m_updating)
				{
					return proto::IapTimeout;
				}
				if (m_waitingForFirstTransfer)
				{
					return config.sbcConnectTimeout;
				}
				return inTransfer ? config.sbcTransferTimeout : config.sbcConnectionTimeout;
			}();

			const auto start = clock::now();
			// Only the TfrRdy edge fd and the stop fd matter here; RequestTransfer wakeups are irrelevant
			// during a transfer and are intentionally not watched, so they cannot perturb this wait.
			pollfd fds[2];
			fds[0] = {m_transferReadyPin->Fd(), POLLIN, 0};
			fds[1] = {m_stopEventFd, POLLIN, 0};

			do
			{
				const int timeToWait = timeout - static_cast<int>(ElapsedMs(start));
				if (timeToWait <= 0 || m_stop.load(std::memory_order_relaxed))
				{
					if (m_stop.load(std::memory_order_relaxed))
					{
						throw TransferTimeout("Transfer cancelled");
					}
					throw TransferTimeout(inTransfer ? "Transfer timeout while waiting for TfrRdy pin"
													 : "Connection timeout while waiting for TfrRdy pin");
				}

				// Block (0% CPU) until the TfrRdy pin toggles, a stop arrives, or the timeout elapses
				fds[0].revents = fds[1].revents = 0;
				const int ready = ::poll(fds, 2, timeToWait);
				if (ready < 0)
				{
					if (errno == EINTR)
					{
						continue;
					}
					throw std::system_error(errno, std::generic_category(), "poll() failed waiting for TfrRdy");
				}

				// Drain the edge events that woke us and re-evaluate readiness (stop is handled at the top)
				while (m_transferReadyPin->ReadEvent())
				{
				}
			} while (!isReady());

			if (inTransfer)
			{
				const double waited = ElapsedMs(start);
				m_maxPinWaitDuration = std::max(waited, m_maxPinWaitDuration);
			}
		}

		// Record the rising edge consumed for this exchange so the next sub-exchange waits for a newer one
		m_consumedRisingEdgeSeq = m_transferReadyPin->RisingSequenceNumber();
		m_waitingForFirstTransfer = false;
	}

	// ---------------------------------------------------------------------------
	// Header exchange (SPI.cs ExchangeHeader)
	// ---------------------------------------------------------------------------
	bool SpiTransfer::ExchangeHeader()
	{
		auto* txHdr = reinterpret_cast<uint8_t*>(&m_txHeader);
		auto* rxHdr = reinterpret_cast<uint8_t*>(&m_rxHeader);

		for (int retry = 0; retry < config.maxSbcRetries; retry++)
		{
			// Perform SPI header exchange
			WaitForTransfer(false);
			// The header grew when the step clock moved into it, so this follows the struct rather
			// than a number: clocking the old length truncates before crcHeader, and the far side
			// then fails its own checksum on a header it never finished receiving
			const size_t headerLen = (m_txHeader.protocolVersion >= 4)
				? sizeof(proto::SpiTransferHeader)
				: proto::LegacyTransferHeaderSize;
			m_spiDevice->TransferFullDuplex(txHdr, rxHdr, headerLen);

			// Check for a possible response code. BadResponse always means "abandon this transfer and start
			// over", and DuetCANMaster only ever sends it on its way back to a header exchange. Restart the
			// full transfer so that both sides line up on a header. Answering with a data response instead
			// would pit our 4-byte response against its full-length header and oscillate: it would truncate the
			// header, reply BadHeaderChecksum, re-arm the header, and we would repeat
			const uint32_t responseCode = ReadU32(rxHdr);
			if (responseCode == proto::TransferResponse::BadResponse)
			{
				return false;
			}

			// Verify the format code. The protocol is little-endian, so a response code clocked out in place
			// of a header lands in formatCode and numPackets. No response code carries a valid format code in
			// its low byte, so this rejects a stray response before any other header field is trusted - in
			// particular the protocol version below, which is read before the header checksum can be verified
			if (m_rxHeader.formatCode != proto::FormatCode && m_rxHeader.formatCode != proto::FormatCodeStandalone)
			{
				ExchangeResponse(proto::TransferResponse::BadResponse);
				return false;
			}

			// Change the protocol version if necessary
			// In update-only mode a newer-than-supported protocol version is adopted rather than refused,
			// so an incompatible firmware can still be flashed
			const uint16_t lastProtocolVersion = m_txHeader.protocolVersion;
			if (m_rxHeader.protocolVersion != lastProtocolVersion &&
				(m_rxHeader.protocolVersion <= proto::ProtocolVersion || config.updateOnly))
			{
				m_txHeader.protocolVersion = m_rxHeader.protocolVersion;
				WriteCrc();
				ExchangeResponse(proto::TransferResponse::BadResponse);
				continue;
			}

			// Verify header checksum
			if (m_rxHeader.protocolVersion >= 4)
			{
				const uint32_t computedCrc = Crc32(rxHdr, proto::SpiTransferHeaderCrcLength);
				if (m_rxHeader.crcHeader != computedCrc)
				{
					const uint32_t rc = ExchangeResponse(proto::TransferResponse::BadHeaderChecksum);
					if (rc == proto::TransferResponse::BadResponse)
					{
						// Both sides saw a bad header checksum: retry
						return false;
					}
					continue;
				}
			}
			else
			{
				const uint16_t computedCrc = Crc16(rxHdr, 10);
				if (ReadU16(rxHdr + 10) != computedCrc)
				{
					const uint32_t rc = ExchangeResponse(proto::TransferResponse::BadHeaderChecksum);
					if (rc == proto::TransferResponse::BadResponse)
					{
						return false;
					}
					continue;
				}
			}

			// Check format code. Any other value was already rejected before the checksum was verified
			if (m_rxHeader.formatCode == proto::FormatCodeStandalone)
			{
				throw TransferError("RepRapFirmware is operating in stand-alone mode");
			}

			// Check for changed protocol version. Update-only mode tolerates a newer firmware so that it
			// can be reflashed rather than refused outright
			if (m_rxHeader.protocolVersion > proto::ProtocolVersion && !config.updateOnly)
			{
				ExchangeResponse(proto::TransferResponse::BadProtocolVersion);
				throw TransferError("Invalid protocol version");
			}

			// Check the data length
			if (m_rxHeader.dataLength > bufferSize)
			{
				ExchangeResponse(proto::TransferResponse::BadDataLength);
				throw TransferError("Data too long");
			}

			// Acknowledge receipt
			const uint32_t response = ExchangeResponse(proto::TransferResponse::Success);
			switch (response)
			{
			case proto::TransferResponse::Success:
				return true;
			case proto::TransferResponse::BadFormat:
				throw TransferError("RepRapFirmware refused message format");
			case proto::TransferResponse::BadProtocolVersion:
				throw TransferError("RepRapFirmware refused protocol version");
			case proto::TransferResponse::BadDataLength:
				throw TransferError("RepRapFirmware refused data length");
			case proto::TransferResponse::BadHeaderChecksum:
				continue;
			case proto::TransferResponse::BadResponse:
				return false;
			default:
				// Always announce the restart with BadResponse rather than quietly completing a transfer
				// whose header response the controller answered with something unexpected. DuetCANMaster
				// does the same from RestartTransfer(true), so both sides restart from a header exchange
				ExchangeResponse(proto::TransferResponse::BadResponse);
				return false;
			}
		}

		return false;
	}

	// ---------------------------------------------------------------------------
	// Response exchange (SPI.cs ExchangeResponse)
	// ---------------------------------------------------------------------------
	uint32_t SpiTransfer::ExchangeResponse(uint32_t response)
	{
		uint32_t tx = response;
		uint32_t rx = 0;
		WaitForTransfer();
		m_spiDevice->TransferFullDuplex(
			reinterpret_cast<const uint8_t*>(&tx), reinterpret_cast<uint8_t*>(&rx), sizeof(uint32_t));
		return rx;
	}

	// ---------------------------------------------------------------------------
	// Data exchange (SPI.cs ExchangeData)
	// ---------------------------------------------------------------------------
	bool SpiTransfer::ExchangeData()
	{
		const size_t bytesToTransfer = std::max<size_t>(m_rxHeader.dataLength, m_txPointer);
		for (int retry = 0; retry < config.maxSbcRetries; retry++)
		{
			WaitForTransfer();
			m_spiDevice->TransferFullDuplex(CurrentTxBuffer().data(), m_rxBuffer.data(), bytesToTransfer);

			// Check for a possible response code
			const uint32_t responseCode = ReadU32(m_rxBuffer.data());
			if (responseCode == proto::TransferResponse::BadResponse)
			{
				return false;
			}

			// Inspect received data
			if (m_rxHeader.protocolVersion >= 4)
			{
				const uint32_t computedCrc = Crc32(m_rxBuffer.data(), m_rxHeader.dataLength);
				if (computedCrc != m_rxHeader.crcData)
				{
					const uint32_t rc = ExchangeResponse(proto::TransferResponse::BadDataChecksum);
					if (rc == proto::TransferResponse::BadDataChecksum)
					{
						// Both sides saw a bad data checksum: retry
					}
					else
					{
						if (rc != proto::TransferResponse::BadResponse)
						{
							ExchangeResponse(proto::TransferResponse::BadResponse);
						}
						return false;
					}
					continue;
				}
			}
			else
			{
				const uint16_t computedCrc = Crc16(m_rxBuffer.data(), m_rxHeader.dataLength);
				const uint16_t expected = ReadU16(reinterpret_cast<uint8_t*>(&m_rxHeader) + 8);
				if (computedCrc != expected)
				{
					const uint32_t rc = ExchangeResponse(proto::TransferResponse::BadDataChecksum);
					if (rc == proto::TransferResponse::BadResponse)
					{
						return false;
					}
					continue;
				}
			}

			// Exchange data response and restart if it failed
			bool success = false;
			if (ExchangeDataResponse(success))
			{
				return success;
			}
		}
		throw TransferError("SPI connection reset because the number of maximum retries has been exceeded");
	}

	// ---------------------------------------------------------------------------
	// Data response exchange (SPI.cs ExchangeDataResponse)
	// ---------------------------------------------------------------------------
	bool SpiTransfer::ExchangeDataResponse(bool& success)
	{
		const uint32_t responseCode = ExchangeResponse(proto::TransferResponse::Success);
		switch (responseCode)
		{
		case proto::TransferResponse::Success:
			success = true;
			return true;
		case proto::TransferResponse::BadDataChecksum:
			success = false;
			return false;
		case proto::TransferResponse::BadResponse:
			success = false;
			return true;
		default:
			// Anything else means the two sides are no longer in step - typically DuetCANMaster has
			// already completed this transfer and moved on to a header, so it will never answer another
			// data response. Retrying here would oscillate against it forever. Send BadResponse, which
			// makes it restart from a header exchange too, and restart the full transfer
			ExchangeResponse(proto::TransferResponse::BadResponse);
			success = false;
			return true;
		}
	}

	// ---------------------------------------------------------------------------
	// Transfer gating (SPI.cs WaitForTransferReason / RequestTransfer)
	// ---------------------------------------------------------------------------
	bool SpiTransfer::WaitForTransferReason()
	{
		// Only gate during normal operation; while connecting, reconnecting, resetting or updating the
		// protocol must always be free to make progress
		if (!m_connected || m_hadTimeout || m_waitingForFirstTransfer || m_updating || m_resetting)
		{
			return true;
		}

		// Start straight away if we have data staged for transmission
		if (m_txPointer != 0)
		{
			return true;
		}

		// Race-free DataAvailable check: drain queued edge events first, then read the authoritative level.
		// Draining stale events also stops them from making poll() return immediately below. A rising edge
		// that arrives after the drain is preserved (poll wakes on it) or is caught by the next Read().
		while (m_dataAvailablePin->ReadEvent())
		{
		}
		if (m_dataAvailablePin->Read())
		{
			return true;
		}

		// Keep-alive
		const int timeToWait = config.sbcConnectionKeepAliveInterval - static_cast<int>(ElapsedMs(m_keepAliveStart));
		if (timeToWait <= 0)
		{
			return true;
		}

		// Block (0% CPU) until a reason arrives: a wake-up from RequestTransfer/Stop, the DataAvailable
		// pin rising, or the keep-alive timeout.
		//
		// The wake eventfd is deliberately NOT drained before poll(). StageOutgoing() (which reads the
		// outgoing queue) has already run in the caller's loop; a RequestTransfer that races in *after*
		// that read but before/inside this poll() must still wake us, otherwise its message would sit in
		// the queue until the keep-alive fires (a spurious ~25 ms gap). Draining only after poll() keeps
		// the signal edge-safe. A leftover count from RequestTransfers served by data-driven transfers
		// merely causes one immediate, harmless extra wake-up before we settle.
		pollfd fds[3];
		fds[0] = {m_dataAvailablePin->Fd(), POLLIN, 0};
		fds[1] = {m_requestEventFd, POLLIN, 0};
		fds[2] = {m_stopEventFd, POLLIN, 0};
		const int ready = ::poll(fds, 3, timeToWait);
		if (ready < 0 && errno != EINTR)
		{
			throw std::system_error(errno, std::generic_category(), "poll() failed waiting for a transfer reason");
		}

		// Consume the wake-ups and any DataAvailable edges so the next poll() blocks properly
		uint64_t v = 0;
		while (::read(m_requestEventFd, &v, sizeof(v)) > 0)
		{
		}
		while (::read(m_stopEventFd, &v, sizeof(v)) > 0)
		{
		}
		while (m_dataAvailablePin->ReadEvent())
		{
		}

		// Proceed on stop so the caller can shut down; otherwise re-stage and retry (the next call
		// re-checks the DataAvailable level / staged data / keep-alive and starts a transfer if warranted)
		return m_stop.load(std::memory_order_relaxed);
	}

	void SpiTransfer::RequestTransfer()
	{
		// Raise the scope trigger: the SBC now has a reason (typically staged data) to transfer
		if (m_sbcDataAvailablePin)
		{
			m_sbcDataAvailablePin->Write(true);
		}
		FullDuplexExchangeTransport::RequestTransfer();
	}

	// ---------------------------------------------------------------------------
	// IAP / firmware update (SPI.cs FlashFirmwareSegment .. WaitForIapReset)
	// ---------------------------------------------------------------------------
	bool SpiTransfer::FlashFirmwareSegment(std::span<const uint8_t> segment)
	{
		if (segment.empty())
		{
			return false;
		}
		if (segment.size() > proto::FirmwareSegmentSize)
		{
			throw TransferError("Firmware segment too large");
		}

		uint8_t writeBuffer[proto::FirmwareSegmentSize];
		uint8_t readBuffer[proto::FirmwareSegmentSize];
		std::memcpy(writeBuffer, segment.data(), segment.size());
		if (segment.size() < proto::FirmwareSegmentSize)
		{
			// Fill the remaining space with 0xFF, as the IAP program does once complete
			std::memset(writeBuffer + segment.size(), 0xFF, proto::FirmwareSegmentSize - segment.size());
		}

		WaitForTransfer();
		m_spiDevice->TransferFullDuplex(writeBuffer, readBuffer, proto::FirmwareSegmentSize);
		return true;
	}

	bool SpiTransfer::VerifyFirmwareChecksum(uint32_t firmwareLength, uint16_t crc16)
	{
		// At this point IAP expects another segment, so wait for it to be ready first. After that give it
		// a moment to acknowledge that we are done before sending the verification request.
		WaitForTransfer();
		InterruptibleSleep(proto::FirmwareFinishedDelay);

		// Send the final firmware size plus CRC16 checksum to IAP
		proto::FlashVerify verifyRequest{};
		verifyRequest.firmwareLength = firmwareLength;
		verifyRequest.crc16 = crc16;
		verifyRequest.padding = 0;

		uint8_t transferData[sizeof(proto::FlashVerify)];
		std::memcpy(transferData, &verifyRequest, sizeof(verifyRequest));
		WaitForTransfer();
		m_spiDevice->TransferFullDuplex(transferData, transferData, sizeof(transferData));

		// Check whether IAP can confirm our CRC16 checksum
		uint8_t writeOk[1] = {0};
		WaitForTransfer();
		m_spiDevice->TransferFullDuplex(writeOk, writeOk, sizeof(writeOk));
		return writeOk[0] == proto::FlashVerifyOk;
	}

	void SpiTransfer::WaitForIapReset()
	{
		// Wait a moment for the firmware to start
		InterruptibleSleep(proto::IapRebootDelay);

		// Wait for the first data transfer from the newly-started firmware. Seeding the sequence numbers
		// this way makes the next exchange look like a fresh connection rather than a reset.
		m_updating = m_connected = false;
		m_waitingForFirstTransfer = true;
		m_rxHeader.sequenceNumber = 1;
		m_txHeader.sequenceNumber = 0;
	}

} // namespace Duet::Sbc
