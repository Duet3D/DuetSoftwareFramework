// The SBC's local time base: CLOCK_MONOTONIC, or a time a bench pins and moves itself.
//
// Everything native that is timed reads this: the step-clock model StepTimer fits, and the transfer
// loop's timeouts and keep-alive. A bench that pins it (the emulation bench, a stepped system test)
// therefore has the whole native timeline in hand: nothing times out and nothing falls due until it
// moves the clock, and moving it wakes every waiter, which then sees whether its deadline has passed.
#pragma once

#include <cstdint>

namespace Duet::Sbc::LocalClock
{

	// The local time, in nanoseconds
	int64_t NowNs() noexcept;

	// Whether a bench has pinned the clock
	bool IsPinned() noexcept;

	// Pin the clock at `ns`, or move it there if it is pinned already, and wake every Waker that
	// follows the clock. Process-wide, like the clock it stands in for; never call it against a
	// real controller, whose step clock would then be fitted against a clock that does not move.
	void Pin(int64_t ns) noexcept;

	// Go back to CLOCK_MONOTONIC
	void Unpin() noexcept;

	// The poll() timeout for a wait until `deadlineNs`: 0 once it has passed; -1 while the clock is
	// pinned, because only Pin moves it and Pin wakes the waiter; otherwise the milliseconds left,
	// rounded up
	int PollTimeoutMs(int64_t deadlineNs) noexcept;

	// `ms` milliseconds after now, as a deadline
	int64_t DeadlineAfterMs(int64_t ms) noexcept;

} // namespace Duet::Sbc::LocalClock
