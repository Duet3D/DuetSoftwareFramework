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
		CHECK(sizeof(ScheduleMoveDriver) == 12, "ScheduleMoveDriver is 12 bytes");
		CHECK_OFFSET(ScheduleMoveDriver, boardAddress, 0);
		CHECK_OFFSET(ScheduleMoveDriver, driverNumber, 1);
		CHECK_OFFSET(ScheduleMoveDriver, isExtruder, 2);
		CHECK_OFFSET(ScheduleMoveDriver, steps, 4);
		CHECK_OFFSET(ScheduleMoveDriver, extrusion, 8);

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
		CHECK(length == 28 + (4 * 8), "a four-drive submission is the header plus two four-entry arrays");

		alignas(uint32_t) char record[MoveParamsLength(numDrives)]{};
		auto *const header = reinterpret_cast<MoveParamsHeader *>(record);
		header->numDrives = numDrives;

		// Fill the tails through the same accessors the reader uses, which is what makes this a
		// round trip rather than a restatement of the same arithmetic twice.
		auto *const endPoints = MoveParamsEndPoints(*header);
		auto *const directions = MoveParamsDirectionVector(*header);
		for (uint8_t i = 0; i < numDrives; ++i)
		{
			endPoints[i] = 1000 + i;
			directions[i] = 0.25F * (float)(i + 1);
		}

		// A byte past the end would be a buffer overrun in the transfer, so check the last write
		// landed inside the record.
		const char *const lastByte = reinterpret_cast<const char *>(&directions[numDrives - 1]) + sizeof(float);
		CHECK(lastByte == record + length, "the direction vector ends exactly at the end of the record");

		const int32_t *const readEndPoints = MoveParamsEndPoints(*header);
		const float *const readDirections = MoveParamsDirectionVector(*header);
		for (uint8_t i = 0; i < numDrives; ++i)
		{
			CHECK(readEndPoints[i] == 1000 + i, "endpoints read back as written");
			CHECK_NEAR(readDirections[i], 0.25 * (i + 1), 1e-9, "direction vector reads back as written");
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
