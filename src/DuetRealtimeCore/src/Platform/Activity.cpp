#include "Activity.h"

#include <pthread.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <mutex>
#include <string>
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

		// The parts of the pending count, for Describe
		std::atomic<int64_t> wakeupCount{0};
		std::atomic<int64_t> eventCount{0};

		// A thread that takes part, and whether it is waiting
		struct Participant
		{
			std::string name;
			bool parked = false;
		};

		// The threads that take part, guarded by waitersMutex
		std::vector<Participant*> participants;
		thread_local Participant* self = nullptr;

		std::string ThreadName()
		{
			std::array<char, 32> name{};
			return ::pthread_getname_np(::pthread_self(), name.data(), name.size()) == 0 ? std::string(name.data()) : std::string("?");
		}

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
			try
			{
				auto* participant = new Participant{ThreadName()};
				const std::lock_guard lock(waitersMutex);
				participants.push_back(participant);
				self = participant;
			}
			catch (...)
			{
				// Without its name the thread still takes part; Describe just cannot name it
			}
		}
	}

	void ThreadStopped() noexcept
	{
		if (threadRunning)
		{
			pendingCount.fetch_sub(1, std::memory_order_seq_cst);
			threadRunning = false;
			if (self != nullptr)
			{
				const std::lock_guard lock(waitersMutex);
				std::erase(participants, self);
				delete self;
				self = nullptr;
			}
		}
	}

	std::string Describe()
	{
		std::string description = "events " + std::to_string(eventCount.load()) + ", wake-ups " + std::to_string(wakeupCount.load()) + ", threads";
		const std::lock_guard lock(waitersMutex);
		if (participants.empty())
		{
			description += " none";
		}
		for (const Participant* participant : participants)
		{
			description += " " + participant->name + (participant->parked ? " (waiting)" : " (running)");
		}
		return description;
	}

	void Signalled(int fd) noexcept
	{
		const std::lock_guard lock(waitersMutex);
		for (WaitingOn* waiter : waiters)
		{
			if (waiter->m_parked && waiter->Watches(fd))
			{
				waiter->m_woken++;
				wakeupCount.fetch_add(1, std::memory_order_seq_cst);
				pendingCount.fetch_add(1, std::memory_order_seq_cst);
			}
		}
	}

	void EventPosted() noexcept
	{
		eventCount.fetch_add(1, std::memory_order_seq_cst);
		pendingCount.fetch_add(1, std::memory_order_seq_cst);
	}

	void EventConsumed() noexcept
	{
		eventCount.fetch_sub(1, std::memory_order_seq_cst);
		pendingCount.fetch_sub(1, std::memory_order_seq_cst);
	}

	WaitingOn::WaitingOn(std::span<const pollfd> fds) noexcept
		: m_fds(fds)
		, m_running(threadRunning)
	{
		const std::lock_guard lock(waitersMutex);
		m_parked = !AnyReadable(fds);
		if (m_parked && m_running && self != nullptr)
		{
			self->parked = true;
		}
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
		if (m_parked && m_running && self != nullptr)
		{
			self->parked = false;
		}
		if (m_parked)
		{
			(m_running ? pendingCount : parkedElsewhereCount).fetch_add(m_running ? 1 : -1, std::memory_order_seq_cst);
		}
		wakeupCount.fetch_sub(m_woken, std::memory_order_seq_cst);
		pendingCount.fetch_sub(m_woken, std::memory_order_seq_cst);
	}

	bool WaitingOn::Watches(int fd) const noexcept
	{
		return std::ranges::any_of(m_fds, [fd](const pollfd& p) { return p.fd == fd; });
	}

} // namespace Duet::Sbc::Activity
