// SBC-side communication loop: the C++ port of DuetControlServer/Link/LinkService.cs.
//
// Owns an SbcTransfer and runs the transfer loop on a pinned real-time thread. All communication with
// the caller goes through two lock-free RingBuffers rather than callbacks, so the loop thread never
// blocks on -- or executes -- foreign code. That is what lets DuetControlServer drive this from C#
// without managed allocation, locks or GC pauses landing on a SCHED_FIFO thread mid-transfer.
//
//   Outbound ring (caller -> loop): messages, CAN messages, CAN enable, emergency stop, reset.
//                                   Drained while staging the next transfer.
//   Inbound ring  (loop -> caller): incoming messages/CAN responses, code buffer updates, connection
//                                   state changes, request completions, diagnostics.
//
// Requests that the caller awaits carry a request id; the loop answers with a RequestCompleted event
// quoting that id. See LinkEvents.h for the record formats.
//
// Firmware update is deliberately NOT a ring record: the binaries are megabytes. The caller stages
// them with RequestFirmwareUpdate() and the loop takes them over for the duration of the flash,
// reporting completion through the usual request id.
#pragma once

#include "LinkEvents.h"
#include "SbcTransfer.h"
#include <Config/Configuration.h>
#include <DuetSpiProtocol/MessageFormats.h>
#include <Platform/RingBuffer.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <span>
#include <string>
#include <thread>
#include <vector>

namespace Duet::Sbc
{

	class SbcInterface
	{
	  public:
		// Called after each full transfer that served a request, with the measured latency from
		// RequestTransfer() to transfer completion (nanoseconds). This is the jitter metric, and it stays
		// a direct callback on purpose: it is consumed natively by the jitter harness, and routing it
		// through the ring would add exactly the scheduling noise it exists to measure.
		using RequestServedCallback = std::function<void(int64_t latencyNs)>;

		explicit SbcInterface(const Config& config);
		~SbcInterface();

		SbcInterface(const SbcInterface&) = delete;
		SbcInterface& operator=(const SbcInterface&) = delete;
		SbcInterface(SbcInterface&&) = delete;
		SbcInterface& operator=(SbcInterface&&) = delete;

		// Connect to the firmware (blocks until the first transfer succeeds). Throws on failure.
		void Connect();

		// Start the transfer loop on its own pinned real-time thread.
		void Start();

		// Stop the transfer loop and join the thread.
		void Stop();

		// --- Outbound: queue work for the transfer loop ---
		//
		// These return false if the outbound ring is full, i.e. the loop is not draining it. The caller
		// must treat that as an error rather than silently losing the message.
		bool QueueMessage(uint32_t messageFlags, const char* message, size_t length);
		bool QueueCanMessage(uint16_t txToken,
							 uint16_t msgType,
							 uint16_t replyType,
							 uint8_t dstAddress,
							 bool isResponse,
							 const uint8_t* payload,
							 size_t payloadLength);
		bool QueueEnableCan(bool enable, uint32_t requestId = kNoRequestId);

		// Queue a prepared move. Called from the motion thread, which must never block, so this
		// returns false rather than waiting when the ring is full - the caller stops preparing.
		bool QueueScheduleMove(std::span<const uint8_t> packet);

		// Post an inbound event from a thread other than the interface thread. The ring serialises
		// its producers with a mutex held only for the copy, so this does not wait on a transfer -
		// but it does mean the interface thread can briefly wait on this one, which is why the
		// motion thread's real-time priority has to stay below the interface thread's.
		void PostEventFromOtherThread(InboundEventType type,
									  const void* header,
									  size_t headerLength,
									  const void* tail = nullptr,
									  size_t tailLength = 0);

		// How much of the outbound ring is free, as a fraction. The motion engine stops preparing
		// moves below a threshold rather than filling the ring and having a move refused halfway.
		[[nodiscard]] bool OutboundHasHeadroom() const;
		void RequestEmergencyStop(uint32_t requestId = kNoRequestId);
		void RequestReset(uint32_t requestId = kNoRequestId);

		// Stage a firmware update. The buffers must stay valid until the matching RequestCompleted event
		// arrives. Returns false if an update is already in progress.
		bool RequestFirmwareUpdate(const uint8_t* iap,
								   size_t iapLength,
								   const uint8_t* firmware,
								   size_t firmwareLength,
								   uint16_t firmwareCrc16,
								   uint32_t requestId);

		// Force a transfer without new data (records the request timestamp for jitter measurement).
		void RequestTransfer();

		// --- Inbound: drained by the caller ---
		RingBuffer& Inbound() noexcept { return m_inbound; }

		// Block until at least one inbound event is available, the timeout elapses, or Stop() is called.
		// Returns true if events are (probably) available. Only the single consumer may call this.
		//
		// The interface thread only performs the wake-up syscall while a consumer is actually parked, so
		// a busy dispatcher costs the real-time thread nothing.
		bool WaitForInbound(int timeoutMs);

		void SetRequestServedCallback(RequestServedCallback cb) { m_onRequestServed = std::move(cb); }

		SbcTransfer& Transfer() noexcept { return m_transfer; }

	  private:
		void Execute();
		void ProcessPacket(const proto::PacketHeader& packet);
		void StageOutgoing();
		void HandleCommand(const uint8_t* record, uint32_t length);
		void MarkRequest();
		void PerformFirmwareUpdate();

		// Inbound helpers. All of these are called on the interface thread and never allocate.
		void PostEvent(InboundEventType type,
					   const void* header,
					   size_t headerLength,
					   const void* tail = nullptr,
					   size_t tailLength = 0);
		void PostLog(LogLevel level, const char* text, size_t length);
		void PostLog(LogLevel level, const std::string& text) { PostLog(level, text.data(), text.size()); }
		void CompleteRequest(uint32_t requestId,
							 RequestResult result,
							 const char* error = nullptr,
							 size_t errorLength = 0);

		Config m_config;
		SbcTransfer m_transfer;
		std::thread m_thread;
		std::atomic<bool> m_stop{false};

		// Sized to hold a comfortable backlog of full-size transfers so a scheduling hiccup on the managed
		// dispatcher thread cannot make the interface thread drop incoming data.
		RingBuffer m_inbound;
		RingBuffer m_outbound;

		// When the most recent transfer completed, in the step-time model's local timebase. Written
		// and read only by the interface thread.
		int64_t m_lastTransferNs = 0;

		// Emergency stop / reset are latched rather than queued: they are unconditional and must survive a
		// full transfer buffer, retrying on each iteration until they are written.
		std::atomic<bool> m_pendingEmergencyStop{false};
		std::atomic<uint32_t> m_emergencyStopRequestId{kNoRequestId};
		std::atomic<bool> m_pendingReset{false};
		std::atomic<uint32_t> m_resetRequestId{kNoRequestId};

		// Firmware update staging. `_pendingFirmwareUpdate` is checked on every loop iteration, so it is
		// an atomic flag: the mutex is only taken once an update is actually pending.
		std::atomic<bool> m_pendingFirmwareUpdate{false};
		std::mutex m_firmwareMutex;
		const uint8_t* m_iapData = nullptr;
		size_t m_iapLength = 0;
		const uint8_t* m_firmwareData = nullptr;
		size_t m_firmwareLength = 0;
		uint16_t m_firmwareCrc16 = 0;
		uint32_t m_firmwareRequestId = kNoRequestId;

		// Jitter measurement: timestamp of the first RequestTransfer since the last completed transfer
		std::atomic<int64_t> m_pendingRequestNs{0};

		// Connection state, so ConnectionLost/ConnectionEstablished are reported on transitions only
		bool m_wasConnected = false;

		// Wake-up channel for a consumer parked in WaitForInbound. `_consumerWaiting` keeps the interface
		// thread from issuing a write() syscall when nobody is parked.
		int m_inboundEventFd = -1;
		std::atomic<bool> m_consumerWaiting{false};

		RequestServedCallback m_onRequestServed;
	};

} // namespace Duet::Sbc
