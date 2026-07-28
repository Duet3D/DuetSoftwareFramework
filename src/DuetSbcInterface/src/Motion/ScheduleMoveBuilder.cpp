/*
 * ScheduleMoveBuilder.cpp - see ScheduleMoveBuilder.h.
 */

#include "ScheduleMoveBuilder.h"

#include <Platform/Platform.h>

#include <cstring>

using Duet::Sbc::Motion::ScheduleMoveBuilder;
using duet::spi::protocol::MaxScheduleMoveDrivers;
using duet::spi::protocol::ScheduleMoveDriver;
using duet::spi::protocol::ScheduleMoveHeader;
namespace ScheduleMoveFlags = duet::spi::protocol::ScheduleMoveFlags;

void ScheduleMoveBuilder::StartMovement() noexcept
{
	// Anything still here belongs to a move that was abandoned between Start and Finish - a
	// simulated move, or one that turned out to move nothing. Dropping it is the whole of the
	// cleanup, because nothing has been sent yet.
	numDrivers = 0;
	usePressureAdvance = false;
}

// Start a driver record, and take the profile while we are here. Every driver of a move shares one
// velocity profile - that is what a velocity profile is - so which call it is taken from does not
// matter, and the packet carries it once rather than per driver.
ScheduleMoveDriver& ScheduleMoveBuilder::NewDriver(const MoveProfile& profileToUse, DriverId driver) noexcept
{
	profile = profileToUse;
	ScheduleMoveDriver& d = drivers[min<size_t>(numDrivers, MaxDriversPerMove - 1)];
	if (numDrivers < MaxDriversPerMove)
	{
		++numDrivers;				// the bound on MaxDriversPerMove says this is always taken
	}
	d.boardAddress = driver.boardAddress;
	d.driverNumber = driver.localDriver;
	d.padding = 0;
	return d;
}

void ScheduleMoveBuilder::AddAxisMovement(const MoveProfile& profileToUse, DriverId driver, int32_t steps) noexcept
{
	ScheduleMoveDriver& d = NewDriver(profileToUse, driver);
	d.isExtruder = 0;
	d.steps = steps;
	d.extrusion = 0.0F;
}

void ScheduleMoveBuilder::AddExtruderMovement(const MoveProfile& profileToUse, DriverId driver, float extrusion,
											  bool usePressureAdvanceForThisDrive) noexcept
{
	ScheduleMoveDriver& d = NewDriver(profileToUse, driver);
	d.isExtruder = 1;
	d.steps = 0;
	d.extrusion = extrusion;

	// Pressure advance is a property of the CAN message rather than of one driver within it, which
	// is how the firmware carries it too. In practice every extruder in a move agrees.
	usePressureAdvance = usePressureAdvance || usePressureAdvanceForThisDrive;
}

bool ScheduleMoveBuilder::SendPacket(uint32_t moveId, uint32_t moveStartTime, uint8_t flags,
									 size_t first, size_t count) noexcept
{
	// One contiguous block: the header, then the drivers this packet carries. Built on the stack
	// because the sink copies it out - nothing downstream keeps a pointer into here.
	alignas(uint32_t) char packet[sizeof(ScheduleMoveHeader) + (MaxScheduleMoveDrivers * sizeof(ScheduleMoveDriver))];

	auto *const header = reinterpret_cast<ScheduleMoveHeader *>(packet);
	header->whenToExecute = moveStartTime;
	header->accelClocks = profile.accelClocks;
	header->steadyClocks = profile.steadyClocks;
	header->decelClocks = profile.decelClocks;
	header->acceleration = (float)profile.acceleration;
	header->deceleration = (float)profile.deceleration;
	header->totalDistance = (float)profile.totalDistance;
	header->accelDistance = (float)profile.accelDistance;
	header->decelStartDistance = (float)profile.decelStartDistance;
	header->startSpeed = (float)profile.startSpeed;
	header->topSpeed = (float)profile.topSpeed;
	header->endSpeed = (float)profile.endSpeed;
	header->moveId = moveId;
	header->numDrivers = (uint8_t)count;
	header->flags = flags;
	header->padding = 0;

	const size_t driversBytes = count * sizeof(ScheduleMoveDriver);
	memcpy(packet + sizeof(ScheduleMoveHeader), &drivers[first], driversBytes);

	return sink != nullptr && sink->Send(packet, sizeof(ScheduleMoveHeader) + driversBytes);
}

uint32_t ScheduleMoveBuilder::FinishMovement(uint32_t moveId, uint32_t moveStartTime, bool simulating,
											 bool checkEndstops, bool useInputShaping) noexcept
{
	const size_t total = numDrivers;
	numDrivers = 0;

	if (total == 0 || simulating)
	{
		// Nothing moves on any board, or we are only working out how long the move would take.
		return 0;
	}

	uint8_t commonFlags = 0;
	if (useInputShaping) { commonFlags |= ScheduleMoveFlags::UseInputShaping; }
	if (usePressureAdvance) { commonFlags |= ScheduleMoveFlags::UsePressureAdvance; }
	if (checkEndstops) { commonFlags |= ScheduleMoveFlags::CheckEndstops; }

	for (size_t first = 0; first < total; first += MaxScheduleMoveDrivers)
	{
		const size_t count = min<size_t>(total - first, MaxScheduleMoveDrivers);
		const bool isLast = (first + count == total);
		const uint8_t flags = (isLast) ? (uint8_t)(commonFlags | ScheduleMoveFlags::LastPacket) : commonFlags;
		if (!SendPacket(moveId, moveStartTime, flags, first, count))
		{
			// The move is now lost: the controller may hold the packets that did get through, and it
			// discards those when it next sees a different moveId. Do not send the rest - a partial
			// move reaching the boards would move some drives and not others.
			++droppedPackets;
			Platform::MessageF(ErrorMessage, "move %" PRIu32 " dropped: link busy\n", moveId);
			return 0;
		}
	}

	return profile.TotalClocks();
}

bool ScheduleMoveBuilder::CanPrepareMove() const noexcept
{
	return sink != nullptr && sink->CanAccept();
}
