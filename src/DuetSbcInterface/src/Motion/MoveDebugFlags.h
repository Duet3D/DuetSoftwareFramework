/*
 * MoveDebugFlags.h
 *
 *  Created on: 20 Aug 2023
 *      Author: David
 */

#ifndef SRC_MOTION_MOVEDEBUGFLAGS_H_
#define SRC_MOTION_MOVEDEBUGFLAGS_H_

namespace MoveDebugFlags
{
	// Bit numbers in the move debug bitmap. The lowest 8 bits are the default settings
	constexpr unsigned int printBadMoves = 0;
	constexpr unsigned int printAllMoves = 1;
	constexpr unsigned int collisionData = 2;

	constexpr unsigned int lookahead = 8; // also used for 3rd order motion control debug
	constexpr unsigned int zProbing = 9;
	constexpr unsigned int axisAllocation = 10;
	constexpr unsigned int simulateSteppingDrivers = 11;
	constexpr unsigned int segments = 12;
	constexpr unsigned int phaseStep = 13;
	constexpr unsigned int printTransforms = 14;
} // namespace MoveDebugFlags

#endif /* SRC_MOTION_MOVEDEBUGFLAGS_H_ */
