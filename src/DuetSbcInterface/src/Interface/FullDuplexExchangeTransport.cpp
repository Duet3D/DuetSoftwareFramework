#include <Interface/FullDuplexExchangeTransport.h>

#include <Storage/Crc.h>

#include <poll.h>
#include <sys/eventfd.h>
#include <unistd.h>

#include <algorithm>
#include <cstring>
#include <string>
#include <system_error>

namespace Duet::Sbc
{

	namespace
	{

		inline void WriteU16(uint8_t* p, uint16_t v)
		{
			std::memcpy(p, &v, sizeof(v));
		}

		double ElapsedMs(std::chrono::steady_clock::time_point start)
		{
			return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
		}

	} // namespace

	FullDuplexExchangeTransport::FullDuplexExchangeTransport(const Config& config)
		: config(config)
		, bufferSize(config.bufferSize)
		, m_requestEventFd(::eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC))
		, m_stopEventFd(::eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC))
	{
		m_rxBuffer.assign(bufferSize, 0);
		m_txBuffers.resize(kNumTxBuffers);
		for (auto& buf : m_txBuffers)
		{
			buf.assign(bufferSize, 0);
		}

		// Initialize the TX header once (see Writer.InitTransferHeader)
		m_txHeader.formatCode = proto::FormatCode;
		m_txHeader.numPackets = 0;
		m_txHeader.protocolVersion = proto::ProtocolVersion;
		m_txHeader.sequenceNumber = 0;
		m_txHeader.dataLength = 0;
		m_txHeader.crcData = 0;
		m_txHeader.crcHeader = 0;

		if (m_requestEventFd < 0 || m_stopEventFd < 0)
		{
			throw std::system_error(errno, std::generic_category(), "Cannot create wake eventfd");
		}

		m_keepAliveStart = clock::now();
	}

	FullDuplexExchangeTransport::~FullDuplexExchangeTransport()
	{
		Stop();
		if (m_requestEventFd >= 0)
		{
			::close(m_requestEventFd);
			m_requestEventFd = -1;
		}
		if (m_stopEventFd >= 0)
		{
			::close(m_stopEventFd);
			m_stopEventFd = -1;
		}
	}

	void FullDuplexExchangeTransport::Stop() noexcept
	{
		m_stop.store(true, std::memory_order_relaxed);
		// Wake the interface thread if it is blocked in poll() (anywhere)
		if (m_stopEventFd >= 0)
		{
			const uint64_t one = 1;
			[[maybe_unused]] const ssize_t n = ::write(m_stopEventFd, &one, sizeof(one));
		}
	}

	void FullDuplexExchangeTransport::ThrowIfStopped()
	{
		if (m_stop.load(std::memory_order_relaxed))
		{
			throw TransferTimeout("Transfer cancelled");
		}
	}

	void FullDuplexExchangeTransport::Connect()
	{
		PerformFullTransfer(true);
	}

	bool FullDuplexExchangeTransport::HadReset() const noexcept
	{
		return m_connected && (static_cast<uint16_t>(m_lastTransferNumber + 1) != m_rxHeader.sequenceNumber);
	}

	void FullDuplexExchangeTransport::RequestTransfer()
	{
		// Wake the interface thread if it is blocked in WaitForTransferReason's poll()
		if (m_requestEventFd >= 0)
		{
			const uint64_t one = 1;
			[[maybe_unused]] const ssize_t n = ::write(m_requestEventFd, &one, sizeof(one));
		}
	}

	// ---------------------------------------------------------------------------
	// Full transfer (SPI.cs PerformFullTransfer)
	// ---------------------------------------------------------------------------
	void FullDuplexExchangeTransport::PerformFullTransfer(bool connecting)
	{
		m_packetsBeingResent.clear();
		m_lastTransferNumber = m_rxHeader.sequenceNumber;

		// Reset RX transfer header
		m_rxHeader.formatCode = proto::InvalidFormatCode;
		m_rxHeader.numPackets = 0;
		m_rxHeader.protocolVersion = 0;
		m_rxHeader.dataLength = 0;
		m_rxHeader.crcData = 0;
		m_rxHeader.crcHeader = 0;

		// Set up TX transfer header
		m_txHeader.numPackets = m_packetId;
		m_txHeader.sequenceNumber++;
		m_txHeader.dataLength = static_cast<uint16_t>(m_txPointer);
		WriteCrc();

		int retry = 0;
		while (!m_stop.load(std::memory_order_relaxed))
		{
			try
			{
				if (retry > config.maxSbcRetries)
				{
					throw TransferTimeout("Maximum number of transfer retries exceeded");
				}

				// Track the maximum time between regular full transfers
				if (!connecting && !m_waitingForFirstTransfer && m_connected && !m_hadTimeout && !m_updating &&
					!m_resetting)
				{
					if (m_fullTransferTimerRunning)
					{
						const double elapsed = ElapsedMs(m_fullTransferStart);
						m_maxFullTransferDelay = std::max(elapsed, m_maxFullTransferDelay);
						m_fullTransferTimerRunning = false;
					}
					else
					{
						m_fullTransferStart = clock::now();
						m_fullTransferTimerRunning = true;
					}
				}

				// Exchange headers, data and the response codes acknowledging them
				if (!PerformExchange())
				{
					retry++;
					continue;
				}

				// Record the protocol version
				m_protocolVersion = m_rxHeader.protocolVersion;

				// Deal with timeouts and the first transmission
				if (m_hadTimeout)
				{
					m_hadTimeout = m_resetting = false;
				}
				else if (!m_connected)
				{
					m_lastTransferNumber = static_cast<uint16_t>(m_rxHeader.sequenceNumber - 1);
				}
				m_connected = true;

				// Transfer OK
				m_maxRxSize = std::max(m_maxRxSize, m_rxHeader.dataLength);
				m_maxTxSize = std::max(m_maxTxSize, m_txHeader.dataLength);
				m_txBufferIndex = (m_txBufferIndex + 1) % kNumTxBuffers;
				m_rxPointer = m_txPointer = 0;
				m_packetId = 0;
				m_keepAliveStart = clock::now();
				m_consecutiveErrors = 0;

				OnTransferCompleted();
				return;
			}
			catch (const TransferTimeout&)
			{
				// Timeout / cancellation. On stop or during the initial connect, unwind to the caller.
				if (connecting || m_stop.load(std::memory_order_relaxed))
				{
					throw;
				}
				if (!m_hadTimeout && m_connected && m_logCallback)
				{
					m_logCallback("Lost connection to controller (timeout); reconnecting");
				}
				// The readiness wait already paced this, so just resync and keep retrying
				PrepareReconnect("Transfer timeout");
				retry = 0;
			}
			catch (const std::exception& e)
			{
				// Any other transfer error (bad format/checksum/protocol, I/O error, ...). During
				// the initial connect surface it; otherwise recover automatically rather than
				// terminating.
				if (connecting || m_stop.load(std::memory_order_relaxed))
				{
					throw;
				}
				m_numResyncs++;
				m_consecutiveErrors++;
				if (m_logCallback)
				{
					m_logCallback("Transfer error, recovering (resync #" + std::to_string(m_numResyncs) +
								  "): " + e.what());
				}
				PrepareReconnect(e.what());
				retry = 0;
				// Pace fast-failing errors (e.g. a persistent protocol mismatch) so recovery does not
				// spin the CPU; the backoff grows with consecutive failures up to a 1 s cap
				InterruptibleSleep(std::min(m_consecutiveErrors * 50, 1000));
			}
		}

		// Stop requested
		throw TransferTimeout("Transfer cancelled");
	}

	// Put the link back into the "reconnecting" state so the next transfer re-runs the handshake, and
	// abandon whatever was staged for the transfer that did not happen.
	void FullDuplexExchangeTransport::PrepareReconnect(const char* reason)
	{
		m_txHeader.protocolVersion = proto::ProtocolVersion;
		m_waitingForFirstTransfer = true;
		const bool justDropped = !m_hadTimeout && m_connected;
		if (justDropped)
		{
			m_hadTimeout = true;
		}
		m_connected = false;

		// Whatever was staged for the transfer that did not happen is abandoned. A controller that
		// rebooted has no state to receive it, and one that merely stalled is about to be configured
		// again by the reconnect, so replaying it means sending yesterday's machine to today's board.
		// The header is emptied along with the buffer: PerformFullTransfer describes the staged
		// transfer once, before its retry loop, so a header left describing the abandoned bytes
		// would be offered by every retry with no data behind it and be rejected by all of them
		m_txPointer = 0;
		m_packetId = 0;
		m_txHeader.dataLength = 0;
		m_txHeader.numPackets = 0;
		m_packetsBeingResent.clear();

		// Report it from here, where it is observed. PerformFullTransfer does not return until the link
		// is back, so a caller watching its result cannot learn that the link went away at all
		if (justDropped && m_connectionLostCallback)
		{
			m_connectionLostCallback(reason != nullptr ? reason : "Transfer timeout");
		}
		m_resetting = false;

		// The header CRC may be stale after a partial/failed exchange or a protocol-version change
		WriteCrc();

		OnPrepareReconnect();
	}

	void FullDuplexExchangeTransport::ResetConnection()
	{
		// Abandon any partially-staged transfer and force a clean handshake on the next call
		m_txPointer = 0;
		m_rxPointer = 0;
		m_packetId = 0;
		m_packetsBeingResent.clear();
		m_numResyncs++;
		PrepareReconnect("Connection reset");
	}

	void FullDuplexExchangeTransport::InterruptibleSleep(int ms)
	{
		if (ms <= 0 || m_stop.load(std::memory_order_relaxed))
		{
			return;
		}
		// Sleep on the stop fd so Stop() cuts the backoff short
		pollfd pfd{m_stopEventFd, POLLIN, 0};
		::poll(&pfd, 1, ms);
	}

	// ---------------------------------------------------------------------------
	// Checksums (SPI.cs WriteCRC)
	// ---------------------------------------------------------------------------
	void FullDuplexExchangeTransport::WriteCrc()
	{
		auto* hdr = reinterpret_cast<uint8_t*>(&m_txHeader);
		const uint8_t* txData = CurrentTxBuffer().data();
		if (m_txHeader.protocolVersion >= 4)
		{
			m_txHeader.crcData = Crc32(txData, m_txPointer);
			m_txHeader.crcHeader = Crc32(hdr, proto::SpiTransferHeaderCrcLength);
		}
		else
		{
			// The pre-version-4 layout, whose CRC16 pair sat at offsets 8 and 10. Those offsets
			// describe that layout rather than this struct, so they stay written out; a peer that old
			// cannot pair with this build anyway, because the header length is negotiated first
			const uint16_t data16 = Crc16(txData, m_txPointer);
			WriteU16(hdr + 8, data16);
			const uint16_t header16 = Crc16(hdr, 10);
			WriteU16(hdr + 10, header16);
		}
	}

	// ---------------------------------------------------------------------------
	// Reading incoming packets (SPI.cs ReadNextPacket)
	// ---------------------------------------------------------------------------
	bool FullDuplexExchangeTransport::ReadNextPacket(proto::PacketHeader& packet)
	{
		if (m_rxPointer >= m_rxHeader.dataLength)
		{
			return false;
		}

		// The packet header and the length it declares both come off the wire, so both are checked
		// against the data block that was actually received before anything is read through them.
		// This is the one place every packet passes through, so bounding it here means the readers
		// above only have to check their own struct sizes rather than repeat this for each request
		if (m_rxPointer + sizeof(proto::PacketHeader) > m_rxHeader.dataLength)
		{
			if (m_logCallback)
			{
				m_logCallback("Discarded a transfer whose data block ends inside a packet header");
			}
			m_rxPointer = m_rxHeader.dataLength;
			return false;
		}

		proto::PacketHeader header{};
		std::memcpy(&header, m_rxBuffer.data() + m_rxPointer, sizeof(header));

		const size_t payloadStart = m_rxPointer + sizeof(header);
		if (payloadStart + proto::AddPadding(header.length) > m_rxHeader.dataLength)
		{
			if (m_logCallback)
			{
				m_logCallback("Discarded a packet claiming " + std::to_string(header.length) +
							  " bytes that the transfer's data block does not carry");
			}
			m_rxPointer = m_rxHeader.dataLength;
			return false;
		}

		m_lastPacket = header;
		m_packetData = m_rxBuffer.data() + payloadStart;
		m_packetDataLength = header.length;
		m_rxPointer = payloadStart + proto::AddPadding(header.length);

		packet = m_lastPacket;
		return true;
	}

	// ---------------------------------------------------------------------------
	// Writing outgoing packets (SPI.cs Write* helpers)
	// ---------------------------------------------------------------------------
	bool FullDuplexExchangeTransport::CanWritePacket(size_t dataLength) const noexcept
	{
		return m_txPointer + sizeof(proto::PacketHeader) + dataLength <= bufferSize;
	}

	void FullDuplexExchangeTransport::WritePacketHeader(proto::SbcRequest request, size_t dataLength)
	{
		proto::PacketHeader header{};
		header.request = static_cast<uint16_t>(request);
		header.id = m_packetId++;
		header.length = static_cast<uint16_t>(dataLength);
		header.resendPacketId = 0;
		std::memcpy(CurrentTxBuffer().data() + m_txPointer, &header, sizeof(header));
		m_txPointer += sizeof(header);
	}

	uint8_t* FullDuplexExchangeTransport::GetWriteBuffer(size_t dataLength)
	{
		const size_t padded = proto::AddPadding(dataLength);
		uint8_t* result = CurrentTxBuffer().data() + m_txPointer;
		// Zero any padding bytes
		if (padded > dataLength)
		{
			std::memset(result + dataLength, 0, padded - dataLength);
		}
		m_txPointer += padded;
		return result;
	}

	bool FullDuplexExchangeTransport::WriteEmergencyStop()
	{
		if (!CanWritePacket())
		{
			return false;
		}
		WritePacketHeader(proto::SbcRequest::EmergencyStop);
		return true;
	}

	bool FullDuplexExchangeTransport::WriteReset()
	{
		if (!CanWritePacket())
		{
			return false;
		}
		m_txPointer = 0;
		m_resetting = true;
		WritePacketHeader(proto::SbcRequest::Reset);
		return true;
	}

	bool FullDuplexExchangeTransport::WriteEnableCan(bool enable)
	{
		if (!CanWritePacket(sizeof(proto::EnableCanHeader)))
		{
			return false;
		}
		WritePacketHeader(proto::SbcRequest::EnableCAN, sizeof(proto::EnableCanHeader));
		proto::EnableCanHeader header{};
		header.channel = 0;
		header.enable = enable ? 1 : 0;
		std::memcpy(GetWriteBuffer(sizeof(header)), &header, sizeof(header));
		return true;
	}

	bool FullDuplexExchangeTransport::WriteCanMessage(uint16_t txToken,
									   uint16_t msgType,
									   uint16_t replyType,
									   uint8_t dstAddress,
									   bool isResponse,
									   std::span<const uint8_t> payload)
	{
		if (payload.size() > 64)
		{
			throw TransferError("CAN message payload must be between 0 and 64 bytes");
		}
		const size_t dataLength = sizeof(proto::SendCanMessageHeader) + payload.size();
		if (!CanWritePacket(proto::AddPadding(dataLength)))
		{
			return false;
		}

		WritePacketHeader(proto::SbcRequest::SendCANMessage, proto::AddPadding(dataLength));

		proto::SendCanMessageHeader header{};
		header.txToken = txToken;
		header.msgType = msgType;
		header.replyType = replyType;
		header.dataLength = static_cast<uint8_t>(payload.size());
		header.dstAddress = dstAddress;
		header.flags = isResponse ? 0x01 : 0x00;

		uint8_t* dst = GetWriteBuffer(dataLength);
		std::memcpy(dst, &header, sizeof(header));
		if (!payload.empty())
		{
			std::memcpy(dst + sizeof(header), payload.data(), payload.size());
		}
		return true;
	}

	bool FullDuplexExchangeTransport::WriteScheduleMove(std::span<const uint8_t> packet)
	{
		if (packet.size() < sizeof(proto::ScheduleMoveHeader))
		{
			throw TransferError("ScheduleMove packet is shorter than its header");
		}
		if (!CanWritePacket(proto::AddPadding(packet.size())))
		{
			return false;
		}

		WritePacketHeader(proto::SbcRequest::ScheduleMove, proto::AddPadding(packet.size()));

		// Copied verbatim: the motion engine built this in the controller's layout precisely so that
		// nothing between it and the CAN send has to understand a move.
		uint8_t* dst = GetWriteBuffer(packet.size());
		std::memcpy(dst, packet.data(), packet.size());
		return true;
	}

	bool FullDuplexExchangeTransport::WriteMessage(uint32_t messageFlags, std::string_view message)
	{
		// Don't send a new request if another one is still pending
		if (std::ranges::find(m_packetsBeingResent, proto::SbcRequest::Message) != m_packetsBeingResent.end())
		{
			return false;
		}

		const size_t dataLength = sizeof(proto::MessageHeader) + message.size();
		if (!CanWritePacket(proto::AddPadding(dataLength)))
		{
			return false;
		}

		WritePacketHeader(proto::SbcRequest::Message, proto::AddPadding(dataLength));

		proto::MessageHeader header{};
		header.messageType = messageFlags;
		header.length = static_cast<uint16_t>(message.size());
		header.padding = 0;

		uint8_t* dst = GetWriteBuffer(dataLength);
		std::memcpy(dst, &header, sizeof(header));
		if (!message.empty())
		{
			// The frame's header.length carries the size; null-terminating it would corrupt the wire
			// format, and the suggested strcpy would be wrong here.
			// NOLINTNEXTLINE(bugprone-not-null-terminated-result) - this is a length-prefixed binary
			std::memcpy(dst + sizeof(header), message.data(), message.size());
		}
		return true;
	}

	// ---------------------------------------------------------------------------
	// Resend a packet (SPI.cs ResendPacket)
	// ---------------------------------------------------------------------------
	void FullDuplexExchangeTransport::ResendPacket(const proto::PacketHeader& packet, proto::SbcRequest& sbcRequestOut)
	{
		// The packet to resend lives in the previously-used TX buffer
		const int prevIndex = (m_txBufferIndex + 1) % kNumTxBuffers;
		const uint8_t* buffer = m_txBuffers[prevIndex].data();
		const size_t headerSize = sizeof(proto::PacketHeader);
		size_t offset = 0;

		for (;;)
		{
			proto::PacketHeader header{};
			std::memcpy(&header, buffer + offset, headerSize);
			if (header.id == packet.resendPacketId)
			{
				sbcRequestOut = static_cast<proto::SbcRequest>(header.request);
				WritePacketHeader(sbcRequestOut, header.length);
				std::memcpy(GetWriteBuffer(header.length), buffer + offset + headerSize, header.length);

				if (std::ranges::find(m_packetsBeingResent, sbcRequestOut) == m_packetsBeingResent.end())
				{
					m_packetsBeingResent.push_back(sbcRequestOut);
				}
				return;
			}

			offset += headerSize + proto::AddPadding(header.length);
			if (header.id >= packet.resendPacketId || offset >= bufferSize)
			{
				break;
			}
		}

		throw TransferError("Firmware requested resend for invalid packet");
	}

	// ---------------------------------------------------------------------------
	// IAP staging (SPI.cs WriteIapSegment / StartIap)
	// ---------------------------------------------------------------------------
	bool FullDuplexExchangeTransport::WriteIapSegment(std::span<const uint8_t> segment)
	{
		if (segment.empty())
		{
			return false;
		}
		if (segment.size() > proto::IapSegmentSize)
		{
			throw TransferError("IAP segment too large");
		}

		WritePacketHeader(proto::SbcRequest::WriteIap, segment.size());
		std::memcpy(GetWriteBuffer(segment.size()), segment.data(), segment.size());
		PerformFullTransfer();
		return true;
	}

	void FullDuplexExchangeTransport::StartIap()
	{
		// Tell the firmware to boot the IAP program
		WritePacketHeader(proto::SbcRequest::StartIap);
		PerformFullTransfer();

		// From here on the controller runs IAP, which speaks a much simpler protocol: bare transfers
		// gated by readiness only. It signals once it is ready to receive the firmware.
		m_waitingForFirstTransfer = m_updating = true;
	}

} // namespace Duet::Sbc
