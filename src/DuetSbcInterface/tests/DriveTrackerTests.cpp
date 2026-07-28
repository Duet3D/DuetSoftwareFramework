// DriveTracker answers "where is this drive right now" by integrating the segment chain, since the
// drives are on CAN-connected boards and cannot be read directly.
//
// Two properties matter more than the rest:
//
//   - It must land exactly on the commanded position. The reported position is what DCS trusts as
//     the machine position at a standstill, so an error of one microstep per move would accumulate
//     into a real offset over a print. Segment distances are fractional and positions are integers,
//     so this only holds because the leftover fraction is carried between segments.
//   - Interpolation must agree with the closed form. During a move the position comes from
//     s = u*t + a*t^2/2 against the current segment, and that is what the tests compare against.
//
// The clock is explicit throughout: nothing here waits, it just tells the tracker what time it is.

#include "TestSupport.h"

#include <Motion/DriveTracker.h>
#include <Platform/Tasks.h>

#include <cstdint>

using Duet::Sbc::Motion::DriveTracker;
using Duet::Sbc::Motion::MoveProfile;

namespace
{
	MovementFlags PlainFlags() noexcept
	{
		MovementFlags f{};
		f.Clear();
		return f;
	}

	// A symmetric trapezoidal move: accelerate, hold, decelerate, ending at rest.
	MoveProfile TrapezoidProfile(uint32_t accelClocks, uint32_t steadyClocks, uint32_t decelClocks,
								 double totalDistanceMm, double accelFraction, double decelFraction) noexcept
	{
		MoveProfile p;
		p.accelClocks = accelClocks;
		p.steadyClocks = steadyClocks;
		p.decelClocks = decelClocks;
		p.totalDistance = (motioncalc_t)totalDistanceMm;
		p.accelDistance = (motioncalc_t)(totalDistanceMm * accelFraction);
		p.decelStartDistance = (motioncalc_t)(totalDistanceMm * (1.0 - decelFraction));

		// Acceleration consistent with covering accelDistance in accelClocks from rest:
		// s = a*t^2/2, so a = 2s/t^2. Deceleration is negative, as the firmware expects.
		p.acceleration = (accelClocks == 0)
							 ? (motioncalc_t)0.0
							 : (motioncalc_t)(2.0 * (double)p.accelDistance / ((double)accelClocks * accelClocks));
		const double decelDistance = totalDistanceMm - (double)p.decelStartDistance;
		p.deceleration = (decelClocks == 0)
							 ? (motioncalc_t)0.0
							 : (motioncalc_t)(-2.0 * decelDistance / ((double)decelClocks * decelClocks));
		return p;
	}
}

// A drive that has been given nothing to do reports where it was left, at any time.
static void TestIdleDriveIsStationary()
{
	DriveTracker t;
	t.Init(0);

	CHECK(!t.MotionPending(), "no motion pending initially");
	CHECK(t.GetMotorPosition() == 0, "starts at zero");
	CHECK_NEAR(t.GetCurrentPosition(0), 0.0, 1e-6, "and stays there at time zero");
	CHECK_NEAR(t.GetCurrentPosition(1000000), 0.0, 1e-6, "and at any later time");

	t.SetMotorPosition(4321);
	CHECK(t.GetMotorPosition() == 4321, "position can be forced, as after homing");
	CHECK_NEAR(t.GetCurrentPosition(1000000), 4321.0, 1e-6, "and holds");
}

// Before a scheduled move starts, the drive has not moved yet.
static void TestPositionBeforeMoveStarts()
{
	DriveTracker t;
	t.Init(0);
	const MoveProfile profile = TrapezoidProfile(1000, 1000, 1000, 3.0, 1.0 / 3.0, 1.0 / 3.0);
	t.AddMove(50000, profile, (motioncalc_t)300.0, PlainFlags());

	CHECK(t.MotionPending(), "the move is pending");
	t.Advance(49000);
	CHECK_NEAR(t.GetCurrentPosition(49000), 0.0, 1e-6, "nothing has moved before the start time");
	CHECK(t.GetMotorPosition() == 0, "and the retired position is unchanged");
}

// The whole point: after a move completes, the drive is exactly where it was told to go.
static void TestMoveEndsOnExactPosition()
{
	DriveTracker t;
	t.Init(0);
	const MoveProfile profile = TrapezoidProfile(1000, 2000, 1000, 10.0, 0.1, 0.1);
	const int32_t steps = 1234;					// deliberately not a round number of segments' worth

	t.AddMove(1000, profile, (motioncalc_t)steps, PlainFlags());
	t.Advance(1000 + profile.TotalClocks());

	CHECK(!t.MotionPending(), "the move has been fully retired");
	CHECK(t.GetMotorPosition() == steps, "the drive ends on exactly the commanded step count");
}

// Positions during the move must match the closed form for the phase being executed.
static void TestInterpolatesWithinSegments()
{
	DriveTracker t;
	t.Init(0);

	// One acceleration-only move so the expected position has a simple closed form:
	// from rest, s = a*t^2/2, reaching `steps` after `accelClocks`.
	const uint32_t accelClocks = 2000;
	const double steps = 800.0;
	MoveProfile profile = TrapezoidProfile(accelClocks, 0, 0, 4.0, 1.0, 0.0);
	profile.accelDistance = profile.totalDistance;

	t.AddMove(0, profile, (motioncalc_t)steps, PlainFlags());

	for (uint32_t fraction = 1; fraction < 10; ++fraction)
	{
		const uint32_t now = (accelClocks * fraction) / 10;
		t.Advance(now);
		const double f = (double)now / accelClocks;
		const double expected = steps * f * f;			// s = a*t^2/2 with a chosen so s(T) = steps
		CHECK_NEAR(t.GetCurrentPosition(now), expected, 1.0, "interpolated position follows a*t^2/2");
	}

	t.Advance(accelClocks);
	CHECK(t.GetMotorPosition() == (int32_t)steps, "and lands exactly at the end");
}

// The position must never run past the end of a segment that has not been retired yet, or a caller
// that reads without advancing first would see the drive keep accelerating indefinitely.
static void TestDoesNotExtrapolatePastSegmentEnd()
{
	DriveTracker t;
	t.Init(0);
	const uint32_t accelClocks = 1000;
	MoveProfile profile = TrapezoidProfile(accelClocks, 0, 0, 2.0, 1.0, 0.0);
	profile.accelDistance = profile.totalDistance;

	t.AddMove(0, profile, (motioncalc_t)500.0, PlainFlags());
	t.Advance(accelClocks / 2);							// enter the segment, but do not finish it

	const float atEnd = t.GetCurrentPosition(accelClocks);
	const float wellPast = t.GetCurrentPosition(accelClocks * 10);
	CHECK_NEAR(atEnd, 500.0, 1.0, "reads the end of the segment at its end time");
	CHECK_NEAR(wellPast, atEnd, 1e-3, "and does not extrapolate beyond it");
}

// A run of moves must not accumulate rounding error, which is what carrying the leftover fraction
// between segments is for. Each move here covers a non-integer number of steps.
static void TestNoDriftOverManyMoves()
{
	DriveTracker t;
	t.Init(0);

	const MoveProfile profile = TrapezoidProfile(500, 500, 500, 1.0, 0.25, 0.25);
	const double stepsPerMove = 33.3333;				// never a whole number of steps
	const unsigned int numMoves = 300;

	uint32_t when = 0;
	for (unsigned int i = 0; i < numMoves; ++i)
	{
		t.AddMove(when, profile, (motioncalc_t)stepsPerMove, PlainFlags());
		when += profile.TotalClocks();
	}
	t.Advance(when);

	CHECK(!t.MotionPending(), "every move retired");

	// Truncation at each segment boundary could lose up to a step per segment; carrying the
	// fraction forward means the total stays within one step of the true travel however many
	// moves there are.
	const double expected = stepsPerMove * numMoves;
	CHECK_NEAR(t.GetMotorPosition(), expected, 1.0, "no drift over 300 chained moves");
}

// Reverse motion has to work as well as forwards, including ending exactly on target.
static void TestReverseMove()
{
	DriveTracker t;
	t.Init(0);
	t.SetMotorPosition(1000);

	const MoveProfile profile = TrapezoidProfile(400, 400, 400, 2.0, 0.25, 0.25);
	t.AddMove(0, profile, (motioncalc_t)-250.0, PlainFlags());
	t.Advance(profile.TotalClocks());

	CHECK(t.GetMotorPosition() == 750, "a reverse move subtracts from the position");
}

// The extruder accumulator reports net movement since it was last read, which is how filament use
// is tracked across a homing operation that resets the position.
static void TestAccumulatorReportsNetMovement()
{
	DriveTracker t;
	t.Init(0);
	CHECK(t.GetAndClearAccumulatedMovement() == 0, "nothing accumulated initially");

	const MoveProfile profile = TrapezoidProfile(400, 400, 400, 2.0, 0.25, 0.25);
	t.AddMove(0, profile, (motioncalc_t)100.0, PlainFlags());
	t.Advance(profile.TotalClocks());

	CHECK(t.GetAndClearAccumulatedMovement() == 100, "accumulates the completed move");
	CHECK(t.GetAndClearAccumulatedMovement() == 0, "and clears on read");

	// A move in each direction nets out.
	t.AddMove(profile.TotalClocks(), profile, (motioncalc_t)60.0, PlainFlags());
	t.AddMove(2 * profile.TotalClocks(), profile, (motioncalc_t)-60.0, PlainFlags());
	t.Advance(3 * profile.TotalClocks());
	CHECK(t.GetAndClearAccumulatedMovement() == 0, "opposing moves net to zero");
}

// An emergency stop abandons pending motion on the boards; the tracker has to do the same without
// pretending the drive completed the move.
static void TestClearMovementPending()
{
	DriveTracker t;
	t.Init(0);
	const MoveProfile profile = TrapezoidProfile(1000, 1000, 1000, 3.0, 1.0 / 3.0, 1.0 / 3.0);
	t.AddMove(0, profile, (motioncalc_t)300.0, PlainFlags());
	t.Advance(1500);

	const int32_t partway = t.GetMotorPosition();
	t.ClearMovementPending();

	CHECK(!t.MotionPending(), "pending motion is dropped");
	CHECK(t.GetMotorPosition() == partway, "without advancing the position to where the move would have ended");
	CHECK_NEAR(t.GetCurrentPosition(1000000), (double)partway, 1e-6, "and it stays put afterwards");
}

// Adding a move that overlaps one already scheduled makes SegmentBuilder split or merge the segment
// currently being read. The tracker must notice and re-read it rather than keep stale parameters.
static void TestOverlappingMoveInvalidatesCachedSegment()
{
	DriveTracker t;
	t.Init(0);

	MoveProfile first = TrapezoidProfile(2000, 0, 0, 4.0, 1.0, 0.0);
	first.accelDistance = first.totalDistance;
	t.AddMove(0, first, (motioncalc_t)400.0, PlainFlags());
	t.Advance(500);									// enter the first move's segment

	// A second move over the second half of the first, as a second drive's contribution would be.
	MoveProfile second = TrapezoidProfile(1000, 0, 0, 2.0, 1.0, 0.0);
	second.accelDistance = second.totalDistance;
	t.AddMove(1000, second, (motioncalc_t)200.0, PlainFlags());

	// Adding the second move split the segment being read at t=1000, so the head of the chain is now
	// [0,1000) covering a quarter of the first move rather than [0,2000) covering all of it.
	// Retiring it against the cached figures would credit the drive with the whole first move.
	//
	// The end position alone would not catch that: the leftover fraction carried between segments
	// subtracts the excess again, so the drive still finishes in the right place. Only a reading
	// taken partway through shows it.
	t.Advance(1000);
	CHECK_NEAR(t.GetMotorPosition(), 100.0, 1.0, "retires only what the split segment actually covers");

	t.Advance(3000);
	CHECK(!t.MotionPending(), "both moves retired");
	CHECK_NEAR(t.GetMotorPosition(), 600.0, 1.0, "the position accounts for both overlapping moves");
}

int main()
{
	if (!Tasks::InitPermanentArena(4 * 1024 * 1024))
	{
		std::printf("FAIL: could not reserve the permanent arena\n");
		return 1;
	}

	TestIdleDriveIsStationary();
	TestPositionBeforeMoveStarts();
	TestMoveEndsOnExactPosition();
	TestInterpolatesWithinSegments();
	TestDoesNotExtrapolatePastSegmentEnd();
	TestNoDriftOverManyMoves();
	TestReverseMove();
	TestAccumulatorReportsNetMovement();
	TestClearMovementPending();
	TestOverlappingMoveInvalidatesCachedSegment();

	return TestSupport::Summarise("drive tracker");
}
