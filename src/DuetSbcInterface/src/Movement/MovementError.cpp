/*
 * MovementError.cpp
 *
 *  Created on: 23 Apr 2025
 *      Author: David
 */

#include "MovementError.h"

const char *_ecv_array GetMovementErrorText(MovementError err) noexcept
{
	switch (err)
	{
	case MovementError::MicrostepPositionTooLarge:	return "microstep position too large";
	case MovementError::UnreachablePosition:			return "unreachable position";
	case MovementError::MoveDurationTooLong:			return "move duration too long";

	case MovementError::Ok:
	case MovementError::NoMovement:
	default:
		return "no error";
	}
}

// End
