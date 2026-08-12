/*
 * MotionSystem.h
 *
 * What replaces RepRapFirmware's Move class on the SBC.
 *
 * Move is the top of the firmware's motion engine: it owns the DDA rings, the per-drive state, the
 * kinematics, bed compensation, homing, the laser task and the step interrupt. Almost none of that
 * belongs here. Kinematics, compensation and homing moved to DuetControlServer, which is where the
 * moves are now built; there is no step interrupt because there are no local drivers.
 *
 * What is left is the part the imported DDA and DDARing sources still need: somewhere to ask how
 * many steps per mm a drive has, which board drives it, how much backlash to take up, and somewhere
 * to put the segments that Prepare produces. So this class is mostly the machine description
 * (MotionConfig, pushed down by DCS) plus the array of DriveTrackers.
 *
 * It is reached through the `reprap` facade in Compat/Platform/RepRap.h, so that the imported code
 * keeps its reprap.GetMove() call sites as they are written upstream.
 */

#ifndef SRC_MOTION_MOTIONSYSTEM_H_
#define SRC_MOTION_MOTIONSYSTEM_H_

#include <Motion/DriveTracker.h>
#include <Motion/MotionConfig.h>
#include <Motion/MoveProfile.h>
#include <Motion/ScheduleMoveBuilder.h>

#include <span>

namespace Duet::Sbc::Motion
{
	class MotionSystem
	{
	public:
		MotionSystem() noexcept;

		// Reserve the permanent arena and reset every drive. Call once before use.
		bool Init() noexcept;

		// Replace the machine description. Only safe when no move is in flight - DCS holds movement
		// locked while it reconfigures, exactly as the firmware requires for M92 and friends.
		//
		// The configuration arrives over the CApi from a separate process, so it is validated rather
		// than trusted: counts are clamped to what the fixed-size arrays below can address. See
		// SanitiseConfig for what is enforced.
		void Configure(const MotionConfig& newConfig) noexcept;

		// Clamp a configuration to the limits this build was compiled with. Exposed for testing;
		// Configure applies it to everything that comes in.
		static void SanitiseConfig(MotionConfig& config) noexcept;

		[[nodiscard]] const MotionConfig& GetConfig() const noexcept { return m_config; }

		// --- Accessors used by the imported DDA / DDARing sources ------------------------------
		//
		// Names match the firmware's Move members, so those call sites read as they do upstream.

		[[nodiscard]] float DriveStepsPerMm(size_t drive) const noexcept { return m_config.driveStepsPerMm[drive]; }
		[[nodiscard]] uint32_t GetJerkPolicy() const noexcept { return m_config.jerkPolicy; }
		[[nodiscard]] float GetMaxInstantDv(size_t drive) const noexcept { return m_config.instantDvs[drive]; }
		[[nodiscard]] float GetPrintingInstantDv(size_t drive) const noexcept { return m_config.printingInstantDvs[drive]; }
		[[nodiscard]] float GetPressureAdvanceK0ClocksForLogicalDrive(size_t drive) const noexcept
		{
			return m_config.pressureAdvanceClocks[drive];
		}

		[[nodiscard]] const AxisDriversConfig& GetAxisDriversConfig(size_t axis) const noexcept
		{
			static constexpr AxisDriversConfig noDrivers{};
			return (axis < maxAxes) ? m_config.axisDrivers[axis] : noDrivers;
		}

		// An out-of-range extruder answers with a default DriverId, whose board address is
		// noCanAddress: IsRemote() is false, so the caller drops the movement rather than addressing
		// it to whichever board the out-of-range read happened to land on.
		[[nodiscard]] DriverId GetExtruderDriver(size_t extruder) const noexcept
		{
			return (extruder < maxExtruders) ? m_config.extruderDrivers[extruder] : DriverId{};
		}

		// Kinematics answers that DCS evaluated for us; see MotionConfig.
		[[nodiscard]] bool IsContinuousRotationAxis(size_t axis) const noexcept
		{
			return AxesBitmap(m_config.continuousRotationAxes).IsBitSet(axis);
		}

		[[nodiscard]] AxesBitmap GetControllingDrives(size_t axis) const noexcept
		{
			return AxesBitmap((axis < maxAxes) ? m_config.controllingDrives[axis] : 0);
		}

		// Extend a reversing move so that the backlash is taken up. Not const: it tracks how much of
		// the correction has been applied, because spreading it over several moves is the point -
		// injecting it all at once would show up as a visible jolt.
		[[nodiscard]] int32_t ApplyBacklashCompensation(size_t drive, int32_t delta) noexcept;

		// No-op: the drivers are on other boards and are enabled over CAN, not from here.
		void EnableDrivers(size_t drive, bool unconditional) noexcept;

		// How long the boards' input shaper spreads a move over. Zero while shaping is off; see
		// MotionConfig::shapingTimeClocks for why this is not simply absent.
		[[nodiscard]] uint32_t GetShapingTimeClocks() const noexcept { return m_config.shapingTimeClocks; }

		// Where prepared moves go out to the controller. Owned here because it is per-machine state
		// with the same lifetime as the drive trackers, and because the CanMotion shim in front of
		// it has to find it from somewhere without a second global.
		[[nodiscard]] ScheduleMoveBuilder& GetScheduleMoveBuilder() noexcept { return m_scheduleMoveBuilder; }

		// --- Per-drive motion -----------------------------------------------------------------

		[[nodiscard]] DriveTracker& GetDriveTracker(size_t drive) noexcept { return m_trackers[drive]; }

		// The logical drive a CAN-connected driver belongs to, or maxAxesPlusExtruders if none does.
		//
		// The controller only ever knows drivers, so anything it reports back has to be mapped
		// through the configuration that placed them before it can be applied here
		[[nodiscard]] size_t GetLogicalDriveForDriver(DriverId driver) const noexcept
		{
			for (size_t axis = 0; axis < maxAxes; ++axis)
			{
				const AxisDriversConfig& config = m_config.axisDrivers[axis];
				for (size_t i = 0; i < config.numDrivers; ++i)
				{
					if (config.driverNumbers[i] == driver)
					{
						return axis;
					}
				}
			}

			for (size_t extruder = 0; extruder < maxExtruders; ++extruder)
			{
				if (m_config.extruderDrivers[extruder] == driver)
				{
					return ExtruderToLogicalDrive(extruder);
				}
			}
			return maxAxesPlusExtruders;
		}

		// How many drivers a logical drive has. An extruder has the one driver that is not listed in
		// axisDrivers, so it answers 1 rather than 0
		[[nodiscard]] size_t GetNumDriversForDrive(size_t drive) const noexcept
		{
			return (drive < maxAxes) ? m_config.axisDrivers[drive].numDrivers : 1;
		}

		// Where a driver sits in the list of the drive's drivers, which is also the endstop switch it
		// watches when the axis has a switch per driver. RepRapFirmware pairs port i with driver i
		// the same way
		[[nodiscard]] size_t GetDriverIndexInDrive(size_t drive, DriverId driver) const noexcept
		{
			if (drive < maxAxes)
			{
				const AxisDriversConfig& config = m_config.axisDrivers[drive];
				for (size_t i = 0; i < config.numDrivers; ++i)
				{
					if (config.driverNumbers[i] == driver)
					{
						return i;
					}
				}
			}
			return 0;
		}

		// Hand one drive's share of a prepared move to its tracker. This is what DDA::Prepare calls
		// in place of the firmware's Move::AddLinearSegments.
		void AddLinearSegments(size_t drive, uint32_t startTime, const MoveProfile& profile,
							   motioncalc_t steps, MovementFlags moveFlags) noexcept;

		// Bring every drive's position up to `now`. Called once per pass of the motion loop.
		void AdvanceTrackers(uint32_t now) noexcept;

		// Motor positions in microsteps, as of the last completed segment. Fills as much of
		// `positions` as there are drives to report.
		void GetMotorPositions(std::span<int32_t> positions) const noexcept;

		// Where the drives are at the given instant, interpolating within the segment each is running.
		// GetMotorPositions reports the commanded position instead, which is what the planner has to
		// resynchronise against; this is what a live position display wants.
		void GetLivePositions(std::span<int32_t> positions, uint32_t now) const noexcept;

		// Force motor positions, for homing and for resynchronising after a move that was cut short.
		// Only the drives named in `drives` are taken, and only as far as `positions` reaches.
		void SetMotorPositions(LogicalDrivesBitmap drives, std::span<const int32_t> positions) noexcept;

		// True once every drive named in `drives` has no pending motion. DDA::HasExpired uses this
		// to decide when a move that checks endstops has finished.
		[[nodiscard]] bool AreDrivesStopped(LogicalDrivesBitmap drives) const noexcept;

		// Abandon pending motion on every drive, without moving the reported positions. For an
		// emergency stop, where the boards drop their queued moves too.
		void CancelStepping() noexcept;

		// Record that preparation could not keep up and everything must slip. Reported to the
		// controller so it can pass the delay on to the expansion boards.
		void AddPrepareHiccup() noexcept;

	private:
		MotionConfig m_config;
		DriveTracker m_trackers[maxAxesPlusExtruders];
		ScheduleMoveBuilder m_scheduleMoveBuilder;

		// Backlash compensation state, as in the firmware's Move. `target` is the correction the
		// current direction calls for, `current` how much of it has been injected so far; the
		// difference is spread over subsequent moves.
		//
		// The firmware packs the direction flags into a LogicalDrivesBitmap, where saving 124 bytes
		// is worth it. That is not a trade this side needs to make, and it is worth avoiding:
		// written as a bitmap, GCC 12.2 miscompiles the `backwards != IsBitSet(drive)` test below at
		// -O1 and above, dropping the `backwards` term so that the correction is never applied.
		// Clang compiles the same source correctly and UBSan reports nothing. See the test in
		// tests/MotionSystemTests.cpp, which catches it.
		bool m_lastMoveWasBackwards[maxAxesPlusExtruders]{};
		int32_t m_targetBacklashSteps[maxAxesPlusExtruders]{};
		int32_t m_currentBacklashSteps[maxAxesPlusExtruders]{};
	};
}

#endif /* SRC_MOTION_MOTIONSYSTEM_H_ */
