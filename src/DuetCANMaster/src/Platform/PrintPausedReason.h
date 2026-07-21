/*
 * PrintPausedReason.h
 *
 *  Created on: 12 Dec 2021
 *      Author: David
 */

#ifndef SRC_PLATFORM_PRINTPAUSEDREASON_H_
#define SRC_PLATFORM_PRINTPAUSEDREASON_H_

#include <cstdint>

// The following values must be kept in sync with DSF! So don't change them unless making major changes to the SBC
// interface.
enum class PrintPausedReason : uint8_t
{
	DontPause = 0, // used by RRF but not by DSF
	User = 1,
	Gcode = 2,
	FilamentChange = 3,
	Trigger = 4,
	HeaterFault = 5,
	FilamentError = 6,
	Stall = 7,
	LowVoltage = 8,
	DriverError = 9
};

#endif /* SRC_PLATFORM_PRINTPAUSEDREASON_H_ */
