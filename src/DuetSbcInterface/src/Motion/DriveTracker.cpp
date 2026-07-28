/*
 * DriveTracker.cpp - see DriveTracker.h.
 */

#include "DriveTracker.h"

#include <Motion/SegmentBuilder.h>
#include <Movement/MoveTiming.h>

using Duet::Sbc::Motion::DriveTracker;

void DriveTracker::Init(size_t logicalDrive) noexcept
{
	drive = (uint8_t)logicalDrive;
	segments = nullptr;
	retiredSegment = nullptr;
	u = 0;
	distanceCarriedForwards = 0;
	currentMotorPosition = 0;
	positionAtSegmentStart = 0;
	netStepsThisSegment = 0;
	movementAccumulator = 0;
	segmentFlags.Init();
	enteredCurrentSegment = false;
}

void DriveTracker::AddMove(uint32_t startTime, const MoveProfile& profile, motioncalc_t steps,
						   MovementFlags moveFlags, motioncalc_t pressureAdvanceClocks) noexcept
{
	segments = SegmentBuilder::AddLinearSegments(segments, startTime, profile, steps, moveFlags,
												 pressureAdvanceClocks);

	// AddLinearSegments may have split or merged the segment that was current, so its cached
	// parameters no longer describe the segment at the head of the chain. Re-enter it on the next
	// Advance rather than trusting them.
	//
	// This is safe here in a way it would not be in the firmware, which has to detach the tail of
	// the chain first so that the step ISR cannot be executing a segment while it is being amended.
	// Nothing else touches the chain on this side; see Compat/RTOSIface/RTOSIface.h.
	enteredCurrentSegment = false;
}

void DriveTracker::EnterCurrentSegment() noexcept
{
	MoveSegment *seg = segments;

	// Fold away segments too short to be worth evaluating separately. Rounding error in the profile
	// makes sub-microsecond segments unreliable, and they show up in the reported position as speed
	// discontinuities that did not happen. Only merge into a segment that starts exactly where this
	// one ends and describes the same kind of motion.
	for (;;)
	{
		MoveSegment *const nextSeg = seg->GetNext();
		if (   seg->GetDuration() >= MoveTiming::MinimumExecutingSegmentDuration
			|| nextSeg == nullptr
			|| !nextSeg->GetFlags().SameStaticFlags(seg->GetFlags())
			|| nextSeg->GetStartTime() != seg->GetStartTime() + seg->GetDuration())
		{
			break;
		}
		nextSeg->CombinePrevious(seg);
		segments = nextSeg;
		MoveSegment::Release(seg);			// released rather than retired: it never really existed
		seg = nextSeg;
	}

	seg->SetExecuting();
	segmentFlags = seg->GetFlags();
	u = seg->CalcU();
	positionAtSegmentStart = currentMotorPosition;

	// Truncation, not rounding, and the leftover is carried into the next segment. A drive only
	// ever moves whole microsteps, so the fractional part has to go somewhere or a long run of
	// moves would accumulate an error in the reported position.
	netStepsThisSegment = (int32_t)(seg->GetLength() + distanceCarriedForwards);
	enteredCurrentSegment = true;
}

void DriveTracker::RetireSegment(MoveSegment *segment) noexcept
{
	if (retiredSegment != nullptr)
	{
		MoveSegment::Release(retiredSegment);
	}
	retiredSegment = segment;
}

void DriveTracker::Advance(uint32_t now) noexcept
{
	for (;;)
	{
		MoveSegment *seg = segments;
		if (seg == nullptr)
		{
			enteredCurrentSegment = false;
			return;
		}

		if ((int32_t)(now - seg->GetStartTime()) < 0)
		{
			return;								// due to start later; nothing to account for yet
		}

		if (!enteredCurrentSegment)
		{
			EnterCurrentSegment();
			seg = segments;						// EnterCurrentSegment may have merged it away
		}

		if ((uint32_t)(now - seg->GetStartTime()) < seg->GetDuration())
		{
			return;								// still running; GetCurrentPosition interpolates it
		}

		// Finished. Take its whole travel, and carry the fraction of a step it could not deliver.
		currentMotorPosition = positionAtSegmentStart + netStepsThisSegment;
		movementAccumulator += netStepsThisSegment;
		distanceCarriedForwards = seg->GetLength() + distanceCarriedForwards - (motioncalc_t)netStepsThisSegment;

		segments = seg->GetNext();
		RetireSegment(seg);
		enteredCurrentSegment = false;
	}
}

float DriveTracker::GetCurrentPosition(uint32_t now) const noexcept
{
	const MoveSegment *const seg = segments;
	if (seg == nullptr || !enteredCurrentSegment)
	{
		// Stationary, or the next segment has not started. Either way the drive is wherever the
		// last retired segment left it.
		return (float)((motioncalc_t)currentMotorPosition + distanceCarriedForwards);
	}

	auto timeSinceStart = (int32_t)(now - seg->GetStartTime());
	if (timeSinceStart < 0)
	{
		return (float)((motioncalc_t)positionAtSegmentStart + distanceCarriedForwards);
	}

	if ((uint32_t)timeSinceStart >= seg->GetDuration())
	{
		// The segment is over but has not been retired yet, because Advance has not run since.
		// Report where it ends rather than extrapolating past it.
		timeSinceStart = (int32_t)seg->GetDuration();
	}

	// s = u*t + a*t^2/2, from the position the segment started at.
	const auto t = (motioncalc_t)timeSinceStart;
	return (float)((u + OneHalf * seg->GetA() * t) * t
				   + (motioncalc_t)positionAtSegmentStart + distanceCarriedForwards);
}

void DriveTracker::SetMotorPosition(int32_t position) noexcept
{
	ClearMovementPending();
	currentMotorPosition = positionAtSegmentStart = position;
	distanceCarriedForwards = 0;
	movementAccumulator = 0;
}

int32_t DriveTracker::GetAndClearAccumulatedMovement() noexcept
{
	const int32_t ret = movementAccumulator;
	movementAccumulator = 0;
	return ret;
}

void DriveTracker::ClearMovementPending() noexcept
{
	MoveSegment::ReleaseAll(segments);
	segments = nullptr;
	if (retiredSegment != nullptr)
	{
		MoveSegment::Release(retiredSegment);
		retiredSegment = nullptr;
	}
	netStepsThisSegment = 0;
	u = 0;
	segmentFlags.Init();
	enteredCurrentSegment = false;
}
