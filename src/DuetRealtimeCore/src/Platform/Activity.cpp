#include "Activity.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <mutex>
#include <vector>

namespace Duet::Sbc::Activity
{
	namespace
	{
		// The most fds one wait watches
		constexpr size_t maxWatchedFds = 8;

		std::atomic<int64_t> pendingCount{0};
		std::atomic<int64_t> parkedElsewhereCount{0};

		// The threads waiting, which Signalled looks through
		std::mutex waitersMutex;
		std::vector<WaitingOn*> waiters;

		// Whether the calling thread takes part
		thread_local bool threadRunning = false;

		bool AnyReadable(std::span<const pollfd> fds) noexcept
		{
			std::array<pollfd, maxWatchedFds> copy{};
			const size_t count = std::min(fds.size(), copy.size());
			std::copy_n(fds.begin(), count, copy.begin());
			return ::poll(copy.data(), count, 0) > 0;
		}
	} // namespace

	int64_t Pending() noexcept
	{
		return pendingCount.load(std::memory_order_seq_cst);
	}

	int64_t ParkedElsewhere() noexcept
	{
		return parkedElsewhereCount.load(std::memory_order_seq_cst);
	}

	void ThreadStarted() noexcept
	{
		if (!threadRunning)
		{
			threadRunning = true;
			pendingCount.fetch_add(1, std::memory_order_seq_cst);
		}
	}

	void ThreadStopped() noexcept
	{
		if (threadRunning)
		{
			pendingCount.fetch_sub(1, std::memory_order_seq_cst);
			threadRunning = false;
		}
	}

	void Signalled(int fd) noexcept
	{
		const std::lock_guard lock(waitersMutex);
		for (WaitingOn* waiter : waiters)
		{
			if (waiter->m_parked && waiter->Watches(fd))
			{
				waiter->m_woken++;
				pendingCount.fetch_add(1, std::memory_order_seq_cst);
			}
		}
	}

	void EventPosted() noexcept
	{
		pendingCount.fetch_add(1, std::memory_order_seq_cst);
	}

	void EventConsumed() noexcept
	{
		pendingCount.fetch_sub(1, std::memory_order_seq_cst);
	}

	WaitingOn::WaitingOn(std::span<const pollfd> fds) noexcept
		: m_fds(fds)
		, m_running(threadRunning)
	{
		const std::lock_guard lock(waitersMutex);
		m_parked = !AnyReadable(fds);
		if (m_parked)
		{
			(m_running ? pendingCount : parkedElsewhereCount).fetch_add(m_running ? -1 : 1, std::memory_order_seq_cst);
		}
		waiters.push_back(this);
	}

	WaitingOn::~WaitingOn()
	{
		const std::lock_guard lock(waitersMutex);
		std::erase(waiters, this);
		if (m_parked)
		{
			(m_running ? pendingCount : parkedElsewhereCount).fetch_add(m_running ? 1 : -1, std::memory_order_seq_cst);
		}
		pendingCount.fetch_sub(m_woken, std::memory_order_seq_cst);
	}

	bool WaitingOn::Watches(int fd) const noexcept
	{
		return std::ranges::any_of(m_fds, [fd](const pollfd& p) { return p.fd == fd; });
	}

} // namespace Duet::Sbc::Activity
