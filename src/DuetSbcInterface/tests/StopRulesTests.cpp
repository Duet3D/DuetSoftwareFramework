// Which drivers an incoming input change stops.
//
// The rule lives in DuetSpiProtocol/StopRules.h and runs in DuetCANMaster, which is compiled for an
// ARM target and reaches it through a firmware task, so it cannot be tested where it runs. It is
// tested here instead, against the same struct and the same function the controller calls - not a
// copy of either. This suite is in the SBC-side tree only because that is where the tree's host-side
// C++ suites already build.
//
// It is worth testing at all because getting it wrong is silent. A driver that never matches simply
// runs its move to the full commanded length, and a driver that matches when it should not stops an
// axis that never reached anything and has it recorded as homed there.

#include <DuetSpiProtocol/StopRules.h>

#include <TestSupport.h>

using duet::spi::protocol::DriverStopWatch;
using duet::spi::protocol::HandleType;
using duet::spi::protocol::IsStallHandle;
using duet::spi::protocol::kHandleTypeEndstop;
using duet::spi::protocol::kHandleTypeStallEndstop;
using duet::spi::protocol::kMaxDriversPerBoard;
using duet::spi::protocol::WatchMatches;

namespace
{
	// The handle M574 registers a switch under: type 1, axis 2, switch 0
	constexpr uint16_t switchHandle = (kHandleTypeEndstop << 12) | (2u << 6);

	// The one handle every board reports every stalled driver under
	constexpr uint16_t stallHandle = kHandleTypeStallEndstop << 12;

	constexpr uint32_t Stalled(uint8_t driver) noexcept { return static_cast<uint32_t>(1) << driver; }
}

void TestHandleTypes() noexcept
{
	CHECK(HandleType(switchHandle) == kHandleTypeEndstop, "a switch handle carries its type");
	CHECK(!IsStallHandle(switchHandle), "and is not a stall");
	CHECK(IsStallHandle(stallHandle), "the stall handle is");
	CHECK(!IsStallHandle(0), "and an unset handle is not, so it cannot be read as one");
}

// A switch identifies itself. The handle names the axis and the port, so board and handle are the
// whole test - and the reading is the pin's value, which says nothing about who is watching it.
void TestASwitchMatchesOnBoardAndHandle() noexcept
{
	constexpr DriverStopWatch watch{ 1, 0, 3, switchHandle };

	CHECK(WatchMatches(watch, 3, switchHandle, 0), "the switch it watches stops it");
	CHECK(WatchMatches(watch, 3, switchHandle, 0xFFFFFFFF),
		  "whatever the reading, which for a switch is the pin and not a bitmap");
	CHECK(!WatchMatches(watch, 4, switchHandle, 0), "the same handle on another board does not");
	CHECK(!WatchMatches(watch, 3, switchHandle + 1, 0), "nor another switch of the same axis");
}

// A stall does not identify itself. Every board reports every driver that stalled under the one
// board-wide handle, so without the bitmap every armed driver on the reporting board stops whichever
// one stalled - and a move homing two stall-homed axes at once records the axis that did not stall
// as homed wherever it happened to be.
void TestAStallMatchesOnlyTheDriverThatStalled() noexcept
{
	// Two drivers of a move, both on board 3, both watching for their own stall. Phase 2 guarantees
	// the input board of a stall watch is the board carrying the driver
	constexpr DriverStopWatch x{ 3, 0, 3, stallHandle };
	constexpr DriverStopWatch y{ 3, 2, 3, stallHandle };

	CHECK(WatchMatches(x, 3, stallHandle, Stalled(0)), "the driver that stalled is stopped");
	CHECK(!WatchMatches(y, 3, stallHandle, Stalled(0)),
		  "and the one that did not keeps going, though it shares the board and the handle");

	CHECK(WatchMatches(y, 3, stallHandle, Stalled(2)), "the other way round for the other driver");
	CHECK(!WatchMatches(x, 3, stallHandle, Stalled(2)), "and not the driver that did not stall");

	// A board can report more than one driver in the one message
	CHECK(WatchMatches(x, 3, stallHandle, Stalled(0) | Stalled(2)), "both bits stop both drivers");
	CHECK(WatchMatches(y, 3, stallHandle, Stalled(0) | Stalled(2)), "each on its own bit");

	// An empty bitmap stops nothing. A board does not send one, but a message read as the wrong
	// version would produce one, and stopping every armed driver on that reading is exactly the
	// failure this bitmap exists to prevent
	CHECK(!WatchMatches(x, 3, stallHandle, 0), "an empty bitmap stops nothing");
}

// A stall on one board says nothing about a driver on another, whatever it stalled. The two boards
// number their drivers independently, so board 4 reporting driver 0 must not stop board 3's driver 0
void TestAStallDoesNotCrossBoards() noexcept
{
	constexpr DriverStopWatch onBoard3{ 3, 0, 3, stallHandle };

	CHECK(!WatchMatches(onBoard3, 4, stallHandle, Stalled(0)),
		  "another board's driver 0 stalling is not this driver stalling");
	CHECK(WatchMatches(onBoard3, 3, stallHandle, Stalled(0)), "its own board's is");
}

// The bitmap is as wide as a board has drivers. A driver number past the end cannot be in it, and
// shifting by it would be undefined rather than false
void TestADriverPastTheBitmapIsNotStalled() noexcept
{
	constexpr DriverStopWatch beyond{ 3, kMaxDriversPerBoard, 3, stallHandle };

	CHECK(!WatchMatches(beyond, 3, stallHandle, 0xFFFFFFFF),
		  "a driver past the width of the bitmap is never reported stalled");
}

// A drive that watches nothing carries no watch at all, so there is nothing here to test it with -
// but a watch whose fields happen to be zero must not be stopped by an unrelated board reporting
void TestAZeroedWatchIsNotMatchedByAnythingReal() noexcept
{
	constexpr DriverStopWatch zeroed{};

	CHECK(!WatchMatches(zeroed, 3, stallHandle, 0xFFFFFFFF), "a zeroed watch is not board 3's stall");
	CHECK(!WatchMatches(zeroed, 1, switchHandle, 0), "nor anybody's switch");
}

int main()
{
	std::printf("Stop rules:\n");
	TestHandleTypes();
	TestASwitchMatchesOnBoardAndHandle();
	TestAStallMatchesOnlyTheDriverThatStalled();
	TestAStallDoesNotCrossBoards();
	TestADriverPastTheBitmapIsNotStalled();
	TestAZeroedWatchIsNotMatchedByAnythingReal();
	return TestSupport::Summarise("Stop rules");
}
