/*
 * StepTimer.h
 *
 * The step clock, as seen from the SBC.
 *
 * On a Duet board this is a hardware timer counting at StepClockRate, and StepTimer also schedules
 * the interrupts that generate steps. Neither applies here: there is no local driver to step, and
 * no hardware counter running at the controller's rate. What the motion code still needs is the
 * *reading* - the number the controller would report right now - because every move is scheduled by
 * absolute start time in those ticks, and DriveTracker asks "where is this drive at time t".
 *
 * So this is a clock model rather than a clock. CLOCK_MONOTONIC provides the local time base, and a
 * linear fit maps it onto the controller's step clock. The fit is disciplined by the MasterClock
 * packet the controller already sends (FirmwareRequest::MasterClock), so no new protocol is
 * involved: RecordMasterClockSample is called with the tick count the controller reported and the
 * local time at which that transfer completed.
 *
 * Accuracy needed: moves are prepared MoveTiming::AbsoluteMinimumPreparedTime (25ms) before they
 * start, so an error well below a millisecond is invisible. Drift matters more than offset - the
 * two crystals are independent, and an uncorrected 100ppm is 6ms of error per minute - which is why
 * this fits the rate rather than only tracking the offset.
 *
 * Deliberately absent compared with the firmware's version: the StepTimer *instance*, i.e. the
 * callback list and the ScheduleCallback* family. Nothing kept on this side schedules a callback in
 * step-clock time; the motion thread polls.
 */

#ifndef SRC_MOVEMENT_STEPTIMER_H_
#define SRC_MOVEMENT_STEPTIMER_H_

#include <RepRapFirmware.h>

class StepTimer
{
public:
	using Ticks = uint32_t;

	// Reset the model to the nominal rate, anchored at the current local time. Call before use;
	// until a MasterClock sample arrives the reading free-runs and is only self-consistent.
	static void Init() noexcept;

	// The controller's step clock as it reads now. Wraps every 2^32 ticks (~95 minutes at 750kHz),
	// exactly as the hardware counter does - every caller compares differences.
	static Ticks GetTimerTicks() noexcept;

	// As above, less the movement delay. Move start times are expressed in this timebase so that
	// a board which fell behind can push every later move back without the others losing sync.
	static Ticks GetMovementTimerTicks() noexcept;
	static Ticks ConvertLocalToMovementTime(Ticks localTime) noexcept;

	static constexpr uint32_t GetTickRate() noexcept { return StepClockRate; }

	// Report that some part of the system could not keep up and everything must slip by this much.
	static void IncreaseMovementDelay(uint32_t increase) noexcept;
	static Ticks GetMovementDelay() noexcept;

	// If the movement delay has grown since the last call, return the new value, else zero. The
	// controller has to be told, so that it can pass it on to the expansion boards.
	static Ticks CheckMovementDelayIncreased() noexcept;

	// Feed the model. `masterTicks` is the controller's step clock from a MasterClock packet;
	// `localNs` is the local monotonic time at which that transfer completed. Sampling at the same
	// point of the transfer every time matters more than sampling at the right point: a constant
	// offset between the two disappears into the fit, a varying one does not.
	static void RecordMasterClockSample(uint32_t masterTicks, int64_t localNs) noexcept;

	// Our tick rate is a multiple of 1000, so multiply by 1000 and divide by StepClockRate/1000
	// rather than by 1000000, which would overflow.
	static constexpr uint32_t TicksToIntegerMicroseconds(uint32_t n) noexcept
	{
		return (n * 1000) / (StepClockRate / 1000);
	}

	static constexpr float TicksToFloatMicroseconds(uint32_t n) noexcept
	{
		return (float)n * (1000000.0f / (float)StepClockRate);
	}

	// --- Diagnostics -------------------------------------------------------------------------

	struct ClockStats
	{
		double driftPpm;				// fitted rate minus nominal, in parts per million
		uint32_t numSamples;			// samples in the current fit
		uint32_t peakResidualNs;		// largest |sample - fit| since Init
		uint32_t numBackwardClamps;		// times a new fit would have made the reading go backwards
		uint32_t numRejectedSamples;	// samples discarded as implausible
		bool synced;					// true once the fit is based on enough samples to trust
	};

	static ClockStats GetClockStats() noexcept;
	static void Diagnostics(const StringRef& reply) noexcept;

	// --- Test seam ---------------------------------------------------------------------------

	// Replace the local time source. Passing nullptr restores CLOCK_MONOTONIC. For tests, which
	// need to drive the model from a clock they control.
	using LocalClockSource = int64_t (*)() noexcept;
	static void SetLocalClockSource(LocalClockSource source) noexcept;

	// The local time base, in nanoseconds. Public so that whoever times an SPI transfer stamps it
	// from the same source the model is fitted against.
	static int64_t GetLocalTimeNs() noexcept;

	// Samples needed before the fit is trusted, and the most it keeps. Exposed for the tests.
	static constexpr unsigned int MinSamplesToSync = 8;
	static constexpr unsigned int MaxSamples = 64;

	// Samples closer together than this are not added to the fit's window.
	//
	// This is what makes the rate measurable at all. The controller reports whole ticks, so each
	// sample carries +/-0.5 tick of quantisation, and the rate uncertainty is roughly that divided
	// by the span of the window. A transfer completes every few milliseconds, so an undecimated
	// 64-sample window spans under half a second and cannot resolve better than tens of ppm - the
	// same order as the drift being corrected. Decimating to 50ms spreads the window over three
	// seconds and brings the resolution to a fraction of a ppm.
	//
	// The offset does not need this and does not wait for it: until the window has filled to
	// MinSamplesToSync, every sample re-anchors the model at the nominal rate.
	static constexpr int64_t MinSampleSpacingNs = 50000000;		// 50ms

	// A fitted rate further than this from nominal is a bad fit, not a bad crystal. Duet 3 and Pi
	// oscillators are specified well inside 100ppm; 2000 leaves room for a hot board.
	static constexpr double MaxDriftPpm = 2000.0;

	// The model itself - the fitted map from local nanoseconds to controller ticks, the seqlock
	// that publishes it, and the sample window - is entirely private to StepTimer.cpp. Nothing
	// outside needs to name it, and keeping it out of the header keeps <atomic> out of every
	// translation unit that only wants to read the clock.
};

#endif /* SRC_MOVEMENT_STEPTIMER_H_ */
