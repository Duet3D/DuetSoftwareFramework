// The emulator link: the SBC's SPI bus and input lines carried over a socket to an emulated
// controller (Hardware/Emulator). The peer here plays the emulator's side of EmulatorLinkFormats.h,
// as the Renode SbcPortBridge does, so these check the frames and the edge bookkeeping SpiTransfer
// relies on without an emulator.

#include <Hardware/Emulator/EmulatorLink.h>

#include <TestSupport.h>

#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <chrono>
#include <cstring>
#include <string>
#include <system_error>
#include <thread>
#include <vector>

using namespace Duet::Sbc::Emulator;

namespace
{
	constexpr int connectTimeoutMs = 2000;
	constexpr int exchangeTimeoutMs = 1000;

	std::string SocketPath(const char* name)
	{
		return "/tmp/duet-emulator-link-" + std::to_string(::getpid()) + "-" + name + ".sock";
	}

	// The emulator's end of the link
	class FakeEmulator
	{
	  public:
		explicit FakeEmulator(const std::string& path)
			: m_path(path)
		{
			::unlink(path.c_str());
			m_listenFd = ::socket(AF_UNIX, SOCK_STREAM, 0);
			sockaddr_un addr{};
			addr.sun_family = AF_UNIX;
			std::memcpy(addr.sun_path, path.c_str(), path.size() + 1);
			if (::bind(m_listenFd, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) != 0 ||
				::listen(m_listenFd, 1) != 0)
			{
				throw std::system_error(errno, std::generic_category(), "cannot listen");
			}
		}

		~FakeEmulator()
		{
			Close();
			::close(m_listenFd);
			::unlink(m_path.c_str());
		}

		FakeEmulator(const FakeEmulator&) = delete;
		FakeEmulator& operator=(const FakeEmulator&) = delete;
		FakeEmulator(FakeEmulator&&) = delete;
		FakeEmulator& operator=(FakeEmulator&&) = delete;

		void Accept() { m_fd = ::accept(m_listenFd, nullptr, nullptr); }

		void Close()
		{
			if (m_fd >= 0)
			{
				::close(m_fd);
				m_fd = -1;
			}
		}

		void SendLine(Line line, bool level)
		{
			const FrameHeader header{static_cast<uint8_t>(FrameType::LineLevel), {}, sizeof(LineLevelPayload)};
			const LineLevelPayload payload{static_cast<uint8_t>(line), static_cast<uint8_t>(level ? 1 : 0)};
			Send(&header, sizeof(header));
			Send(&payload, sizeof(payload));
		}

		// Answer one exchange with the complement of each byte received, and return what was received
		std::vector<uint8_t> AnswerExchange()
		{
			FrameHeader header{};
			Receive(&header, sizeof(header));
			std::vector<uint8_t> data(header.length);
			Receive(data.data(), data.size());
			std::vector<uint8_t> reply(data);
			for (uint8_t& b : reply)
			{
				b = static_cast<uint8_t>(~b);
			}
			const FrameHeader replyHeader{static_cast<uint8_t>(FrameType::ExchangeResult), {}, header.length};
			Send(&replyHeader, sizeof(replyHeader));
			Send(reply.data(), reply.size());
			return data;
		}

	  private:
		void Send(const void* data, size_t length) const { (void)::send(m_fd, data, length, MSG_NOSIGNAL); }

		void Receive(void* data, size_t length) const
		{
			auto* p = static_cast<uint8_t*>(data);
			while (length != 0)
			{
				const ssize_t n = ::recv(m_fd, p, length, 0);
				if (n <= 0)
				{
					throw std::runtime_error("fake emulator lost its connection");
				}
				p += n;
				length -= static_cast<size_t>(n);
			}
		}

		std::string m_path;
		int m_listenFd = -1;
		int m_fd = -1;
	};

	bool PollsReadable(int fd, int timeoutMs)
	{
		pollfd pfd{fd, POLLIN, 0};
		return ::poll(&pfd, 1, timeoutMs) == 1 && (pfd.revents & POLLIN) != 0;
	}

	void AnExchangeReturnsWhatTheSlaveClockedBack()
	{
		const std::string path = SocketPath("exchange");
		FakeEmulator emulator(path);
		std::thread accept([&] { emulator.Accept(); });
		auto link = std::make_shared<EmulatorLink>(path, connectTimeoutMs, exchangeTimeoutMs);
		accept.join();
		EmulatedSpiBus bus(link);

		const std::vector<uint8_t> tx{0x5F, 0x01, 0x02, 0xFE};
		std::vector<uint8_t> rx(tx.size());
		std::vector<uint8_t> received;
		std::thread peer([&] { received = emulator.AnswerExchange(); });
		bus.TransferFullDuplex(tx.data(), rx.data(), tx.size());
		peer.join();

		CHECK(received == tx, "the emulator receives the bytes the master clocked out");
		CHECK(rx == (std::vector<uint8_t>{0xA0, 0xFE, 0xFD, 0x01}), "the master receives the bytes the slave clocked back");
	}

	void LineChangesAreEdgesThatPollWaitsFor()
	{
		const std::string path = SocketPath("lines");
		FakeEmulator emulator(path);
		std::thread accept([&] { emulator.Accept(); });
		auto link = std::make_shared<EmulatorLink>(path, connectTimeoutMs, exchangeTimeoutMs);
		accept.join();
		EmulatedInputLine transferReady(link, Line::TransferReady);
		EmulatedInputLine dataAvailable(link, Line::DataAvailable);

		CHECK(!transferReady.Read(), "a line reads low until the emulator says otherwise");
		CHECK(!PollsReadable(transferReady.Fd(), 0), "no edge is queued before the emulator sends one");

		emulator.SendLine(Line::TransferReady, false);
		emulator.SendLine(Line::TransferReady, true);
		CHECK(PollsReadable(transferReady.Fd(), 1000), "a rising edge wakes poll()");
		CHECK(transferReady.Read(), "the line reads high after its rising edge");
		CHECK(transferReady.ReadEvent(), "the rising edge is queued");
		CHECK(!transferReady.ReadEvent(), "a repeated level is not an edge");
		const uint32_t first = transferReady.RisingSequenceNumber();
		CHECK(first != 0, "a consumed rising edge has a sequence number");
		CHECK(!dataAvailable.ReadEvent(), "one line's edges are not queued on the other");

		emulator.SendLine(Line::TransferReady, false);
		emulator.SendLine(Line::TransferReady, true);
		// The two edges arrive separately, so poll() may wake on the falling one alone
		int edgesConsumed = 0;
		while (edgesConsumed < 2 && PollsReadable(transferReady.Fd(), 1000))
		{
			while (transferReady.ReadEvent())
			{
				++edgesConsumed;
			}
		}
		CHECK(edgesConsumed == 2, "the next edges wake poll()");
		CHECK(transferReady.RisingSequenceNumber() > first, "a later rising edge has a larger sequence number");
		CHECK(!PollsReadable(transferReady.Fd(), 0), "consuming every edge leaves nothing for poll()");
		CHECK(transferReady.MissedEdges() == 0, "no edge was lost");
	}

	void AnExchangeFailsOnceTheEmulatorHasGone()
	{
		const std::string path = SocketPath("closed");
		FakeEmulator emulator(path);
		std::thread accept([&] { emulator.Accept(); });
		auto link = std::make_shared<EmulatorLink>(path, connectTimeoutMs, exchangeTimeoutMs);
		accept.join();
		EmulatedSpiBus bus(link);

		emulator.Close();
		const uint8_t tx[4] = {};
		uint8_t rx[4] = {};
		bool threw = false;
		try
		{
			bus.TransferFullDuplex(tx, rx, sizeof(tx));
		}
		catch (const std::system_error&)
		{
			threw = true;
		}
		CHECK(threw, "an exchange on a closed link throws, as a failed spidev transfer does");
	}

	void AnExchangeTheEmulatorDoesNotAnswerTimesOut()
	{
		const std::string path = SocketPath("silent");
		FakeEmulator emulator(path);
		std::thread accept([&] { emulator.Accept(); });
		auto link = std::make_shared<EmulatorLink>(path, connectTimeoutMs, 100);
		accept.join();
		EmulatedSpiBus bus(link);

		const uint8_t tx[4] = {};
		uint8_t rx[4] = {};
		bool threw = false;
		try
		{
			bus.TransferFullDuplex(tx, rx, sizeof(tx));
		}
		catch (const std::system_error& e)
		{
			threw = e.code().value() == ETIMEDOUT;
		}
		CHECK(threw, "an unanswered exchange times out");
	}

	void TheLinkWaitsForAnEmulatorThatIsStillStarting()
	{
		const std::string path = SocketPath("late");
		::unlink(path.c_str());
		std::unique_ptr<FakeEmulator> emulator;
		std::thread late(
			[&]
			{
				std::this_thread::sleep_for(std::chrono::milliseconds(200));
				emulator = std::make_unique<FakeEmulator>(path);
				emulator->Accept();
			});
		bool connected = false;
		try
		{
			EmulatorLink link(path, connectTimeoutMs, exchangeTimeoutMs);
			connected = true;
		}
		catch (const std::system_error&)
		{
		}
		late.join();
		CHECK(connected, "the link connects once the emulator listens, within the connect timeout");

		bool threw = false;
		try
		{
			EmulatorLink link(SocketPath("absent"), 100, exchangeTimeoutMs);
		}
		catch (const std::system_error&)
		{
			threw = true;
		}
		CHECK(threw, "the link gives up on an emulator that never listens");
	}

} // namespace

int main()
{
	AnExchangeReturnsWhatTheSlaveClockedBack();
	LineChangesAreEdgesThatPollWaitsFor();
	AnExchangeFailsOnceTheEmulatorHasGone();
	AnExchangeTheEmulatorDoesNotAnswerTimesOut();
	TheLinkWaitsForAnEmulatorThatIsStillStarting();
	return TestSupport::Summarise("emulator link");
}
