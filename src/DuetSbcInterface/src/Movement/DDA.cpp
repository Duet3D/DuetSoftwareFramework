/*
 * DDA.cpp
 *
 *  Created on: 7 Dec 2014
 *      Author: David
 */

#include "DDA.h"
#include "DDARing.h"
#include "MoveDebugFlags.h"
#include <Motion/Log.h>
#include "MoveTiming.h"
#include "StepTimer.h"

#include <Motion/MotionSystem.h>
#include <Motion/ScheduleMoveBuilder.h>

#include <algorithm>
#include <limits>

#define DDA_MOVE_DEBUG	(0)

#if DDA_MOVE_DEBUG

// Structure to hold the essential parameters of a move, for debugging
struct MoveParameters
{
	float accelDistance;
	float steadyDistance;
	float decelDistance;
	float requestedSpeed;
	float startSpeed;
	float topSpeed;
	float endSpeed;
	float targetNextSpeed;
	uint32_t endstopChecks;
	uint32_t flags;

	MoveParameters() noexcept
	{
		accelDistance = steadyDistance = decelDistance = requestedSpeed = startSpeed = topSpeed = endSpeed = targetNextSpeed = 0.0;
		endstopChecks = 0;
		flags = 0;
	}

	void DebugPrint() const noexcept
	{
		Duet::Sbc::Motion::LogMessage("%f,%f,%f,%f,%f,%f,%f,%f,%08" PRIX32 ",%08" PRIx32 "\n",
								(double)accelDistance, (double)steadyDistance, (double)decelDistance, (double)requestedSpeed, (double)startSpeed, (double)topSpeed, (double)endSpeed,
								(double)targetNextSpeed, endstopChecks, flags);
	}

	static void PrintHeading() noexcept
	{
		Duet::Sbc::Motion::LogMessage("accelDistance,steadyDistance,decelDistance,requestedSpeed,startSpeed,"
									  "topSpeed,endSpeed,targetNextSpeed,endstopChecks,flags\n");
	}
};

const size_t NumSavedMoves = 128;

static MoveParameters savedMoves[NumSavedMoves];
static size_t savedMovePointer = 0;

// Print the saved moves in CSV format for analysis
/*static*/ void DDA::PrintMoves() noexcept
{
	// Print the saved moved in CSV format
	MoveParameters::PrintHeading();
	for (size_t i = 0; i < NumSavedMoves; ++i)
	{
		savedMoves[savedMovePointer].DebugPrint();
		savedMovePointer = (savedMovePointer + 1) % NumSavedMoves;
	}
}

#else

/*static*/ void DDA::PrintMoves() noexcept { }

#endif

// Work out the velocity profile this move will be executed with. Only called for non-Scurve moves.
// As a side effect it sets clocksNeeded. If 3rd order motion control is used it also sets the start speed and acceleration in the following DDA.
PrepParams DDA::BuildProfile() noexcept
{
	PrepParams params;
	params.totalDistance = m_totalDistance;
	// Due to rounding error, for an accelerate-decelerate move we may have accelDistance+decelDistance slightly greater than totalDistance.
	// We need to make sure that accelDistance <= decelStartDistance for subsequent calculations to work.
#if SUPPORT_S_CURVE
	params.jerk = 0.0;							// this signals that we are not using S-curve acceleration
	params.peakAcceleration = params.initialAcceleration = m_maxAcceleration;
	params.peakDeceleration = params.initialDeceleration = -m_maxAcceleration;
	params.phaseClocks[0] = params.phaseClocks[2] = params.phaseClocks[4] = params.phaseClocks[6] = 0;
	params.phaseClocks[1] = std::lrint((motioncalc_t)(m_topSpeed - m_startSpeed)/params.peakAcceleration);
	params.phaseClocks[5] = std::lrint((motioncalc_t)(m_endSpeed - m_topSpeed)/params.peakDeceleration);
	params.distances[0] = params.distances[2] = params.distances[4] = params.distances[6] = 0.0;
	params.distances[5] = m_beforePrepare.decelDistance;
	const motioncalc_t decelStartDistance = m_totalDistance - m_beforePrepare.decelDistance;
	params.distances[1] = min<motioncalc_t>(m_beforePrepare.accelDistance, decelStartDistance);
	params.distances[3] = decelStartDistance - params.distances[1];
	params.phaseClocks[3] = (params.distances[3] <= (motioncalc_t)0.0) ? 0 : std::lrint(params.distances[3]/(motioncalc_t)m_topSpeed);
	m_clocksNeeded = params.phaseClocks[1] + params.phaseClocks[3] + params.phaseClocks[5];
	params.speedsCalculated = false;
#else
	params.decelStartDistance = m_totalDistance - m_beforePrepare.decelDistance;
	params.accelDistance = min<motioncalc_t>(m_beforePrepare.accelDistance, params.decelStartDistance);
	params.acceleration = m_maxAcceleration;
	params.deceleration = -m_maxAcceleration;
	params.accelClocks = std::lrint((motioncalc_t)(m_topSpeed - m_startSpeed)/params.acceleration);
	params.decelClocks = std::lrint((motioncalc_t)(m_endSpeed - m_topSpeed)/params.deceleration);
	const motioncalc_t steadyDistance = params.decelStartDistance - params.accelDistance;
	params.steadyClocks = (steadyDistance <= (motioncalc_t)0.0) ? 0 : std::lrint(steadyDistance/(motioncalc_t)m_topSpeed);
	m_clocksNeeded = params.accelClocks + params.steadyClocks + params.decelClocks;
#endif
	params.startSpeed = m_startSpeed;
	params.topSpeed = m_topSpeed;
	params.endSpeed = m_endSpeed;
	return params;
}

#if SUPPORT_S_CURVE

void PrepParams::EnsureSpeedsSet() const noexcept
{
	if (!speedsCalculated)
	{
		phase1StartSpeed = (phaseClocks[0] == 0) ? startSpeed : startSpeed + (initialAcceleration + (motioncalc_t)0.5 * jerk * (motioncalc_t)phaseClocks[0]) * (motioncalc_t)phaseClocks[0];
		phase1EndSpeed = phase1StartSpeed + peakAcceleration * (motioncalc_t)phaseClocks[1];
		phase5StartSpeed = (phaseClocks[4] == 0) ? topSpeed : topSpeed - (motioncalc_t)0.5 * jerk * Msquare((motioncalc_t)phaseClocks[4]);
		phase5EndSpeed = phase5StartSpeed + peakDeceleration * (motioncalc_t)phaseClocks[5];
		speedsCalculated = true;
	}
}

#endif

void PrepParams::DebugPrint() const noexcept
{
	DebugPrintf("pp: td=%.3g ss=%.4g ts=%.4g es=%.4g"
#if SUPPORT_S_CURVE
				" ad=[%.4g %.4g %.4g] sd=%.4g dd=[%.4g %.4g %.4g] a=[%.4g %.4g] d=[%.4g %.4g] ac=[%" PRIu32 " %" PRIu32 " %" PRIu32 "] sc=%" PRIu32 " dc=[%" PRIu32 " %" PRIu32 " %" PRIu32 "]"
#else
				" ad=%.4g dsd=%.4g a=%.4g d=%.4g ac=%" PRIu32 " sc=%" PRIu32 " dc=%" PRIu32
#endif
				"\n",
					(double)totalDistance, (double)startSpeed, (double)topSpeed, (double)endSpeed,
#if SUPPORT_S_CURVE
					(double)distances[0], (double)distances[1], (double)distances[2],
					(double)distances[3],
					(double)distances[4], (double)distances[5], (double)distances[6],
					(double)initialAcceleration, (double)peakAcceleration,
					(double)initialDeceleration, (double)peakDeceleration,
					phaseClocks[0], phaseClocks[1], phaseClocks[2], phaseClocks[3], phaseClocks[4], phaseClocks[5], phaseClocks[6]
#else
					(double)accelDistance, (double)decelStartDistance,
					(double)acceleration, (double)deceleration,
					accelClocks, steadyClocks, decelClocks
#endif
				);
}

DDA::DDA(DDA *_ecv_null n) noexcept : m_next(n), m_prev(nullptr)
{
	// Set the endpoints to zero, because Move will ask for them.
	// They will be wrong if we are on a delta. We take care of that when we process the M665 command in config.g.
	for (int32_t& ep : m_endPoint)
	{
		ep = 0;
	}

	m_flags.all = 0;						// in particular we need to set endCoordinatesValid, usePressureAdvance to false, stateBits to empty, also checkEndstops false for the ATE build
	SetState(Empty);					// should alrrady be covered by the above
	m_moveId = 0;
}

// Return the number of clocks this DDA still needs to execute.
uint32_t DDA::GetTimeLeft() const noexcept
{
	switch (GetState())
	{
	case Planned:
		return m_clocksNeeded;
	case Committed:
		{
			const int32_t timeExecuting = (int32_t)(StepTimer::GetMovementTimerTicks() - m_afterPrepare.moveStartTime);
			return (timeExecuting <= 0) ? m_clocksNeeded							// move has not started yet
					: ((uint32_t)timeExecuting > m_clocksNeeded) ? 0				// move has completed
						: m_clocksNeeded - (uint32_t)timeExecuting;				// move is part way through
		}
	default:
		return 0;
	}
}

void DDA::DebugPrintVector(const char *_ecv_array name, const float *_ecv_array vec, size_t len) const noexcept
{
	DebugPrintf("%s=", name);
	for (size_t i = 0; i < len; ++i)
	{
		const char c = (i == 0) ? '[' : ' ';
		if (vec[i] == 0.0)
		{
			DebugPrintf("%c0", c);						// just print 0 to save characters
		}
		else
		{
			DebugPrintf("%c%.4g", c, (double)vec[i]);
		}
	}
	DebugPrintf("]");
}

// Print the text followed by the DDA only
void DDA::DebugPrint(const char *_ecv_array tag) const noexcept
{
	DebugPrintf("%s %u ts=%" PRIu32 " DDA: s=%.4g", tag, (unsigned int)GetState(), m_afterPrepare.moveStartTime, (double)m_totalDistance);
	DebugPrintVector(" vec", m_directionVector, maxAxesPlusExtruders);
	DebugPrintf("\n"
#if SUPPORT_S_CURVE
				"a=[%.4e, %.4e, 0.0] j=%.4e"
#else
				"a=%.4e"
#endif
				" reqv=%.4e startv=%.4e topv=%.4e endv=%.4e cks=%" PRIu32 " id=%" PRIu32 " fl=0x%06" PRIx32 "\n",
#if SUPPORT_S_CURVE
				(double)m_startAcceleration, (double)m_maxAcceleration, (double)m_jerk,
#else
				(double)m_maxAcceleration,
#endif
				(double)m_requestedSpeed, (double)m_startSpeed, (double)m_topSpeed, (double)m_endSpeed, m_clocksNeeded, m_moveId, m_flags.all);
}

// Take up a move that DuetControlServer has already worked out the shape of.
//
// Upstream this is the tail of InitStandardMove. Steps 1 to 6 - endpoints, direction vector,
// normalisation, the acceleration and speed limits - happen in DCS now, because they need the
// kinematics and the machine configuration and they depend on nothing but the move itself. What is
// left is step 7 onwards, which needs the moves either side of this one and therefore needs the
// ring. The code below is upstream's, with the parameters read from the message rather than
// computed.
MovementError DDA::InitFromParams(DDARing& ring, const Duet::Sbc::Motion::MoveParamsHeader& params) noexcept
{
	using namespace Duet::Sbc::Motion;

	m_moveId = params.moveId;
	m_flags.all = 0;												// set all flags false

#if SUPPORT_ASYNC_MOVES
	m_ownedDrives = LogicalDrivesBitmap(params.ownedDrives);
#endif

	// A drive DCS did not send is one this move does not touch, so it ends where the previous move
	// left it. Getting this wrong is not a small error: Prepare takes the difference against the
	// previous DDA's endpoint, so a stale entry moves the drive by the whole difference.
	const std::span<const int32_t> endPoints = MoveParamsEndPoints(params);
	const std::span<const float> directions = MoveParamsDirectionVector(params);
	const std::span<const Duet::Sbc::Motion::MoveStopInput> stopInputs = MoveParamsStopInputs(params);
	for (size_t drive = 0; drive < maxAxesPlusExtruders; ++drive)
	{
		// The bound comes from the spans rather than from numDrives again: they were built from it
		// once, at the point where the record's length was known to cover it.
		if (drive < endPoints.size())
		{
			m_endPoint[drive] = endPoints[drive];
			m_directionVector[drive] = directions[drive];
			m_stopOnInput[drive] = stopInputs[drive];
		}
		else
		{
			m_endPoint[drive] = m_prev->m_endPoint[drive];
			m_directionVector[drive] = 0.0;
			m_stopOnInput[drive] = Duet::Sbc::Motion::kNoStopSwitches;
		}
	}

	m_totalDistance = params.totalDistance;
	m_maxAcceleration = params.maxAcceleration;
	m_requestedSpeed = params.requestedSpeed;

	m_flags.canPauseAfter = (params.flags & MoveFlags::canPauseAfter) != 0;
	m_flags.checkEndstops = (params.flags & MoveFlags::checkEndstops) != 0;
	m_flags.sharedSwitches = (params.flags & MoveFlags::sharedSwitches) != 0;
	m_flags.usingStandardFeedrate = (params.flags & MoveFlags::usingStandardFeedrate) != 0;
	m_flags.usePressureAdvance = (params.flags & MoveFlags::usePressureAdvance) != 0;
	m_flags.isPrintingMove = (params.flags & MoveFlags::isPrintingMove) != 0;
	m_flags.xyMoving = (params.flags & MoveFlags::xyMoving) != 0;
	m_flags.isNonPrintingExtruderMove = (params.flags & MoveFlags::isNonPrintingExtruderMove) != 0;
	m_flags.continuousRotationShortcut = (params.flags & MoveFlags::continuousRotationShortcut) != 0;
	m_flags.isolatedMove = (params.flags & MoveFlags::isolatedMove) != 0;
	m_flags.hasForwardExtrusion = (params.flags & MoveFlags::hasForwardExtrusion) != 0;

	// 7. Calculate the provisional accelerate and decelerate distances and the top speed
	m_endSpeed = 0.0;																	// until we have a following move

	const Duet::Sbc::Motion::MotionSystem& move = ring.GetMove();
	const bool melded =
		   m_prev->IsProvisional()													// if previous move has not started yet
		&& (   move.GetJerkPolicy() != 0											// and melding is allowed
			|| (   m_flags.isPrintingMove == m_prev->m_flags.isPrintingMove
				&& m_flags.xyMoving == m_prev->m_flags.xyMoving
				&& m_flags.isNonPrintingExtruderMove == m_prev->m_flags.isNonPrintingExtruderMove		// this is to prevent extruder-only move being melded with Z-axis moves (issue 990)
			   )
		   );
	if (melded)
	{
		// Try to meld this move to the previous move to avoid stop/start
		// Assuming that this move ends with zero speed, calculate the maximum possible starting speed: u^2 = -2as limited to the requested speed
		m_prev->m_beforePrepare.targetNextSpeed = min<float>(fastSqrtf(m_maxAcceleration * m_totalDistance * 2.0), m_requestedSpeed);
		DoLookahead(ring, m_prev);
		m_startSpeed = m_prev->m_endSpeed;
	}
	else
	{
		m_startSpeed = 0.0;															// there is no previous move that we can adjust, so start at zero speed.
	}

	const MovementError rslt = RecalculateMove(ring);
	if (rslt != MovementError::Ok)
	{
		// Leave the DDA empty. Promoting it to Planned would hand the ring's add slot to a move that
		// was rejected: CanAddMove would stay false for ever and Spin would prepare and commit it.
		if (melded)
		{
			// The lookahead above raised the previous move's end speed so this one could carry on
			// from it. Nothing is going to, so re-plan the chain to come to rest instead - otherwise
			// the last move in the ring ends at speed with nothing following it.
			m_prev->m_beforePrepare.targetNextSpeed = 0.0;
			DoLookahead(ring, m_prev);
		}
		return rslt;
	}

	SetState(Planned);
	return rslt;
}

// Return true if this move is or might have been intended to be a deceleration-only move
// A move planned as a deceleration-only move may have a short acceleration segment at the start because of rounding error
bool DDA::IsDecelerationMove() const noexcept
{
	return m_beforePrepare.decelDistance == m_totalDistance					// the simple case - is a deceleration-only move
			|| (m_topSpeed < m_requestedSpeed								// can't have been intended as deceleration-only if it reaches the requested speed
				&& m_beforePrepare.decelDistance > 0.98 * m_totalDistance	// rounding error can only go so far
			   );
}

// Return true if this move is or might have been intended to be a deceleration-only move
// A move planned as a deceleration-only move may have a short acceleration segment at the start because of rounding error
bool DDA::IsAccelerationMove() const noexcept
{
	return m_beforePrepare.accelDistance == m_totalDistance					// the simple case - is an acceleration-only move
			|| (m_topSpeed < m_requestedSpeed								// can't have been intended as deceleration-only if it reaches the requested speed
				&& m_beforePrepare.accelDistance > 0.98 * m_totalDistance	// rounding error can only go so far
			   );
}

#if 0
#define LA_DEBUG	do { if (fabsf(fsquare(laDDA->m_endSpeed) - fsquare(laDDA->m_startSpeed)) > 2.02 * laDDA->m_maxAcceleration * laDDA->m_totalDistance \
								|| laDDA->m_topSpeed > laDDA->m_requestedSpeed) { \
							DebugPrintf("%s(%d) ", __FILE__, __LINE__);		\
							laDDA->DebugPrint("la");	\
						}	\
					} while(false)
#else
#define LA_DEBUG	do { } while(false)
#endif

// Try to increase the ending speed of this move to allow the next move to start at targetNextSpeed.
// Only called if this move and the next one (which we have just added) are both printing moves, or both non-printing moves.
/*static*/ void DDA::DoLookahead(DDARing& ring, DDA *laDDA) noexcept
//pre(state == provisional)
{
//	if (ring.GetMove().IsDebugEnabled(Module::DDA)) DebugPrintf("Adjusting, %f\n", laDDA->m_beforePrepare.targetNextSpeed);
	unsigned int laDepth = 0;

	// Iterate through the list towards earlier moves
	for (;;)
	{
		// We have been asked to adjust the end speed of this move to match the next move starting at targetNextSpeed
		if (laDDA->m_beforePrepare.targetNextSpeed > laDDA->m_requestedSpeed)
		{
			laDDA->m_beforePrepare.targetNextSpeed = laDDA->m_requestedSpeed;			// don't try for an end speed higher than our requested speed
		}
		if (laDDA->m_topSpeed >= laDDA->m_requestedSpeed)
		{
			// This move already reaches its top speed, so we just need to adjust the deceleration part
			break;																	// stop going back to previous moves
		}
		if (   laDDA->IsDecelerationMove()
			 && laDDA->m_prev->m_beforePrepare.decelDistance > 0.0						// if the previous move has no deceleration phase then no point in adjusting it
			)
		{
			// This is a deceleration-only move, and the previous one has a deceleration phase. We may have to adjust the previous move as well to get optimum behaviour.
			if (   laDDA->m_prev->IsProvisional()
				&& (   ring.GetMove().GetJerkPolicy() != 0
					|| (   laDDA->m_prev->m_flags.xyMoving == laDDA->m_flags.xyMoving
						&& (   laDDA->m_prev->m_flags.isPrintingMove == laDDA->m_flags.isPrintingMove
							|| (laDDA->m_prev->m_flags.isPrintingMove && laDDA->m_prev->m_requestedSpeed == laDDA->m_requestedSpeed)	// special case to support coast-to-end
						   )
					   )
				   )
			   )
			{
				laDDA->MatchSpeeds(ring.GetMove());
				const float maxStartSpeed = fastSqrtf(fsquare(laDDA->m_beforePrepare.targetNextSpeed) + (2 * laDDA->m_maxAcceleration * laDDA->m_totalDistance));
				laDDA->m_prev->m_beforePrepare.targetNextSpeed = min<float>(maxStartSpeed, laDDA->m_requestedSpeed);

				// Still going up
				laDDA = _ecv_not_null(laDDA->m_prev);
				++laDepth;
				continue;
			}

			// This move is a deceleration-only move but we can't adjust the previous one
			if (laDDA->m_prev->IsCommitted())
			{
				laDDA->m_flags.hadLookaheadUnderrun = true;
			}
		}

		// This move doesn't reach its requested speed, but either it isn't a deceleration-only move or we can't adjust the previous one
		// Set its target end speed to the minimum of the requested speed and the highest we can reach
		const float maxReachableSpeed = fastSqrtf(fsquare(laDDA->m_startSpeed) + (2 * laDDA->m_maxAcceleration * laDDA->m_totalDistance));
		if (laDDA->m_beforePrepare.targetNextSpeed > maxReachableSpeed)
		{
			laDDA->m_beforePrepare.targetNextSpeed = maxReachableSpeed;
		}
		break;
	}

	laDDA->MatchSpeeds(ring.GetMove());										// adjust the target end speed if necessary

	// Iterate back through the list towards later moves
	for (;;)
	{
		if (laDDA->m_beforePrepare.targetNextSpeed < laDDA->m_endSpeed)
		{
			// This situation should not normally happen except by a small amount because of rounding error.
			// Don't reduce the end speed of the current move, because that may make the move infeasible.
			// Report a lookahead error if the change is too large to be accounted for by rounding error.
			if (laDDA->m_beforePrepare.targetNextSpeed < laDDA->m_endSpeed * 0.99)
			{
				ring.RecordLookaheadError();
				if (ring.GetMove().GetDebugFlags(Module::Move).IsBitSet(MoveDebugFlags::lookahead))
				{
					DebugPrintf("DDA.cpp(%d) tn=%f ", __LINE__, (double)laDDA->m_beforePrepare.targetNextSpeed);
					laDDA->DebugPrint("la");
				}
			}
		}
		else
		{
			laDDA->m_endSpeed = laDDA->m_beforePrepare.targetNextSpeed;
		}

LA_DEBUG;
		laDDA->RecalculateMove(ring);

		if (laDepth == 0)
		{
#if 0
			if (ring.GetMove().IsDebugEnabled(Module::DDA))
			{
				DebugPrintf("Complete, %f\n", laDDA->m_beforePrepare.targetNextSpeed);
			}
#endif
			return;
		}

		laDDA = _ecv_not_null(laDDA->m_next);
		--laDepth;

		// Going back down the list
		// We have adjusted the end speed of the previous move as much as is possible. Adjust this move to match it.
		laDDA->m_startSpeed = laDDA->m_prev->m_endSpeed;
		const float maxEndSpeed = fastSqrtf(fsquare(laDDA->m_startSpeed) + (2 * laDDA->m_maxAcceleration * laDDA->m_totalDistance));
		if (maxEndSpeed < laDDA->m_beforePrepare.targetNextSpeed)
		{
			laDDA->m_beforePrepare.targetNextSpeed = maxEndSpeed;
		}
	}
}

// Recalculate the top speed, acceleration distance and deceleration distance, and whether we can pause after this move
// This may cause a move that we intended to be a deceleration-only move to have a tiny acceleration segment at the start
// Check that the move will execute in less than 2^31 step clocks and return MovementError::ok if so
MovementError DDA::RecalculateMove(DDARing& ring) noexcept
{
	const float twoA = 2 * m_maxAcceleration;
	m_beforePrepare.accelDistance = (fsquare(m_requestedSpeed) - fsquare(m_startSpeed))/twoA;
	m_beforePrepare.decelDistance = (fsquare(m_requestedSpeed) - fsquare(m_endSpeed))/twoA;
	if (m_beforePrepare.accelDistance + m_beforePrepare.decelDistance < m_totalDistance)
	{
		// This move reaches its top speed
		// It sometimes happens that we get a very short acceleration or deceleration segment. Remove any such segments by reducing the top speed to the start or end speed.
		// Don't do this if the cause is that the top speed is very low because that results in issues 989 and 994
		if (m_startSpeed >= m_endSpeed)
		{
			if (m_startSpeed + m_maxAcceleration * minimumAccelOrDecelClocks > m_requestedSpeed && m_startSpeed >= m_requestedSpeed * 0.9)
			{
				m_topSpeed = m_startSpeed;
				m_beforePrepare.accelDistance = 0.0;
			}
			else
			{
				m_topSpeed = m_requestedSpeed;
			}
		}
		else
		{
			if (m_endSpeed + m_maxAcceleration * minimumAccelOrDecelClocks > m_requestedSpeed && m_endSpeed >= m_requestedSpeed * 0.9)
			{
				m_topSpeed = m_endSpeed;
				m_beforePrepare.decelDistance = 0.0;
			}
			else
			{
				m_topSpeed = m_requestedSpeed;
			}
		}
	}
	else
	{
		// This move has no steady-speed phase, so it's accelerate-decelerate or accelerate-only or decelerate-only move.
		// If V is the peak speed, then (V^2 - u^2)/2a + (V^2 - v^2)/2d = dist
		// So V^2(2a + 2d) = 2a.2d.dist + 2a.v^2 + 2d.u^2
		// So V^2 = (2a.2d.dist + 2a.v^2 + 2d.u^2)/(2a + 2d)
		// We now always set a == d so the above reduces to: V^2 = (2a.dist + v^2 + u^2)/2
		const float vsquared = ((twoA * m_totalDistance) + fsquare(m_endSpeed) + fsquare(m_startSpeed)) * 0.5;
		if (vsquared > fsquare(m_startSpeed) && vsquared > fsquare(m_endSpeed))
		{
			// It's an accelerate-decelerate move. Calculate accelerate distance from: V^2 = u^2 + 2as.
			m_beforePrepare.accelDistance = (vsquared - fsquare(m_startSpeed))/twoA;
			m_beforePrepare.decelDistance = (vsquared - fsquare(m_endSpeed))/twoA;
			m_topSpeed = fastSqrtf(vsquared);
		}
		else
		{
			// It's an accelerate-only or decelerate-only move.
			// Due to rounding errors and babystepping adjustments, we may have to adjust the acceleration or deceleration slightly.
			// It's OK to adjust maxAcceleration because if we get here then we have either an acceleration or a deceleration segment, not both
			if (m_startSpeed < m_endSpeed)
			{
				m_beforePrepare.accelDistance = m_totalDistance;
				m_beforePrepare.decelDistance = 0.0;
				m_topSpeed = m_endSpeed;
				const float newAcceleration = (fsquare(m_endSpeed) - fsquare(m_startSpeed))/(2 * m_totalDistance);
				if (newAcceleration > 1.02 * m_maxAcceleration)
				{
					// The acceleration increase is greater than we expect from rounding error, so record an error
					ring.RecordLookaheadError();
					if (ring.GetMove().GetDebugFlags(Module::Move).IsBitSet(MoveDebugFlags::lookahead))
					{
						DebugPrintf("DDA.cpp(%d) na=%f", __LINE__, (double)newAcceleration);
						DebugPrint("rm");
					}
				}
				m_maxAcceleration = newAcceleration;
			}
			else
			{
				m_beforePrepare.accelDistance = 0.0;
				m_beforePrepare.decelDistance = m_totalDistance;
				m_topSpeed = m_startSpeed;
				const float newDeceleration = (fsquare(m_startSpeed) - fsquare(m_endSpeed))/(2 * m_totalDistance);
				if (newDeceleration > 1.02 * m_maxAcceleration)
				{
					// The deceleration increase is greater than we expect from rounding error, so record an error
					ring.RecordLookaheadError();
					if (ring.GetMove().GetDebugFlags(Module::Move).IsBitSet(MoveDebugFlags::lookahead))
					{
						DebugPrintf("DDA.cpp(%d) nd=%f", __LINE__, (double)newDeceleration);
						DebugPrint("rm");
					}
				}
				m_maxAcceleration = newDeceleration;
			}
		}
	}

	// Set up flags.canPauseAfter
	if (m_flags.canPauseAfter && m_endSpeed != 0.0)
	{
		const Duet::Sbc::Motion::MotionSystem& m = ring.GetMove();
		for (size_t drive = 0; drive < maxAxesPlusExtruders; ++drive)
		{
			if (m_endSpeed * fabsf(m_directionVector[drive]) > m.GetMaxInstantDv(drive))
			{
				m_flags.canPauseAfter = false;
				break;
			}
		}
	}

	// We need to set the number of clocks needed here because we use it before the move has been frozen
	const float totalTime = (2 * m_topSpeed - m_startSpeed - m_endSpeed)/m_maxAcceleration
							+ (m_totalDistance - m_beforePrepare.accelDistance - m_beforePrepare.decelDistance)/m_topSpeed;
	m_clocksNeeded = (uint32_t)totalTime;
	return (totalTime < (float)(std::numeric_limits<int32_t>::max() - 100)) ? MovementError::Ok : MovementError::MoveDurationTooLong;
}

// Decide what speed we would really like this move to end at and the next move to start at, assuming we want to use the same speed for both.
// On entry, targetNextSpeed is the speed we would like the next move after this one to start at and this one to end at
// On return, targetNextSpeed is the actual speed we can achieve without exceeding the jerk limits.
void DDA::MatchSpeeds(const Duet::Sbc::Motion::MotionSystem& move) noexcept
{
	for (size_t drive = 0; drive < maxAxesPlusExtruders; ++drive)
	{
		if (m_directionVector[drive] != 0.0 || m_next->m_directionVector[drive] != 0.0)
		{
			const float totalFraction = fabsf(m_directionVector[drive] - m_next->m_directionVector[drive]);
			const float instantDv = totalFraction * m_beforePrepare.targetNextSpeed;
			const float allowedInstantDv = move.GetPrintingInstantDv(drive);
			if (instantDv > allowedInstantDv)
			{
				m_beforePrepare.targetNextSpeed = allowedInstantDv/totalFraction;
			}
		}
	}
}

// Force an end point. Called when a homing switch is triggered.
void DDA::SetDriveCoordinate(size_t drive, int32_t ep) noexcept
{
	m_endPoint[drive] = ep;
}

// Dispatch this DDA to the move segment queue for execution.
// This must not be called with interrupts disabled, because it calls Platform::EnableDrive.
void DDA::Prepare(DDARing& ring,
#if SUPPORT_S_CURVE
					MovementProfile& plannedProfile,
#endif
					uint32_t prepareAdvanceTime, SimulationMode simMode) noexcept
{
#if SUPPORT_S_CURVE
	PrepParams params;
	if (m_flags.useScurve)
	{
		AllocateMoveFromPlan(plannedProfile, params);
	}
	else
	{
		params = BuildProfile();
	}
#else
	PrepParams params = BuildProfile();
#endif
	params.useInputShaping = UsesInputShaping();

#if SUPPORT_LASER
	if (m_topSpeed < m_requestedSpeed && move.GetMachineType() == MachineType::laser)
	{
		// Scale back the laser power according to the actual speed
		laserPwmOrIoBits.laserPwm = (Pwm_t)((laserPwmOrIoBits.laserPwm * m_topSpeed)/m_requestedSpeed);
	}
#endif

	// Decide when this move should start.
	// Avoid setting the move start time in the past or with very little time before it starts, because this can lead to us trying to modify a segment that is already executing
	Duet::Sbc::Motion::MotionSystem& move = ring.GetMove();
	const uint32_t now = StepTimer::GetMovementTimerTicks();

	// 'prepareAdvanceTime' includes lead time for CAN-connected drivers to receive and queue their movement commands before the deadline.
	// If this move doesn't touch any CAN-connected driver, that lead time is wasted latency (causes M400/G4 stalls under fast host-driven pipelines like OpenPnP);
	// we still need enough of a margin to avoid the Move task modifying a segment list that the step ISR is already executing, so fall back to
	// MoveTiming::AbsoluteMinimumPreparedTime, the same value already trusted elsewhere in this function for that exact purpose.
	// A move that chains directly onto this one (see the 'start this move directly after the previous one' case below) inherits whatever margin
	// we gave this move, so if this move is short and a CAN-connected move is queued close behind it in the ring, that move could end up with
	// less than the CAN lead time it needs. So before shortening our own margin, check the ring for a CAN-connected move that's due within
	// the window we would otherwise be cutting (prepareAdvanceTime - AbsoluteMinimumPreparedTime) and keep the full margin if one is found.
	auto touchesRemoteDriver = [&move](const DDA& dda) noexcept -> bool
	{
		const size_t numTotalAxes = move.GetTotalAxes();
		for (size_t drive = 0; drive < numTotalAxes; ++drive)
		{
			if (dda.m_directionVector[drive] != 0.0)
			{
				const Duet::Sbc::Motion::AxisDriversConfig& config = move.GetAxisDriversConfig(drive);
				for (size_t i = 0; i < config.numDrivers; ++i)
				{
					if (config.driverNumbers[i].IsRemote())
					{
						return true;
					}
				}
			}
		}
		const size_t numExtruders = move.GetNumExtruders();
		for (size_t extruder = 0; extruder < numExtruders; ++extruder)
		{
			if (dda.m_directionVector[ExtruderToLogicalDrive(extruder)] != 0.0 && move.GetExtruderDriver(extruder).IsRemote())
			{
				return true;
			}
		}
		return false;
	};

	bool involvesRemoteDriver = touchesRemoteDriver(*this);
	if (!involvesRemoteDriver)
	{
		// Walk forward through the moves currently queued behind this one. Anything beyond the window we are about to cut
		// (prepareAdvanceTime - AbsoluteMinimumPreparedTime) will get its own fresh margin decision when it's prepared, so we
		// only need to worry about moves that could inherit a start time within that window via direct chaining.
		uint32_t clocksScanned = 0;
		const uint32_t dangerWindow = prepareAdvanceTime - MoveTiming::absoluteMinimumPreparedTime;
		for (const DDA *dda = GetNext(); dda != this && dda->GetState() != DDA::Empty && clocksScanned < dangerWindow; dda = dda->GetNext())
		{
			if (touchesRemoteDriver(*dda))
			{
				involvesRemoteDriver = true;
				break;
			}
			// Underestimate this move's duration (ignore acceleration/deceleration ramps) so that we err on the side of not reducing the margin
			clocksScanned += (dda->m_topSpeed > 0.0) ? (uint32_t)(dda->m_totalDistance / dda->m_topSpeed) : 0;
		}
	}
	const uint32_t localPrepareAdvanceTime = (involvesRemoteDriver) ? prepareAdvanceTime : min<uint32_t>(prepareAdvanceTime, MoveTiming::absoluteMinimumPreparedTime);

	if (m_prev->GetState() == Committed)
	{
		uint32_t prevEndTime = m_prev->m_afterPrepare.moveStartTime + m_prev->m_clocksNeeded;
		// Don't allow the start of a move without input shaping (e.g. retraction/repriming) to overlap a move with input shaping
		if (!params.useInputShaping && m_prev->UsesInputShaping())
		{
			prevEndTime += move.GetShapingTimeClocks();
		}
		if ((int32_t)(prevEndTime - now) >= (int32_t)MoveTiming::absoluteMinimumPreparedTime)
		{
			m_afterPrepare.moveStartTime = prevEndTime;		// start this move directly after the previous one
		}
		else if (m_startSpeed == 0.0)
		{
			m_afterPrepare.moveStartTime = now + localPrepareAdvanceTime;
		}
		else
		{
			m_afterPrepare.moveStartTime = now + MoveTiming::absoluteMinimumPreparedTime;
			move.AddPrepareHiccup();		// move was supposed to follow the previous one directly, so record a hiccup
		}
	}
	else
	{
		m_afterPrepare.moveStartTime = now + localPrepareAdvanceTime;
	}

	if (simMode < SimulationMode::Normal)
	{
		Duet::Sbc::Motion::ScheduleMoveBuilder& scheduler = move.GetScheduleMoveBuilder();
		scheduler.StartMovement();
		float extrusionFraction = 0.0;
		m_afterPrepare.drivesMoving.Clear();

		// One counter per stop group rather than one for the move. An axis whose switches are shared
		// across a set hands them out round-robin over the set's drivers so that all of them are
		// watched by somebody; a move may carry several such sets, and one counter across all of
		// them would interleave one set's switches into another's drivers
		uint8_t nextSwitchInGroup[maxAxesPlusExtruders] = { };
		MovementFlags segFlags{};
		segFlags.Clear();
		segFlags.checkEndstops = m_flags.checkEndstops;
		segFlags.noShaping = !params.useInputShaping;
		segFlags.nonPrintingMove = !m_flags.isPrintingMove;
		for (size_t drive = 0; drive < maxAxesPlusExtruders; ++drive)
		{
#if SUPPORT_ASYNC_MOVES
			if (m_ownedDrives.IsBitSet(drive))
#endif
			{
				if (drive < move.GetTotalAxes())
				{
					// It's a linear axis
					int32_t delta = m_endPoint[drive] - m_prev->m_endPoint[drive];
					if (delta != 0)
					{
						if (m_flags.continuousRotationShortcut && move.IsContinuousRotationAxis(drive))
						{
							// This is a continuous rotation axis, so we may have adjusted the move to cross the 180 degrees position
							const int32_t stepsPerRotation = lrintf(360.0 * move.DriveStepsPerMm(drive));
							if (delta > stepsPerRotation/2)
							{
								delta -= stepsPerRotation;
							}
							else if (delta < -stepsPerRotation/2)
							{
								delta += stepsPerRotation;
							}
						}

						delta = move.ApplyBacklashCompensation(drive, delta);

						// We generate segments even for nonlocal drivers so that the final position is correct and to track the position in near real time
						move.AddLinearSegments(drive, m_afterPrepare.moveStartTime, params, (motioncalc_t)delta, segFlags);
						m_afterPrepare.drivesMoving.SetBit(drive);

						const Duet::Sbc::Motion::AxisDriversConfig& config = move.GetAxisDriversConfig(drive);
						for (size_t i = 0; i < config.numDrivers; ++i)
						{
							const DriverId driver = config.driverNumbers[i];
							if (driver.IsRemote())
							{
								// A driver already sitting on its own switch is given no steps, while
								// the rest of the axis moves. That is what squares a gantry which
								// starts with one side already down: holding the whole axis because
								// one switch is closed would make the move that corrects the skew do
								// nothing. The driver is still named in the message, so it is still
								// enabled and the controller still marks it as not to be stopped
								const int32_t driverSteps =
									Duet::Sbc::Motion::IsDriverHeld(m_stopOnInput[drive], i) ? 0 : delta;

								// Which switch this driver watches. Normally port i of an endstop
								// belongs to driver i of the axis, so it follows from the index. Where
								// an axis' switches are shared across a set of drives it does not:
								// every drive of the set carries the one axis' switches and any of
								// them stops the set, so they are handed out round-robin across the
								// set's drivers purely so that all of them end up watched.
								// RepRapFirmware watches every port of an endstop whatever the action
								const uint8_t group = m_stopOnInput[drive].stopGroup;
								const size_t switchIndex =
									(m_flags.sharedSwitches && group < maxAxesPlusExtruders)
										? (nextSwitchInGroup[group]++ % m_stopOnInput[drive].numSwitches)
										: i;

								// The group is DCS's, because the kinematics is what says which drives
								// have to stop together and the controller holds no axis-to-driver map
								scheduler.AddAxisMovement(
									params, driver, driverSteps,
									Duet::Sbc::Motion::StopInputForSwitch(m_stopOnInput[drive], switchIndex,
																		  driver.boardAddress),
									m_stopOnInput[drive].stopGroup, m_stopOnInput[drive].stopAction);
							}
						}
					}
				}
				else
				{
					// It's an extruder drive
					if (m_directionVector[drive] != 0.0)
					{
						const size_t extruder = LogicalDriveToExtruder(drive);

						// Upstream checks here for cold extrusion, which needs the tool and its
						// temperatures. DuetControlServer has both and refuses the move before it
						// gets here, so there is nothing left to check.
						{
	
							if (m_flags.isPrintingMove && m_directionVector[drive] > 0.0)
							{
								extrusionFraction += m_directionVector[drive];					// accumulate the total extrusion fraction
							}

#if SUPPORT_NONLINEAR_EXTRUSION
							// Add the nonlinear extrusion correction to totalExtrusion.
							// If we are given a stupidly short move to execute then clocksNeeded can be zero, which leads to NaNs in this code; so we need to guard against that.
							if (m_flags.isPrintingMove && m_clocksNeeded != 0)
							{
								const Duet::Sbc::Motion::NonlinearExtrusion& nl = move.GetExtrusionCoefficients(extruder);
								float& dv = m_directionVector[drive];
								const float averageExtrusionSpeed = (m_totalDistance * dv * stepClockRate)/(float)m_clocksNeeded;		// need speed in mm/sec for nonlinear extrusion calculation
								const float factor = 1.0 + min<float>((nl.a + (nl.b * averageExtrusionSpeed)) * averageExtrusionSpeed, nl.limit);
								dv *= factor;
							}
#endif

							const motioncalc_t delta = m_totalDistance * m_directionVector[drive] * move.DriveStepsPerMm(drive);

							// We generate segments even for nonlocal extruders in order to track extruder position
							move.AddLinearSegments(drive, m_afterPrepare.moveStartTime, params, delta, segFlags.AddIsExtruder());

							const DriverId driver = move.GetExtruderDriver(extruder);
							if (driver.IsRemote())
							{
								// The MovementLinearShaped message requires the extrusion amount in steps to be passed as a float. The remote board adds the PA and handles fractional steps.
								scheduler.AddExtruderMovement(params, driver, (float)delta, m_flags.usePressureAdvance);
							}
							m_afterPrepare.drivesMoving.SetBit(drive);
						}
					}
				}
			}
		}

		m_afterPrepare.averageExtrusionSpeed = (extrusionFraction * m_totalDistance * (float)stepClockRate)/(float)m_clocksNeeded;

		SetState(Committed);

		// Upstream re-checks the endstops here, from the step interrupt's priority, so that a switch
		// that is already triggered stops the motors concerned before they are told to move. Here the
		// endstops belong to the expansion boards and to DCS: the boards stop their own drivers, and
		// the ScheduleMove packet's CheckEndstops flag is what tells the controller to arm that.

		const uint32_t canClocksNeeded = scheduler.FinishMovement(m_moveId, m_afterPrepare.moveStartTime,
																 simMode != SimulationMode::Off, IsCheckingEndstops(),
																 UsesInputShaping());
		if (canClocksNeeded > m_clocksNeeded)
		{
			// Due to rounding error in the calculations, we quite often calculate the CAN move as being longer than our previously-calculated value, normally by just one clock.
			// Extend our move time in this case so that the expansion boards don't need to catch up.
			m_clocksNeeded = canClocksNeeded;
		}

		if (move.GetDebugFlags(Module::Move).IsBitSet(MoveDebugFlags::printAllMoves))		// show the prepared DDA if debug enabled
		{
			DebugPrint("pr");
		}

#if DDA_MOVE_DEBUG
		MoveParameters& m = savedMoves[savedMovePointer];
		m.accelDistance = m_beforePrepare.accelDistance;
		m.decelDistance = m_beforePrepare.decelDistance;
		m.steadyDistance = m_totalDistance - m_beforePrepare.accelDistance - m_beforePrepare.decelDistance;
		m.requestedSpeed = m_requestedSpeed;
		m.startSpeed = m_startSpeed;
		m.topSpeed = m_topSpeed;
		m.endSpeed = m_endSpeed;
		m.targetNextSpeed = m_beforePrepare.targetNextSpeed;
		m.endstopChecks = m_flags.checkEndstops;
		m.flags = m_flags.all;
		savedMovePointer = (savedMovePointer + 1) % NumSavedMoves;
#endif
	}
	else
	{
		SetState(Committed);
	}
}

// Check whether a committed move has finished
bool DDA::HasExpired(const Duet::Sbc::Motion::MotionSystem& move) const noexcept
{
	// Note, for Z leadscrew adjustment moves (and any other individual motor moves that we may support in future), we must not use drivesMoving, because it doesn't describe the drivers that are moving.
	return (m_flags.checkEndstops)
			? move.AreDrivesStopped(m_afterPrepare.drivesMoving)
				: (int32_t)(StepTimer::GetMovementTimerTicks() - GetMoveFinishTime()) >= 0;
}

// Free up this DDA, returning true if the lookahead underrun flag was set
bool DDA::Free() noexcept
{
	SetState(Empty);
	return m_flags.hadLookaheadUnderrun;
}

// End
