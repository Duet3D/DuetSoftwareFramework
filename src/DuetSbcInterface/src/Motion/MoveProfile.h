/*
 * MoveProfile.h
 *
 * The velocity profile of one move: how long it accelerates, holds and decelerates, and how far it
 * travels in each of those phases.
 *
 * In RepRapFirmware this is PrepParams, declared inside DDA.h and filled by DDA::Prepare. It is
 * pulled out into its own type here because on this side it is the interface between three
 * otherwise-unrelated things: DDA::Prepare produces it, SegmentBuilder turns it into the segment
 * chains that position tracking walks, and ScheduleMoveBuilder puts it on the wire for the
 * controller to fan out to the boards. Two of those three have no reason to know what a DDA is.
 *
 * Fields map one-for-one onto the ScheduleMove packet, so that the wire format is a re-encoding of
 * this and not a separate description of the same move that could drift away from it.
 *
 * Units throughout are step clocks and millimetres, i.e. speed is mm per step clock and
 * acceleration mm per step clock squared. SegmentBuilder scales to steps per drive as it goes.
 */

#ifndef SRC_MOTION_MOVEPROFILE_H_
#define SRC_MOTION_MOVEPROFILE_H_

#include <RepRapFirmware.h>

namespace Duet::Sbc::Motion
{
	struct MoveProfile
	{
		uint32_t accelClocks = 0;
		uint32_t steadyClocks = 0;
		uint32_t decelClocks = 0;

		motioncalc_t acceleration = 0;			// always positive
		motioncalc_t deceleration = 0;			// always negative, matching the firmware's convention

		motioncalc_t totalDistance = 0;
		motioncalc_t accelDistance = 0;			// distance at the end of the acceleration phase
		motioncalc_t decelStartDistance = 0;	// distance at which deceleration begins

		motioncalc_t startSpeed = 0;
		motioncalc_t topSpeed = 0;
		motioncalc_t endSpeed = 0;

		[[nodiscard]] constexpr uint32_t TotalClocks() const noexcept
		{
			return accelClocks + steadyClocks + decelClocks;
		}
	};
}

#endif /* SRC_MOTION_MOVEPROFILE_H_ */
