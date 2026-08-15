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
	// How many movement systems this build supports, i.e. how many DDA rings MotionService builds.
	// A second one exists for asynchronous moves whether or not anything uses it.
	inline constexpr unsigned int maxRings = 2;

	// Bounds on the lookahead depth. The minimum is what makes a ring a ring: CanAddMove refuses the
	// last free slot and DDA::Prepare reads the previous DDA's endpoints, so anything shorter has
	// each move planned against the slot it is about to overwrite. The maximum only exists to keep a
	// nonsense configuration from allocating until the process dies.
	inline constexpr unsigned int minDdasPerRing = 3;
	inline constexpr unsigned int maxDdasPerRing = 1000;

	// M592 nonlinear extrusion correction, per extruder. The commanded extrusion is scaled by
	// 1 + min((a + b*v) * v, limit), where v is the average extrusion speed of the move in mm/sec.
	struct NonlinearExtrusion
	{
		float a = 0.0;
		float b = 0.0;
		float limit = 0.2;			// RepRapFirmware's DefaultNonlinearExtrusionLimit
	};

	// The drivers that move one axis. An axis with several drivers - a Z axis with three
	// leadscrews, say - moves all of them together.
	struct AxisDriversConfig
	{
		uint8_t numDrivers = 0;
		DriverId driverNumbers[maxDriversPerAxis];
	};

	struct MotionConfig
	{
		// --- Machine shape -------------------------------------------------------------------

		uint8_t numVisibleAxes = 0;			// axes the user can refer to
		uint8_t numTotalAxes = 0;			// including axes that exist only in the kinematics
		uint8_t numExtruders = 0;

		uint8_t numRings = 1;				// 1, or 2 for a second asynchronous movement system
		uint16_t numDdasPerRing = 40;		// lookahead depth
		uint16_t padding = 0;				// explicit, so the layout is the same on both sides

		uint32_t gracePeriodMs = 10;		// how long to let moves accumulate before starting one

		// --- Per-drive limits ----------------------------------------------------------------

		float driveStepsPerMm[maxAxesPlusExtruders]{};

		// Instantaneous speed change a drive tolerates at a junction between moves, in mm per step
		// clock. The printing variant applies when both moves are extruding, where a lower limit
		// avoids visible artefacts.
		float instantDvs[maxAxesPlusExtruders]{};
		float printingInstantDvs[maxAxesPlusExtruders]{};

		// Pressure advance time constant per drive, in step clocks. Zero for anything that is not
		// an extruder.
		float pressureAdvanceClocks[maxAxesPlusExtruders]{};

		// Backlash to take up when a drive reverses, in microsteps, and the distance over which to
		// spread it (as a multiple of the backlash itself).
		int32_t backlashSteps[maxAxes]{};
		uint32_t backlashCorrectionDistanceFactor = 10;

		// --- Junction policy -----------------------------------------------------------------

		// M566 P parameter. 0 allows a junction speed only between moves of the same kind;
		// higher values allow melding more aggressively. Read by DDA::DoLookahead.
		uint32_t jerkPolicy = 0;

		// --- Driver mapping ------------------------------------------------------------------

		AxisDriversConfig axisDrivers[maxAxes];
		DriverId extruderDrivers[maxExtruders];
		uint16_t padding2 = 0;							// explicit, to realign what follows

		// --- Kinematics results, evaluated by DCS ---------------------------------------------

		// Axes that wrap at 360 degrees, so a move may take the short way round.
		uint32_t continuousRotationAxes = 0;			// AxesBitmap raw

		// For each axis, the other drives that must be energised to hold it. On a Cartesian machine
		// this is empty; on CoreXY moving X requires both motors to be enabled.
		uint32_t controllingDrives[maxAxes]{};			// AxesBitmap raw

		// --- Input shaping --------------------------------------------------------------------

		// How long the expansion boards' input shaper spreads a move over, in step clocks.
		//
		// Nothing here shapes anything - AxisShaper was removed from this project and shaping now
		// happens on the boards. But the boards' motion is the shaped profile while the segments
		// built here are the unshaped one, so during acceleration the tracked position leads the
		// real one by up to this long. Endpoints still agree exactly. Zero until DCS enables
		// shaping on the boards.
		uint32_t shapingTimeClocks = 0;

		// --- Extrusion correction --------------------------------------------------------------

		// Appended rather than placed beside the other per-extruder values: everything below
		// gracePeriodMs has an asserted offset that the C# mirror hardcodes, and there is nothing to
		// be gained by moving them all. Both sides are built from this repository together, so the
		// order carries no compatibility obligation of its own.
		NonlinearExtrusion nonlinearExtrusion[maxExtruders];

		// --- Derived --------------------------------------------------------------------------

		[[nodiscard]] constexpr size_t FirstExtruderDrive() const noexcept
		{
			return maxAxesPlusExtruders - numExtruders;
		}

		[[nodiscard]] constexpr bool IsExtruder(size_t drive) const noexcept
		{
			return drive >= FirstExtruderDrive();
		}
	};

	// ---------------------------------------------------------------------------------------------
	// Layout guarantees.
	//
	// DuetSbc_MotionConfigure memcpys the managed side's bytes straight into a MotionConfig, so this
	// struct is as much an ABI as LinkEvents.h and MoveParams.h are. It is not packed, because
	// driveStepsPerMm and its neighbours are read on the move-preparation path and misaligned float
	// arrays are not worth the few bytes saved; instead the padding the compiler would have inserted
	// is declared, so that the C# mirror can reproduce it rather than having to guess at it.
	//
	// The mirror is DuetControlServer/Motion/Native/MotionConfig.cs. Its layout test asserts the same
	// numbers as these do.
	// ---------------------------------------------------------------------------------------------

	static_assert(sizeof(DriverId) == 2, "DriverId must be 2 bytes");
	static_assert(sizeof(AxisDriversConfig) == 1 + (2 * maxDriversPerAxis), "AxisDriversConfig must be tightly packed");

	static_assert(offsetof(MotionConfig, gracePeriodMs) == 8);
	static_assert(offsetof(MotionConfig, driveStepsPerMm) == 12);
	static_assert(offsetof(MotionConfig, instantDvs) == 12 + (4 * maxAxesPlusExtruders));
	static_assert(offsetof(MotionConfig, backlashSteps) == 12 + (16 * maxAxesPlusExtruders));
	static_assert(offsetof(MotionConfig, axisDrivers) == 20 + (16 * maxAxesPlusExtruders) + (4 * maxAxes));
	static_assert(offsetof(MotionConfig, continuousRotationAxes) % 4 == 0, "the bitmaps must stay 4-aligned");
	static_assert(sizeof(NonlinearExtrusion) == 12, "NonlinearExtrusion is three floats with no padding");
	static_assert(offsetof(MotionConfig, nonlinearExtrusion) % 4 == 0, "the coefficients must stay 4-aligned");
	static_assert(sizeof(MotionConfig) % 4 == 0, "no tail padding beyond what is declared");
}

#endif /* SRC_MOTION_MOTIONCONFIG_H_ */
