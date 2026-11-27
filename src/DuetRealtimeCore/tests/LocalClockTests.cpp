// The local time base a bench can pin (Platform/LocalClock.h), the wake-ups that follow it
// (Platform/Waker.h) and the activity count a bench waits on before moving it (Platform/Activity.h).

#include <Platform/Activity.h>
#include <Platform/LocalClock.h>
#include <Platform/Waker.h>

#include <TestSupport.h>

#include <poll.h>

#include <atomic>
#include <thread>

using namespace Duet::Sbc;

namespace
{
	bool PollsReadable(int fd)
	{
		pollfd pfd{fd, POLLIN, 0};
		return ::poll(&pfd, 1, 0) == 1 && (pfd.revents & POLLIN) != 0;
	}

	void APinnedClockReadsWhatItIsPinnedTo()
	{
		LocalClock::Pin(5000000);
		CHECK(LocalClock::IsPinned(), "the clock is pinned");
		CHECK(LocalClock::NowNs() == 5000000, "it reads what it is pinned to");
		LocalClock::Pin(7000000);
		CHECK(LocalClock::NowNs() == 7000000, "pinning again moves it");
		LocalClock::Unpin();
		CHECK(!LocalClock::IsPinned(), "unpinned, it is the monotonic clock again");
		const int64_t a = LocalClock::NowNs();
		const int64_t b = LocalClock::NowNs();
		CHECK(b >= a, "which never goes backwards");
	}

	void APollTimeoutWaitsForThePinnedClockToMove()
	{
		LocalClock::Pin(10000000);
		CHECK(LocalClock::PollTimeoutMs(10000000) == 0, "a deadline that has come needs no wait");
		CHECK(LocalClock::PollTimeoutMs(20000000) == -1, "on a pinned clock a later deadline waits for the clock to move");
		CHECK(LocalClock::DeadlineAfterMs(5) == 15000000, "a deadline is measured from the pinned time");
		LocalClock::Unpin();
		const int64_t deadline = LocalClock::DeadlineAfterMs(50);
		const int timeout = LocalClock::PollTimeoutMs(deadline);
		CHECK(timeout > 0 && timeout <= 50, "on the monotonic clock it waits for the time left");
	}

	void AWakerThatFollowsTheClockIsWokenWhenItMoves()
	{
		Waker follows(Waker::Clock::Follow);
		Waker ignores;
		LocalClock::Pin(1000);
		CHECK(PollsReadable(follows.Fd()), "moving the clock wakes a waker that follows it");
		CHECK(!PollsReadable(ignores.Fd()), "but not one that ignores it");
		CHECK(follows.Drain(), "the wake-up is taken");
		CHECK(!PollsReadable(follows.Fd()), "and is gone");
		CHECK(!follows.Drain(), "there is nothing more to take");
		LocalClock::Unpin();
		(void)follows.Drain();
	}

	// Runs before Enable, which cannot be undone
	void NothingIsCountedUntilABenchEnablesIt()
	{
		Waker waker;
		{
			const Activity::Running running;
			CHECK(Activity::Pending() == 0, "a thread that runs is not counted");
			pollfd pfd{waker.Fd(), POLLIN, 0};
			const Activity::WaitingOn wait({&pfd, 1});
			CHECK(Activity::ParkedElsewhere() == 0, "nor is one that waits");
			waker.Signal();
			Activity::EventPosted();
			CHECK(Activity::Pending() == 0, "nor a wake-up or an event");
			Activity::EventConsumed();
		}
		(void)waker.Drain();
		CHECK(Activity::Describe() == "events 0, wake-ups 0, threads none", "and no thread takes part");

		// A wait that began before counting started ends without upsetting the count
		pollfd pfd{waker.Fd(), POLLIN, 0};
		{
			const Activity::WaitingOn wait({&pfd, 1});
			Activity::Enable();
		}
		CHECK(Activity::Pending() == 0 && Activity::ParkedElsewhere() == 0, "a wait from before counting leaves nothing behind");
	}

	void AWakeUpCountsOnlyWhileAThreadWaitsOnIt()
	{
		const int64_t before = Activity::Pending();
		Waker waker;
		waker.Signal();
		CHECK(Activity::Pending() == before, "a wake-up nobody waits on is not counted");
		(void)waker.Drain();

		std::atomic<bool> waiting{false};
		std::atomic<bool> woken{false};
		std::thread worker(
			[&]
			{
				const Activity::Running running;
				pollfd pfd{waker.Fd(), POLLIN, 0};
				{
					const Activity::WaitingOn wait({&pfd, 1});
					waiting = true;
					(void)::poll(&pfd, 1, -1);
					woken = true;
					while (woken)
					{
						std::this_thread::yield();
					}
				}
				(void)waker.Drain();
			});
		while (!waiting)
		{
			std::this_thread::yield();
		}
		CHECK(Activity::Pending() == before, "a thread waiting with nothing to wake it is not pending");
		waker.Signal();
		CHECK(Activity::Pending() == before + 1, "a wake-up for a waiting thread is pending");
		while (!woken)
		{
			std::this_thread::yield();
		}
		CHECK(Activity::Pending() == before + 1, "and stays pending until the thread stops waiting");
		woken = false;
		worker.join();
		CHECK(Activity::Pending() == before, "a thread that has stopped is not pending");
	}

	void AThreadIsPendingWhileItRunsAndNotWhileItWaits()
	{
		const int64_t before = Activity::Pending();
		const int64_t elsewhere = Activity::ParkedElsewhere();
		Waker waker;
		pollfd pfd{waker.Fd(), POLLIN, 0};
		{
			const Activity::WaitingOn wait({&pfd, 1});
			CHECK(Activity::Pending() == before, "a thread that does not take part is not counted as waiting");
			CHECK(Activity::ParkedElsewhere() == elsewhere + 1, "but as waiting elsewhere");
		}
		CHECK(Activity::ParkedElsewhere() == elsewhere, "until it stops waiting");

		std::thread worker(
			[&]
			{
				const Activity::Running running;
				CHECK(Activity::Pending() == before + 1, "a thread that takes part is pending while it runs");
				{
					const Activity::WaitingOn wait({&pfd, 1});
					CHECK(Activity::Pending() == before, "and not while it waits");
				}
				CHECK(Activity::Pending() == before + 1, "it is pending again once it stops waiting");
				waker.Signal();
				{
					const Activity::WaitingOn wait({&pfd, 1});
					CHECK(Activity::Pending() == before + 1, "a thread whose wake-up has come already is not counted as waiting");
				}
				(void)waker.Drain();
			});
		worker.join();
		CHECK(Activity::Pending() == before, "a thread that has stopped is not pending");
	}

} // namespace

int main()
{
	APinnedClockReadsWhatItIsPinnedTo();
	APollTimeoutWaitsForThePinnedClockToMove();
	AWakerThatFollowsTheClockIsWokenWhenItMoves();
	NothingIsCountedUntilABenchEnablesIt();
	AWakeUpCountsOnlyWhileAThreadWaitsOnIt();
	AThreadIsPendingWhileItRunsAndNotWhileItWaits();
	return TestSupport::Summarise("local clock");
}
