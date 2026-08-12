/*
 * MotionSystem.cpp - see MotionSystem.h.
 */

#include <cmath>
#include "MotionSystem.h"

#include <Movement/MoveTiming.h>
#include <Movement/StepTimer.h>
#include <Platform/Tasks.h>

#include <algorithm>
#include <cstdlib>

using Duet::Sbc::Motion::MotionSystem;

namespace
{
	// How much of the permanent arena to reserve. Sized from the worst case the ring can reach:
	// every DDA in both rings, plus a segment per drive per queued move. The firmware sizes its
	// equivalent from whatever RAM is left over; here memory is not the constraint, so this is
	// generous enough that exhaustion means a bug rather than a busy machine.
	constexpr size_t permanentArenaBytes = 4 * 1024 * 1024;
}

MotionSystem::MotionSystem() noexcept
{
	for (size_t drive = 0; drive < maxAxesPlusExtruders; ++drive)
	{
		m_trackers[drive].Init(drive);
	}
}

bool MotionSystem::Init() noexcept
{
	if (!Tasks::InitPermanentArena(permanentArenaBytes))
	{
		return false;
	}

	StepTimer::Init();
	for (size_t drive = 0; drive < maxAxesPlusExtruders; ++drive)
	{
		m_trackers[drive].Init(drive);
		m_targetBacklashSteps[drive] = 0;
		m_currentBacklashSteps[drive] = 0;
	}
	for (auto& d : m_lastMoveWasBackwards) { d = false; }
	return true;
}

void MotionSystem::SanitiseConfig(MotionConfig& config) noexcept
{
	// Axis and extruder counts. numTotalAxes and numExtruders together decide FirstExtruderDrive,
	// which is what splits the logical drive space; if their sum exceeds maxAxesPlusExtruders then
	// a drive is both an axis and an extruder, and LogicalDriveToExtruder returns an index past the
	// end of extruderDrivers. Extruders are what give way, because the axes are already placed.
	config.numTotalAxes = static_cast<uint8_t>(std::min<size_t>(config.numTotalAxes, maxAxes));
	config.numExtruders = static_cast<uint8_t>(std::min<size_t>(config.numExtruders, maxExtruders));
	config.numExtruders = static_cast<uint8_t>(
		std::min<size_t>(config.numExtruders, maxAxesPlusExtruders - config.numTotalAxes));
	config.numVisibleAxes = static_cast<uint8_t>(std::min(config.numVisibleAxes, config.numTotalAxes));

	// Rings. DDARing::Init clamps the depth as well, but doing it here keeps GetConfig() honest
	// about what was actually built.
	config.numRings = static_cast<uint8_t>(std::clamp<unsigned int>(config.numRings, 1, maxRings));
	config.numDdasPerRing = static_cast<uint16_t>(
		std::clamp<unsigned int>(config.numDdasPerRing, minDdasPerRing, maxDdasPerRing));

	// Driver mapping. numDrivers indexes driverNumbers[maxDriversPerAxis] in DDA::Prepare.
	for (AxisDriversConfig& axis : config.axisDrivers)
	{
		axis.numDrivers = static_cast<uint8_t>(std::min<size_t>(axis.numDrivers, maxDriversPerAxis));
	}
}

void MotionSystem::Configure(const MotionConfig& newConfig) noexcept
{
	m_config = newConfig;
	SanitiseConfig(m_config);
}

int32_t MotionSystem::ApplyBacklashCompensation(size_t drive, int32_t delta) noexcept
{
	if (drive >= maxAxes)
	{
		return delta;						// extruders have no backlash to take up
	}

	// A change of direction means the whole backlash has to be taken up again, in the new direction.
	const bool backwards = (delta < 0);
	int32_t& targetSteps = m_targetBacklashSteps[drive];
	if (backwards != m_lastMoveWasBackwards[drive])
	{
		m_lastMoveWasBackwards[drive] = backwards;
		const int32_t backlash = m_config.backlashSteps[drive];
		targetSteps += (backwards) ? -backlash : backlash;
	}

	int32_t& currentSteps = m_currentBacklashSteps[drive];
	const int32_t stepsDue = targetSteps - currentSteps;
	if (stepsDue != 0)
	{
		// Spread the correction over several moves rather than injecting it in one: a whole
		// backlash added to a short move would be a visible jolt. backlashCorrectionDistanceFactor
		// is how many times the correction the move must be before it is all taken at once.
		if ((uint32_t)labs(stepsDue) * m_config.backlashCorrectionDistanceFactor <= (uint32_t)labs(delta))
		{
			delta += stepsDue;
			currentSteps = targetSteps;
		}
		else
		{
			const auto maxAllowedSteps =
				(int32_t)max<uint32_t>((uint32_t)labs(delta) / m_config.backlashCorrectionDistanceFactor, 1u);
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
		(moveFlags.isExtruder && !moveFlags.nonPrintingMove) ? m_config.pressureAdvanceClocks[drive] : 0;
	m_trackers[drive].AddMove(startTime, profile, steps, moveFlags, pressureAdvance);
}

void MotionSystem::AdvanceTrackers(uint32_t now) noexcept
{
	for (auto& tracker : m_trackers)
	{
		tracker.Advance(now);
	}
}

void MotionSystem::GetMotorPositions(std::span<int32_t> positions) const noexcept
{
	const size_t count = std::min(positions.size(), maxAxesPlusExtruders);
	for (size_t drive = 0; drive < count; ++drive)
	{
		// Subtract the backlash correction already injected: it moved the motor, but it did not
		// move the axis, so reporting it would put the machine position out by the backlash.
		positions[drive] = m_trackers[drive].GetMotorPosition() - m_currentBacklashSteps[drive];
	}
}

void MotionSystem::GetLivePositions(std::span<int32_t> positions, uint32_t now) const noexcept
{
	const size_t count = std::min(positions.size(), maxAxesPlusExtruders);
	for (size_t drive = 0; drive < count; ++drive)
	{
		// Where the drive is *now*, not where the last retired segment left it. DriveTracker::Advance
		// deliberately leaves the stored position alone while a segment is still running, so reading
		// it would report a move as three jumps - one per phase of the trapezoid - instead of motion.
		// Rounded rather than truncated: the tracker carries a fraction of a step, and truncating it
		// would make a slow drive appear to lag by up to a whole step.
		const float live = m_trackers[drive].GetCurrentPosition(now);
		positions[drive] = (int32_t)lrintf(live) - m_currentBacklashSteps[drive];
	}
}

void MotionSystem::SetMotorPositions(LogicalDrivesBitmap drives, std::span<const int32_t> positions) noexcept
{
	const size_t count = std::min(positions.size(), maxAxesPlusExtruders);
	for (size_t drive = 0; drive < count; ++drive)
	{
		if (drives.IsBitSet(drive))
		{
			m_trackers[drive].SetMotorPosition(positions[drive]);

			// The axis is being told where it is, so whatever backlash was outstanding is no longer
			// meaningful - it described a position that has just been redefined.
			m_targetBacklashSteps[drive] = 0;
			m_currentBacklashSteps[drive] = 0;
		}
	}
}

bool MotionSystem::AreDrivesStopped(LogicalDrivesBitmap drives) const noexcept
{
	bool stopped = true;
	drives.Iterate([this, &stopped](unsigned int drive, unsigned int) noexcept
				   {
					   if (drive < maxAxesPlusExtruders && m_trackers[drive].MotionPending())
					   {
						   stopped = false;
					   }
				   });
	return stopped;
}

void MotionSystem::CancelStepping() noexcept
{
	for (auto& tracker : m_trackers)
	{
		tracker.ClearMovementPending();
	}
}

void MotionSystem::AddPrepareHiccup() noexcept
{
	// Preparation did not finish before the move was due to start. Slip the whole movement timebase
	// rather than starting the move late: every board shares this delay, so their moves stay in
	// step with each other and only the print takes slightly longer.
	StepTimer::IncreaseMovementDelay(MoveTiming::hiccupTime);
}
