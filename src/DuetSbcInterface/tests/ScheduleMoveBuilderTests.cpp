// Tests for ScheduleMoveBuilder: the SBC's replacement for RepRapFirmware's CanMotion.
//
// What matters here is that the packet the controller receives describes the same move the SBC
// planned, and that a move either reaches the boards whole or does not reach them at all. Those are
// the two ways this can go wrong quietly: a field dropped or converted (the boards move the wrong
// distance) and a partially-sent move (some drives move and others do not).

#include <Motion/ScheduleMoveBuilder.h>

#include <TestSupport.h>

#include <vector>

using Duet::Sbc::Motion::MoveProfile;
using Duet::Sbc::Motion::ScheduleMoveBuilder;
using Duet::Sbc::Motion::ScheduleMoveSink;
using duet::spi::protocol::MaxScheduleMoveDrivers;
using duet::spi::protocol::ScheduleMoveDriver;
using duet::spi::protocol::ScheduleMoveHeader;
namespace ScheduleMoveFlags = duet::spi::protocol::ScheduleMoveFlags;

namespace
{
	// One packet, kept as bytes so that the tests read it back the way the controller will: by
	// casting the buffer, not by looking at the builder's internals.
	struct Packet
	{
		std::vector<char> bytes;

		[[nodiscard]] const ScheduleMoveHeader& Header() const noexcept
		{
			return *reinterpret_cast<const ScheduleMoveHeader *>(bytes.data());
		}

		[[nodiscard]] const ScheduleMoveDriver& Driver(size_t index) const noexcept
		{
			const auto *const first =
				reinterpret_cast<const ScheduleMoveDriver *>(bytes.data() + sizeof(ScheduleMoveHeader));
			return first[index];
		}
	};

	class RecordingSink final : public ScheduleMoveSink
	{
	public:
		bool Send(std::span<const uint8_t> packet) noexcept override
		{
			if (refuseFrom >= 0 && (int)packets.size() >= refuseFrom)
			{
				return false;
			}
			const auto *const first = reinterpret_cast<const char *>(packet.data());
			packets.push_back(Packet{std::vector<char>(first, first + packet.size())});
			return true;
		}

		[[nodiscard]] bool CanAccept() const noexcept override { return accepting; }

		std::vector<Packet> packets;
		// Refuse from this packet index onwards; negative to accept everything
		int refuseFrom = -1;
		bool accepting = true;
	};

	// A profile with distinct values in every field, so that a field copied from the wrong place or
	// dropped shows up rather than matching by coincidence.
	MoveProfile SampleProfile() noexcept
	{
		MoveProfile p;
		p.accelClocks = 1000;
		p.steadyClocks = 2000;
		p.decelClocks = 500;
		p.acceleration = 1.5e-7F;
		p.deceleration = -2.5e-7F;
		p.totalDistance = 42.0F;
		p.accelDistance = 7.5F;
		p.decelStartDistance = 33.25F;
		p.startSpeed = 1.0e-4F;
		p.topSpeed = 5.0e-4F;
		p.endSpeed = 2.0e-4F;
		return p;
	}

	constexpr uint8_t board1 = 1;
	constexpr uint8_t board2 = 2;

	void TestProfileReachesTheWire() noexcept
	{
		RecordingSink sink;
		ScheduleMoveBuilder builder;
		builder.SetSink(&sink);

		const MoveProfile profile = SampleProfile();
		builder.StartMovement();
		builder.AddAxisMovement(profile, DriverId(board1, 0), 1234);
		const uint32_t clocks = builder.FinishMovement(7, 0xDEADBEEF, false, false, true);

		CHECK(clocks == 3500, "FinishMovement returns the total clocks of the profile");
		CHECK(sink.packets.size() == 1, "one driver produces one packet");
		if (sink.packets.empty())
		{
			return;
		}

		const ScheduleMoveHeader& h = sink.packets[0].Header();
		CHECK(h.whenToExecute == 0xDEADBEEF, "start time is passed through unchanged");
		CHECK(h.moveId == 7, "move id is passed through unchanged");
		CHECK(h.accelClocks == 1000, "acceleration duration survives");
		CHECK(h.steadyClocks == 2000, "steady duration survives");
		CHECK(h.decelClocks == 500, "deceleration duration survives");
		CHECK_NEAR(h.acceleration, 1.5e-7, 1e-12, "acceleration survives unscaled");
		CHECK_NEAR(h.deceleration, -2.5e-7, 1e-12, "deceleration keeps its negative sign");
		CHECK_NEAR(h.totalDistance, 42.0, 1e-6, "total distance survives");
		CHECK_NEAR(h.accelDistance, 7.5, 1e-6, "acceleration distance survives");
		CHECK_NEAR(h.decelStartDistance, 33.25, 1e-6, "deceleration start distance survives");
		CHECK_NEAR(h.startSpeed, 1.0e-4, 1e-10, "start speed survives");
		CHECK_NEAR(h.topSpeed, 5.0e-4, 1e-10, "top speed survives");
		CHECK_NEAR(h.endSpeed, 2.0e-4, 1e-10, "end speed survives");
		CHECK((h.flags & ScheduleMoveFlags::UseInputShaping) != 0, "input shaping flag is set when asked for");
		CHECK((h.flags & ScheduleMoveFlags::LastPacket) != 0, "the only packet is the last packet");
		CHECK((h.flags & ScheduleMoveFlags::CheckEndstops) == 0, "endstop flag is not set when not asked for");
		CHECK((h.flags & ScheduleMoveFlags::UsePressureAdvance) == 0, "no extruder means no pressure advance");

		CHECK(h.numDrivers == 1, "one driver record");
		const ScheduleMoveDriver& d = sink.packets[0].Driver(0);
		CHECK(d.boardAddress == board1, "the driver keeps the board it was added for");
		CHECK(d.driverNumber == 0, "the driver keeps its number on that board");
		CHECK(d.isExtruder == 0, "an axis driver is not an extruder");
		CHECK(d.steps == 1234, "axis steps survive");
		CHECK_NEAR(d.extrusion, 0.0, 0.0, "the unused field of an axis driver is zero");
	}

	void TestExtruderMovement() noexcept
	{
		RecordingSink sink;
		ScheduleMoveBuilder builder;
		builder.SetSink(&sink);

		const MoveProfile profile = SampleProfile();
		builder.StartMovement();
		builder.AddAxisMovement(profile, DriverId(board1, 0), 100);
		builder.AddExtruderMovement(profile, DriverId(board2, 3), 12.5F, true);
		(void)builder.FinishMovement(1, 0, false, true, false);

		CHECK(sink.packets.size() == 1, "two drivers still fit in one packet");
		if (sink.packets.empty())
		{
			return;
		}

		const ScheduleMoveHeader& h = sink.packets[0].Header();
		CHECK(h.numDrivers == 2, "both drivers are in the packet");
		CHECK((h.flags & ScheduleMoveFlags::UsePressureAdvance) != 0, "an extruder asking for PA sets the flag");
		CHECK((h.flags & ScheduleMoveFlags::CheckEndstops) != 0, "the endstop flag is set when asked for");
		CHECK((h.flags & ScheduleMoveFlags::UseInputShaping) == 0, "input shaping is off when not asked for");

		const ScheduleMoveDriver& e = sink.packets[0].Driver(1);
		CHECK(e.boardAddress == board2, "the extruder keeps the board it was added for");
		CHECK(e.driverNumber == 3, "the extruder keeps its number on that board");
		CHECK(e.isExtruder == 1, "the extruder is marked as one");
		CHECK_NEAR(e.extrusion, 12.5, 1e-6, "the fractional part of the extrusion survives");
		CHECK(e.steps == 0, "the unused field of an extruder is zero");
	}

	void TestNothingToSend() noexcept
	{
		RecordingSink sink;
		ScheduleMoveBuilder builder;
		builder.SetSink(&sink);

		builder.StartMovement();
		CHECK(builder.FinishMovement(1, 0, false, false, false) == 0, "a move with no drivers reports no clocks");
		CHECK(sink.packets.empty(), "a move with no drivers sends nothing");

		// Simulating: the move is planned so that the time it takes is known, but no board runs it.
		const MoveProfile profile = SampleProfile();
		builder.StartMovement();
		builder.AddAxisMovement(profile, DriverId(board1, 0), 500);
		CHECK(builder.FinishMovement(2, 0, true, false, false) == 0, "a simulated move reports no clocks");
		CHECK(sink.packets.empty(), "a simulated move sends nothing");

		// ...and having discarded it, the builder is clean for the next move rather than carrying
		// the simulated move's drivers into it.
		builder.StartMovement();
		builder.AddAxisMovement(profile, DriverId(board1, 1), 600);
		(void)builder.FinishMovement(3, 0, false, false, false);
		CHECK(sink.packets.size() == 1, "the move after a simulated one is sent");
		if (!sink.packets.empty())
		{
			CHECK(sink.packets[0].Header().numDrivers == 1, "the simulated move's drivers were discarded");
		}
	}

	void TestAbandonedMoveIsDiscarded() noexcept
	{
		RecordingSink sink;
		ScheduleMoveBuilder builder;
		builder.SetSink(&sink);

		const MoveProfile profile = SampleProfile();
		builder.StartMovement();
		builder.AddAxisMovement(profile, DriverId(board1, 0), 111);
		// No FinishMovement: Prepare bailed out. The next move must not inherit that driver.
		builder.StartMovement();
		builder.AddAxisMovement(profile, DriverId(board1, 1), 222);
		(void)builder.FinishMovement(4, 0, false, false, false);

		CHECK(sink.packets.size() == 1, "the second move is sent");
		if (sink.packets.empty())
		{
			return;
		}
		CHECK(sink.packets[0].Header().numDrivers == 1, "the abandoned move's driver was discarded");
		CHECK(sink.packets[0].Driver(0).steps == 222, "the surviving driver is the second move's");
	}

	void TestSplitAcrossPackets() noexcept
	{
		RecordingSink sink;
		ScheduleMoveBuilder builder;
		builder.SetSink(&sink);

		// One and a half packets' worth, so that the split is uneven and a fencepost error in the
		// last packet's length shows up.
		const size_t total = MaxScheduleMoveDrivers + (MaxScheduleMoveDrivers / 2);
		const MoveProfile profile = SampleProfile();
		builder.StartMovement();
		for (size_t i = 0; i < total; ++i)
		{
			builder.AddAxisMovement(profile, DriverId((uint8_t)(i / 4), (uint8_t)(i % 4)), (int32_t)i + 1);
		}
		const uint32_t clocks = builder.FinishMovement(9, 12345, false, false, false);

		CHECK(clocks == 3500, "a split move still reports the profile's clocks");
		CHECK(sink.packets.size() == 2, "a move of one and a half packets takes two packets");
		if (sink.packets.size() != 2)
		{
			return;
		}

		CHECK(sink.packets[0].Header().numDrivers == MaxScheduleMoveDrivers, "the first packet is full");
		CHECK(sink.packets[1].Header().numDrivers == MaxScheduleMoveDrivers / 2, "the second holds the remainder");
		CHECK((sink.packets[0].Header().flags & ScheduleMoveFlags::LastPacket) == 0,
			  "the first packet is not marked last");
		CHECK((sink.packets[1].Header().flags & ScheduleMoveFlags::LastPacket) != 0, "the second packet is marked last");
		CHECK(sink.packets[0].Header().moveId == sink.packets[1].Header().moveId,
			  "both packets carry the same move id");
		CHECK(sink.packets[0].Header().whenToExecute == sink.packets[1].Header().whenToExecute,
			  "both packets carry the same start time");

		// Every driver appears exactly once, in order, in the packet it belongs to.
		size_t next = 0;
		for (const Packet& packet : sink.packets)
		{
			for (size_t i = 0; i < packet.Header().numDrivers; ++i, ++next)
			{
				CHECK(packet.Driver(i).steps == (int32_t)next + 1, "drivers appear once each, in order");
			}
		}
		CHECK(next == total, "every driver was sent");

		CHECK(sink.packets[1].bytes.size()
				  == sizeof(ScheduleMoveHeader) + ((MaxScheduleMoveDrivers / 2) * sizeof(ScheduleMoveDriver)),
			  "the last packet is only as long as the drivers it carries");
	}

	void TestRefusedPacketStopsTheMove() noexcept
	{
		RecordingSink sink;
		sink.refuseFrom = 1;					// take the first packet of the move, refuse the second
		ScheduleMoveBuilder builder;
		builder.SetSink(&sink);

		const MoveProfile profile = SampleProfile();
		builder.StartMovement();
		for (size_t i = 0; i < MaxScheduleMoveDrivers * 3; ++i)
		{
			builder.AddAxisMovement(profile, DriverId(1, (uint8_t)(i % 4)), (int32_t)i + 1);
		}
		const uint32_t clocks = builder.FinishMovement(11, 0, false, false, false);

		CHECK(clocks == 0, "a move that could not be sent reports no clocks");
		CHECK(builder.GetDroppedPackets() == 1, "the drop is counted");
		CHECK(sink.packets.size() == 1, "no further packets are sent after one is refused");
	}

	void TestCanPrepareMove() noexcept
	{
		RecordingSink sink;
		ScheduleMoveBuilder builder;

		CHECK(!builder.CanPrepareMove(), "no sink means nothing can be prepared");
		builder.SetSink(&sink);
		CHECK(builder.CanPrepareMove(), "an accepting sink allows preparation");
		sink.accepting = false;
		CHECK(!builder.CanPrepareMove(), "a full sink stops preparation");
	}
}

int main()
{
	TestProfileReachesTheWire();
	TestExtruderMovement();
	TestNothingToSend();
	TestAbandonedMoveIsDiscarded();
	TestSplitAcrossPackets();
	TestRefusedPacketStopsTheMove();
	TestCanPrepareMove();
	return TestSupport::Summarise("ScheduleMoveBuilder");
}
