/*
 * LinkScheduleMoveSink.cpp - see LinkScheduleMoveSink.h.
 */

#include <Motion/LinkScheduleMoveSink.h>

#include <Interface/LinkService.h>

namespace Duet::Sbc
{
	bool LinkScheduleMoveSink::Send(std::span<const uint8_t> packet) noexcept
	{
		return m_link->QueueScheduleMove(packet);
	}

	bool LinkScheduleMoveSink::CanAccept() const noexcept
	{
		return m_link->OutboundHasHeadroom();
	}
}
