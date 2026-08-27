/*
 * CanInterface.cpp
 *
 *  Created on: 19 Sep 2018
 *      Author: David
 */

#include "CanInterface.h"

#if SUPPORT_CAN_EXPANSION

#  include "CanMotion.h"
#  include "CommandProcessor.h"

#  include "CanMessageGenericConstructor.h"
#  include <CanMessageBuffer.h>
#  include <CanMessageGenericTables.h>
#  include <Movement/StepTimer.h>
#  include <RTOSIface/RTOSIface.h>

#  include <Platform/RepRap.h>

#  include <Platform/Platform.h>

#  include <Platform/OutputMemory.h>
#  include <Platform/TaskPriorities.h>

#  include <AppNotifyIndices.h>

#  if HAS_SBC_INTERFACE
#	include "SBC/SbcInterface.h"
#  endif

#  define SUPPORT_CAN 1 // needed by CanDevice.h
#  include <CanDevice.h>
#  if SAME70
#	include <pmc/pmc.h>
#  endif

const unsigned int numCanBuffers = 2 * MaxCanBoards + 10;

constexpr uint32_t maxMotionSendWait = 20;								 // milliseconds
constexpr uint32_t maxUrgentSendWait = 20;								 // milliseconds
constexpr uint32_t maxTimeSyncSendWait = 2;								 // milliseconds
constexpr uint32_t maxResponseSendWait = CanInterface::UsualSendTimeout; // milliseconds
constexpr uint32_t maxRequestSendWait = CanInterface::UsualSendTimeout;	 // milliseconds

// Define how often we send time sync messages. This value and the time interval between sending broadcast status
// messages (currently 250ms) should be relatively prime. The reason is that if we try to send a time sync message just
// after a board has started broadcasting a status message, the time sync message will get delayed until the broadcast
// message finishes, which could be up to 600us at 1Mbps (the time taken to send a message with a 64-byte payload when
// not using BRS). When we used a 200us interval here, this meant that the same clash would occur 1 second later, and
// again 1 second after that. Using a value here that is relatively prime to 250ms avoids that happening. Alternatively
// we could add a random element to the interval.
constexpr uint32_t canClockIntervalMillis = 211;

// Define the maximum time sync delay that we tolerate. Occasionally on the SAME70 we get spurious very long delays, so
// we must ignore those. CAN-FD packets have 42 header bits, up to 64*8 data bits and 45 trailer bits. The header and
// data parts may have added stuff bits. That's a maximum of 554 header+data bits plus up to 20% additional stuff bits,
// and 45 trailer bits including fixed stuff bits. So the maximum number of bits is less than 710, which takes 710us to
// transmit at 1Mbps. Allow an extra 5us for scheduling delays.
constexpr uint16_t maxTimeSyncDelay = (uint16_t)MicrosecondsToStepClocks(
	(42 + 64 * 8) * 1.2 + 45 + 5); // the maximum normal delay before a CAN time sync message is sent, in step clocks

static_assert(maxTimeSyncDelay >= 400 && maxTimeSyncDelay <= 1000); // check it's in the right ball park

#  define USE_BIT_RATE_SWITCH 0

constexpr uint32_t minBitRate = 15; // MCP2542 has a minimum bite rate of 14.4kbps
constexpr uint32_t maxBitRate = 5000;
constexpr uint32_t defaultBitRate = 1000;

constexpr float minSamplePoint = 0.5;
constexpr float maxSamplePoint = 0.95;

constexpr float minJumpWidth = 0.05;
constexpr float maxJumpWidth = 0.5;

// In-flight SBC-originated CAN requests, so that responses can be matched back to the SBC's txToken
constexpr size_t numPendingCanRequests = 32;
static CanInterface::CanRequestMapping pendingRequests[numPendingCanRequests];

static uint32_t longestWaitTime = 0;
static uint16_t longestWaitMessageType = 0;

static uint32_t peakTimeSyncTxDelay = 0;

// Debug
static unsigned int goodTimeStamps = 0;
static unsigned int badTimeStamps = 0;
static unsigned int timeSyncMessagesSent = 0;
// End debug

static volatile uint16_t timeSyncTxTimeStamp;
static volatile bool gotTimeSyncTxTimeStamp = false;

static CanAddress myAddress =
#  ifdef DUET3_ATE
	CanId::ATEMasterAddress;
#  else
	CanId::MasterAddress;
#  endif

static uint8_t fastDataRate = 0;   // the fast data phase bit rate multiplier minus one. 0 means don't use BRS.
static uint8_t dTseg1MinusOne = 0; // the fast data rate sample point minus one
static uint8_t currentTimeSyncMarker = 0xFF; // the marker we use to track time sync message transmit events

static volatile bool canEnabled =
	false; // whether the CAN interface is enabled. When enabled we broadcast time sync messages.

// #define CAN_DEBUG

// Define the memory configuration we want to use
constexpr CanDevice::Config can0Config = {
	.dataSize = 64,
	.numTxBuffers = 5,
	.txFifoSize = 16,
	.numRxBuffers = 0,
	.rxFifo0Size = 32, // increased from 16 to help with accelerometer and closed loop data collection
	.rxFifo1Size = 16,
	.numShortFilterElements = 0,
#  ifdef DUET3_ATE
	.numExtendedFilterElements = 7,
#  else
	.numExtendedFilterElements = 6,
#  endif
	.txEventFifoSize = 16};

static_assert(can0Config.IsValid());

// CAN buffer memory must be in the first 64Kb of RAM (SAME5x) or in non-cached RAM (SAME70), so put it in its own
// memory section
static uint32_t can0Memory[can0Config.GetMemorySize()] __attribute__((section(".CanMessage")));

static CanDevice* _ecv_null can0dev = nullptr;

static unsigned int txTimeouts[can0Config.numTxBuffers + 1] = {0};
static uint32_t lastCancelledId = 0;

#  if DUAL_CAN

constexpr CanDevice::Config can1Config = {.dataSize = 8,
										  .numTxBuffers = 2,
										  .txFifoSize = 4,
										  .numRxBuffers = 0,
										  .rxFifo0Size = 16,
										  .rxFifo1Size = 16,
										  .numShortFilterElements = 1,
										  .numExtendedFilterElements = 1,
										  .txEventFifoSize = 16};

static_assert(can1Config.IsValid());

// CAN buffer memory must be in the first 64Kb of RAM (SAME5x) or in non-cached RAM (SAME70), so put it in its own
// segment
static uint32_t can1Memory[can1Config.GetMemorySize()] __attribute__((section(".CanMessage")));

static CanDevice* _ecv_null can1dev = nullptr;

#  endif

// Transmit buffer usage. All dedicated buffer numbers must be < Can0Config.numTxBuffers.
constexpr auto txBufferIndexUrgent = CanDevice::TxBufferNumber::buffer0;
constexpr auto txBufferIndexTimeSync = CanDevice::TxBufferNumber::buffer1;
constexpr auto txBufferIndexRequest = CanDevice::TxBufferNumber::buffer2;
constexpr auto txBufferIndexResponse = CanDevice::TxBufferNumber::buffer3;
constexpr auto txBufferIndexBroadcast = CanDevice::TxBufferNumber::buffer4;
constexpr auto txBufferIndexMotion =
	CanDevice::TxBufferNumber::fifo; // we send lots of movement messages so use the FIFO for them

// Receive buffer/FIFO usage. All dedicated buffer numbers must be < Can0Config.numRxBuffers.
constexpr auto rxBufferIndexBroadcast = CanDevice::RxBufferNumber::fifo0;
constexpr auto rxBufferIndexRequest = CanDevice::RxBufferNumber::fifo0;
constexpr auto rxBufferIndexResponse = CanDevice::RxBufferNumber::fifo1;

// CanSender management task
constexpr size_t canSenderTaskStackWords = 400;
static Task<canSenderTaskStackWords> canSenderTask;

constexpr size_t canReceiverTaskStackWords = 1000;
static Task<canReceiverTaskStackWords> canReceiverTask;

// High-priority receiver task: drains FIFO 1 (latency-sensitive message types) and forwards them to the SBC
constexpr size_t canHiPriReceiverTaskStackWords = 1000;
static Task<canHiPriReceiverTaskStackWords> canHiPriReceiverTask;

constexpr size_t canClockTaskStackWords = 400; // used to be 300 but RD had a stack overflow
static Task<canSenderTaskStackWords> canClockTask;

static CanMessageBuffer* volatile _ecv_null pendingMotionBuffers = nullptr;
static CanMessageBuffer* volatile lastMotionBuffer; // only valid when pendingBuffers != nullptr

#  if 0 // unused
static unsigned int numPendingMotionBuffers = 0;
#  endif

extern "C" [[noreturn]] void CanSenderLoop(void* /*unused*/) noexcept;
extern "C" [[noreturn]] void CanClockLoop(void* /*unused*/) noexcept;
extern "C" [[noreturn]] void CanReceiverLoop(void* /*unused*/) noexcept;
extern "C" [[noreturn]] void CanHiPriReceiverLoop(void* /*unused*/) noexcept;

// Status LED handling

#  if SUPPORT_MULTICAST_DISCOVERY

// The STATUS LED is also used to identify one board among several visible to the user
static volatile uint32_t identInitialClocks = 0; // when we started identifying
static volatile uint32_t identTotalClocks = 0;	 // how many step clocks to identify for, zero means until cancelled
static volatile bool identifying = false;

void CanInterface::SetStatusLedIdentify(uint32_t seconds) noexcept
{
	identTotalClocks = seconds * StepClockRate;
	identInitialClocks = StepTimer::GetTimerTicks();
	identifying = true;
}

void CanInterface::SetStatusLedNormal() noexcept
{
	identifying = false;
}

#  endif

// This is called only from the CAN clock loop, so inline
static inline void UpdateLed(uint32_t stepClocks) noexcept
{
#  if SUPPORT_MULTICAST_DISCOVERY
	if (identifying)
	{
		if (identTotalClocks != 0 && stepClocks - identInitialClocks >= identTotalClocks)
		{
			identifying = false; // stop identifying
		}
		else
		{
			// Blink the LED fast. This function gets called every 200ms, so that's the fastest we can blink it without
			// having another task do it.
			reprap.GetPlatform().InvertDiagLed();
			return;
		}
	}
#  endif

	// Blink the LED at about 1Hz. Duet 3 expansion boards will blink in sync when they have established clock sync with
	// us.
	reprap.GetPlatform().SetDiagLed((stepClocks & (1u << 19)) != 0);
}

static void InitReceiveFilters() noexcept
{
	// All received frames are delivered to FIFO 0 (and forwarded to the SBC) except a few latency-sensitive message
	// types, which we route to FIFO 1 so a dedicated high-priority task can forward them to the SBC with minimal delay.
	// Filter elements are evaluated in order and the first match wins, so the high-priority filters must come first.
	constexpr uint32_t msgTypeMask = CanId::MessageTypeMask << CanId::MessageTypeShift;
	can0dev->SetExtendedFilterElement(
		0, rxBufferIndexResponse, (uint32_t)CanMessageType::event << CanId::MessageTypeShift, msgTypeMask);
	can0dev->SetExtendedFilterElement(
		1, rxBufferIndexResponse, (uint32_t)CanMessageType::enterTestMode << CanId::MessageTypeShift, msgTypeMask);
	can0dev->SetExtendedFilterElement(2,
									  rxBufferIndexResponse,
									  (uint32_t)CanMessageType::inputStateChangedV1 << CanId::MessageTypeShift,
									  msgTypeMask);
	can0dev->SetExtendedFilterElement(3,
									  rxBufferIndexResponse,
									  (uint32_t)CanMessageType::inputStateChangedV2 << CanId::MessageTypeShift,
									  msgTypeMask);

	// Receive all remaining frames addressed to us (requests and responses) in FIFO 0
	can0dev->SetExtendedFilterElement(4,
									  rxBufferIndexRequest,
									  CanInterface::GetCanAddress() << CanId::DstAddressShift,
									  CanId::BoardAddressMask << CanId::DstAddressShift);

	// Receive all broadcast messages also in FIFO 0
	can0dev->SetExtendedFilterElement(5,
									  rxBufferIndexRequest,
									  CanId::BroadcastAddress << CanId::DstAddressShift,
									  CanId::BoardAddressMask << CanId::DstAddressShift);
#  ifdef DUET3_ATE
	// Also respond to requests addressed to board 0 so we can update firmware on ATE boards
	can0dev->SetExtendedFilterElement(6,
									  RxBufferIndexRequest,
									  CanId::MasterAddress << CanId::DstAddressShift,
									  (CanId::BoardAddressMask << CanId::DstAddressShift) | CanId::ResponseBit);
#  endif
}

// This is the function called by the transmit event handler when the message marker is nonzero
void TxCallback(uint8_t marker, CanId /*id*/, uint16_t timeStamp) noexcept
{
	if (marker == currentTimeSyncMarker)
	{
		timeSyncTxTimeStamp = timeStamp;
		gotTimeSyncTxTimeStamp = true;
		++goodTimeStamps;
	}
	else
	{
		++badTimeStamps;
	}
}

void CanInterface::Init() noexcept
{
	CanMessageBuffer::Init(numCanBuffers);
	pendingMotionBuffers = nullptr;

#  if SAME70
	SetPinFunction(APIN_CAN1_TX, CAN1TXPinPeriphMode);
	SetPinFunction(APIN_CAN1_RX, CAN1RXPinPeriphMode);
#	if DUAL_CAN
	SetPinFunction(APIN_CAN0_TX, CAN0PinPeriphMode);
	SetPinFunction(APIN_CAN0_RX, CAN0PinPeriphMode);
#	endif
	pmc_enable_upll_clock(); // configure_mcan sets up PCLK5 to be the UPLL divided by something, so make sure the UPLL
							 // is running
#  elif SAME5x
	SetPinFunction(CanRxPin, CanPinsMode);
	SetPinFunction(CanTxPin, CanPinsMode);
#  else
#	error Unsupported MCU
#  endif

	// Initialise the CAN hardware
	CanTiming timing{};
	timing.SetDefaults(CanTiming::DefaultCanBitRate);
#  if false
	uint32_t bitRateMultiplier = 1;
	timing.EnableBrs(bitRateMultiplier);
#	if SUPPORT_BRS
	{
		AtomicCriticalSectionLocker lock;
		fastDataRate = bitRateMultiplier - 1;															// TODO allow this to be configured
		dTseg1MinusOne = timing.dTseg1;
	}
#	endif
#  endif

	can0dev = CanDevice::Init(0, CanDeviceNumber, can0Config, can0Memory, timing, nullptr);
	InitReceiveFilters();
	can0dev->Enable();

	// The CAN hardware is initialised with the default timing but the interface is not enabled by default: canEnabled
	// is false, so the CAN clock task will not broadcast time sync messages until the SBC sends an EnableCAN request.
	canEnabled = false;

	CanMotion::Init();

	// Create the task that sends CAN messages
	canClockTask.Create(CanClockLoop, "CanClock", nullptr, TaskPriority::CanClockPriority);
	canSenderTask.Create(CanSenderLoop, "CanSender", nullptr, TaskPriority::CanSenderPriority);
	canReceiverTask.Create(CanReceiverLoop, "CanReceiver", nullptr, TaskPriority::CanReceiverPriority);
	canHiPriReceiverTask.Create(CanHiPriReceiverLoop, "CanHiPri", nullptr, TaskPriority::CanHiPriReceiverPriority);

#  if DUAL_CAN
	timing.SetDefaults(250'000);
	can1dev = CanDevice::Init(1, SecondaryCanDeviceNumber, can1Config, can1Memory, timing, nullptr);
	can1dev->SetShortFilterElement(
		0, CanDevice::RxBufferNumber::fifo0, 0, 0); // set up a filter to receive all messages in FIFO 0
	can1dev->SetExtendedFilterElement(0, CanDevice::RxBufferNumber::fifo0, 0, 0);
	can1dev->Enable();
#  endif
}

void CanInterface::Shutdown() noexcept
{
	canClockTask.TerminateAndUnlink();
	canSenderTask.TerminateAndUnlink();
	canReceiverTask.TerminateAndUnlink();
	canHiPriReceiverTask.TerminateAndUnlink();

	if (can0dev != nullptr)
	{
		can0dev->DeInit();
		can0dev = nullptr;
	}
}

CanAddress CanInterface::GetCanAddress() noexcept
{
	return myAddress;
}

// Allocate a CAN request ID
// Currently we reserve the top bit of the 12-bit request ID so that CanRequestIdAcceptAlways is distinct from any
// genuine request ID. Currently we use a single RID sequence for all destination addresses. In future we may use a
// separate sequence for each address. The message buffer is provided so that if the board is not known, we can use the
// buffer to send a message to it to announce ourselves, but this is not yet implemented
CanRequestId CanInterface::AllocateRequestId(CanAddress /*destination*/, CanMessageBuffer* /*buf*/) noexcept
{
	static uint16_t s_rid = 0;

	const CanRequestId rslt = s_rid & CanRequestIdMask;
	++s_rid;
	return rslt;
}

// Allocate a CAN message buffer, throw if failed
CanMessageBuffer* CanInterface::AllocateBuffer() THROWS(CanException)
{
	CanMessageBuffer* const _ecv_null buf = CanMessageBuffer::Allocate();
	if (buf == nullptr)
	{
		throw CanException(NoCanBufferMessage);
	}
	return _ecv_not_null(buf);
}

void CanInterface::CheckCanAddress(uint32_t address) THROWS(CanException)
{
	if (address == 0 || address > CanId::MaxCanAddress)
	{
		throw CanException("CAN address out of range");
	}
}

uint16_t CanInterface::GetTimeStampCounter() noexcept
{
	return can0dev->ReadTimeStampCounter();
}

#  if !SAME70

uint16_t CanInterface::GetTimeStampPeriod() noexcept
{
	return can0dev->GetTimeStampPeriod();
}

#  endif

uint32_t CanInterface::Convert16bitReceivedTimeStampTo32bits(uint16_t ts) noexcept
{
	const uint32_t now = StepTimer::GetTimerTicks();
	const uint16_t delay = (uint16_t)now - ts;

	// A timestamp more than 10ms old is not late, it is wrong: either the message sat somewhere it
	// should not have, or the board's clock has not been synchronised yet. Reporting now instead
	// gives up the correction the timestamp exists for, which costs the message's own latency;
	// trusting it would place the event up to a whole 16-bit period away
	return (delay < MillisToStepClocks(10)) ? now - (uint32_t)delay : now;
}

// Send a message on the CAN FD channel and record any errors
static void SendCanMessage(CanDevice::TxBufferNumber whichBuffer, uint32_t timeout, CanMessageBuffer& buffer) noexcept
{
	const uint32_t cancelledId = can0dev->SendMessage(whichBuffer, timeout, &buffer);
	if (cancelledId != 0)
	{
		++txTimeouts[(unsigned int)whichBuffer];
		lastCancelledId = cancelledId;
	}
}

// This task picks up motion messages and sends them
extern "C" [[noreturn]] void CanSenderLoop(void* /*unused*/) noexcept
{
	for (;;)
	{
		{
			// In main board mode this task sends urgent messages concerning motion
			for (;;)
			{
				CanMessageBuffer* const _ecv_null urgentMessage = CanMotion::GetUrgentMessage();
				if (urgentMessage != nullptr)
				{
					SendCanMessage(txBufferIndexUrgent, maxUrgentSendWait, *urgentMessage);
				}
				else if (pendingMotionBuffers != nullptr)
				{
					CanMessageBuffer* buf = nullptr;
					{
						const TaskCriticalSectionLocker lock;
						buf = _ecv_not_null(pendingMotionBuffers);
						pendingMotionBuffers = buf->next;
#  if 0 // unused
						--numPendingMotionBuffers;
#  endif
					}

					// Send the message
					SendCanMessage(txBufferIndexMotion, maxMotionSendWait, *buf);
					reprap.GetPlatform().OnProcessingCanMessage();

#  ifdef CAN_DEBUG
					// Display a debug message too
					debugPrintf("CCCR %08" PRIx32 ", PSR %08" PRIx32 ", ECR %08" PRIx32 ", TXBRP %08" PRIx32
								", TXBTO %08" PRIx32 ", st %08" PRIx32 "\n",
								MCAN1->MCAN_CCCR,
								MCAN1->MCAN_PSR,
								MCAN1->MCAN_ECR,
								MCAN1->MCAN_TXBRP,
								MCAN1->MCAN_TXBTO,
								GetAndClearStatusBits());
					buf->msg.DebugPrint();
					delay(50);
					debugPrintf("CCCR %08" PRIx32 ", PSR %08" PRIx32 ", ECR %08" PRIx32 ", TXBRP %08" PRIx32
								", TXBTO %08" PRIx32 ", st %08" PRIx32 "\n",
								MCAN1->MCAN_CCCR,
								MCAN1->MCAN_PSR,
								MCAN1->MCAN_ECR,
								MCAN1->MCAN_TXBRP,
								MCAN1->MCAN_TXBTO,
								GetAndClearStatusBits());
#  endif
					// Free the message buffer.
					CanMessageBuffer::Free(buf);
				}
				else
				{
					break;
				}
			}
			TaskBase::TakeIndexed(NotifyIndices::CanSender, Mutex::TimeoutUnlimited);
		}
	}
}

extern "C" [[noreturn]] void CanClockLoop(void* /*unused*/) noexcept
{
	CanMessageBuffer buf;
	uint32_t lastWakeTime = xTaskGetTickCount();
	uint32_t lastTimeSent = 0;
	uint32_t lastRealTimeSent = 0;
#  if !SAME70
	uint16_t lastTimeSyncTxPreparedStamp = 0;
#  endif

	for (;;)
	{
		// Don't broadcast time sync messages until the CAN interface has been enabled by the SBC.
		if (!canEnabled)
		{
			TaskBase::TakeIndexed(NotifyIndices::CanClock, Mutex::TimeoutUnlimited);
			lastWakeTime = xTaskGetTickCount(); // reset the wake time so we don't try to catch up on missed intervals
			continue;
		}

		auto* const msg = buf.SetupBroadcastMessage<CanMessageTimeSync>(CanInterface::GetCanAddress());
		msg->fastDataRate = fastDataRate;
		msg->tseg1Minus1 = dTseg1MinusOne;
		msg->lastTimeSent = lastTimeSent;
		msg->lastTimeAcknowledgeDelay = 0; // assume we don't have the transmit delay available

		currentTimeSyncMarker = ((currentTimeSyncMarker + 1) & 0x0F) | 0xA0;
		buf.marker = currentTimeSyncMarker;
		buf.reportInFifo = 1;

		if (gotTimeSyncTxTimeStamp)
		{
			// Calculate the delay in sending the last time sync message, in step clocks
#  if SAME70
			// On the SAME70 the step clock is also the external time stamp counter
			const uint32_t timeSyncTxDelay = (timeSyncTxTimeStamp - (uint16_t)lastTimeSent) & 0xFFFF;
#  else
			// On the SAME5x the time stamp counter counts CAN bit times. The step clock is the CAN clock divided by 64.
			const uint32_t timeSyncTxDelay = ((uint32_t)((timeSyncTxTimeStamp - lastTimeSyncTxPreparedStamp) & 0xFFFF) *
											  CanInterface::GetTimeStampPeriod()) >>
											 6;
#  endif
			peakTimeSyncTxDelay = std::max(timeSyncTxDelay, peakTimeSyncTxDelay);

			// Occasionally on the SAME70 we get very large delays reported. These delays are not genuine.
			if (timeSyncTxDelay < maxTimeSyncDelay)
			{
				msg->lastTimeAcknowledgeDelay = timeSyncTxDelay;
			}
			gotTimeSyncTxTimeStamp = false;
		}

		msg->isPrinting = false; // TODO remove or set this later when we have a way to know if we are printing or not

		// Send the real time just once a second unless we also need to send the movement delay
		const auto realTime = (uint32_t)reprap.GetPlatform().GetDateTime();
		const StepTimer::Ticks newMovementDelay = StepTimer::CheckMovementDelayIncreased();
		if (newMovementDelay != 0)
		{
			msg->realTime = realTime;
			lastRealTimeSent = realTime;
			msg->movementDelay = newMovementDelay;
		}
		else if (realTime != lastRealTimeSent)
		{
			msg->realTime = realTime;
			lastRealTimeSent = realTime;
			buf.dataLength = CanMessageTimeSync::SizeWithRealTime;
		}
		else
		{
			buf.dataLength = CanMessageTimeSync::SizeWithoutRealTime; // send a short message to save CAN bandwidth
		}

#  if SAME70
		lastTimeSent = StepTimer::GetTimerTicks();
#  else
		{
			AtomicCriticalSectionLocker lock;
			lastTimeSent = StepTimer::GetTimerTicksWhenInterruptsDisabled();
			lastTimeSyncTxPreparedStamp = CanInterface::GetTimeStampCounter();
		}
#  endif
		msg->timeSent = lastTimeSent;
		SendCanMessage(txBufferIndexTimeSync, 0, buf);
		++timeSyncMessagesSent;

		UpdateLed(lastTimeSent);

		// Delay until it is time again
		vTaskDelayUntil(&lastWakeTime, canClockIntervalMillis);

		// Check that the message was sent and get the time stamp
		if (can0dev->IsSpaceAvailable(txBufferIndexTimeSync,
									  0)) // if the buffer is free already then the message was sent
		{
			can0dev->PollTxEventFifo(TxCallback);
		}
		else
		{
			(void)can0dev->IsSpaceAvailable(txBufferIndexTimeSync, maxTimeSyncSendWait); // free the buffer
			can0dev->PollTxEventFifo(TxCallback);										 // empty the fifo
			gotTimeSyncTxTimeStamp = false; // ignore any values read from it
		}
	}
}

// Members of namespace CanInterface, and associated local functions

// Add a buffer to the end of the send queue
void CanInterface::SendMotion(CanMessageBuffer* buf) noexcept
{
	buf->next = nullptr;
#  if 0
	buf->msg.moveLinear.DebugPrint();
#  endif
	{
		const TaskCriticalSectionLocker lock;

		if (pendingMotionBuffers == nullptr)
		{
			pendingMotionBuffers = buf;
		}
		else
		{
			lastMotionBuffer->next = buf;
		}
		lastMotionBuffer = buf;
#  if 0 // unused
		++numPendingMotionBuffers;
#  endif
	}

	canSenderTask.Give(NotifyIndices::CanSender);
}

#  if 0 // not currently used

// Get the number of motion messages waiting to be sent through the Tx fifo
unsigned int CanInterface::GetNumPendingMotionMessages() noexcept
{
	return can0dev->NumTxMessagesPending(TxBufferIndexMotion) + numPendingMotionBuffers;
}

#  endif

// Send a CAN request that originated from the SBC. 'buf' has already been populated by the SBC interface (CAN id,
// payload and flags). If a reply is expected (replyType != 0xFFFF) we allocate a request ID, write it into the first 12
// bits of the message data and register the request so that the response can be matched back to the SBC's txToken. The
// send is non-blocking.
void CanInterface::SendCanRequest(CanMessageBuffer& buf, uint16_t txToken, CanMessageType replyType) noexcept
{
	bool noReplyPossible = false;
	if (can0dev == nullptr || !canEnabled)
	{
		// CAN is not enabled (the device exists from Init(), but the SBC has not sent EnableCAN, or
		// has disabled the bus again). Nothing reaches the bus and the SBC has no other way to find
		// out, so the send is refused rather than quietly transmitted on a bus that is officially
		// off. This is what makes a config.g that issues CAN-bound codes before M953 fail loudly.
		reprap.GetSbcInterface().ReportCanMessageSent(txToken, CanStatus::BusError);
		return;
	}

	if (replyType != CanMessageType::unusedMessageType)
	{
		// A reply is expected. The SBC must have set the request ID field to all-ones as a placeholder; verify that
		// before overriding it.
		if ((buf.msg.generic.requestId & CanRequestIdMask) != CanRequestIdMask)
		{
			reprap.GetPlatform().MessageF(WarningMessage,
										  "Dropped SBC CAN request type %u: request ID placeholder not 0xFFF\n",
										  (unsigned int)buf.id.MsgType());
			reprap.GetSbcInterface().ReportCanMessageSent(txToken, CanStatus::BusError);
			return;
		}

		const CanAddress dest = buf.id.Dst();
		const CanRequestId rid = AllocateRequestId(dest, &buf); // TODO handle CanRequestIdNoReplyNeeded
		buf.msg.generic.requestId = rid;

		// Register the request so the reply can be matched back to the SBC's txToken
		{
			const TaskCriticalSectionLocker lock;
			const uint32_t now = millis();
			CanRequestMapping* slot = nullptr;
			for (CanRequestMapping& m : pendingRequests)
			{
				// Silent expiry of stale entries that never got a reply
				if (m.active && now - m.whenStarted >= UsualResponseTimeout)
				{
					m.active = false;
				}
				if (slot == nullptr && !m.active)
				{
					slot = &m;
					break;
				}
			}
			if (slot != nullptr)
			{
				slot->active = true;
				slot->board = dest;
				slot->rid = rid;
				slot->txToken = txToken;
				slot->replyType = replyType;
				slot->whenStarted = now;
				slot->fragmentsReceived = 0;
			}
			else
			{
				// The request is still sent, but nothing can match its reply back to the SBC. Saying so
				// is what lets the caller fail now rather than wait out a reply that cannot arrive
				noReplyPossible = true;
			}
		}
	}
	else
	{
		// No reply expected
		// TODO: check whether we need to set CanRequestIdNoReplyNeeded
	}

	// Non-blocking send
	// Technically this is blocking on the send itself but it doesn't block waiting for the response
	// Each buffer can hold a single message, before sending a CAN message, all the buffers and the next message in the
	// fifo are checked and the highest priority message is sent
	CanDevice::TxBufferNumber txBuffer{};
	switch (buf.id.MsgType())
	{
	case CanMessageType::movementLinearShaped:
		txBuffer = txBufferIndexMotion;
		break;
	default:
		txBuffer = txBufferIndexRequest;
		break;
	} // TODO: choose a different buffer for urgent requests
	const auto timeout = maxRequestSendWait; // TODO make this configurable per request type
	SendCanMessage(txBuffer, timeout, buf);

	// Accepted by the CAN controller, which is as much as this can say: SendMessage returns once the
	// peripheral has taken the message, not once it is on the wire
	reprap.GetSbcInterface().ReportCanMessageSent(txToken, noReplyPossible ? CanStatus::NoBuffer : CanStatus::Ok);
	reprap.GetPlatform().OnProcessingCanMessage();
}

// Find an in-flight SBC-originated request matching a received response. Returns nullptr if there is no match.
CanInterface::CanRequestMapping* CanInterface::FindPendingRequest(CanAddress src, CanRequestId rid) noexcept
{
	const TaskCriticalSectionLocker lock;
	for (CanRequestMapping& m : pendingRequests)
	{
		if (m.active && m.board == src && m.rid == rid)
		{
			return &m;
		}
	}
	return nullptr;
}

// Free a pending request slot and release any reassembly buffer it holds
void CanInterface::ReleasePendingRequest(CanRequestMapping* mapping) noexcept
{
	const TaskCriticalSectionLocker lock;
	mapping->active = false;
}

// Send a response to an expansion board and free the buffer
void CanInterface::SendResponseNoFree(CanMessageBuffer& buf) noexcept
{
	SendCanMessage(txBufferIndexResponse, maxResponseSendWait, buf);
}

// Send a broadcast message and free the buffer
void CanInterface::SendBroadcastNoFree(CanMessageBuffer& buf) noexcept
{
	if (can0dev != nullptr)
	{
		SendCanMessage(txBufferIndexBroadcast, maxResponseSendWait, buf);
	}
}

// Send a request message with no reply expected, and don't free the buffer. Used to send emergency stop messages.
void CanInterface::SendMessageNoReplyNoFree(CanMessageBuffer& buf) noexcept
{
	if (can0dev != nullptr)
	{
		SendCanMessage(txBufferIndexBroadcast, maxResponseSendWait, buf);
	}
}

#  if DUAL_CAN

uint32_t CanInterface::SendPlainMessageNoFree(CanMessageBuffer& buf, uint32_t timeout) noexcept
{
	return (can1dev != nullptr) ? can1dev->SendMessage(CanDevice::TxBufferNumber::fifo, timeout, &buf) : 0;
}

bool CanInterface::ReceivePlainMessage(CanMessageBuffer* _ecv_null buf, uint32_t timeout) noexcept
{
	return can1dev != nullptr && can1dev->ReceiveMessage(CanDevice::RxBufferNumber::fifo0, timeout, buf);
}

#  endif

// The CanReceiver task
extern "C" [[noreturn]] void CanReceiverLoop(void* /*unused*/) noexcept
{
	CanMessageBuffer buf;
	for (;;)
	{
		if (can0dev->ReceiveMessage(rxBufferIndexRequest, TaskBase::TimeoutUnlimited, &buf))
		{
			if (reprap.Debug(Module::CAN))
			{
				buf.DebugPrint("Rx0:");
			}

			CommandProcessor::ProcessReceivedMessage(buf);
		}
	}
}

// The high-priority CanReceiver task. It drains FIFO 1 (latency-sensitive message types) and forwards them to the SBC.
extern "C" [[noreturn]] void CanHiPriReceiverLoop(void* /*unused*/) noexcept
{
	CanMessageBuffer buf;
	for (;;)
	{
		if (can0dev->ReceiveMessage(rxBufferIndexResponse, TaskBase::TimeoutUnlimited, &buf))
		{
			if (reprap.Debug(Module::CAN))
			{
				buf.DebugPrint("Rx1:");
			}

			CommandProcessor::ProcessReceivedMessage(buf);
		}
	}
}

void CanInterface::WakeAsyncSender() noexcept
{
	if (inInterrupt())
	{
		canSenderTask.GiveFromISR(NotifyIndices::CanSender);
	}
	else
	{
		canSenderTask.Give(NotifyIndices::CanSender);
	}
}

void CanInterface::WakeAsyncSenderFromIsr() noexcept
{
	canSenderTask.GiveFromISR(NotifyIndices::CanSender);
}

void CanInterface::Diagnostics(const StringRef& reply) noexcept
{
	reply.copy("=== CAN ===");
	// If the user runs M122 after an emergency stop, can0dev will be null
	if (can0dev == nullptr)
	{
		reply.lcat("Disabled");
	}
	else
	{
		CanDevice::CanStats stats{};
		can0dev->GetAndClearStats(stats);
		reply.lcatf("Messages queued %u, received %u, lost %u, "
					"errs %u, boc %u\n",
					stats.messagesQueuedForSending,
					stats.messagesReceived,
					stats.messagesLost,
					stats.protocolErrors,
					stats.busOffCount);
	}

	reply.lcatf("Longest wait %" PRIu32 "ms for reply type %u, peak Tx sync delay %" PRIu32 ", free buffers %u (min %u)"
				// debug
				", ts %u/%u/%u"
				// end debug
				,
				longestWaitTime,
				longestWaitMessageType,
				peakTimeSyncTxDelay,
				CanMessageBuffer::GetFreeBuffers(),
				CanMessageBuffer::GetAndClearMinFreeBuffers()
				// debug
				,
				timeSyncMessagesSent,
				goodTimeStamps,
				badTimeStamps
				// end debug
	);

	reply.lcat("Tx timeouts");
	char c = ' ';
	for (unsigned int& txt : txTimeouts)
	{
		reply.catf("%c%u", c, txt);
		txt = 0;
		c = ',';
	}

	if (lastCancelledId != 0)
	{
		CanId id{};
		id.SetReceivedId(lastCancelledId);
		lastCancelledId = 0;
		reply.catf(" last cancelled message type %u dest %u", (unsigned int)id.MsgType(), id.Dst());
	}

	longestWaitTime = 0;
	longestWaitMessageType = 0;
	peakTimeSyncTxDelay = 0;
	timeSyncMessagesSent = goodTimeStamps = badTimeStamps = 0;
}

// Enable or disable the CAN interface in master board mode. When enabled, the CAN clock task starts broadcasting time
// sync messages.
void CanInterface::EnableCan(bool enable) noexcept
{
	if (enable != canEnabled)
	{
		canEnabled = enable;
		if (enable)
		{
			// Wake the CAN clock task so it starts broadcasting time sync messages
			canClockTask.Give(NotifyIndices::CanClock);
		}
		// When disabling, the CAN clock task will notice canEnabled is false on its next iteration and block until
		// re-enabled.
	}
}

// Configure our own (master) CAN timing, or report the current timing into 'reply' if doSetTiming is false.
// This is used when the SBC forwards a setAddressAndNormalTiming message addressed to the master (oldAddress 0).
void CanInterface::ConfigLocalCanTiming(const CanTiming& timing, bool doSetTiming, const StringRef& reply) noexcept
{
	if (doSetTiming)
	{
		{
			const AtomicCriticalSectionLocker lock;
			fastDataRate = timing.dataRateMultiplier; // disable BRS
			dTseg1MinusOne = timing.dTseg1;
		}
		delay(50); // allow any existing transactions to complete
		can0dev->ChangeLocalCanTiming(timing);
	}
	else
	{
		// Report the current timing back to the caller, which forwards it to the SBC as a CAN response
		ReportCanTiming(reply);
	}
}

void CanInterface::ReportCanTiming(const StringRef& reply) noexcept
{
	CanTiming timing{};
	can0dev->GetLocalCanTiming(timing);
	reply.printf("CAN arbitration speed %.1fkbps, sample point %.2f, jump width %.2f, ",
				 (double)((float)CanTiming::ClockFrequency / (float)(1000 * timing.period)),
				 (double)((float)(timing.nTseg1 + 1) / (float)timing.period),
				 (double)((float)timing.nJumpWidth / (float)timing.period));
	if (fastDataRate == 0)
	{
		reply.cat("bit rate switching disabled");
	}
	else
	{
		const uint32_t dataPeriod = timing.period / (fastDataRate + 1);
		reply.catf("data speed %.1fkbps, sample point %.2f, jump width %.2f",
				   (double)((float)CanTiming::ClockFrequency / (float)(1000 * dataPeriod)),
				   (double)((float)(timing.dTseg1 + 1) / (float)dataPeriod),
				   (double)((float)timing.dJumpWidth / (float)dataPeriod));
	}
}

#  if DUAL_CAN

CanId CanInterface::ODrive::ArbitrationId(DriverId driver, uint8_t cmd) noexcept
{
	const auto arbitrationId = (driver.boardAddress << 5) + cmd;
	CanId canId{};
	canId.SetReceivedId(arbitrationId);
	return canId;
}

CanMessageBuffer* _ecv_null CanInterface::ODrive::PrepareSimpleMessage(const DriverId /*driver*/,
																	   const StringRef& /*reply*/) noexcept
{
	// Detect any early return conditions
	if (can1dev == nullptr)
	{
		return nullptr;
	}
	CanMessageBuffer* _ecv_null buf = CanMessageBuffer::Allocate();
	if (buf == nullptr)
	{
		return nullptr;
	}

	// Build the message
	buf->marker = 0;
	buf->extId = false; // ODrive uses 11-bit IDs
	buf->fdMode = false;
	buf->useBrs = false;
	buf->dataLength = 0;
	buf->reportInFifo = false;

	return buf;
}

void CanInterface::ODrive::FlushCanReceiveHardware() noexcept
{
	while (CanInterface::ReceivePlainMessage(nullptr, 0))
	{
	}
}

bool CanInterface::ODrive::GetExpectedSimpleMessage(CanMessageBuffer* buf,
													const DriverId driver,
													const uint8_t cmd,
													const StringRef& reply) noexcept
{
	const CanId expectedId = ArbitrationId(driver, cmd);

	int count = 0;
	bool ok = true;
	do
	{
		ok = ReceivePlainMessage(buf);
		count++;
	} while (ok && buf->id != expectedId && count < 5);

	ok = ok && buf->id == expectedId;

	if (!ok)
	{
		reply.printf("Message not received");
	}

	return ok;
}
#  endif // DUAL_CAN

#endif

// End
