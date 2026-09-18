/*
 * StepClock.h
 *
 * The controller's step clock: its rate, and the conversions between it and the units a move is
 * expressed in.
 *
 * This is the one quantity that must be identical on both sides of the link. Move start times travel
 * to the boards as absolute counts in these ticks, so a mismatch does not degrade motion, it
 * schedules it at the wrong moment. The value is copied verbatim from
 * src/DuetCANMaster/src/RepRapFirmware.h.
 *
 * The rate is what this header holds; the *reading* is StepTimer, which models the controller's
 * counter from the samples every SPI transfer carries. See StepTimer.h.
 */

#ifndef SRC_MOTION_STEPCLOCK_H_
#define SRC_MOTION_STEPCLOCK_H_

#include <Config/MachineLimits.h>

constexpr uint32_t stepClockRate = 48000000 / 64; // 750kHz, common to all Duet 3 boards
constexpr uint64_t stepClockRateSquared = (uint64_t)stepClockRate * stepClockRate;
constexpr float stepClocksToSeconds = 1.0f / (float)stepClockRate;

constexpr unsigned int iMinutesToSeconds = 60;

static constexpr uint32_t MillisToStepClocks(uint32_t numMillis) noexcept
{
	static_assert(stepClockRate % 1000 == 0);
	return numMillis * (stepClockRate / 1000);
}

static constexpr float InverseConvertSpeedToMmPerSec(float speed) noexcept
{
	return speed * (float)stepClockRate;
}

static constexpr float ConvertAcceleration(float accel) noexcept
{
	return accel * (1.0f / (float)stepClockRateSquared);
}

static constexpr float InverseConvertAcceleration(float accel) noexcept
{
	return accel * (float)stepClockRateSquared;
}

#endif /* SRC_MOTION_STEPCLOCK_H_ */
