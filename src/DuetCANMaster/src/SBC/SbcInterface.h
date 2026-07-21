/*
 * SbcInterface.h
 *
 *  Created on: 29 Mar 2019
 *      Authors: Christian
 */

#ifndef SRC_SBC_SBCINTERFACE_H_
#define SRC_SBC_SBCINTERFACE_H_

#include <RepRapFirmware.h>

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
	bool IsConnected() const noexcept { return isConnected; }

	void EventOccurred(bool timeCritical = false) const noexcept; // Called when a new event has happened. It can
																  // optionally start off a new transfer immediately
	GCodeResult HandleM576(GCodeBuffer& gb, const StringRef& reply) noexcept; // Set the SPI communication parameters

#  if SUPPORTS_SBC_OVER_USB
	void RequestUsbSwitch(
		SerialCDC* dev, unsigned int usbDevIndex) noexcept; // Request a switch to USB transport (called from main task)
#  endif

	DataTransfer& GetDataTransfer() noexcept { return transfer; }

	bool FillBuffer(GCodeBuffer& gb) noexcept; // Try to fill up the G-code buffer with the next available G-code

	void ReportPause() noexcept; // Report that the print has been paused

	void HandleGCodeReply(MessageType mt, const char* reply) noexcept;	  // accessed by Platform
	void HandleGCodeReply(MessageType mt, OutputBuffer* buffer) noexcept; // accessed by Platform

	bool EnqueueCanResponse(const CANResponseHeader& header, const char* _ecv_null data) noexcept;
	void EnqueueCanTextReply(
		uint16_t txToken,
		CanRequestId requestId,
		const char* text) noexcept; // Forward a text reply to the SBC as one or more standardReply CAN responses

  private:
	DataTransfer transfer;
	volatile bool isConnected;
	TransferState state{};
	uint32_t numDisconnects, numTimeouts, numSbcTimeouts, lastTransferTime;

	volatile uint16_t rxPointer, txPointer, txEnd;
	volatile bool sendBufferUpdate;

	uint32_t iapRamAvailable{}; // must be at least 32Kb otherwise the SPI IAP can't work

#  if SUPPORTS_SBC_OVER_USB
	SerialCDC* pendingUsbDevice; // set from main task, read from SBC task
	unsigned int usbDeviceIndex; // index of the USB device used for SBC mode (for reinit on disconnect)
#  endif

	volatile OutputStack gcodeReply;
	Mutex gcodeReplyMutex;

	// Ring buffer of CAN responses waiting to be forwarded to the SBC. Producers are the CAN receiver tasks; the
	// consumer is the SBC task.
	static constexpr size_t NumCanResponseBuffers = 24;
	struct CanResponseBuffer
	{
		CANResponseHeader header;
		uint8_t payload[64];
	};
	CanResponseBuffer canResponseRing[NumCanResponseBuffers]{};
	volatile size_t canResponseHead,
		canResponseTail; // head = next slot to write, tail = next slot to read; empty when equal

	bool ProcessCanResponses() noexcept; // Write queued CAN responses into the current transfer

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
