/*
 * MotionSystem.cpp - see MotionSystem.h.
 */

#include "MotionSystem.h"

#include <Movement/MoveTiming.h>
#include <Movement/StepTimer.h>
#include <Platform/Tasks.h>

#include <cstdlib>

using Duet::Sbc::Motion::MotionSystem;

namespace
{
	// How much of the permanent arena to reserve. Sized from the worst case the ring can reach:
	// every DDA in both rings, plus a segment per drive per queued move. The firmware sizes its
	// equivalent from whatever RAM is left over; here memory is not the constraint, so this is
	// generous enough that exhaustion means a bug rather than a busy machine.
	constexpr size_t PermanentArenaBytes = 4 * 1024 * 1024;
}

MotionSystem::MotionSystem() noexcept
{
	for (size_t drive = 0; drive < MaxAxesPlusExtruders; ++drive)
	{
		trackers[drive].Init(drive);
	}
}

bool MotionSystem::Init() noexcept
{
	if (!Tasks::InitPermanentArena(PermanentArenaBytes))
	{
		return false;
	}

	StepTimer::Init();
	for (size_t drive = 0; drive < MaxAxesPlusExtruders; ++drive)
	{
		trackers[drive].Init(drive);
		targetBacklashSteps[drive] = 0;
		currentBacklashSteps[drive] = 0;
	}
	for (auto& d : lastMoveWasBackwards) { d = false; }
	return true;
}

void MotionSystem::Configure(const MotionConfig& newConfig) noexcept
{
	config = newConfig;
}

int32_t MotionSystem::ApplyBacklashCompensation(size_t drive, int32_t delta) noexcept
{
	if (drive >= MaxAxes)
	{
		return delta;						// extruders have no backlash to take up
	}

	// A change of direction means the whole backlash has to be taken up again, in the new direction.
	const bool backwards = (delta < 0);
	int32_t& targetSteps = targetBacklashSteps[drive];
	if (backwards != lastMoveWasBackwards[drive])
	{
		lastMoveWasBackwards[drive] = backwards;
		const int32_t backlash = config.backlashSteps[drive];
		targetSteps += (backwards) ? -backlash : backlash;
	}

	int32_t& currentSteps = currentBacklashSteps[drive];
	const int32_t stepsDue = targetSteps - currentSteps;
	if (stepsDue != 0)
	{
		// Spread the correction over several moves rather than injecting it in one: a whole
		// backlash added to a short move would be a visible jolt. backlashCorrectionDistanceFactor
		// is how many times the correction the move must be before it is all taken at once.
		if ((uint32_t)labs(stepsDue) * config.backlashCorrectionDistanceFactor <= (uint32_t)labs(delta))
		{
			delta += stepsDue;
			currentSteps = targetSteps;
		}
		else
		{
			const auto maxAllowedSteps =
				(int32_t)max<uint32_t>((uint32_t)labs(delta) / config.backlashCorrectionDistanceFactor, 1u);
			const int32_t stepsToDo = (stepsDue < 0) ? max<int32_t>(stepsDue, -maxAllowedSteps)
													 : min<int32_t>(stepsDue, maxAllowedSteps);
			currentSteps += stepsToDo;
			delta += stepsToDo;
		}
	}
	return delta;
}

void MotionSystem::EnableDrivers(size_t drive, bool unconditional) noexcept
{
	// Nothing to do. Every driver is on a CAN-connected board and is enabled by the move messages
	// the controller sends it; the SBC has no driver enable line of its own. Kept so that
	// DDA::Prepare needs no edits.
	(void)drive;
	(void)unconditional;
}

void MotionSystem::AddLinearSegments(size_t drive, uint32_t startTime, const MoveProfile& profile,
									 motioncalc_t steps, MovementFlags moveFlags) noexcept
{
	const motioncalc_t pressureAdvance =
		(moveFlags.isExtruder && !moveFlags.nonPrintingMove) ? config.pressureAdvanceClocks[drive] : 0;
	trackers[drive].AddMove(startTime, profile, steps, moveFlags, pressureAdvance);
}

void MotionSystem::AdvanceTrackers(uint32_t now) noexcept
{
	for (auto& tracker : trackers)
	{
		tracker.Advance(now);
	}
}

void MotionSystem::GetMotorPositions(int32_t *positions, size_t count) const noexcept
{
	for (size_t drive = 0; drive < count && drive < MaxAxesPlusExtruders; ++drive)
	{
		// Subtract the backlash correction already injected: it moved the motor, but it did not
		// move the axis, so reporting it would put the machine position out by the backlash.
		positions[drive] = trackers[drive].GetMotorPosition() - currentBacklashSteps[drive];
	}
}

void MotionSystem::SetMotorPositions(LogicalDrivesBitmap drives, const int32_t *positions, size_t count) noexcept
{
	for (size_t drive = 0; drive < count && drive < MaxAxesPlusExtruders; ++drive)
	{
		if (drives.IsBitSet(drive))
		{
			trackers[drive].SetMotorPosition(positions[drive]);

			// The axis is being told where it is, so whatever backlash was outstanding is no longer
			// meaningful - it described a position that has just been redefined.
			targetBacklashSteps[drive] = 0;
			currentBacklashSteps[drive] = 0;
		}
	}
}

bool MotionSystem::AreDrivesStopped(LogicalDrivesBitmap drives) const noexcept
{
	bool stopped = true;
	drives.Iterate([this, &stopped](unsigned int drive, unsigned int) noexcept
				   {
					   if (drive < MaxAxesPlusExtruders && trackers[drive].MotionPending())
					   {
						   stopped = false;
					   }
				   });
	return stopped;
}

void MotionSystem::CancelStepping() noexcept
{
	for (auto& tracker : trackers)
	{
		tracker.ClearMovementPending();
	}
}

void MotionSystem::AddPrepareHiccup() noexcept
{
	// Preparation did not finish before the move was due to start. Slip the whole movement timebase
	// rather than starting the move late: every board shares this delay, so their moves stay in
	// step with each other and only the print takes slightly longer.
	StepTimer::IncreaseMovementDelay(MoveTiming::HiccupTime);
}
