// Thread placement helpers, ported from DuetSharedLibrary/ProcessHelpers.cs.
#pragma once

namespace Duet::Sbc
{

	// True if running on a Raspberry Pi (best-effort check of /proc/cpuinfo).
	bool IsRaspberryPi() noexcept;

	// Pin the calling thread to a single CPU core via sched_setaffinity. Returns true on success.
	bool PinCurrentThreadToCore(int coreId) noexcept;

	// Switch the calling thread to SCHED_FIFO at the given real-time priority (1..99, clamped to the
	// kernel-accepted range). This is what gives deterministic, preempt-over-CFS latency on PREEMPT_RT.
	// Requires CAP_SYS_NICE or a suitable RLIMIT_RTPRIO. Returns true on success.
	bool SetCurrentThreadRealtimePriority(int priority) noexcept;

} // namespace Duet::Sbc
