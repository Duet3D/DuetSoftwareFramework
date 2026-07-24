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
void CanException::GetMessage(const StringRef& reply) const noexcept
{
	reply.Clear();

	// Print the message and any parameter
	if (m_message == nullptr)
	{
		reply.cat("<null error message>"); // should not happen
	}
	else if (strstr(m_message, "%s") != nullptr)
	{
		reply.catf(m_message, m_stringParam.c_str());
	}
	else if (strstr(m_message, "%u") != nullptr || strstr(m_message, "%c") != nullptr)
	{
		reply.catf(m_message, m_param.u);
	}
	else
	{
		reply.catf(m_message, m_param.i);
	}
}

// Print basic details for debugging. Currently called only from FileInfoParser, so don't bother printing line and
// column.
void CanException::DebugPrint() const noexcept
{
	if (m_message == nullptr)
	{
		debugPrintf("<null error message>"); // should not happen
	}
	else if (strstr(m_message, "%s") != nullptr)
	{
		debugPrintf(m_message, m_stringParam.c_str());
	}
	else if (strstr(m_message, "%u") != nullptr || strstr(m_message, "%c") != nullptr)
	{
		debugPrintf(m_message, m_param.u);
	}
	else
	{
		debugPrintf(m_message, m_param.i);
	}
	debugPrintf("\n");
}

[[noreturn]] void ThrowCANException(const char* _ecv_array errMsg) THROWS(CanException)
{
	throw CanException(errMsg);
}

[[noreturn]] void ThrowCANException(const char* _ecv_array errMsg, uint32_t param) THROWS(CanException)
{
	throw CanException(errMsg, param);
}

// End
