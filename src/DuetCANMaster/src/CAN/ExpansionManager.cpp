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


#  include <Movement/StepTimer.h>

#  include <CAN/CanException.h>
#  include <CAN/CommandProcessor.h>

ReadWriteLock ExpansionManager::boardsLock;

ExpansionBoardData::ExpansionBoardData() noexcept
	: typeName(nullptr)
	, whenLastStatusReportReceived(0)
	, whenBoardStarted(0)
	, state(BoardState::Unknown)
	, numDrivers(0)
	, announcedV1(false)
	, usesUf2Binary(false)
{
}

ExpansionManager::ExpansionManager() noexcept
	: m_numExpansionBoards(0)
	, m_numBoardsFlashing(0)
	, m_lastIndexSearched(0)
	, m_lastAddressFound(0)
	, m_replayNextAddress(NoReplayPending)
{
	// The boards table array is initialised by its constructor. Note, boards[0] is not used.
}

// Update the state of a board. Caller should have a write lock on boardsLock before calling this.
void ExpansionManager::UpdateBoardState(CanAddress address, const BoardState& newState) noexcept
{
	ExpansionBoardData& board = m_boards[address];
	const TaskCriticalSectionLocker lock;

	const BoardState oldState = board.state;
	if (newState != oldState)
	{
		board.state = newState;
		if (oldState == BoardState::Unknown)
		{
			++m_numExpansionBoards;
			m_lastIndexSearched = 0;
			m_lastAddressFound = 0;
		}
		else if (oldState == BoardState::Flashing && m_numBoardsFlashing != 0)
		{
			--m_numBoardsFlashing;
		}

		if (newState == BoardState::Flashing)
		{
			++m_numBoardsFlashing;
		}
		else if (newState == BoardState::Unknown && m_numExpansionBoards != 0)
		{
			--m_numExpansionBoards;
			m_lastIndexSearched = 0;
			m_lastAddressFound = 0;
		}
	}
}

// Process an announcement from an expansion board. Don't free the message buffer that it arrived in
void ExpansionManager::ProcessAnnouncement(CanMessageBuffer& buf, bool isNewFormat) noexcept
{
	const CanAddress src = buf.id.Src();
	if (src <= CanId::MaxCanAddress)
	{
		ExpansionBoardData& board = m_boards[src];
		{
			const WriteLocker lock(boardsLock);

			board.whenLastStatusReportReceived = millis();
			board.announcedV1 = isNewFormat;
			String<StringLength100> boardTypeAndFirmwareVersion;
			if (isNewFormat)
			{
				boardTypeAndFirmwareVersion.copy(buf.msg.announceV1.boardTypeAndFirmwareVersion,
												 CanMessageAnnounceV1::GetMaxTextLength(buf.dataLength));
				board.numDrivers = buf.msg.announceV1.numDrivers;
				board.usesUf2Binary = buf.msg.announceV1.usesUf2Binary;
				// The boards are synchronised to our clock, so rebasing the age it reported onto ours
				// keeps it correct however long ago this was
				board.whenBoardStarted = millis() - buf.msg.announceV1.timeSinceStarted;
			}
			else
			{
				boardTypeAndFirmwareVersion.copy(buf.msg.announceV0.boardTypeAndFirmwareVersion,
												 CanMessageAnnounceV0::GetMaxTextLength(buf.dataLength));
				board.numDrivers = buf.msg.announceV0.numDrivers;
				board.whenBoardStarted = millis() - buf.msg.announceV0.timeSinceStarted;
			}
			UpdateBoardState(src, BoardState::Unknown);
			if (board.typeName == nullptr || strcmp(board.typeName, boardTypeAndFirmwareVersion.c_str()) != 0)
			{
				// To save memory, see if we already have another board with the same type name
				const char* _ecv_array _ecv_null newTypeName = nullptr;
				for (const ExpansionBoardData& data : m_boards)
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
			UpdateBoardState(src, BoardState::Running);
		}

		// Tell the sending board that we don't need any more announcements from it. This overwrites the
		// announcement in buf, so the caller must be done with it - see CommandProcessor::ProcessReceivedMessage
		buf.SetupRequestMessageNoRid<CanMessageAcknowledgeAnnounce>(CanInterface::GetCanAddress(), src);
		CanInterface::SendMessageNoReplyNoFree(buf);
	}
}

// Process a board status report
void ExpansionManager::ProcessBoardStatusReport(const CanMessageBuffer& buf) noexcept
{
	const CanAddress address = buf.id.Src();
	ExpansionBoardData& board = m_boards[address];
	board.whenLastStatusReportReceived = millis();
	if (board.state != BoardState::Running && board.state != BoardState::Flashing)
	{
		const WriteLocker lock(boardsLock);
		UpdateBoardState(address, BoardState::Running);
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

// Arrange for the SBC to be told about every board we have already heard from
//
// A board announces itself until it is acknowledged and then goes quiet, so an announcement made while the SBC was not
// listening is the only one it will ever make. That is the normal case rather than an edge case: the boards and this
// firmware are running within a second of power being applied, and the SBC takes far longer than that to boot. It is
// also what a restart of DuetControlServer leaves behind, because that clears its boards[] and the boards have no
// reason to announce again. Replaying what the announcement left here is what fills those entries in.
void ExpansionManager::BeginReplayToSbc() noexcept
{
	m_replayNextAddress = 0;
}

// Send as much of the replay as the SBC has room for, in the form it decodes announcements in. A machine can carry more
// boards than the response queue holds entries, so this is called until it has nothing left to send.
void ExpansionManager::ContinueReplayToSbc() noexcept
{
	if (m_replayNextAddress == NoReplayPending)
	{
		return;
	}

	const ReadLocker lock(boardsLock);

	while (m_replayNextAddress <= CanId::MaxCanAddress)
	{
		const auto addr = (CanAddress)m_replayNextAddress;
		const ExpansionBoardData& board = m_boards[addr];
		if (board.state != BoardState::Running || board.typeName == nullptr)
		{
			++m_replayNextAddress;
			continue;
		}

		// Sent from the board's own address, so that the SBC applies it to the board it describes
		CanMessageBuffer buf;
		if (board.announcedV1)
		{
			const auto msg = buf.SetupRequestMessageNoRid<CanMessageAnnounceV1>(addr, CanInterface::GetCanAddress());
			msg->timeSinceStarted = millis() - board.whenBoardStarted;
			msg->numDrivers = board.numDrivers;
			msg->usesUf2Binary = board.usesUf2Binary;
			msg->isReconnect = 0;
			msg->wasShutDown = 0;
			memcpy(msg->uniqueId, board.uniqueId.GetRaw(), sizeof(msg->uniqueId));
			SafeStrncpy(msg->boardTypeAndFirmwareVersion, board.typeName, ARRAY_SIZE(msg->boardTypeAndFirmwareVersion));
			buf.dataLength = msg->GetActualDataLength();
		}
		else
		{
			const auto msg = buf.SetupRequestMessageNoRid<CanMessageAnnounceV0>(addr, CanInterface::GetCanAddress());
			msg->timeSinceStarted = millis() - board.whenBoardStarted;
			msg->numDrivers = board.numDrivers;
			SafeStrncpy(msg->boardTypeAndFirmwareVersion, board.typeName, ARRAY_SIZE(msg->boardTypeAndFirmwareVersion));
			buf.dataLength = msg->GetActualDataLength();
		}

		if (!CommandProcessor::ForwardMessageToSbc(buf))
		{
			return;					// no room left; this board goes in the next transfer
		}
		++m_replayNextAddress;
	}

	m_replayNextAddress = NoReplayPending;
}

// Return a pointer to the expansion board, if it is present
const ExpansionBoardData* _ecv_null ExpansionManager::GetBoardDetails(uint8_t address) const noexcept
{
	return (address < ARRAY_SIZE(m_boards) && m_boards[address].state == BoardState::Running) ? &m_boards[address]
																							  : nullptr;
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
	UpdateBoardState(address, BoardState::Resetting);
}

void ExpansionManager::UpdateFailed(CanAddress address) noexcept
{
	const WriteLocker lock(boardsLock);
	UpdateBoardState(address, BoardState::FlashFailed);
}

const ExpansionBoardData& ExpansionManager::FindIndexedBoard(unsigned int index) const noexcept
{
	// The common case is where we are looking for the same board as last time, so check for that first
	if (index == m_lastIndexSearched)
	{
		const unsigned int addr = m_lastAddressFound;
		if (index == m_lastIndexSearched) // check it again in case we got interrupted
		{
			return m_boards[addr];
		}
	}

	// If index 0 or out of range, return the dummy entry for the main board
	if (index == 0 || index > m_numExpansionBoards)
	{
		return m_boards[0];
	}

	const TaskCriticalSectionLocker lock;

	// If we are looking for a board earlier in the table than the last one, restart the search from the beginning
	if (m_lastIndexSearched > index)
	{
		m_lastIndexSearched = 0;
		m_lastAddressFound = 0;
	}

	unsigned int address = m_lastAddressFound;
	unsigned int currentIndex = m_lastIndexSearched;
	while (currentIndex < index)
	{
		++address;
		if (address == ARRAY_SIZE(m_boards))
		{
			return m_boards[0];
		}
		if (m_boards[address].state != BoardState::Unknown)
		{
			++currentIndex;
		}
	}

	m_lastIndexSearched = index;
	m_lastAddressFound = address;
	return m_boards[address];
}

// Nothing to do here. A board that stops reporting is noticed by the SBC, which receives the same
// status reports this forwards and owns both the board state it would set and the event it would
// raise; see ExpansionBoardManager and docs/devel/EVENTS_MIGRATION.md section 3.3.1.
void ExpansionManager::Spin() noexcept
{
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
		if (m_boards[addr].state == BoardState::Running)
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
