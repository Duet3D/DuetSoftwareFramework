#include <Interface/Socket/SocketTransport.h>

#include <Platform/RingBuffer.h>
#include <Storage/Crc.h>

#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <cstring>
#include <system_error>

namespace Duet::Sbc
{

	namespace
	{

		using clock = std::chrono::steady_clock;

		double ElapsedMs(clock::time_point start)
		{
			return std::chrono::duration<double, std::milli>(clock::now() - start).count();
		}

	} // namespace

	SocketTransport::SocketTransport(const Config& config)
		: FullDuplexExchangeTransport(config)
	{
		if (config.socketPath.empty())
		{
			throw std::invalid_argument("Socket transport selected but no socket path configured");
		}
		sockaddr_un addr{};
		if (config.socketPath.size() >= sizeof(addr.sun_path))
		{
			throw std::invalid_argument("Socket path is too long for a Unix domain socket");
		}
	}

	SocketTransport::~SocketTransport()
	{
		Stop();
		CloseSocket();
	}

	void SocketTransport::CloseSocket() noexcept
	{
		if (m_socketFd >= 0)
		{
			::close(m_socketFd);
			m_socketFd = -1;
		}
		m_ready = false;
		m_dataAvailable = false;
	}

	void SocketTransport::EnsureConnected()
	{
		if (m_socketFd >= 0)
		{
			return;
		}
		ThrowIfStopped();

		const int fd = ::socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
		if (fd < 0)
		{
			throw std::system_error(errno, std::generic_category(), "Cannot create link socket");
		}

		sockaddr_un addr{};
		addr.sun_family = AF_UNIX;
		std::memcpy(addr.sun_path, config.socketPath.c_str(), config.socketPath.size() + 1);

		if (::connect(fd, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) != 0)
		{
			if (errno == EINPROGRESS)
			{
				// Wait for the connect to resolve, up to the connect timeout
				pollfd fds[2];
				fds[0] = {fd, POLLOUT, 0};
				fds[1] = {m_stopEventFd, POLLIN, 0};
				const int ready = ::poll(fds, 2, config.sbcConnectTimeout);
				int soError = 0;
				socklen_t soLen = sizeof(soError);
				if (ready <= 0 || m_stop.load(std::memory_order_relaxed) ||
					::getsockopt(fd, SOL_SOCKET, SO_ERROR, &soError, &soLen) != 0 || soError != 0)
				{
					::close(fd);
					throw TransferTimeout("Timed out connecting to controller socket " + config.socketPath);
				}
			}
			else
			{
				const int connectErrno = errno;
				::close(fd);
				// Pace the retry: a peer that is not listening refuses instantly, and the recovery
				// loop would otherwise spin on it. The connect timeout is the natural cadence - it is
				// what a silent peer would have cost.
				InterruptibleSleep(config.sbcConnectTimeout);
				throw TransferTimeout("Cannot connect to controller socket " + config.socketPath + ": " +
									  std::strerror(connectErrno));
			}
		}

		m_socketFd = fd;
		m_ready = false;
		m_dataAvailable = false;
	}

	// ---------------------------------------------------------------------------
	// Exact-count socket I/O
	// ---------------------------------------------------------------------------
	void SocketTransport::ReadExact(std::span<uint8_t> buffer, int timeoutMs)
	{
		const auto start = clock::now();
		size_t done = 0;
		while (done < buffer.size())
		{
			ThrowIfStopped();
			const int timeToWait = timeoutMs - static_cast<int>(ElapsedMs(start));
			if (timeToWait <= 0)
			{
				throw TransferTimeout("Timed out waiting for data from the controller socket");
			}

			pollfd fds[2];
			fds[0] = {m_socketFd, POLLIN, 0};
			fds[1] = {m_stopEventFd, POLLIN, 0};
			const int ready = ::poll(fds, 2, timeToWait);
			if (ready < 0)
			{
				if (errno == EINTR)
				{
					continue;
				}
				CloseSocket();
				throw TransferTimeout("poll() failed on the controller socket");
			}
			if ((fds[0].revents & POLLIN) == 0)
			{
				continue;
			}

			const ssize_t n = ::recv(m_socketFd, buffer.data() + done, buffer.size() - done, 0);
			if (n > 0)
			{
				done += static_cast<size_t>(n);
			}
			else if (n == 0)
			{
				CloseSocket();
				throw TransferTimeout("Connection to controller socket closed by peer");
			}
			else if (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)
			{
				CloseSocket();
				throw TransferTimeout(std::string("Controller socket read failed: ") + std::strerror(errno));
			}
		}
	}

	void SocketTransport::WriteAll(std::span<const uint8_t> data)
	{
		const auto start = clock::now();
		size_t done = 0;
		while (done < data.size())
		{
			ThrowIfStopped();
			const int timeToWait = config.sbcTransferTimeout - static_cast<int>(ElapsedMs(start));
			if (timeToWait <= 0)
			{
				CloseSocket();
				throw TransferTimeout("Timed out writing to the controller socket");
			}

			pollfd fds[2];
			fds[0] = {m_socketFd, POLLOUT, 0};
			fds[1] = {m_stopEventFd, POLLIN, 0};
			const int ready = ::poll(fds, 2, timeToWait);
			if (ready < 0)
			{
				if (errno == EINTR)
				{
					continue;
				}
				CloseSocket();
				throw TransferTimeout("poll() failed on the controller socket");
			}
			if ((fds[0].revents & POLLOUT) == 0 && (fds[0].revents & (POLLERR | POLLHUP)) == 0)
			{
				continue;
			}

			const ssize_t n = ::send(m_socketFd, data.data() + done, data.size() - done, MSG_NOSIGNAL);
			if (n > 0)
			{
				done += static_cast<size_t>(n);
			}
			else if (n < 0 && errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)
			{
				CloseSocket();
				throw TransferTimeout(std::string("Controller socket write failed: ") + std::strerror(errno));
			}
		}
	}

	void SocketTransport::SendFrame(proto::SocketFrameType type,
									std::span<const uint8_t> payload,
									std::span<const uint8_t> tail)
	{
		proto::SocketFrameHeader header{};
		header.type = static_cast<uint8_t>(type);
		header.length = static_cast<uint32_t>(payload.size() + tail.size());
		WriteAll(AsBytes(header));
		if (!payload.empty())
		{
			WriteAll(payload);
		}
		if (!tail.empty())
		{
			WriteAll(tail);
		}
	}

	proto::SocketFrameHeader SocketTransport::ReadContentFrameHeader(int timeoutMs)
	{
		const auto start = clock::now();
		for (;;)
		{
			const int timeToWait = timeoutMs - static_cast<int>(ElapsedMs(start));
			if (timeToWait <= 0)
			{
				throw TransferTimeout("Timed out waiting for a frame from the controller socket");
			}

			proto::SocketFrameHeader header{};
			ReadExact({reinterpret_cast<uint8_t*>(&header), sizeof(header)}, timeToWait);
			if (header.length > proto::MaxSocketFramePayload)
			{
				throw TransferError("Controller sent an oversized frame");
			}

			switch (static_cast<proto::SocketFrameType>(header.type))
			{
			case proto::SocketFrameType::Ready:
				m_ready = true;
				continue;
			case proto::SocketFrameType::DataAvailable:
				m_dataAvailable = true;
				continue;
			default:
				return header;
			}
		}
	}

	int SocketTransport::ReadinessTimeout() const noexcept
	{
		if (m_updating)
		{
			return proto::IapTimeout;
		}
		if (m_waitingForFirstTransfer)
		{
			return config.sbcConnectTimeout;
		}
		return config.sbcConnectionTimeout;
	}

	void SocketTransport::WaitForReady()
	{
		if (!m_ready)
		{
			const int timeout = ReadinessTimeout();
			const auto start = clock::now();
			while (!m_ready)
			{
				const int timeToWait = timeout - static_cast<int>(ElapsedMs(start));
				if (timeToWait <= 0)
				{
					throw TransferTimeout("Transfer timeout while waiting for controller readiness");
				}

				proto::SocketFrameHeader header{};
				ReadExact({reinterpret_cast<uint8_t*>(&header), sizeof(header)}, timeToWait);
				switch (static_cast<proto::SocketFrameType>(header.type))
				{
				case proto::SocketFrameType::Ready:
					m_ready = true;
					break;
				case proto::SocketFrameType::DataAvailable:
					m_dataAvailable = true;
					break;
				default:
					// Any content frame here means the two sides are out of step
					throw TransferError("Controller sent an unexpected frame while idle");
				}
			}

			const double waited = ElapsedMs(start);
			m_maxPinWaitDuration = std::max(waited, m_maxPinWaitDuration);
		}

		// Spend the readiness on this exchange. The data the peer announced is collected by it too;
		// a prompt that arrives later sets the flag anew.
		m_ready = false;
		m_dataAvailable = false;
		m_waitingForFirstTransfer = false;
	}

	uint32_t SocketTransport::ReadResponseCode(int timeoutMs)
	{
		const proto::SocketFrameHeader header = ReadContentFrameHeader(timeoutMs);
		if (static_cast<proto::SocketFrameType>(header.type) != proto::SocketFrameType::Response ||
			header.length != sizeof(uint32_t))
		{
			throw TransferError("Controller answered the exchange with something other than a response code");
		}
		uint32_t code = 0;
		ReadExact({reinterpret_cast<uint8_t*>(&code), sizeof(code)}, timeoutMs);
		return code;
	}

	// ---------------------------------------------------------------------------
	// One exchange attempt
	// ---------------------------------------------------------------------------
	bool SocketTransport::PerformExchange()
	{
		EnsureConnected();
		WaitForReady();

		// One frame out: our header and staged data, verbatim. The CRCs were written by the caller.
		SendFrame(proto::SocketFrameType::Transfer, AsBytes(m_txHeader), {CurrentTxBuffer().data(), m_txPointer});

		// One frame in: the peer always answers a Transfer with its own Transfer, and reports what it
		// thought of ours in the response stage. A BadResponse in its place aborts the exchange.
		const proto::SocketFrameHeader frame = ReadContentFrameHeader(config.sbcTransferTimeout);
		if (static_cast<proto::SocketFrameType>(frame.type) == proto::SocketFrameType::Response)
		{
			if (frame.length != sizeof(uint32_t))
			{
				throw TransferError("Controller sent a malformed response frame");
			}
			uint32_t code = 0;
			ReadExact({reinterpret_cast<uint8_t*>(&code), sizeof(code)}, config.sbcTransferTimeout);
			if (code == proto::TransferResponse::BadResponse)
			{
				return false;
			}
			throw TransferError("Controller aborted the exchange with an unexpected response code");
		}
		if (static_cast<proto::SocketFrameType>(frame.type) != proto::SocketFrameType::Transfer ||
			frame.length < sizeof(proto::SpiTransferHeader))
		{
			throw TransferError("Controller sent a malformed transfer frame");
		}

		ReadExact({reinterpret_cast<uint8_t*>(&m_rxHeader), sizeof(m_rxHeader)}, config.sbcTransferTimeout);
		const size_t dataBytes = frame.length - sizeof(proto::SpiTransferHeader);
		const size_t stored = std::min(dataBytes, bufferSize);
		ReadExact({m_rxBuffer.data(), stored}, config.sbcTransferTimeout);
		for (size_t discarded = stored; discarded < dataBytes;)
		{
			// An overlong data block is drained so the stream stays in step; the response stage
			// answers it with BadDataLength
			uint8_t scratch[512];
			const size_t chunk = std::min(sizeof(scratch), dataBytes - discarded);
			ReadExact({scratch, chunk}, config.sbcTransferTimeout);
			discarded += chunk;
		}

		return ValidateAndRespond(static_cast<uint16_t>(std::min<size_t>(dataBytes, UINT16_MAX)));
	}

	bool SocketTransport::ValidateAndRespond(uint16_t receivedDataLength)
	{
		auto* rxHdr = reinterpret_cast<uint8_t*>(&m_rxHeader);

		// Work out our verdict on the received frame, exactly as the SPI header/data validation does
		uint32_t verdict = proto::TransferResponse::Success;

		if (m_rxHeader.formatCode != proto::FormatCode && m_rxHeader.formatCode != proto::FormatCodeStandalone)
		{
			verdict = proto::TransferResponse::BadResponse;
		}
		else if (m_rxHeader.protocolVersion != m_txHeader.protocolVersion &&
				 (m_rxHeader.protocolVersion <= proto::ProtocolVersion || config.updateOnly))
		{
			// Adopt the peer's protocol version and restart the exchange with it, as on SPI
			m_txHeader.protocolVersion = m_rxHeader.protocolVersion;
			WriteCrc();
			verdict = proto::TransferResponse::BadResponse;
		}
		else if (m_rxHeader.crcHeader != Crc32(rxHdr, proto::SpiTransferHeaderCrcLength))
		{
			verdict = proto::TransferResponse::BadHeaderChecksum;
		}
		else if (m_rxHeader.protocolVersion > proto::ProtocolVersion && !config.updateOnly)
		{
			verdict = proto::TransferResponse::BadProtocolVersion;
		}
		else if (m_rxHeader.dataLength > bufferSize)
		{
			verdict = proto::TransferResponse::BadDataLength;
		}
		else if (m_rxHeader.dataLength != receivedDataLength)
		{
			// The frame does not carry the data block its (validated) header claims. That is a
			// framing violation rather than a corrupt transfer, and no response code can fix it.
			throw TransferError("Controller transfer frame does not match its header's data length");
		}
		else if (m_rxHeader.crcData != Crc32(m_rxBuffer.data(), m_rxHeader.dataLength))
		{
			verdict = proto::TransferResponse::BadDataChecksum;
		}

		// Both sides send their verdict, then read the other's
		SendFrame(proto::SocketFrameType::Response, AsBytes(verdict));
		const uint32_t peerVerdict = ReadResponseCode(config.sbcTransferTimeout);

		// Our verdict decides first
		switch (verdict)
		{
		case proto::TransferResponse::Success:
			break;
		case proto::TransferResponse::BadResponse:
			// An invalid format code, or a renegotiated protocol version taking effect
			return false;
		case proto::TransferResponse::BadProtocolVersion:
			throw TransferError("Invalid protocol version");
		case proto::TransferResponse::BadDataLength:
			throw TransferError("Data too long");
		default:
			// A checksum verdict: retry the exchange with the same staged transfer
			return false;
		}

		if (m_rxHeader.formatCode == proto::FormatCodeStandalone)
		{
			throw TransferError("RepRapFirmware is operating in stand-alone mode");
		}

		// Then the peer's verdict on our frame
		switch (peerVerdict)
		{
		case proto::TransferResponse::Success:
			return true;
		case proto::TransferResponse::BadFormat:
			throw TransferError("Controller refused message format");
		case proto::TransferResponse::BadProtocolVersion:
			throw TransferError("Controller refused protocol version");
		case proto::TransferResponse::BadDataLength:
			throw TransferError("Controller refused data length");
		case proto::TransferResponse::BadHeaderChecksum:
		case proto::TransferResponse::BadDataChecksum:
		case proto::TransferResponse::BadResponse:
			// The peer wants the exchange again (or abandoned); it re-arms with Ready
			return false;
		default:
			throw TransferError("Controller answered the exchange with an unknown response code");
		}
	}

	void SocketTransport::OnPrepareReconnect() noexcept
	{
		// Whatever half-exchanged frames the stream still holds belong to the connection being
		// abandoned. Dropping the socket gives the next handshake a clean stream; the peer keeps
		// listening and its protocol state survives the reconnect, so a resync stays a resync
		// rather than looking like a controller reset.
		CloseSocket();
	}

	// ---------------------------------------------------------------------------
	// Transfer gating
	// ---------------------------------------------------------------------------
	bool SocketTransport::WaitForTransferReason()
	{
		// Only gate during normal operation; while connecting, reconnecting, resetting or updating
		// the protocol must always be free to make progress
		if (!m_connected || m_hadTimeout || m_waitingForFirstTransfer || m_updating || m_resetting)
		{
			return true;
		}

		// Start straight away if we have data staged for transmission, or the peer has said it does
		if (m_txPointer != 0 || m_dataAvailable)
		{
			return true;
		}

		// Keep-alive
		const int timeToWait =
			config.sbcConnectionKeepAliveInterval - static_cast<int>(ElapsedMs(m_keepAliveStart));
		if (timeToWait <= 0)
		{
			return true;
		}

		// Block (0% CPU) until a reason arrives: a wake-up from RequestTransfer/Stop, a frame from
		// the peer, or the keep-alive timeout. The wake eventfd is deliberately not drained before
		// poll(), for the same reason as the SPI transport: a RequestTransfer racing in after the
		// caller's StageOutgoing must still wake this poll.
		pollfd fds[3];
		fds[0] = {m_socketFd, POLLIN, 0};
		fds[1] = {m_requestEventFd, POLLIN, 0};
		fds[2] = {m_stopEventFd, POLLIN, 0};
		const int ready = ::poll(fds, 3, timeToWait);
		if (ready < 0 && errno != EINTR)
		{
			throw std::system_error(errno, std::generic_category(), "poll() failed waiting for a transfer reason");
		}

		// A readable socket while idle carries Ready or DataAvailable notifications; absorb them so
		// the flags are current for the re-check (and so the next poll() blocks properly)
		if (ready > 0 && (fds[0].revents & (POLLIN | POLLERR | POLLHUP)) != 0)
		{
			try
			{
				proto::SocketFrameHeader header{};
				ReadExact({reinterpret_cast<uint8_t*>(&header), sizeof(header)}, config.sbcTransferTimeout);
				switch (static_cast<proto::SocketFrameType>(header.type))
				{
				case proto::SocketFrameType::Ready:
					m_ready = true;
					break;
				case proto::SocketFrameType::DataAvailable:
					m_dataAvailable = true;
					break;
				default:
					throw TransferError("Controller sent an unexpected frame while idle");
				}
			}
			catch (const TransferTimeout&)
			{
				if (m_stop.load(std::memory_order_relaxed))
				{
					return true;
				}
				// The peer dropped the connection while the link was idle. On SPI an outage can only
				// be seen by a transfer failing, so the reconnect bookkeeping lives in the transfer
				// path; here it has to run as well, or the outage would go unreported and the
				// re-handshake would masquerade as a healthy link with a sequence jump.
				PrepareReconnect("Connection to controller socket lost");
				return true;
			}
		}

		// Consume the wake-ups so the next poll() blocks properly
		uint64_t v = 0;
		while (::read(m_requestEventFd, &v, sizeof(v)) > 0)
		{
		}
		while (::read(m_stopEventFd, &v, sizeof(v)) > 0)
		{
		}

		// Proceed on stop so the caller can shut down; otherwise re-stage and retry
		return m_stop.load(std::memory_order_relaxed);
	}

	// ---------------------------------------------------------------------------
	// IAP over the framed link
	// ---------------------------------------------------------------------------
	bool SocketTransport::FlashFirmwareSegment(std::span<const uint8_t> segment)
	{
		if (segment.empty())
		{
			return false;
		}
		if (segment.size() > proto::FirmwareSegmentSize)
		{
			throw TransferError("Firmware segment too large");
		}

		// Padded to a whole segment with 0xFF like the SPI transport, so the flashing side sees the
		// same bytes whichever link carried them
		uint8_t padded[proto::FirmwareSegmentSize];
		std::memcpy(padded, segment.data(), segment.size());
		if (segment.size() < proto::FirmwareSegmentSize)
		{
			std::memset(padded + segment.size(), 0xFF, proto::FirmwareSegmentSize - segment.size());
		}

		EnsureConnected();
		WaitForReady();
		SendFrame(proto::SocketFrameType::IapData, padded);
		return true;
	}

	bool SocketTransport::VerifyFirmwareChecksum(uint32_t firmwareLength, uint16_t crc16)
	{
		proto::FlashVerify verifyRequest{};
		verifyRequest.firmwareLength = firmwareLength;
		verifyRequest.crc16 = crc16;
		verifyRequest.padding = 0;

		EnsureConnected();
		WaitForReady();
		SendFrame(proto::SocketFrameType::IapVerify, AsBytes(verifyRequest));

		const proto::SocketFrameHeader header = ReadContentFrameHeader(proto::IapTimeout);
		if (static_cast<proto::SocketFrameType>(header.type) != proto::SocketFrameType::IapVerdict ||
			header.length != 1)
		{
			throw TransferError("Flasher answered the verification request with an unexpected frame");
		}
		uint8_t verdict = 0;
		ReadExact({&verdict, sizeof(verdict)}, proto::IapTimeout);
		return verdict == proto::FlashVerifyOk;
	}

	void SocketTransport::WaitForIapReset()
	{
		// Re-arm the handshake state so the next exchange looks like a fresh connection rather than
		// a reset. The virtual controller signals its own readiness, so there is no fixed reboot
		// delay to sit out here.
		m_updating = m_connected = false;
		m_waitingForFirstTransfer = true;
		m_rxHeader.sequenceNumber = 1;
		m_txHeader.sequenceNumber = 0;
	}

} // namespace Duet::Sbc
