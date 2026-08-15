/*
 * MotionService.cpp - see MotionService.h.
 */

#include "MotionService.h"

#include "SbcInterface.h"

#include <Movement/MoveTiming.h>
#include <Movement/StepTimer.h>
#include <Platform/ProcessHelpers.h>

#include <chrono>
#include <cstring>

namespace Duet::Sbc
{

	namespace
	{
		// Room for a good many moves in flight. A submission that does not fit is reported to the
		// caller rather than dropped, so this only has to absorb the gap between DCS handing moves
		// over in bursts and the motion thread taking them up.
		constexpr size_t kSubmissionCapacity = 256 * 1024;

		// Forced positions are rare - homing, probing, G92, a reconfiguration - and the motion thread
		// takes every one of them within a millisecond. Room for a burst, not for a backlog.
		constexpr size_t kForcedPositionCapacity = 8 * 1024;

		// The longest the motion thread sleeps when the rings say there is nothing to do. Short
		// enough that a move submitted while it sleeps is still prepared well inside its lead time.
	}

	MotionService::MotionService(SbcInterface& link)
		: m_link(&link)
		, m_sink(link)
		, m_submissions(kSubmissionCapacity)
		, m_forcedPositions(kForcedPositionCapacity)
	{
		// The controller stops an endstop move itself. Where the drives should end up is decided in
		// DuetControlServer, from the positions this side can evaluate - see GetPositionAt - so all
		// that happens here is handing the report on
		link.SetMotionStoppedCallback(
			[this](uint32_t whenTriggered, uint32_t moveId,
				   std::span<const duet::spi::protocol::MotionStoppedDriver> drivers)
			{
				PostMotionStopped(whenTriggered, moveId, drivers);
			});
	}

	MotionService::~MotionService()
	{
		Stop();
	}

	bool MotionService::Init()
	{
		if (m_initialised)
		{
			return true;
		}
		if (!m_move.Init())
		{
			return false;
		}
		m_move.GetScheduleMoveBuilder().SetSink(&m_sink);

		const Motion::MotionConfig& config = m_move.GetConfig();
		for (unsigned int i = 0; i < numRings; ++i)
		{
			m_rings[i].Init(m_move, config.numDdasPerRing);
			m_rings[i].SetGracePeriod(MillisToStepClocks(config.gracePeriodMs));

			// The callback context carries the ring index as well as `this`. The DDA does not know
			// which ring it belongs to, and the events it produces have to name one.
			m_retirementContext[i] = RetirementContext{this, i};
			m_rings[i].SetRetirementCallback(&MotionService::OnMoveRetired, &m_retirementContext[i]);
		}
		m_initialised = true;
		return true;
	}

	void MotionService::Configure(const Motion::MotionConfig& config)
	{
		m_move.Configure(config);
	}

	void MotionService::Start(int rtPriority)
	{
		if (m_thread.joinable())
		{
			return;
		}
		m_stop.store(false, std::memory_order_release);
		m_thread = std::thread([this, rtPriority] {
			if (rtPriority > 0)
			{
				// Below the interface thread's, deliberately: a late transfer loses the link, while
				// a late move preparation only costs a hiccup that every board slips by together.
				(void)SetCurrentThreadRealtimePriority(rtPriority);
			}
			Run();
		});
	}

	void MotionService::Stop()
	{
		m_stop.store(true, std::memory_order_release);
		if (m_thread.joinable())
		{
			m_thread.join();
		}
	}

	void MotionService::Run()
	{
		while (!m_stop.load(std::memory_order_acquire))
		{
			SpinOnce();
			// A fixed tick rather than the interval the rings ask for. Spin returns how long it may
			// be before more preparation is needed, but a move submitted a moment later needs
			// picking up sooner than that, and there is no cost to asking again.
			std::this_thread::sleep_for(std::chrono::milliseconds(1));
		}
	}

	void MotionService::SpinOnce()
	{
		// Before the submissions, so that a move queued after a position was forced is planned from
		// that position rather than from the one it replaced
		DrainForcedPositions();
		DrainSubmissions();

		for (unsigned int i = 0; i < numRings; ++i)
		{
			if (m_ringState[i].waitingForEmpty.load(std::memory_order_relaxed))
			{
				(void)m_rings[i].SetWaitingToEmpty();
			}

			// Both arguments as upstream's Move::Spin computes them. shouldStartMove in particular
			// is local timing - hold off briefly so lookahead has moves to work with, but not for
			// ever - and not something DuetControlServer can answer. It may still force the issue,
			// which is what the flag it sets is for.
			const bool shouldStartMove =
				m_rings[i].ShouldStartMove() || m_ringState[i].shouldStartMove.load(std::memory_order_relaxed);
			const bool signalMoveCompletion = !m_rings[i].CanAddMove();

			(void)m_rings[i].Spin(
				MoveTiming::usualMinimumPreparedTime, SimulationMode::Off, signalMoveCompletion, shouldStartMove);
		}

		m_move.AdvanceTrackers(StepTimer::GetMovementTimerTicks());
		PublishPositions();
	}

	bool MotionService::IsWellFormedSubmission(std::span<const uint8_t> record) noexcept
	{
		if (record.data() == nullptr || record.size() < sizeof(Motion::MoveParamsHeader))
		{
			return false;
		}

		// numDrives says where the direction vector starts and how far both trailing arrays run, so
		// it decides what InitFromParams reads. A record that does not carry the drives it claims
		// would have it read past the end - checking the header alone is not enough.
		const auto& params = *reinterpret_cast<const Motion::MoveParamsHeader *>(record.data());
		return params.numDrives <= maxAxesPlusExtruders
			&& record.size() >= Motion::MoveParamsLength(params.numDrives);
	}

	void MotionService::DrainSubmissions()
	{
		while (const std::optional<ByteSpan> record = m_submissions.Peek())
		{
			if (!IsWellFormedSubmission(*record))
			{
				m_submissions.Consume();
				continue;
			}

			const auto& params = *reinterpret_cast<const Motion::MoveParamsHeader *>(record->data());
			const unsigned int ring = (params.ringNumber < numRings) ? params.ringNumber : 0;

			// Stop taking moves once the ring is full, and leave the rest where they are: the ring
			// is a queue, so a move taken out of order would be planned against the wrong neighbour.
			if (!m_rings[ring].CanAddMove())
			{
				break;
			}

			const MovementError err = m_rings[ring].AddMove(params);
			if (err != MovementError::Ok && err != MovementError::NoMovement)
			{
				PostMoveFailed(ring, params.moveId, err);
			}
			else if (err == MovementError::NoMovement)
			{
				// Nothing to do, but DCS is still waiting to hear that this move is done with.
				PostMoveCompleted(ring, params.moveId);
			}
			m_submissions.Consume();
		}
	}

	void MotionService::PublishPositions()
	{
		// Seqlock: readers see an odd sequence while the write is in progress and try again. This
		// replaces the firmware's "turn the step interrupt off", which is not available here and
		// would have been the wrong shape anyway - the reader is a managed thread that may stop for
		// a garbage collection in the middle of the read.
		const uint32_t sequence = m_snapshotSequence.load(std::memory_order_relaxed);
		m_snapshotSequence.store(sequence + 1, std::memory_order_release);
		std::atomic_thread_fence(std::memory_order_release);

		const uint32_t now = StepTimer::GetMovementTimerTicks();
		m_snapshot.whenTicks = now;
		// The spans are deduced from the arrays, so the lengths cannot drift from the things they
		// describe
		m_move.GetMotorPositions(m_snapshot.positions);
		m_move.GetLivePositions(m_snapshot.livePositions, now);

		std::atomic_thread_fence(std::memory_order_release);
		m_snapshotSequence.store(sequence + 2, std::memory_order_release);
	}

	size_t MotionService::GetMotorPositions(std::span<int32_t> positions, uint32_t *whenTicks) const
	{
		return ReadSnapshot(positions, whenTicks, m_snapshot.positions);
	}

	size_t MotionService::GetLivePositions(std::span<int32_t> positions, uint32_t *whenTicks) const
	{
		return ReadSnapshot(positions, whenTicks, m_snapshot.livePositions);
	}

	// Both readers share the retry loop: the seqlock protects the whole snapshot, so which array is
	// being read makes no difference to how it has to be read.
	size_t MotionService::ReadSnapshot(std::span<int32_t> positions, uint32_t *whenTicks,
									   const int32_t (&source)[maxAxesPlusExtruders]) const
	{
		if (positions.empty())
		{
			return 0;
		}
		const size_t toCopy = std::min(positions.size(), maxAxesPlusExtruders);

		for (;;)
		{
			const uint32_t before = m_snapshotSequence.load(std::memory_order_acquire);
			if ((before & 1u) != 0)
			{
				continue;					// a write is in progress
			}
			std::atomic_thread_fence(std::memory_order_acquire);

			const uint32_t when = m_snapshot.whenTicks;
			std::memcpy(positions.data(), source, toCopy * sizeof(int32_t));

			std::atomic_thread_fence(std::memory_order_acquire);
			if (m_snapshotSequence.load(std::memory_order_relaxed) == before)
			{
				if (whenTicks != nullptr)
				{
					*whenTicks = when;
				}
				return toCopy;
			}
		}
	}

	bool MotionService::SetMotorPositions(uint32_t driveMask, std::span<const int32_t> positions)
	{
		// A drive the caller did not supply a position for cannot be set, whatever the mask says.
		// maxAxesPlusExtruders is also the width of the mask, so a full array names every drive
		const size_t count = std::min(positions.size(), maxAxesPlusExtruders);
		static_assert(maxAxesPlusExtruders <= 32, "a drive mask is 32 bits wide");
		const uint32_t suppliedDrives =
			(count >= maxAxesPlusExtruders) ? 0xFFFFFFFFu : ((1u << count) - 1u);

		ForcedPositions forced;
		forced.driveMask = driveMask & suppliedDrives;
		std::memcpy(forced.positions, positions.data(), count * sizeof(int32_t));
		return m_forcedPositions.Write(AsBytes(forced));
	}

	void MotionService::DrainForcedPositions()
	{
		while (const std::optional<ByteSpan> record = m_forcedPositions.Peek())
		{
			if (record->size() >= sizeof(ForcedPositions))
			{
				ForcedPositions forced{};
				std::memcpy(&forced, record->data(), sizeof(forced));

				const LogicalDrivesBitmap drives{forced.driveMask};
				m_move.SetMotorPositions(drives, forced.positions);

				// The rings hold the endpoint each drive was last planned to, and DDA::Prepare turns
				// a move into steps by differencing against it. Leaving that behind would make the
				// next move travel the gap between where the machine was told it is and where the
				// last move meant to leave it - the whole of the homing move, after homing.
				for (auto& ring : m_rings)
				{
					ring.SetLastEndpoints(drives, forced.positions);
				}
				m_forcedPositionsApplied.fetch_add(1, std::memory_order_relaxed);
			}
			m_forcedPositions.Consume();
		}
	}

	bool MotionService::GetPositionAt(size_t drive, uint32_t whenTicks, int32_t& position,
									  int32_t& positionAtMoveStart, bool& usedTimestamp) const
	{
		if (drive >= maxAxesPlusExtruders)
		{
			return false;
		}

		// A trigger timestamp is in the controller's step clock, and this side only has one of those
		// once the fit has enough MasterClock samples to trust. Before then, asking where a drive was
		// at that timestamp would extrapolate far outside the move
		const bool canUseTimestamp = whenTicks != 0 && StepTimer::GetClockStats().synced;

		// The timestamp is a reading of the raw step clock - the board stamps it from its own and the
		// controller converts it to master time - but a segment is timed in the movement timebase,
		// which is the raw clock less the movement delay. Evaluating one against the other reads the
		// trigger as having happened `movementDelay` later than it did, and since only the segment at
		// the head of the chain is evaluated and the answer is clamped to its end, a delay of any size
		// puts the drive at the end of whatever phase it was in rather than where the switch fired.
		const uint32_t whenInMovementTime = StepTimer::ConvertLocalToMovementTime(whenTicks);

		// Read as the motion thread last left it rather than advanced here. Advancing retires
		// segments and releases them, and the segment freelist is not thread-safe; the motion thread
		// advances every tracker once a millisecond, which is closer to the trigger than the report
		// that asks this question. Advancing would also be the wrong answer as often as the right
		// one: it moves the chain past the segment `whenTicks` falls in, and only the segment at the
		// head is evaluated
		const Motion::DriveTracker& tracker = m_move.GetDriveTracker(drive);
		position = canUseTimestamp ? lrintf(tracker.GetCurrentPosition(whenInMovementTime))
								   : tracker.GetMotorPosition();
		positionAtMoveStart = tracker.GetPositionAtMoveStart();
		usedTimestamp = canUseTimestamp;
		return true;
	}

	bool MotionService::CanAddMove(unsigned int ring) const
	{
		return ring < numRings && m_rings[ring].CanAddMove();
	}

	bool MotionService::SubmitMove(std::span<const uint8_t> params)
	{
		// Reject a malformed record here rather than dropping it on the motion thread, so the caller
		// finds out that the move it is waiting on will never happen.
		if (!IsWellFormedSubmission(params))
		{
			return false;
		}
		if (!m_submissions.Write(params))
		{
			m_submissionsDropped.fetch_add(1, std::memory_order_relaxed);
			return false;
		}
		return true;
	}

	void MotionService::SetRingState(unsigned int ring, bool shouldStartMove, bool waitingForEmpty)
	{
		if (ring < numRings)
		{
			m_ringState[ring].shouldStartMove.store(shouldStartMove, std::memory_order_relaxed);
			m_ringState[ring].waitingForEmpty.store(waitingForEmpty, std::memory_order_relaxed);
		}
	}

	uint32_t MotionService::GetScheduledMoves(unsigned int ring) const
	{
		return (ring < numRings) ? m_rings[ring].GetScheduledMoves() : 0;
	}

	uint32_t MotionService::GetCompletedMoves(unsigned int ring) const
	{
		return (ring < numRings) ? m_rings[ring].GetCompletedMoves() : 0;
	}

	MotionService::Stats MotionService::GetStats() const
	{
		Stats stats{};
		stats.segmentsCreated = MoveSegment::NumCreated();
		stats.movementDelayTicks = StepTimer::GetMovementDelay();
		stats.submissionsDropped = m_submissionsDropped.load(std::memory_order_relaxed);
		stats.forcedPositionsApplied = m_forcedPositionsApplied.load(std::memory_order_relaxed);
		stats.droppedSchedulePackets = m_move.GetScheduleMoveBuilder().GetDroppedPackets();
		for (unsigned int i = 0; i < numRings; ++i)
		{
			stats.rings[i] = m_rings[i].GetStats();
		}
		return stats;
	}

	void MotionService::ResetStats()
	{
		for (auto& ring : m_rings)
		{
			ring.ResetStats();
		}
	}

	void MotionService::OnMoveRetired(const DDA& dda, void *context) noexcept
	{
		const auto& ctx = *static_cast<const RetirementContext *>(context);
		MotionService& self = *ctx.service;
		self.PostMoveCompleted(ctx.ring, dda.GetMoveId());

		// Nothing is reported for an endstop move. Its planned endpoints are not where the machine
		// ended up - that is the whole point of it stopping short - and DCS has already been told the
		// stop and worked out the real position from the trigger timestamp. Sending the planned
		// endpoints afterwards would overwrite the corrected ones with the ones that were never
		// reached, which is the failure this arrangement exists to remove
	}

	void MotionService::PostMotionStopped(uint32_t whenTriggered, uint32_t moveId,
										  std::span<const duet::spi::protocol::MotionStoppedDriver> drivers)
	{
		if (drivers.empty() || m_link == nullptr)
		{
			return;
		}

		const size_t numDrivers = std::min(drivers.size(), duet::spi::protocol::MaxMotionStoppedDrivers);

		MotionStoppedEvent event{};
		event.header.type = static_cast<uint16_t>(InboundEventType::MotionStopped);
		event.whenTriggered = whenTriggered;
		event.moveId = moveId;
		event.numDrivers = static_cast<uint8_t>(numDrivers);

		MotionStoppedDriverEntry entries[duet::spi::protocol::MaxMotionStoppedDrivers]{};
		for (size_t i = 0; i < numDrivers; ++i)
		{
			entries[i].boardAddress = drivers[i].boardAddress;
			entries[i].driverNumber = drivers[i].driverNumber;
		}

		m_link->PostEventFromOtherThread(InboundEventType::MotionStopped, &event, sizeof(event),
										 entries, numDrivers * sizeof(MotionStoppedDriverEntry));
	}

	void MotionService::PostMoveCompleted(unsigned int ring, uint32_t moveId)
	{
		MoveCompletedEvent event{};
		event.header.type = static_cast<uint16_t>(InboundEventType::MoveCompleted);
		event.moveId = moveId;
		// The running total is per ring, and it is what DCS checks for a missed event: quoting
		// another ring's total would make that check fire on every move.
		event.completedMoves = GetCompletedMoves(ring);
		event.ring = static_cast<uint8_t>(ring);
		m_link->PostEventFromOtherThread(InboundEventType::MoveCompleted, &event, sizeof(event));
	}

	void MotionService::PostMoveFailed(unsigned int ring, uint32_t moveId, MovementError error)
	{
		MoveFailedEvent event{};
		event.header.type = static_cast<uint16_t>(InboundEventType::MoveFailed);
		event.moveId = moveId;
		event.ring = static_cast<uint8_t>(ring);
		event.error = static_cast<uint8_t>(error);
		m_link->PostEventFromOtherThread(InboundEventType::MoveFailed, &event, sizeof(event));
	}
}
