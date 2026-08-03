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
		CHECK(length == 28 + (4 * 12), "a four-drive submission is the header plus three four-entry arrays");

		alignas(uint32_t) char record[MoveParamsLength(numDrives)]{};
		auto *const header = reinterpret_cast<MoveParamsHeader *>(record);
		header->numDrives = numDrives;

		// Fill the tails through the same accessors the reader uses, which is what makes this a
		// round trip rather than a restatement of the same arithmetic twice.
		const std::span<int32_t> endPoints = MoveParamsEndPoints(*header);
		const std::span<float> directions = MoveParamsDirectionVector(*header);
		const std::span<uint32_t> stopInputs = MoveParamsStopInputs(*header);
		CHECK(endPoints.size() == numDrives, "the endpoint span covers the drives the header claims");
		CHECK(directions.size() == numDrives, "the direction span covers the drives the header claims");
		CHECK(stopInputs.size() == numDrives, "the stop input span covers the drives the header claims");
		for (uint8_t i = 0; i < numDrives; ++i)
		{
			endPoints[i] = 1000 + i;
			directions[i] = 0.25F * (float)(i + 1);
			stopInputs[i] = Duet::Sbc::Motion::MakeStopInput((uint8_t)(i + 1), (uint16_t)(0x100 + i));
		}

		// A byte past the end would be a buffer overrun in the transfer, so check the span ends
		// exactly where the record does.
		const char *const lastByte = reinterpret_cast<const char *>(stopInputs.data() + stopInputs.size());
		CHECK(lastByte == record + length, "the stop inputs end exactly at the end of the record");

		const std::span<const int32_t> readEndPoints = MoveParamsEndPoints(*header);
		const std::span<const float> readDirections = MoveParamsDirectionVector(*header);
		const std::span<const uint32_t> readStopInputs = MoveParamsStopInputs(*header);
		for (uint8_t i = 0; i < numDrives; ++i)
		{
			CHECK(readEndPoints[i] == 1000 + i, "endpoints read back as written");
			CHECK_NEAR(readDirections[i], 0.25 * (i + 1), 1e-9, "direction vector reads back as written");

			// The board and handle have to survive the round trip separately: they are matched
			// against an incoming input change one field at a time
			CHECK(Duet::Sbc::Motion::StopInputBoard(readStopInputs[i]) == i + 1, "the stop input board reads back as written");
			CHECK(Duet::Sbc::Motion::StopInputHandle(readStopInputs[i]) == 0x100 + i, "the stop input handle reads back as written");
		}
	}
}

int main()
{
	std::printf("Motion wire layouts:\n");
	TestMoveParamsLayout();
	TestScheduleMoveLayout();
	TestMoveParamsTails();
	return TestSupport::Summarise("MoveParams layout");
}
