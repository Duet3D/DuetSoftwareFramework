/*
 * DDA.h
 *
 *  Created on: 7 Dec 2014
 *      Author: David
 *
 * Trimmed for the SBC. This is RepRapFirmware's DDA with everything that belongs on the other side
 * of the split removed, and it is meant to stay diffable against upstream: what is left is upstream
 * source, not a rewrite.
 *
 * Gone, because DuetControlServer owns it now: InitStandardMove and everything it needs
 * (kinematics, the acceleration and feedrate tables, the vector helpers, normalisation), together
 * with the fields it filled that only pause, resume and the object model ever read - filePos, tool,
 * virtualExtruderPosition, proportionDone, initialUserC0/C1, originalFeedRate. InitFromParams takes
 * their place: DCS ships the results of steps 1-6 and this picks the move up at step 7.
 *
 * Gone, because the SBC has no local drivers: everything that fed the step interrupt, and the
 * laser and IOBits handling that ran off the same timebase.
 *
 * Gone for Phase 1: leadscrew adjustment moves, async moves' InitAsyncMove, babystepping into a
 * queued move, and scanning probe moves.
 */

#ifndef DDA_H_
#define DDA_H_

#include <RepRapFirmware.h>
#include "StepTimer.h"
#include "MoveSegment.h"
#include "MovementError.h"
#include <Motion/MoveParams.h>
#include <Motion/MoveProfile.h>
#include <Platform/Tasks.h>
#include <GCodes/SimulationMode.h>

# define DDA_LOG_PROBE_CHANGES	0
# define DDA_DEBUG_STEP_COUNT	0

class DDARing;

// Struct for passing parameters to the segment builder and the schedule builder.
//
// Upstream this declares the whole velocity profile inline. Here the profile itself is
// Motion::MoveProfile, which the segment builder and the ScheduleMove packet also speak, so that
// they need no knowledge of DDA; PrepParams adds the one field that is about how the move is to be
// executed rather than about its shape.
struct PrepParams : public Duet::Sbc::Motion::MoveProfile
{
	bool useInputShaping;

	[[nodiscard]] uint32_t SteadyClocks() const noexcept { return steadyClocks; }
	[[nodiscard]] uint32_t TotalAccelClocks() const noexcept { return accelClocks; }
	[[nodiscard]] uint32_t TotalDecelClocks() const noexcept { return decelClocks; }
	[[nodiscard]] motioncalc_t TotalAccelDistance() const noexcept { return accelDistance; }

	// Set up the parameters from the DDA. As a side effect it sets up dda.clocksNeeded.
	void SetFromDDA(DDA& dda) noexcept;

	void DebugPrint() const noexcept;
};

// This defines a single coordinated movement of one or several motors
class DDA final
{
	friend struct PrepParams;

public:

	enum DDAState : uint8_t
	{
		empty,				// empty or being filled in
#if SUPPORT_S_CURVE
		created,			// filled in but not yet planned
#endif
		planned,			// ready, but could be subject to modifications
		committed			// has been converted into move segments already
	};

	explicit DDA(DDA *_ecv_null n) noexcept;

	void* operator new(size_t count) { return Tasks::AllocPermanent(count); }
	void* operator new(size_t count, std::align_val_t align) { return Tasks::AllocPermanent(count, align); }
	void operator delete(void* ptr) noexcept {}
	void operator delete(void* ptr, std::align_val_t align) noexcept {}

	// Take up a move DuetControlServer has already worked out the shape of, and plan it against the
	// moves already in the ring. This is where InitStandardMove's step 7 used to begin.
	MovementError InitFromParams(DDARing& ring, const Duet::Sbc::Motion::MoveParamsHeader& params) noexcept SPEED_CRITICAL;

	void SetNext(DDA *n) noexcept { next = n; }
	void SetPrevious(DDA *p) noexcept { prev = p; }
	bool Free() noexcept;
	void Prepare(DDARing& ring,
#if SUPPORT_S_CURVE
					MovementProfile& plannedProfile,
#endif
					uint32_t prepareAdvanceTime, SimulationMode simMode) noexcept SPEED_CRITICAL;	// Calculate all the values and freeze this DDA
	bool CanPauseAfter() const noexcept;
	bool IsPrintingMove() const noexcept { return flags.isPrintingMove; }							// Return true if this involves both XY movement and extrusion
	bool UsingStandardFeedrate() const noexcept { return flags.usingStandardFeedrate; }
	bool IsCheckingEndstops() const noexcept { return flags.checkEndstops; }
	bool IsIsolatedMove() const noexcept { return flags.isolatedMove; }
	bool NoShaping() const noexcept { return flags.isolatedMove; }
	bool UsesInputShaping() const noexcept;									// return true if this move should use input shaping

	DDAState GetState() const noexcept { return (DDAState)flags.stateBits; }
	void SetState(DDAState state) noexcept { flags.stateBits = (uint32_t)state; }
	bool IsCommitted() const noexcept { return GetState() == DDA::committed; }
	bool IsProvisional() const noexcept;
	DDA* GetNext() const noexcept { return _ecv_not_null(next); }
	DDA* GetPrevious() const noexcept { return _ecv_not_null(prev); }
	uint32_t GetTimeLeft() const noexcept;

	const int32_t *_ecv_array DriveCoordinates() const noexcept { return endPoint; }				// Get endpoints of a move in machine coordinates
	void SetDriveCoordinate(size_t drive, int32_t ep) noexcept;										// Force an end point
	void SetFeedRate(float rate) noexcept { requestedSpeed = rate; }

	// DuetControlServer's correlation id for this move, quoted back when the move completes or fails
	uint32_t GetMoveId() const noexcept { return moveId; }

	float GetRequestedSpeedMmPerClock() const noexcept { return requestedSpeed; }
	float GetRequestedSpeedMmPerSec() const noexcept { return InverseConvertSpeedToMmPerSec(requestedSpeed); }
	float GetTopSpeedMmPerSec() const noexcept { return InverseConvertSpeedToMmPerSec(topSpeed); }
	float GetAccelerationMmPerSecSquared() const noexcept							// Get the (peak) acceleration for reporting in the object model
#if SUPPORT_S_CURVE
		{ return InverseConvertAcceleration(afterPrepare.peakAcceleration); }
#else
		{ return InverseConvertAcceleration(maxAcceleration); }
#endif
	float GetDecelerationMmPerSecSquared() const noexcept							// Get the (peak) acceleration for reporting in the object model
#if SUPPORT_S_CURVE
		{ return InverseConvertAcceleration(afterPrepare.peakDeceleration); }
#else
		{ return InverseConvertAcceleration(maxAcceleration); }
#endif
#if SUPPORT_S_CURVE
	bool IsSCurveMove() const noexcept { return flags.useScurve; }
	bool IsFullyPlanned() const noexcept { return flags.fullyPlanned; }
	float GetMovementRatio() const noexcept { return movementRatio; }
	void SetSpeedRatioAndMaxJunctionSpeedForPrintingMoves(const Move& move) noexcept;
	void SetSpeedRatioAndMaxJunctionSpeedForNonPrintingMoves(const Move& move) noexcept;
	void SetStartSpeedAndAcceleration(float speed, float acceleration) noexcept { startSpeed = speed; startAcceleration = acceleration; }

	static void PlanMoves(DDA *firstUnpreparedMove, MovementProfile& plannedProfile, bool stopping) noexcept;
#endif

	float GetTotalDistance() const noexcept { return totalDistance; }

	uint32_t GetClocksNeeded() const noexcept { return clocksNeeded; }
	bool HasExpired() const noexcept pre(IsCommitted());
	bool IsNonPrintingExtruderMove() const noexcept { return flags.isNonPrintingExtruderMove; }
	uint32_t GetMoveStartTime() const noexcept { return afterPrepare.moveStartTime; }
	uint32_t GetMoveFinishTime() const noexcept { return afterPrepare.moveStartTime + clocksNeeded; }

	float GetAverageExtrusionSpeed() const noexcept pre(IsCommitted()) { return afterPrepare.averageExtrusionSpeed; }
	bool HasForwardExtrusion() const noexcept { return flags.hasForwardExtrusion; }

	void DebugPrint(const char *_ecv_array tag) const noexcept;				// print the DDA only

	static void PrintMoves() noexcept;										// print saved moves for debugging

#if DDA_LOG_PROBE_CHANGES
	static const size_t MaxLoggedProbePositions = 40;
	static size_t numLoggedProbePositions;
	static int32_t loggedProbePositions[XYZ_AXES * MaxLoggedProbePositions];
#endif

private:
	static constexpr float MinimumAccelOrDecelClocks = 10.0;				// Minimum number of acceleration or deceleration clocks we try to ensure

	MovementError RecalculateMove(DDARing& ring) noexcept SPEED_CRITICAL;
	static void DoLookahead(DDARing& ring, DDA *laDDA) noexcept SPEED_CRITICAL;	// Try to smooth out moves in the queue

#if SUPPORT_S_CURVE
	static void PlanDeceleratingMoves(double distance, double acc, MovementProfile& plannedProfile) noexcept SPEED_CRITICAL;
	void AllocateMoveFromPlan(MovementProfile& plannedProfile, PrepParams& params) noexcept SPEED_CRITICAL;
#endif

	void MatchSpeeds() noexcept SPEED_CRITICAL;
	bool IsDecelerationMove() const noexcept;								// return true if this move is or have been might have been intended to be a deceleration-only move
	bool IsAccelerationMove() const noexcept;								// return true if this move is or have been might have been intended to be an acceleration-only move
	void DebugPrintVector(const char *_ecv_array name, const float *_ecv_array vec, size_t len) const noexcept;

    DDA *_ecv_null next;							// The next one in the ring
	DDA *_ecv_null prev;							// The previous one in the ring

	union
	{
		struct
		{
			// Flag bits. The first 4 or 5 are copied from similar flag bits in RawMove, so keep them together and in the same order so that the compiler can copy them using a ubfx instruction.
			uint32_t stateBits : 3,					// What state this DDA is in
					 canPauseAfter : 1,				// True if we can pause at the end of this move
			 	 	 checkEndstops : 1,				// True if this move monitors endstops or Z probe
					 usingStandardFeedrate : 1,		// True if this move uses the standard feed rate
					 usePressureAdvance : 1,		// True if pressure advance should be applied to any forward extrusion

					 isPrintingMove : 1,			// True if this move includes XY movement and extrusion
					 hadLookaheadUnderrun : 1,		// True if the lookahead queue was not long enough to optimise this move
					 xyMoving : 1,					// True if movement along an X axis or a Y axis was requested, even if it's too small to do
					 isNonPrintingExtruderMove : 1,	// True if this move is an extruder-only move, or involves reverse extrusion (and possibly axis movement too)
					 continuousRotationShortcut : 1, // True if continuous rotation axes take shortcuts
					 isolatedMove : 1,				// set if we disable input shaping for this move and wait for it to finish e.g. for a G1 H2 move
					 hasForwardExtrusion : 1		// set if any extruder has forward movement (used by M571)
#if SUPPORT_S_CURVE
					 , useScurve : 1,				// set if this move uses S-curve acceleration
					 fullyPlanned : 1				// set if this move can't be made to go any faster even if we add more moves to the ring
#endif
					 ;
		};
		uint32_t all;								// so that we can print all the flags at once for debugging
	} flags;

	// DuetControlServer's id for this move. Native never interprets it; it exists so that a
	// MoveCompleted or MoveFailed report can be matched to the move that caused it.
	uint32_t moveId;

	int32_t endPoint[MaxAxesPlusExtruders];  		// Machine coordinates of the endpoint
	float directionVector[MaxAxesPlusExtruders];	// The normalised direction vector - first 3 are XYZ Cartesian coordinates even on a delta
    float totalDistance;							// How long is the move in hypercuboid space
    float maxAcceleration;							// The maximum acceleration and deceleration to use, always positive
#if SUPPORT_S_CURVE
	float jerk;										// The magnitude of the rate of change of acceleration or deceleration, always positive
#endif
    float requestedSpeed;							// The speed that the user asked for

    // These vary depending on how we connect the move with its predecessor and successor, but remain constant while the move is being executed
    float startSpeed, topSpeed, endSpeed;
#if SUPPORT_S_CURVE
    float startAcceleration;
    float movementRatio;							// for moves with extrusion and axis movement this is the ratio of total extrusion to total distance. For non extruding moves it is 1.0.
#endif

	uint32_t clocksNeeded;

#if SUPPORT_ASYNC_MOVES
	LogicalDrivesBitmap ownedDrives;				// logical drives we are allowed to move
#endif

	union
	{
		// Values that are needed only before Prepare is called and in the first few lines of Prepare
		struct
		{
			float accelDistance;
			float decelDistance;
			float targetNextSpeed;					// The speed that the next move would like to start at, used to keep track of the lookahead without making recursive calls
#if SUPPORT_S_CURVE
			float startSpeedRatio;					// the ratio of start speed of this move to the end speed of the previous move needed to maintain the same extrusion speed across the boundary
			float maxPrevEndSpeed;					// the maximum end speed we can have for the previous move to remain within the instantaneous speed change limits
#endif
		} beforePrepare;

		// Values that are not set or accessed before Prepare is called
		struct
		{
			uint32_t moveStartTime;					// clock count at which the move is due to start (before execution) or was started (during execution)
			float averageExtrusionSpeed;			// the average extrusion speed in mm/sec, for applying heater feedforward
			LogicalDrivesBitmap drivesMoving;		// bitmap of logical drives moving - needed to keep track of whether remote drives are moving and to determine when a move that checks endstops has terminated
		} afterPrepare;
	};

#if DDA_LOG_PROBE_CHANGES
	static bool probeTriggered;

	void LogProbePosition() noexcept;
#endif
};

inline bool DDA::CanPauseAfter() const noexcept
{
	return flags.canPauseAfter && !next->IsCommitted();		// we can't easily cancel moves that have already been sent to CAN expansion boards
}

inline bool DDA::IsProvisional() const noexcept
{
#if SUPPORT_S_CURVE
	return GetState() == created || GetState() == planned;
#else
	return GetState() == planned;
#endif
}

// Return true if this move should use input shaping
inline bool DDA::UsesInputShaping() const noexcept
{
	return flags.xyMoving && !flags.isolatedMove;
}

#endif /* DDA_H_ */
