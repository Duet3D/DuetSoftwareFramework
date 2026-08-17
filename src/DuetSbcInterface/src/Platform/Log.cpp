/*
 * Log.cpp - see Log.h.
 */

#include <Platform/Log.h>

#include <cstdarg>

#include <atomic>
#include <cstdio>

namespace
{
	std::atomic<Duet::Sbc::LogSink> logSink{nullptr};

	// Long enough for the widest DDA::DebugPrint line; anything longer is truncated rather than
	// allocated for, because this runs on the motion thread.
	constexpr size_t maxMessageLength = 256;
} // namespace

void Duet::Sbc::SetLogSink(LogSink sink) noexcept
{
	logSink.store(sink, std::memory_order_release);
}

namespace
{
	void Emit(const char* fmt, va_list args) noexcept
	{
		char buffer[maxMessageLength];
		if (vsnprintf(buffer, sizeof(buffer), fmt, args) < 0)
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
} // namespace

void Duet::Sbc::LogMessage(const char* fmt, ...) noexcept
{
	va_list args;
	va_start(args, fmt);
	Emit(fmt, args);
	va_end(args);
}

void DebugPrintf(const char* fmt, ...) noexcept
{
	va_list args;
	va_start(args, fmt);
	Emit(fmt, args);
	va_end(args);
}
