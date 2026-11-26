// The SBC's SPI bus and input lines, connected to an emulated controller over the emulator link (see
// EmulatorLinkFormats.h). Used in place of spidev and the GPIO character device when the configuration
// says the controller is emulated.
#pragma once

#include <Hardware/Emulator/EmulatorLinkFormats.h>
#include <Hardware/SbcHardware.h>
#include <Platform/Waker.h>

#include <array>
#include <atomic>
#include <cstdint>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace Duet::Sbc::Emulator
{

	// One connection to the emulator, shared by the bus and the lines that use it. A reader thread
	// takes the emulator's frames off the socket as they arrive and holds them; the bench releases
	// them one at a time once it has moved the local clock to their time (EmulatorLinkFormats.h).
	// A released line change is queued as an edge on its line; a released exchange result is handed
	// to the exchange waiting for it.
	class EmulatorLink
	{
	  public:
		// Connect to the emulator listening at `socketPath`, retrying for up to `connectTimeoutMs`
		// while it starts. `exchangeTimeoutMs` bounds the wait for an exchange's result, in local
		// time. Throws std::system_error if no connection can be made.
		EmulatorLink(const std::string& socketPath, int connectTimeoutMs, int exchangeTimeoutMs);
		~EmulatorLink();

		EmulatorLink(const EmulatorLink&) = delete;
		EmulatorLink& operator=(const EmulatorLink&) = delete;
		EmulatorLink(EmulatorLink&&) = delete;
		EmulatorLink& operator=(EmulatorLink&&) = delete;

		// The connected link, for the bench to drive through the CApi; null when none is connected
		static std::shared_ptr<EmulatorLink> Current();

		// Register a link as the current one, which SbcHardware does once it has made it
		static void SetCurrent(const std::shared_ptr<EmulatorLink>& link);

		// One chip-select cycle. Throws std::system_error if the link has failed or the emulator does
		// not answer in time.
		void Exchange(const uint8_t* tx, uint8_t* rx, size_t length);

		// --- Lockstep, driven by the bench ---

		// The emulated time of the oldest frame held, in nanoseconds; false if none is held
		bool NextFrameTime(int64_t& ns);

		// Deliver the oldest frame held. False if none is held.
		bool ReleaseFrame();

		// Exchanges sent to the emulator, and frames received from it
		[[nodiscard]] uint32_t FramesSent() const noexcept { return m_framesSent.load(); }
		[[nodiscard]] uint32_t FramesReceived() const noexcept { return m_framesReceived.load(); }

		// --- Lines ---

		struct Edge
		{
			bool rising;
			uint32_t sequenceNumber;
		};

		[[nodiscard]] int LineFd(Line line) const noexcept;
		[[nodiscard]] bool LineLevel(Line line) const noexcept;
		// Take the oldest queued edge of a line. False if there is none.
		bool TakeEdge(Line line, Edge& edge);
		[[nodiscard]] int MissedEdges(Line line) const noexcept;

	  private:
		// Edges a line keeps until they are consumed. More are counted as missed, as the kernel does
		// when its event buffer overflows.
		static constexpr size_t s_maxQueuedEdges = 256;

		struct LineState
		{
			int eventFd = -1; // semaphore eventfd: one count per queued edge
			std::atomic<bool> level{false};
			std::mutex mutex;
			std::deque<Edge> edges;
			uint32_t sequenceNumber = 0;
			std::atomic<int> missedEdges{0};
		};

		struct HeldFrame
		{
			FrameHeader header;
			std::vector<uint8_t> payload;
		};

		void ReaderLoop() noexcept;
		void Deliver(HeldFrame& frame);
		void OnLineLevel(Line line, bool level);
		void Fail(const char* reason) noexcept;
		void SendAll(const void* data, size_t length);

		const int m_exchangeTimeoutMs;
		int m_socketFd = -1;

		std::array<LineState, NumLines> m_lines;

		// Frames received and not yet released
		std::mutex m_heldMutex;
		std::deque<HeldFrame> m_held;
		std::atomic<uint32_t> m_framesSent{0};
		std::atomic<uint32_t> m_framesReceived{0};

		// The exchange in progress and its result, handed over by ReleaseFrame
		std::mutex m_exchangeMutex;
		std::mutex m_resultMutex;
		Waker m_resultWaker;
		Waker m_clockWaker{Waker::Clock::Follow};
		std::vector<uint8_t> m_result;
		bool m_haveResult = false;
		uint16_t m_sequence = 0; // of the exchange in progress or last made
		bool m_failed = false;
		std::string m_failure;

		std::thread m_reader;
	};

	// The emulated controller's SPI bus
	class EmulatedSpiBus final : public SpiBus
	{
	  public:
		explicit EmulatedSpiBus(std::shared_ptr<EmulatorLink> link)
			: m_link(std::move(link))
		{
		}

		void TransferFullDuplex(const uint8_t* tx, const uint8_t* rx, size_t length) const override;

	  private:
		std::shared_ptr<EmulatorLink> m_link;
	};

	// One of the emulated controller's output lines, as an input to the SBC
	class EmulatedInputLine final : public InputLine
	{
	  public:
		EmulatedInputLine(std::shared_ptr<EmulatorLink> link, Line line)
			: m_link(std::move(link))
			, m_line(line)
		{
		}

		[[nodiscard]] int Fd() const noexcept override { return m_link->LineFd(m_line); }
		[[nodiscard]] bool Read() const override { return m_link->LineLevel(m_line); }
		bool ReadEvent() override;
		[[nodiscard]] uint32_t RisingSequenceNumber() const noexcept override { return m_lastRisingSeqno; }
		[[nodiscard]] int MissedEdges() const noexcept override { return m_link->MissedEdges(m_line); }

	  private:
		std::shared_ptr<EmulatorLink> m_link;
		Line m_line;
		uint32_t m_lastRisingSeqno = 0;
	};

} // namespace Duet::Sbc::Emulator
