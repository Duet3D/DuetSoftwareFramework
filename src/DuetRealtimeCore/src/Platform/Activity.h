// How much the SBC side's own threads have left to do.
//
// A bench that drives time itself (the emulation bench, a stepped system test) moves the clock only
// once everything the last move set off has run its course, which is what makes its runs repeat.
// Pending() is that test for the threads that take part: each counts while it runs, and a thread
// waiting on some fds counts again from the moment a wake-up is written to one of them until it
// wakes. A thread waiting with no wake-up written to what it waits on has nothing to do until the
// clock or something outside moves.
//
// A wake-up written to an fd that nobody is waiting on is not counted: either its thread is running,
// and counted as such, or it is waiting on something else and will find the wake-up once that has
// happened. Counting it would have the bench wait for a thread that is itself waiting for the bench.
//
// The native threads take part, and so does DuetControlServer's dispatcher, through the CApi. A
// thread that does not take part (a thread-pool thread calling into the library) is counted by
// ParkedElsewhere() while it waits here, so that whoever is watching the thread pool can tell a pool
// thread parked in a native wait from one doing work. The managed side's inbound events are counted
// from when they are posted until they are consumed.
#pragma once

#include <poll.h>

#include <cstdint>
#include <string>
#include <span>

namespace Duet::Sbc::Activity
{

	// Threads that take part and are running, wake-ups written to what a thread waits on, and
	// inbound events not yet consumed; 0 when there is nothing to do until something moves
	int64_t Pending() noexcept;

	// Threads that do not take part and are waiting here
	int64_t ParkedElsewhere() noexcept;

	// What is pending, for a bench that has waited too long for it: the inbound events and wake-ups
	// not yet taken, and each thread that takes part, by name, running or waiting
	std::string Describe();

	// The calling thread starts or stops taking part
	void ThreadStarted() noexcept;
	void ThreadStopped() noexcept;

	// A wake-up is about to be written to `fd`. Call it before the write, so that the thread it wakes
	// is counted before it can be woken.
	void Signalled(int fd) noexcept;

	// An inbound event has been posted for the managed side, or consumed by it
	void EventPosted() noexcept;
	void EventConsumed() noexcept;

	// Takes part for the life of the object
	class Running
	{
	  public:
		Running() noexcept { ThreadStarted(); }
		~Running() { ThreadStopped(); }
		Running(const Running&) = delete;
		Running& operator=(const Running&) = delete;
		Running(Running&&) = delete;
		Running& operator=(Running&&) = delete;
	};

	// The calling thread waits on `fds` for the life of the object, which is held around the
	// poll(). A thread with a wake-up already written to one of them is not counted as waiting,
	// because its poll() will return at once.
	class WaitingOn
	{
	  public:
		explicit WaitingOn(std::span<const pollfd> fds) noexcept;
		~WaitingOn();
		WaitingOn(const WaitingOn&) = delete;
		WaitingOn& operator=(const WaitingOn&) = delete;
		WaitingOn(WaitingOn&&) = delete;
		WaitingOn& operator=(WaitingOn&&) = delete;

		// Whether it is waiting on `fd`; for Signalled
		[[nodiscard]] bool Watches(int fd) const noexcept;

	  private:
		std::span<const pollfd> m_fds;
		bool m_parked = false;
		bool m_running = false;
		int64_t m_woken = 0;

		friend void Signalled(int fd) noexcept;
	};

} // namespace Duet::Sbc::Activity
