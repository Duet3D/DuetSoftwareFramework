// The emulator link: the SBC's SPI bus and input lines carried over a socket to an emulated
// controller (Hardware/Emulator). The peer here plays the emulator's side of EmulatorLinkFormats.h,
// as the SBC link of the Renode SPI model does, and the test plays the bench that releases its
// frames, so these check the frames, the lockstep holding and the edge bookkeeping SpiTransfer
// relies on without an emulator.

#include <Hardware/Emulator/EmulatorLink.h>
#include <Platform/Activity.h>
#include <Platform/LocalClock.h>

#include <TestSupport.h>

#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <chrono>
#include <cstring>
#include <memory>
#include <string>
#include <system_error>
#include <thread>
#include <utility>
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

		// The emulated time the fake stamps its frames with
		uint64_t emulatedTimeNs = 0;

		void SendLine(Line line, bool level)
		{
			const FrameHeader header{static_cast<uint8_t>(FrameType::LineLevel), 0, 0, sizeof(LineLevelPayload), emulatedTimeNs};
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
			const FrameHeader replyHeader{static_cast<uint8_t>(FrameType::ExchangeResult), 0, header.sequence, header.length, emulatedTimeNs};
			Send(&replyHeader, sizeof(replyHeader));
			Send(reply.data(), reply.size());
			return data;
		}

		// Take one exchange without answering it
		std::pair<FrameHeader, std::vector<uint8_t>> ReceiveExchange()
		{
			FrameHeader header{};
			Receive(&header, sizeof(header));
			std::vector<uint8_t> data(header.length);
			Receive(data.data(), data.size());
			return {header, data};
		}

		void SendResult(uint16_t sequence, const std::vector<uint8_t>& data)
		{
			const FrameHeader header{static_cast<uint8_t>(FrameType::ExchangeResult), 0, sequence, static_cast<uint32_t>(data.size()), emulatedTimeNs};
			Send(&header, sizeof(header));
			Send(data.data(), data.size());
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

	// Wait, in real time, until the link has received `count` frames in all
	bool WaitForFrames(const EmulatorLink& link, uint32_t count)
	{
		const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(1);
		while (link.FramesReceived() < count)
		{
			if (std::chrono::steady_clock::now() >= deadline)
			{
				return false;
			}
			std::this_thread::sleep_for(std::chrono::milliseconds(1));
		}
		return true;
	}

	// Wait, in real time, until the link has sent `count` exchanges in all
	bool WaitForExchanges(const EmulatorLink& link, uint32_t count)
	{
		const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(1);
		while (link.FramesSent() < count)
		{
			if (std::chrono::steady_clock::now() >= deadline)
			{
				return false;
			}
			std::this_thread::sleep_for(std::chrono::milliseconds(1));
		}
		return true;
	}

	std::shared_ptr<EmulatorLink> Connect(FakeEmulator& emulator, const std::string& path, int timeoutMs = exchangeTimeoutMs)
	{
		std::thread accept([&] { emulator.Accept(); });
		auto link = std::make_shared<EmulatorLink>(path, connectTimeoutMs, timeoutMs);
		accept.join();
		return link;
	}

	void AnExchangeReturnsWhatTheSlaveClockedBack()
	{
		const std::string path = SocketPath("exchange");
		FakeEmulator emulator(path);
		auto link = Connect(emulator, path);
		EmulatedSpiBus bus(link);

		const std::vector<uint8_t> tx{0x5F, 0x01, 0x02, 0xFE};
		std::vector<uint8_t> rx(tx.size());
		std::vector<uint8_t> received;
		std::thread peer([&] { received = emulator.AnswerExchange(); });
		std::thread master([&] { bus.TransferFullDuplex(tx.data(), rx.data(), tx.size()); });
		peer.join();
		CHECK(WaitForFrames(*link, 1), "the result arrives");
		CHECK(link->ReleaseFrame(), "and is released");
		master.join();

		CHECK(link->FramesSent() == 1, "the exchange is counted as sent");
		CHECK(received == tx, "the emulator receives the bytes the master clocked out");
		CHECK(rx == (std::vector<uint8_t>{0xA0, 0xFE, 0xFD, 0x01}), "the master receives the bytes the slave clocked back");
	}

	void FramesAreHeldUntilReleasedInOrder()
	{
		const std::string path = SocketPath("held");
		FakeEmulator emulator(path);
		auto link = Connect(emulator, path);
		EmulatedInputLine transferReady(link, Line::TransferReady);

		int64_t next = 0;
		CHECK(!link->NextFrameTime(next), "nothing is held before the emulator sends");
		emulator.emulatedTimeNs = 3000000;
		emulator.SendLine(Line::TransferReady, true);
		emulator.emulatedTimeNs = 4000000;
		emulator.SendLine(Line::TransferReady, false);
		CHECK(WaitForFrames(*link, 2), "both frames arrive");
		CHECK(!transferReady.Read() && !PollsReadable(transferReady.Fd(), 0), "a frame that has arrived does nothing until it is released");
		CHECK(link->NextFrameTime(next) && next == 3000000, "the oldest frame's emulated time is reported");

		CHECK(link->ReleaseFrame(), "the first frame is released");
		CHECK(transferReady.Read() && PollsReadable(transferReady.Fd(), 0), "and the line rises");
		CHECK(link->NextFrameTime(next) && next == 4000000, "the next frame is the later one");
		CHECK(link->ReleaseFrame(), "the second frame is released");
		CHECK(!transferReady.Read(), "and the line falls");
		CHECK(!link->ReleaseFrame(), "nothing more is held");
		while (transferReady.ReadEvent())
		{
		}
	}

	void LineChangesAreEdgesThatPollWaitsFor()
	{
		const std::string path = SocketPath("lines");
		FakeEmulator emulator(path);
		auto link = Connect(emulator, path);
		EmulatedInputLine transferReady(link, Line::TransferReady);
		EmulatedInputLine dataAvailable(link, Line::DataAvailable);

		CHECK(!transferReady.Read(), "a line reads low until the emulator says otherwise");
		CHECK(!PollsReadable(transferReady.Fd(), 0), "no edge is queued before the emulator sends one");

		emulator.SendLine(Line::TransferReady, false);
		emulator.SendLine(Line::TransferReady, true);
		CHECK(WaitForFrames(*link, 2), "the frames arrive");
		while (link->ReleaseFrame())
		{
		}
		CHECK(PollsReadable(transferReady.Fd(), 0), "a rising edge wakes poll()");
		CHECK(transferReady.Read(), "the line reads high after its rising edge");
		CHECK(transferReady.ReadEvent(), "the rising edge is queued");
		CHECK(!transferReady.ReadEvent(), "a repeated level is not an edge");
		const uint32_t first = transferReady.RisingSequenceNumber();
		CHECK(first != 0, "a consumed rising edge has a sequence number");
		CHECK(!dataAvailable.ReadEvent(), "one line's edges are not queued on the other");

		emulator.SendLine(Line::TransferReady, false);
		emulator.SendLine(Line::TransferReady, true);
		CHECK(WaitForFrames(*link, 4), "the next frames arrive");
		while (link->ReleaseFrame())
		{
		}
		int edgesConsumed = 0;
		while (transferReady.ReadEvent())
		{
			++edgesConsumed;
		}
		CHECK(edgesConsumed == 2, "the next edges are queued");
		CHECK(transferReady.RisingSequenceNumber() > first, "a later rising edge has a larger sequence number");
		CHECK(!PollsReadable(transferReady.Fd(), 0), "consuming every edge leaves nothing for poll()");
		CHECK(transferReady.MissedEdges() == 0, "no edge was lost");
	}

	void AnEdgeCountsWhileAThreadWaitsOnItsLine()
	{
		const std::string path = SocketPath("activity");
		FakeEmulator emulator(path);
		auto link = Connect(emulator, path);
		EmulatedInputLine transferReady(link, Line::TransferReady);

		const int64_t before = Duet::Sbc::Activity::Pending();
		emulator.SendLine(Line::TransferReady, true);
		CHECK(WaitForFrames(*link, 1), "the frame arrives");
		{
			pollfd pfd{transferReady.Fd(), POLLIN, 0};
			const Duet::Sbc::Activity::WaitingOn waiting({&pfd, 1});
			CHECK(Duet::Sbc::Activity::Pending() == before, "a frame held for the bench is not activity");
			(void)link->ReleaseFrame();
			CHECK(Duet::Sbc::Activity::Pending() == before + 1, "a released edge is activity while a thread waits on its line");
		}
		CHECK(Duet::Sbc::Activity::Pending() == before, "and stops being activity when the thread stops waiting");
		CHECK(transferReady.ReadEvent(), "the edge is queued for it");
	}

	void AnExchangeFailsOnceTheEmulatorHasGone()
	{
		const std::string path = SocketPath("closed");
		FakeEmulator emulator(path);
		auto link = Connect(emulator, path);
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
		auto link = Connect(emulator, path, 100);
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

	void APinnedClockTimesAnExchangeOut()
	{
		const std::string path = SocketPath("pinned-timeout");
		FakeEmulator emulator(path);
		Duet::Sbc::LocalClock::Pin(1000000000);
		auto link = Connect(emulator, path, 100);
		EmulatedSpiBus bus(link);

		const uint8_t tx[4] = {};
		uint8_t rx[4] = {};
		bool timedOut = false;
		std::thread master(
			[&]
			{
				try
				{
					bus.TransferFullDuplex(tx, rx, sizeof(tx));
				}
				catch (const std::system_error& e)
				{
					timedOut = e.code().value() == ETIMEDOUT;
				}
			});
		CHECK(WaitForExchanges(*link, 1), "the exchange is sent");
		std::this_thread::sleep_for(std::chrono::milliseconds(200));
		CHECK(!timedOut, "real time passing does not time out an exchange on a pinned clock");
		Duet::Sbc::LocalClock::Pin(1000000000 + 99000000);
		std::this_thread::sleep_for(std::chrono::milliseconds(20));
		CHECK(!timedOut, "nor does the clock moving short of the timeout");
		Duet::Sbc::LocalClock::Pin(1000000000 + 100000000);
		master.join();
		CHECK(timedOut, "the clock reaching the timeout times it out");
		Duet::Sbc::LocalClock::Unpin();
	}

	void ALateResultIsNotTakenForTheNextExchange()
	{
		const std::string path = SocketPath("late-result");
		FakeEmulator emulator(path);
		auto link = Connect(emulator, path, 100);
		EmulatedSpiBus bus(link);

		const std::vector<uint8_t> tx{1, 2};
		std::vector<uint8_t> rx(tx.size());
		std::pair<FrameHeader, std::vector<uint8_t>> first;
		std::thread peer([&] { first = emulator.ReceiveExchange(); });
		bool timedOut = false;
		try
		{
			bus.TransferFullDuplex(tx.data(), rx.data(), tx.size());
		}
		catch (const std::system_error&)
		{
			timedOut = true;
		}
		peer.join();
		CHECK(timedOut, "the first exchange times out");

		// The first exchange's result arrives late, then the second's
		emulator.SendResult(first.first.sequence, {0xEE, 0xEE});
		std::thread master([&] { bus.TransferFullDuplex(tx.data(), rx.data(), tx.size()); });
		const auto second = emulator.ReceiveExchange();
		emulator.SendResult(second.first.sequence, {0x55, 0xAA});
		CHECK(WaitForFrames(*link, 2), "both results arrive");
		while (link->ReleaseFrame())
		{
		}
		master.join();
		CHECK(rx == (std::vector<uint8_t>{0x55, 0xAA}), "the second exchange gets its own result, not the late one");
	}

	void TheCurrentLinkIsTheOneRegistered()
	{
		const std::string path = SocketPath("current");
		FakeEmulator emulator(path);
		{
			auto link = Connect(emulator, path);
			EmulatorLink::SetCurrent(link);
			CHECK(EmulatorLink::Current() == link, "the registered link is current");
		}
		CHECK(EmulatorLink::Current() == nullptr, "a link that has gone is not current");
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
			// @intentional: CHECK below reports it
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
	FramesAreHeldUntilReleasedInOrder();
	LineChangesAreEdgesThatPollWaitsFor();
	AnEdgeCountsWhileAThreadWaitsOnItsLine();
	AnExchangeFailsOnceTheEmulatorHasGone();
	AnExchangeTheEmulatorDoesNotAnswerTimesOut();
	APinnedClockTimesAnExchangeOut();
	ALateResultIsNotTakenForTheNextExchange();
	TheCurrentLinkIsTheOneRegistered();
	TheLinkWaitsForAnEmulatorThatIsStillStarting();
	return TestSupport::Summarise("emulator link");
}
