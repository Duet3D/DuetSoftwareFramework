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
 * What is left is the part DDA and DDARing still need: somewhere to ask how many steps per mm a
 * drive has, which board drives it, how much backlash to take up, and somewhere to put the segments
 * that Prepare produces. So this class is mostly the machine description (MachineConfig, pushed down
 * by DCS) plus the array of DriveTrackers.
 *
 * A DDARing is given one of these when it is built, and the DDAs in it reach it through the ring.
 */

#ifndef SRC_MOTION_MOTIONSYSTEM_H_
#define SRC_MOTION_MOTIONSYSTEM_H_

#include <Motion/DriveTracker.h>
#include <Motion/MachineConfig.h>
#include <Motion/MoveProfile.h>
#include <Motion/ScheduleMoveBuilder.h>

#include <span>

namespace Duet::Sbc::Motion
{
	class MotionSystem
	{
	  public:
		MotionSystem() noexcept;

		// Give up this system's share of the permanent arena. The DDA ring and the segments were
		// allocated from it and are gone with it, which is why this is tied to the object's life
		// rather than being something a caller has to remember
		~MotionSystem() noexcept;

		MotionSystem(const MotionSystem&) = delete;
		MotionSystem& operator=(const MotionSystem&) = delete;

		// Reserve the permanent arena and reset every drive. Call once before use.
		bool Init() noexcept;

		// Replace the machine description. Only safe when no move is in flight - DCS holds movement
		// locked while it reconfigures, exactly as the firmware requires for M92 and friends.
		//
		// The configuration arrives over the CApi from a separate process, so it is validated rather
		// than trusted: counts are clamped to what the fixed-size arrays below can address. See
		// SanitiseConfig for what is enforced.
		void Configure(const MachineConfig& newConfig) noexcept;

		// Clamp a configuration to the limits this build was compiled with. Exposed for testing;
		// Configure applies it to everything that comes in.
		static void SanitiseConfig(MachineConfig& config) noexcept;

		[[nodiscard]] const MachineConfig& GetConfig() const noexcept { return m_config; }

		// --- Machine shape ---------------------------------------------------------------------

		[[nodiscard]] size_t GetTotalAxes() const noexcept { return m_config.numTotalAxes; }
		[[nodiscard]] size_t GetNumExtruders() const noexcept { return m_config.numExtruders; }

		// --- Debug topics ----------------------------------------------------------------------
		//
		// One bitmap per topic, as M111 sets them in the firmware. Nothing sets them yet, so the
		// branches that read them are compiled but not taken; see the note on SetDebugFlags.

		[[nodiscard]] AxesBitmap GetDebugFlags(Module module) const noexcept
		{
			return AxesBitmap((module < Module::Num) ? m_debugFlags[(unsigned int)module] : 0);
		}

		[[nodiscard]] bool IsDebugEnabled(Module module) const noexcept { return GetDebugFlags(module).IsNonEmpty(); }

		// Set one topic's flags. There is no caller yet: M111 is not ported, and until it is, the
		// diagnostics behind these branches cannot be switched on. Kept so that a topic can be
		// enabled from one place once it is, rather than the branches being deleted and rewritten.
		void SetDebugFlags(Module module, uint32_t flags) noexcept
		{
			if (module < Module::Num)
			{
				m_debugFlags[(unsigned int)module] = flags;
			}
		}

		// --- Per-drive configuration -----------------------------------------------------------
		//
		// Names match the firmware's Move members, so the planning code reads as it does upstream.

		[[nodiscard]] float DriveStepsPerMm(size_t drive) const noexcept { return m_config.driveStepsPerMm[drive]; }
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

		// Kinematics answers that DCS evaluated for us; see MachineConfig.
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
		//
		// `backlashSteps` and `distanceFactor` come from the move rather than from the configuration
		// here, so that changing them cannot reach a move that is already queued. What stays here is
		// the accumulator, which is machine state rather than configuration.
		[[nodiscard]] int32_t ApplyBacklashCompensation(size_t drive, int32_t delta, int32_t backlashSteps,
														uint32_t distanceFactor) noexcept;

		// Where prepared moves go out to the controller. Owned here because it is per-machine state
		// with the same lifetime as the drive trackers.
		[[nodiscard]] ScheduleMoveBuilder& GetScheduleMoveBuilder() noexcept { return m_scheduleMoveBuilder; }
		[[nodiscard]] const ScheduleMoveBuilder& GetScheduleMoveBuilder() const noexcept
		{
			return m_scheduleMoveBuilder;
		}

		// --- Per-drive motion -----------------------------------------------------------------

		[[nodiscard]] DriveTracker& GetDriveTracker(size_t drive) noexcept { return m_trackers[drive]; }
		[[nodiscard]] const DriveTracker& GetDriveTracker(size_t drive) const noexcept { return m_trackers[drive]; }

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
		//
		// `pressureAdvanceClocks` is the move's own, so that changing it cannot reach a move that is
		// already queued. Ignored for anything that is not an extruder doing a printing move.
		void AddLinearSegments(size_t drive,
							   uint32_t startTime,
							   const MoveProfile& profile,
							   motioncalc_t steps,
							   MovementFlags moveFlags,
							   motioncalc_t pressureAdvanceClocks) noexcept;

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
		static void AddPrepareHiccup() noexcept;

	  private:
		// Whether Init reserved the arena, so that a system that never started does not give up a
		// reservation it never took
		bool m_reservedArena = false;

		MachineConfig m_config;
		uint32_t m_debugFlags[(unsigned int)Module::Num]{};
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
} // namespace Duet::Sbc::Motion

#endif /* SRC_MOTION_MOTIONSYSTEM_H_ */
