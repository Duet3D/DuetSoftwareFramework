/*
 * SbcInterface.h
 *
 *  Created on: 29 Mar 2019
 *      Authors: Christian
 */

#ifndef SRC_SBC_SBCINTERFACE_H_
#define SRC_SBC_SBCINTERFACE_H_

#include <RepRapFirmware.h>

#include <span>

#if HAS_SBC_INTERFACE

#  include "RTOSIface/RTOSIface.h"

#  include "SbcMessageFormats.h"

#  include "DataTransfer.h"
#  include <Platform/OutputMemory.h>

class Platform;
class SerialCDC;

class GCodeBuffer;

// #define TRACK_FILE_CODES			// Uncomment this to enable code <-> code reply tracking for the file G-code channel

// G-Code input class for an SPI channel
class SbcInterface
{
  public:
	SbcInterface() noexcept;

	// The Init method must be called prior to calling any of the other methods.
	void Init() noexcept;
	[[noreturn]] void TaskLoop() noexcept;
	void Diagnostics(const StringRef& reply) noexcept;
	[[nodiscard]] bool IsConnected() const noexcept { return m_isConnected; }

	void EventOccurred(bool timeCritical = false) const noexcept; // Called when a new event has happened. It can
																  // optionally start off a new transfer immediately
	GCodeResult HandleM576(GCodeBuffer& gb, const StringRef& reply) noexcept; // Set the SPI communication parameters

#  if SUPPORTS_SBC_OVER_USB
	void RequestUsbSwitch(
		SerialCDC* dev, unsigned int usbDevIndex) noexcept; // Request a switch to USB transport (called from main task)
#  endif

	DataTransfer& GetDataTransfer() noexcept { return m_transfer; }

	bool FillBuffer(GCodeBuffer& gb) noexcept; // Try to fill up the G-code buffer with the next available G-code

	void ReportPause() noexcept; // Report that the print has been paused

	void HandleGCodeReply(MessageType mt, const char* reply) noexcept;	  // accessed by Platform
	void HandleGCodeReply(MessageType mt, OutputBuffer* buffer) noexcept; // accessed by Platform

	bool EnqueueCanResponse(const CANResponseHeader& header, const char* _ecv_null data) noexcept;

	// Tell the SBC that an endstop cut a move short. The controller stops the drives itself, but only
	// the SBC can say where they should have ended up, so it takes the trigger timestamp from here
	// and sends the revert. Called from the CAN receiver task
	bool ReportMotionStopped(uint32_t whenTriggered,
							 std::span<const duet::spi::protocol::MotionStoppedDriver> stopped) noexcept;
	void EnqueueCanTextReply(
		uint16_t txToken,
		CanRequestId requestId,
		const char* text) noexcept; // Forward a text reply to the SBC as one or more standardReply CAN responses

  private:
	DataTransfer m_transfer;
	volatile bool m_isConnected;
	TransferState m_state{};
	uint32_t m_numDisconnects, m_numTimeouts, m_numSbcTimeouts, m_lastTransferTime;

	volatile uint16_t m_rxPointer, m_txPointer, m_txEnd;
	volatile bool m_sendBufferUpdate;

	uint32_t m_iapRamAvailable{}; // must be at least 32Kb otherwise the SPI IAP can't work

#  if SUPPORTS_SBC_OVER_USB
	SerialCDC* m_pendingUsbDevice; // set from main task, read from SBC task
	unsigned int m_usbDeviceIndex; // index of the USB device used for SBC mode (for reinit on disconnect)
#  endif

	volatile OutputStack m_gcodeReply;
	Mutex m_gcodeReplyMutex;

	// Ring buffer of CAN responses waiting to be forwarded to the SBC. Producers are the CAN receiver tasks; the
	// consumer is the SBC task.
	static constexpr size_t NumCanResponseBuffers = 24;
	struct CanResponseBuffer
	{
		CANResponseHeader header;
		uint8_t payload[64];
	};
	CanResponseBuffer m_canResponseRing[NumCanResponseBuffers]{};
	volatile size_t m_canResponseHead,
		m_canResponseTail; // head = next slot to write, tail = next slot to read; empty when equal

	bool ProcessCanResponses() noexcept; // Write queued CAN responses into the current transfer

	// Ring of motion-stopped reports waiting to go to the SBC. Endstop stops are rare, so this is
	// much smaller than the CAN response ring
	static constexpr size_t NumMotionStoppedBuffers = 4;
	struct MotionStoppedBuffer
	{
		duet::spi::protocol::MotionStoppedHeader header;
		duet::spi::protocol::MotionStoppedDriver drivers[duet::spi::protocol::MaxMotionStoppedDrivers];
	};
	MotionStoppedBuffer m_motionStoppedRing[NumMotionStoppedBuffers]{};
	volatile size_t m_motionStoppedHead, m_motionStoppedTail;

	bool ProcessMotionStopped() noexcept; // Write queued motion-stopped reports into the current transfer

#  ifdef TRACK_FILE_CODES
	volatile size_t fileCodesRead, fileCodesHandled, fileMacrosRunning, fileMacrosClosing;
#  endif

	void ExchangeData() noexcept; // Exchange data between RRF and the SBC
	[[noreturn]] void ReceiveAndStartIap(const char* iapChunk,
										 size_t length) noexcept; // Receive and start the IAP binary
	void InvalidateResources() noexcept;						  // Invalidate local resources on connection errors
};

#endif

#endif
