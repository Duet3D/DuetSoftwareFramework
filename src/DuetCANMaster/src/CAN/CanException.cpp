/*
 * ParseException.cpp
 *
 *  Created on: 21 Dec 2019
 *      Author: David
 */

#include "CanException.h"

#include <General/StringRef.h>
#include <Platform/RepRap.h>
#include <Platform/Tasks.h>

// Construct the error message. This will be prefixed with "Error: " when it is returned to the user.
void CanException::GetMessage(const StringRef &reply) const noexcept
{
    reply.Clear();

	// Print the message and any parameter
	if (message == nullptr)
	{
		reply.cat("<null error message>");					// should not happen
	}
	else if (strstr(message, "%s") != nullptr)
	{
		reply.catf(message, stringParam.c_str());
	}
	else if (strstr(message, "%u") != nullptr || strstr(message, "%c") != nullptr)
	{
		reply.catf(message, param.u);
	}
	else
	{
		reply.catf(message, param.i);
	}
}

// Print basic details for debugging. Currently called only from FileInfoParser, so don't bother printing line and column.
void CanException::DebugPrint() const noexcept
{
	if (message == nullptr)
	{
		debugPrintf("<null error message>");					// should not happen
	}
	else if (strstr(message, "%s") != nullptr)
	{
		debugPrintf(message, stringParam.c_str());
	}
	else if (strstr(message, "%u") != nullptr || strstr(message, "%c") != nullptr)
	{
		debugPrintf(message, param.u);
	}
	else
	{
		debugPrintf(message, param.i);
	}
	debugPrintf("\n");
}

[[noreturn]] void ThrowCANException(const char *_ecv_array errMsg) THROWS(CanException)
{
	throw CanException(errMsg);
}

[[noreturn]] void ThrowCANException(const char *_ecv_array errMsg, uint32_t param) THROWS(CanException)
{
	throw CanException(errMsg, param);
}

// End
