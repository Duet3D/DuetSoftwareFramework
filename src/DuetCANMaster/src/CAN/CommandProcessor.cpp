/*
 * CommandProcessor.cpp
 *
 *  Created on: 12 Aug 2019
 *      Author: David
 */

#include "CommandProcessor.h"

#if SUPPORT_CAN_EXPANSION

#  include "CanInterface.h"
#  include "CanMotion.h"
#  include "ExpansionManager.h"
#  include <CanMessageBuffer.h>
#  include <Platform/OutputMemory.h>
#  include <Platform/Platform.h>
#  include <Platform/RepRap.h>
#  include <SBC/SbcInterface.h>

static unsigned int duplicateMotionMessages = 0;
static unsigned int oosMessages1Ahead = 0, oosMessages2Ahead = 0, oosMessages2Behind = 0, oosMessagesOther = 0;

// Append diagnostics relating to bad motion messages
void CommandProcessor::AppendBadMotionStats(const StringRef& reply) noexcept
{
	reply.lcatf("Motion dup %u, oos %u/%u/%u/%u",
				duplicateMotionMessages,
				oosMessages1Ahead,
				oosMessages2Ahead,
				oosMessages2Behind,
				oosMessagesOther);
	duplicateMotionMessages = oosMessages1Ahead = oosMessages2Ahead = oosMessages2Behind = oosMessagesOther = 0;
}

// Handle a firmware update request
// 'buf' holds the request
// 'buf->useBrs' is true if the request used bit rate switching, in which case we use it for the response too
static void HandleFirmwareBlockRequest(CanMessageBuffer& buf) noexcept
	pre(buf.id.MsgType() == CanMessageType::firmwareBlockRequest)
{
#  if 0
	const CanMessageFirmwareUpdateRequest& msg = buf.msg.firmwareUpdateRequest;
	const CanAddress src = buf.id.Src();
	if (   msg.bootloaderVersion == CanMessageFirmwareUpdateRequest::BootloaderVersion0
		&& (msg.fileWanted == (unsigned int)FirmwareModule::main || msg.fileWanted == (unsigned int)FirmwareModule::bootloader)
	   )																	// we only understand bootloader version 0 and files requests for main firmware and bootloader
	{
		String<MaxFilenameLength> fname;
		fname.copy((msg.fileWanted == (unsigned int)FirmwareModule::bootloader) ? "Duet3Bootloader-" : "Duet3Firmware_");
		fname.catn(msg.boardType, msg.GetBoardTypeLength(buf.dataLength));
		fname.cat((msg.uf2Format) ? ".uf2" : ".bin");

		uint32_t fileOffset = msg.fileOffset, fileLength = 0;
		uint32_t lreq = msg.lengthRequested;

#	if HAS_MASS_STORAGE || HAS_SBC_INTERFACE
		// Fetch the firmware file from the local SD card or SBC
		FileStore *_ecv_null const f = reprap.GetPlatform().OpenFile(FIRMWARE_DIRECTORY, fname.c_str(), OpenMode::read);
		if (f != nullptr)
		{
			fileLength = f->Length();
			if (fileOffset >= fileLength)
			{
				CanMessageFirmwareUpdateResponse * const msgp = buf.SetupResponseMessageNoRid<CanMessageFirmwareUpdateResponse>(CanInterface::GetCurrentMasterAddress(), src);
				msgp->dataLength = 0;
				msgp->err = CanMessageFirmwareUpdateResponse::ErrBadOffset;
				msgp->fileLength = fileLength;
				msgp->fileOffset = 0;
				buf.dataLength = msgp->GetActualDataLength();
				CanInterface::SendResponseNoFree(buf);

				reprap.GetPlatform().MessageF(ErrorMessage, "Received firmware update request with bad file offset, actual %" PRIu32 " max %" PRIu32 "\n", fileOffset, fileLength);
			}
			else
			{
				f->Seek(fileOffset);
				if (fileLength - fileOffset < lreq)
				{
					lreq = fileLength - fileOffset;
				}

//debugPrintf("Sending %" PRIu32 " bytes at offset %" PRIu32 "\n", lreq, fileOffset);

				for (;;)
				{
					CanMessageFirmwareUpdateResponse * const msgp = buf.SetupResponseMessageNoRid<CanMessageFirmwareUpdateResponse>(CanInterface::GetCurrentMasterAddress(), src);
					const size_t lengthToSend = min<size_t>(lreq, sizeof(msgp->data));
					if (f->Read(msgp->data, lengthToSend) != (int)lengthToSend)
					{
						msgp->dataLength = 0;
						msgp->err = CanMessageFirmwareUpdateResponse::ErrOther;
						msgp->fileLength = fileLength;
						msgp->fileOffset = 0;
						buf.dataLength = msgp->GetActualDataLength();
						CanInterface::SendResponseNoFree(buf);

						reprap.GetPlatform().MessageF(ErrorMessage, "Error reading firmware update file '%s'\n", fname.c_str());
						reprap.GetExpansion().UpdateFailed(src);
						return;
					}

					msgp->dataLength = lengthToSend;
					msgp->err = CanMessageFirmwareUpdateResponse::ErrNone;
					msgp->fileLength = fileLength;
					msgp->fileOffset = fileOffset;
					buf.dataLength = msgp->GetActualDataLength();
					CanInterface::SendResponseNoFree(buf);
					fileOffset += lengthToSend;
					lreq -= lengthToSend;
					if (lreq == 0)
					{
						break;
					}
				}
			}
			f->Close();
		}
#	endif

		if (lreq != 0)			// if we didn't complete the request
		{
			CanMessageFirmwareUpdateResponse * const msgp = buf.SetupResponseMessageNoRid<CanMessageFirmwareUpdateResponse>(CanInterface::GetCurrentMasterAddress(), src);
			msgp->dataLength = 0;
			msgp->err = CanMessageFirmwareUpdateResponse::ErrNoFile;
			msgp->fileLength = 0;
			msgp->fileOffset = 0;
			buf.dataLength = msgp->GetActualDataLength();
			CanInterface::SendResponseNoFree(buf);

			reprap.GetPlatform().MessageF(ErrorMessage, "Received firmware update request for missing file '%s'\n", fname.c_str());
			reprap.GetExpansion().UpdateFailed(src);
		}
		else if (fileOffset == fileLength)
		{
			reprap.GetExpansion().UpdateFinished(src);
		}
	}
	else
	{
		const unsigned int bootloaderVersion = msg.bootloaderVersion;
		const unsigned int fileWanted = msg.fileWanted;
		CanMessageFirmwareUpdateResponse * const msgp = buf.SetupResponseMessageNoRid<CanMessageFirmwareUpdateResponse>(CanInterface::GetCurrentMasterAddress(), src);
		msgp->dataLength = 0;
		msgp->err = CanMessageFirmwareUpdateResponse::ErrOther;
		msgp->fileLength = 0;
		msgp->fileOffset = 0;
		buf.dataLength = msgp->GetActualDataLength();
		CanInterface::SendResponseNoFree(buf);
		reprap.GetPlatform().MessageF(ErrorMessage, "Can't satisfy request for firmware file %u from bootloader version %u\n", fileWanted, bootloaderVersion);
	}
#  endif
}

// Forward a received CAN message to the SBC. If it is a response to a request we sent on behalf of the SBC, map the
// request ID back to the SBC's txToken and collate multi-fragment standard replies into a single response.
// Stop whatever the move said this trigger stops, and tell the SBC what was stopped.
//
// The two message versions differ in the size of their per-handle entries, so each is read as
// itself; reading one as the other shifts every handle. Both carry a reading, which for a stall is
// the bitmap of the reporting board's stalled drivers and is the only thing that says which motor
// stalled. Only V2 carries the trigger timestamp, which is what lets the SBC undo the overshoot; a
// V1 board can only be stopped where the message found it
static void HandleInputStateChanged(CanMessageBuffer& buf, CanMessageType id) noexcept
{
#if HAS_SBC_INTERFACE
	const CanAddress src = buf.id.Src();
	SbcProtocol::MotionStoppedDriver stopped[SbcProtocol::MaxMotionStoppedDrivers];
	size_t numStopped = 0;
	// V1 carries no trigger timestamp; zero tells the SBC to correct from where the message
	// found the drives instead, which keeps the overshoot V2 exists to remove
	uint32_t whenTriggered = 0;
	uint32_t moveId = 0;

	if (id == CanMessageType::inputStateChangedV2)
	{
		const CanMessageInputChangedV2& msg = buf.msg.inputChangedV2;
		for (unsigned int i = 0; i < msg.numHandles && i < ARRAY_SIZE(msg.results); ++i)
		{
			// Bit i of states is the level of the i'th handle. Remembered whichever way it went, so
			// that a move armed on an input that is already active is stopped before it starts
			const bool active = (msg.states & (1u << i)) != 0;
			CanMotion::NoteInputState(src, msg.results[i].handle.asU16(), active);

			// Only a handle that went active stops anything; a release is just as much a change, and
			// stopping on one would end the move the moment the axis backed off the endstop
			if (!active)
			{
				continue;
			}

			const size_t added = CanMotion::StopDriversWatchingInput(
				src, msg.results[i].handle.asU16(), (uint32_t)msg.GetEntryReading(i),
				std::span{stopped + numStopped, ARRAY_SIZE(stopped) - numStopped}, moveId);
			if (added != 0)
			{
				// The board reports its own low 16 bits. Widened here rather than on the SBC,
				// because this is the side whose clock they are comparable with: the SBC's is a
				// model fitted to this one, and the boards are synchronised to this one
				whenTriggered = CanInterface::Convert16bitReceivedTimeStampTo32bits(msg.GetWhen(i));
				numStopped += added;
			}
		}
	}
	else
	{
		const CanMessageInputChangedV1& msg = buf.msg.inputChangedV1;
		for (unsigned int i = 0; i < msg.numHandles && i < ARRAY_SIZE(msg.results); ++i)
		{
			const bool active = (msg.states & (1u << i)) != 0;
			CanMotion::NoteInputState(src, msg.results[i].handle.asU16(), active);
			if (!active)
			{
				continue;
			}

			numStopped += CanMotion::StopDriversWatchingInput(
				src, msg.results[i].handle.asU16(), (uint32_t)msg.GetEntryReading(i),
				std::span{stopped + numStopped, ARRAY_SIZE(stopped) - numStopped}, moveId);
		}
	}

	if (numStopped != 0)
	{
		// The stop messages are built but not sent from this task; the async sender does that
		CanInterface::WakeAsyncSender();

		// The SBC works out where the drives should have ended up and sends the revert itself
		reprap.GetSbcInterface().ReportMotionStopped(whenTriggered, moveId, std::span{stopped, numStopped});
	}
#else
	(void)buf;
	(void)id;
#endif
}

void CommandProcessor::ForwardMessageToSbc(CanMessageBuffer& buf) noexcept
{
#  if HAS_SBC_INTERFACE
	SbcInterface& sbc = reprap.GetSbcInterface();
	const CanMessageType msgType = buf.id.MsgType();
	const CanAddress src = buf.id.Src();

	// If this is a response to a request we forwarded on behalf of the SBC, recover the SBC's txToken
	CanInterface::CanRequestMapping* _ecv_null mapping = nullptr;
	uint16_t txToken = 0xFFFF; // TODO synchronise this default value with DSF
	if (buf.id.IsResponse())
	{
		const auto rid = (CanRequestId)(buf.msg.generic.requestId);
		mapping = CanInterface::FindPendingRequest(src, rid);
		if (mapping != nullptr)
		{
			txToken = mapping->txToken;
		}
	}

	// Single-frame message (broadcast, unsolicited, or non-standard reply): forward the raw payload
	CANResponseHeader header;
	header.txToken = txToken;
	header.msgType = (uint16_t)msgType;
	header.dataLength = (uint16_t)buf.dataLength;
	header.srcAddress = src;
	header.flags = 0;
	header.status = (uint8_t)CanStatus::Ok;
	header.padding = 0;
	header.padding2 = 0;
	if (!sbc.EnqueueCanResponse(header, reinterpret_cast<const char*>(&buf.msg)))
	{
		// TODO handle this error
	}

	if (mapping != nullptr)
	{
		if (msgType != CanMessageType::standardReply || !buf.msg.standardReply.moreFollows)
		{
			CanInterface::ReleasePendingRequest(mapping);
		}
	}
#  else
	(void)buf;
#  endif
}

// Process a received broadcast or request message. Don't free the message buffer
void CommandProcessor::ProcessReceivedMessage(CanMessageBuffer& buf) noexcept
{
	if (buf.id.Src() !=
		CanInterface::GetCanAddress()) // I don't think we should receive our own messages, but in case we do...
	{
		const CanMessageType id = buf.id.MsgType();
		if (buf.id.Dst() != CanId::BroadcastAddress // don't flash the LED on broadcast messages e.g. temperature
													// reports and time sync
			&& id != CanMessageType::fansReport		// don't flash whenever we receive a regular status message
			&& id != CanMessageType::heatersStatusReport && id != CanMessageType::boardStatusReportV0 &&
			id != CanMessageType::boardStatusReportV1 && id != CanMessageType::driversStatusReport &&
			id != CanMessageType::filamentMonitorsStatusReportV2)
		{
			reprap.GetPlatform().OnProcessingCanMessage();
		}

		{
			const bool forwardToSbc = true;
			// Handle messages received in normal operation mode
			switch (id)
			{
			case CanMessageType::inputStateChangedV1:
			case CanMessageType::inputStateChangedV2:
				// An endstop move told us which input stops which driver, so the stop is decided here
				// rather than at the SBC. That is the whole point of doing it in this task: the round
				// trip to the SBC and back would let the axis overrun the endstop.
				//
				// The message is still forwarded below, because the object model has to see the input
				// change whether or not anything was moving
				HandleInputStateChanged(buf, id);
				break;

			case CanMessageType::firmwareBlockRequest:
				HandleFirmwareBlockRequest(buf);
				break;

			case CanMessageType::announceV0:
			case CanMessageType::announceV1:
				reprap.GetExpansion().ProcessAnnouncement(buf, id == CanMessageType::announceV1);
				break;

			case CanMessageType::boardStatusReportV0:
			case CanMessageType::boardStatusReportV1:
				reprap.GetExpansion().ProcessBoardStatusReport(buf);
				break;

			default:
				break;
			}

			// Forward broadcasts, status reports and responses (including standard replies) to the SBC
			if (forwardToSbc)
			{
				ForwardMessageToSbc(buf);
			}
		}
	}
}

#endif

// End
