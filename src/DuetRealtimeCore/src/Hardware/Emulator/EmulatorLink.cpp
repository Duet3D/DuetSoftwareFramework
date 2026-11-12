#include "EmulatorLink.h"

#include <sys/eventfd.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <cerrno>
#include <chrono>
#include <cstring>
#include <system_error>

namespace Duet::Sbc::Emulator
{
	namespace
	{
		using clock = std::chrono::steady_clock;

		// Pause between attempts to connect to an emulator that is not listening yet
		constexpr auto connectRetryInterval = std::chrono::milliseconds(20);

		// Read exactly `length` bytes. False on end of stream or a socket error.
		bool ReadExact(int fd, void* data, size_t length) noexcept
		{
			auto* p = static_cast<uint8_t*>(data);
			while (length != 0)
			{
				const ssize_t n = ::recv(fd, p, length, 0);
				if (n < 0 && errno == EINTR)
				{
					continue;
				}
				if (n <= 0)
				{
					return false;
				}
				p += n;
				length -= static_cast<size_t>(n);
			}
			return true;
		}

		int Connect(const std::string& socketPath, int timeoutMs)
		{
			sockaddr_un addr{};
			addr.sun_family = AF_UNIX;
			if (socketPath.empty() || socketPath.size() >= sizeof(addr.sun_path))
			{
				throw std::system_error(EINVAL, std::generic_category(), "Invalid emulator socket path '" + socketPath + "'");
			}
			std::memcpy(addr.sun_path, socketPath.c_str(), socketPath.size() + 1);

			const auto deadline = clock::now() + std::chrono::milliseconds(timeoutMs);
			while (true)
			{
				const int fd = ::socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
				if (fd < 0)
				{
					throw std::system_error(errno, std::generic_category(), "Cannot create emulator socket");
				}
				if (::connect(fd, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) == 0)
				{
					return fd;
				}
				const int error = errno;
				::close(fd);
				if ((error != ENOENT && error != ECONNREFUSED) || clock::now() >= deadline)
				{
					throw std::system_error(error, std::generic_category(), "Cannot connect to emulator at '" + socketPath + "'");
				}
				std::this_thread::sleep_for(connectRetryInterval);
			}
		}
	} // namespace

	EmulatorLink::EmulatorLink(const std::string& socketPath, int connectTimeoutMs, int exchangeTimeoutMs)
		: m_exchangeTimeoutMs(exchangeTimeoutMs)
	{
		for (LineState& line : m_lines)
		{
			line.eventFd = ::eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK | EFD_SEMAPHORE);
			if (line.eventFd < 0)
			{
				const int error = errno;
				for (LineState& opened : m_lines)
				{
					if (opened.eventFd >= 0)
					{
						::close(opened.eventFd);
					}
				}
				throw std::system_error(error, std::generic_category(), "Cannot create emulator line eventfd");
			}
		}

		try
		{
			m_socketFd = Connect(socketPath, connectTimeoutMs);
		}
		catch (...)
		{
			for (LineState& line : m_lines)
			{
				::close(line.eventFd);
			}
			throw;
		}
		m_reader = std::thread([this] { ReaderLoop(); });
	}

	EmulatorLink::~EmulatorLink()
	{
		// Unblocks the reader, which then sees the end of the stream
		::shutdown(m_socketFd, SHUT_RDWR);
		if (m_reader.joinable())
		{
			m_reader.join();
		}
		::close(m_socketFd);
		for (LineState& line : m_lines)
		{
			::close(line.eventFd);
		}
	}

	void EmulatorLink::SendAll(const void* data, size_t length)
	{
		const auto* p = static_cast<const uint8_t*>(data);
		while (length != 0)
		{
			const ssize_t n = ::send(m_socketFd, p, length, MSG_NOSIGNAL);
			if (n < 0 && errno == EINTR)
			{
				continue;
			}
			if (n <= 0)
			{
				throw std::system_error(errno, std::generic_category(), "Emulator link write failed");
			}
			p += n;
			length -= static_cast<size_t>(n);
		}
	}

	void EmulatorLink::Exchange(const uint8_t* tx, uint8_t* rx, size_t length)
	{
		if (length > MaxExchangeLength)
		{
			throw std::system_error(EMSGSIZE, std::generic_category(), "SPI exchange too long for the emulator link");
		}

		const std::lock_guard exchangeLock(m_exchangeMutex);
		{
			const std::lock_guard lock(m_resultMutex);
			if (m_failed)
			{
				throw std::system_error(ENOTCONN, std::generic_category(), "Emulator link failed: " + m_failure);
			}
			m_haveResult = false;
		}

		const FrameHeader header{static_cast<uint8_t>(FrameType::Exchange), {}, static_cast<uint32_t>(length)};
		SendAll(&header, sizeof(header));
		SendAll(tx, length);

		std::unique_lock lock(m_resultMutex);
		if (!m_resultReady.wait_for(lock,
									std::chrono::milliseconds(m_exchangeTimeoutMs),
									[this] { return m_haveResult || m_failed; }))
		{
			throw std::system_error(ETIMEDOUT, std::generic_category(), "Emulator did not complete an SPI exchange");
		}
		if (!m_haveResult)
		{
			throw std::system_error(ENOTCONN, std::generic_category(), "Emulator link failed: " + m_failure);
		}
		if (m_result.size() != length)
		{
			throw std::system_error(EPROTO, std::generic_category(), "Emulator returned an SPI exchange of the wrong length");
		}
		std::memcpy(rx, m_result.data(), length);
		m_haveResult = false;
	}

	void EmulatorLink::ReaderLoop() noexcept
	{
		std::vector<uint8_t> payload;
		while (true)
		{
			FrameHeader header{};
			if (!ReadExact(m_socketFd, &header, sizeof(header)))
			{
				Fail("connection closed");
				return;
			}
			if (header.length > MaxExchangeLength)
			{
				Fail("oversized frame");
				return;
			}
			payload.resize(header.length);
			if (!ReadExact(m_socketFd, payload.data(), payload.size()))
			{
				Fail("connection closed");
				return;
			}

			switch (static_cast<FrameType>(header.type))
			{
			case FrameType::ExchangeResult:
			{
				const std::lock_guard lock(m_resultMutex);
				m_result.swap(payload);
				m_haveResult = true;
				m_resultReady.notify_all();
				break;
			}

			case FrameType::LineLevel:
			{
				LineLevelPayload level{};
				if (payload.size() != sizeof(level))
				{
					Fail("malformed line frame");
					return;
				}
				std::memcpy(&level, payload.data(), sizeof(level));
				if (level.line >= NumLines)
				{
					Fail("frame for an unknown line");
					return;
				}
				OnLineLevel(static_cast<Line>(level.line), level.level != 0);
				break;
			}

			default:
				Fail("unknown frame type");
				return;
			}
		}
	}

	void EmulatorLink::OnLineLevel(Line line, bool level)
	{
		LineState& state = m_lines[static_cast<size_t>(line)];
		const std::lock_guard lock(state.mutex);
		if (state.level.load() == level)
		{
			return;
		}
		state.level.store(level);
		++state.sequenceNumber;
		if (state.edges.size() >= s_maxQueuedEdges)
		{
			state.missedEdges.fetch_add(1);
			return;
		}
		state.edges.push_back({level, state.sequenceNumber});
		const uint64_t one = 1;
		(void)::write(state.eventFd, &one, sizeof(one));
	}

	void EmulatorLink::Fail(const char* reason) noexcept
	{
		const std::lock_guard lock(m_resultMutex);
		m_failed = true;
		m_failure = reason;
		m_resultReady.notify_all();
	}

	int EmulatorLink::LineFd(Line line) const noexcept
	{
		return m_lines[static_cast<size_t>(line)].eventFd;
	}

	bool EmulatorLink::LineLevel(Line line) const noexcept
	{
		return m_lines[static_cast<size_t>(line)].level.load();
	}

	bool EmulatorLink::TakeEdge(Line line, Edge& edge)
	{
		LineState& state = m_lines[static_cast<size_t>(line)];
		uint64_t count = 0;
		if (::read(state.eventFd, &count, sizeof(count)) != static_cast<ssize_t>(sizeof(count)))
		{
			return false;
		}
		const std::lock_guard lock(state.mutex);
		edge = state.edges.front();
		state.edges.pop_front();
		return true;
	}

	int EmulatorLink::MissedEdges(Line line) const noexcept
	{
		return m_lines[static_cast<size_t>(line)].missedEdges.load();
	}

	void EmulatedSpiBus::TransferFullDuplex(const uint8_t* tx, const uint8_t* rx, size_t length) const
	{
		// rx is const in the interface because spidev takes both buffers as addresses; it is written
		m_link->Exchange(tx, const_cast<uint8_t*>(rx), length);
	}

	bool EmulatedInputLine::ReadEvent()
	{
		EmulatorLink::Edge edge{};
		if (!m_link->TakeEdge(m_line, edge))
		{
			return false;
		}
		if (edge.rising)
		{
			m_lastRisingSeqno = edge.sequenceNumber;
		}
		return true;
	}

} // namespace Duet::Sbc::Emulator
