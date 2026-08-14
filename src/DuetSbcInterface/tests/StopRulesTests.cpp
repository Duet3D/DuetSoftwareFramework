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

#include <span>

using duet::spi::protocol::ActiveInput;
using duet::spi::protocol::DriverStopWatch;
using duet::spi::protocol::HandleType;
using duet::spi::protocol::IsStallHandle;
using duet::spi::protocol::kHandleTypeEndstop;
using duet::spi::protocol::kHandleTypeStallEndstop;
using duet::spi::protocol::kHandleTypeZProbe;
using duet::spi::protocol::kMaxDriversPerBoard;
using duet::spi::protocol::NoteInputState;
using duet::spi::protocol::StopAction;
using duet::spi::protocol::StopDecision;
using duet::spi::protocol::StopsDriver;
using duet::spi::protocol::DecideStop;
using duet::spi::protocol::WatchMatches;

namespace
{
	// The handle M574 registers a switch under: type 1, axis 2, switch 0
	constexpr uint16_t switchHandle = (kHandleTypeEndstop << 12) | (2u << 6);

	// The one handle every board reports every stalled driver under
	constexpr uint16_t stallHandle = kHandleTypeStallEndstop << 12;

	// A probe, which a move can be stopped by, and a general purpose input, which it cannot
	constexpr uint16_t probeHandle = kHandleTypeZProbe << 12;
	constexpr uint16_t gpInHandle = 2u << 12;

	constexpr uint32_t Stalled(uint8_t driver) noexcept { return static_cast<uint32_t>(1) << driver; }

	bool Held(std::span<const ActiveInput> inputs, size_t count, uint8_t board, uint16_t handle) noexcept
	{
		for (size_t i = 0; i < count; ++i)
		{
			if (inputs[i].board == board && inputs[i].handle == handle)
			{
				return true;
			}
		}
		return false;
	}
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
	constexpr DriverStopWatch watch{ 1, 0, 3, switchHandle, 0, StopAction::group, true };

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
	constexpr DriverStopWatch x{ 3, 0, 3, stallHandle, 2, StopAction::group, true };
	constexpr DriverStopWatch y{ 3, 2, 3, stallHandle, 2, StopAction::group, true };

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
	constexpr DriverStopWatch onBoard3{ 3, 0, 3, stallHandle, 2, StopAction::group, true };

	CHECK(!WatchMatches(onBoard3, 4, stallHandle, Stalled(0)),
		  "another board's driver 0 stalling is not this driver stalling");
	CHECK(WatchMatches(onBoard3, 3, stallHandle, Stalled(0)), "its own board's is");
}

// The bitmap is as wide as a board has drivers. A driver number past the end cannot be in it, and
// shifting by it would be undefined rather than false
void TestADriverPastTheBitmapIsNotStalled() noexcept
{
	constexpr DriverStopWatch beyond{ 3, kMaxDriversPerBoard, 3, stallHandle, 2, StopAction::group, true };

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

// stopAll: every driver of the move goes, whichever endstop fired. The drives are coupled, so
// letting the others run on would drag the head into the switch.
void TestStopAllStopsEveryDriverOfTheMove() noexcept
{
	const DriverStopWatch watches[] = {
		{ 1, 0, 3, switchHandle, 0, StopAction::all, true },
		{ 1, 1, 3, switchHandle, 1, StopAction::all, true },
		{ 2, 0, 9, switchHandle, 2, StopAction::all, true },   // another drive, another board entirely
	};
	const std::span all{ watches };

	const StopDecision decision = DecideStop(all, 3, switchHandle, 0);
	CHECK(decision.action == StopAction::all, "the endstop that fired says stop everything");
	for (size_t i = 0; i < all.size(); ++i)
	{
		CHECK(StopsDriver(all, decision, i), "so every driver of the move stops");
	}
}

// stopAxis: every motor of the drive goes, and nothing else does. The drive is the only thing that
// ties them together - a dual-motor axis may have its motors on two boards, under two driver
// numbers, so nothing about the trigger itself names the motor that did not stall.
void TestGroupStopsTheDriveAndNothingElse() noexcept
{
	const DriverStopWatch watches[] = {
		{ 3, 0, 3, stallHandle, 2, StopAction::group, true },  // Z motor 1, its own board
		{ 4, 0, 4, stallHandle, 2, StopAction::group, true },  // Z motor 2, a different board
		{ 5, 0, 5, stallHandle, 0, StopAction::group, true },  // X, a different drive
	};
	const std::span all{ watches };

	// Board 3's motor stalls. Board 4's is a different board and a different driver number, so
	// nothing about the trigger names it - only the group does
	const StopDecision decision = DecideStop(all, 3, stallHandle, Stalled(0));
	CHECK(decision.action == StopAction::group, "an S3 stall stops the drive");
	CHECK(StopsDriver(all, decision, 0), "the motor that stalled");
	CHECK(StopsDriver(all, decision, 1), "and the one on the other board, which is the whole point");
	CHECK(!StopsDriver(all, decision, 2), "but not a drive that was not stopped");
}

// stopDriver, and the escalation that makes it usable: each motor stops where it stalled, which is
// what squares a gantry, and the last one left stops the drive. Without the escalation the last
// motor would stop alone and the move would run on with nothing to end it.
void TestIndividualStopsOneMotorUntilItIsTheLast() noexcept
{
	DriverStopWatch watches[] = {
		{ 3, 0, 3, stallHandle, 2, StopAction::driver, true },
		{ 3, 1, 3, stallHandle, 2, StopAction::driver, true },
		{ 3, 2, 3, stallHandle, 2, StopAction::driver, true },
	};
	const std::span all{ watches };

	// Three motors running: the first to stall stops alone
	StopDecision decision = DecideStop(all, 3, stallHandle, Stalled(0));
	CHECK(decision.action == StopAction::driver, "with others still running, only this motor stops");
	CHECK(StopsDriver(all, decision, 0), "the motor that stalled stops");
	CHECK(!StopsDriver(all, decision, 1), "the others keep running on to their own stalls");
	CHECK(!StopsDriver(all, decision, 2), "however many of them there are");
	watches[0].stillRunning = false;

	// Two left: still individual
	decision = DecideStop(all, 3, stallHandle, Stalled(1));
	CHECK(decision.action == StopAction::driver, "and again with one still running behind it");
	watches[1].stillRunning = false;

	// One left: it has nothing to square against, so it stops the drive
	decision = DecideStop(all, 3, stallHandle, Stalled(2));
	CHECK(decision.action == StopAction::group, "the last motor of the drive stops the drive");
	CHECK(StopsDriver(all, decision, 2), "including itself");
}

// A driver already stopped by this move is not stopped again, and stops counting towards its group.
// Both matter: a second stop would be reported twice, and a stopped motor still counted would keep
// the escalation from ever firing.
void TestAStoppedDriverIsNotStoppedAgain() noexcept
{
	const DriverStopWatch watches[] = {
		{ 3, 0, 3, stallHandle, 2, StopAction::driver, false },  // already stopped
		{ 3, 1, 3, stallHandle, 2, StopAction::driver, true },
	};
	const std::span all{ watches };

	CHECK(DecideStop(all, 3, stallHandle, Stalled(0)).action == StopAction::none,
		  "a driver this move already stopped matches nothing");

	const StopDecision decision = DecideStop(all, 3, stallHandle, Stalled(1));
	CHECK(decision.action == StopAction::group,
		  "and does not count towards its group, so the one left escalates");
}

// A driver in no group stops alone under `group`, rather than sweeping up every other driver that
// also has no group. Nothing builds such a move today; the rule matters because kNoStopGroup is what
// a driver watching nothing carries, and those share the value.
void TestNoGroupDoesNotStopEveryOtherUngroupedDriver() noexcept
{
	const DriverStopWatch watches[] = {
		{ 3, 0, 3, stallHandle, duet::spi::protocol::kNoStopGroup, StopAction::group, true },
		{ 3, 1, 0, 0, duet::spi::protocol::kNoStopGroup, StopAction::none, true },
	};
	const std::span all{ watches };

	const StopDecision decision = DecideStop(all, 3, stallHandle, Stalled(0));
	CHECK(decision.action == StopAction::group, "the action is what the move asked for");
	CHECK(!StopsDriver(all, decision, 0), "a driver in no group is not stopped by a group action");
	CHECK(!StopsDriver(all, decision, 1), "and neither is anything else that has no group");
}

// A trigger nothing watches stops nothing at all, which is the ordinary case: a board reports every
// input change, and most of them belong to no move in flight.
void TestAnUnwatchedTriggerStopsNothing() noexcept
{
	const DriverStopWatch watches[] = {
		{ 3, 0, 3, stallHandle, 2, StopAction::group, true },
	};
	const std::span all{ watches };

	CHECK(DecideStop(all, 9, stallHandle, Stalled(0)).action == StopAction::none, "another board");
	CHECK(DecideStop(all, 3, switchHandle, 0).action == StopAction::none, "another kind of input");
	CHECK(DecideStop(all, 3, stallHandle, Stalled(1)).action == StopAction::none, "another driver");
	CHECK(DecideStop({}, 3, stallHandle, Stalled(0)).action == StopAction::none, "or no move at all");
}

// A board reports an input when it changes, so an input that goes active while a move is on its way
// to the controller is reported before there is any watch to match it against. Holding the level is
// what lets that move still be stopped rather than driving into a closed switch
void TestAnInputIsHeldFromWhenItGoesActiveUntilItGoesInactive() noexcept
{
	ActiveInput inputs[4];
	const std::span all{ inputs };

	size_t count = NoteInputState(all, 0, 3, switchHandle, true);
	CHECK(count == 1, "an input that went active is held");

	count = NoteInputState(all, count, 3, switchHandle, true);
	CHECK(count == 1, "and reporting it again does not hold it twice");

	count = NoteInputState(all, count, 3, switchHandle, false);
	CHECK(count == 0, "until it goes inactive");
}

void TestInputsAreToldApartByBoardAndHandle() noexcept
{
	ActiveInput inputs[4];
	const std::span all{ inputs };

	size_t count = NoteInputState(all, 0, 3, switchHandle, true);
	count = NoteInputState(all, count, 4, switchHandle, true);
	count = NoteInputState(all, count, 3, switchHandle + 1, true);
	CHECK(count == 3, "the same handle on another board is another input");

	// Clearing takes the last entry to fill the hole, so the one cleared has to be the one that goes
	count = NoteInputState(all, count, 4, switchHandle, false);
	CHECK(count == 2, "and clearing one clears only it");
	CHECK(Held(all, count, 3, switchHandle) && Held(all, count, 3, switchHandle + 1), "leaving the others");
}

// Only what a move can be stopped by is held. A stall is excluded for a reason of its own: it says a
// driver failed to keep up with the move that was running, not a level the input can be found at,
// and held it would stop the next move armed on the same handle before it turned a step
void TestOnlyWhatCanStopAMoveIsHeld() noexcept
{
	ActiveInput inputs[4];
	const std::span all{ inputs };

	CHECK(NoteInputState(all, 0, 3, switchHandle, true) == 1, "a switch is held");
	CHECK(NoteInputState(all, 0, 3, probeHandle, true) == 1, "so is a probe");
	CHECK(NoteInputState(all, 0, 3, stallHandle, true) == 0, "a stall is not a level");
	CHECK(NoteInputState(all, 0, 3, gpInHandle, true) == 0,
		  "and an input no move watches would only crowd out one it does");
}

// Losing an input leaves the window open for it, which is what there was before any of this. What
// must not happen is the reverse: an input held that nothing can clear would stop every later move
void TestAnInputThatDoesNotFitIsSimplyNotHeld() noexcept
{
	ActiveInput inputs[2];
	const std::span all{ inputs };

	size_t count = NoteInputState(all, 0, 3, switchHandle, true);
	count = NoteInputState(all, count, 3, switchHandle + 1, true);
	count = NoteInputState(all, count, 3, switchHandle + 2, true);
	CHECK(count == 2, "the third does not fit");

	count = NoteInputState(all, count, 3, switchHandle + 2, false);
	CHECK(count == 2, "and clearing one that was never held changes nothing");
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
	TestStopAllStopsEveryDriverOfTheMove();
	TestGroupStopsTheDriveAndNothingElse();
	TestIndividualStopsOneMotorUntilItIsTheLast();
	TestAStoppedDriverIsNotStoppedAgain();
	TestNoGroupDoesNotStopEveryOtherUngroupedDriver();
	TestAnUnwatchedTriggerStopsNothing();
	TestAnInputIsHeldFromWhenItGoesActiveUntilItGoesInactive();
	TestInputsAreToldApartByBoardAndHandle();
	TestOnlyWhatCanStopAMoveIsHeld();
	TestAnInputThatDoesNotFitIsSimplyNotHeld();
	return TestSupport::Summarise("Stop rules");
}
