#include "LocalClock.h"

#include <Platform/Activity.h>
#include <Platform/Waker.h>

#include <sys/eventfd.h>
#include <unistd.h>

#include <algorithm>
#include <atomic>
#include <cerrno>
#include <ctime>
#include <mutex>
#include <system_error>
#include <vector>

// Waker is implemented here, beside the list of those that follow the clock
namespace Duet::Sbc
{
	namespace
	{
		constexpr int64_t nsPerMs = 1000000;

		std::atomic<bool> pinned{false};
		std::atomic<int64_t> pinnedNs{0};

		// The Wakers that follow the clock
		std::mutex followersMutex;
		std::vector<Waker*> followers;

		int64_t MonotonicNs() noexcept
		{
			timespec ts{};
			clock_gettime(CLOCK_MONOTONIC, &ts);
			return (int64_t)ts.tv_sec * 1000000000 + ts.tv_nsec;
		}

		void WakeFollowers() noexcept
		{
			const std::lock_guard lock(followersMutex);
			for (Waker* waker : followers)
			{
				waker->Signal();
			}
		}
	} // namespace

	namespace LocalClock
	{
		int64_t NowNs() noexcept
		{
			return pinned.load(std::memory_order_acquire) ? pinnedNs.load(std::memory_order_acquire) : MonotonicNs();
		}

		bool IsPinned() noexcept
		{
			return pinned.load(std::memory_order_acquire);
		}

		void Pin(int64_t ns) noexcept
		{
			pinnedNs.store(ns, std::memory_order_release);
			pinned.store(true, std::memory_order_release);
			WakeFollowers();
		}

		void Unpin() noexcept
		{
			pinned.store(false, std::memory_order_release);
			WakeFollowers();
		}

		int PollTimeoutMs(int64_t deadlineNs) noexcept
		{
			const int64_t left = deadlineNs - NowNs();
			if (left <= 0)
			{
				return 0;
			}
			if (IsPinned())
			{
				return -1;
			}
			return static_cast<int>(std::min<int64_t>((left + nsPerMs - 1) / nsPerMs, INT32_MAX));
		}

		int64_t DeadlineAfterMs(int64_t ms) noexcept
		{
			return NowNs() + ms * nsPerMs;
		}
	} // namespace LocalClock

	Waker::Waker(Clock clock)
		: m_fd(::eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC))
		, m_followsClock(clock == Clock::Follow)
	{
		if (m_fd < 0)
		{
			throw std::system_error(errno, std::generic_category(), "Cannot create wake eventfd");
		}
		if (m_followsClock)
		{
			const std::lock_guard lock(followersMutex);
			followers.push_back(this);
		}
	}

	Waker::~Waker()
	{
		if (m_followsClock)
		{
			const std::lock_guard lock(followersMutex);
			std::erase(followers, this);
		}
		// Wake-ups nobody will take any more
		Drain();
		::close(m_fd);
	}

	void Waker::Signal() noexcept
	{
		Activity::Signal(m_fd);
	}

	bool Waker::Drain() noexcept
	{
		uint64_t count = 0;
		return ::read(m_fd, &count, sizeof(count)) == sizeof(count);
	}

} // namespace Duet::Sbc
