/*
 * CanInterface.h
 *
 *  Created on: 19 Sep 2018
 *      Author: David
 */

#ifndef SRC_CAN_CANINTERFACE_H_
#define SRC_CAN_CANINTERFACE_H_

#include "RepRapFirmware.h"

#if SUPPORT_CAN_EXPANSION

#  include <CanId.h>
#  include <CanMessageFormats.h>

#  include "CanDriversData.h"
#  include "CanException.h"

class CanMessageBuffer;
class OutputBuffer;
class DDA;
class DriveMovement;
struct PrepParams;

namespace CanInterface
{
	// Note: GetCanAddress() in this namespace is now declared in RepRapFirmware.h to overcome ordering issues
	constexpr uint32_t UsualResponseTimeout = 1000; // how long we normally wait for a response, in milliseconds
	constexpr uint32_t UsualSendTimeout = 200;		// how long we normally wait to send a message, in milliseconds

	// Low level functions
	void Init() noexcept;
	void Shutdown() noexcept;
	inline CanAddress GetCurrentMasterAddress() noexcept
	{
		return CanId::MasterAddress;
	} // currently fixed, but might change in future

	void ReportCanTiming(const StringRef& reply) noexcept;

	CanRequestId AllocateRequestId(CanAddress destination, CanMessageBuffer* buf) noexcept;
	void SendResponseNoFree(CanMessageBuffer& buf) noexcept;
	void SendBroadcastNoFree(CanMessageBuffer& buf) noexcept;
	void SendMessageNoReplyNoFree(CanMessageBuffer& buf) noexcept;
	void Diagnostics(const StringRef& reply) noexcept;
	CanMessageBuffer* AllocateBuffer() THROWS(CanException);
	void CheckCanAddress(uint32_t address) THROWS(CanException);

	uint16_t GetTimeStampCounter() noexcept;

#  if DUAL_CAN
	uint32_t SendPlainMessageNoFree(CanMessageBuffer& buf, uint32_t timeout = UsualSendTimeout) noexcept;
	bool ReceivePlainMessage(CanMessageBuffer* null buf, uint32_t timeout = UsualResponseTimeout) noexcept;
#  endif

#  if !SAME70
	uint16_t GetTimeStampPeriod() noexcept; // return the period of the time stamp counter in units of 48MHz CAN clocks
#  endif

	// Info functions
	GCodeResult GetRemoteFirmwareDetails(uint32_t boardAddress, const StringRef& reply) THROWS(CanException);
	GCodeResult RemoteDiagnostics(MessageType mt, uint32_t boardAddress, unsigned int type, const StringRef& reply)
		THROWS(CanException);
	GCodeResult HandleM111(uint32_t boardAddress, const StringRef& reply) THROWS(CanException);

	// SBC bridging: in-flight SBC-originated CAN request, so that a response can be matched back to the SBC's txToken
	// and multi-fragment replies reassembled. Written by the SBC task, read/cleared by the CAN receiver tasks.
	struct CanRequestMapping
	{
		bool active;
		CanAddress board;		   // the expansion board we sent to and expect the reply from
		CanRequestId rid;		   // the request ID we allocated
		uint16_t txToken;		   // the SBC's token to return in the response
		CanMessageType replyType;  // the CanMessageType the SBC expects (CanMessageType::unusedMessageType means none)
		uint32_t whenStarted;	   // millis() when the request was sent, used for silent expiry
		uint8_t fragmentsReceived; // number of reply fragments collated so far
	};

	// Send a CAN request that originated from the SBC. 'buf' has already been populated by the SBC interface.
	// 'txToken' is the SBC's token to return in any response; 'replyType' is the reply the SBC expects (0xFFFF means
	// none).
	void SendCanRequest(CanMessageBuffer& buf, uint16_t txToken, CanMessageType replyType) noexcept;
	CanRequestMapping* _ecv_null FindPendingRequest(
		CanAddress src, CanRequestId rid) noexcept; // Find an in-flight request matching a received response
	void ReleasePendingRequest(
		CanRequestMapping* mapping) noexcept; // Free a pending request slot and any reassembly buffer

	// Motor control functions
	void SendMotion(CanMessageBuffer* buf) noexcept;

#  if 0 // not currently used
	unsigned int GetNumPendingMotionMessages() noexcept;
#  endif
	void WakeAsyncSender() noexcept;
	void WakeAsyncSenderFromIsr() noexcept;

	// Misc functions
	GCodeResult ChangeAddressAndNormalTiming(const StringRef& reply) THROWS(CanException);
	void EnableCan(
		bool enable) noexcept; // enable or disable the CAN interface (enabling starts the time sync broadcasts)
	void ConfigLocalCanTiming(const CanTiming& timing,
							  bool doSetTiming,
							  const StringRef& reply) noexcept; // configure our own CAN timing, or report the current
																// timing into 'reply' if doSetTiming is false

#  if SUPPORT_MULTICAST_DISCOVERY
	void SetStatusLedIdentify(uint32_t seconds) noexcept;
	void SetStatusLedNormal() noexcept;
#  endif

#  if DUAL_CAN
	namespace ODrive
	{
		CanId ArbitrationId(DriverId driver, uint8_t cmd) noexcept;
		CanMessageBuffer* _ecv_null PrepareSimpleMessage(const DriverId driver, const StringRef& reply) noexcept;
		void FlushCanReceiveHardware() noexcept;
		bool GetExpectedSimpleMessage(CanMessageBuffer* buf,
									  const DriverId driver,
									  const uint8_t cmd,
									  const StringRef& reply) noexcept;
	} // namespace ODrive
#  endif
} // namespace CanInterface

#endif

#endif /* SRC_CAN_CANINTERFACE_H_ */
