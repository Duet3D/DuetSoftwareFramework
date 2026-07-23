// C ABI wrapping the SBC interface so it can be consumed from C# via P/Invoke (or any other
// language). Kept intentionally small and free of C++ types on the boundary. Built into
// libduet_sbc.so.
//
// The design point is that the caller's threads and the SPI interface thread never block each other:
// work is exchanged through lock-free ring buffers, so the interface thread can hold SCHED_FIFO on an
// isolated core while the caller runs a garbage-collected runtime. Incoming events are drained with
// the zero-copy Peek/Consume pair; outgoing work is pushed with the Queue*/Request* calls.
//
// The record layouts carried by the rings are defined in LinkEvents.h and mirrored in C# by
// DuetControlServer/Link/Native/LinkEvents.cs.
//
// Threading:
//   - Queue*/Request*/RequestTransfer are safe to call from any thread, concurrently.
//   - PeekEvent/ConsumeEvent/WaitForEvent form a SINGLE-consumer API: exactly one thread may use them.
//   - Create/Connect/Start/Stop/Destroy are expected to be called from one owning thread.
#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C"
{
#endif

	using DuetSbcHandle = struct DuetSbcHandle;

	// Configuration passed across the ABI. Mirrors duet::sbc::Config. Any string may be null to use the
	// built-in default.
	using DuetSbcConfig = struct
	{
		const char* spiDevice;
		uint32_t spiFrequency;
		int32_t spiTransferMode;
		int32_t bufferSize;

		const char* gpioChipDevice;
		int32_t transferReadyPin;
		int32_t dataAvailablePin;
		int32_t sbcDataAvailablePin; // < 0 disables the scope-trigger output line

		int32_t isolateInterfaceThread; // bool
		int32_t isolatedCoreId;
		int32_t useRealtimeScheduling; // bool
		int32_t interfaceRtPriority;

		int32_t sbcConnectTimeout;
		int32_t sbcTransferTimeout;
		int32_t sbcConnectionTimeout;
		int32_t sbcConnectionKeepAliveInterval;
		int32_t maxSbcRetries;

		int32_t updateOnly; // bool: tolerate a newer-than-supported protocol version so it can be flashed
	};

	// Fill `config` with the default values.
	void DuetSbc_DefaultConfig(DuetSbcConfig* config);

	// Create an interface instance. Returns null on failure and writes an error message into
	// errorBuf (if non-null). The instance must be freed with DuetSbc_Destroy.
	DuetSbcHandle* DuetSbc_Create(const DuetSbcConfig* config, char* errorBuf, int32_t errorBufLen);

	// Connect to the firmware (blocking). Returns 0 on success, non-zero on failure (message in errorBuf).
	int32_t DuetSbc_Connect(DuetSbcHandle* h, char* errorBuf, int32_t errorBufLen);

	// Start / stop the transfer loop.
	void DuetSbc_Start(DuetSbcHandle* h);
	void DuetSbc_Stop(DuetSbcHandle* h);

	// --- Outbound: queue work for the transfer loop (any thread) ---

	// These return 0 on success and non-zero if the outbound ring is full (i.e. the message was NOT
	// queued and the caller must surface that rather than lose it).
	int32_t DuetSbc_QueueMessage(DuetSbcHandle* h, uint32_t flags, const char* message, int32_t length);
	int32_t DuetSbc_QueueCanMessage(DuetSbcHandle* h,
									uint16_t txToken,
									uint16_t msgType,
									uint16_t replyType,
									uint8_t dstAddress,
									int32_t isResponse,
									const uint8_t* payload,
									int32_t length);
	// requestId may be 0 for fire-and-forget; otherwise the outcome arrives as a RequestCompleted event.
	int32_t DuetSbc_QueueEnableCan(DuetSbcHandle* h, int32_t enable, uint32_t requestId);
	void DuetSbc_RequestEmergencyStop(DuetSbcHandle* h, uint32_t requestId);
	void DuetSbc_RequestReset(DuetSbcHandle* h, uint32_t requestId);

	// Stage a firmware update. `iap` and `firmware` must remain valid and pinned until the matching
	// RequestCompleted event arrives. Returns 0 on success, non-zero if an update is already running.
	int32_t DuetSbc_RequestFirmwareUpdate(DuetSbcHandle* h,
										  const uint8_t* iap,
										  int32_t iapLength,
										  const uint8_t* firmware,
										  int32_t firmwareLength,
										  uint16_t firmwareCrc16,
										  uint32_t requestId);

	// Ask for a transfer without new data (e.g. to flush a queued request promptly).
	void DuetSbc_RequestTransfer(DuetSbcHandle* h);

	// --- Inbound: drain events (single consumer thread only) ---

	// Point `data`/`length` at the next event record without copying it. Returns 1 if an event is
	// available, 0 otherwise. The pointer stays valid until the next DuetSbc_ConsumeEvent call.
	int32_t DuetSbc_PeekEvent(DuetSbcHandle* h, const uint8_t** data, int32_t* length);

	// Release the event most recently returned by DuetSbc_PeekEvent.
	void DuetSbc_ConsumeEvent(DuetSbcHandle* h);

	// Block until an event is available, the timeout elapses, or the loop is stopped.
	// Returns 1 if an event is (probably) available, 0 on timeout.
	int32_t DuetSbc_WaitForEvent(DuetSbcHandle* h, int32_t timeoutMs);

	// --- Diagnostics ---
	int32_t DuetSbc_GetProtocolVersion(DuetSbcHandle* h);
	double DuetSbc_GetMaxPinWaitMs(DuetSbcHandle* h);
	double DuetSbc_GetMaxFullTransferDelayMs(DuetSbcHandle* h);
	int32_t DuetSbc_GetTfrPinGlitches(DuetSbcHandle* h);
	int32_t DuetSbc_GetMissedEdges(DuetSbcHandle* h);
	int32_t DuetSbc_GetResyncCount(DuetSbcHandle* h);
	// Events dropped because the inbound ring was full (i.e. the consumer could not keep up).
	uint64_t DuetSbc_GetDroppedEvents(DuetSbcHandle* h);

	// Destroy the instance (stops the loop first).
	void DuetSbc_Destroy(DuetSbcHandle* h);

#ifdef __cplusplus
}
#endif
