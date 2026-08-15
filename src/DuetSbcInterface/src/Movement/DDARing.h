/*
 * DDARing.h
 *
 *  Created on: 28 Feb 2019
 *      Author: David
 *
 *  This class represents a queue of moves, where for each move the movement is synchronised between all the motors involved.
 */

#ifndef SRC_MOVEMENT_DDARING_H_
#define SRC_MOVEMENT_DDARING_H_

#include "DDA.h"

#if SUPPORT_S_CURVE
# include "MovementProfile.h"
#endif

class DDARing final
{
public:
	// Until DCS pushes its own value down in MotionConfig::gracePeriodMs. Upstream this lives in
	// Move.h, which is not ported.
	// How long to let moves accumulate before committing the first one, in step clocks. Upstream
	// this is 10ms; DuetControlServer overrides it through MotionConfig::gracePeriodMs.
	static constexpr uint32_t defaultGracePeriod = (stepClockRate / 1000) * 10;

	DDARing() noexcept;

	// Build the ring against the machine it will plan for. `numDdas` is clamped to
	// Motion::minDdasPerRing..Motion::maxDdasPerRing; the value actually used is what GetNumDdas
	// reports. The motion system must outlive the ring.
	void Init(Duet::Sbc::Motion::MotionSystem& move, unsigned int numDdas) noexcept;

	// The machine this ring plans for. The DDAs in it reach the configuration and the drive
	// trackers through here rather than through a global.
	[[nodiscard]] Duet::Sbc::Motion::MotionSystem& GetMove() const noexcept { return *m_move; }

	[[nodiscard]] unsigned int GetNumDdas() const noexcept { return m_numDdasInRing; }

	[[nodiscard]] bool CanAddMove() const noexcept;

	// Queue a move that DuetControlServer has already worked out the shape of. Replaces upstream's
	// AddStandardMove: the caller has done everything up to the point where the ring is needed.
	MovementError AddMove(const Duet::Sbc::Motion::MoveParamsHeader& params) noexcept SPEED_CRITICAL;

	uint32_t Spin(uint32_t prepareAdvanceTime, SimulationMode simulationMode, bool signalMoveCompletion, bool shouldStartMove) noexcept SPEED_CRITICAL;	// Try to process moves in the ring
	[[nodiscard]] bool IsIdle() const noexcept;														// Return true if this DDA ring is idle
	[[nodiscard]] uint32_t GetGracePeriod() const noexcept { return m_gracePeriod; }					// Return the minimum idle time, before we should start a move. Better to have a few moves in the queue so that we can do lookahead
	void SetGracePeriod(uint32_t clocks) noexcept { m_gracePeriod = clocks; }

	// Whether it is time to commit the first move rather than keep waiting for more.
	//
	// This is the `shouldStartMove` argument of Spin, and it is entirely about local timing: hold
	// off briefly so that a few moves accumulate and lookahead has something to work with, but do
	// not hold off for ever if no more are coming. Upstream this lives in Move::Spin, measured in
	// milliseconds off the Move task's tick; here it is measured against the step clock, which is
	// this side's own timebase and is what the tests can drive.
	[[nodiscard]] bool ShouldStartMove() const noexcept
	{
		return (StepTimer::GetTimerTicks() - m_whenLastMoveAdded) >= m_gracePeriod;
	}

	[[nodiscard]] DDA *_ecv_null GetCurrentDDA() const noexcept;										// If a move from this ring should be executing now, fetch its DDA

	[[nodiscard]] uint32_t GetScheduledMoves() const noexcept { return m_scheduledMoves; }				// How many moves have been scheduled?
	[[nodiscard]] uint32_t GetCompletedMoves() const noexcept { return m_completedMoves; }				// How many moves have been completed?

	[[nodiscard]] float GetSimulationTime() const noexcept { return m_simulationTime; }

	[[nodiscard]] float GetRequestedSpeedMmPerSec() const noexcept;
	[[nodiscard]] float GetTopSpeedMmPerSec() const noexcept;
	[[nodiscard]] float GetAccelerationMmPerSecSquared() const noexcept;								// Get the (peak) acceleration for reporting in the object model
	[[nodiscard]] float GetDecelerationMmPerSecSquared() const noexcept;								// Get the (peak) deceleration for reporting in the object model

	[[nodiscard]] int32_t GetLastEndpoint(size_t drive) const noexcept;
	void SetLastEndpoints(LogicalDrivesBitmap logicalDrives, const int32_t *_ecv_array ep) noexcept;
	void SetLastEndpoint(size_t drive, int32_t ep) noexcept;

	// Called as each move retires, with the move id DuetControlServer gave it. The ring itself has
	// no use for that id; this is how whoever queued the move learns it has finished, without
	// polling, and without the ring knowing there is anyone to tell.
	using RetirementCallback = void (*)(const DDA& dda, void *context) noexcept;
	void SetRetirementCallback(RetirementCallback callback, void *context) noexcept
	{
		m_retirementCallback = callback;
		m_retirementContext = context;
	}

	void RecordLookaheadError() noexcept { ++m_numLookaheadErrors; }						// Record a lookahead error

	// What this ring has done since the counters were last reset. Reported rather than formatted
	// here: DuetControlServer owns the wording of M122, as it does for every other reply.
	struct Stats
	{
		uint32_t scheduledMoves;
		uint32_t completedMoves;
		unsigned int numLookaheadErrors;			// how many times the lookahead algorithm failed
		unsigned int numLookaheadUnderruns;		// how many times it ran out of moves to adjust
		unsigned int numNoMoveUnderruns;			// how many times a move was wanted and there was none
	};

	[[nodiscard]] Stats GetStats() const noexcept
	{
		return Stats{ m_scheduledMoves, m_completedMoves,
					  m_numLookaheadErrors, m_numLookaheadUnderruns, m_numNoMoveUnderruns };
	}

	// Zero the error and underrun counters. Separate from GetStats on purpose: reporting used to
	// zero them as a side effect, so a second report showed zeros however bad the first had been.
	// The move counts are not reset - they are running totals DCS matches move ids against.
	void ResetStats() noexcept
	{
		m_numLookaheadErrors = m_numLookaheadUnderruns = m_numNoMoveUnderruns = 0;
	}

	bool SetWaitingToEmpty() noexcept;

private:
	void ReportRetirement(const DDA& dda) const noexcept
	{
		if (m_retirementCallback != nullptr)
		{
			m_retirementCallback(dda, m_retirementContext);
		}
	}

	[[nodiscard]] bool IsTimeToPrepareMove(uint32_t prepareAdvanceTime, uint32_t moveTimeLeft) const noexcept;
	uint32_t PrepareMoves(DDA *firstUnpreparedMove, uint32_t prepareAdvanceTime, uint32_t moveTimeLeft, SimulationMode simulationMode) noexcept;
#if SUPPORT_S_CURVE
	void PlanMoves(DDA *firstUnpreparedMove, bool stopping) noexcept;
	bool NeedNewPlan(DDA *moveToPrepare) const noexcept;
#endif

	Duet::Sbc::Motion::MotionSystem* m_move = nullptr;

	DDA* m_addPointer{};															// Pointer to the next DDA that we can use to add a new move, if this DDA is free
	DDA* volatile m_getPointer{};													// Pointer to the oldest committed or provisional move, if not equal to addPointer

	unsigned int m_numDdasInRing{};													// The number of DDAs that this ring contains
	uint32_t m_gracePeriod = defaultGracePeriod;								// The minimum idle time, in step clocks, before we should start a move
	uint32_t m_whenLastMoveAdded = 0;											// Step clock time at which the most recent move was queued

#if SUPPORT_S_CURVE
	MovementProfile m_plannedProfile;												// the profile planned for a collection of moves
#endif

	RetirementCallback m_retirementCallback = nullptr;
	void *m_retirementContext = nullptr;

	uint32_t m_scheduledMoves = 0;												// Number of moves scheduled in this ring
	uint32_t m_completedMoves = 0;												// Number of moves completed in this ring

	unsigned int m_numLookaheadUnderruns = 0;										// How many times we have run out of moves to adjust during lookahead
	unsigned int m_numNoMoveUnderruns = 0;										// How many times we wanted a new move but there were none
	unsigned int m_numLookaheadErrors = 0;										// How many times our lookahead algorithm failed

	float m_simulationTime = 0.0;													// Print time since we started simulating

	volatile bool m_waitingForRingToEmpty = false;								// True if Move has signalled that we are waiting for this ring to empty
};

#endif /* SRC_MOVEMENT_DDARING_H_ */
