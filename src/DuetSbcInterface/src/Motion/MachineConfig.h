/*
 * MachineConfig.h
 *
 * The machine description the motion engine needs, as pushed down from DuetControlServer.
 *
 * This is the machine itself - how many drives there are, what a microstep of each is worth, which
 * board drives it, and what the kinematics says about it. It describes moves that are already
 * queued, so replacing it is only safe at standstill: a DDA holds endpoints in microsteps computed
 * under the old steps per mm, and Prepare turns them into driver steps by differencing against the
 * previous DDA's.
 *
 * The settings that can change mid-print are not here. Jerk limits, pressure advance, backlash,
 * nonlinear extrusion and input shaping travel on each move instead, so that changing one cannot
 * reach a move that is already queued and nothing has to stop. See
 * docs/devel/MOTION_CONFIG_ORDERING.md and Motion/MoveParams.h.
 *
 * gracePeriodMs is the exception that proves the rule: it is ring behaviour rather than anything a
 * move carries, and it is safe to replace at any moment.
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

#ifndef SRC_MOTION_MACHINECONFIG_H_
#define SRC_MOTION_MACHINECONFIG_H_

#include <Config/MachineLimits.h>

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

	// The drivers that move one axis. An axis with several drivers - a Z axis with three
	// leadscrews, say - moves all of them together.
	struct AxisDriversConfig
	{
		uint8_t numDrivers = 0;
		DriverId driverNumbers[maxDriversPerAxis];
	};

	struct MachineConfig
	{
		// --- Machine shape -------------------------------------------------------------------

		uint8_t numTotalAxes = 0;	// including axes that exist only in the kinematics
		uint8_t numExtruders = 0;

		uint8_t numRings = 1;		  // 1, or 2 for a second asynchronous movement system
		uint16_t numDdasPerRing = 40; // lookahead depth

		uint32_t gracePeriodMs = 10; // how long to let moves accumulate before starting one

		// --- Per-drive ------------------------------------------------------------------------

		float driveStepsPerMm[maxAxesPlusExtruders]{};

		// --- Driver mapping ------------------------------------------------------------------

		AxisDriversConfig axisDrivers[maxAxes];
		DriverId extruderDrivers[maxExtruders];

		// --- Kinematics results, evaluated by DCS ---------------------------------------------

		// Axes that wrap at 360 degrees, so a move may take the short way round.
		uint32_t continuousRotationAxes = 0; // AxesBitmap raw

		// For each axis, the other drives that must be energised to hold it. On a Cartesian machine
		// this is empty; on CoreXY moving X requires both motors to be enabled.
		uint32_t controllingDrives[maxAxes]{}; // AxesBitmap raw

		// --- Derived --------------------------------------------------------------------------

		[[nodiscard]] constexpr size_t FirstExtruderDrive() const noexcept
		{
			return maxAxesPlusExtruders - numExtruders;
		}

		[[nodiscard]] constexpr bool IsExtruder(size_t drive) const noexcept { return drive >= FirstExtruderDrive(); }
	};

	// ---------------------------------------------------------------------------------------------
	// Layout guarantees.
	//
	// DuetSbc_MotionConfigure memcpys the managed side's bytes straight into a MachineConfig, so this
	// struct is as much an ABI as LinkEvents.h and MoveParams.h are. It is not packed, because
	// driveStepsPerMm and its neighbours are read on the move-preparation path and misaligned float
	// arrays are not worth the few bytes saved.
	//
	// The gaps that leaves are the compiler's, and are not declared. The mirror -
	// DuetControlServer/Motion/Native/MachineConfig.cs - is a sequential struct of the same fields in
	// the same order, so the two compilers align them identically without being told to. What used to
	// be spelled out in padding fields is now asserted instead: the offsets below, and the same
	// numbers on the managed side.
	// ---------------------------------------------------------------------------------------------

	static_assert(sizeof(DriverId) == 2, "DriverId must be 2 bytes");
	static_assert(sizeof(AxisDriversConfig) == 1 + (2 * maxDriversPerAxis), "AxisDriversConfig must be tightly packed");

	static_assert(offsetof(MachineConfig, numDdasPerRing) == 4);
	static_assert(offsetof(MachineConfig, gracePeriodMs) == 8);
	static_assert(offsetof(MachineConfig, driveStepsPerMm) == 12);
	static_assert(offsetof(MachineConfig, axisDrivers) == 12 + (4 * maxAxesPlusExtruders));
	static_assert(offsetof(MachineConfig, continuousRotationAxes) % 4 == 0, "the bitmaps must stay 4-aligned");
	static_assert(offsetof(MachineConfig, controllingDrives) == 696);
	static_assert(sizeof(MachineConfig) == 816);
} // namespace Duet::Sbc::Motion

#endif /* SRC_MOTION_MACHINECONFIG_H_ */
