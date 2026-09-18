// MoveSegment list algebra and the segment builder on top of it.
//
// AddSegment is the subtlest code in the motion engine. A new segment may start before, during or
// after any existing one, so adding it can split an existing segment, merge into it, or both,
// several times over. Superimposing segments is how several moves that overlap in time - and, in
// the firmware, several shaping impulses - end up as one description of what a drive does.
//
// The invariant that matters is conservation: however the list gets carved up, the total distance
// over any interval must equal the sum of what was put in over that interval. Everything else -
// ordering, contiguity, no zero-length segments - exists to make that computable. So most of these
// tests add segments in awkward arrangements and then integrate the resulting list back.

#include "TestSupport.h"

#include <Motion/SegmentBuilder.h>
#include <Motion/MoveSegment.h>
#include <Platform/MemoryArena.h>

#include <cstdint>
#include <vector>

using Duet::Sbc::Motion::MoveProfile;
namespace SegmentBuilder = Duet::Sbc::Motion::SegmentBuilder;

namespace
{
	MovementFlags PlainFlags() noexcept
	{
		MovementFlags f{};				// the union has no default member initialiser; Clear() sets `all`
		f.Clear();
		return f;
	}

	// Position covered by one segment `t` clocks after it starts: s = u*t + a*t^2/2.
	double DistanceInto(const MoveSegment *seg, double t) noexcept
	{
		return (double)seg->CalcU() * t + 0.5 * (double)seg->GetA() * t * t;
	}

	// Total distance the whole list covers strictly before absolute time `endTime`, integrating each
	// segment over however much of it falls in range. This is the quantity that must be conserved.
	double DistanceBefore(const MoveSegment *list, uint32_t endTime) noexcept
	{
		double total = 0.0;
		for (const MoveSegment *seg = list; seg != nullptr; seg = seg->GetNext())
		{
			const auto offset = (int32_t)(endTime - seg->GetStartTime());
			if (offset <= 0)
			{
				continue;
			}
			const auto within = (uint32_t)offset;
			total += DistanceInto(seg, (double)((within >= seg->GetDuration()) ? seg->GetDuration() : within));
		}
		return total;
	}

	double TotalDistance(const MoveSegment *list) noexcept
	{
		double total = 0.0;
		for (const MoveSegment *seg = list; seg != nullptr; seg = seg->GetNext())
		{
			total += (double)seg->GetLength();
		}
		return total;
	}

	unsigned int CountSegments(const MoveSegment *list) noexcept
	{
		unsigned int n = 0;
		for (const MoveSegment *seg = list; seg != nullptr; seg = seg->GetNext())
		{
			++n;
		}
		return n;
	}

	// Segments must come out in start-time order, none overlapping and none empty. A reader walks
	// the list front to back and stops at the first segment that has not finished, so an
	// out-of-order or overlapping list would silently report the wrong position.
	void CheckWellFormed(const MoveSegment *list, const char *what)
	{
		bool ordered = true;
		bool nonEmpty = true;
		for (const MoveSegment *seg = list; seg != nullptr; seg = seg->GetNext())
		{
			if (seg->GetDuration() == 0)
			{
				nonEmpty = false;
			}
			const MoveSegment *const next = seg->GetNext();
			if (next != nullptr && (int32_t)(next->GetStartTime() - (seg->GetStartTime() + seg->GetDuration())) < 0)
			{
				ordered = false;
			}
		}
		CHECK(ordered, what);
		CHECK(nonEmpty, what);
	}

	MoveSegment *AddOne(MoveSegment *list, uint32_t start, uint32_t duration, double distance, double a = 0.0)
	{
		return SegmentBuilder::AddSegment(list, start, duration, (motioncalc_t)distance, (motioncalc_t)a,
										  PlainFlags(), (motioncalc_t)0.0);
	}

	void Release(MoveSegment *list) noexcept
	{
		MoveSegment::ReleaseAll(list);
	}
}

// A single segment should come back exactly as it went in.
static void TestSingleSegment()
{
	MoveSegment *list = AddOne(nullptr, 1000, 500, 250.0);
	CHECK(CountSegments(list) == 1, "one segment in, one out");
	CHECK(list->GetStartTime() == 1000, "start time preserved");
	CHECK(list->GetDuration() == 500, "duration preserved");
	CHECK_NEAR(list->GetLength(), 250.0, 1e-3, "distance preserved");
	CHECK(list->IsLinear(), "zero acceleration means linear");
	CHECK_NEAR(list->CalcU(), 0.5, 1e-6, "initial speed recovered from distance and duration");
	Release(list);
}

// Non-overlapping segments just chain up, whatever order they are added in.
static void TestDisjointSegmentsSortByTime()
{
	MoveSegment *list = AddOne(nullptr, 2000, 100, 50.0);
	list = AddOne(list, 1000, 100, 25.0);			// earlier than the first
	list = AddOne(list, 3000, 100, 75.0);			// later than both

	CHECK(CountSegments(list) == 3, "three disjoint segments stay separate");
	CHECK(list->GetStartTime() == 1000, "earliest segment ends up first");
	CHECK(list->GetNext()->GetStartTime() == 2000, "and the rest follow in time order");
	CHECK(list->GetNext()->GetNext()->GetStartTime() == 3000, "including one added last");
	CHECK_NEAR(TotalDistance(list), 150.0, 1e-3, "no distance gained or lost");
	CheckWellFormed(list, "disjoint segments are well formed");
	Release(list);
}

// Exactly coincident segments merge into one, which is the common case: several drives' worth of
// the same move phase, or two moves scheduled back to back.
static void TestIdenticalIntervalMerges()
{
	MoveSegment *list = AddOne(nullptr, 1000, 400, 100.0);
	list = AddOne(list, 1000, 400, 60.0);

	CHECK(CountSegments(list) == 1, "coincident segments merge");
	CHECK_NEAR(list->GetLength(), 160.0, 1e-3, "distances add");
	Release(list);
}

// A new segment landing inside an existing one splits it, and the overlap merges.
static void TestOverlapSplitsAndMerges()
{
	// Existing: [1000,2000) covering 100. New: [1200,1600) covering 40.
	MoveSegment *list = AddOne(nullptr, 1000, 1000, 100.0);
	list = AddOne(list, 1200, 400, 40.0);

	CheckWellFormed(list, "overlapping insert stays well formed");
	CHECK(CountSegments(list) == 3, "the existing segment is split either side of the overlap");
	CHECK_NEAR(TotalDistance(list), 140.0, 1e-3, "total distance is the sum of both inputs");

	// The overlap must carry both contributions and nothing outside it may have changed.
	CHECK_NEAR(DistanceBefore(list, 1200), 20.0, 1e-3, "before the overlap, only the original");
	CHECK_NEAR(DistanceBefore(list, 1600), 20.0 + 40.0 + 40.0, 1e-3, "through the overlap, both");
	CHECK_NEAR(DistanceBefore(list, 2000), 140.0, 1e-3, "after it, everything");
	Release(list);
}

// A new segment that starts before an existing one and outlasts it: the leading part is inserted,
// the overlap merges, and the trailing part is inserted again - all three paths in one call.
static void TestStraddlingSegment()
{
	MoveSegment *list = AddOne(nullptr, 2000, 400, 40.0);		// existing: [2000,2400)
	list = AddOne(list, 1000, 2000, 200.0);						// new:      [1000,3000)

	CheckWellFormed(list, "straddling insert stays well formed");
	CHECK_NEAR(TotalDistance(list), 240.0, 1e-3, "total distance is the sum of both inputs");
	CHECK_NEAR(DistanceBefore(list, 2000), 100.0, 1e-3, "leading part is the new segment alone");
	CHECK_NEAR(DistanceBefore(list, 2400), 100.0 + 40.0 + 40.0, 1e-3, "middle carries both");
	CHECK_NEAR(DistanceBefore(list, 3000), 240.0, 1e-3, "trailing part is the new segment alone");
	CHECK(list->GetStartTime() == 1000, "list still starts at the earliest time");
	Release(list);
}

// Accelerating segments have to survive splitting: the two halves must together cover what the
// whole did, with the second starting at the speed the first reached.
static void TestSplitPreservesAcceleratingMotion()
{
	// u = 1 step/clock, a = 0.002 step/clock^2 over 100 clocks: s = 100 + 10 = 110.
	const double a = 0.002;
	const double duration = 100.0;
	const double distance = 1.0 * duration + 0.5 * a * duration * duration;

	MoveSegment *list = AddOne(nullptr, 5000, (uint32_t)duration, distance, a);
	const double wholeAt40 = DistanceInto(list, 40.0);

	// Force a split by adding a zero-distance, zero-acceleration segment over the second part.
	list = AddOne(list, 5040, 60, 0.0);

	CheckWellFormed(list, "split accelerating segment stays well formed");
	CHECK(CountSegments(list) == 2, "the accelerating segment is split in two");
	CHECK_NEAR(TotalDistance(list), distance, 1e-3, "split conserves total distance");
	CHECK_NEAR(DistanceBefore(list, 5040), wholeAt40, 1e-3, "the split point covers the same distance");
	CHECK_NEAR(list->GetNext()->CalcU(), 1.0 + a * 40.0, 1e-6, "second part starts at the speed the first reached");
	CHECK_NEAR(list->GetNext()->GetA(), a, 1e-9, "and keeps the same acceleration");
	Release(list);
}

// A whole move's worth of segments for one drive: accelerate, hold, decelerate.
static void TestBuildsThreePhaseMove()
{
	MoveProfile profile;
	profile.accelClocks = 1000;
	profile.steadyClocks = 2000;
	profile.decelClocks = 1000;
	profile.totalDistance = 10.0;			// mm
	profile.accelDistance = 2.0;
	profile.decelStartDistance = 8.0;
	profile.acceleration = (motioncalc_t)1e-5;
	profile.deceleration = (motioncalc_t)-1e-5;

	// 100 microsteps per mm, so the whole move is 1000 steps.
	MoveSegment *list = SegmentBuilder::AddLinearSegments(nullptr, 7000, profile, (motioncalc_t)1000.0, PlainFlags());

	CheckWellFormed(list, "a built move is well formed");
	CHECK(CountSegments(list) == 3, "accelerate, steady, decelerate");
	CHECK(list->GetStartTime() == 7000, "starts when asked");
	CHECK_NEAR(TotalDistance(list), 1000.0, 1e-3, "covers the drive's whole travel in steps");

	// Phase boundaries land where the profile says, and each phase covers its own share.
	CHECK_NEAR(DistanceBefore(list, 8000), 200.0, 1e-3, "acceleration phase covers accelDistance");
	CHECK_NEAR(DistanceBefore(list, 10000), 800.0, 1e-3, "steady phase ends at decelStartDistance");
	CHECK(list->IsAccelerating(), "first phase accelerates");
	CHECK(list->GetNext()->IsLinear(), "second phase is constant speed");
	CHECK(!list->GetNext()->GetNext()->IsAccelerating(), "third phase decelerates");

	const uint32_t endTime = list->GetNext()->GetNext()->GetStartTime() + list->GetNext()->GetNext()->GetDuration();
	CHECK(endTime == 7000 + profile.TotalClocks(), "the move ends when the profile says it does");
	Release(list);
}

// A drive moving backwards gets negative distances throughout, and the profile is unchanged.
static void TestNegativeStepsReverseDirection()
{
	MoveProfile profile;
	profile.accelClocks = 500;
	profile.steadyClocks = 500;
	profile.decelClocks = 500;
	profile.totalDistance = 4.0;
	profile.accelDistance = 1.0;
	profile.decelStartDistance = 3.0;
	profile.acceleration = (motioncalc_t)1e-5;
	profile.deceleration = (motioncalc_t)-1e-5;

	MoveSegment *list = SegmentBuilder::AddLinearSegments(nullptr, 0, profile, (motioncalc_t)-400.0, PlainFlags());
	CHECK(CountSegments(list) == 3, "three phases regardless of direction");
	CHECK_NEAR(TotalDistance(list), -400.0, 1e-3, "travel is negative");
	CHECK(list->GetLength() < 0, "so is each phase");
	Release(list);
}

// Phases the profile gives no time to must not appear: a zero-duration segment would divide by zero
// when its initial speed is recovered.
static void TestZeroDurationPhasesAreSkipped()
{
	MoveProfile profile;
	profile.accelClocks = 0;					// straight to top speed
	profile.steadyClocks = 1000;
	profile.decelClocks = 0;					// and stops dead
	profile.totalDistance = 5.0;
	profile.accelDistance = 0.0;
	profile.decelStartDistance = 0.0;

	MoveSegment *list = SegmentBuilder::AddLinearSegments(nullptr, 100, profile, (motioncalc_t)500.0, PlainFlags());
	CHECK(CountSegments(list) == 1, "only the steady phase is emitted");
	CHECK(list->GetDuration() == 1000, "and it has the steady phase's duration");
	CHECK_NEAR(list->GetLength(), 500.0, 1e-3, "covering the whole travel");
	Release(list);
}

// An acceleration-only move: no steady phase, so the acceleration phase covers everything.
static void TestAccelerationOnlyMove()
{
	MoveProfile profile;
	profile.accelClocks = 800;
	profile.steadyClocks = 0;
	profile.decelClocks = 0;
	profile.totalDistance = 3.0;
	profile.accelDistance = 3.0;
	profile.decelStartDistance = 3.0;
	profile.acceleration = (motioncalc_t)2e-5;

	MoveSegment *list = SegmentBuilder::AddLinearSegments(nullptr, 0, profile, (motioncalc_t)300.0, PlainFlags());
	CHECK(CountSegments(list) == 1, "one phase only");
	CHECK_NEAR(TotalDistance(list), 300.0, 1e-3, "which covers the whole travel");
	Release(list);
}

// Two consecutive moves for the same drive, as the ring produces them: the second starts exactly
// where the first ends, and the chain must read as one continuous motion.
static void TestConsecutiveMovesChain()
{
	MoveProfile profile;
	profile.accelClocks = 500;
	profile.steadyClocks = 1000;
	profile.decelClocks = 500;
	profile.totalDistance = 2.0;
	profile.accelDistance = 0.5;
	profile.decelStartDistance = 1.5;
	profile.acceleration = (motioncalc_t)1e-5;
	profile.deceleration = (motioncalc_t)-1e-5;

	MoveSegment *list = SegmentBuilder::AddLinearSegments(nullptr, 0, profile, (motioncalc_t)200.0, PlainFlags());
	list = SegmentBuilder::AddLinearSegments(list, profile.TotalClocks(), profile, (motioncalc_t)200.0, PlainFlags());

	CheckWellFormed(list, "chained moves are well formed");
	CHECK(CountSegments(list) == 6, "neither move's phases merge with the other's");
	CHECK_NEAR(TotalDistance(list), 400.0, 1e-3, "both moves' travel is present");
	CHECK_NEAR(DistanceBefore(list, profile.TotalClocks()), 200.0, 1e-3, "the first move completes before the second starts");
	Release(list);
}

// Pressure advance adds distance proportional to the speed change, so it should lengthen the
// acceleration phase, shorten the deceleration phase by as much, and leave the total alone.
static void TestPressureAdvanceShiftsDistanceWithoutAddingAny()
{
	MoveProfile profile;
	profile.accelClocks = 1000;
	profile.steadyClocks = 1000;
	profile.decelClocks = 1000;
	profile.totalDistance = 6.0;
	profile.accelDistance = 2.0;
	profile.decelStartDistance = 4.0;
	profile.acceleration = (motioncalc_t)1e-5;
	profile.deceleration = (motioncalc_t)-1e-5;

	MovementFlags extruderFlags = PlainFlags();
	extruderFlags.isExtruder = true;

	MoveSegment *plain = SegmentBuilder::AddLinearSegments(nullptr, 0, profile, (motioncalc_t)600.0, extruderFlags);
	MoveSegment *advanced = SegmentBuilder::AddLinearSegments(nullptr, 0, profile, (motioncalc_t)600.0, extruderFlags,
															  (motioncalc_t)20.0);

	CHECK(advanced->GetLength() > plain->GetLength(), "pressure advance extrudes more while accelerating");

	const MoveSegment *plainDecel = plain->GetNext()->GetNext();
	const MoveSegment *advancedDecel = advanced->GetNext()->GetNext();
	CHECK(advancedDecel->GetLength() < plainDecel->GetLength(), "and less while decelerating");

	// Acceleration and deceleration are equal and opposite here, so what is added to one phase is
	// taken from the other and the filament ends up in the same place.
	CHECK_NEAR(TotalDistance(advanced), TotalDistance(plain), 1e-3, "total extrusion is unchanged");
	Release(plain);
	Release(advanced);
}

int main()
{
	// DDA and MoveSegment allocate from the permanent arena rather than the heap; see
	// Motion/MemoryArena.h. 1MB is far more than these tests need.
	if (!Duet::Sbc::MemoryArena::Reserve(1024 * 1024))
	{
		std::printf("FAIL: could not reserve the permanent arena\n");
		return 1;
	}

	TestSingleSegment();
	TestDisjointSegmentsSortByTime();
	TestIdenticalIntervalMerges();
	TestOverlapSplitsAndMerges();
	TestStraddlingSegment();
	TestSplitPreservesAcceleratingMotion();
	TestBuildsThreePhaseMove();
	TestNegativeStepsReverseDirection();
	TestZeroDurationPhasesAreSkipped();
	TestAccelerationOnlyMove();
	TestConsecutiveMovesChain();
	TestPressureAdvanceShiftsDistanceWithoutAddingAny();

	return TestSupport::Summarise("move segment");
}
