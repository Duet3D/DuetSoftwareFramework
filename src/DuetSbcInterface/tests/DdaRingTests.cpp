// The offline DDA ring: moves go in as MoveParams, get planned against each other, get prepared,
// and come out as ScheduleMove packets. No hardware, no SPI, no CAN - a clock the test drives and a
// sink the test reads.
//
// This is the test that says the port works. Everything before it checks a piece; this checks that
// the pieces still add up to RepRapFirmware's lookahead: that a run of colinear moves melds into
// one continuous motion instead of stopping at every boundary, that each move starts exactly where
// the previous one ended in both speed and time, and that the phase durations account for the whole
// move. Those are the invariants that go wrong quietly - the machine still moves, just slower and
// noisier than it should.

#include <Motion/MoveParams.h>
#include <Motion/DDARing.h>
#include <Motion/MoveTiming.h>
#include <Motion/StepTimer.h>
#include <Motion/MotionSystem.h>

#include <TestSupport.h>

#include <vector>

using Duet::Sbc::Motion::MachineConfig;
using Duet::Sbc::Motion::MotionSystem;
using Duet::Sbc::Motion::MoveParamsDirectionVector;
using Duet::Sbc::Motion::MoveParamsEndPoints;
using Duet::Sbc::Motion::MoveParamsHeader;
using Duet::Sbc::Motion::MoveParamsLength;
using Duet::Sbc::Motion::ScheduleMoveSink;
namespace MoveFlags = Duet::Sbc::Motion::MoveFlags;
using duet::spi::protocol::ScheduleMoveDriver;
using duet::spi::protocol::ScheduleMoveHeader;
namespace ScheduleMoveFlags = duet::spi::protocol::ScheduleMoveFlags;

namespace
{
	// --- The clock the test drives ----------------------------------------------------------

	int64_t fakeNow = 0;

	int64_t FakeClock() noexcept { return fakeNow; }

	// Scale first, then divide: a nanoseconds-per-tick constant rounds down (1333 rather than
	// 1333.33 at 750kHz) and the fake clock then runs 0.025% slow, which is enough to make a wait
	// measured in whole ticks come up short.
	void AdvanceTicks(uint32_t ticks) noexcept
	{
		fakeNow += ((int64_t)ticks * 1000000000) / stepClockRate;
	}

	// --- The sink the test reads ------------------------------------------------------------

	class RecordingSink final : public ScheduleMoveSink
	{
	public:
		bool Send(std::span<const uint8_t> packet) noexcept override
		{
			headers.push_back(*reinterpret_cast<const ScheduleMoveHeader *>(packet.data()));

			// The steps a driver is told to take, which is the quantity a forced position has to
			// change: the ring differences the move's endpoint against the previous move's
			const auto *const drivers =
				reinterpret_cast<const ScheduleMoveDriver *>(packet.data() + sizeof(ScheduleMoveHeader));
			firstDriverSteps.push_back(headers.back().numDrivers > 0 ? drivers[0].steps : 0);
			return true;
		}

		[[nodiscard]] bool CanAccept() const noexcept override { return true; }

		void Clear() noexcept
		{
			headers.clear();
			firstDriverSteps.clear();
		}

		std::vector<ScheduleMoveHeader> headers;
		std::vector<int32_t> firstDriverSteps;
	};

	// --- The machine -------------------------------------------------------------------------

	constexpr size_t numAxes = 3;
	constexpr size_t numExtruders = 1;
	constexpr size_t extruderDrive = maxAxesPlusExtruders - 1;
	constexpr float stepsPerMm = 80.0F;

	// A modest printer: 100 mm/s, 1000 mm/s^2, 10 mm/s of allowed instantaneous speed change.
	constexpr float feedRate = 100.0F / stepClockRate;
	constexpr float acceleration = 1000.0F / ((float)stepClockRate * stepClockRate);
	constexpr float instantDv = 10.0F / stepClockRate;

	MotionSystem theMove;

	void ConfigureMachine() noexcept
	{
		MachineConfig config;
		config.numTotalAxes = numAxes;
		config.numExtruders = numExtruders;
		for (size_t drive = 0; drive < maxAxesPlusExtruders; ++drive)
		{
			config.driveStepsPerMm[drive] = stepsPerMm;
					}
		for (size_t axis = 0; axis < numAxes; ++axis)
		{
			config.axisDrivers[axis].numDrivers = 1;
			config.axisDrivers[axis].driverNumbers[0] = DriverId(1, (uint8_t)axis);
		}
		config.extruderDrivers[0] = DriverId(1, 3);
		theMove.Configure(config);
	}

	// --- Building a move ---------------------------------------------------------------------

	// One move along X, as DuetControlServer would send it: the endpoint in microsteps and a unit
	// direction vector, with the speed and acceleration already limited on that side.
	struct MoveRecord
	{
		alignas(uint32_t) char bytes[MoveParamsLength(maxAxesPlusExtruders)]{};

		[[nodiscard]] MoveParamsHeader& Header() noexcept { return *reinterpret_cast<MoveParamsHeader *>(bytes); }
	};

	MoveRecord MakeXMove(uint32_t moveId, float startX, float endX) noexcept
	{
		MoveRecord move;
		MoveParamsHeader& h = move.Header();
		h.moveId = moveId;
		h.ownedDrives = 0xFFFFFFFFu;
		h.flags = MoveFlags::canPauseAfter | MoveFlags::xyMoving | MoveFlags::usingStandardFeedrate;
		h.totalDistance = endX - startX;
		h.maxAcceleration = acceleration;
		h.requestedSpeed = feedRate;
		h.ringNumber = 0;
		h.numDrives = maxAxesPlusExtruders;

		const std::span<int32_t> endPoints = MoveParamsEndPoints(h);
		const std::span<float> directions = MoveParamsDirectionVector(h);
		for (size_t drive = 0; drive < maxAxesPlusExtruders; ++drive)
		{
			endPoints[drive] = 0;
			directions[drive] = 0.0F;
		}
		endPoints[xAxis] = lrintf(endX * stepsPerMm);
		directions[xAxis] = 1.0F;

		// The tuning a move carries is what it will be executed with; ConfigureMachine's values are
		// what DuetControlServer would have filled in here
		const std::span<Duet::Sbc::Motion::MoveDriveTuning> tuning = MoveParamsDriveTuning(h);
		for (auto& entry : tuning)
		{
			entry = {};
			entry.instantDv = instantDv;
			entry.printingInstantDv = instantDv;
		}
		h.backlashCorrectionDistanceFactor = 1;
		return move;
	}

	// A move that travels back down X, which is what makes a backlash correction due. The distance is
	// positive as it is for any move; it is the direction vector and the endpoint that reverse.
	MoveRecord MakeReverseXMove(uint32_t moveId, float fromX, float toX, int32_t backlashSteps) noexcept
	{
		MoveRecord move = MakeXMove(moveId, 0.0F, fromX - toX);
		MoveParamsHeader& h = move.Header();
		MoveParamsEndPoints(h)[xAxis] = lrintf(toX * stepsPerMm);
		MoveParamsDirectionVector(h)[xAxis] = -1.0F;
		MoveParamsDriveTuning(h)[xAxis].backlashSteps = backlashSteps;
		return move;
	}

	// Run the ring until it has nothing left, advancing the clock as a real machine would. Returns
	// false if it did not finish, which means a move is stuck rather than merely slow.
	bool Drain(DDARing& ring) noexcept
	{
		for (int spins = 0; spins < 20000; ++spins)
		{
			if (ring.IsIdle())
			{
				return true;
			}
			(void)ring.Spin(MoveTiming::usualMinimumPreparedTime, SimulationMode::Off, !ring.CanAddMove(), ring.ShouldStartMove());
			AdvanceTicks(stepClockRate / 1000);
		}
		return false;
	}

	// One move along a single axis with a chosen acceleration, for the rejection check below.
	MoveRecord MakeMoveWithAcceleration(uint32_t moveId, float accel) noexcept
	{
		MoveRecord move = MakeXMove(moveId, 0.0F, 50.0F);
		move.Header().maxAcceleration = accel;
		return move;
	}

	// --- Checks ------------------------------------------------------------------------------

	// A single move from and to a standstill: it accelerates, may hold, and decelerates back to
	// rest, and the three phases account for exactly the time the move was planned to take.
	// A move whose acceleration is zero has no finite duration: the time it takes is worked out by
	// dividing by the acceleration. The ring rejects it rather than queueing something it can never
	// prepare, which is right - but the rejection is far from the cause, so the axis simply stops
	// moving and the log says only "move duration too long". Everything that writes an axis
	// acceleration therefore has to keep it above zero, and everything that creates an axis has to
	// give it one; this is the check that says why.
	// The point of carrying tuning on the move: a value changed between two moves takes effect on the
	// move after it and not on the moves already queued, without the ring ever having to be empty.
	//
	// This is the M572 case from docs/devel/MOTION_CONFIG_ORDERING.md, using backlash because it
	// shows up directly in the steps the controller is told to take. Pressure advance and the rest
	// travel the same way.
	void TestTuningAppliesFromTheMoveThatCarriesIt(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		CHECK(Drain(ring), "the ring starts empty");

		// Establish a forward direction, so that the reversals below are what make a correction due
		CHECK(ring.AddMove(MakeXMove(1, 0.0F, 10.0F).Header()) == MovementError::Ok, "the priming move is queued");

		// Two reversals carrying no backlash, then one carrying 100 steps - all queued before any of
		// them is prepared, which is exactly the case a shared configuration gets wrong
		CHECK(ring.AddMove(MakeReverseXMove(2, 10.0F, 0.0F, 0).Header()) == MovementError::Ok, "the first reversal is queued");
		CHECK(ring.AddMove(MakeXMove(3, 0.0F, 10.0F).Header()) == MovementError::Ok, "a forward move is queued");
		CHECK(ring.AddMove(MakeReverseXMove(4, 10.0F, 0.0F, 100).Header()) == MovementError::Ok,
			  "the reversal carrying backlash is queued");
		CHECK(Drain(ring), "the ring drains");

		CHECK(sink.headers.size() == 4, "every move reached the controller");
		if (sink.firstDriverSteps.size() == 4)
		{
			const int32_t plain = lrintf(10.0F * stepsPerMm);
			CHECK(sink.firstDriverSteps[1] == -plain,
				  "the reversal queued before the change takes no correction");
			CHECK(sink.firstDriverSteps[3] == -plain - 100,
				  "the reversal that carries the change takes it, though both were queued together");
		}
	}

	void TestZeroAccelerationIsRejected(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		CHECK(Drain(ring), "the ring starts empty");

		MoveRecord bad = MakeMoveWithAcceleration(900, 0.0F);
		CHECK(ring.AddMove(bad.Header()) == MovementError::MoveDurationTooLong,
			  "a move with no acceleration is refused rather than queued");
		CHECK(sink.headers.empty(), "and nothing reaches the boards");

		// The ring is still usable: the rejected move must not have taken the add slot
		MoveRecord good = MakeMoveWithAcceleration(901, acceleration);
		CHECK(ring.AddMove(good.Header()) == MovementError::Ok, "a move with acceleration is accepted");
		CHECK(Drain(ring), "and runs");
	}

	void TestSingleMove(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		MoveRecord move = MakeXMove(1, 0.0F, 50.0F);
		CHECK(ring.CanAddMove(), "an empty ring accepts a move");
		CHECK(ring.AddMove(move.Header()) == MovementError::Ok, "the move is accepted");
		CHECK(ring.GetScheduledMoves() == 1, "the move is counted as scheduled");

		// Spin until it has been prepared and sent. Long enough to cover the grace period: the ring
		// deliberately holds the first move back for a moment in case more are coming.
		for (int i = 0; i < 200 && sink.headers.empty(); ++i)
		{
			(void)ring.Spin(MoveTiming::usualMinimumPreparedTime, SimulationMode::Off, !ring.CanAddMove(), ring.ShouldStartMove());
			AdvanceTicks(stepClockRate / 1000);
		}

		CHECK(sink.headers.size() == 1, "the move reaches the sink as one packet");
		if (sink.headers.empty())
		{
			return;
		}

		const ScheduleMoveHeader& h = sink.headers[0];
		CHECK(h.moveId == 1, "the packet quotes the move id DCS sent");
		CHECK((h.flags & ScheduleMoveFlags::LastPacket) != 0, "the packet is marked as the last of the move");
		CHECK_NEAR(h.startSpeed, 0.0, 1e-12, "a move from standstill starts at rest");
		CHECK_NEAR(h.endSpeed, 0.0, 1e-12, "a lone move ends at rest");
		CHECK(h.topSpeed > 0.0F, "the move reaches some speed");
		CHECK_NEAR(h.topSpeed, feedRate, feedRate * 0.01, "50mm is long enough to reach the requested speed");
		CHECK(h.accelClocks > 0, "the move accelerates");
		CHECK(h.decelClocks > 0, "the move decelerates");
		CHECK_NEAR(h.totalDistance, 50.0, 1e-3, "the distance is what was asked for");

		// The three phases must account for the whole move: a shortfall would leave the boards idle
		// mid-move, and an excess would have them still moving when the next move starts.
		const double byPhases = (double)h.accelClocks + h.steadyClocks + h.decelClocks;
		const double byDistance = 2.0 * (double)h.accelDistance / (double)(h.startSpeed + h.topSpeed)
								  + ((double)h.decelStartDistance - h.accelDistance) / (double)h.topSpeed
								  + 2.0 * ((double)h.totalDistance - h.decelStartDistance)
										/ (double)(h.topSpeed + h.endSpeed);
		CHECK_NEAR(byPhases, byDistance, byPhases * 0.001, "the phase durations account for the whole distance");

		CHECK(Drain(ring), "the move finishes");
	}

	// Nothing commits a move except the passage of time, so this is what decides whether the machine
	// moves at all. The ring holds the first move back briefly in case more are coming - lookahead
	// with one move in the queue can only plan a stop at the end of it - and then commits it whether
	// or not more arrived. Getting the second half of that wrong means a queue that fills and never
	// empties, which is exactly what happened when the decision was left to the caller and no caller
	// made it.
	void TestGracePeriodCommitsTheMove(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		CHECK(Drain(ring), "the ring starts empty");

		MoveRecord move = MakeXMove(500, 0.0F, 30.0F);
		CHECK(ring.AddMove(move.Header()) == MovementError::Ok, "the move is accepted");
		CHECK(!ring.ShouldStartMove(), "a move just added is held back in case more follow");

		// Spinning is not enough on its own: without the clock moving on, the move stays queued.
		for (int i = 0; i < 20; ++i)
		{
			(void)ring.Spin(MoveTiming::usualMinimumPreparedTime, SimulationMode::Off, !ring.CanAddMove(),
							ring.ShouldStartMove());
		}
		CHECK(sink.headers.empty(), "a move is not committed while the ring is still waiting for company");

		// Once the grace period has passed it goes, with nothing else having been added.
		AdvanceTicks(ring.GetGracePeriod() + 1);
		CHECK(ring.ShouldStartMove(), "the wait ends even though no more moves arrived");

		for (int i = 0; i < 200 && sink.headers.empty(); ++i)
		{
			(void)ring.Spin(MoveTiming::usualMinimumPreparedTime, SimulationMode::Off, !ring.CanAddMove(),
							ring.ShouldStartMove());
			AdvanceTicks(stepClockRate / 1000);
		}
		CHECK(!sink.headers.empty(), "the move is committed once the ring stops waiting");
		if (!sink.headers.empty())
		{
			CHECK(sink.headers[0].moveId == 500, "and it is the move that was queued");
		}
		CHECK(Drain(ring), "the move finishes");
	}

	// A run of colinear moves is one motion, not five: lookahead must carry speed across every
	// boundary, and each move must start where the last one ended, in speed and in time.
	void TestColinearRunMelds(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		constexpr int numMoves = 5;
		for (int i = 0; i < numMoves; ++i)
		{
			MoveRecord move = MakeXMove((uint32_t)i + 10, (float)i * 20.0F, (float)(i + 1) * 20.0F);
			CHECK(ring.AddMove(move.Header()) == MovementError::Ok, "each move of the run is accepted");
		}

		for (int i = 0; i < 20000 && sink.headers.size() < numMoves; ++i)
		{
			(void)ring.Spin(MoveTiming::usualMinimumPreparedTime, SimulationMode::Off, !ring.CanAddMove(), ring.ShouldStartMove());
			AdvanceTicks(stepClockRate / 1000);
		}

		CHECK(sink.headers.size() == numMoves, "every move of the run is sent");
		if (sink.headers.size() != numMoves)
		{
			return;
		}

		for (int i = 0; i < numMoves - 1; ++i)
		{
			const ScheduleMoveHeader& a = sink.headers[i];
			const ScheduleMoveHeader& b = sink.headers[i + 1];
			CHECK(a.endSpeed > 0.0F, "a move in the middle of a colinear run does not stop at its end");
			CHECK_NEAR(b.startSpeed, a.endSpeed, feedRate * 1e-3,
					   "each move starts at the speed the previous one ended at");
			const uint32_t expectedStart = a.whenToExecute + a.accelClocks + a.steadyClocks + a.decelClocks;
			CHECK(b.whenToExecute == expectedStart, "each move starts exactly when the previous one finishes");
		}
		CHECK_NEAR(sink.headers[numMoves - 1].endSpeed, 0.0, 1e-12, "the last move of the run comes to rest");

		// Leave the ring empty, so that the next test's move counts are its own.
		CHECK(Drain(ring), "the colinear run finishes");
	}

	// Moves retire in the order they were queued, and only once their time has passed.
	void TestRetirementInOrder(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		const uint32_t completedBefore = ring.GetCompletedMoves();
		for (int i = 0; i < 3; ++i)
		{
			MoveRecord move = MakeXMove((uint32_t)i + 100, (float)i * 10.0F, (float)(i + 1) * 10.0F);
			CHECK(ring.AddMove(move.Header()) == MovementError::Ok, "the move is accepted");
		}

		CHECK(Drain(ring), "the ring empties once the moves have run");
		CHECK(ring.GetCompletedMoves() == completedBefore + 3, "all three moves are counted as completed");

		// The ids arrive in order: the ring is a queue, and lookahead reorders speeds, not moves.
		CHECK(sink.headers.size() == 3, "all three moves were sent");
		for (size_t i = 0; i + 1 < sink.headers.size(); ++i)
		{
			CHECK(sink.headers[i].moveId + 1 == sink.headers[i + 1].moveId, "moves are sent in the order queued");
		}
	}

	// The ring is finite, and refusing a move is how back pressure reaches DCS.
	void TestRingSaturates(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		// Do not Spin: nothing is prepared or retired, so the ring can only fill up.
		int added = 0;
		while (ring.CanAddMove() && added < 1000)
		{
			MoveRecord move = MakeXMove((uint32_t)added + 200, (float)added * 5.0F, (float)(added + 1) * 5.0F);
			if (ring.AddMove(move.Header()) != MovementError::Ok)
			{
				break;
			}
			++added;
		}

		CHECK(added > 0, "the ring accepts some moves");
		CHECK(added < 1000, "the ring stops accepting moves rather than filling for ever");
		CHECK(!ring.CanAddMove(), "a full ring says so");

		CHECK(Drain(ring), "the ring drains again");
	}

	// The ring also throttles on time, not just on free slots: queueing more than a couple of
	// seconds of unprepared movement means a feed rate or extrusion change reaches the machine
	// seconds after the user asked for it. Long moves must hit that limit well before the ring
	// physically fills.
	void TestRingThrottlesOnTime(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		int added = 0;
		while (ring.CanAddMove() && added < 1000)
		{
			// 100mm at 100mm/s is a second of movement per move.
			MoveRecord move = MakeXMove((uint32_t)added + 400, (float)added * 100.0F, (float)(added + 1) * 100.0F);
			if (ring.AddMove(move.Header()) != MovementError::Ok)
			{
				break;
			}
			++added;
		}

		CHECK(added > 1, "the ring takes enough moves to look ahead with");
		CHECK(added < 10, "the ring stops on queued time, well before its 20 slots are full");
		CHECK(Drain(ring), "the throttled moves finish");
	}

	// A move the ring refuses must not take the add slot down with it.
	//
	// InitFromParams used to promote the DDA to Planned and only then return the error, which left
	// the slot permanently non-empty: CanAddMove was false for ever after, and Spin went on to
	// prepare and commit the very move that had just been rejected.
	void TestRejectedMoveLeavesTheRingUsable(DDARing& ring, RecordingSink& sink) noexcept
	{
		CHECK(Drain(ring), "the ring is idle before the check");
		sink.Clear();

		// Far enough at a low enough speed to need more than the 2^31 step clocks a move may take.
		MoveRecord bad = MakeXMove(500, 0.0F, 1000.0F);
		bad.Header().requestedSpeed = 1.0e-9F;
		bad.Header().maxAcceleration = 1.0e-18F;

		CHECK(ring.AddMove(bad.Header()) == MovementError::MoveDurationTooLong, "an impossibly long move is rejected");
		CHECK(ring.CanAddMove(), "the rejected move did not consume the add slot");

		// The real test of that: the ring still runs. A slot left provisional would either refuse
		// this move or execute the rejected one instead of it.
		MoveRecord good = MakeXMove(501, 0.0F, 10.0F);
		CHECK(ring.AddMove(good.Header()) == MovementError::Ok, "the ring still accepts moves");
		CHECK(Drain(ring), "the accepted move runs to completion");
		CHECK(sink.headers.size() == 1, "only the accepted move reached the sink");
	}

	// A move is turned into steps by differencing its endpoint against the previous move's, so the
	// endpoint the ring remembers is what every following move is measured from. Forcing it is how a
	// position established outside the ring - a homing switch, a probe, G92, or a move an endstop cut
	// short - reaches the drivers. Without it the next move travels the gap between where the machine
	// really is and where the last move meant to leave it, which is the whole of it after homing.
	void TestForcedEndpointIsWhatTheNextMoveIsMeasuredFrom(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		CHECK(Drain(ring), "the ring starts empty");

		// A move to 50mm that the machine only got 10mm into, as a homing move stopped short does
		MoveRecord cutShort = MakeXMove(700, 0.0F, 50.0F);
		CHECK(ring.AddMove(cutShort.Header()) == MovementError::Ok, "the move is accepted");
		CHECK(Drain(ring), "the move finishes");

		const int32_t stoppedAt = lrintf(10.0F * stepsPerMm);
		ring.SetLastEndpoint(xAxis, stoppedAt);
		CHECK(ring.GetLastEndpoint(xAxis) == stoppedAt, "the ring reports the position it was given");

		sink.Clear();
		MoveRecord next = MakeXMove(701, 10.0F, 20.0F);
		CHECK(ring.AddMove(next.Header()) == MovementError::Ok, "the following move is accepted");
		CHECK(Drain(ring), "the following move finishes");

		CHECK(sink.headers.size() == 1, "the following move reaches the sink");
		if (sink.headers.empty())
		{
			return;
		}
		CHECK(sink.firstDriverSteps[0] == lrintf(10.0F * stepsPerMm),
			  "the driver is told the distance from where the machine really is, not from where the "
			  "cut-short move was planned to end");
	}

	// Simulation works out how long a print would take without moving anything.
	void TestSimulationSendsNothing(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		for (int i = 0; i < 3; ++i)
		{
			MoveRecord move = MakeXMove((uint32_t)i + 300, (float)i * 10.0F, (float)(i + 1) * 10.0F);
			CHECK(ring.AddMove(move.Header()) == MovementError::Ok, "the move is accepted");
		}

		int spins = 0;
		while (!ring.IsIdle() && spins < 20000)
		{
			(void)ring.Spin(MoveTiming::usualMinimumPreparedTime, SimulationMode::Normal, !ring.CanAddMove(), ring.ShouldStartMove());
			AdvanceTicks(stepClockRate / 1000);
			++spins;
		}

		CHECK(ring.IsIdle(), "the simulated moves all retire");
		CHECK(sink.headers.empty(), "a simulated move reaches no board");
		CHECK(ring.GetSimulationTime() > 0.0F, "the simulation reports how long the moves would take");
	}
}

	// --- Stopping early ---------------------------------------------------------------------
	//
	// Two ways to stop the ring before it has run: PauseMoves looks for a junction the toolpath is
	// already slow enough to stop at, which is RepRapFirmware's, and Feedhold makes one. What both
	// have to get right is the boundary between what may be dropped and what may not - a move whose
	// segments have gone to the boards cannot be recalled - and that the moves left behind still add
	// up to a motion that comes to rest.
	//
	// DuetControlServer asks only for the feedhold now: every asynchronous pause stops that way. The
	// tests keep PauseMoves because it is the reference behaviour the deviation is measured against,
	// and the pair of them is what shows the difference is real - a run these tests can stop by
	// planning a deceleration is one the search declines to stop in at all.

	// A run of colinear moves along X, each starting where the last ended - which is what makes
	// lookahead raise the junction speeds between them, and so what makes the run one that
	// RepRapFirmware's search cannot stop in. Nothing is spun, so every move is still provisional.
	//
	// `restartable` clears the flag that says a print may resume from the end of a move, which is
	// how an arc segment or a retraction reaches the ring.
	unsigned int FillWithRun(DDARing& ring, unsigned int count, float lengthMm, uint32_t firstMoveId,
							 bool restartable = true) noexcept
	{
		unsigned int added = 0;
		for (unsigned int i = 0; i < count && ring.CanAddMove(); ++i)
		{
			MoveRecord move = MakeXMove(firstMoveId + i, (float)i * lengthMm, (float)(i + 1) * lengthMm);
			if (!restartable)
			{
				move.Header().flags &= ~MoveFlags::canPauseAfter;
			}
			if (ring.AddMove(move.Header()) != MovementError::Ok)
			{
				break;
			}
			++added;
		}
		return added;
	}

	// A ring of moves that never slow down between them melds into one run, so there is no junction
	// at or below jerk anywhere in it. That is the case RepRapFirmware cannot stop early in, and it
	// is the ordinary state of a machine printing at speed - so the answer has to be "no", not a
	// stop at a junction the machine could not actually have taken.
	void TestPauseMovesFindsNothingInAFastRun(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		CHECK(Drain(ring), "the ring starts empty so the counts below are this test's own");
		const uint32_t scheduledBefore = ring.GetScheduledMoves();
		const unsigned int added = FillWithRun(ring, 8, 50.0F, 100);
		CHECK(added >= 4, "the ring took enough moves to have junctions in it");

		DDARing::FeedholdOutcome outcome{};
		const bool stopped = ring.PauseMoves(outcome);
		CHECK(!stopped, "a run that never drops to jerk speed offers nowhere to stop");
		CHECK(outcome.movesPurged == 0, "nothing is dropped when there is nowhere to stop");
		CHECK(ring.GetScheduledMoves() == scheduledBefore + added, "the moves are all still scheduled");

		CHECK(Drain(ring), "the ring still finishes normally afterwards");
	}

	// The feedhold's whole purpose: where PauseMoves finds nothing, this makes a stopping point. What
	// it must not do is drop everything - the machine has to be left enough distance to decelerate in.
	void TestFeedholdStopsWhereAPauseCannot(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		CHECK(Drain(ring), "the ring starts empty");
		const unsigned int added = FillWithRun(ring, 8, 50.0F, 110);
		CHECK(added >= 4, "the ring took enough moves to stop within");
		const uint32_t scheduledBefore = ring.GetScheduledMoves();

		DDARing::FeedholdOutcome outcome{};
		const bool stopped = ring.Feedhold(outcome);
		CHECK(stopped, "the feedhold finds a stopping point where PauseMoves found none");
		CHECK(outcome.movesPurged > 0, "it drops the moves after the stop");
		CHECK(outcome.movesPurged < added, "it leaves the machine somewhere to decelerate");
		CHECK(outcome.firstPurgedMoveId >= 110, "it names the first move it dropped");
		CHECK(ring.GetScheduledMoves() == scheduledBefore - outcome.movesPurged,
			  "the scheduled count drops by exactly what was purged");

		CHECK(Drain(ring), "what is left of the ring runs to a standstill");
	}

	// The first move keeps the start speed it already had, because the move before it is committed
	// and its end speed has already gone to the boards. A feedhold that reduced it would be asking
	// the machine to arrive at a speed it was never told to reach.
	void TestFeedholdLeavesCommittedMovesAlone(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		CHECK(Drain(ring), "the ring starts empty");
		const unsigned int added = FillWithRun(ring, 8, 50.0F, 120);
		CHECK(added >= 4, "the ring took enough moves");

		// Let the front of the ring commit and reach the boards
		for (int i = 0; i < 2000 && sink.headers.empty(); ++i)
		{
			(void)ring.Spin(MoveTiming::usualMinimumPreparedTime, SimulationMode::Off, true, true);
			AdvanceTicks(stepClockRate / 1000);
		}
		const size_t sentBeforeStop = sink.headers.size();
		CHECK(sentBeforeStop > 0, "at least one move has been committed and sent");

		DDARing::FeedholdOutcome outcome{};
		(void)ring.Feedhold(outcome);

		// Nothing already sent may be taken back, and the count of what was sent cannot fall
		CHECK(sink.headers.size() >= sentBeforeStop, "a feedhold does not unsend a committed move");
		CHECK(outcome.movesPurged <= added - sentBeforeStop,
			  "it purges only moves that had not been committed");

		CHECK(Drain(ring), "the ring finishes after the stop");
	}

	// A move that is not a restartable boundary - one segment of an arc, a retraction - is not a
	// place a print can resume from however much room there is to stop in. Neither kind of stop may
	// choose one.
	void TestNeitherStopChoosesAnUnrestartableBoundary(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();

		CHECK(Drain(ring), "the ring starts empty");
		const unsigned int added = FillWithRun(ring, 8, 50.0F, 200, /*restartable=*/false);
		CHECK(added >= 4, "the ring took the indivisible moves");
		const uint32_t scheduledBefore = ring.GetScheduledMoves();

		DDARing::FeedholdOutcome pauseOutcome{};
		CHECK(!ring.PauseMoves(pauseOutcome), "PauseMoves will not stop inside an indivisible run");

		DDARing::FeedholdOutcome feedholdOutcome{};
		CHECK(!ring.Feedhold(feedholdOutcome), "the feedhold will not stop inside one either");
		CHECK(feedholdOutcome.movesPurged == 0, "so nothing is dropped");
		CHECK(ring.GetScheduledMoves() == scheduledBefore, "and every move is still scheduled");

		CHECK(Drain(ring), "the indivisible run finishes intact");
	}

	// An empty ring has nothing to stop, and asking must not walk off the end of it.
	void TestStoppingAnEmptyRingDoesNothing(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		CHECK(Drain(ring), "the ring starts empty");

		DDARing::FeedholdOutcome outcome{};
		CHECK(!ring.Feedhold(outcome), "a feedhold on an empty ring stops nothing");
		CHECK(!ring.PauseMoves(outcome), "and neither does a pause");
		CHECK(outcome.movesPurged == 0, "nothing is dropped");
		CHECK(ring.CanAddMove(), "the ring is still usable");
	}

	// After a stop the ring must be usable again: the add pointer has moved back over the moves that
	// were dropped, and a move queued next has to go in behind the one the machine will stop at.
	void TestRingIsUsableAfterAStop(DDARing& ring, RecordingSink& sink) noexcept
	{
		sink.Clear();
		CHECK(Drain(ring), "the ring starts empty");
		const unsigned int added = FillWithRun(ring, 8, 50.0F, 210);
		CHECK(added >= 4, "the ring took enough moves");

		DDARing::FeedholdOutcome outcome{};
		CHECK(ring.Feedhold(outcome), "the feedhold stops the ring");
		CHECK(Drain(ring), "what was left runs out");

		MoveRecord resumed = MakeXMove(300, 0.0F, 20.0F);
		CHECK(ring.CanAddMove(), "the ring accepts moves again");
		CHECK(ring.AddMove(resumed.Header()) == MovementError::Ok, "a move queued after the stop is taken");
		CHECK(Drain(ring), "and it runs");
	}

int main()
{
	StepTimer::Init();
	StepTimer::SetLocalClockSource(FakeClock);
	if (!theMove.Init())
	{
		std::printf("FAIL: could not initialise the motion system\n");
		return 1;
	}
	ConfigureMachine();

	RecordingSink sink;
	theMove.GetScheduleMoveBuilder().SetSink(&sink);

	DDARing ring;
	ring.Init(theMove, 20);

	TestSingleMove(ring, sink);
	TestGracePeriodCommitsTheMove(ring, sink);
	TestColinearRunMelds(ring, sink);
	TestRetirementInOrder(ring, sink);
	TestRingSaturates(ring, sink);
	TestRingThrottlesOnTime(ring, sink);
	TestRejectedMoveLeavesTheRingUsable(ring, sink);
	TestForcedEndpointIsWhatTheNextMoveIsMeasuredFrom(ring, sink);
	TestSimulationSendsNothing(ring, sink);
	TestTuningAppliesFromTheMoveThatCarriesIt(ring, sink);
	TestZeroAccelerationIsRejected(ring, sink);
	TestPauseMovesFindsNothingInAFastRun(ring, sink);
	TestFeedholdStopsWhereAPauseCannot(ring, sink);
	TestFeedholdLeavesCommittedMovesAlone(ring, sink);
	TestNeitherStopChoosesAnUnrestartableBoundary(ring, sink);
	TestStoppingAnEmptyRingDoesNothing(ring, sink);
	TestRingIsUsableAfterAStop(ring, sink);

	StepTimer::SetLocalClockSource(nullptr);
	return TestSupport::Summarise("DDARing");
}
