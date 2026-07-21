/*
 * UniqueId.cpp
 *
 *  Created on: 4 Oct 2021
 *      Author: David
 */

#include "UniqueId.h"

// Append the unique ID in character form to an output buffer
void UniqueId::AppendCharsToBuffer(OutputBuffer *buf) const noexcept
{
	AppendCharsTo([buf](char c)-> void { buf->cat(c);});
}

// End
