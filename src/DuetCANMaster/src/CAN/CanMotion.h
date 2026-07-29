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

	// The next 4 functions may be called from the step ISR, so they can't send CAN messages directly
	void StopDriverWhenProvisional(DriverId driver) noexcept pre(driver.IsRemote());
	bool StopDriverWhenExecuting(DriverId driver, int32_t netStepsTaken) noexcept pre(driver.IsRemote());
	void FinishedStoppingDrivers() noexcept;
	bool RevertStoppedDrivers() noexcept;
} // namespace CanMotion

#endif

#endif /* SRC_CAN_CANMOTION_H_ */
