/*
 * LinkScheduleMoveSink.cpp - see LinkScheduleMoveSink.h.
 */

#include "LinkScheduleMoveSink.h"

#include "SbcInterface.h"

namespace Duet::Sbc
{
	bool LinkScheduleMoveSink::Send(const void *packet, size_t length) noexcept
	{
		return m_link->QueueScheduleMove(static_cast<const uint8_t *>(packet), length);
	}

	bool LinkScheduleMoveSink::CanAccept() const noexcept
	{
		return m_link->OutboundHasHeadroom();
	}
}
