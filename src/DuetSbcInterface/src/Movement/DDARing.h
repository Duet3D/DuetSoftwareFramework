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

#include <ObjectModel/ObjectModel.h>

#if SUPPORT_S_CURVE
# include "MovementProfile.h"
#endif

class DDARing final INHERIT_OBJECT_MODEL
{
public:
	// Until DCS pushes its own value down in MotionConfig::gracePeriodMs. Upstream this lives in
	// Move.h, which is not ported.
	static constexpr uint32_t DefaultGracePeriod = 10;

	DDARing() noexcept;

	void Init(unsigned int numDdas) noexcept;
	void Exit() noexcept;

	bool CanAddMove() const noexcept;

	// Queue a move that DuetControlServer has already worked out the shape of. Replaces upstream's
	// AddStandardMove: the caller has done everything up to the point where the ring is needed.
	MovementError AddMove(const Duet::Sbc::Motion::MoveParamsHeader& params) noexcept SPEED_CRITICAL;

	uint32_t Spin(uint32_t prepareAdvanceTime, SimulationMode simulationMode, bool signalMoveCompletion, bool shouldStartMove) noexcept SPEED_CRITICAL;	// Try to process moves in the ring
	bool IsIdle() const noexcept;														// Return true if this DDA ring is idle
	uint32_t GetGracePeriod() const noexcept { return gracePeriod; }					// Return the minimum idle time, before we should start a move. Better to have a few moves in the queue so that we can do lookahead

	DDA *_ecv_null GetCurrentDDA() const noexcept;										// If a move from this ring should be executing now, fetch its DDA

	uint32_t GetScheduledMoves() const noexcept { return scheduledMoves; }				// How many moves have been scheduled?
	uint32_t GetCompletedMoves() const noexcept { return completedMoves; }				// How many moves have been completed?
	void ResetMoveCounters() noexcept { scheduledMoves = completedMoves = 0; }

	float GetSimulationTime() const noexcept { return simulationTime; }
	void ResetSimulationTime() noexcept { simulationTime = 0.0; }

	float GetRequestedSpeedMmPerSec() const noexcept;
	float GetTopSpeedMmPerSec() const noexcept;
	float GetAccelerationMmPerSecSquared() const noexcept;								// Get the (peak) acceleration for reporting in the object model
	float GetDecelerationMmPerSecSquared() const noexcept;								// Get the (peak) deceleration for reporting in the object model
	float GetCurrentMoveDistance() const noexcept;
	float GetCurrentMoveDuration() const noexcept;

	void GetLastEndpoints(LogicalDrivesBitmap logicalDrives, int32_t returnedEndpoints[MaxAxesPlusExtruders]) const noexcept;
	int32_t GetLastEndpoint(size_t drive) const noexcept;
	void SetLastEndpoints(LogicalDrivesBitmap logicalDrives, const int32_t *_ecv_array ep) noexcept;
	void SetLastEndpoint(size_t drive, int32_t ep) noexcept;

	void RecordLookaheadError() noexcept { ++numLookaheadErrors; }						// Record a lookahead error
	void Diagnostics(const StringRef& reply, unsigned int ringNumber) noexcept;

	bool SetWaitingToEmpty() noexcept;

private:
	bool IsTimeToPrepareMove(uint32_t prepareAdvanceTime, uint32_t moveTimeLeft) const noexcept;
	uint32_t PrepareMoves(DDA *firstUnpreparedMove, uint32_t prepareAdvanceTime, uint32_t moveTimeLeft, SimulationMode simulationMode) noexcept;
#if SUPPORT_S_CURVE
	void PlanMoves(DDA *firstUnpreparedMove, bool stopping) noexcept;
	bool NeedNewPlan(DDA *moveToPrepare) const noexcept;
#endif

	DDA* addPointer;															// Pointer to the next DDA that we can use to add a new move, if this DDA is free
	DDA* volatile getPointer;													// Pointer to the oldest committed or provisional move, if not equal to addPointer

	unsigned int numDdasInRing;													// The number of DDAs that this ring contains
	uint32_t gracePeriod = DefaultGracePeriod;									// The minimum idle time in milliseconds, before we should start a move. Better to have a few moves in the queue so that we can do lookahead

#if SUPPORT_S_CURVE
	MovementProfile plannedProfile;												// the profile planned for a collection of moves
#endif

	uint32_t scheduledMoves = 0;												// Number of moves scheduled in this ring
	uint32_t completedMoves = 0;												// Number of moves completed in this ring

	unsigned int numLookaheadUnderruns = 0;										// How many times we have run out of moves to adjust during lookahead
	unsigned int numNoMoveUnderruns = 0;										// How many times we wanted a new move but there were none
	unsigned int numLookaheadErrors = 0;										// How many times our lookahead algorithm failed

	float simulationTime = 0.0;													// Print time since we started simulating

	volatile bool waitingForRingToEmpty = false;								// True if Move has signalled that we are waiting for this ring to empty
};

#endif /* SRC_MOVEMENT_DDARING_H_ */
