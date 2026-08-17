/*
 * DriveTracker.h
 *
 * Where one drive is, right now.
 *
 * DuetControlServer needs live positions to report - and, at a standstill, to trust as the machine
 * position. The drives are all on CAN-connected boards, so the SBC cannot read them; what it can do
 * is evaluate the same motion it asked for. SegmentBuilder has already turned every scheduled move
 * into a chain of constant-acceleration segments for this drive, so the position at any time is a
 * matter of integrating that chain.
 *
 * This replaces RepRapFirmware's DriveMovement. Most of that class - the DMState machine,
 * CalcNextStepTime and its step-limit and direction-reversal bookkeeping - exists to decide when to
 * pulse a step pin, and there is no step pin here. What is left is the part that answers "how far
 * has this drive got", which in the firmware is carried along as a side effect of stepping.
 *
 * That difference is the one thing to be careful about. In the firmware the step ISR advances
 * through the segment chain, so positionAtSegmentStart and distanceCarriedForwards are always
 * current. Here nothing does that on its own: Advance() must be called, and until it is,
 * GetCurrentPosition() is reporting against whichever segment was current last time. The motion
 * thread calls it once per pass of the ring.
 */

#ifndef SRC_MOTION_DRIVETRACKER_H_
#define SRC_MOTION_DRIVETRACKER_H_

#include <Motion/MoveProfile.h>
#include <Motion/MoveSegment.h>

namespace Duet::Sbc::Motion
{
	class DriveTracker
	{
	  public:
		// Reset to a stationary drive at position zero with no pending motion.
		void Init(size_t logicalDrive) noexcept;

		// Append the segments for one drive's share of a move. `steps` is this drive's signed
		// movement in microsteps and `profile` the move's velocity profile.
		void AddMove(uint32_t startTime,
					 const MoveProfile& profile,
					 motioncalc_t steps,
					 MovementFlags moveFlags,
					 motioncalc_t pressureAdvanceClocks = 0) noexcept;

		// Retire every segment that has finished by `now`, folding it into the motor position.
		void Advance(uint32_t now) noexcept;

		// Position in microsteps at `now`, interpolated within the current segment. Call Advance
		// first: this only looks at the segment at the head of the chain.
		[[nodiscard]] float GetCurrentPosition(uint32_t now) const noexcept;

		// Position as of the last retired segment, i.e. not interpolated. This is the value that is
		// exact at a standstill.
		[[nodiscard]] int32_t GetMotorPosition() const noexcept { return m_currentMotorPosition; }

		// Where this drive was when the most recently added move started.
		//
		// A revert message tells a board the net steps its last move should have taken, so undoing an
		// endstop overshoot needs the move to be measured from somewhere. This is the firmware's
		// DriveMovement::positionAtMoveStart. It describes the last move added rather than the one
		// executing, which is the same move whenever it matters: a move that watches endstops is
		// always an isolated move, so nothing is queued behind it
		[[nodiscard]] int32_t GetPositionAtMoveStart() const noexcept { return m_positionAtMoveStart; }

		// Force the position, discarding any pending motion. For homing, and for resynchronising
		// after a move that an endstop cut short.
		void SetMotorPosition(int32_t position) noexcept;

		[[nodiscard]] bool MotionPending() const noexcept { return m_segments != nullptr; }

		// Net steps taken since this was last called. Extruders use it to report how much filament
		// has actually been moved, which is not recoverable from the position alone once the
		// position has been reset by homing.
		[[nodiscard]] int32_t GetAndClearAccumulatedMovement() noexcept;

		// Drop all pending segments without moving the position. For an emergency stop, where the
		// boards abandon the moves too.
		void ClearMovementPending() noexcept;

	  private:
		// Take up the segment at the head of the chain: fold in short followers, and work out how
		// far it travels and how fast it starts.
		void EnterCurrentSegment() noexcept;

		// Retire a finished segment. The most recently retired one is kept rather than released, so
		// that it is still readable when diagnosing a position that looks wrong.
		void RetireSegment(MoveSegment* segment) noexcept;

		MoveSegment* m_segments = nullptr;		 // pending motion, earliest first
		MoveSegment* m_retiredSegment = nullptr; // kept for diagnostics only

		motioncalc_t m_u = 0;						// initial speed of the current segment, steps/clock
		motioncalc_t m_distanceCarriedForwards = 0; // the fraction of a step left over from the last segment

		int32_t m_currentMotorPosition = 0;	  // microsteps, as of the last retired segment
		int32_t m_positionAtMoveStart = 0;	  // position when the most recent move was added
		int32_t m_positionAtSegmentStart = 0; // position when the current segment was entered
		int32_t m_netStepsThisSegment = 0;	  // what the current segment will add to the position
		int32_t m_movementAccumulator = 0;

		MovementFlags m_segmentFlags{};
		uint8_t m_drive = 0;
		bool m_enteredCurrentSegment = false; // whether the head segment's parameters are loaded
	};
} // namespace Duet::Sbc::Motion

#endif /* SRC_MOTION_DRIVETRACKER_H_ */
