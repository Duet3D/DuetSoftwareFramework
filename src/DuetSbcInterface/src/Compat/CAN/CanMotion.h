/*
 * CanMotion.h - compatibility shim
 *
 * In RepRapFirmware, DDA::Prepare hands each driver's share of a move to CanMotion, which builds
 * one CAN message per expansion board and sends it. The SBC is not on the CAN bus, so its version
 * puts the same information on the SPI link and the controller does the CAN send.
 *
 * This header exists so that Prepare does not have to know which of those it is talking to: the
 * names and signatures are the firmware's, and the implementation in Motion/CanMotionShim.cpp
 * forwards to the ScheduleMoveBuilder the motion system owns.
 *
 * Only the five functions Prepare and PrepareMoves actually call are declared. The rest of the
 * firmware's CanMotion - the driver stop list, urgent messages, position reversion - deals with
 * stopping drivers from the step interrupt, and there is no step interrupt here.
 */

#ifndef SRC_COMPAT_CAN_CANMOTION_H_
#define SRC_COMPAT_CAN_CANMOTION_H_

#include <RepRapFirmware.h>

#include <DuetSpiProtocol/StopRules.h>

class DDA;
struct PrepParams;

namespace CanMotion
{
	// Begin a move, discarding anything left over from one that was abandoned part way through.
	void StartMovement() noexcept;

	// Add one axis driver's share of the move, in net microsteps.
	// stopOnInput is the endstop that stops this driver, packed by Motion::MakeStopInput, or
	// Motion::kNoStopInput. stopGroup and stopAction say what else that endstop stops. The
	// controller does the watching; see Motion/MoveParams.h
	void AddAxisMovement(const PrepParams& params, DriverId canDriver, int32_t steps, uint32_t stopOnInput,
						 uint8_t stopGroup, duet::spi::protocol::StopAction stopAction) noexcept;

	// Add one extruder driver's share, in microsteps including fractional parts.
	void AddExtruderMovement(const PrepParams& params, DriverId canDriver, float extrusion,
							 bool usePressureAdvance) noexcept;

	// Send the move and return how many step clocks the boards will take over it, or 0 if nothing
	// was sent. Prepare extends its own figure to match when this is larger.
	uint32_t FinishMovement(const DDA& dda, uint32_t moveStartTime, bool simulating) noexcept;

	// False when the link is too backed up to take another move, so that PrepareMoves waits rather
	// than dropping one.
	bool CanPrepareMove() noexcept;
}

#endif /* SRC_COMPAT_CAN_CANMOTION_H_ */
