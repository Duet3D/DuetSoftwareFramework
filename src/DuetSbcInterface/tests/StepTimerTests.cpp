// StepTimer models the controller's step clock from MasterClock samples arriving over SPI. These
// tests drive it from a clock they control, so a "minute" of tracking runs in microseconds and the
// drift and jitter are exactly known rather than whatever the host oscillator happens to do.
//
// What matters about this model, in order:
//   - the reading never goes backwards, because DDA::HasExpired and DriveTracker::Advance both
//     compare against a stored tick count and would retire a move twice if it did;
//   - it converges on the controller's rate, since uncorrected drift accumulates without limit;
//   - it survives the 32-bit wrap of the controller's counter, which happens every ~95 minutes;
//   - an implausible sample cannot steer it somewhere wrong.

#include "TestSupport.h"

#include <Movement/StepTimer.h>

#include <cstdint>
#include <vector>

namespace
{
	// The clock StepTimer sees. Tests advance it explicitly.
	int64_t fakeLocalNs = 0;

	int64_t FakeClock() noexcept
	{
		return fakeLocalNs;
	}

	constexpr double nominalTicksPerNs = (double)stepClockRate / 1.0e9;
	constexpr int64_t oneMsNs = 1000000;

	// What the real link does: a transfer completes every couple of milliseconds. Everything below
	// is expressed in those terms, so a test that says "feed 2500 samples" means "five seconds of
	// running", which is what it takes for the fit's decimated window to fill.
	constexpr int64_t transferIntervalNs = 2 * oneMsNs;
	constexpr unsigned int samplesPerSecond = (unsigned int)(1000000000 / transferIntervalNs);

	// Enough transfers to fill StepTimer::maxSamples slots at minSampleSpacingNs apart, plus a
	// margin. Below this the fit is working from a shorter window and is correspondingly noisier.
	constexpr unsigned int samplesToFillWindow =
		(unsigned int)((StepTimer::maxSamples + 2) * StepTimer::minSampleSpacingNs / transferIntervalNs);

	void ResetClock()
	{
		fakeLocalNs = 1000000000;			// start away from zero so sign errors show up
		StepTimer::SetLocalClockSource(FakeClock);
		StepTimer::Init();
	}

	// Feed `count` samples `intervalNs` apart from a controller whose clock runs at
	// `ppm` parts per million away from nominal, with `jitterNs` of alternating sampling error.
	// Returns the controller tick count corresponding to the final sample.
	uint32_t FeedSamples(unsigned int count, int64_t intervalNs, double ppm, int64_t jitterNs = 0,
						 uint32_t startTicks = 0)
	{
		const double actualTicksPerNs = nominalTicksPerNs * (1.0 + ppm / 1.0e6);
		const int64_t baseNs = fakeLocalNs;
		uint32_t ticks = startTicks;

		for (unsigned int i = 0; i < count; ++i)
		{
			fakeLocalNs = baseNs + (int64_t)(i + 1) * intervalNs;

			// The controller's clock is a function of true elapsed time...
			const int64_t elapsed = fakeLocalNs - baseNs;
			ticks = startTicks + (uint32_t)llrint((double)elapsed * actualTicksPerNs);

			// ...but we observe it at a slightly wrong moment. Alternating the sign models the
			// transfer-completion timestamp wobbling either side of where the controller latched.
			const int64_t observedNs = fakeLocalNs + ((i % 2 == 0) ? jitterNs : -jitterNs);
			StepTimer::RecordMasterClockSample(ticks, observedNs);
		}
		return ticks;
	}
}

// The reading has to be monotonic across model republishes, which happen on every sample. Jitter is
// what makes this non-trivial: a fit that is a shade lower than its predecessor would otherwise
// step the clock back by the difference.
static void TestMonotonicUnderJitter()
{
	ResetClock();

	uint32_t previous = StepTimer::GetTimerTicks();
	bool wentBackwards = false;

	const double actualTicksPerNs = nominalTicksPerNs * (1.0 + 40.0 / 1.0e6);
	const int64_t baseNs = fakeLocalNs;

	for (unsigned int i = 0; i < 10 * samplesPerSecond; ++i)
	{
		fakeLocalNs = baseNs + (int64_t)(i + 1) * transferIntervalNs;
		const auto ticks = (uint32_t)llrint((double)(fakeLocalNs - baseNs) * actualTicksPerNs);

		// +/-150us of sampling jitter, an order of magnitude worse than a real transfer.
		const int64_t observedNs = fakeLocalNs + ((i % 3 == 0) ? 150000 : -150000);
		StepTimer::RecordMasterClockSample(ticks, observedNs);

		const uint32_t now = StepTimer::GetTimerTicks();
		if ((int32_t)(now - previous) < 0)
		{
			wentBackwards = true;
		}
		previous = now;
	}

	CHECK(!wentBackwards, "reading never goes backwards while being disciplined");

	// The above is only evidence if the clamp was reached. Jitter this large drives the anchor
	// backwards repeatedly, most of all before the fit has enough samples to average it out, so a
	// zero count here means the test stopped covering the guard rather than that the guard works.
	CHECK(StepTimer::GetClockStats().numBackwardClamps > 0, "the backward-step guard was exercised");

	// Anti-backward anchoring must not be a substitute for fitting: if the model were simply
	// ratcheting it would drift arbitrarily far from the controller. Check it still tracks.
	const auto reading = (int32_t)StepTimer::GetTimerTicks();
	const auto truth = (int32_t)(uint32_t)llrint((double)(fakeLocalNs - baseNs) * actualTicksPerNs);
	CHECK_NEAR(reading, truth, 0.002 * stepClockRate, "still within 2ms of the controller after clamping");
}

// A 100ppm rate error is 6ms per minute uncorrected - far more than the 25ms scheduling margin
// tolerates over a print. The fit has to find the rate, not just the offset.
static void TestConvergesOnDriftingClock()
{
	for (const double ppm : {-120.0, -30.0, 30.0, 120.0})
	{
		ResetClock();
		FeedSamples(samplesToFillWindow, transferIntervalNs, ppm);

		const StepTimer::ClockStats stats = StepTimer::GetClockStats();
		CHECK(stats.synced, "synced once the window has filled");
		CHECK_NEAR(stats.driftPpm, ppm, 1.0, "fitted drift matches the controller's rate error");
	}
}

// Before enough samples have arrived to fit a rate, the model still has to give a usable answer:
// the first move is scheduled long before the eighth transfer completes.
static void TestUsableBeforeSynced()
{
	ResetClock();
	const uint32_t lastTicks = FeedSamples(3, transferIntervalNs, 50.0);

	const StepTimer::ClockStats stats = StepTimer::GetClockStats();
	CHECK(!stats.synced, "three samples is not synced");

	// At the instant of the last sample the reading should be that sample's value.
	CHECK_NEAR((int32_t)(StepTimer::GetTimerTicks() - lastTicks), 0, 2,
			   "tracks the offset at the nominal rate before it can fit one");
}

// The controller's counter is 32-bit and wraps every ~95 minutes. Unwrapping it wrongly would put a
// 4-billion-tick discontinuity into the fit.
static void TestSurvivesCounterWrap()
{
	ResetClock();

	// Put the wrap halfway through the run. Starting just short of it would not do: the counter
	// would roll over during the first few transfers, before the fit's decimated window has taken
	// a single sample, and the window would never span the discontinuity at all.
	const int64_t runNs = (int64_t)samplesToFillWindow * transferIntervalNs;
	const auto halfRunTicks = (uint32_t)llrint((double)runNs * 0.5 * nominalTicksPerNs);
	const uint32_t startTicks = 0u - halfRunTicks;

	const uint32_t endTicks = FeedSamples(samplesToFillWindow, transferIntervalNs, 25.0, 0, startTicks);

	CHECK(endTicks < startTicks, "the controller's counter wrapped during the run");
	CHECK(endTicks > halfRunTicks / 2, "and it wrapped in the middle, not at the very end");

	const StepTimer::ClockStats stats = StepTimer::GetClockStats();
	CHECK(stats.synced, "still synced across the wrap");
	CHECK_NEAR(stats.driftPpm, 25.0, 1.0, "wrap does not corrupt the fitted rate");
	CHECK(stats.numRejectedSamples == 0, "wrap is not mistaken for a bad sample");
	CHECK_NEAR((int32_t)(StepTimer::GetTimerTicks() - endTicks), 0, 0.001 * stepClockRate,
			   "reading follows the controller through the wrap");
}

// A controller reset restarts its clock. Steering the model towards the resulting nonsense would be
// worse than ignoring it: the fit is rebuilt from scratch instead.
static void TestRejectsImplausibleSample()
{
	ResetClock();
	FeedSamples(samplesToFillWindow, transferIntervalNs, 20.0);
	const StepTimer::ClockStats before = StepTimer::GetClockStats();
	CHECK(before.synced, "synced before the bad sample");

	// One sample claiming the controller advanced by a second while 1ms of local time passed.
	fakeLocalNs += StepTimer::minSampleSpacingNs;
	StepTimer::RecordMasterClockSample(0x40000000u, fakeLocalNs);

	const StepTimer::ClockStats after = StepTimer::GetClockStats();
	CHECK(after.numRejectedSamples > 0, "implausible sample is counted as rejected");
	CHECK(!after.synced, "the window is dropped rather than steered");

	// And it recovers once sane samples resume.
	FeedSamples(samplesToFillWindow, transferIntervalNs, 20.0);
	const StepTimer::ClockStats recovered = StepTimer::GetClockStats();
	CHECK(recovered.synced, "resyncs after the disturbance");
	CHECK_NEAR(recovered.driftPpm, 20.0, 1.0, "and finds the same rate again");
}

// The movement delay shifts every scheduled move later without disturbing their relative timing, so
// it must come straight off the reading and be reported exactly once per increase.
static void TestMovementDelay()
{
	ResetClock();
	CHECK(StepTimer::GetMovementDelay() == 0, "no movement delay initially");
	CHECK(StepTimer::CheckMovementDelayIncreased() == 0, "nothing to report initially");

	StepTimer::IncreaseMovementDelay(1000);
	CHECK(StepTimer::GetMovementDelay() == 1000, "delay accumulates");
	CHECK(StepTimer::CheckMovementDelayIncreased() == 1000, "increase is reported once");
	CHECK(StepTimer::CheckMovementDelayIncreased() == 0, "and not reported twice");

	StepTimer::IncreaseMovementDelay(500);
	CHECK(StepTimer::GetMovementDelay() == 1500, "further increases add");
	CHECK(StepTimer::CheckMovementDelayIncreased() == 1500, "reports the total, not the increment");

	const uint32_t raw = StepTimer::GetTimerTicks();
	CHECK(StepTimer::ConvertLocalToMovementTime(raw) == raw - 1500, "movement time trails by the delay");
	CHECK((uint32_t)(raw - StepTimer::GetMovementTimerTicks()) >= 1500, "and so does the movement reading");
}

// The controller reports its movement delay as a total, and both sides have to converge on the
// larger of the two: a delay already applied to queued moves cannot be taken back, and a board that
// does not slip with the others loses sync with them.
static void TestAdoptingTheControllersDelay()
{
	ResetClock();
	CHECK(StepTimer::GetMovementDelay() == 0, "no movement delay initially");

	StepTimer::RaiseMovementDelayTo(800);
	CHECK(StepTimer::GetMovementDelay() == 800, "the controller's delay is adopted");
	CHECK(StepTimer::CheckMovementDelayIncreased() == 800, "and reported back once");

	// The controller repeats its total every transfer. Treating that as an increment would run the
	// delay away, a few hundred microseconds per transfer, until moves were scheduled far too late.
	StepTimer::RaiseMovementDelayTo(800);
	CHECK(StepTimer::GetMovementDelay() == 800, "repeating the same total changes nothing");
	CHECK(StepTimer::CheckMovementDelayIncreased() == 0, "and there is nothing new to report");

	StepTimer::RaiseMovementDelayTo(500);
	CHECK(StepTimer::GetMovementDelay() == 800, "a smaller total does not undo a delay already applied");

	StepTimer::RaiseMovementDelayTo(1200);
	CHECK(StepTimer::GetMovementDelay() == 1200, "a larger total is adopted");

	// Our own hiccups and the controller's meet at the larger of the two rather than summing.
	StepTimer::IncreaseMovementDelay(300);
	CHECK(StepTimer::GetMovementDelay() == 1500, "a local hiccup still adds to the delay");
	StepTimer::RaiseMovementDelayTo(1400);
	CHECK(StepTimer::GetMovementDelay() == 1500, "the controller catching up does not lower ours");
}

// Unit conversions, which the ring uses to size its wakeups.
static void TestConversions()
{
	CHECK(StepTimer::GetTickRate() == stepClockRate, "tick rate is the step clock rate");
	CHECK(StepTimer::TicksToIntegerMicroseconds(stepClockRate) == 1000000, "one second is 1e6 us");
	CHECK_NEAR(StepTimer::TicksToFloatMicroseconds(stepClockRate / 1000), 1000.0, 0.001, "one ms is 1000us");
	CHECK(MillisToStepClocks(1000) == stepClockRate, "one second of step clocks");
}

int main()
{
	TestMonotonicUnderJitter();
	TestConvergesOnDriftingClock();
	TestUsableBeforeSynced();
	TestSurvivesCounterWrap();
	TestRejectsImplausibleSample();
	TestMovementDelay();
	TestAdoptingTheControllersDelay();
	TestConversions();

	StepTimer::SetLocalClockSource(nullptr);
	return TestSupport::Summarise("step timer");
}
