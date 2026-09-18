/*
 * LinkScheduleMoveSink.h
 *
 * The real destination for prepared moves: the outbound ring that the SPI transfer loop drains.
 *
 * The motion engine knows nothing about this. It writes ScheduleMove packets into whatever
 * Motion::ScheduleMoveSink it was given, which is what lets the whole engine be exercised offline
 * against a recording sink. This is the implementation that puts them on the link, and it lives on
 * this side of the seam because it is the only part that knows there is a link.
 */

#ifndef SRC_MOTION_LINKSCHEDULEMOVESINK_H_
#define SRC_MOTION_LINKSCHEDULEMOVESINK_H_

#include <Motion/ScheduleMoveBuilder.h>

namespace Duet::Sbc
{
	class LinkService;

	class LinkScheduleMoveSink final : public Motion::ScheduleMoveSink
	{
	public:
		explicit LinkScheduleMoveSink(LinkService& link) noexcept : m_link(&link) { }

		// Both are called from the motion thread and neither blocks: the ring is lock-free and a
		// full ring is reported rather than waited on.
		bool Send(std::span<const uint8_t> packet) noexcept override;
		[[nodiscard]] bool CanAccept() const noexcept override;

	private:
		LinkService *m_link;
	};
}

#endif /* SRC_MOTION_LINKSCHEDULEMOVESINK_H_ */
