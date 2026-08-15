/*
 * MoveTimings.h
 *
 *  Created on: 28 Nov 2023
 *      Author: David
 */

#ifndef SRC_MOVEMENT_MOVETIMING_H_
#define SRC_MOVEMENT_MOVETIMING_H_

#include "StepTimer.h"
#include <Movement/StepClock.h>

namespace MoveTiming
{
	// Note on the following constant:
	// If we calculate the step interval on every clock, we reach a point where the calculation time exceeds the step
	// interval. The worst case is pure Z movement on a delta. On a Mini Kossel with 80 steps/mm with this firmware
	// running on a Duet (84MHx SAM3X8 processor), the calculation can just be managed in time at speeds of 15000mm/min
	// (step interval 50us), but not at 20000mm/min (step interval 37.5us). Therefore, where the step interval falls
	// below 60us, we don't calculate on every step. Note: the above measurements were taken some time ago, before some
	// firmware optimisations. These two are per-processor in the firmware, where they bound how often the step ISR can
	// recalculate and how long it backs off for when it cannot keep up. There is no step ISR on the
	// SBC, so the per-processor distinction is gone; the SAM4E/SAME70 values are kept because
	// HiccupTime still sets the granularity by which the whole movement timebase slips when some
	// part of the system falls behind, and that has to stay comparable with the firmware's.
	constexpr uint32_t hiccupTime = (30 * stepClockRate) / 1000000; // 30us in step clocks

	constexpr uint32_t usualMinimumPreparedTime = stepClockRate / 20;	 // 50ms
	constexpr uint32_t absoluteMinimumPreparedTime = stepClockRate / 40; // 25ms

	constexpr uint32_t standardMoveWakeupInterval = 500; // milliseconds

	// Segments shorter than this are folded into the one that follows them. Rounding error in the
	// profile calculation throws off sub-microsecond segments, and they contribute nothing but a
	// speed discontinuity in the reported position. In the firmware this constant lives in
	// DriveMovement; here it belongs with the other timings because DriveTracker is the only user.
	constexpr uint32_t minimumExecutingSegmentDuration = (10 * stepClockRate) / 1000000; // 10us in step clocks
} // namespace MoveTiming

#endif /* SRC_MOVEMENT_MOVETIMING_H_ */
