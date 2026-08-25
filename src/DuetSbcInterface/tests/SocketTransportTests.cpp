// Loopback validation of SocketTransport against a minimal in-process controller peer speaking the
// framing of DuetSpiProtocol/SocketLinkFormats.h. The peer here is the executable specification of
// that framing: the C# fake endpoint in src/SystemTests implements the same behaviour.
//
// Covered: the empty keep-alive exchange, packets in both directions, protocol-version reporting,
// header/data CRC corruption answered with the checksum responses and a retry rather than a resync,
// withheld readiness timing the transfer out and the reconnect recovering, a reconnect that abandons
// staged data still recovering, a packet declaring more payload than the transfer carries being
// refused, and reset detection from a sequence-number discontinuity.
#include "TestSupport.h"

#include <Config/Configuration.h>
#include <DuetSpiProtocol/MessageFormats.h>
#include <DuetSpiProtocol/SocketLinkFormats.h>
#include <Interface/Socket/SocketTransport.h>
#include <Storage/Crc.h>

#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

using namespace Duet::Sbc;
namespace proto = duet::spi::protocol;

namespace
{

	// One packet as the peer captured or stages it
	struct PeerPacket
	{
		proto::PacketHeader header{};
		std::vector<uint8_t> data;
	};

	// The controller side of the framed exchange, scripted enough for these tests. Runs its loop on
	// its own thread: arm with Ready, exchange Transfer frames, exchange verdicts, repeat. State is
	// guarded by one mutex; the test synchronises on the exchange counter.
	class FakePeer
	{
	  public:
		explicit FakePeer(std::string path)
			: m_path(std::move(path))
		{
			::unlink(m_path.c_str());
			m_listenFd = ::socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
			CHECK(m_listenFd >= 0, "peer listen socket");
			sockaddr_un addr{};
			addr.sun_family = AF_UNIX;
			std::memcpy(addr.sun_path, m_path.c_str(), m_path.size() + 1);
			CHECK(::bind(m_listenFd, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) == 0, "peer bind");
			CHECK(::listen(m_listenFd, 1) == 0, "peer listen");
			m_thread = std::thread([this] { Run(); });
		}

		~FakePeer()
		{
			m_stop.store(true);
			m_armGate.notify_all();
			::shutdown(m_listenFd, SHUT_RDWR);
			if (m_connFd >= 0)
			{
				::shutdown(m_connFd, SHUT_RDWR);
			}
			m_thread.join();
			if (m_connFd >= 0)
			{
				::close(m_connFd);
			}
			::close(m_listenFd);
			::unlink(m_path.c_str());
		}

		// --- Scripting, called from the test thread ---

		void CorruptNextHeaderCrc() { WithLock([this] { m_corruptHeaderCrc = true; }); }
		void CorruptNextDataCrc() { WithLock([this] { m_corruptDataCrc = true; }); }
		void JumpSequenceBy(uint16_t jump) { WithLock([this, jump] { m_sequenceJump = jump; }); }

		// Declare a longer payload than the packet actually carries, so the transfer's data block
		// ends inside the packet it claims. A controller that did this by accident (or a corrupt
		// length that survived the CRC) must not be able to walk the receiver off its buffer
		void OverstateNextPacketLength(uint16_t extra) { WithLock([this, extra] { m_packetLengthInflation = extra; }); }

		// Withhold readiness: while paused, the peer arms no further exchange, so the SBC's next
		// transfer times out waiting for Ready. The pause takes effect from the exchange after the
		// one already armed.
		void PauseArming() { WithLock([this] { m_armingPaused = true; }); }
		void ResumeArming()
		{
			WithLock([this] { m_armingPaused = false; });
			m_armGate.notify_all();
		}

		void StagePacket(uint16_t request, const void* data, size_t length)
		{
			const std::lock_guard<std::mutex> lock(m_mutex);
			PeerPacket packet;
			packet.header.request = request;
			packet.header.id = m_nextPacketId++;
			packet.header.length = static_cast<uint16_t>(length);
			packet.data.assign(static_cast<const uint8_t*>(data), static_cast<const uint8_t*>(data) + length);
			m_stagedPackets.push_back(std::move(packet));
		}

		// --- Observation ---

		// Block until at least `count` exchanges have completed successfully
		void WaitForExchanges(unsigned int count)
		{
			std::unique_lock<std::mutex> lock(m_mutex);
			m_exchangeDone.wait(lock, [&] { return m_completedExchanges >= count; });
		}

		std::vector<PeerPacket> CapturedPackets()
		{
			const std::lock_guard<std::mutex> lock(m_mutex);
			return m_capturedPackets;
		}

		unsigned int CompletedExchanges()
		{
			const std::lock_guard<std::mutex> lock(m_mutex);
			return m_completedExchanges;
		}

		unsigned int Accepts()
		{
			const std::lock_guard<std::mutex> lock(m_mutex);
			return m_accepts;
		}

	  private:
		template <typename F> void WithLock(F&& f)
		{
			const std::lock_guard<std::mutex> lock(m_mutex);
			f();
		}

		bool ReadExact(uint8_t* buffer, size_t length)
		{
			size_t done = 0;
			while (done < length && !m_stop.load())
			{
				const ssize_t n = ::recv(m_connFd, buffer + done, length - done, 0);
				if (n <= 0)
				{
					return false;
				}
				done += static_cast<size_t>(n);
			}
			return done == length;
		}

		bool WriteAll(const uint8_t* buffer, size_t length)
		{
			size_t done = 0;
			while (done < length && !m_stop.load())
			{
				const ssize_t n = ::send(m_connFd, buffer + done, length - done, MSG_NOSIGNAL);
				if (n <= 0)
				{
					return false;
				}
				done += static_cast<size_t>(n);
			}
			return done == length;
		}

		bool SendFrame(proto::SocketFrameType type, const uint8_t* payload, size_t length)
		{
			proto::SocketFrameHeader header{};
			header.type = static_cast<uint8_t>(type);
			header.length = static_cast<uint32_t>(length);
			return WriteAll(reinterpret_cast<const uint8_t*>(&header), sizeof(header)) &&
				   (length == 0 || WriteAll(payload, length));
		}

		void Run()
		{
			while (!m_stop.load())
			{
				m_connFd = ::accept(m_listenFd, nullptr, nullptr);
				if (m_connFd < 0)
				{
					return;
				}
				{
					const std::lock_guard<std::mutex> lock(m_mutex);
					++m_accepts;
				}
				ServeConnection();
				::close(m_connFd);
				m_connFd = -1;
			}
		}

		void ServeConnection()
		{
			bool retrying = false;
			std::vector<uint8_t> txData;
			proto::SpiTransferHeader txHeader{};

			while (!m_stop.load())
			{
				// Arm the exchange - or, while a test withholds readiness, wait here without arming
				// and let the SBC time out and drop the connection
				{
					std::unique_lock<std::mutex> lock(m_mutex);
					m_armGate.wait(lock, [&] { return !m_armingPaused || m_stop.load(); });
				}
				if (m_stop.load())
				{
					return;
				}
				if (!SendFrame(proto::SocketFrameType::Ready, nullptr, 0))
				{
					return;
				}

				// The SBC's transfer frame
				proto::SocketFrameHeader frame{};
				if (!ReadExact(reinterpret_cast<uint8_t*>(&frame), sizeof(frame)))
				{
					return;
				}
				CHECK(frame.type == static_cast<uint8_t>(proto::SocketFrameType::Transfer), "peer expects a transfer");
				CHECK(frame.length >= sizeof(proto::SpiTransferHeader), "transfer frame carries a header");
				proto::SpiTransferHeader rxHeader{};
				if (!ReadExact(reinterpret_cast<uint8_t*>(&rxHeader), sizeof(rxHeader)))
				{
					return;
				}
				std::vector<uint8_t> rxData(frame.length - sizeof(rxHeader));
				if (!rxData.empty() && !ReadExact(rxData.data(), rxData.size()))
				{
					return;
				}

				// Validate what arrived
				uint32_t verdict = proto::TransferResponse::Success;
				if (rxHeader.formatCode != proto::FormatCode)
				{
					verdict = proto::TransferResponse::BadResponse;
				}
				else if (rxHeader.crcHeader !=
						 Crc32(reinterpret_cast<const uint8_t*>(&rxHeader), proto::SpiTransferHeaderCrcLength))
				{
					verdict = proto::TransferResponse::BadHeaderChecksum;
				}
				else if (rxHeader.dataLength != rxData.size() ||
						 rxHeader.crcData != Crc32(rxData.data(), rxData.size()))
				{
					verdict = proto::TransferResponse::BadDataChecksum;
				}

				// Build (or on a retry, re-send) this side's transfer
				if (!retrying)
				{
					const std::lock_guard<std::mutex> lock(m_mutex);
					txData.clear();
					for (const PeerPacket& packet : m_stagedPackets)
					{
						proto::PacketHeader header = packet.header;
						header.length = static_cast<uint16_t>(header.length + m_packetLengthInflation);
						m_packetLengthInflation = 0;

						const size_t headerOffset = txData.size();
						txData.resize(headerOffset + sizeof(proto::PacketHeader));
						std::memcpy(txData.data() + headerOffset, &header, sizeof(proto::PacketHeader));
						const size_t dataOffset = txData.size();
						txData.resize(dataOffset + proto::AddPadding(packet.data.size()), 0);
						std::memcpy(txData.data() + dataOffset, packet.data.data(), packet.data.size());
					}

					txHeader.formatCode = proto::FormatCode;
					txHeader.numPackets = static_cast<uint8_t>(m_stagedPackets.size());
					txHeader.protocolVersion = proto::ProtocolVersion;
					m_sequenceNumber = static_cast<uint16_t>(m_sequenceNumber + 1 + m_sequenceJump);
					m_sequenceJump = 0;
					txHeader.sequenceNumber = m_sequenceNumber;
					txHeader.dataLength = static_cast<uint16_t>(txData.size());
					txHeader.masterClock = m_masterClock;
					txHeader.hiccupTime = 0;
					txHeader.crcData = Crc32(txData.data(), txData.size());
					txHeader.crcHeader =
						Crc32(reinterpret_cast<const uint8_t*>(&txHeader), proto::SpiTransferHeaderCrcLength);
				}

				proto::SpiTransferHeader sentHeader = txHeader;
				{
					const std::lock_guard<std::mutex> lock(m_mutex);
					if (m_corruptHeaderCrc)
					{
						m_corruptHeaderCrc = false;
						sentHeader.crcHeader ^= 0xDEADBEEFU;
					}
					if (m_corruptDataCrc)
					{
						m_corruptDataCrc = false;
						sentHeader.crcData ^= 0xDEADBEEFU;
						sentHeader.crcHeader = Crc32(reinterpret_cast<const uint8_t*>(&sentHeader),
													 proto::SpiTransferHeaderCrcLength);
					}
				}

				proto::SocketFrameHeader txFrame{};
				txFrame.type = static_cast<uint8_t>(proto::SocketFrameType::Transfer);
				txFrame.length = static_cast<uint32_t>(sizeof(sentHeader) + txData.size());
				if (!WriteAll(reinterpret_cast<const uint8_t*>(&txFrame), sizeof(txFrame)) ||
					!WriteAll(reinterpret_cast<const uint8_t*>(&sentHeader), sizeof(sentHeader)) ||
					(!txData.empty() && !WriteAll(txData.data(), txData.size())))
				{
					return;
				}

				// Verdicts both ways
				if (!SendFrame(proto::SocketFrameType::Response,
							   reinterpret_cast<const uint8_t*>(&verdict),
							   sizeof(verdict)))
				{
					return;
				}
				proto::SocketFrameHeader responseFrame{};
				if (!ReadExact(reinterpret_cast<uint8_t*>(&responseFrame), sizeof(responseFrame)))
				{
					return;
				}
				CHECK(responseFrame.type == static_cast<uint8_t>(proto::SocketFrameType::Response),
					  "peer expects a response frame");
				uint32_t sbcVerdict = 0;
				if (!ReadExact(reinterpret_cast<uint8_t*>(&sbcVerdict), sizeof(sbcVerdict)))
				{
					return;
				}

				const bool completed =
					verdict == proto::TransferResponse::Success && sbcVerdict == proto::TransferResponse::Success;
				if (completed)
				{
					const std::lock_guard<std::mutex> lock(m_mutex);
					size_t offset = 0;
					while (offset + sizeof(proto::PacketHeader) <= rxHeader.dataLength)
					{
						PeerPacket packet;
						std::memcpy(&packet.header, rxData.data() + offset, sizeof(proto::PacketHeader));
						offset += sizeof(proto::PacketHeader);
						packet.data.assign(rxData.begin() + static_cast<long>(offset),
										   rxData.begin() + static_cast<long>(offset + packet.header.length));
						offset += proto::AddPadding(packet.header.length);
						m_capturedPackets.push_back(std::move(packet));
					}
					m_stagedPackets.clear();
					++m_completedExchanges;
					m_exchangeDone.notify_all();
				}
				retrying = !completed;
			}
		}

		const std::string m_path;
		int m_listenFd = -1;
		int m_connFd = -1;
		std::thread m_thread;
		std::atomic<bool> m_stop{false};

		std::mutex m_mutex;
		std::condition_variable m_exchangeDone;
		std::vector<PeerPacket> m_stagedPackets;
		std::vector<PeerPacket> m_capturedPackets;
		unsigned int m_completedExchanges = 0;
		unsigned int m_accepts = 0;
		uint16_t m_nextPacketId = 0;
		uint16_t m_sequenceNumber = 0;
		uint16_t m_sequenceJump = 0;
		uint32_t m_masterClock = 0;
		uint16_t m_packetLengthInflation = 0;
		bool m_corruptHeaderCrc = false;
		bool m_corruptDataCrc = false;
		bool m_armingPaused = false;
		std::condition_variable m_armGate;
	};

	Config MakeConfig(const std::string& path)
	{
		Config config;
		config.transport = TransportKind::Socket;
		config.socketPath = path;
		// Short timeouts keep the withheld-readiness test quick without tripping the healthy paths
		config.sbcConnectTimeout = 2000;
		config.sbcTransferTimeout = 2000;
		config.sbcConnectionTimeout = 500;
		return config;
	}

	std::string TestSocketPath()
	{
		return "/tmp/duet-sbc-socket-test-" + std::to_string(::getpid()) + ".sock";
	}

	void TestBasicExchangeAndPackets()
	{
		const std::string path = TestSocketPath();
		FakePeer peer(path);
		SocketTransport transport(MakeConfig(path));

		transport.Connect();
		CHECK(transport.IsConnected(), "connected after the first exchange");
		CHECK(transport.ProtocolVersion() == proto::ProtocolVersion, "protocol version reported");
		peer.WaitForExchanges(1);

		// A packet out: reaches the peer with its payload intact
		const std::string message = "hello controller";
		CHECK(transport.WriteMessage(0x12345678U, message), "message staged");
		transport.PerformFullTransfer();
		peer.WaitForExchanges(2);
		{
			const std::vector<PeerPacket> captured = peer.CapturedPackets();
			CHECK(captured.size() == 1, "peer captured one packet");
			if (captured.size() == 1)
			{
				CHECK(captured[0].header.request == static_cast<uint16_t>(proto::SbcRequest::Message),
					  "captured packet is a Message");
				proto::MessageHeader messageHeader{};
				std::memcpy(&messageHeader, captured[0].data.data(), sizeof(messageHeader));
				CHECK(messageHeader.messageType == 0x12345678U, "message flags survived");
				const std::string text(reinterpret_cast<const char*>(captured[0].data.data()) + sizeof(messageHeader),
									   messageHeader.length);
				CHECK(text == message, "message text survived");
			}
		}

		// A packet in: staged by the peer, decoded by the transport
		proto::CodeBufferUpdateHeader update{};
		update.bufferSpace = 4096;
		peer.StagePacket(static_cast<uint16_t>(proto::FirmwareRequest::CodeBufferUpdate), &update, sizeof(update));
		transport.PerformFullTransfer();
		CHECK(transport.PacketsToRead() == 1, "one packet to read");
		proto::PacketHeader packet{};
		CHECK(transport.ReadNextPacket(packet), "packet read");
		CHECK(packet.request == static_cast<uint16_t>(proto::FirmwareRequest::CodeBufferUpdate),
			  "packet is the staged CodeBufferUpdate");
		proto::CodeBufferUpdateHeader received{};
		std::memcpy(&received, transport.PacketData().data(), sizeof(received));
		CHECK(received.bufferSpace == 4096, "packet payload survived");

		CHECK(!transport.HadReset(), "no reset seen");
		CHECK(transport.ResyncCount() == 0, "no resyncs in the healthy path");
	}

	void TestCrcCorruptionIsRetriedNotResynced()
	{
		const std::string path = TestSocketPath();
		FakePeer peer(path);
		SocketTransport transport(MakeConfig(path));
		transport.Connect();
		peer.WaitForExchanges(1);

		int lostConnections = 0;
		transport.SetConnectionLostCallback([&](std::string_view) { ++lostConnections; });

		peer.CorruptNextHeaderCrc();
		transport.PerformFullTransfer();
		peer.WaitForExchanges(2);

		peer.CorruptNextDataCrc();
		// The data CRC only matters when there is data; stage a packet each way
		proto::CodeBufferUpdateHeader update{};
		peer.StagePacket(static_cast<uint16_t>(proto::FirmwareRequest::CodeBufferUpdate), &update, sizeof(update));
		transport.PerformFullTransfer();
		peer.WaitForExchanges(3);

		CHECK(lostConnections == 0, "a corrupt CRC does not drop the connection");
		CHECK(transport.ResyncCount() == 0, "a corrupt CRC is retried, not resynced");
		CHECK(transport.IsConnected(), "still connected after the retries");
		CHECK(peer.Accepts() == 1, "the socket connection survived the retries");
	}

	void TestWithheldReadinessTimesOutAndRecovers()
	{
		const std::string path = TestSocketPath();
		FakePeer peer(path);
		SocketTransport transport(MakeConfig(path));
		transport.Connect();
		peer.WaitForExchanges(1);

		std::atomic<int> lostConnections{0};
		transport.SetConnectionLostCallback([&](std::string_view) { ++lostConnections; });

		// The peer has already armed the next exchange, so the pause bites on the one after it
		peer.PauseArming();
		transport.PerformFullTransfer();
		peer.WaitForExchanges(2);

		// This transfer finds no readiness. PerformFullTransfer recovers internally, so it only
		// returns once the link is back; run it aside and watch the connection-lost report
		std::thread starved([&] { transport.PerformFullTransfer(); });
		for (int waited = 0; lostConnections.load() == 0 && waited < 10000; waited += 10)
		{
			std::this_thread::sleep_for(std::chrono::milliseconds(10));
		}
		CHECK(lostConnections.load() == 1, "withheld readiness reports the connection lost");

		peer.ResumeArming();
		starved.join();
		peer.WaitForExchanges(3);

		CHECK(transport.IsConnected(), "the transfer loop reconnected on its own");
		CHECK(peer.Accepts() >= 2, "recovery re-dialled the socket");
	}

	// A reconnect that happens while a transfer's worth of data is staged abandons that data, so the
	// TX header must stop describing it. If the header still claims the abandoned bytes, every
	// retried exchange offers a header and data block that disagree, the peer rejects each one, and
	// the link never comes back.
	void TestReconnectWithStagedDataRecovers()
	{
		const std::string path = TestSocketPath();
		FakePeer peer(path);
		SocketTransport transport(MakeConfig(path));
		transport.Connect();
		peer.WaitForExchanges(1);

		// The peer has already armed the next exchange, so the pause bites on the one after it
		peer.PauseArming();
		transport.PerformFullTransfer();
		peer.WaitForExchanges(2);

		// Stage a packet, then let the transfer that would carry it starve. The reconnect therefore
		// runs with a non-empty staged transfer, which is the case under test
		const std::string message = "staged before the outage";
		CHECK(transport.WriteMessage(0x11112222U, message), "message staged before the outage");

		std::atomic<int> lostConnections{0};
		transport.SetConnectionLostCallback([&](std::string_view) { ++lostConnections; });

		std::atomic<bool> returned{false};
		std::thread starved([&] {
			try
			{
				transport.PerformFullTransfer();
			}
			catch (const std::exception&)
			{
				// @intentional: only reached via the Stop() below, which unwinds a loop that failed
				// to recover. The check on the exchange count is what reports that failure.
			}
			returned.store(true);
		});

		// Only arm again once the outage has actually been observed, so the reconnect really runs
		for (int waited = 0; lostConnections.load() == 0 && waited < 10000; waited += 10)
		{
			std::this_thread::sleep_for(std::chrono::milliseconds(10));
		}
		CHECK(lostConnections.load() == 1, "the starved transfer reports the connection lost");
		peer.ResumeArming();

		// The reconnected exchange must complete. Bounded rather than a blocking wait: the failure
		// mode is a loop that retries for ever, and a hung test reports nothing
		bool recovered = false;
		for (int waited = 0; waited < 5000; waited += 10)
		{
			if (peer.CompletedExchanges() >= 3)
			{
				recovered = true;
				break;
			}
			std::this_thread::sleep_for(std::chrono::milliseconds(10));
		}
		CHECK(recovered, "the transfer completes once the peer arms again");

		transport.Stop();
		starved.join();
		CHECK(returned.load(), "PerformFullTransfer returned");

		// The staged message is dropped by the reconnect rather than replayed, so the recovered
		// exchange carries no packets at all
		if (recovered)
		{
			CHECK(peer.CapturedPackets().empty(), "the abandoned packet is not replayed after the reconnect");
		}
	}

	// A packet whose declared length runs past the transfer's data block is refused rather than
	// read through. The length is a wire value bounded only by its uint16_t, so believing it would
	// hand the readers above a payload span reaching well beyond the receive buffer
	void TestOverlongPacketIsRefused()
	{
		const std::string path = TestSocketPath();
		FakePeer peer(path);
		SocketTransport transport(MakeConfig(path));
		transport.Connect();
		peer.WaitForExchanges(1);

		proto::CodeBufferUpdateHeader update{};
		update.bufferSpace = 4096;
		peer.StagePacket(static_cast<uint16_t>(proto::FirmwareRequest::CodeBufferUpdate), &update, sizeof(update));
		peer.OverstateNextPacketLength(0xF000);
		transport.PerformFullTransfer();
		peer.WaitForExchanges(2);

		proto::PacketHeader packet{};
		CHECK(!transport.ReadNextPacket(packet), "a packet longer than the data block is not read");

		// The link carries on: a bad packet is dropped, it does not take the connection with it
		peer.StagePacket(static_cast<uint16_t>(proto::FirmwareRequest::CodeBufferUpdate), &update, sizeof(update));
		transport.PerformFullTransfer();
		CHECK(transport.ReadNextPacket(packet), "the next well-formed packet still arrives");
		CHECK(packet.request == static_cast<uint16_t>(proto::FirmwareRequest::CodeBufferUpdate),
			  "the well-formed packet is the staged CodeBufferUpdate");
		CHECK(transport.IsConnected(), "still connected after the bad packet");
	}

	void TestSequenceJumpReportsReset()
	{
		const std::string path = TestSocketPath();
		FakePeer peer(path);
		SocketTransport transport(MakeConfig(path));
		transport.Connect();
		peer.WaitForExchanges(1);
		CHECK(!transport.HadReset(), "no reset after the first transfer");

		peer.JumpSequenceBy(5);
		transport.PerformFullTransfer();
		CHECK(transport.HadReset(), "a sequence discontinuity reports a reset");

		transport.PerformFullTransfer();
		CHECK(!transport.HadReset(), "the reset indication clears on the next transfer");
	}

} // namespace

int main()
{
	TestBasicExchangeAndPackets();
	TestCrcCorruptionIsRetriedNotResynced();
	TestWithheldReadinessTimesOutAndRecovers();
	TestReconnectWithStagedDataRecovers();
	TestOverlongPacketIsRefused();
	TestSequenceJumpReportsReset();
	return TestSupport::Summarise("socket transport");
}
