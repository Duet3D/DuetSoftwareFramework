/*
 * ExpansionManager.cpp
 *
 *  Created on: 4 Feb 2020
 *      Author: David
 */

#include "ExpansionManager.h"

#if SUPPORT_CAN_EXPANSION

#  include <CAN/CanInterface.h>
#  include <Platform/RepRap.h>

#  include <Platform/Platform.h>

#  include <Platform/Event.h>

#  include <Movement/StepTimer.h>

#  include <CAN/CanException.h>

ReadWriteLock ExpansionManager::boardsLock;

ExpansionBoardData::ExpansionBoardData() noexcept
	: typeName(nullptr)
	, whenLastStatusReportReceived(0)
	, state(BoardState::unknown)
{
}

ExpansionManager::ExpansionManager() noexcept
	: numExpansionBoards(0)
	, numBoardsFlashing(0)
	, lastIndexSearched(0)
	, lastAddressFound(0)
{
	// The boards table array is initialised by its constructor. Note, boards[0] is not used.
}

// Update the state of a board. Caller should have a write lock on boardsLock before calling this.
void ExpansionManager::UpdateBoardState(CanAddress address, const BoardState& newState) noexcept
{
	ExpansionBoardData& board = boards[address];
	const TaskCriticalSectionLocker lock;

	const BoardState oldState = board.state;
	if (newState != oldState)
	{
		board.state = newState;
		if (oldState == BoardState::unknown)
		{
			++numExpansionBoards;
			lastIndexSearched = 0;
			lastAddressFound = 0;
		}
		else if (oldState == BoardState::flashing && numBoardsFlashing != 0)
		{
			--numBoardsFlashing;
		}

		if (newState == BoardState::flashing)
		{
			++numBoardsFlashing;
		}
		else if (newState == BoardState::unknown && numExpansionBoards != 0)
		{
			--numExpansionBoards;
			lastIndexSearched = 0;
			lastAddressFound = 0;
		}
	}
}

// Process an announcement from an expansion board. Don't free the message buffer that it arrived in
void ExpansionManager::ProcessAnnouncement(CanMessageBuffer& buf, bool isNewFormat) noexcept
{
	const CanAddress src = buf.id.Src();
	if (src <= CanId::MaxCanAddress)
	{
		ExpansionBoardData& board = boards[src];
		{
			const WriteLocker lock(boardsLock);

			board.whenLastStatusReportReceived = millis();
			if (board.state == BoardState::running)
			{
				Event::AddEvent(EventType::expansion_reconnect, 0, src, 0, "");
			}
			String<StringLength100> boardTypeAndFirmwareVersion;
			if (isNewFormat)
			{
				boardTypeAndFirmwareVersion.copy(buf.msg.announceV1.boardTypeAndFirmwareVersion,
												 CanMessageAnnounceV1::GetMaxTextLength(buf.dataLength));
			}
			else
			{
				boardTypeAndFirmwareVersion.copy(buf.msg.announceV0.boardTypeAndFirmwareVersion,
												 CanMessageAnnounceV0::GetMaxTextLength(buf.dataLength));
			}
			UpdateBoardState(src, BoardState::unknown);
			if (board.typeName == nullptr || strcmp(board.typeName, boardTypeAndFirmwareVersion.c_str()) != 0)
			{
				// To save memory, see if we already have another board with the same type name
				const char* _ecv_array _ecv_null newTypeName = nullptr;
				for (const ExpansionBoardData& data : boards)
				{
					if (data.typeName != nullptr && strcmp(boardTypeAndFirmwareVersion.c_str(), data.typeName) == 0)
					{
						newTypeName = data.typeName;
						break;
					}
				}

				if (newTypeName == nullptr)
				{
					char* const _ecv_array temp = new char[boardTypeAndFirmwareVersion.strlen() + 1];
					strcpy(temp, boardTypeAndFirmwareVersion.c_str());
					newTypeName = temp;
				}

				board.typeName = newTypeName;
				if (isNewFormat)
				{
					board.uniqueId.SetFromRemote(buf.msg.announceV1.uniqueId);
				}
				else
				{
					board.uniqueId.Clear();
				}
			}
			UpdateBoardState(src, BoardState::running);
		}

		// Tell the sending board that we don't need any more announcements from it
		buf.SetupRequestMessageNoRid<CanMessageAcknowledgeAnnounce>(CanInterface::GetCanAddress(), src);
		CanInterface::SendMessageNoReplyNoFree(buf);
	}
}

// Process a board status report
void ExpansionManager::ProcessBoardStatusReport(const CanMessageBuffer& buf) noexcept
{
	const CanAddress address = buf.id.Src();
	ExpansionBoardData& board = boards[address];
	board.whenLastStatusReportReceived = millis();
	if (board.state != BoardState::running && board.state != BoardState::flashing)
	{
		const WriteLocker lock(boardsLock);
		UpdateBoardState(address, BoardState::running);
	}

	if (buf.id.MsgType() == CanMessageType::boardStatusReportV1)
	{
		const CanMessageBoardStatusV1& msg = buf.msg.boardStatusV1;
		if (msg.hasMovementDelay)
		{
			StepTimer::ProcessMovementDelayRequest(msg.movementDelay);
		}
	}
	else
	{
		// Must be CanMessageType::boardStatusReportV0
		const CanMessageBoardStatusV0& msg = buf.msg.boardStatusV0;
		if (msg.hasMovementDelay)
		{
			StepTimer::ProcessMovementDelayRequest(msg.movementDelay);
		}
	}
}

// Return a pointer to the expansion board, if it is present
const ExpansionBoardData* _ecv_null ExpansionManager::GetBoardDetails(uint8_t address) const noexcept
{
	return (address < ARRAY_SIZE(boards) && boards[address].state == BoardState::running) ? &boards[address] : nullptr;
}

// Tell an expansion board to update
GCodeResult ExpansionManager::UpdateRemoteFirmware(uint32_t boardAddress,
												   const StringRef& reply,
												   const uint16_t moduleNumber) THROWS(GCodeException)
{
	CanInterface::CheckCanAddress(boardAddress);

	if (moduleNumber != (unsigned int)FirmwareModule::main && moduleNumber != (unsigned int)FirmwareModule::bootloader)
	{
		reply.printf("Unknown module number %u", moduleNumber);
		return GCodeResult::error;
	}

	// Updating remote firmware requires synchronous CAN request/reply transactions, which this firmware no longer
	// performs. In SBC bridge mode the SBC drives expansion board firmware updates itself.
	reply.copy("remote firmware updates are handled by the SBC, not the firmware");
	return GCodeResult::error;
}

void ExpansionManager::UpdateFinished(CanAddress address) noexcept
{
	const WriteLocker lock(boardsLock);
	UpdateBoardState(address, BoardState::resetting);
}

void ExpansionManager::UpdateFailed(CanAddress address) noexcept
{
	const WriteLocker lock(boardsLock);
	UpdateBoardState(address, BoardState::flashFailed);
}

const ExpansionBoardData& ExpansionManager::FindIndexedBoard(unsigned int index) const noexcept
{
	// The common case is where we are looking for the same board as last time, so check for that first
	if (index == lastIndexSearched)
	{
		const unsigned int addr = lastAddressFound;
		if (index == lastIndexSearched) // check it again in case we got interrupted
		{
			return boards[addr];
		}
	}

	// If index 0 or out of range, return the dummy entry for the main board
	if (index == 0 || index > numExpansionBoards)
	{
		return boards[0];
	}

	const TaskCriticalSectionLocker lock;

	// If we are looking for a board earlier in the table than the last one, restart the search from the beginning
	if (lastIndexSearched > index)
	{
		lastIndexSearched = 0;
		lastAddressFound = 0;
	}

	unsigned int address = lastAddressFound;
	unsigned int currentIndex = lastIndexSearched;
	while (currentIndex < index)
	{
		++address;
		if (address == ARRAY_SIZE(boards))
		{
			return boards[0];
		}
		if (boards[address].state != BoardState::unknown)
		{
			++currentIndex;
		}
	}

	lastIndexSearched = index;
	lastAddressFound = address;
	return boards[address];
}

// Check whether we have lost contact with any expansion boards
void ExpansionManager::Spin() noexcept
{
	for (CanAddress addr = 1; addr <= CanId::MaxCanAddress; ++addr)
	{
		const ExpansionBoardData& board = boards[addr];
		if (board.state == BoardState::running)
		{
			// We can get interrupted here by the CanReceive task, which may update
			// 'board.whenLastStatusReportReceived'. So read and save that value before we call millis().
			const uint32_t lastTimeReceived =
				board.whenLastStatusReportReceived; // capture volatile variable before we call millis()
			if (millis() - lastTimeReceived > StatusMessageTimeoutMillis)
			{
				{
					const WriteLocker lock(boardsLock);
					UpdateBoardState(addr, BoardState::timedOut);
				}
				Event::AddEvent(EventType::expansion_timeout, 0, addr, 0, "");
			}
		}
	}
}

void ExpansionManager::EmergencyStop() noexcept
{
	CanMessageBuffer buf;

	// Send a broadcast message for fastest possible delivery to all boards
	buf.SetupBroadcastMessage<CanMessageEmergencyStop>(CanInterface::GetCanAddress());
	CanInterface::SendBroadcastNoFree(buf);

	// Send an individual message to each known expansion board to ensure that they all acknowledged
	for (CanAddress addr = 1; addr <= CanId::MaxCanAddress; ++addr)
	{
		if (boards[addr].state == BoardState::running)
		{
			buf.SetupRequestMessageNoRid<CanMessageEmergencyStop>(CanInterface::GetCanAddress(), addr);
			CanInterface::SendMessageNoReplyNoFree(buf);
		}
	}

	delay(10); // allow time for the broadcast to be sent
	CanInterface::Shutdown();
}

#endif

// End
