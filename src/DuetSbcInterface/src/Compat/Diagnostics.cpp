/*
 * Diagnostics.cpp - debugPrintf() and millis() for the imported motion code.
 */

#include <Diagnostics.h>

#include <atomic>
#include <cstdio>
#include <ctime>

namespace
{
	std::atomic<Duet::Sbc::Motion::LogSink> logSink{nullptr};

	// Long enough for the widest DDA::DebugPrint line; anything longer is truncated rather than
	// allocated for, because this runs on the motion thread.
	constexpr size_t maxMessageLength = 256;
}

void Duet::Sbc::Motion::SetLogSink(LogSink sink) noexcept
{
	logSink.store(sink, std::memory_order_release);
}

void DebugPrintf(const char *fmt, ...) noexcept
{
	char buffer[maxMessageLength];

	va_list args;
	va_start(args, fmt);
	const int written = vsnprintf(buffer, sizeof(buffer), fmt, args);
	va_end(args);

	if (written < 0)
	{
		return;
	}

	const auto sink = logSink.load(std::memory_order_acquire);
	if (sink != nullptr)
	{
		sink(buffer);
	}
	else
	{
		std::fputs(buffer, stderr);
	}
}

uint32_t Millis() noexcept
{
	// CLOCK_MONOTONIC rather than the step clock: this is only used for the ring's grace period and
	// for rate-limiting diagnostics, neither of which needs to agree with the controller's clock.
	// Truncating to 32 bits and wrapping every 49 days is deliberate - every caller compares
	// differences, exactly as the firmware does.
	timespec ts{};
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return (uint32_t)((uint64_t)ts.tv_sec * 1000u + (uint64_t)ts.tv_nsec / 1000000u);
}
