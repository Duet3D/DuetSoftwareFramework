/*
 * Log.h
 *
 * Where debugPrintf() output from the imported motion code goes.
 *
 * The motion thread runs SCHED_FIFO, so it must not write to a file descriptor: a pipe nobody is
 * draining blocks, and blocking that thread is the failure this component exists to prevent. So the
 * sink is a function pointer that the owner installs, and DuetSbcInterface points it at the same
 * lock-free ring the rest of its logging uses, where the message is drained by DuetControlServer's
 * dispatcher thread instead.
 *
 * Left unset it writes to stderr, which is what the unit tests and the offline harness want.
 */

#ifndef SRC_MOTION_LOG_H_
#define SRC_MOTION_LOG_H_

#include <Config/MachineLimits.h>

// Debug topic selection, as M111 sets it in the firmware. Nothing sets it yet - see
// MotionSystem::SetDebugFlags - so the branches that read it are compiled but not taken.
enum class Module : uint8_t
{
	Move = 0,
	DDA,
	Num
};

// Debug output from the motion sources. Routed to the sink below rather than to stdout: the motion
// thread runs SCHED_FIFO, and a write() to a pipe nobody is draining would block it.
void DebugPrintf(const char* fmt, ...) noexcept __attribute__((format(printf, 1, 2)));

namespace Duet::Sbc::Motion
{
	// Receives one already-formatted, NUL-terminated line. Must not block.
	using LogSink = void (*)(const char* message) noexcept;

	// Install the sink. Passing nullptr restores the stderr default.
	void SetLogSink(LogSink sink) noexcept;

	// Format one line and hand it to the sink. This is what the motion sources reach through
	// DebugPrintf, and what stands in for the firmware's Platform::MessageF.
	void LogMessage(const char* fmt, ...) noexcept __attribute__((format(printf, 1, 2)));
} // namespace Duet::Sbc::Motion

#endif /* SRC_MOTION_LOG_H_ */
