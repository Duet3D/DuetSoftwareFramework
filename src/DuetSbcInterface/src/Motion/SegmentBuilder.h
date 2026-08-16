/*
 * SegmentBuilder.h
 *
 * Turns a move's velocity profile into the MoveSegment chain for one drive.
 *
 * This is Move::AddSegment and the segment-building half of Move::AddLinearSegments, lifted out of
 * the imported RepRapFirmware Move.cpp. Two things are deliberately left behind:
 *
 *   - Step scheduling. In the firmware AddLinearSegments also arms the step interrupt for the drive
 *     it just gave work to. There are no local drivers here, so there is nothing to arm; the caller
 *     (DriveTracker) just walks the chain when it wants a position.
 *   - Input shaping. AxisShaper was removed from this project, so only the unshaped path exists.
 *     Shaping happens on the expansion boards instead, which means the segment chain here is the
 *     unshaped profile and lags the boards' real motion slightly during acceleration - see the note
 *     on shapingTimeClocks in MotionConfig.
 *
 * The list algebra in AddSegment is kept as-is, because it is the part that is genuinely subtle: a
 * new segment may start before, during or after any existing one, so adding it can mean splitting
 * an existing segment, merging into it, or both, possibly several times over.
 */

#ifndef SRC_MOTION_SEGMENTBUILDER_H_
#define SRC_MOTION_SEGMENTBUILDER_H_

#include <Motion/MoveProfile.h>
#include <Motion/MoveSegment.h>

namespace Duet::Sbc::Motion::SegmentBuilder
{
	// Add one constant-acceleration segment to `list`, which may be empty and whose existing
	// segments the new one may overlap. Returns the new head of the list.
	//
	// Units are steps for distance and step clocks for time. `pressureAdvanceClocksTimesDuration` is
	// the pressure-advance time constant multiplied by this segment's duration; the extra distance
	// it implies is a * that, since pressure advance adds distance proportional to the speed change.
	MoveSegment* AddSegment(MoveSegment* list,
							uint32_t startTime,
							uint32_t duration,
							motioncalc_t distance,
							motioncalc_t a,
							MovementFlags moveFlags,
							motioncalc_t pressureAdvanceClocksTimesDuration) noexcept;

	// Add the accelerate/steady/decelerate segments for one drive's share of a move.
	//
	// `steps` is that drive's signed movement in microsteps, `profile` the move's velocity profile
	// in mm and step clocks; the ratio between them is what scales the profile onto this drive.
	// `pressureAdvanceClocks` is the drive's pressure-advance time constant, zero for anything that
	// is not an extruder doing a printing move. Returns the new head of the list.
	MoveSegment* AddLinearSegments(MoveSegment* list,
								   uint32_t startTime,
								   const MoveProfile& profile,
								   motioncalc_t steps,
								   MovementFlags moveFlags,
								   motioncalc_t pressureAdvanceClocks = 0) noexcept;
} // namespace Duet::Sbc::Motion::SegmentBuilder

#endif /* SRC_MOTION_SEGMENTBUILDER_H_ */
