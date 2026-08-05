/*
 * MotionService.h
 *
 * The motion engine as a running thing: the rings, the thread that spins them, and the two places
 * that thread meets the rest of the process.
 *
 * The engine below this is all synchronous - a ring spins when someone calls Spin. This is what
 * calls it, on its own thread, and what connects it to DuetControlServer at both ends: moves come
 * down through SubmitMove, and what happened to them goes back as inbound events on the same ring
 * the SPI transfer loop uses.
 *
 * Threading is the point of the design. There are three threads and none of them may block another:
 *
 *   - the SPI interface thread, which holds SCHED_FIFO and must never wait for anything;
 *   - this motion thread, which prepares moves ahead of when they are due;
 *   - whichever managed thread calls the CApi, which may be stopped by a garbage collection at any
 *     moment.
 *
 * So moves arrive through a lock-free ring rather than a call, and positions leave through a
 * seqlock-protected snapshot rather than a lock. RepRapFirmware guards the same state by turning
 * off the step interrupt; there is no step interrupt here, and that mutual exclusion had to be
 * replaced with something rather than deleted.
 */

#ifndef SRC_SBC_MOTIONSERVICE_H_
#define SRC_SBC_MOTIONSERVICE_H_

#include "LinkScheduleMoveSink.h"

#include <DuetSpiProtocol/MessageFormats.h>
#include <Motion/MotionConfig.h>
#include <Motion/MoveParams.h>
#include <Movement/DDARing.h>
#include <Platform/RingBuffer.h>

#include <atomic>
#include <span>
#include <thread>

namespace Duet::Sbc
{
	class SbcInterface;

	class MotionService
	{
	public:
		explicit MotionService(SbcInterface& link);
		MotionService(const MotionService&) = delete;
		MotionService& operator=(const MotionService&) = delete;
		MotionService(MotionService&&) = delete;
		MotionService& operator=(MotionService&&) = delete;
		~MotionService();

		// Reserve the permanent arena and build the rings. Once only, before Start.
		bool Init();

		// Start and stop the motion thread. `rtPriority` is the SCHED_FIFO priority to run at, or 0
		// to leave the thread at the default policy; it must be below the interface thread's.
		void Start(int rtPriority);
		void Stop();

		// Replace the machine description. Safe only while no move is in flight, which is DCS's
		// responsibility - it holds movement locked while it reconfigures, exactly as M92 requires
		// in the firmware.
		static void Configure(const Motion::MotionConfig& config);

		// --- Called from the managed side -------------------------------------------------------

		// True if `ring` has room for another move. Advisory: by the time the caller acts the ring
		// may have retired a move and made more room, which is the harmless direction to be wrong in.
		[[nodiscard]] bool CanAddMove(unsigned int ring) const;

		// Queue a move. Copies the record, so the caller's buffer is free on return. False means the
		// submission ring is full and the caller must retry - never a silent drop.
		bool SubmitMove(std::span<const uint8_t> params);

		// The most recent position snapshot: motor positions in microsteps and the step-clock time
		// they were taken at. Lock-free, so a garbage collection in the caller cannot stall motion.
		// Returns the number of drives written.
		size_t GetMotorPositions(std::span<int32_t> positions, uint32_t *whenTicks) const;

		// Force motor positions, after homing or a move that was cut short.
		static void SetMotorPositions(uint32_t driveMask, std::span<const int32_t> positions);

		// Where a drive was at a given step-clock time, and where it was when the current move began.
		//
		// This is the one question only this side can answer: it planned the motion and holds the
		// segment chain, so it can evaluate the profile at an instant that has already passed. That is
		// what undoing an endstop overshoot needs - the position at the moment the switch fired, not
		// the position the stop message happened to catch.
		//
		// `usedTimestamp` says whether the answer came from evaluating at `whenTicks` or from where
		// the drive is now. It is false when `whenTicks` is zero, which is what a board using the
		// older message reports, and when the step-clock fit is not yet trusted - the two clocks then
		// share nothing but their rate, so evaluating at a controller timestamp would extrapolate far
		// outside the move. Falling back leaves the overshoot, which is a small error rather than a
		// wild one. The caller has to know which it got, because it decides what to do about it.
		//
		// Returns false if `drive` is out of range.
		static bool GetPositionAt(size_t drive, uint32_t whenTicks, int32_t& position,
								  int32_t& positionAtMoveStart, bool& usedTimestamp);

		// What DCS decides from its own state each cycle, stored rather than called: the motion
		// thread must never wait on the managed side to answer a question.
		void SetRingState(unsigned int ring, bool shouldStartMove, bool waitingForEmpty);

		[[nodiscard]] uint32_t GetScheduledMoves(unsigned int ring) const;
		[[nodiscard]] uint32_t GetCompletedMoves(unsigned int ring) const;
		[[nodiscard]] uint32_t GetSubmissionsDropped() const
		{
			return m_submissionsDropped.load(std::memory_order_relaxed);
		}

	private:
		// Both rings, in the order they are spun. SUPPORT_ASYNC_MOVES gives a second one for moves
		// that a second motion system owns; it exists even when nothing uses it.
		static constexpr unsigned int numRings = Motion::maxRings;

		void Run();
		void SpinOnce();
		void DrainSubmissions();
		void PublishPositions();

		// True if `record` is long enough for the header and for the two trailing arrays that the
		// header's numDrives claims. Checked at both ends: on submission so the caller hears about
		// it, and again on the motion thread so nothing reaches a DDA unvalidated.
		[[nodiscard]] static bool IsWellFormedSubmission(std::span<const uint8_t> record) noexcept;

		static void OnMoveRetired(const DDA& dda, void *context) noexcept;

		// Hand the controller's stop report to DCS unchanged, so it can decide what the drives should
		// have ended up at. Raw rather than a conclusion: this side knows where the drives were, but
		// only DCS knows what the move was for and what should be done about it.
		void PostMotionStopped(uint32_t whenTriggered,
							   std::span<const duet::spi::protocol::MotionStoppedDriver> drivers);

		void PostMoveCompleted(unsigned int ring, uint32_t moveId);
		void PostMoveFailed(unsigned int ring, uint32_t moveId, MovementError error);

		SbcInterface *m_link;
		LinkScheduleMoveSink m_sink;

		DDARing m_rings[numRings];

		// What a ring's retirement callback is given. The DDA does not know which ring holds it, so
		// the index travels in the context rather than being guessed at the far end.
		struct RetirementContext
		{
			MotionService *service = nullptr;
			unsigned int ring = 0;
		};
		RetirementContext m_retirementContext[numRings];

		// Moves waiting to be taken up by the motion thread. A ring rather than a call, so that
		// SubmitMove never blocks the caller and never blocks the motion thread either.
		RingBuffer m_submissions;
		std::atomic<uint32_t> m_submissionsDropped{0};

		// Per-ring state that DCS sets and the motion thread reads. Plain atomics: the answer is
		// allowed to be one cycle stale, and waiting for a fresh one would be far worse.
		struct RingState
		{
			std::atomic<bool> shouldStartMove{false};
			std::atomic<bool> waitingForEmpty{false};
		};
		RingState m_ringState[numRings];

		// The position snapshot, published once per spin and read without a lock. `sequence` is odd
		// while a write is in progress; a reader that sees it change across the read tries again.
		struct PositionSnapshot
		{
			uint32_t whenTicks = 0;
			int32_t positions[maxAxesPlusExtruders]{};
		};
		mutable std::atomic<uint32_t> m_snapshotSequence{0};
		PositionSnapshot m_snapshot;

		std::thread m_thread;
		std::atomic<bool> m_stop{false};
		bool m_initialised = false;
	};
}

#endif /* SRC_SBC_MOTIONSERVICE_H_ */
