/*
 * ScheduleMoveBuilder.cpp - see ScheduleMoveBuilder.h.
 */

#include "ScheduleMoveBuilder.h"

#include "MoveParams.h"

#include <Platform/Log.h>

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
	m_numDrivers = 0;
	m_usePressureAdvance = false;
}

// Start a driver record, and take the profile while we are here. Every driver of a move shares one
// velocity profile - that is what a velocity profile is - so which call it is taken from does not
// matter, and the packet carries it once rather than per driver.
ScheduleMoveDriver& ScheduleMoveBuilder::NewDriver(const MoveProfile& profileToUse, DriverId driver) noexcept
{
	m_profile = profileToUse;
	ScheduleMoveDriver& d = m_drivers[min<size_t>(m_numDrivers, maxDriversPerMove - 1)];
	if (m_numDrivers < maxDriversPerMove)
	{
		++m_numDrivers; // the bound on maxDriversPerMove says this is always taken
	}
	d.boardAddress = driver.boardAddress;
	d.driverNumber = driver.localDriver;
	d.stopOnBoard = duet::spi::protocol::NoEndstopBoard;
	d.stopOnHandle = 0;
	d.stopGroup = duet::spi::protocol::kNoStopGroup;
	d.stopAction = duet::spi::protocol::StopAction::none;
	return d;
}

void ScheduleMoveBuilder::AddAxisMovement(const MoveProfile& profileToUse,
										  DriverId driver,
										  int32_t steps,
										  uint32_t stopOnInput,
										  uint8_t stopGroup,
										  duet::spi::protocol::StopAction stopAction) noexcept
{
	ScheduleMoveDriver& d = NewDriver(profileToUse, driver);
	d.isExtruder = 0;
	d.steps = steps;
	d.extrusion = 0.0F;

	// The controller watches for this input and stops this driver itself. Only an endstop move
	// carries one; every other move leaves the sentinel NewDriver already wrote
	if (stopOnInput != Duet::Sbc::Motion::kNoStopInput)
	{
		d.stopOnBoard = Duet::Sbc::Motion::StopInputBoard(stopOnInput);
		d.stopOnHandle = Duet::Sbc::Motion::StopInputHandle(stopOnInput);
		d.stopGroup = stopGroup;
		d.stopAction = stopAction;
	}
}

void ScheduleMoveBuilder::AddExtruderMovement(const MoveProfile& profileToUse,
											  DriverId driver,
											  float extrusion,
											  bool usePressureAdvanceForThisDrive) noexcept
{
	ScheduleMoveDriver& d = NewDriver(profileToUse, driver);
	d.isExtruder = 1;
	d.steps = 0;
	d.extrusion = extrusion;

	// Pressure advance is a property of the CAN message rather than of one driver within it, which
	// is how the firmware carries it too. In practice every extruder in a move agrees.
	m_usePressureAdvance = m_usePressureAdvance || usePressureAdvanceForThisDrive;
}

bool ScheduleMoveBuilder::SendPacket(
	uint32_t moveId, uint32_t moveStartTime, uint8_t flags, size_t first, size_t count) noexcept
{
	// One contiguous block: the header, then the drivers this packet carries. Built on the stack
	// because the sink copies it out - nothing downstream keeps a pointer into here.
	alignas(uint32_t) char packet[sizeof(ScheduleMoveHeader) + (MaxScheduleMoveDrivers * sizeof(ScheduleMoveDriver))];

	auto* const header = reinterpret_cast<ScheduleMoveHeader*>(packet);
	header->whenToExecute = moveStartTime;
	header->accelClocks = m_profile.accelClocks;
	header->steadyClocks = m_profile.steadyClocks;
	header->decelClocks = m_profile.decelClocks;
	header->acceleration = (float)m_profile.acceleration;
	header->deceleration = (float)m_profile.deceleration;
	header->totalDistance = (float)m_profile.totalDistance;
	header->accelDistance = (float)m_profile.accelDistance;
	header->decelStartDistance = (float)m_profile.decelStartDistance;
	header->startSpeed = (float)m_profile.startSpeed;
	header->topSpeed = (float)m_profile.topSpeed;
	header->endSpeed = (float)m_profile.endSpeed;
	header->moveId = moveId;
	header->numDrivers = (uint8_t)count;
	header->flags = flags;
	header->padding = 0;

	const size_t driversBytes = count * sizeof(ScheduleMoveDriver);
	memcpy(packet + sizeof(ScheduleMoveHeader), &m_drivers[first], driversBytes);

	return m_sink != nullptr &&
		   m_sink->Send({reinterpret_cast<const uint8_t*>(packet), sizeof(ScheduleMoveHeader) + driversBytes});
}

uint32_t ScheduleMoveBuilder::FinishMovement(
	uint32_t moveId, uint32_t moveStartTime, bool simulating, bool checkEndstops, bool useInputShaping) noexcept
{
	const size_t total = m_numDrivers;
	m_numDrivers = 0;

	if (total == 0 || simulating)
	{
		// Nothing moves on any board, or we are only working out how long the move would take.
		return 0;
	}

	uint8_t commonFlags = 0;
	if (useInputShaping)
	{
		commonFlags |= ScheduleMoveFlags::UseInputShaping;
	}
	if (m_usePressureAdvance)
	{
		commonFlags |= ScheduleMoveFlags::UsePressureAdvance;
	}
	if (checkEndstops)
	{
		commonFlags |= ScheduleMoveFlags::CheckEndstops;
	}

	for (size_t first = 0; first < total; first += MaxScheduleMoveDrivers)
	{
		const auto count = min<size_t>(total - first, MaxScheduleMoveDrivers);
		const bool isLast = (first + count == total);
		const uint8_t flags = (isLast) ? (uint8_t)(commonFlags | ScheduleMoveFlags::LastPacket) : commonFlags;
		if (!SendPacket(moveId, moveStartTime, flags, first, count))
		{
			// The move is now lost: the controller may hold the packets that did get through, and it
			// discards those when it next sees a different moveId. Do not send the rest - a partial
			// move reaching the boards would move some drives and not others.
			++m_droppedPackets;
			LogMessage("move %" PRIu32 " dropped: link busy\n", moveId);
			return 0;
		}
	}

	return m_profile.TotalClocks();
}

bool ScheduleMoveBuilder::CanPrepareMove() const noexcept
{
	return m_sink != nullptr && m_sink->CanAccept();
}
