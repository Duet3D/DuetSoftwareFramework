// A wake-up for a thread blocked in poll(): an eventfd that becomes readable when Signal is called,
// counted by Activity while a thread waits on it (Activity::WaitingOn). One that follows the clock is
// also signalled whenever LocalClock::Pin moves the clock, so a thread waiting for a deadline on a
// pinned clock looks at it again.
#pragma once

#include <cstdint>

namespace Duet::Sbc
{

	class Waker
	{
	  public:
		enum class Clock : uint8_t
		{
			Ignore,
			Follow,
		};

		// Throws std::system_error if the eventfd cannot be created
		explicit Waker(Clock clock = Clock::Ignore);
		~Waker();
		Waker(const Waker&) = delete;
		Waker& operator=(const Waker&) = delete;
		Waker(Waker&&) = delete;
		Waker& operator=(Waker&&) = delete;

		// The fd to poll for POLLIN
		[[nodiscard]] int Fd() const noexcept { return m_fd; }

		// Wake the thread. Safe from any thread.
		void Signal() noexcept;

		// Take every wake-up sent so far, so that the next poll() blocks. Returns whether there was one.
		bool Drain() noexcept;

	  private:
		int m_fd;
		bool m_followsClock;
	};

} // namespace Duet::Sbc
