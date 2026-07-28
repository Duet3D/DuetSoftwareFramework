/*
 * MotionConfig.h
 *
 * The machine description the motion engine needs, as pushed down from DuetControlServer.
 *
 * DCS owns configuration: it parses M92, M201, M203, M566, M425, M569, M584 and it owns the
 * kinematics. This is the subset of the result that the code on this side actually reads while
 * planning and preparing moves, which the imported RepRapFirmware sources reach for through
 * reprap.GetMove() and reprap.GetGCodes().
 *
 * Two entries are worth explaining because they are not configuration in the firmware's sense, but
 * kinematics results: continuousRotationAxes and controllingDrives. DDA::Prepare needs to know
 * whether an axis can take a short cut across 180 degrees, and which other motors have to be
 * energised to hold an axis in place on a CoreXY-like machine. In the firmware it asks the
 * Kinematics object; here that object lives in C#, so DCS evaluates both once when the kinematics
 * changes and sends the answers down.
 *
 * Everything is in the firmware's internal units - mm and step clocks - not the user-facing units,
 * so DCS converts before sending. Doing it the other way round would put the conversion on the
 * motion path.
 */

#ifndef SRC_MOTION_MOTIONCONFIG_H_
#define SRC_MOTION_MOTIONCONFIG_H_

#include <RepRapFirmware.h>

namespace Duet::Sbc::Motion
{
	// The drivers that move one axis. An axis with several drivers - a Z axis with three
	// leadscrews, say - moves all of them together.
	struct AxisDriversConfig
	{
		uint8_t numDrivers = 0;
		DriverId driverNumbers[MaxDriversPerAxis];
	};

	struct MotionConfig
	{
		// --- Machine shape -------------------------------------------------------------------

		uint8_t numVisibleAxes = 0;			// axes the user can refer to
		uint8_t numTotalAxes = 0;			// including axes that exist only in the kinematics
		uint8_t numExtruders = 0;

		uint8_t numRings = 1;				// 1, or 2 for a second asynchronous movement system
		uint16_t numDdasPerRing = 40;		// lookahead depth

		uint32_t gracePeriodMs = 10;		// how long to let moves accumulate before starting one

		// --- Per-drive limits ----------------------------------------------------------------

		float driveStepsPerMm[MaxAxesPlusExtruders]{};

		// Instantaneous speed change a drive tolerates at a junction between moves, in mm per step
		// clock. The printing variant applies when both moves are extruding, where a lower limit
		// avoids visible artefacts.
		float instantDvs[MaxAxesPlusExtruders]{};
		float printingInstantDvs[MaxAxesPlusExtruders]{};

		// Pressure advance time constant per drive, in step clocks. Zero for anything that is not
		// an extruder.
		float pressureAdvanceClocks[MaxAxesPlusExtruders]{};

		// Backlash to take up when a drive reverses, in microsteps, and the distance over which to
		// spread it (as a multiple of the backlash itself).
		int32_t backlashSteps[MaxAxes]{};
		uint32_t backlashCorrectionDistanceFactor = 10;

		// --- Junction policy -----------------------------------------------------------------

		// M566 P parameter. 0 allows a junction speed only between moves of the same kind;
		// higher values allow melding more aggressively. Read by DDA::DoLookahead.
		uint32_t jerkPolicy = 0;

		// --- Driver mapping ------------------------------------------------------------------

		AxisDriversConfig axisDrivers[MaxAxes];
		DriverId extruderDrivers[MaxExtruders];

		// --- Kinematics results, evaluated by DCS ---------------------------------------------

		// Axes that wrap at 360 degrees, so a move may take the short way round.
		uint32_t continuousRotationAxes = 0;			// AxesBitmap raw

		// For each axis, the other drives that must be energised to hold it. On a Cartesian machine
		// this is empty; on CoreXY moving X requires both motors to be enabled.
		uint32_t controllingDrives[MaxAxes]{};			// AxesBitmap raw

		// --- Input shaping --------------------------------------------------------------------

		// How long the expansion boards' input shaper spreads a move over, in step clocks.
		//
		// Nothing here shapes anything - AxisShaper was removed from this project and shaping now
		// happens on the boards. But the boards' motion is the shaped profile while the segments
		// built here are the unshaped one, so during acceleration the tracked position leads the
		// real one by up to this long. Endpoints still agree exactly. Zero until DCS enables
		// shaping on the boards.
		uint32_t shapingTimeClocks = 0;

		// --- Derived --------------------------------------------------------------------------

		[[nodiscard]] constexpr size_t FirstExtruderDrive() const noexcept
		{
			return MaxAxesPlusExtruders - numExtruders;
		}

		[[nodiscard]] constexpr bool IsExtruder(size_t drive) const noexcept
		{
			return drive >= FirstExtruderDrive();
		}
	};
}

#endif /* SRC_MOTION_MOTIONCONFIG_H_ */
