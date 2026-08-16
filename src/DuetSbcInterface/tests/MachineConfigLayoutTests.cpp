// MachineConfig crosses the CApi boundary as raw bytes: DuetSbc_MotionConfigure memcpys whatever
// DuetControlServer hands it straight into the struct. Unlike the other boundary structs it is not
// packed, because driveStepsPerMm and its neighbours are read while preparing moves and a misaligned
// float array is not worth the twenty bytes saved.
//
// The gaps that leaves belong to the compiler and are not declared on either side. The managed
// mirror is a sequential struct of the same fields in the same order, so both compilers align them
// the same way; these offsets are what would catch them ever not doing so.
//
// This suite prints the layout and checks the offsets the mirror hardcodes, and it exercises the
// validation Configure applies to a description it did not write.

#include "TestSupport.h"

#include <Motion/MachineConfig.h>
#include <Motion/MotionSystem.h>
#include <Motion/DDARing.h>

#include <cstddef>

using Duet::Sbc::Motion::AxisDriversConfig;
using Duet::Sbc::Motion::MachineConfig;
using Duet::Sbc::Motion::MotionSystem;

namespace
{
	void ReportField(const char *name, size_t offset, size_t size) noexcept
	{
		std::printf("    %-32s @%-5zu %4zu\n", name, offset, size);
	}

#define FIELD(type, member) ReportField(#member, offsetof(type, member), sizeof(type::member))
#define CHECK_OFFSET(type, member, expected)                                                                           \
	do                                                                                                                 \
	{                                                                                                                  \
		FIELD(type, member);                                                                                           \
		CHECK(offsetof(type, member) == (expected), #type "::" #member " is at the expected offset");                   \
	} while (0)

	// The numbers the C# mirror's SerializedLength adds up to. If one of these moves, that constant
	// and the order Serialize writes fields in have to move with it.
	void TestLayout() noexcept
	{
		std::printf("  MachineConfig %zu bytes\n", sizeof(MachineConfig));

		CHECK(sizeof(DriverId) == 2, "DriverId is 2 bytes");
		CHECK(sizeof(AxisDriversConfig) == 17, "AxisDriversConfig is 1 + 8*2 bytes with no padding");

		CHECK_OFFSET(MachineConfig, numTotalAxes, 0);
		CHECK_OFFSET(MachineConfig, numExtruders, 1);
		CHECK_OFFSET(MachineConfig, numRings, 2);
		CHECK_OFFSET(MachineConfig, numDdasPerRing, 4);
		CHECK_OFFSET(MachineConfig, gracePeriodMs, 8);
		CHECK_OFFSET(MachineConfig, driveStepsPerMm, 12);
		CHECK_OFFSET(MachineConfig, axisDrivers, 140);
		CHECK_OFFSET(MachineConfig, extruderDrivers, 650);
		CHECK_OFFSET(MachineConfig, continuousRotationAxes, 692);
		CHECK_OFFSET(MachineConfig, controllingDrives, 696);

		CHECK(sizeof(MachineConfig) == 816, "MachineConfig is 816 bytes");
	}

	// The description arrives from another process, so the counts in it decide how far this side
	// indexes into its own fixed arrays. None of them may be taken on trust.
	void TestSanitiseCounts() noexcept
	{
		MachineConfig config;
		config.numTotalAxes = 200;
		config.numExtruders = 200;
		MotionSystem::SanitiseConfig(config);

		CHECK(config.numTotalAxes == maxAxes, "numTotalAxes is clamped to the axes there is room for");

		// This is the one that matters: FirstExtruderDrive is maxAxesPlusExtruders - numExtruders,
		// and DDA::Prepare turns a drive at or above it into an extruder index. Too many extruders
		// and that index runs off the end of extruderDrivers, choosing the board a movement message
		// is addressed to from whatever happened to be in memory.
		CHECK(config.numExtruders <= maxExtruders, "numExtruders is clamped to the extruders there is room for");
		CHECK(config.numTotalAxes + config.numExtruders <= (int)maxAxesPlusExtruders,
			  "axes and extruders together fit in the logical drive space");

		const size_t firstExtruder = config.FirstExtruderDrive();
		CHECK(firstExtruder >= config.numTotalAxes, "no drive is both an axis and an extruder");
		CHECK(maxAxesPlusExtruders - 1 - firstExtruder < maxExtruders,
			  "the highest extruder index in range is addressable");
	}

	void TestSanitiseDrivers() noexcept
	{
		MachineConfig config;
		for (AxisDriversConfig& axis : config.axisDrivers)
		{
			axis.numDrivers = 250;
		}
		MotionSystem::SanitiseConfig(config);

		for (const AxisDriversConfig& axis : config.axisDrivers)
		{
			CHECK(axis.numDrivers <= maxDriversPerAxis, "numDrivers is clamped to the driverNumbers array");
		}
	}

	void TestSanitiseRings() noexcept
	{
		MachineConfig zeroed;
		zeroed.numRings = 0;
		zeroed.numDdasPerRing = 0;
		MotionSystem::SanitiseConfig(zeroed);
		CHECK(zeroed.numRings >= 1, "there is always at least one ring");
		CHECK(zeroed.numDdasPerRing >= Duet::Sbc::Motion::minDdasPerRing, "a ring is deep enough to be a ring");

		MachineConfig huge;
		huge.numRings = 200;
		huge.numDdasPerRing = 60000;
		MotionSystem::SanitiseConfig(huge);
		CHECK(huge.numRings <= Duet::Sbc::Motion::maxRings, "numRings is clamped to the rings that exist");
		CHECK(huge.numDdasPerRing <= Duet::Sbc::Motion::maxDdasPerRing, "the lookahead depth is bounded");
	}

	MotionSystem theMove;

	// A ring of 0 or 1 is not a ring: every move would take its start endpoints from the DDA it is
	// about to overwrite, so the drives would be commanded the whole distance a second time.
	void TestRingDepthIsClamped() noexcept
	{
		for (const unsigned int requested : {0u, 1u, 2u})
		{
			DDARing ring;
			ring.Init(theMove, requested);
			CHECK(ring.GetNumDdas() >= Duet::Sbc::Motion::minDdasPerRing, "a degenerate ring depth is refused");
			CHECK(ring.CanAddMove(), "the clamped ring has room for a move");
		}

		DDARing sane;
		sane.Init(theMove, 40);
		CHECK(sane.GetNumDdas() == 40, "a sensible depth is used as given");
	}
}

int main()
{
	std::printf("MachineConfig layout:\n");
	TestLayout();
	TestSanitiseCounts();
	TestSanitiseDrivers();
	TestSanitiseRings();

	// The ring allocates its DDAs from the permanent arena, which Init reserves
	if (!theMove.Init())
	{
		std::printf("FAIL: could not initialise the motion system\n");
		return 1;
	}
	TestRingDepthIsClamped();

	return TestSupport::Summarise("MachineConfig layout");
}
