// MotionSystem is mostly a holder for the machine description DuetControlServer pushes down, but it
// does carry one piece of real logic: backlash compensation.
//
// Backlash is the slack a drive takes up when it reverses, during which the motor turns and the
// axis does not. The correction is extra motor steps, deliberately spread over several moves rather
// than injected at once, because a whole backlash added to a short move is a visible jolt. That
// makes it stateful - how much is still owed carries between moves - which is what these tests are
// about, along with the consequence that the reported position must exclude it.

#include "TestSupport.h"

#include <Motion/MotionSystem.h>
#include <Motion/MotionSystem.h>

#include <cstdint>

using Duet::Sbc::Motion::MachineConfig;
using Duet::Sbc::Motion::MotionSystem;
using Duet::Sbc::Motion::MoveProfile;

namespace
{

	// A three-axis, one-extruder Cartesian machine with one driver per axis.
	// The backlash values a test wants; a move carries them, so FreshSystem records them for the
	// helper below rather than the configuration holding them
	int32_t theBacklashSteps = 0;
	uint32_t theDistanceFactor = 1;

	MachineConfig BasicConfig(int32_t backlash = 0, uint32_t distanceFactor = 10) noexcept
	{
		theBacklashSteps = backlash;
		theDistanceFactor = distanceFactor;
		MachineConfig c;
		c.numTotalAxes = 3;
		c.numExtruders = 1;

		for (size_t axis = 0; axis < 3; ++axis)
		{
			c.driveStepsPerMm[axis] = 80.0f;
					c.axisDrivers[axis].numDrivers = 1;
			c.axisDrivers[axis].driverNumbers[0] = DriverId((CanAddress)1, (uint8_t)axis);
			}

		const size_t extruderDrive = maxAxesPlusExtruders - 1;
		c.driveStepsPerMm[extruderDrive] = 400.0f;
		c.extruderDrivers[0] = DriverId((CanAddress)1, 3);
		return c;
	}

	MotionSystem theMove;

	MotionSystem& FreshSystem(const MachineConfig& config) noexcept
	{
		MotionSystem& move = theMove;
		(void)move.Init();
		move.Configure(config);
		return move;
	}

	int32_t ApplyBacklash(MotionSystem& move, size_t drive, int32_t delta) noexcept
	{
		// An extruder carries no backlash, exactly as DuetControlServer would fill the move
		const int32_t steps = (drive < maxAxes) ? theBacklashSteps : 0;
		return move.ApplyBacklashCompensation(drive, delta, steps, theDistanceFactor);
	}
}

// With no backlash configured, deltas pass through untouched. Worth stating explicitly: this is the
// common case, and it must not cost anything or perturb anything.
static void TestNoBacklashLeavesDeltasAlone()
{
	MotionSystem& move = FreshSystem(BasicConfig(0));

	CHECK(ApplyBacklash(move, xAxis, 1000) == 1000, "forward move unchanged");
	CHECK(ApplyBacklash(move, xAxis, -1000) == -1000, "reverse move unchanged");
	CHECK(ApplyBacklash(move, xAxis, 1000) == 1000, "and back again");
}

// Reversing direction calls for the whole backlash, and a move long enough to absorb it takes it
// all in one go.
static void TestReversalInjectsBacklash()
{
	MotionSystem& move = FreshSystem(BasicConfig(20, 10));

	// Drives start out recorded as moving forwards, so a forward move is not a reversal and needs
	// no correction. Only a change of direction does.
	CHECK(ApplyBacklash(move, xAxis, 1000) == 1000, "the first forward move needs no correction");
	CHECK(ApplyBacklash(move, xAxis, 1000) == 1000, "nor does continuing forwards");

	CHECK(ApplyBacklash(move, xAxis, -1000) == -1020, "reversing takes up the backlash");
	CHECK(ApplyBacklash(move, xAxis, -1000) == -1000, "continuing backwards needs no more");

	// Reversing back swings the correction from -20 to 0, so this move owes 20 the other way.
	CHECK(ApplyBacklash(move, xAxis, 1000) == 1020, "and reversing again takes it up the other way");
}

// A move too short to hide the correction gets only a share of it, and the rest is carried.
static void TestShortMovesSpreadTheCorrection()
{
	constexpr int32_t backlash = 100;
	constexpr uint32_t distanceFactor = 10;
	MotionSystem& move = FreshSystem(BasicConfig(backlash, distanceFactor));

	// Establish a direction, then reverse so that a correction is owed.
	CHECK(ApplyBacklash(move, xAxis, 200) == 200, "no correction until the drive reverses");

	// A 200-step move cannot absorb 100 steps of correction at a factor of 10: it may take at most
	// 200/10 = 20. So the move becomes 220 and 80 steps remain owed.
	CHECK(ApplyBacklash(move, xAxis, -200) == -220, "a short move takes only its share");
	CHECK(ApplyBacklash(move, xAxis, -200) == -220, "the next one takes another share");

	// After enough short moves the whole correction has been delivered and moves return to normal.
	int32_t delta = 0;
	for (unsigned int i = 0; i < 10; ++i)
	{
		delta = ApplyBacklash(move, xAxis, -200);
	}
	CHECK(delta == -200, "once the correction is delivered, moves are unmodified again");

	// A long move takes whatever is outstanding in one go instead.
	MotionSystem& other = FreshSystem(BasicConfig(backlash, distanceFactor));
	CHECK(ApplyBacklash(other, xAxis, 5000) == 5000, "no correction until it reverses");
	CHECK(ApplyBacklash(other, xAxis, -5000) == -5100, "a long move absorbs the whole correction");
}

// Backlash steps move the motor but not the axis, so they must not appear in the reported position.
// Otherwise every direction reversal would shift the machine position by the backlash.
static void TestReportedPositionExcludesBacklash()
{
	MotionSystem& move = FreshSystem(BasicConfig(20, 10));

	int32_t positions[maxAxesPlusExtruders] = {};
	move.GetMotorPositions(positions);
	CHECK(positions[xAxis] == 0, "starts at zero");

	// Establish a direction, then reverse so that a correction is injected.
	(void)ApplyBacklash(move, xAxis, 1000);
	const int32_t compensated = ApplyBacklash(move, xAxis, -1000);
	CHECK(compensated == -1020, "the reversing delta carries 20 steps of correction");

	// Run both moves through a drive so the tracker holds what the motor really did: 1000 forwards,
	// then 1020 back, i.e. -20 net motor steps for zero net axis movement.
	MoveProfile profile;
	profile.accelClocks = 100;
	profile.steadyClocks = 100;
	profile.decelClocks = 100;
	profile.totalDistance = 3;
	profile.accelDistance = 1;
	profile.decelStartDistance = 2;

	MovementFlags flags{};
	flags.Clear();
	move.AddLinearSegments(xAxis, 0, profile, (motioncalc_t)1000.0, flags, 0);
	move.AddLinearSegments(xAxis, profile.TotalClocks(), profile, (motioncalc_t)compensated, flags, 0);
	move.AdvanceTrackers(2 * profile.TotalClocks());

	// Within a step: the two moves cover different step counts over the same profile, so the
	// fraction carried between segments does not cancel exactly. That is the tracker working as
	// designed - see DriveTrackerTests - not slack in what is being asserted here.
	CHECK_NEAR(move.GetDriveTracker(xAxis).GetMotorPosition(), -20.0, 1.0,
			   "the motor really did move by the correction");

	move.GetMotorPositions(positions);
	CHECK_NEAR(positions[xAxis], 0.0, 1.0, "but the reported axis position is back where it started");
}

// Extruders have no backlash: the concept does not apply to filament, and applying it would corrupt
// the extrusion amount.
static void TestExtrudersAreNotCompensated()
{
	const MachineConfig config = BasicConfig(50, 10);
	MotionSystem& move = FreshSystem(config);

	const size_t extruderDrive = maxAxesPlusExtruders - 1;
	CHECK(ApplyBacklash(move, extruderDrive, 500) == 500, "extruder deltas are untouched");
	CHECK(ApplyBacklash(move, extruderDrive, -500) == -500, "in both directions");
}

// AreDrivesStopped is how a move that checks endstops knows it has finished, so it has to consider
// only the drives it is asked about.
static void TestAreDrivesStopped()
{
	MotionSystem& move = FreshSystem(BasicConfig());

	const LogicalDrivesBitmap all = LogicalDrivesBitmap::MakeLowestNBits(4);
	CHECK(move.AreDrivesStopped(all), "everything is stopped initially");

	MoveProfile profile;
	profile.accelClocks = 1000;
	profile.steadyClocks = 1000;
	profile.decelClocks = 1000;
	profile.totalDistance = 3;
	profile.accelDistance = 1;
	profile.decelStartDistance = 2;

	MovementFlags flags{};
	flags.Clear();
	move.AddLinearSegments(xAxis, 0, profile, (motioncalc_t)300.0, flags, 0);

	CHECK(!move.AreDrivesStopped(all), "not stopped while X has pending motion");
	CHECK(move.AreDrivesStopped(LogicalDrivesBitmap::MakeFromBits(1, 2)), "but the other axes are");

	move.AdvanceTrackers(profile.TotalClocks());
	CHECK(move.AreDrivesStopped(all), "stopped again once the move has been retired");

	int32_t positions[maxAxesPlusExtruders] = {};
	move.GetMotorPositions(positions);
	CHECK(positions[xAxis] == 300, "and the move's steps are in the reported position");
}

// An emergency stop drops queued moves on the boards; the tracked positions must stop where the
// drives actually got to rather than jumping to where the move would have ended.
static void TestCancelSteppingAbandonsPendingMotion()
{
	MotionSystem& move = FreshSystem(BasicConfig());

	MoveProfile profile;
	profile.accelClocks = 1000;
	profile.steadyClocks = 1000;
	profile.decelClocks = 1000;
	profile.totalDistance = 3;
	profile.accelDistance = 1;
	profile.decelStartDistance = 2;

	MovementFlags flags{};
	flags.Clear();
	move.AddLinearSegments(xAxis, 0, profile, (motioncalc_t)300.0, flags, 0);
	move.AdvanceTrackers(1000);					// partway through

	int32_t before[maxAxesPlusExtruders] = {};
	move.GetMotorPositions(before);

	move.CancelStepping();

	int32_t after[maxAxesPlusExtruders] = {};
	move.GetMotorPositions(after);
	CHECK(move.AreDrivesStopped(LogicalDrivesBitmap::MakeLowestNBits(4)), "nothing is pending afterwards");
	CHECK(after[xAxis] == before[xAxis], "the position does not jump to where the move would have ended");
	CHECK(after[xAxis] < 300, "and is short of the commanded travel");
}

// The machine description is what every accessor reads, so a reconfigure has to be visible through
// all of them. What a move carries is not here: that is tested where the move is built.
static void TestConfigureIsVisibleThroughAccessors()
{
	MachineConfig config = BasicConfig();
	config.continuousRotationAxes = AxesBitmap::MakeFromBits(2).GetRaw();
	config.controllingDrives[xAxis] = AxesBitmap::MakeFromBits(0, 1).GetRaw();

	const MotionSystem& move = FreshSystem(config);

	CHECK_NEAR(move.DriveStepsPerMm(xAxis), 80.0, 1e-6, "steps per mm");
	CHECK(move.GetAxisDriversConfig(xAxis).numDrivers == 1, "driver count");
	CHECK(move.GetAxisDriversConfig(xAxis).driverNumbers[0].IsRemote(), "axis drivers are on a remote board");
	CHECK(move.GetExtruderDriver(0).localDriver == 3, "extruder driver");
	CHECK(move.IsContinuousRotationAxis(2), "continuous rotation axis, as evaluated by DCS");
	CHECK(!move.IsContinuousRotationAxis(xAxis), "and one that is not");
	CHECK(move.GetControllingDrives(xAxis).IsBitSet(1), "controlling drives, as evaluated by DCS");
	CHECK(move.GetTotalAxes() == 3, "the axis count comes from the same config");
	CHECK(move.GetNumExtruders() == 1, "as does the extruder count");
}

// An axis with a switch per driver needs each driver's index within its axis, because the index is
// the switch that driver watches. Getting it from the configuration is the only thing that keeps that
// pairing the same as the one M574 registered the switches under.
static void TestDriverIndexWithinAnAxis()
{
	MachineConfig config = BasicConfig();
	constexpr size_t zAxis = 2;
	config.axisDrivers[zAxis].numDrivers = 2;
	config.axisDrivers[zAxis].driverNumbers[0] = DriverId((CanAddress)1, 2);
	config.axisDrivers[zAxis].driverNumbers[1] = DriverId((CanAddress)1, 5);

	const MotionSystem& move = FreshSystem(config);

	CHECK(move.GetNumDriversForDrive(zAxis) == 2, "a dual-motor axis reports both drivers");
	CHECK(move.GetDriverIndexInDrive(zAxis, DriverId((CanAddress)1, 2)) == 0, "the first driver watches switch 0");
	CHECK(move.GetDriverIndexInDrive(zAxis, DriverId((CanAddress)1, 5)) == 1, "the second driver watches switch 1");

	// An extruder is not listed among the axis drivers, but it still has the one driver it is
	// configured with, and a stop report for it must not be left waiting for a second
	CHECK(move.GetNumDriversForDrive(maxAxesPlusExtruders - 1) == 1, "an extruder has one driver");
}

int main()
{
	TestNoBacklashLeavesDeltasAlone();
	TestReversalInjectsBacklash();
	TestShortMovesSpreadTheCorrection();
	TestReportedPositionExcludesBacklash();
	TestExtrudersAreNotCompensated();
	TestAreDrivesStopped();
	TestCancelSteppingAbandonsPendingMotion();
	TestConfigureIsVisibleThroughAccessors();
	TestDriverIndexWithinAnAxis();

	return TestSupport::Summarise("motion system");
}
