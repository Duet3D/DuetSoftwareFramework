/*
 * ExpansionManager.h
 *
 *  Created on: 4 Feb 2020
 *      Author: David
 */

#ifndef SRC_CAN_EXPANSIONMANAGER_H_
#define SRC_CAN_EXPANSIONMANAGER_H_

#include <RepRapFirmware.h>

#if SUPPORT_CAN_EXPANSION

#  include <CanId.h>
#  include <CanMessageBuffer.h>
#  include <General/NamedEnum.h>
#  include <RTOSIface/RTOSIface.h>

#  include <Platform/UniqueId.h>

NamedEnum(BoardState, uint8_t, unknown, flashing, flashFailed, resetting, running, timedOut);

struct ExpansionBoardData
{
	ExpansionBoardData() noexcept;

	const char* _ecv_array _ecv_null typeName;
	volatile uint32_t whenLastStatusReportReceived;
	UniqueId uniqueId;
	BoardState state;
};

class ExpansionManager
{
  public:
	ExpansionManager() noexcept;

	unsigned int GetNumExpansionBoards() const noexcept { return numExpansionBoards; }
	const ExpansionBoardData* _ecv_null GetBoardDetails(uint8_t address) const noexcept;

	void ProcessAnnouncement(CanMessageBuffer& buf, bool isNewFormat) noexcept;
	void ProcessBoardStatusReport(const CanMessageBuffer& buf) noexcept;

	// Firmware update and related functions
	GCodeResult ResetRemote(uint32_t boardAddress, const StringRef& reply) THROWS(GCodeException);
	GCodeResult UpdateRemoteFirmware(uint32_t boardAddress, const StringRef& reply, const uint16_t moduleNumber = 0)
		THROWS(GCodeException);

	void UpdateFinished(CanAddress address) noexcept;
	void UpdateFailed(CanAddress address) noexcept;
	bool IsFlashing() const noexcept { return numBoardsFlashing != 0; }

	void Spin() noexcept;

	void EmergencyStop() noexcept;

  private:
	static constexpr uint32_t StatusMessageTimeoutMillis =
		5000; // if we don't receive a board status message for this long we presume that communication has been lost

	const ExpansionBoardData& FindIndexedBoard(unsigned int index) const noexcept;
	void UpdateBoardState(CanAddress address, const BoardState& newState) noexcept;

	static ReadWriteLock boardsLock;

	unsigned int numExpansionBoards;
	unsigned int numBoardsFlashing;
	mutable volatile unsigned int lastIndexSearched; // the last board index we searched for, or 0 if invalid
	mutable volatile unsigned int
		lastAddressFound; // if lastIndexSearched is nonzero, this is the corresponding board address we found
	ExpansionBoardData boards[CanId::MaxCanAddress + 1]; // the first entry is a dummy one
};

#endif // SUPPORT_CAN_EXPANSION

#endif /* SRC_CAN_EXPANSIONMANAGER_H_ */
