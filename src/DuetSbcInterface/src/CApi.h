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

	// --- Motion ---
	//
	// The motion engine runs on its own thread inside the library. These calls hand moves to it and
	// read back what it has done; none of them blocks on that thread, because the caller is a
	// garbage-collected runtime and the motion thread must not wait on one.

	// Machine description, pushed down from DuetControlServer. Mirrors Motion::MotionConfig; the
	// managed side builds the bytes and this copies them, so the struct is not repeated here.
	// Safe only while no move is in flight.
	int32_t DuetSbc_MotionConfigure(DuetSbcHandle* h, const void* config, int32_t length);

	// Start and stop the motion thread. `rtPriority` is a SCHED_FIFO priority, or 0 for the default
	// scheduler; it must be below the interface thread's, so that a late transfer never waits on a
	// move being prepared.
	int32_t DuetSbc_MotionStart(DuetSbcHandle* h, int32_t rtPriority);
	void DuetSbc_MotionStop(DuetSbcHandle* h);

	// 1 if the ring has room for another move. Advisory: it may have room again a moment later.
	int32_t DuetSbc_MotionCanAddMove(DuetSbcHandle* h, int32_t ring);

	// Queue a move. `moveParams` is a MoveParamsHeader followed by its two arrays; see
	// Motion/MoveParams.h and its C# mirror. Returns 1 if queued, 0 if the caller must retry.
	int32_t DuetSbc_MotionSubmitMove(DuetSbcHandle* h, const void* moveParams, int32_t length);

	// Motor positions in microsteps and the step-clock time they were taken at. Returns how many
	// were written. Reads a snapshot rather than the live state, so it never stalls the motion
	// thread and never tears.
	int32_t DuetSbc_MotionGetMotorPositions(DuetSbcHandle* h, int32_t* stepsOut, int32_t count, uint32_t* whenTicks);

	// Where one drive was at a given step-clock time, and where it was when its current move began.
	// This is what undoing an endstop overshoot needs: the position at the instant the switch fired
	// rather than the one the stop message caught. Only this side can answer it, because only this
	// side holds the segment chain the move was planned into.
	//
	// `usedTimestamp` reports whether the answer came from evaluating at `whenTicks` or from where the
	// drive is now. It is 0 when `whenTicks` is 0 - what a board using the older message reports - and
	// when the step-clock fit is not yet trusted. Falling back leaves the overshoot uncorrected, which
	// the caller has to be able to tell.
	//
	// Returns 1 on success, 0 if `drive` is out of range.
	int32_t DuetSbc_MotionGetPositionAt(DuetSbcHandle* h, int32_t drive, uint32_t whenTicks,
										int32_t* positionOut, int32_t* positionAtMoveStartOut,
										int32_t* usedTimestampOut);

	// Force motor positions, after homing or a move that stopped early.
	void DuetSbc_MotionSetMotorPositions(DuetSbcHandle* h, uint32_t driveMask, const int32_t* positions, int32_t count);

	// State DCS decides from its own bookkeeping each cycle, stored for the motion thread to read.
	void DuetSbc_MotionSetRingState(DuetSbcHandle* h, int32_t ring, int32_t shouldStartMove, int32_t waitingForEmpty);

	uint32_t DuetSbc_MotionGetScheduledMoves(DuetSbcHandle* h, int32_t ring);
	uint32_t DuetSbc_MotionGetCompletedMoves(DuetSbcHandle* h, int32_t ring);
	// Submissions refused because the queue was full. Non-zero means DCS ignored a retry.
	uint32_t DuetSbc_MotionGetSubmissionsDropped(DuetSbcHandle* h);

	// --- Step clock ---
	//
	// The SBC has no step clock of its own: it models the controller's, from the MasterClock packet
	// the controller sends every transfer. Move start times are in that timebase, so how well the
	// model tracks is how well moves land - a move scheduled against a model that is out by more
	// than the preparation margin arrives late.

	// The current step-clock reading, in the controller's ticks.
	uint32_t DuetSbc_GetStepClockTicks(DuetSbcHandle* h);

	// How the model is tracking. Mirrors StepTimer::ClockStats; see LinkEvents.cs's counterpart for
	// the managed shape. `synced` is 0 until the fit has enough samples to be trusted, during which
	// the model still works but is anchored to the last sample at the nominal rate.
	using DuetSbcClockStats = struct
	{
		double driftPpm;			 // fitted rate minus nominal, in parts per million
		uint32_t numSamples;		 // samples in the current fit
		uint32_t peakResidualNs;	 // largest deviation of a sample from the fit since startup
		uint32_t numBackwardClamps;	 // times a new fit would have made the reading go backwards
		uint32_t numRejectedSamples; // samples discarded as implausible
		int32_t synced;
	};

	void DuetSbc_GetClockStats(DuetSbcHandle* h, DuetSbcClockStats* stats);

	// Destroy the instance (stops the loop first).
	void DuetSbc_Destroy(DuetSbcHandle* h);

#ifdef __cplusplus
}
#endif
