/*
 * CommandProcessor.h
 *
 *  Created on: 12 Aug 2019
 *      Author: David
 */

#ifndef SRC_CAN_COMMANDPROCESSOR_H_
#define SRC_CAN_COMMANDPROCESSOR_H_

#include "RepRapFirmware.h"

#if SUPPORT_CAN_EXPANSION

class CanMessageBuffer;

namespace CommandProcessor
{
	void ProcessReceivedMessage(
		CanMessageBuffer& buf) noexcept; // Process a received broadcast or request message and free the message buffer
	// Forward a received CAN message to the SBC, mapping a response back to the request waiting for it.
	// False if the SBC could not take it, which a caller that can send the same message again treats as
	// back-pressure and every other caller treats as the reply being lost.
	bool ForwardMessageToSbc(CanMessageBuffer& buf) noexcept;
	void AppendBadMotionStats(const StringRef& reply) noexcept; // Append diagnostics relating to bad motion messages
} // namespace CommandProcessor

#endif

#endif /* SRC_CAN_COMMANDPROCESSOR_H_ */
