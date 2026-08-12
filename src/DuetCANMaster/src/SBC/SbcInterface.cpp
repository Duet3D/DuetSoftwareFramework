/*
 * SbcInterface.cpp
 *
 *  Created on: 29 Mar 2019
 *      Author: Christian
 */

#include "SbcInterface.h"

#include "DataTransfer.h"

#if HAS_SBC_INTERFACE

#  if SUPPORTS_SBC_OVER_USB
#	include <Devices.h>
#  endif

#  include <AppNotifyIndices.h>
#  include <Hardware/ExceptionHandlers.h>
#  include <Hardware/SoftwareReset.h>
#  include <Platform/OutputMemory.h>
#  include <Platform/Platform.h>
#  include <Platform/RepRap.h>
#  include <Platform/TaskPriorities.h>
#  include <Platform/Tasks.h>
#  include <RepRapFirmware.h>

#  if SUPPORT_CAN_EXPANSION
#	include <CAN/CanInterface.h>
#	include <CAN/CanMotion.h>
#	include <CanMessageBuffer.h>

#	include <algorithm>
#	include <span>
#  endif

// script (same70q20b_flash.ld); the leading underscore is part of that contract
// NOLINTNEXTLINE(bugprone-reserved-identifier) - _estack is defined by the linker
extern char _estack; // defined by the linker

// The SBC task's stack size needs to be enough to support rr_model and expression evaluation
// In RRF 3.3beta3, 744 is only just enough for simple expression evaluation in a release build when using globals
// In 3.3beta3.1 we have saved ~151 bytes (37 words) of stack compared to 3.3beta3
// In 3.5.2, the stack size is increased again to allow for nested functions to be properly evaluated (up to 7 nested
// max calls e.g.)
#  if defined(DEBUG)
constexpr size_t sbcTaskStackWords = 1600; // debug builds use more stack
#  else
constexpr size_t sbcTaskStackWords = 1400;
#  endif

constexpr uint32_t sbcYieldTimeout = 10;

static Task<sbcTaskStackWords>* sbcTask;

extern "C" [[noreturn]] void SBCTaskStart(void* /*pvParameters*/) noexcept
{
	reprap.GetSbcInterface().TaskLoop();
}

SbcInterface::SbcInterface() noexcept
	: m_isConnected(false)
	, m_numDisconnects(0)
	, m_numTimeouts(0)
	, m_numSbcTimeouts(0)
	, m_lastTransferTime(0)
	, m_rxPointer(0)
	, m_txPointer(0)
	, m_txEnd(0)
	, m_sendBufferUpdate(true)
#  if SUPPORTS_SBC_OVER_USB
	, m_pendingUsbDevice(nullptr)
	, m_usbDeviceIndex(0)
#  endif

	, m_canResponseHead(0)
	, m_canResponseTail(0)
	, m_motionStoppedHead(0)
	, m_motionStoppedTail(0)
#  ifdef TRACK_FILE_CODES
	, fileCodesRead(0)
	, fileCodesHandled(0)
	, fileMacrosRunning(0)
	, fileMacrosClosing(0)
#  endif
{
}

void SbcInterface::Init() noexcept
{
	m_gcodeReplyMutex.Create("SBCReply");
	m_transfer.Init();
	sbcTask = new Task<sbcTaskStackWords>();
	sbcTask->Create(SBCTaskStart, "SBC", nullptr, TaskPriority::SbcPriority);
	m_iapRamAvailable = (const char*)&_estack - Tasks::GetHeapTop();
}

#  if SUPPORTS_SBC_OVER_USB

// Blocking write that retries until all bytes are sent or timeout
static bool ReliableUsbWrite(SerialCDC* dev, const uint8_t* data, size_t length, uint32_t timeoutMs) noexcept
{
	const uint32_t startTime = millis();
	while (length > 0)
	{
		const size_t written = dev->write(data, length);
		data += written;
		length -= written;
		if (length > 0)
		{
			if (millis() - startTime >= timeoutMs)
			{
				return false;
			}
			delay(1);
		}
	}
	dev->flush();
	return true;
}

// Send the USB SBC init response message reliably
static void SendUsbInitMessage(SerialCDC* dev) noexcept
{
	char buf[128];
	SafeSnprintf(buf,
				 sizeof(buf),
				 "Switching to binary SBC mode\n"
				 "{\"protocol\":%u,\"rxBuffer\":%u,\"txBuffer\":%u}\n",
				 (unsigned)SbcProtocolVersion,
				 (unsigned)SbcTransferBufferSize,
				 (unsigned)SbcTransferBufferSize);
	ReliableUsbWrite(dev, reinterpret_cast<const uint8_t*>(buf), strlen(buf), 2000);
}

#  endif // SUPPORTS_SBC_OVER_USB

[[noreturn]] void SbcInterface::TaskLoop() noexcept
{
	DataTransfer::InitFromTask();
	m_transfer.StartNextTransfer();

	bool busy = false;
	bool transferComplete = false;
	bool hadTimeout = false;
	bool hadSbcTimeout = false;
	bool hadReset = false;
	for (;;)
	{
#  if SUPPORTS_SBC_OVER_USB
		// Check for pending USB transport switch (requested by GCode system after M576.1)
		if (m_pendingUsbDevice != nullptr)
		{
			SerialCDC* dev = m_pendingUsbDevice;
			m_pendingUsbDevice = nullptr;

			// Switch DataTransfer from SPI to USB
			m_transfer.SwitchToUsb(dev, m_usbDeviceIndex);

			// Send init message via standard CDC I/O (before direct mode)
			SendUsbInitMessage(dev);
			dev->WaitForTxEmpty(SbcTxDrainTimeout);

			// Handover: host sends first packet to complete CDC stream's pending OUT transfer
			dev->BeginDirectMode();
			continue; // restart the task loop
		}
#  endif

		// Try to exchange data with the SBC
		transferComplete = hadTimeout = hadReset = false;
		do
		{
			busy = false;
			m_state = m_transfer.DoTransfer();
			const uint32_t transferStartTime = millis();
			switch (m_state)
			{
			case TransferState::DoingFullTransfer:
#  if SUPPORTS_SBC_OVER_USB
				// When USB SBC is supported but not connected over SPI, use a short timeout so we can poll for M576.1
				if (!m_isConnected && m_transfer.GetTransportType() == SbcTransportType::spi)
				{
					hadTimeout = !TaskBase::TakeIndexed(NotifyIndices::SbcInterface, SbcConnectionTimeout);
					hadSbcTimeout = false;
					break;
				}
#  endif
				hadTimeout = !TaskBase::TakeIndexed(NotifyIndices::SbcInterface,
													m_isConnected ? SbcConnectionTimeout : TaskBase::TimeoutUnlimited);
				hadSbcTimeout = hadTimeout && millis() - transferStartTime < SbcConnectionTimeout + sbcYieldTimeout;
				if (!hadTimeout && !DataTransfer::DataReceived() &&
					m_transfer.GetTransportType() == SbcTransportType::spi)
				{
					// Woken by EventOccurred because new outgoing data was queued while we sat idle-armed
					// (no SPI transfer has happened yet). Fold the new data into the armed buffer and
					// re-arm - without advancing the sequence number - so SbcDataAvailable goes high and
					// the SBC pulls it on the next clock.
					// Motion-stopped reports first: a move is already stopped and waiting for the
					// SBC to say where it should end up, so it should not queue behind status traffic
					const bool wroteEverything = ProcessMotionStopped() & ProcessCanResponses();
					if (wroteEverything)
					{
						m_transfer.StartNextTransfer(true);
					}
					busy = true; // re-enter DoTransfer and keep waiting
				}
				break;
			case TransferState::DoingPartialTransfer:
				hadTimeout = !TaskBase::TakeIndexed(NotifyIndices::SbcInterface, SbcTransferTimeout);
				hadSbcTimeout = hadTimeout && millis() - transferStartTime < SbcTransferTimeout + sbcYieldTimeout;
				break;
			case TransferState::FinishingTransfer:
				busy = true;
				break;
			case TransferState::ConnectionTimeout:
				hadTimeout = hadSbcTimeout = true;
				break;
			case TransferState::ConnectionReset:
				hadReset = true;
				break;
			case TransferState::Finished:
				transferComplete = true;
				break;
			}
		} while (busy);

		// Handle connection errors
		if (m_isConnected && (hadReset || hadTimeout))
		{
			m_isConnected = false;
			m_numDisconnects++;
			if (hadTimeout)
			{
				m_numTimeouts++;
				if (hadSbcTimeout)
				{
					m_numSbcTimeouts++;
				}
#  if SUPPORTS_SBC_OVER_USB
				if (m_transfer.GetTransportType() == SbcTransportType::Usb)
				{
					SerialCDC* dev = m_transfer.GetUsbDevice();
					if (dev != nullptr && !dev->IsConnected())
					{
						reprap.GetPlatform().Message(NetworkInfoMessage, "Lost connection to SBC (port closed)\n");
					}
					else
					{
						reprap.GetPlatform().Message(NetworkInfoMessage, "Lost connection to SBC (timeout)\n");
					}
				}
				else
#  endif
				{
					reprap.GetPlatform().Message(NetworkInfoMessage, "Lost connection to SBC (timeout)\n");
				}
			}
			else
			{
				reprap.GetPlatform().Message(NetworkInfoMessage, "Lost connection to SBC (connection reset)\n");
			}

			// Invalidate local resources
			InvalidateResources();
			if (hadReset)
			{
				// Let the main task invalidate resources before processing new data
				TaskBase::TakeIndexed(NotifyIndices::SbcInterface, sbcYieldTimeout);
			}

#  if SUPPORTS_SBC_OVER_USB
			// On USB disconnect, exit direct mode and reinit the USB GCode device
			if (m_transfer.GetTransportType() == SbcTransportType::Usb)
			{
				if (SerialCDC* dev = m_transfer.GetUsbDevice())
				{
					dev->EndDirectMode();
				}
				// reprap.GetPlatform().ReinitUsbDevice(usbDeviceIndex);
				m_transfer.ResetConnection(true);

				continue; // restart the task loop
			}
#  endif
		}

		// Deal with received data
		if (transferComplete)
		{
			if (!m_isConnected)
			{
				m_isConnected = true;
				reprap.GetPlatform().MessageF(NetworkInfoMessage,
											  "Connection to SBC established over %s!\n",
											  m_transfer.GetTransportType() == SbcTransportType::Usb ? "USB" : "SPI");
			}

			// Handle exchanged data and kick off the next transfer
			ExchangeData();
			m_transfer.StartNextTransfer();
		}
		else if (hadTimeout || hadReset)
		{
#  if SUPPORTS_SBC_OVER_USB
			// If USB transport failed, always exit direct mode and reset to SPI
			if (m_transfer.GetTransportType() == SbcTransportType::Usb)
			{
				if (SerialCDC* dev = m_transfer.GetUsbDevice())
				{
					dev->EndDirectMode();
				}
				// reprap.GetPlatform().ReinitUsbDevice(usbDeviceIndex);
				m_transfer.ResetConnection(true);

				continue;
			}
#  endif
			// SPI: reset the connection if no data could be exchanged
			m_transfer.ResetConnection(hadTimeout);
		}
	}
}

// Queue a CAN response to be forwarded to the SBC. Called from the CAN receiver tasks. Returns false if the queue is
// full.
bool SbcInterface::EnqueueCanResponse(const CANResponseHeader& header, const char* _ecv_null data) noexcept
{
	const TaskCriticalSectionLocker lock;
	const size_t next = (m_canResponseHead + 1) % NumCanResponseBuffers;
	if (next == m_canResponseTail)
	{
		return false; // queue full
	}

	CanResponseBuffer& item = m_canResponseRing[m_canResponseHead];
	item.header = header;
	if (data != nullptr && header.dataLength <= sizeof(item.payload))
	{
		memcpy(item.payload, data, header.dataLength);
	}
	m_canResponseHead = next;

	// const bool timeCritical = header.msgType <= CanMessageType::inputStateChangedV2;
	EventOccurred(true);
	return true;
}

bool SbcInterface::ReportMotionStopped(uint32_t whenTriggered,
									   std::span<const duet::spi::protocol::MotionStoppedDriver> stopped) noexcept
{
	if (stopped.empty())
	{
		return true;
	}

	const TaskCriticalSectionLocker lock;
	++m_motionStoppedReports;
	const size_t next = (m_motionStoppedHead + 1) % NumMotionStoppedBuffers;
	if (next == m_motionStoppedTail)
	{
		++m_motionStoppedDropped;
		return false;						// queue full; the move will stop but keep its overshoot
	}

	MotionStoppedBuffer& item = m_motionStoppedRing[m_motionStoppedHead];
	const auto count = min<size_t>(stopped.size(), ARRAY_SIZE(item.drivers));
	item.header.whenTriggered = whenTriggered;
	item.header.numDrivers = (uint8_t)count;
	memset(item.header.padding, 0, sizeof(item.header.padding));
	memcpy(item.drivers, stopped.data(), count * sizeof(item.drivers[0]));
	m_motionStoppedHead = next;

	EventOccurred(true);
	return true;
}

// Write any queued motion-stopped reports into the current transfer.
// Returns false when the transfer is full, in which case the rest go next time.
bool SbcInterface::ProcessMotionStopped() noexcept
{
	while (m_motionStoppedTail != m_motionStoppedHead)
	{
		const MotionStoppedBuffer& item = m_motionStoppedRing[m_motionStoppedTail];
		if (!m_transfer.WriteMotionStopped(item.header, item.drivers))
		{
			return false;
		}
		m_motionStoppedTail = (m_motionStoppedTail + 1) % NumMotionStoppedBuffers;
	}
	return true;
}

// Forward a text reply to the SBC as one or more standardReply CAN responses, tagged with the request's txToken so the
// SBC can match it back to the request. Long text is split into fragments exactly as an expansion board would send
// them.
void SbcInterface::EnqueueCanTextReply(uint16_t txToken, CanRequestId requestId, const char* text) noexcept
{
	const size_t textLength = strlen(text);
	size_t offset = 0;
	unsigned int fragment = 0;
	do
	{
		CanMessageStandardReply msg{};
		memset(&msg, 0, sizeof(msg));
		msg.SetRequestId(requestId);
		msg.resultCode = (uint16_t)GCodeResult::ok;
		msg.fragmentNumber = fragment;

		size_t thisLength = textLength - offset;
		thisLength = std::min(thisLength, CanMessageStandardReply::MaxTextLength);
		memcpy(msg.text, text + offset, thisLength);
		offset += thisLength;
		msg.moreFollows = (offset < textLength) ? 1 : 0;

		CANResponseHeader header;
		header.txToken = txToken;
		header.msgType = (uint16_t)CanMessageType::standardReply;
		header.dataLength = (uint16_t)msg.GetActualDataLength(thisLength);
		header.srcAddress = CanInterface::GetCanAddress();
		header.flags = 0;
		header.status = (uint8_t)CanStatus::Ok;
		header.padding = 0;
		header.padding2 = 0;
		(void)EnqueueCanResponse(header, reinterpret_cast<const char*>(&msg));

		++fragment;
	} while (offset < textLength);
}

// Write queued CAN responses into the current transfer. Stops if the transfer runs out of room (the rest are sent next
// time).
bool SbcInterface::ProcessCanResponses() noexcept
{
	bool ret = false;
	for (;;)
	{
		CanResponseBuffer* item = nullptr;
		{
			const TaskCriticalSectionLocker lock;
			if (m_canResponseTail == m_canResponseHead)
			{
				break; // nothing queued
			}
			item = &m_canResponseRing[m_canResponseTail];
		}

		if (!m_transfer.WriteCANResponse(item->header, reinterpret_cast<const char*>(item->payload)))
		{
			break; // not enough room in this transfer
		}

		const TaskCriticalSectionLocker lock;
		m_canResponseTail = (m_canResponseTail + 1) % NumCanResponseBuffers;
		ret = true;
	}
	return ret;
}

void SbcInterface::ExchangeData() noexcept
{

// Process incoming packets
#  if 0
	bool codeBufferAvailable = true;
#  endif
	for (size_t i = 0; i < m_transfer.PacketsToRead(); i++)
	{
		const PacketHeader* const packet = m_transfer.ReadPacket();
		if (packet == nullptr)
		{
			if (reprap.Debug(Module::SbcInterface))
			{
				debugPrintf("Error trying to read next packet\n");
			}
			break;
		}

		if (packet->request > (uint16_t)SbcRequest::Message)
		{
			REPORT_INTERNAL_ERROR;
			break;
		}

		bool packetAcknowledged = true;
		switch ((SbcRequest)packet->request)
		{
		// Perform an emergency stop
		case SbcRequest::EmergencyStop:
			reprap.EmergencyStop();
			break;

		// Reset the controller
		case SbcRequest::Reset:
			reprap.EmergencyStop(); // turn off heaters and motors, tell expansion boards to reset
			SoftwareReset(SoftwareResetReason::UserFromSbc);
			break;

#  if SUPPORT_CAN_EXPANSION
		// Enable or disable the CAN interface
		case SbcRequest::EnableCAN:
		{
			const auto* const header = m_transfer.ReadDataHeader<EnableCANHeader>();
			CanInterface::EnableCan(header->enable != 0);
			break;
		}

		// Schedule a move planned by the SBC. The packet is DDA::Prepare's output, so this hands it
		// straight to CanMotion; nothing here needs to understand the move.
		case SbcRequest::ScheduleMove:
		{
			// Take the whole declared payload in one read, so the read pointer lands on the next
			// packet whether or not this one turns out to be usable. numDrivers is what says how
			// much follows the header, and it arrives over SPI: sizing the driver array from it
			// without checking would read past the payload and desynchronise the rest of the
			// transfer as well.
			const char* const payload = m_transfer.ReadData(packet->length);
			if (packet->length < sizeof(ScheduleMoveHeader))
			{
				REPORT_INTERNAL_ERROR;
				break;
			}

			const auto* const header = reinterpret_cast<const ScheduleMoveHeader*>(payload);
			const size_t numDrivers = header->numDrivers;
			if (numDrivers > SbcProtocol::MaxScheduleMoveDrivers ||
				packet->length < sizeof(ScheduleMoveHeader) + (numDrivers * sizeof(ScheduleMoveDriver)))
			{
				REPORT_INTERNAL_ERROR;
				break;
			}

			// The span is built from the count that has just been checked against the payload, so
			// the bound travels with the pointer instead of being re-derived from the header at the
			// far end.
			const std::span drivers{reinterpret_cast<const ScheduleMoveDriver*>(payload + sizeof(ScheduleMoveHeader)),
									numDrivers};
			CanMotion::ScheduleFromSbc(*header, drivers);
			break;
		}

		// Send a CAN message on behalf of the SBC
		case SbcRequest::SendCANMessage:
		{
			const auto* header = m_transfer.ReadDataHeader<CANRequestHeader>();
			const uint16_t txToken = header->txToken;
			const auto msgType = (CanMessageType)header->msgType;	  // TODO validate this is a valid CanMessageType
			const auto replyType = (CanMessageType)header->replyType; // TODO validate this is a valid CanMessageType
			const uint8_t dstAddress = header->dstAddress;
			const uint8_t dataLength = header->dataLength;
			const char* payload = m_transfer.ReadData(dataLength);

			// A setAddressAndNormalTiming message addressed to the master (oldAddress 0) configures our own CAN timing
			// rather than being forwarded onto the bus. newAddress is ignored in this case.
			if (msgType == CanMessageType::setAddressAndNormalTiming &&
				dataLength >= sizeof(CanMessageSetAddressAndNormalTiming))
			{
				const auto* const timingMsg = reinterpret_cast<const CanMessageSetAddressAndNormalTiming*>(payload);
				if (timingMsg->oldAddress == 0)
				{
					const bool doSetTiming =
						(timingMsg->doSetTiming == CanMessageSetAddressAndNormalTiming::DoSetTimingYes);
					String<StringLength100> reply;
					CanInterface::ConfigLocalCanTiming(timingMsg->normalTiming, doSetTiming, reply.GetRef());
					if (!reply.IsEmpty())
					{
						// Forward the timing report back to the SBC as a CAN response tagged with the request's txToken
						EnqueueCanTextReply(txToken, (CanRequestId)timingMsg->requestId, reply.c_str());
					}
					break;
				}
			}

			CanMessageBuffer buf;

			// Convert the SBC header into a CAN message buffer, leaving the request ID field exactly as the SBC sent it
			if (dstAddress == CanId::BroadcastAddress)
			{
				buf.id.SetBroadcast(msgType, CanInterface::GetCanAddress());
			}
			else if ((header->flags & SbcProtocol::SendCanMessageFlags::IsResponse) != 0)
			{
				buf.id.SetResponse(msgType, CanInterface::GetCanAddress(), (CanAddress)dstAddress);
			}
			else
			{
				buf.id.SetRequest(msgType, CanInterface::GetCanAddress(), (CanAddress)dstAddress);
			}
			buf.dataLength = dataLength;
			buf.marker = 0;
			buf.extId = 1;
			buf.fdMode = 1;
			buf.useBrs = 1;
			buf.remote = 0;
			buf.reportInFifo = 0;
			buf.spare = 0;
			memcpy(&buf.msg, payload, dataLength);

			CanInterface::SendCanRequest(buf, txToken, replyType);
			break;
		}
#  endif

		// Write the first chunk of the IAP binary
		case SbcRequest::WriteIap:
		{
			// TODO may want to implement this?
			// reprap.PrepareToLoadIap();
			// ReceiveAndStartIap(transfer.ReadData(packet->length), packet->length);
			break;
		}

		// Send a firmware message, typically a response to a command that has been passed to DSF.
		// These responses can get quite long (e.g. responses to M20) so receive it into an OutputBuffer.
		case SbcRequest::Message:
		{
			OutputBuffer* buf = nullptr;
			if (OutputBuffer::Allocate(buf))
			{
				MessageType type{};
				if (m_transfer.ReadMessage(type, buf))
				{
					// Output message to the target
					Platform::Message(type, buf); // Message(MessageType, OutputBuffer*) is static
				}
				else
				{
					// Not enough memory for reading the whole message, try again later
					OutputBuffer::ReleaseAll(buf);
					packetAcknowledged = false;
				}
			}
			else
			{
				// No output memory available, skip the packet content and try again later
				(void)m_transfer.ReadData(packet->length);
				packetAcknowledged = false;
			}
			break;
		}

		// Invalid request
		default:
			(void)m_transfer.ReadData(packet->length); // skip the packet content
#  ifdef DEBUG
			// Report this error only in debug builds. We may get here when the SBC sends a file response but the
			// connection was reset
			REPORT_INTERNAL_ERROR;
#  endif
			break;
		}

		// Request the packet again if no response could be sent back
		if (!packetAcknowledged)
		{
			m_transfer.ResendPacket(packet);
		}
	}

	// No inter-transfer delay: the HAT re-arms immediately after each transfer and holds TfrReady, so
	// idle costs nothing (the SBC only clocks a transfer when it has data or sees SbcDataAvailable).

#  if 0
	// TODO do we need this or similar functionality?
	// Send code replies and generic messages
	if (!gcodeReply.IsEmpty())
	{
		MutexLocker lock(gcodeReplyMutex);
		while (!gcodeReply.IsEmpty())
		{
			const MessageType type = gcodeReply.GetFirstItemType();
			OutputBuffer* buffer = gcodeReply.GetFirstItem(); // this may be null
			if (!transfer.WriteCodeReply(type, buffer))		  // this handles the null case too
			{
				break;
			}
			gcodeReply.SetFirstItem(buffer); // this does a pop if buffer is null
		}
	}
#  endif

	// Forward any CAN responses queued by the CAN receiver tasks
	ProcessMotionStopped();
	ProcessCanResponses();
}

[[noreturn]] void SbcInterface::ReceiveAndStartIap(const char* iapChunk, size_t length) noexcept
{
	// NOLINTNEXTLINE(performance-no-int-to-ptr) - IAP_IMAGE_START is a fixed RAM address
	char* iapWritePointer = reinterpret_cast<char*>(IAP_IMAGE_START);
	for (;;)
	{
		// Write the next IAP chunk
		if (iapChunk != nullptr)
		{
			auto* dst = reinterpret_cast<uint32_t*>(iapWritePointer);
			const auto* src = reinterpret_cast<const uint32_t*>(iapChunk);
			memcpyu32(dst, src, length / sizeof(uint32_t));
			iapWritePointer += length;
			iapChunk = nullptr;
		}

		// Get the next IAP chunk
		m_transfer.StartNextTransfer();
		bool transferComplete = false;
		do
		{
			switch (m_transfer.DoTransfer())
			{
#  if SAME5x
			case TransferState::connectionTimeout:
#  endif
			case TransferState::ConnectionReset:
				// Perform a firmware reset, we're in an unsafe state to resume regular operation
				SoftwareReset(SoftwareResetReason::User);
				break;
			case TransferState::Finished:
				transferComplete = true;
				break;
			default:
				// do nothing
				break;
			}
		} while (!transferComplete);

		// Process only IAP-related packets
		for (size_t i = 0; i < m_transfer.PacketsToRead(); i++)
		{
			const PacketHeader* const packet = m_transfer.ReadPacket();
			switch ((SbcRequest)packet->request)
			{
			case SbcRequest::WriteIap: // Write another IAP chunk. It's always bound on a 4-byte boundary
			{
				iapChunk = m_transfer.ReadData(packet->length);
				length = packet->length;
				break;
			}
			case SbcRequest::StartIap: // Start the IAP binary
#  if SUPPORTS_SBC_OVER_USB
									   // Cleanly shut down TinyUSB before IAP re-initializes the USB peripheral
				// with its own bare-metal driver
				if (m_transfer.GetTransportType() == SbcTransportType::Usb)
				{
					serialUSB.end();
					StopUsbTask();
				}
#  endif
				reprap.StartIap(nullptr);
				break;
			default: // Other packet types are not supported while IAP is being written
				// do nothing
				break;
			}
		}
	}
}

void SbcInterface::InvalidateResources() noexcept
{
	m_txEnd = 0;
	m_txPointer = 0;
	m_rxPointer = 0;
	m_sendBufferUpdate = true;

	// Don't cache any messages if they cannot be sent
	{
		const MutexLocker lock(m_gcodeReplyMutex);
		m_gcodeReply.ReleaseAll();
	}

	// TODO Turn off all the heaters
}

void SbcInterface::Diagnostics(const StringRef& reply) noexcept
{
	reply.copy("=== SBC interface ===");
	if (m_isConnected)
	{
		m_transfer.Diagnostics(reply);
	}
	else
	{
		reply.lcat("Not connected");
	}
	reply.lcatf("State: %d, disconnects: %" PRIu32 ", timeouts: %" PRIu32 " total, %" PRIu32
				" by SBC, IAP RAM available 0x%05" PRIx32,
				(int)m_state,
				m_numDisconnects,
				m_numTimeouts,
				m_numSbcTimeouts,
				m_iapRamAvailable);
	reply.lcatf("Buffer RX/TX: %d/%d-%d", (int)m_rxPointer, (int)m_txPointer, (int)m_txEnd);

	// Where an endstop stop leaves this board. The SBC works out where the drives should end up, so
	// a stop that is never reported here is one it can never correct - and nothing else says whether
	// the report was made
	reply.lcatf("Motion stops reported: %" PRIu32 ", dropped: %" PRIu32, m_motionStoppedReports, m_motionStoppedDropped);
#  ifdef TRACK_FILE_CODES
	reply.lcatf("File codes read/handled: %d/%d, file macros open/closing: %d %d",
				(int)fileCodesRead,
				(int)fileCodesHandled,
				(int)fileMacrosRunning,
				(int)fileMacrosClosing);
#  endif
}

#  if SUPPORTS_SBC_OVER_USB

void SbcInterface::RequestUsbSwitch(SerialCDC* dev, unsigned int usbDevIndex) noexcept
{
	m_pendingUsbDevice = dev;
	m_usbDeviceIndex = usbDevIndex;
	sbcTask->Give(
		NotifyIndices::SbcInterface); // wake the SBC task directly, bypassing IsConnected() check in EventOccurred
}

#  endif

void SbcInterface::HandleGCodeReply(MessageType mt, const char* reply) noexcept
{
	if (!IsConnected())
	{
		return;
	}

#  ifdef TRACK_FILE_CODES
	if ((mt & ((1u << GCodeChannel::File) | (1u << GCodeChannel::File2))) != 0)
	{
		fileCodesHandled++;
	}
#  endif

	const MutexLocker lock(m_gcodeReplyMutex);
	OutputBuffer* buffer = m_gcodeReply.GetLastItem();
	if (buffer != nullptr && mt == m_gcodeReply.GetLastItemType() && (mt & PushFlag) != 0 && !buffer->IsReferenced())
	{
		// Try to save some space by combining segments that have the Push flag set
		buffer->Cat(reply);
	}
	else if (reply[0] != 0 && OutputBuffer::Allocate(buffer))
	{
		// Attempt to allocate one G-code buffer per non-empty output message
		buffer->Cat(reply);
		m_gcodeReply.Push(buffer, mt);
	}
	else
	{
		// Store nullptr to indicate an empty response. This way many OutputBuffer references can be saved
		m_gcodeReply.Push(nullptr, mt);
	}
	EventOccurred();
}

void SbcInterface::HandleGCodeReply(MessageType mt, OutputBuffer* buffer) noexcept
{
	if (!IsConnected())
	{
		OutputBuffer::ReleaseAll(buffer);
		return;
	}

#  ifdef TRACK_FILE_CODES
	if ((mt & ((1u << GCodeChannel::File) | (1u << GCodeChannel::File2))) != 0)
	{
		fileCodesHandled++;
	}
#  endif

	const MutexLocker lock(m_gcodeReplyMutex);
	m_gcodeReply.Push(buffer, mt);
	EventOccurred();
}

void SbcInterface::EventOccurred(bool timeCritical) const noexcept
{
	(void)timeCritical; // all events are handled the same way now that there is no inter-transfer delay
	if (!IsConnected())
	{
		return;
	}

	// Wake the SBC task so it folds the newly-queued outgoing data into the armed transfer and raises
	// SbcDataAvailable. If a transfer is already in progress the wake is harmless (the task re-blocks).
	sbcTask->Give(NotifyIndices::SbcInterface);
}

#endif
