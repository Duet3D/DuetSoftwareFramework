/*
 * TaskPriorities.h
 *
 *  Created on: 23 Oct 2019
 *      Author: David
 */

#ifndef SRC_TASKPRIORITIES_H_
#define SRC_TASKPRIORITIES_H_

// Task priorities. These must all be less than configMAX_PRIORITIES defined in FreeRTOSConfig.g.
namespace TaskPriority
{
	constexpr unsigned int IdlePriority = 0;
	constexpr unsigned int SpinPriority = 1; // priority for tasks that rarely block
#if HAS_SBC_INTERFACE
	constexpr unsigned int SbcPriority = 2; // priority for SBC task
#endif
	constexpr unsigned int UsbPriority = 3; // priority of USB task when using tinyusb
	constexpr unsigned int CanSenderPriority = 4;
	constexpr unsigned int CanReceiverPriority = 5;
	constexpr unsigned int CanHiPriReceiverPriority = 6;
	constexpr unsigned int CanClockPriority = 7;

	// Assert that the highest priority one isn't too high
	static_assert(CanClockPriority < configMAX_PRIORITIES);
} // namespace TaskPriority

#endif /* SRC_TASKPRIORITIES_H_ */
