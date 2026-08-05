// Layout of the two structs that cross a language or a wire boundary in the motion path:
// MoveParamsHeader (C# DuetControlServer -> native, in-process) and the ScheduleMove pair
// (native -> controller, over SPI).
//
// The sizes are already static_asserted where they are declared, so this suite is not there to
// catch a change to them - it is there to *print* the layout in ctest output. When the C# mirror or
// the firmware struct disagrees, the argument is settled by running this rather than by reading two
// headers side by side and counting padding. The offsets are checked too, because a pair of fields
// swapped keeps the size and changes the meaning of every byte after them.

#include <Motion/MoveParams.h>
#include <Motion/ScheduleMoveBuilder.h>

#include <TestSupport.h>

using Duet::Sbc::Motion::MoveParamsDirectionVector;
using Duet::Sbc::Motion::MoveParamsEndPoints;
using Duet::Sbc::Motion::MoveParamsHeader;
using Duet::Sbc::Motion::MoveParamsLength;
using duet::spi::protocol::ScheduleMoveDriver;
using duet::spi::protocol::ScheduleMoveHeader;

namespace
{
	void Report(const char *name, size_t size) noexcept
	{
		std::printf("  %-20s %3zu bytes\n", name, size);
	}

	void ReportField(const char *name, size_t offset, size_t size) noexcept
	{
		std::printf("    %-24s @%-3zu %2zu\n", name, offset, size);
	}

#define FIELD(type, member) ReportField(#member, offsetof(type, member), sizeof(type::member))
#define CHECK_OFFSET(type, member, expected)                                                                           \
	do                                                                                                                 \
	{                                                                                                                  \
		FIELD(type, member);                                                                                           \
		CHECK(offsetof(type, member) == (expected), #type "::" #member " is at the expected offset");                   \
	} while (0)

	void TestMoveParamsLayout() noexcept
	{
		Report("MoveParamsHeader", sizeof(MoveParamsHeader));
		CHECK(sizeof(MoveParamsHeader) == 28, "MoveParamsHeader is 28 bytes");
		CHECK_OFFSET(MoveParamsHeader, moveId, 0);
		CHECK_OFFSET(MoveParamsHeader, ownedDrives, 4);
		CHECK_OFFSET(MoveParamsHeader, flags, 8);
		CHECK_OFFSET(MoveParamsHeader, totalDistance, 12);
		CHECK_OFFSET(MoveParamsHeader, maxAcceleration, 16);
		CHECK_OFFSET(MoveParamsHeader, requestedSpeed, 20);
		CHECK_OFFSET(MoveParamsHeader, ringNumber, 24);
		CHECK_OFFSET(MoveParamsHeader, numDrives, 25);
	}

	void TestScheduleMoveLayout() noexcept
	{
		Report("ScheduleMoveHeader", sizeof(ScheduleMoveHeader));
		CHECK(sizeof(ScheduleMoveHeader) == 56, "ScheduleMoveHeader is 56 bytes");
		CHECK_OFFSET(ScheduleMoveHeader, whenToExecute, 0);
		CHECK_OFFSET(ScheduleMoveHeader, accelClocks, 4);
		CHECK_OFFSET(ScheduleMoveHeader, steadyClocks, 8);
		CHECK_OFFSET(ScheduleMoveHeader, decelClocks, 12);
		CHECK_OFFSET(ScheduleMoveHeader, acceleration, 16);
		CHECK_OFFSET(ScheduleMoveHeader, deceleration, 20);
		CHECK_OFFSET(ScheduleMoveHeader, totalDistance, 24);
		CHECK_OFFSET(ScheduleMoveHeader, accelDistance, 28);
		CHECK_OFFSET(ScheduleMoveHeader, decelStartDistance, 32);
		CHECK_OFFSET(ScheduleMoveHeader, startSpeed, 36);
		CHECK_OFFSET(ScheduleMoveHeader, topSpeed, 40);
		CHECK_OFFSET(ScheduleMoveHeader, endSpeed, 44);
		CHECK_OFFSET(ScheduleMoveHeader, moveId, 48);
		CHECK_OFFSET(ScheduleMoveHeader, numDrivers, 52);
		CHECK_OFFSET(ScheduleMoveHeader, flags, 53);

		Report("ScheduleMoveDriver", sizeof(ScheduleMoveDriver));
		CHECK(sizeof(ScheduleMoveDriver) == 16, "ScheduleMoveDriver is 16 bytes");
		CHECK_OFFSET(ScheduleMoveDriver, boardAddress, 0);
		CHECK_OFFSET(ScheduleMoveDriver, driverNumber, 1);
		CHECK_OFFSET(ScheduleMoveDriver, isExtruder, 2);
		CHECK_OFFSET(ScheduleMoveDriver, stopOnBoard, 3);
		CHECK_OFFSET(ScheduleMoveDriver, steps, 4);
		CHECK_OFFSET(ScheduleMoveDriver, extrusion, 8);
		CHECK_OFFSET(ScheduleMoveDriver, stopOnHandle, 12);

		// The controller matches an incoming input change against these two, so they are the only
		// thing standing between an endstop firing and the right drive being stopped
		Report("MotionStoppedHeader", sizeof(duet::spi::protocol::MotionStoppedHeader));
		CHECK(sizeof(duet::spi::protocol::MotionStoppedHeader) == 8, "MotionStoppedHeader is 8 bytes");
		CHECK_OFFSET(duet::spi::protocol::MotionStoppedHeader, whenTriggered, 0);
		CHECK_OFFSET(duet::spi::protocol::MotionStoppedHeader, numDrivers, 4);
		CHECK(sizeof(duet::spi::protocol::MotionStoppedDriver) == 4, "MotionStoppedDriver is 4 bytes");

		// numDrivers is a byte, so a packet can never ask for more drivers than the SBC can put in
		// one. If MaxScheduleMoveDrivers ever grows past 255 the field has to grow with it.
		CHECK(duet::spi::protocol::MaxScheduleMoveDrivers <= 255, "numDrivers must be able to count the drivers");
	}

	// The two arrays after MoveParamsHeader are addressed by pointer arithmetic on both sides, so
	// walk a record built the way DCS builds one and read it back the way native reads one.
	void TestMoveParamsTails() noexcept
	{
		constexpr uint8_t numDrives = 4;
		constexpr size_t length = MoveParamsLength(numDrives);
		Report("MoveParams(4 drives)", length);
		CHECK(length == 28 + (4 * (4 + 4 + 12)), "a four-drive submission is the header plus three four-entry arrays");

		alignas(uint32_t) char record[MoveParamsLength(numDrives)]{};
		auto *const header = reinterpret_cast<MoveParamsHeader *>(record);
		header->numDrives = numDrives;

		// Fill the tails through the same accessors the reader uses, which is what makes this a
		// round trip rather than a restatement of the same arithmetic twice.
		const std::span<int32_t> endPoints = MoveParamsEndPoints(*header);
		const std::span<float> directions = MoveParamsDirectionVector(*header);
		const std::span<Duet::Sbc::Motion::MoveStopInput> stopInputs = MoveParamsStopInputs(*header);
		CHECK(endPoints.size() == numDrives, "the endpoint span covers the drives the header claims");
		CHECK(directions.size() == numDrives, "the direction span covers the drives the header claims");
		CHECK(stopInputs.size() == numDrives, "the stop input span covers the drives the header claims");
		for (uint8_t i = 0; i < numDrives; ++i)
		{
			endPoints[i] = 1000 + i;
			directions[i] = 0.25F * (float)(i + 1);
			stopInputs[i].handle = (uint16_t)(0x100 + i);
			stopInputs[i].numSwitches = 1;
			stopInputs[i].boards[0] = (uint8_t)(i + 1);
		}

		// A byte past the end would be a buffer overrun in the transfer, so check the span ends
		// exactly where the record does.
		const char *const lastByte = reinterpret_cast<const char *>(stopInputs.data() + stopInputs.size());
		CHECK(lastByte == record + length, "the stop inputs end exactly at the end of the record");

		const std::span<const int32_t> readEndPoints = MoveParamsEndPoints(*header);
		const std::span<const float> readDirections = MoveParamsDirectionVector(*header);
		const std::span<const Duet::Sbc::Motion::MoveStopInput> readStopInputs = MoveParamsStopInputs(*header);
		for (uint8_t i = 0; i < numDrives; ++i)
		{
			CHECK(readEndPoints[i] == 1000 + i, "endpoints read back as written");
			CHECK_NEAR(readDirections[i], 0.25 * (i + 1), 1e-9, "direction vector reads back as written");

			// The board and handle have to survive the round trip separately: they are matched
			// against an incoming input change one field at a time
			const uint32_t forDriver = Duet::Sbc::Motion::StopInputForDriver(readStopInputs[i], 0);
			CHECK(Duet::Sbc::Motion::StopInputBoard(forDriver) == i + 1, "the stop input board reads back as written");
			CHECK(Duet::Sbc::Motion::StopInputHandle(forDriver) == 0x100 + i, "the stop input handle reads back as written");
		}
	}
}

// An axis with a switch per driver pairs port i with driver i, exactly as RepRapFirmware does. That
// pairing is the only thing that tells one motor of a gantry from the other - get it wrong and both
// motors watch one switch, which is the behaviour the configuration was trying to avoid.
void TestStopInputPerDriver() noexcept
{
	using namespace Duet::Sbc::Motion;

	// The handle M574 registered: type 1 (endstop) in the top nibble, axis 2 in the major field,
	// switch 0 in the minor field
	constexpr uint16_t handle = (1u << 12) | (2u << 6);

	// One switch for the whole axis: every driver watches it, whichever driver it is
	MoveStopInput shared{};
	shared.handle = handle;
	shared.numSwitches = 1;
	shared.boards[0] = 3;
	for (size_t driver = 0; driver < 3; ++driver)
	{
		const uint32_t forDriver = StopInputForDriver(shared, driver);
		CHECK(StopInputBoard(forDriver) == 3, "every driver watches the axis' board");
		CHECK(StopInputHandle(forDriver) == handle, "and the axis' switch");
	}

	// A switch per driver: the handle follows the driver's index, and the board is whichever board
	// that switch happens to be wired to - they need not be the same one
	MoveStopInput perDriver{};
	perDriver.handle = handle;
	perDriver.numSwitches = 3;
	perDriver.boards[0] = 1;
	perDriver.boards[1] = 4;
	perDriver.boards[2] = 0;
	for (size_t driver = 0; driver < perDriver.numSwitches; ++driver)
	{
		const uint32_t forDriver = StopInputForDriver(perDriver, driver);
		CHECK(StopInputBoard(forDriver) == perDriver.boards[driver], "each switch keeps its own board");
		CHECK(StopInputHandle(forDriver) == handle + driver, "the minor field selects the driver's switch");
	}

	// A driver with no switch of its own must not fall back to another motor's, which would stop it
	// at the wrong place and defeat the point of giving each motor one
	CHECK(StopInputForDriver(perDriver, 3) == kNoStopInput, "a driver past the last switch watches nothing");

	// A drive with no endstop has to stay without one, or it would start watching switch 0
	CHECK(StopInputForDriver(kNoStopSwitches, 1) == kNoStopInput, "a drive watching nothing keeps the sentinel");

	// A stall endstop is n boards but one handle: a board reports every driver that stalled under
	// RemoteInputHandle(typeStallEndstop, 0, 0), so the board tells one driver's stall from
	// another's. Deriving a minor per driver here would name a handle no board ever reports, and the
	// move would run on as though it had no endstop
	constexpr uint16_t stallHandle = 5u << 12;			// type 5 (stall endstop), major 0, minor 0
	MoveStopInput stall{};
	stall.handle = stallHandle;
	stall.numSwitches = 3;
	stall.boards[0] = 1;
	stall.boards[1] = 4;
	stall.boards[2] = 0;
	for (size_t driver = 0; driver < stall.numSwitches; ++driver)
	{
		const uint32_t forDriver = StopInputForDriver(stall, driver);
		CHECK(StopInputBoard(forDriver) == stall.boards[driver], "each driver watches its own board");
		CHECK(StopInputHandle(forDriver) == stallHandle, "under the one board-wide stall handle");
	}
}

int main()
{
	std::printf("Motion wire layouts:\n");
	TestMoveParamsLayout();
	TestScheduleMoveLayout();
	TestMoveParamsTails();
	TestStopInputPerDriver();
	return TestSupport::Summarise("MoveParams layout");
}
