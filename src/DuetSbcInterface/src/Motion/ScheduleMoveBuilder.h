/*
 * ScheduleMoveBuilder.h
 *
 * Turns a prepared move into SbcRequest::ScheduleMove packets for the controller.
 *
 * This is what stands in for RepRapFirmware's CanMotion on the SBC. In the firmware, DDA::Prepare
 * calls CanMotion::AddAxisMovement once per driver and CanMotion builds one CanMessageMovementLinearShaped
 * per board. Here the SBC is not on the CAN bus at all: it accumulates the same per-driver
 * information, puts it on the SPI link as one ScheduleMove packet, and the controller's CanMotion
 * does the grouping by board and the CAN send, which is code that already exists and works.
 *
 * The interface deliberately mirrors CanMotion's: StartMovement, AddAxisMovement, AddExtruderMovement,
 * FinishMovement, CanPrepareMove. In step 9 a thin `namespace CanMotion` shim forwards to it, so the
 * imported DDA::Prepare needs no edits at its call sites.
 *
 * Where the packets go is a sink the caller supplies. The real one queues onto the outbound ring
 * that the SPI transfer loop drains; the tests supply one that records, which is how the whole
 * motion engine can be exercised with no hardware and no transfer loop.
 */

#ifndef SRC_MOTION_SCHEDULEMOVEBUILDER_H_
#define SRC_MOTION_SCHEDULEMOVEBUILDER_H_

#include <DuetSpiProtocol/MessageFormats.h>
#include <Motion/MoveProfile.h>

namespace Duet::Sbc::Motion
{
	// Where finished packets go. Implementations must not block: FinishMovement runs on the motion
	// thread, and everything downstream of it is a lock-free queue for that reason.
	class ScheduleMoveSink
	{
	public:
		ScheduleMoveSink() noexcept = default;
		ScheduleMoveSink(const ScheduleMoveSink&) = delete;
		ScheduleMoveSink& operator=(const ScheduleMoveSink&) = delete;
		ScheduleMoveSink(ScheduleMoveSink&&) = delete;
		ScheduleMoveSink& operator=(ScheduleMoveSink&&) = delete;
		virtual ~ScheduleMoveSink() = default;

		// Take one packet: a ScheduleMoveHeader followed by header.numDrivers ScheduleMoveDriver
		// records, contiguous. The bytes are only valid for the duration of the call. Returns false
		// if the packet could not be taken, which is a dropped move and must be reported.
		virtual bool Send(const void *packet, size_t length) noexcept = 0;

		// False when the sink is too full to take another move. Preparation stops rather than
		// overruns: a move that is dropped after the previous one was scheduled leaves a gap that
		// the boards would execute as a stop and restart.
		[[nodiscard]] virtual bool CanAccept() const noexcept = 0;
	};

	class ScheduleMoveBuilder
	{
	public:
		void SetSink(ScheduleMoveSink *sinkToUse) noexcept { m_sink = sinkToUse; }

		// Begin a move. Discards anything left over from a move that was abandoned part way through.
		void StartMovement() noexcept;

		// Add one axis driver's share of the move, in net microsteps.
		void AddAxisMovement(const MoveProfile& profile, DriverId driver, int32_t steps) noexcept;

		// Add one extruder driver's share, in microsteps including fractional parts. The board adds
		// pressure advance and carries the fraction forward, which is why this is not rounded here.
		void AddExtruderMovement(const MoveProfile& profile, DriverId driver, float extrusion,
								 bool usePressureAdvance) noexcept;

		// Emit the move and return how many step clocks it will take the boards to run it, or 0 if
		// nothing was sent. The caller compares that against its own figure and extends its move if
		// the boards' is longer, so that they never have to catch up.
		uint32_t FinishMovement(uint32_t moveId, uint32_t moveStartTime, bool simulating,
								bool checkEndstops, bool useInputShaping) noexcept;

		// False when the sink is backed up, so that PrepareMoves throttles instead of dropping.
		[[nodiscard]] bool CanPrepareMove() const noexcept;

		// Packets the sink refused. Non-zero means motion was lost and the machine must be stopped.
		[[nodiscard]] uint32_t GetDroppedPackets() const noexcept { return m_droppedPackets; }

	private:
		// Append a driver record and take the move's profile from this call.
		duet::spi::protocol::ScheduleMoveDriver& NewDriver(const MoveProfile& profileToUse, DriverId driver) noexcept;

		// Send drivers [first, first + count) as one packet.
		bool SendPacket(uint32_t moveId, uint32_t moveStartTime, uint8_t flags,
						size_t first, size_t count) noexcept;

		ScheduleMoveSink *m_sink = nullptr;

		// The profile of the move being built. Shared by every driver in it, so the packet carries
		// it once rather than per driver
		MoveProfile m_profile;
		bool m_usePressureAdvance = false;

		// Every driver of every axis, plus one per extruder: the most Prepare can possibly add,
		// because it visits each logical drive once and each axis has at most maxDriversPerAxis
		// motors. Sized from that bound rather than from a guess so that overflow is impossible
		// rather than merely unlikely, which is what lets Add() have no failure path.
		static constexpr size_t maxDriversPerMove = (maxAxes * maxDriversPerAxis) + maxExtruders;

		duet::spi::protocol::ScheduleMoveDriver m_drivers[maxDriversPerMove]{};
		size_t m_numDrivers = 0;

		uint32_t m_droppedPackets = 0;
	};
}

#endif /* SRC_MOTION_SCHEDULEMOVEBUILDER_H_ */
