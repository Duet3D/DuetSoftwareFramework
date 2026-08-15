/*
 * StepTimer.cpp - see StepTimer.h for what this models and why.
 */

#include "StepTimer.h"

#include <atomic>
#include <cinttypes>
#include <cmath>
#include <ctime>

namespace
{
	constexpr double nominalTicksPerNs = (double)stepClockRate / 1.0e9;

	// The linear map from local nanoseconds to controller ticks:
	//     ticks = masterTicks0 + (localNs - localNs0) * ticksPerNs
	// masterTicks0 is 64-bit; the wrapping 32-bit value callers see is formed on read.
	struct ClockModel
	{
		int64_t localNs0;
		int64_t masterTicks0;
		double ticksPerNs;
	};

	// The published model, behind a seqlock. GetTimerTicks is called from the motion thread on
	// every pass of the ring, and RecordMasterClockSample from the SPI interface thread once per
	// transfer; a mutex between them would put the writer in a position to block the reader, which
	// is the one thing this component must not do. Readers retry instead.
	std::atomic<uint32_t> modelSeq{0};
	ClockModel publishedModel{}; // only written between odd/even seq transitions

	std::atomic<StepTimer::LocalClockSource> localClockSource{nullptr};

	// Movement delay, in step clocks. Written rarely, read on the motion path.
	std::atomic<uint32_t> movementDelay{0};
	std::atomic<bool> movementDelayIncreased{false};

	// --- Fit state. Touched only by RecordMasterClockSample / Init, both on the interface thread.

	struct Sample
	{
		int64_t localNs;
		int64_t masterTicks; // unwrapped
	};

	Sample samples[StepTimer::maxSamples];
	unsigned int numSamples = 0;
	unsigned int nextSample = 0; // ring index; the buffer is a sliding window
	int64_t unwrappedMaster = 0;
	int64_t lastAcceptedNs = 0; // local time of the newest sample in the window
	uint32_t lastRawMaster = 0;
	bool haveFirstSample = false;

	std::atomic<uint32_t> peakResidualNs{0};
	std::atomic<uint32_t> numBackwardClamps{0};
	std::atomic<uint32_t> numRejectedSamples{0};
	std::atomic<double> fittedTicksPerNs{nominalTicksPerNs};
	std::atomic<uint32_t> publishedSampleCount{0};

	ClockModel LoadModel() noexcept
	{
		for (;;)
		{
			const uint32_t before = modelSeq.load(std::memory_order_acquire);
			if ((before & 1u) == 0)
			{
				const ClockModel model = publishedModel;
				std::atomic_thread_fence(std::memory_order_acquire);
				if (modelSeq.load(std::memory_order_relaxed) == before)
				{
					return model;
				}
			}
			// A write is in progress, or landed mid-read. Writes happen once per SPI transfer and
			// take a few dozen nanoseconds, so spinning here costs less than any alternative.
		}
	}

	void PublishModel(const ClockModel& model) noexcept
	{
		const uint32_t seq = modelSeq.load(std::memory_order_relaxed);
		modelSeq.store(seq + 1, std::memory_order_relaxed); // odd: write in progress
		std::atomic_thread_fence(std::memory_order_release);
		publishedModel = model;
		std::atomic_thread_fence(std::memory_order_release);
		modelSeq.store(seq + 2, std::memory_order_release); // even: readable again
	}

	int64_t TicksAt(const ClockModel& model, int64_t localNs) noexcept
	{
		return model.masterTicks0 + (int64_t)llrint((double)(localNs - model.localNs0) * model.ticksPerNs);
	}

	// Publish a model, having first made sure it cannot make the reading go backwards.
	//
	// DDA::HasExpired and DriveTracker::Advance both compare the current tick count against a stored
	// one, so a step back would retire a move twice or rewind a tracked position. A new model can
	// ask for one whenever sampling jitter puts its anchor behind its predecessor's - which is most
	// likely before the fit has enough samples to average that jitter away, so every publish goes
	// through here rather than only the fitted ones. Re-anchoring costs an offset error of at most
	// the jitter, which the next models take back up.
	void PublishClamped(ClockModel model, int64_t nowNs) noexcept
	{
		const int64_t previousReading = TicksAt(LoadModel(), nowNs);
		const int64_t newReading = TicksAt(model, nowNs);
		if (newReading < previousReading)
		{
			model.masterTicks0 += previousReading - newReading;
			numBackwardClamps.fetch_add(1, std::memory_order_relaxed);
		}
		PublishModel(model);
	}

	int64_t MonotonicNs() noexcept
	{
		timespec ts{};
		clock_gettime(CLOCK_MONOTONIC, &ts);
		return (int64_t)ts.tv_sec * 1000000000 + ts.tv_nsec;
	}
} // namespace

int64_t StepTimer::GetLocalTimeNs() noexcept
{
	const auto source = localClockSource.load(std::memory_order_acquire);
	return (source != nullptr) ? source() : MonotonicNs();
}

void StepTimer::SetLocalClockSource(LocalClockSource source) noexcept
{
	localClockSource.store(source, std::memory_order_release);
}

// --- Reading -----------------------------------------------------------------------------------

void StepTimer::Init() noexcept
{
	numSamples = 0;
	nextSample = 0;
	unwrappedMaster = 0;
	lastAcceptedNs = 0;
	lastRawMaster = 0;
	haveFirstSample = false;

	peakResidualNs.store(0, std::memory_order_relaxed);
	numBackwardClamps.store(0, std::memory_order_relaxed);
	numRejectedSamples.store(0, std::memory_order_relaxed);
	fittedTicksPerNs.store(nominalTicksPerNs, std::memory_order_relaxed);
	publishedSampleCount.store(0, std::memory_order_relaxed);
	movementDelay.store(0, std::memory_order_relaxed);
	movementDelayIncreased.store(false, std::memory_order_relaxed);

	PublishModel(ClockModel{GetLocalTimeNs(), 0, nominalTicksPerNs});
}

StepTimer::Ticks StepTimer::GetTimerTicks() noexcept
{
	const ClockModel model = LoadModel();
	return (Ticks)(uint64_t)TicksAt(model, GetLocalTimeNs());
}

StepTimer::Ticks StepTimer::ConvertLocalToMovementTime(Ticks localTime) noexcept
{
	return localTime - movementDelay.load(std::memory_order_relaxed);
}

StepTimer::Ticks StepTimer::GetMovementTimerTicks() noexcept
{
	return ConvertLocalToMovementTime(GetTimerTicks());
}

// --- Movement delay ----------------------------------------------------------------------------

void StepTimer::IncreaseMovementDelay(uint32_t increase) noexcept
{
	movementDelay.fetch_add(increase, std::memory_order_relaxed);
	movementDelayIncreased.store(true, std::memory_order_release);
}

void StepTimer::RaiseMovementDelayTo(Ticks total) noexcept
{
	Ticks current = movementDelay.load(std::memory_order_relaxed);
	while (total > current)
	{
		if (movementDelay.compare_exchange_weak(current, total, std::memory_order_relaxed))
		{
			movementDelayIncreased.store(true, std::memory_order_release);
			return;
		}
	}
}

StepTimer::Ticks StepTimer::GetMovementDelay() noexcept
{
	return movementDelay.load(std::memory_order_relaxed);
}

StepTimer::Ticks StepTimer::CheckMovementDelayIncreased() noexcept
{
	if (movementDelayIncreased.exchange(false, std::memory_order_acq_rel))
	{
		return movementDelay.load(std::memory_order_relaxed);
	}
	return 0;
}

// --- Discipline --------------------------------------------------------------------------------

void StepTimer::RecordMasterClockSample(uint32_t masterTicks, int64_t localNs) noexcept
{
	// Unwrap the controller's 32-bit counter. Transfers are milliseconds apart and the counter
	// wraps every ~95 minutes, so the difference between consecutive samples is always a small
	// positive number and the wrap falls out of the unsigned subtraction.
	if (!haveFirstSample)
	{
		unwrappedMaster = masterTicks;
		haveFirstSample = true;
	}
	else
	{
		unwrappedMaster += (int64_t)(uint32_t)(masterTicks - lastRawMaster);
	}
	lastRawMaster = masterTicks;

	// Decimate into the fitting window. See minSampleSpacingNs for why the window has to span
	// seconds rather than transfers.
	const bool accepted = (numSamples == 0) || (localNs - lastAcceptedNs >= minSampleSpacingNs);
	if (accepted)
	{
		samples[nextSample] = Sample{localNs, unwrappedMaster};
		nextSample = (nextSample + 1) % maxSamples;
		lastAcceptedNs = localNs;
		if (numSamples < maxSamples)
		{
			++numSamples;
		}
	}

	if (numSamples < minSamplesToSync)
	{
		// Not enough to fit a rate yet. Track the offset at the nominal rate, on every sample rather
		// than only the accepted ones, so the reading is usable immediately - the first move is
		// scheduled long before the window fills.
		PublishClamped(ClockModel{localNs, unwrappedMaster, nominalTicksPerNs}, localNs);
		publishedSampleCount.store(numSamples, std::memory_order_relaxed);
		return;
	}

	if (!accepted)
	{
		// Between window samples the published model extrapolates, which is what it is for.
		// Re-anchoring here on every transfer would feed each sample's quantisation error straight
		// into the reading.
		return;
	}

	// Ordinary least squares of masterTicks against localNs. Both are offset by the oldest sample
	// before summing: the raw values are ~1e18 and ~1e12, and the sums of squares would lose every
	// bit that matters in double.
	const unsigned int oldest = (numSamples < maxSamples) ? 0 : nextSample;
	const int64_t refNs = samples[oldest].localNs;
	const int64_t refTicks = samples[oldest].masterTicks;

	double sumX = 0.0, sumY = 0.0, sumXX = 0.0, sumXY = 0.0;
	for (unsigned int i = 0; i < numSamples; ++i)
	{
		const Sample& s = samples[(oldest + i) % maxSamples];
		const double x = (double)(s.localNs - refNs);
		const double y = (double)(s.masterTicks - refTicks);
		sumX += x;
		sumY += y;
		sumXX += x * x;
		sumXY += x * y;
	}

	const double n = (double)numSamples;
	const double denom = (n * sumXX) - (sumX * sumX);
	if (denom <= 0.0)
	{
		// Every sample carries the same local timestamp, so there is no rate information. Possible
		// if the clock source is coarse; keep the previous model.
		numRejectedSamples.fetch_add(1, std::memory_order_relaxed);
		return;
	}

	const double slope = ((n * sumXY) - (sumX * sumY)) / denom;
	const double intercept = (sumY - (slope * sumX)) / n;

	const double ppm = ((slope / nominalTicksPerNs) - 1.0) * 1.0e6;
	if (!std::isfinite(slope) || std::fabs(ppm) > maxDriftPpm)
	{
		// The fit is implausible - a stalled transfer, or a controller reset that reset its clock.
		// Drop the window and start again rather than steering the model somewhere wrong.
		numRejectedSamples.fetch_add(1, std::memory_order_relaxed);
		numSamples = 0;
		nextSample = 0;
		haveFirstSample = false;
		publishedSampleCount.store(0, std::memory_order_relaxed);
		return;
	}

	// Residual of the newest sample, as a health metric.
	const double predicted = intercept + (slope * (double)(localNs - refNs));
	const double residualTicks = (double)(unwrappedMaster - refTicks) - predicted;
	const auto residualNs = (uint32_t)std::fabs(residualTicks / nominalTicksPerNs);
	if (residualNs > peakResidualNs.load(std::memory_order_relaxed))
	{
		peakResidualNs.store(residualNs, std::memory_order_relaxed);
	}

	const ClockModel model{refNs, refTicks + (int64_t)llrint(intercept), slope};

	fittedTicksPerNs.store(slope, std::memory_order_relaxed);
	publishedSampleCount.store(numSamples, std::memory_order_relaxed);
	PublishClamped(model, GetLocalTimeNs());
}

StepTimer::ClockStats StepTimer::GetClockStats() noexcept
{
	const uint32_t count = publishedSampleCount.load(std::memory_order_relaxed);
	return ClockStats{((fittedTicksPerNs.load(std::memory_order_relaxed) / nominalTicksPerNs) - 1.0) * 1.0e6,
					  count,
					  peakResidualNs.load(std::memory_order_relaxed),
					  numBackwardClamps.load(std::memory_order_relaxed),
					  numRejectedSamples.load(std::memory_order_relaxed),
					  count >= minSamplesToSync};
}
