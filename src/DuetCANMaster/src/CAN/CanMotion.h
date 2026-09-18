/*
 * CanMotion.h
 *
 *  Created on: 11 Aug 2019
 *      Author: David
 */

#ifndef SRC_CAN_CANMOTION_H_
#define SRC_CAN_CANMOTION_H_

#include "RepRapFirmware.h"

#if SUPPORT_CAN_EXPANSION

#  if HAS_SBC_INTERFACE
// The wire format, shared with the SBC. Named in full here rather than through the firmware-side
// aliases in SbcMessageFormats.h so that this header stands on its own.
#    include <DuetSpiProtocol/MessageFormats.h>

#    include <span>
#  endif

class CanMessageBuffer;
class PrepParams;

namespace CanMotion
{
	void Init() noexcept;
	void StartMovement() noexcept;
	void AddAxisMovement(const PrepParams& params, DriverId canDriver, int32_t steps) noexcept;
	void AddExtruderMovement(const PrepParams& params,
							 DriverId canDriver,
							 float extrusion,
							 bool usePressureAdvance) noexcept;
	uint32_t FinishMovement(uint32_t moveStartTime, bool simulating, bool checkEndstops) noexcept;

#  if HAS_SBC_INTERFACE
	// Take one ScheduleMove packet from the SBC, which plans the moves in this configuration.
	//
	// The packet's fields are PrepParams, so this fills one and hands it to the same GetBuffer path
	// that DDA::Prepare uses in standalone mode; nothing below here can tell where the move came
	// from. A move too large for one packet arrives as several sharing a moveId, and only the one
	// carrying ScheduleMoveFlags::LastPacket sends what has been accumulated.
	//
	// `drivers` is sized by the caller from the payload it actually received, and is what this
	// iterates - not header.numDrivers, which is a count that arrived in the same packet as the
	// records it describes and so cannot vouch for them.
	void ScheduleFromSbc(const duet::spi::protocol::ScheduleMoveHeader& header,
						 std::span<const duet::spi::protocol::ScheduleMoveDriver> drivers) noexcept;
#  endif

	bool CanPrepareMove() noexcept;
	CanMessageBuffer* _ecv_null GetUrgentMessage() noexcept;

#  if HAS_SBC_INTERFACE
	// Stop every driver of the move in progress that this trigger stops.
	//
	// This is why the controller and not the SBC watches endstops: an input change that had to reach
	// the SBC and come back as a stop would take long enough for the axis to overrun. The move says
	// which input stops which driver (ScheduleMoveDriver::stopOnBoard and stopOnHandle), so matching
	// an incoming change against it needs no lookup and no knowledge of what an endstop means.
	//
	// `reading` is the entry's reading from the input change message. A switch does not need it - the
	// handle identifies the switch - but a stall does: every board reports every driver that stalled
	// under one board-wide handle, and the bitmap in the reading is the only thing that says which.
	// The rule is duet::spi::protocol::WatchMatches, shared with the host-side tests.
	//
	// Called from the CAN receiver task.
	// `stopped` receives the drivers that were stopped, so the caller can report them to the SBC.
	// Returns how many were written, which is at most `stopped.size()`
	// `moveId` receives the move the stopped drivers belong to, for the SBC to check the report
	// against the move it armed.
	size_t StopDriversWatchingInput(uint8_t inputBoard, uint16_t inputHandle, uint32_t reading,
									std::span<duet::spi::protocol::MotionStoppedDriver> stopped,
									uint32_t& moveId) noexcept;

	// Record the level an input was last reported at, so that a move armed on one that is already
	// active is stopped before it starts. What is held and what is not is decided by
	// DuetSpiProtocol/StopRules.h.
	// Called from the CAN receiver task, for a level a board reported, and from the SBC task, for a
	// handle whose monitor is being replaced or dropped
	void NoteInputState(uint8_t inputBoard, uint16_t inputHandle, bool active) noexcept;
#  endif

	// These may be called from the CAN receiver task, so they can't send CAN messages directly.
	//
	// Neither records where the drive should end up. Correcting for the overshoot between the
	// endstop firing and the stop taking effect needs the position at the trigger instant, and that
	// is worked out on the SBC, which evaluates the same motion anyway to report live positions.
	// The SBC sends the revert itself; see FirmwareRequest::MotionStopped
	// Returns true if the driver was found in the move being accumulated and given no steps.
	bool StopDriverWhenProvisional(DriverId driver) noexcept pre(driver.IsRemote());
	bool StopDriverWhenExecuting(DriverId driver) noexcept pre(driver.IsRemote());
} // namespace CanMotion

#endif

#endif /* SRC_CAN_CANMOTION_H_ */
