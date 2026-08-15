/*
 * DDA.h
 *
 *  Created on: 7 Dec 2014
 *      Author: David
 *
 * Trimmed for the SBC. This is RepRapFirmware's DDA with everything that belongs on the other side
 * of the split removed. The logic that remains is upstream's rather than a rewrite, but the
 * identifiers are this project's: the whole tree was renamed to the convention in .clang-tidy, so
 * re-syncing against a future RRF release is a merge rather than a diff.
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

class DDARing;

namespace Duet::Sbc::Motion
{
	class MotionSystem;
}

#if SUPPORT_S_CURVE
// The 3rd-order planner. Declared rather than included: MovementProfile.h is not ported, so the
// declarations below that take one by reference are as far as this side goes. See
// src/Documentation/articles/rrf-differences.md.
class MovementProfile;
#endif

// Struct for passing parameters to the segment builder and the schedule builder.
//
// Upstream this declares the whole velocity profile inline. Here the profile itself is
// Motion::MoveProfile, which the segment builder and the ScheduleMove packet also speak, so that
// they need no knowledge of DDA; PrepParams adds the one field that is about how the move is to be
// executed rather than about its shape.
struct PrepParams : public Duet::Sbc::Motion::MoveProfile
{
	bool useInputShaping{};

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
		Empty,				// empty or being filled in
#if SUPPORT_S_CURVE
		Created,			// filled in but not yet planned
#endif
		Planned,			// ready, but could be subject to modifications
		Committed			// has been converted into move segments already
	};

	explicit DDA(DDA *_ecv_null n) noexcept;

	void* operator new(size_t count) { return Tasks::AllocPermanent(count); }
	void* operator new(size_t count, std::align_val_t align) { return Tasks::AllocPermanent(count, align); }
	void operator delete(void* ptr) noexcept {}
	void operator delete(void* ptr, std::align_val_t align) noexcept {}

	// Take up a move DuetControlServer has already worked out the shape of, and plan it against the
	// moves already in the ring. This is where InitStandardMove's step 7 used to begin.
	MovementError InitFromParams(DDARing& ring, const Duet::Sbc::Motion::MoveParamsHeader& params) noexcept SPEED_CRITICAL;

	void SetNext(DDA *n) noexcept { m_next = n; }
	void SetPrevious(DDA *p) noexcept { m_prev = p; }
	bool Free() noexcept;
	void Prepare(DDARing& ring,
#if SUPPORT_S_CURVE
					MovementProfile& plannedProfile,
#endif
					uint32_t prepareAdvanceTime, SimulationMode simMode) noexcept SPEED_CRITICAL;	// Calculate all the values and freeze this DDA
	[[nodiscard]] bool CanPauseAfter() const noexcept;
	[[nodiscard]] bool IsPrintingMove() const noexcept { return m_flags.isPrintingMove; }							// Return true if this involves both XY movement and extrusion
	[[nodiscard]] bool UsingStandardFeedrate() const noexcept { return m_flags.usingStandardFeedrate; }
	[[nodiscard]] bool IsCheckingEndstops() const noexcept { return m_flags.checkEndstops; }
	// True if any watched input stops every driver of this move - RepRapFirmware's stopAll
	[[nodiscard]] bool IsIsolatedMove() const noexcept { return m_flags.isolatedMove; }
	[[nodiscard]] bool NoShaping() const noexcept { return m_flags.isolatedMove; }
	[[nodiscard]] bool UsesInputShaping() const noexcept;									// return true if this move should use input shaping

	[[nodiscard]] DDAState GetState() const noexcept { return (DDAState)m_flags.stateBits; }
	void SetState(DDAState state) noexcept { m_flags.stateBits = (uint32_t)state; }
	[[nodiscard]] bool IsCommitted() const noexcept { return GetState() == DDA::Committed; }
	[[nodiscard]] bool IsProvisional() const noexcept;
	[[nodiscard]] DDA* GetNext() const noexcept { return _ecv_not_null(m_next); }
	[[nodiscard]] DDA* GetPrevious() const noexcept { return _ecv_not_null(m_prev); }
	[[nodiscard]] uint32_t GetTimeLeft() const noexcept;

	[[nodiscard]] const int32_t *_ecv_array DriveCoordinates() const noexcept { return m_endPoint; }				// Get endpoints of a move in machine coordinates
	void SetDriveCoordinate(size_t drive, int32_t ep) noexcept;										// Force an end point

	void SetFeedRate(float rate) noexcept { m_requestedSpeed = rate; }

	// DuetControlServer's correlation id for this move, quoted back when the move completes or fails
	[[nodiscard]] uint32_t GetMoveId() const noexcept { return m_moveId; }

	[[nodiscard]] float GetRequestedSpeedMmPerClock() const noexcept { return m_requestedSpeed; }
	[[nodiscard]] float GetRequestedSpeedMmPerSec() const noexcept { return InverseConvertSpeedToMmPerSec(m_requestedSpeed); }
	[[nodiscard]] float GetTopSpeedMmPerSec() const noexcept { return InverseConvertSpeedToMmPerSec(m_topSpeed); }
	[[nodiscard]] float GetAccelerationMmPerSecSquared() const noexcept							// Get the (peak) acceleration for reporting in the object model
#if SUPPORT_S_CURVE
		{ return InverseConvertAcceleration(m_afterPrepare.peakAcceleration); }
#else
		{ return InverseConvertAcceleration(m_maxAcceleration); }
#endif
	[[nodiscard]] float GetDecelerationMmPerSecSquared() const noexcept							// Get the (peak) acceleration for reporting in the object model
#if SUPPORT_S_CURVE
		{ return InverseConvertAcceleration(m_afterPrepare.peakDeceleration); }
#else
		{ return InverseConvertAcceleration(m_maxAcceleration); }
#endif
#if SUPPORT_S_CURVE
	bool IsSCurveMove() const noexcept { return m_flags.useScurve; }
	bool IsFullyPlanned() const noexcept { return m_flags.fullyPlanned; }
	float GetMovementRatio() const noexcept { return m_movementRatio; }
	void SetSpeedRatioAndMaxJunctionSpeedForPrintingMoves(const Duet::Sbc::Motion::MotionSystem& move) noexcept;
	void SetSpeedRatioAndMaxJunctionSpeedForNonPrintingMoves(const Duet::Sbc::Motion::MotionSystem& move) noexcept;
	void SetStartSpeedAndAcceleration(float speed, float acceleration) noexcept { m_startSpeed = speed; m_startAcceleration = acceleration; }

	static void PlanMoves(DDA *firstUnpreparedMove, MovementProfile& plannedProfile, bool stopping) noexcept;
#endif

	[[nodiscard]] float GetTotalDistance() const noexcept { return m_totalDistance; }

	[[nodiscard]] uint32_t GetClocksNeeded() const noexcept { return m_clocksNeeded; }
	[[nodiscard]] bool HasExpired() const noexcept pre(IsCommitted());
	[[nodiscard]] bool IsNonPrintingExtruderMove() const noexcept { return m_flags.isNonPrintingExtruderMove; }
	[[nodiscard]] uint32_t GetMoveStartTime() const noexcept { return m_afterPrepare.moveStartTime; }
	[[nodiscard]] uint32_t GetMoveFinishTime() const noexcept { return m_afterPrepare.moveStartTime + m_clocksNeeded; }

	[[nodiscard]] float GetAverageExtrusionSpeed() const noexcept pre(IsCommitted()) { return m_afterPrepare.averageExtrusionSpeed; }
	[[nodiscard]] bool HasForwardExtrusion() const noexcept { return m_flags.hasForwardExtrusion; }

	void DebugPrint(const char *_ecv_array tag) const noexcept;				// print the DDA only

	static void PrintMoves() noexcept;										// print saved moves for debugging


private:
	static constexpr float minimumAccelOrDecelClocks = 10.0;				// Minimum number of acceleration or deceleration clocks we try to ensure

	MovementError RecalculateMove(DDARing& ring) noexcept SPEED_CRITICAL;
	static void DoLookahead(DDARing& ring, DDA *laDDA) noexcept SPEED_CRITICAL;	// Try to smooth out moves in the queue

#if SUPPORT_S_CURVE
	static void PlanDeceleratingMoves(double distance, double acc, MovementProfile& plannedProfile) noexcept SPEED_CRITICAL;
	void AllocateMoveFromPlan(MovementProfile& plannedProfile, PrepParams& params) noexcept SPEED_CRITICAL;
#endif

	void MatchSpeeds() noexcept SPEED_CRITICAL;
	[[nodiscard]] bool IsDecelerationMove() const noexcept;								// return true if this move is or have been might have been intended to be a deceleration-only move
	[[nodiscard]] bool IsAccelerationMove() const noexcept;								// return true if this move is or have been might have been intended to be an acceleration-only move
	void DebugPrintVector(const char *_ecv_array name, const float *_ecv_array vec, size_t len) const noexcept;

    DDA *_ecv_null m_next;							// The next one in the ring
	DDA *_ecv_null m_prev;							// The previous one in the ring

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
					 hasForwardExtrusion : 1,		// set if any extruder has forward movement (used by M571)
				 sharedSwitches : 1				// set if an armed axis' switches are watched by drives other than its own, so they must be spread over the set's drivers
#if SUPPORT_S_CURVE
					 , useScurve : 1,				// set if this move uses S-curve acceleration
					 fullyPlanned : 1				// set if this move can't be made to go any faster even if we add more moves to the ring
#endif
					 ;
		};
		uint32_t all;								// so that we can print all the flags at once for debugging
	} m_flags{};

	// DuetControlServer's id for this move. Native never interprets it; it exists so that a
	// MoveCompleted or MoveFailed report can be matched to the move that caused it.
	uint32_t m_moveId;

	int32_t m_endPoint[maxAxesPlusExtruders]{};  		// Machine coordinates of the endpoint
	float m_directionVector[maxAxesPlusExtruders]{};	// The normalised direction vector - first 3 are XYZ Cartesian coordinates even on a delta
	// Which switches stop each drive, and how its drivers share them. Only meaningful when
	// checkEndstops is set. Carried per drive so one move can home several axes at once, each
	// stopping on its own endstop
	Duet::Sbc::Motion::MoveStopInput m_stopOnInput[maxAxesPlusExtruders]{};

    float m_totalDistance{};							// How long is the move in hypercuboid space
    float m_maxAcceleration{};							// The maximum acceleration and deceleration to use, always positive
#if SUPPORT_S_CURVE
	float m_jerk;									// The magnitude of the rate of change of acceleration or deceleration, always positive
#endif
    float m_requestedSpeed{};							// The speed that the user asked for

    // These vary depending on how we connect the move with its predecessor and successor, but remain constant while the move is being executed
    float m_startSpeed{}, m_topSpeed{}, m_endSpeed{};
#if SUPPORT_S_CURVE
    float m_startAcceleration;
    float m_movementRatio;							// for moves with extrusion and axis movement this is the ratio of total extrusion to total distance. For non extruding moves it is 1.0.
#endif

	uint32_t m_clocksNeeded{};

#if SUPPORT_ASYNC_MOVES
	LogicalDrivesBitmap m_ownedDrives;				// logical drives we are allowed to move
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
			float m_startSpeedRatio;					// the ratio of start speed of this move to the end speed of the previous move needed to maintain the same extrusion speed across the boundary
			float m_maxPrevEndSpeed;					// the maximum end speed we can have for the previous move to remain within the instantaneous speed change limits
#endif
		} m_beforePrepare{};

		// Values that are not set or accessed before Prepare is called
		struct
		{
			uint32_t moveStartTime;					// clock count at which the move is due to start (before execution) or was started (during execution)
			float averageExtrusionSpeed;			// the average extrusion speed in mm/sec, for applying heater feedforward
			LogicalDrivesBitmap drivesMoving;		// bitmap of logical drives moving - needed to keep track of whether remote drives are moving and to determine when a move that checks endstops has terminated
		} m_afterPrepare;
	};

};

inline bool DDA::CanPauseAfter() const noexcept
{
	return m_flags.canPauseAfter && !m_next->IsCommitted();		// we can't easily cancel moves that have already been sent to CAN expansion boards
}

inline bool DDA::IsProvisional() const noexcept
{
#if SUPPORT_S_CURVE
	return GetState() == Created || GetState() == Planned;
#else
	return GetState() == Planned;
#endif
}

// Return true if this move should use input shaping
inline bool DDA::UsesInputShaping() const noexcept
{
	return m_flags.xyMoving && !m_flags.isolatedMove;
}

#endif /* DDA_H_ */
