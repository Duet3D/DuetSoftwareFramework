/*
 * SegmentBuilder.cpp - see SegmentBuilder.h.
 *
 * AddSegment below is Move::AddSegment from the imported RepRapFirmware Move.cpp with the S-curve
 * and AVOID_SHORT_SEGMENTS branches removed. Keeping its structure recognisable is deliberate: it
 * is the piece most likely to need comparing against upstream when a motion bug turns up.
 */

#include "SegmentBuilder.h"

#include <cstdlib>

namespace
{
	using Duet::Sbc::Motion::MoveProfile;

	// The initial speed of a segment, given what it covers and how it accelerates: s = u*t + a*t^2/2
	// rearranged for u. Segments do not store u because it is recoverable this way.
	motioncalc_t CalcInitialSpeed(uint32_t duration, motioncalc_t distance, motioncalc_t a) noexcept
	{
		return distance / (motioncalc_t)duration - oneHalf * a * (motioncalc_t)duration;
	}
} // namespace

MoveSegment* Duet::Sbc::Motion::SegmentBuilder::AddSegment(MoveSegment* list,
														   uint32_t startTime,
														   uint32_t duration,
														   motioncalc_t distance,
														   motioncalc_t a,
														   MovementFlags moveFlags,
														   motioncalc_t pressureAdvanceClocksTimesDuration) noexcept
{
	if ((int32_t)duration <= 0)
	{
		// Every caller checks the phase is non-empty first, so this means the profile itself is
		// malformed. Report it and drop the segment rather than inserting one that would divide by
		// zero in CalcInitialSpeed.
		DebugPrintf("Adding zero or negative duration segment: d=%.3e a=%.3e\n", (double)distance, (double)a);
		return list;
	}

	// Pressure advance adds distance proportional to the speed change, i.e. to a * t.
	distance += a * pressureAdvanceClocksTimesDuration;

	MoveSegment* prev = nullptr;
	MoveSegment* seg = list;

	// Find the earliest existing segment that the new one starts before, or overlaps.
	while (seg != nullptr)
	{
		int32_t offset = (int32_t)(startTime - seg->GetStartTime()); // how much later the new one starts
		if (offset < 0)												 // new segment starts first
		{
			if (offset + (int32_t)duration <= 0)
			{
				break; // and ends before this one starts
			}

			// Insert the part that precedes the existing segment, then go round again with the rest.
			seg = MoveSegment::Allocate(seg);
			const uint32_t firstDuration = (uint32_t)-offset;
			const auto mFirstDuration = (motioncalc_t)firstDuration;
			const motioncalc_t firstDistance =
				(CalcInitialSpeed(duration, distance, a) + oneHalf * a * mFirstDuration) * mFirstDuration;
			seg->SetParameters(
				startTime, firstDuration, firstDistance, a J_ACTUAL_PARAMETER((motioncalc_t)0.0), moveFlags);
			if (prev == nullptr)
			{
				list = seg;
			}
			else
			{
				prev->SetNext(seg);
			}

			duration -= firstDuration;
			startTime += firstDuration;
			distance -= firstDistance;
			prev = seg;
			seg = seg->GetNext();
			if (seg == nullptr)
			{
				break;
			}
			offset = 0;
		}

		// The new segment now starts at or after the existing one starts.
		if (offset < (int32_t)seg->GetDuration()) // it starts before this one ends
		{
			// If it starts strictly later, split the existing segment so the two line up.
			if (offset != 0)
			{
				prev = seg;
				seg = seg->Split((uint32_t)offset);
				offset = 0;
			}

			// Same start time now, but they may end at different times.
			const int32_t timeDifference = (int32_t)(duration - seg->GetDuration());
			if (timeDifference > 0)
			{
				// The new segment outlasts the existing one: merge as much as overlaps, then loop.
				const auto segDuration = (motioncalc_t)seg->GetDuration();
				const motioncalc_t firstDistance =
					(CalcInitialSpeed(duration, distance, a) + oneHalf * a * segDuration) * segDuration;
				seg->Merge(firstDistance, a J_ACTUAL_PARAMETER((motioncalc_t)0.0), moveFlags);
				distance -= firstDistance;
				startTime += seg->GetDuration();
				duration = (uint32_t)timeDifference;
			}
			else
			{
				// The new segment ends at or before the existing one. Trim the existing one to match
				// if needed, then merge; there is nothing left of the new segment afterwards.
				if (timeDifference != 0)
				{
					(void)seg->Split(duration);
				}
				seg->Merge(distance, a J_ACTUAL_PARAMETER((motioncalc_t)0.0), moveFlags);
				return list;
			}
		}

		prev = seg;
		seg = seg->GetNext();
	}

	// Whatever is left of the new segment goes before 'seg', which may be null.
	MoveSegment* const newSeg = MoveSegment::Allocate(seg);
	newSeg->SetParameters(startTime, duration, distance, a J_ACTUAL_PARAMETER((motioncalc_t)0.0), moveFlags);
	if (prev == nullptr)
	{
		list = newSeg;
	}
	else
	{
		prev->SetNext(newSeg);
	}
	return list;
}

MoveSegment* Duet::Sbc::Motion::SegmentBuilder::AddLinearSegments(MoveSegment* list,
																  uint32_t startTime,
																  const MoveProfile& profile,
																  motioncalc_t steps,
																  MovementFlags moveFlags,
																  motioncalc_t pressureAdvanceClocks) noexcept
{
	if (profile.totalDistance == (motioncalc_t)0.0)
	{
		return list; // nothing to scale onto this drive
	}

	const uint32_t steadyStartTime = startTime + profile.accelClocks;
	const uint32_t decelStartTime = steadyStartTime + profile.steadyClocks;

	// The move's profile is in mm; this drive covers `steps` microsteps over the whole of it.
	const motioncalc_t stepsPerMm = steps / profile.totalDistance;

	// A phase of zero duration is not executed, and dividing by its duration would produce
	// infinities. Skip those, but keep the distances adding up to the whole move.
	motioncalc_t accelDistance;
	motioncalc_t accelPressureAdvance;
	if (profile.accelClocks == 0)
	{
		accelDistance = 0;
		accelPressureAdvance = 0;
	}
	else
	{
		accelDistance =
			(profile.decelClocks + profile.steadyClocks == 0) ? profile.totalDistance : profile.accelDistance;
		accelPressureAdvance = (motioncalc_t)profile.accelClocks * pressureAdvanceClocks;
	}

	motioncalc_t decelDistance;
	motioncalc_t decelPressureAdvance;
	if (profile.decelClocks == 0)
	{
		decelDistance = 0;
		decelPressureAdvance = 0;
	}
	else
	{
		decelDistance =
			profile.totalDistance - ((profile.steadyClocks == 0) ? accelDistance : profile.decelStartDistance);
		decelPressureAdvance = (motioncalc_t)profile.decelClocks * pressureAdvanceClocks;
	}

	const motioncalc_t steadyDistance =
		(profile.steadyClocks == 0) ? (motioncalc_t)0.0 : profile.totalDistance - accelDistance - decelDistance;

	if (profile.accelClocks != 0)
	{
		list = AddSegment(list,
						  startTime,
						  profile.accelClocks,
						  accelDistance * stepsPerMm,
						  profile.acceleration * stepsPerMm,
						  moveFlags,
						  accelPressureAdvance);
	}
	if (profile.steadyClocks != 0)
	{
		list = AddSegment(list,
						  steadyStartTime,
						  profile.steadyClocks,
						  steadyDistance * stepsPerMm,
						  (motioncalc_t)0.0,
						  moveFlags,
						  (motioncalc_t)0.0);
	}
	if (profile.decelClocks != 0)
	{
		list = AddSegment(list,
						  decelStartTime,
						  profile.decelClocks,
						  decelDistance * stepsPerMm,
						  profile.deceleration * stepsPerMm,
						  moveFlags,
						  decelPressureAdvance);
	}
	return list;
}
