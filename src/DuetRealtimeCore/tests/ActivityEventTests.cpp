// The inbound events Activity counts (Platform/Activity.h) as LinkService posts them and the managed
// dispatcher consumes them: an event is pending from when it is posted until it is consumed, so a
// consumer that takes an event the moment it appears never sees less pending than the thread that
// posted it, which is running.

#include "TestSupport.h"

#include <Config/Configuration.h>
#include <Interface/LinkEvents.h>
#include <Interface/LinkService.h>
#include <Interface/Socket/SocketTransport.h>
#include <Platform/Activity.h>
#include <Platform/RingBuffer.h>

#include <unistd.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <memory>
#include <string>
#include <thread>

using namespace Duet::Sbc;

namespace
{
	// Never connected: posting and consuming inbound events needs no link
	std::unique_ptr<LinkService> CreateUnconnected()
	{
		Config config;
		config.transport = TransportKind::Socket;
		config.socketPath = "/tmp/duet-activity-event-test-" + std::to_string(::getpid()) + ".sock";
		return std::make_unique<LinkService>(config, std::make_unique<SocketTransport>(config));
	}

	// The motion thread posts MoveCompleted while the dispatcher, already awake, takes each event as
	// DuetRT_PeekEvent and DuetRT_ConsumeEvent do. The poster takes part and runs throughout, so
	// Pending() is never below 1 while it does
	void AnEventConsumedAsSoonAsItIsPostedNeverHidesThePoster()
	{
		const std::unique_ptr<LinkService> link = CreateUnconnected();
		const int64_t before = Activity::Pending();

		constexpr int numEvents = 2000000;
		std::atomic<bool> posting{true};
		std::atomic<bool> drained{false};
		std::atomic<bool> running{false};

		std::thread poster(
			[&]
			{
				const Activity::Running participant;
				running = true;
				MoveCompletedEvent event{};
				event.header.type = static_cast<uint16_t>(InboundEventType::MoveCompleted);
				for (int i = 0; i < numEvents; ++i)
				{
					event.moveId = static_cast<uint32_t>(i) + 1;
					link->PostEventFromOtherThread(InboundEventType::MoveCompleted, AsBytes(event));
				}
				posting = false;
				// Still running until the consumer has seen the last event
				while (!drained)
				{
					std::this_thread::yield();
				}
			});

		while (!running)
		{
			std::this_thread::yield();
		}

		int64_t consumed = 0;
		int64_t hidden = 0;
		int64_t lowest = before + 1;
		RingBuffer& inbound = link->Inbound();
		for (;;)
		{
			if (inbound.Peek().has_value())
			{
				if (inbound.Consume())
				{
					Activity::EventConsumed();
					++consumed;
				}
				const int64_t pending = Activity::Pending();
				if (pending < before + 1)
				{
					++hidden;
					lowest = std::min(lowest, pending);
				}
			}
			else if (!posting)
			{
				break;
			}
		}
		drained = true;
		poster.join();

		std::printf("  %lld events consumed; Pending() read below the running poster %lld time(s), lowest %lld against %lld\n",
					static_cast<long long>(consumed), static_cast<long long>(hidden), static_cast<long long>(lowest),
					static_cast<long long>(before + 1));
		CHECK(consumed > 0, "the events posted are consumed");
		CHECK(hidden == 0, "an event consumed before it is counted never cancels out the thread that posted it");
		CHECK(Activity::Pending() == before, "nothing is left pending once every event is consumed and the poster stops");
	}

	// A link destroyed with events in its inbound ring, as a bench's is when it ends, leaves none of them
	// pending: nobody will consume them, and a later bench in the process would wait on them for ever
	void EventsLeftWhenTheLinkGoesAreNotPending()
	{
		const int64_t before = Activity::Pending();
		{
			const std::unique_ptr<LinkService> link = CreateUnconnected();
			MoveCompletedEvent event{};
			event.header.type = static_cast<uint16_t>(InboundEventType::MoveCompleted);
			for (uint32_t i = 1; i <= 3; ++i)
			{
				event.moveId = i;
				link->PostEventFromOtherThread(InboundEventType::MoveCompleted, AsBytes(event));
			}
			if (link->Inbound().Peek().has_value() && link->Inbound().Consume())
			{
				Activity::EventConsumed();
			}
			CHECK(Activity::Pending() == before + 2, "the events not consumed are pending");
		}
		CHECK(Activity::Pending() == before, "none is pending once the link has gone");
	}

} // namespace

int main()
{
	// Before anything native is created, as a bench must
	Activity::Enable();
	AnEventConsumedAsSoonAsItIsPostedNeverHidesThePoster();
	EventsLeftWhenTheLinkGoesAreNotPending();
	return TestSupport::Summarise("activity event");
}
