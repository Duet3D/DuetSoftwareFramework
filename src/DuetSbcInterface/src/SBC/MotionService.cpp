/*
 * MotionService.cpp - see MotionService.h.
 */

#include "MotionService.h"

#include "SbcInterface.h"

#include <Movement/MoveTiming.h>
#include <Movement/StepTimer.h>
#include <Platform/ProcessHelpers.h>
#include <Platform/RepRap.h>

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

		// The longest the motion thread sleeps when the rings say there is nothing to do. Short
		// enough that a move submitted while it sleeps is still prepared well inside its lead time.
	}

	MotionService::MotionService(SbcInterface& link)
		: m_link(&link)
		, m_sink(link)
		, m_submissions(kSubmissionCapacity)
	{
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
		if (!reprap.GetMove().Init())
		{
			return false;
		}
		reprap.GetMove().GetScheduleMoveBuilder().SetSink(&m_sink);

		const Motion::MotionConfig& config = reprap.GetMove().GetConfig();
		for (DDARing& ring : m_rings)
		{
			ring.Init(config.numDdasPerRing);
			ring.SetGracePeriod(MillisToStepClocks(config.gracePeriodMs));
			ring.SetRetirementCallback(&MotionService::OnMoveRetired, this);
		}
		m_initialised = true;
		return true;
	}

	void MotionService::Configure(const Motion::MotionConfig& config)
	{
		reprap.GetMove().Configure(config);
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

		reprap.GetMove().AdvanceTrackers(StepTimer::GetMovementTimerTicks());
		PublishPositions();
	}

	void MotionService::DrainSubmissions()
	{
		const uint8_t *record = nullptr;
		uint32_t length = 0;
		while (m_submissions.Peek(record, length))
		{
			if (length < sizeof(Motion::MoveParamsHeader))
			{
				m_submissions.Consume();
				continue;
			}

			const auto& params = *reinterpret_cast<const Motion::MoveParamsHeader *>(record);
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
				PostMoveFailed(params.moveId, err);
			}
			else if (err == MovementError::NoMovement)
			{
				// Nothing to do, but DCS is still waiting to hear that this move is done with.
				PostMoveCompleted(params.moveId);
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

		m_snapshot.whenTicks = StepTimer::GetMovementTimerTicks();
		reprap.GetMove().GetMotorPositions(m_snapshot.positions, maxAxesPlusExtruders);

		std::atomic_thread_fence(std::memory_order_release);
		m_snapshotSequence.store(sequence + 2, std::memory_order_release);
	}

	size_t MotionService::GetMotorPositions(int32_t *positions, size_t count, uint32_t *whenTicks) const
	{
		if (positions == nullptr || count == 0)
		{
			return 0;
		}
		const size_t toCopy = std::min(count, maxAxesPlusExtruders);

		for (;;)
		{
			const uint32_t before = m_snapshotSequence.load(std::memory_order_acquire);
			if ((before & 1u) != 0)
			{
				continue;					// a write is in progress
			}
			std::atomic_thread_fence(std::memory_order_acquire);

			const uint32_t when = m_snapshot.whenTicks;
			std::memcpy(positions, m_snapshot.positions, toCopy * sizeof(int32_t));

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

	void MotionService::SetMotorPositions(uint32_t driveMask, const int32_t *positions, size_t count)
	{
		reprap.GetMove().SetMotorPositions(LogicalDrivesBitmap(driveMask), positions, count);
	}

	bool MotionService::CanAddMove(unsigned int ring) const
	{
		return ring < numRings && m_rings[ring].CanAddMove();
	}

	bool MotionService::SubmitMove(const void *params, size_t length)
	{
		if (params == nullptr || length < sizeof(Motion::MoveParamsHeader))
		{
			return false;
		}
		if (!m_submissions.Write(params, length))
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

	void MotionService::OnMoveRetired(const DDA& dda, void *context) noexcept
	{
		auto & self = *static_cast<MotionService *>(context);
		self.PostMoveCompleted(dda.GetMoveId());

		if (dda.IsCheckingEndstops())
		{
			// A move that watches endstops can stop short, so where the drives actually ended up is
			// not what DCS planned. It plans the next move as a delta from its own copy of the
			// endpoints, so it has to be told before it sends another one.
			MotionEndpointsEvent event{};
			event.header.type = static_cast<uint16_t>(InboundEventType::MotionEndpoints);
			event.moveId = dda.GetMoveId();
			event.driveMask = 0xFFFFFFFFu;
			event.ring = 0;
			event.numDrives = static_cast<uint8_t>(maxAxesPlusExtruders);

			int32_t endPoints[maxAxesPlusExtruders]{};
			std::memcpy(endPoints, dda.DriveCoordinates(), sizeof(endPoints));
			self.m_link->PostEventFromOtherThread(
				InboundEventType::MotionEndpoints, &event, sizeof(event), endPoints, sizeof(endPoints));
		}
	}

	void MotionService::PostMoveCompleted(uint32_t moveId)
	{
		MoveCompletedEvent event{};
		event.header.type = static_cast<uint16_t>(InboundEventType::MoveCompleted);
		event.moveId = moveId;
		event.completedMoves = m_rings[0].GetCompletedMoves();
		event.ring = 0;
		m_link->PostEventFromOtherThread(InboundEventType::MoveCompleted, &event, sizeof(event));
	}

	void MotionService::PostMoveFailed(uint32_t moveId, MovementError error)
	{
		MoveFailedEvent event{};
		event.header.type = static_cast<uint16_t>(InboundEventType::MoveFailed);
		event.moveId = moveId;
		event.ring = 0;
		event.error = static_cast<uint8_t>(error);
		m_link->PostEventFromOtherThread(InboundEventType::MoveFailed, &event, sizeof(event));
	}
}
