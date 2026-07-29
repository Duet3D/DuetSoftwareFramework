/*
 * DDARing.cpp
 *
 *  Created on: 28 Feb 2019
 *      Author: David
 */

#include "DDARing.h"
#include "DDA.h"
#include "MoveDebugFlags.h"
#include "MoveTiming.h"
#include <Platform/Platform.h>
#include <Platform/RepRap.h>
#include <Platform/Tasks.h>
#include <GCodes/GCodes.h>
#include <Motion/MotionConfig.h>

#include <algorithm>

#if SUPPORT_CAN_EXPANSION
# include "CAN/CanMotion.h"
#endif

/* Note on how the DDA ring works, using the new step-generation code that implements late input shaping:
 * A DDA represents a straight-line move with at least one of an acceleration segment, a steady speed segment, and a deceleration segment.
 * A single G0 or G1 command may be represented by a single DDA, or by multiple DDAs when the move has been segmented.
 *
 * DDAs are added to a ring in response to G0, G1, G2 and G3 commands and when RRF generates movement automatically (e.g. probing moves).
 * A newly-added DDA is in state 'provisional' and has its end speed set to zero. In this state its speed, acceleration and deceleration can be modified.
 * These modifications happen as other DDAs are added to the ring and the DDAs are adjusted to give a smooth transition between them.
 *
 * Shortly before a move is due to be executed, DDA::Prepare is called. This causes the move parameters to be frozen.
 * Move segments are generated, and/or the move details are sent to CAN-connected expansion boards. The DDA state is set to "committed".
 *
 * The scheduled DDA remains in the ring until the time for it to finish executing has passed, in order that we can report on
 * the parameters of the currently-executing move, e.g. requested and top speeds, extrusion rate, and extrusion amount for the filament monitor.
 *
 * When a move requires that endstops and/or Z probes are active, all other moves are completed before starting it, and no new moves are allowed
 * to be added to the ring until it completes. So it is the only move in the ring with state 'committed'.
 */

constexpr uint32_t moveStartPollInterval = 10;					// delay in milliseconds between checking whether we should start moves

// The object model tables are gone with the object model: DuetControlServer builds the object model
// now, from what the CApi reports.

DDARing::DDARing() noexcept : m_gracePeriod(defaultGracePeriod)
{
}

// This can be called in the constructor for class Move
void DDARing::Init(unsigned int numDdas) noexcept
{
	// The configuration comes down from DuetControlServer, so it is not trusted to be sane. A ring
	// of 0 or 1 is not a ring at all: every move would take its start endpoints from the DDA it is
	// about to overwrite, and the drives would be commanded the whole distance again.
	numDdas = std::clamp(numDdas, Duet::Sbc::Motion::minDdasPerRing, Duet::Sbc::Motion::maxDdasPerRing);
	m_numDdasInRing = numDdas;

	// Build the DDA ring
	DDA *dda = new DDA(nullptr);
	m_addPointer = dda;
	for (size_t i = 1; i < numDdas; i++)
	{
		DDA * const oldDda = dda;
		dda = new DDA(dda);
		oldDda->SetPrevious(dda);
	}
	m_addPointer->SetNext(dda);
	dda->SetPrevious(m_addPointer);
	m_getPointer = m_addPointer;
}

void DDARing::Exit() noexcept
{
	// Clear the DDA ring so that we don't report any moves as pending
	DDA *gp;										// use a local variable to avoid loading volatile variable getPointer too often
	while ((gp = m_getPointer) != m_addPointer)
	{
		gp->Free();
		m_getPointer = gp = gp->GetNext();
	}
}

bool DDARing::CanAddMove() const noexcept
{
	// We have two constraints here that may prevent us from using the last free element in the ring:
	// 1. DDA::Prepare needs to access the previous DDA in the ring to find the endpoints of the previous move.
	//    So we must not allocate an empty slot if the next one has state 'provisional'.
	// 2. If all DDAs in the ring have state 'committed' then function ManageIOBitsAndFeedforward may loop indefinitely.
	//    So we must not allocate an empty slot if the next one has state 'committed'.
	// The simplest solution is not to allow the last free slot to be allocated.
	if (   m_addPointer->GetState() == DDA::Empty
		&& m_addPointer->GetNext()->GetState() == DDA::Empty
	   )
	 {
			// In order to react faster to speed and extrusion rate changes, only add more moves if the total duration of
			// all un-frozen moves is less than 2 seconds, or the total duration of all but the first un-frozen move is less than 0.5 seconds.
		 	 // When using S-curve acceleration we use late planning, so GetClocksNeeded() for provisional moves is the minimum clocks that it will need.
			const DDA *dda = m_addPointer;
			uint32_t unPreparedTime = 0;
			uint32_t prevMoveTime = 0;
			for(;;)
			{
				dda = dda->GetPrevious();
				if (!dda->IsProvisional())
				{
					break;
				}
				unPreparedTime += prevMoveTime;
				prevMoveTime = dda->GetClocksNeeded();
			}

			return (unPreparedTime < stepClockRate/2 || unPreparedTime + prevMoveTime < 2 * stepClockRate);
	 }
	 return false;
}

#if SUPPORT_ASYNC_MOVES

#endif

// Add a move that DuetControlServer has already worked out the shape of.
MovementError DDARing::AddMove(const Duet::Sbc::Motion::MoveParamsHeader& params) noexcept
{
	const MovementError err = m_addPointer->InitFromParams(*this, params);
	if (err == MovementError::Ok)
	{
		m_addPointer = m_addPointer->GetNext();
		m_scheduledMoves++;
		m_whenLastMoveAdded = StepTimer::GetTimerTicks();
	}
	return err;
}

// Try to process moves in the ring. Called by the Move task.
// Return the maximum time in milliseconds that should elapse before we prepare further unprepared moves that are already in the ring, or MoveTiming::StandardMoveWakeupInterval if there are no unprepared moves left.
uint32_t DDARing::Spin(uint32_t prepareAdvanceTime, SimulationMode simulationMode, bool signalMoveCompletion, bool shouldStartMove) noexcept
{
	DDA *cdda = m_getPointer;											// capture volatile variable

	// If we are simulating, simulate completion of the current move
	if (simulationMode >= SimulationMode::Normal)
	{
		if (cdda->IsCommitted())
		{
			// Retiring the current move unconditionally would keep the ring nearly empty, so moves would be committed with hardly any lookahead behind them and the simulated time would come out too high
			if (!CanAddMove() || m_waitingForRingToEmpty || shouldStartMove || cdda->IsIsolatedMove())
			{
				m_simulationTime += (float)cdda->GetClocksNeeded() * (1.0 / stepClockRate);
				++m_completedMoves;
				ReportRetirement(*cdda);
				if (cdda->Free())
				{
					++m_numLookaheadUnderruns;
				}
				m_getPointer = cdda = cdda->GetNext();
			}
			else
			{
				return 1;											// wait for more moves to be added, MoveAvailable() wakes us up earlier
			}
		}
	}
	else
	{
		// See if we can retire any completed moves
		while (cdda->IsCommitted() && cdda->HasExpired())
		{
			++m_completedMoves;
			ReportRetirement(*cdda);
			//debugPrintf("Retiring move: now=%" PRIu32 " start=%" PRIu32 " dur=%" PRIu32 "\n", StepTimer::GetMovementTimerTicks(), cdda->GetMoveStartTime(), cdda->GetClocksNeeded());
			if (cdda->Free())
			{
				++m_numLookaheadUnderruns;
			}
			m_getPointer = cdda = cdda->GetNext();
		}
	}

	// If we are already moving, see whether we need to prepare any more moves
	if (cdda->IsCommitted())										// if we have started executing moves
	{
		const DDA* const currentMove = cdda;						// save for later

		// Count how many prepared or executing moves we have and how long they will take
		uint32_t preparedTime = 0;
		while (cdda->IsCommitted())
		{
			preparedTime += cdda->GetTimeLeft();
			cdda = cdda->GetNext();
		}

		uint32_t ret;
		if (cdda->IsProvisional())
		{
			ret = PrepareMoves(cdda, prepareAdvanceTime, preparedTime, simulationMode);
		}
		else
		{
			if (!m_waitingForRingToEmpty && IsTimeToPrepareMove(prepareAdvanceTime, preparedTime))
			{
				++m_numNoMoveUnderruns;
			}
			ret = MoveTiming::standardMoveWakeupInterval;
		}

		if (simulationMode != SimulationMode::Off)
		{
			return 0;
		}

		if (signalMoveCompletion || m_waitingForRingToEmpty || currentMove->IsIsolatedMove())
		{
			// Wake up the Move task shortly after we expect the current move to finish
			const int32_t moveTicksLeft = currentMove->GetMoveFinishTime() - StepTimer::GetMovementTimerTicks();
			if (moveTicksLeft < 0)
			{
				return 0;
			}

			const uint32_t moveTime = (uint32_t)moveTicksLeft/(stepClockRate/1000) + 1;	// 1ms ticks until the move finishes plus 1ms
			if (moveTime < ret)
			{
				return moveTime;
			}
		}

		return ret;
	}

	// No DDA is committed, so commit a new one if possible
	if (   shouldStartMove											// if the Move code told us that we should start a move in any case...
		|| m_waitingForRingToEmpty									// ...or GCodes is waiting for all moves to finish...
		|| cdda->IsIsolatedMove()									// ...or checking endstops or another isolated move, so we can't schedule the following move
		|| (simulationMode >= SimulationMode::Normal && !CanAddMove())	// ...or we are simulating with a full ring, so waiting cannot gain any more lookahead
	   )
	{
		const uint32_t ret = PrepareMoves(cdda, prepareAdvanceTime, 0, simulationMode);
		if (cdda->IsCommitted())
		{
			if (simulationMode != SimulationMode::Off)
			{
				return 0;											// we don't want any delay because we want Spin() to be called again soon to complete this move
			}

			if (signalMoveCompletion || m_waitingForRingToEmpty || cdda->IsIsolatedMove())
			{
				// Wake up the Move task shortly after we expect the current move to finish
				const int32_t moveTicksLeft = cdda->GetMoveFinishTime() - StepTimer::GetMovementTimerTicks();
				if (moveTicksLeft < 0)
				{
					return 0;
				}

				const uint32_t moveTime = (uint32_t)moveTicksLeft/(stepClockRate/1000) + 1;	// 1ms ticks until the move finishes plus 1ms
				if (moveTime < ret)
				{
					return moveTime;
				}
			}
		}
		return ret;
	}

	return (cdda->IsProvisional())
			? moveStartPollInterval									// there are moves in the queue but it is not time to prepare them yet
				: MoveTiming::standardMoveWakeupInterval;			// the queue is empty, nothing to do until new moves arrive
}

#if SUPPORT_S_CURVE

// Return true if we need to create a new plan before we can prepare a move
inline bool DDARing::NeedNewPlan(DDA *moveToPrepare) const noexcept
{
	if (plannedProfile.numberOfMovesCovered == 0)
	{
		return true;												// if we don't have a plan yet, we need one
	}
	if (!plannedProfile.usesAllMoves)
	{
		return false;												// if the plan ends before all moves are used, we don't need a new plan
	}
	if (plannedProfile.scheduledMovesWhenCreated == scheduledMoves)
	{
		return false;												// if no moves have been added, we don't need to re-plan
	}
	if (plannedProfile.reachesRequestedSpeed && (double)moveToPrepare->GetTotalDistance() <= plannedProfile.NonDecelDistance())
	{
		return false;												// if the profile reaches its requested speed and deceleration begins later than the end of this move, we don't need to re-plan yet
	}
	if (plannedProfile.ReducingDeceleration())
	{
		return false;												// if we are already in the reducing deceleration phase then unless allowed jerk has increased we can't avoid stopping
	}

	// We have an existing plan but it is out of date. Update the start speed and acceleration in the move to prepare to agree with the plan.
	moveToPrepare->SetStartSpeedAndAcceleration((float)plannedProfile.startSpeed/moveToPrepare->GetMovementRatio(), (float)plannedProfile.startAcceleration/moveToPrepare->GetMovementRatio());
	return true;													// we do need to construct a [new] plan
}

#endif

// Return true if it is time to prepare some moves
inline bool DDARing::IsTimeToPrepareMove(uint32_t prepareAdvanceTime, uint32_t moveTimeLeft) const noexcept
{
	return moveTimeLeft < prepareAdvanceTime;						// prepare moves one tenth of a second ahead of when they will be needed
}

// Prepare some moves. moveTimeLeft is the total length remaining of moves that are already executing or prepared.
// Return the maximum time in milliseconds that should elapse before we prepare further unprepared moves that are already in the ring, or MoveTiming::StandardMoveWakeupInterval if there are no unprepared moves left.
uint32_t DDARing::PrepareMoves(DDA *firstUnpreparedMove, uint32_t prepareAdvanceTime, uint32_t moveTimeLeft, SimulationMode simulationMode) noexcept
{
	// If the already-prepared moves will execute in less than the minimum time, prepare another move.
	// Try to avoid preparing deceleration-only moves too early
	while (	  firstUnpreparedMove->IsProvisional()
		   && IsTimeToPrepareMove(prepareAdvanceTime, moveTimeLeft)
#if SUPPORT_CAN_EXPANSION
		   && CanMotion::CanPrepareMove()
#endif
		  )
	{
#if SUPPORT_S_CURVE
		// If the move to prepare is an S-curve move than it may not have been planned yet.
		// Even if it has been planned, if any moves have been added to the ring then we may need to re-plan it
		if (firstUnpreparedMove->IsSCurveMove())
		{
			if (NeedNewPlan(firstUnpreparedMove))
			{
				DDA::PlanMoves(firstUnpreparedMove, plannedProfile, false);
				plannedProfile.scheduledMovesWhenCreated = scheduledMoves;
			}
			else
			{
#if 0
				if (reprap.GetDebugFlags(Module::Move).IsBitSet(MoveDebugFlags::Lookahead))
				{
					debugPrintf("Skipping planning\n");
				}
#endif
			}
		}
		firstUnpreparedMove->Prepare(*this, plannedProfile, prepareAdvanceTime, simulationMode);
#else
		firstUnpreparedMove->Prepare(*this, prepareAdvanceTime, simulationMode);
#endif
		moveTimeLeft += firstUnpreparedMove->GetTimeLeft();
		firstUnpreparedMove = firstUnpreparedMove->GetNext();
	}

	// Decide how soon we want to be called again to prepare further moves
	if (firstUnpreparedMove->IsProvisional())
	{
		// There are more moves waiting to be prepared, so ask to be woken up early
		if (simulationMode != SimulationMode::Off)
		{
			return 1;
		}

		const int32_t clocksTillWakeup = (int32_t)(moveTimeLeft - prepareAdvanceTime);			// calculate how long before we run out of prepared moves, less the usual advance prepare time
		return (clocksTillWakeup <= 0) ? 2 : max<uint32_t>((uint32_t)clocksTillWakeup/(stepClockRate/1000), 2);		// wake up at that time, but delay for at least 2 ticks
	}

	// There are no moves waiting to be prepared
	return MoveTiming::standardMoveWakeupInterval;
}

// Return true if this DDA ring is idle
bool DDARing::IsIdle() const noexcept
{
	return m_getPointer->GetState() == DDA::Empty;
}

// Tell the DDA ring that the caller is waiting for it to empty. Returns true if it is already empty. This is called from the Main task.
bool DDARing::SetWaitingToEmpty() noexcept
{
	m_waitingForRingToEmpty = true;					// set this first to avoid a possible race condition
	const bool ret = IsIdle();
	if (ret)
	{
		m_waitingForRingToEmpty = false;
#if SUPPORT_S_CURVE
		plannedProfile.Invalidate();				// we may be waiting for movement to stop after an asynchronous pause, in which case the planned profile may not have been completed
#endif
	}
	return ret;
}

void DDARing::GetLastEndpoints(LogicalDrivesBitmap logicalDrives, int32_t returnedEndpoints[maxAxesPlusExtruders]) const noexcept
{
	logicalDrives.Iterate([this, returnedEndpoints](unsigned int drive, unsigned int count) noexcept { returnedEndpoints[drive] = m_addPointer->GetPrevious()->DriveCoordinates()[drive]; } );
}

int32_t DDARing::GetLastEndpoint(size_t drive) const noexcept
{
	return m_addPointer->GetPrevious()->DriveCoordinates()[drive];
}

// Set the endpoints of some drives that we have just allocated. The drives must not be owned in the previous move!
void DDARing::SetLastEndpoints(LogicalDrivesBitmap logicalDrives, const int32_t *_ecv_array ep) noexcept
{
	DDA *prev = m_addPointer->GetPrevious();
	logicalDrives.Iterate([prev, ep](unsigned int drive, unsigned int count) noexcept
							{
								prev->SetDriveCoordinate(drive, ep[drive]);
							});
}

void DDARing::SetLastEndpoint(size_t drive, int32_t ep) noexcept
{
	m_addPointer->GetPrevious()->SetDriveCoordinate(drive, ep);
}

// Get the DDA that should currently be executing, or nullptr if no move from this ring should be executing
DDA *_ecv_null DDARing::GetCurrentDDA() const noexcept
{
	// Upstream takes a task-level critical section here, because the Move task and the GCodes task
	// both read the ring. The two threads here are the motion thread, which is the only writer, and
	// the CApi caller; that pairing is handled by the position snapshot rather than by locking, so
	// that a managed GC pause can never stall move preparation.
	DDA *cdda = m_getPointer;
	const uint32_t now = StepTimer::GetMovementTimerTicks();
	while (cdda->IsCommitted())
	{
		const uint32_t timeRunning = now - cdda->GetMoveStartTime();
		if ((int32_t)timeRunning < 0) { break; }			// move has not started yet
		if (timeRunning < cdda->GetClocksNeeded()) { return cdda; }
		cdda = cdda->GetNext();								// move has completed so look at the next one
	}
	return nullptr;
}

// Get various data for reporting in the OM
float DDARing::GetRequestedSpeedMmPerSec() const noexcept
{
	const DDA *_ecv_null const cdda = GetCurrentDDA();
	return (cdda != nullptr) ? cdda->GetRequestedSpeedMmPerSec() : 0.0;
}

float DDARing::GetTopSpeedMmPerSec() const noexcept
{
	const DDA *_ecv_null const cdda = GetCurrentDDA();
	return (cdda != nullptr) ? cdda->GetTopSpeedMmPerSec() : 0.0;
}

// Get the (peak) acceleration for reporting in the object model
float DDARing::GetAccelerationMmPerSecSquared() const noexcept
{
	const DDA *_ecv_null const cdda = GetCurrentDDA();
	return (cdda != nullptr) ? cdda->GetAccelerationMmPerSecSquared() : 0.0;
}

// Get the (peak) deceleration for reporting in the object model
float DDARing::GetDecelerationMmPerSecSquared() const noexcept
{
	const DDA *_ecv_null const cdda = GetCurrentDDA();
	return (cdda != nullptr) ? cdda->GetDecelerationMmPerSecSquared() : 0.0;
}

float DDARing::GetCurrentMoveDistance() const noexcept
{
	const DDA *_ecv_null const cdda = GetCurrentDDA();
	return (cdda != nullptr) ? cdda->GetTotalDistance() : 0.0;;
}

float DDARing::GetCurrentMoveDuration() const noexcept
{
	const DDA *_ecv_null const cdda = GetCurrentDDA();
	return (cdda != nullptr) ? (float)cdda->GetClocksNeeded() * stepClocksToSeconds : 0.0;;
}

#if HAS_VOLTAGE_MONITOR || HAS_STALL_DETECT

#endif

void DDARing::Diagnostics(const StringRef& reply, unsigned int ringNumber) noexcept
{
	reply.lcatf("=== DDARing %u ===\nScheduled moves %" PRIu32 ", completed %" PRIu32 ", LaErrors %u, Underruns [%u, %u]\n",
				ringNumber, m_scheduledMoves, m_completedMoves, m_numLookaheadErrors, m_numLookaheadUnderruns, m_numNoMoveUnderruns
			   );
	m_numLookaheadUnderruns = m_numNoMoveUnderruns = m_numLookaheadErrors = 0;
}

#if SUPPORT_LASER

#endif

// End
