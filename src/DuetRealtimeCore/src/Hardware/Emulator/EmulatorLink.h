// The SBC's SPI bus and input lines, connected to an emulated controller over the emulator link (see
// EmulatorLinkFormats.h). Used in place of spidev and the GPIO character device when the configuration
// says the controller is emulated.
#pragma once

#include <Hardware/Emulator/EmulatorLinkFormats.h>
#include <Hardware/SbcHardware.h>

#include <array>
#include <atomic>
#include <condition_variable>
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
	// takes the emulator's frames off the socket as they arrive: line changes are queued as edge events
	// on their line, and exchange results are handed to the exchange waiting for them.
	class EmulatorLink
	{
	  public:
		// Connect to the emulator listening at `socketPath`, retrying for up to `connectTimeoutMs`
		// while it starts. `exchangeTimeoutMs` bounds the wait for an exchange's result. Throws
		// std::system_error if no connection can be made.
		EmulatorLink(const std::string& socketPath, int connectTimeoutMs, int exchangeTimeoutMs);
		~EmulatorLink();

		EmulatorLink(const EmulatorLink&) = delete;
		EmulatorLink& operator=(const EmulatorLink&) = delete;
		EmulatorLink(EmulatorLink&&) = delete;
		EmulatorLink& operator=(EmulatorLink&&) = delete;

		// One chip-select cycle. Throws std::system_error if the link has failed or the emulator does
		// not answer in time.
		void Exchange(const uint8_t* tx, uint8_t* rx, size_t length);

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

		void ReaderLoop() noexcept;
		void OnLineLevel(Line line, bool level);
		void Fail(const char* reason) noexcept;
		void SendAll(const void* data, size_t length);

		const int m_exchangeTimeoutMs;
		int m_socketFd = -1;

		std::array<LineState, NumLines> m_lines;

		// The exchange in progress and its result, handed over by the reader thread
		std::mutex m_exchangeMutex;
		std::mutex m_resultMutex;
		std::condition_variable m_resultReady;
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
