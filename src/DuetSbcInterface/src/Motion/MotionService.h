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

#ifndef SRC_MOTION_MOTIONSERVICE_H_
#define SRC_MOTION_MOTIONSERVICE_H_

#include <Motion/LinkScheduleMoveSink.h>

#include <DuetSpiProtocol/MessageFormats.h>
#include <Motion/MachineConfig.h>
#include <Motion/MotionSystem.h>
#include <Motion/MoveParams.h>
#include <Motion/DDARing.h>
#include <Platform/RingBuffer.h>

#include <atomic>
#include <span>
#include <thread>

namespace Duet::Sbc
{
	class LinkService;

	class MotionService
	{
	public:
		// Both rings, in the order they are spun. SUPPORT_ASYNC_MOVES gives a second one for moves
		// that a second motion system owns; it exists even when nothing uses it.
		static constexpr unsigned int numRings = Motion::maxRings;

		explicit MotionService(LinkService& link);
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
		void Configure(const Motion::MachineConfig& config);

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
		size_t GetLivePositions(std::span<int32_t> positions, uint32_t *whenTicks) const;

		// Ask for a feedhold: bring the machine to a controlled stop as early as the ring allows and
		// drop the moves after it. See DDARing::Feedhold for what "as early as the ring allows"
		// means and why it is not RepRapFirmware's search for a slow-enough junction.
		//
		// Queued for the motion thread rather than done here, for the reason SetMotorPositions is:
		// dropping a move frees its segments, and the freelist is not thread-safe. So the answer
		// cannot come back from this call - GetFeedholdResult is where it appears, once the motion
		// thread has acted. `sequence` counts completed feedholds, so a caller reads it before
		// asking and waits for it to change.
		//
		// False means the request queue was full and nothing was asked for - never a silent drop.
		bool RequestFeedhold();

		// What the last feedhold did. `sequence` increments once per completed feedhold, including
		// one that found nothing it could stop before, which reports stopped = false.
		struct FeedholdResult
		{
			uint32_t sequence = 0;
			uint32_t firstPurgedMoveId = 0;
			uint32_t movesPurged = 0;
			bool stopped = false;
		};
		[[nodiscard]] FeedholdResult GetFeedholdResult() const;

		// Force motor positions, after homing or a move that was cut short.
		//
		// The rings are told as well as the trackers. A move is scheduled as the difference between
		// its own endpoint and the previous move's, so a position forced here that the ring never
		// heard about would be undone by the next move: it would travel the difference between where
		// the machine really is and where the last move meant to leave it. This is RepRapFirmware's
		// Move::ChangeEndpointsAfterHoming, which sets both for the same reason.
		//
		// Queued for the motion thread rather than applied here, like a move and for the same reason.
		// Adopting a position discards the drive's remaining segments, and the segment freelist is not
		// thread-safe; doing that from the caller's thread while the motion thread is retiring
		// segments of its own would corrupt it. The endstop correction forces a position in the middle
		// of a move, so this is the ordinary case rather than a corner of it. Applied within one pass
		// of the motion thread, before any move submitted after it.
		//
		// False means the queue is full and the position was not taken - never a silent drop.
		bool SetMotorPositions(uint32_t driveMask, std::span<const int32_t> positions);

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
		bool GetPositionAt(size_t drive, uint32_t whenTicks, int32_t& position,
						   int32_t& positionAtMoveStart, bool& usedTimestamp) const;

		// What DCS decides from its own state each cycle, stored rather than called: the motion
		// thread must never wait on the managed side to answer a question.
		void SetRingState(unsigned int ring, bool shouldStartMove, bool waitingForEmpty);

		[[nodiscard]] uint32_t GetScheduledMoves(unsigned int ring) const;
		[[nodiscard]] uint32_t GetCompletedMoves(unsigned int ring) const;

		// True while a submitted move has not yet been taken up by the motion thread.
		//
		// A ring's scheduled count only rises when the motion thread takes the move out of the
		// submission queue, so between SubmitMove returning and the next pass of that thread the
		// rings say the machine is idle while a move is already on its way to it. Anything asking
		// whether the machine has stopped has to ask this as well, or it is answered "yes" about a
		// move that has not started. DrainSubmissions consumes each record only after the ring has
		// counted it, so the two together never both say idle while a move exists.
		[[nodiscard]] bool HasPendingSubmissions() const { return !m_submissions.IsEmpty(); }

		// Positions the motion thread has adopted. The counterpart of what DCS believes it has sent:
		// the two diverging is the difference between a position that was queued and one that took
		// effect, which nothing else distinguishes.
		[[nodiscard]] uint32_t GetForcedPositionsApplied() const
		{
			return m_forcedPositionsApplied.load(std::memory_order_relaxed);
		}

		[[nodiscard]] uint32_t GetSubmissionsDropped() const
		{
			return m_submissionsDropped.load(std::memory_order_relaxed);
		}

		// Everything M122 reports about the motion engine, in one read.
		//
		// Counters rather than formatted text: DuetControlServer owns the wording of a reply, and
		// marshalling a string across the ABI so that the managed side could parse it back would be
		// the awkward way round. The step clock is already reported this way.
		struct Stats
		{
			uint32_t segmentsCreated;			// MoveSegments allocated since startup, never reused
			uint32_t movementDelayTicks;		// how far the movement timebase lags the raw step clock
			uint32_t submissionsDropped;		// moves refused because the submission queue was full
			uint32_t forcedPositionsApplied;	// positions the motion thread has adopted
			uint32_t droppedSchedulePackets;	// ScheduleMove packets the link refused: motion was lost
			DDARing::Stats rings[numRings];
		};

		[[nodiscard]] Stats GetStats() const;

		// Zero the error and underrun counters, as reporting them used to do by itself.
		void ResetStats();

	private:
		void Run();
		void SpinOnce();
		void DrainSubmissions();
		void DrainForcedPositions();
		void DrainFeedholds();
		void PublishPositions();

		// True if `record` is long enough for the header and for the two trailing arrays that the
		// header's numDrives claims. Checked at both ends: on submission so the caller hears about
		// it, and again on the motion thread so nothing reaches a DDA unvalidated.
		[[nodiscard]] static bool IsWellFormedSubmission(std::span<const uint8_t> record) noexcept;

		static void OnMoveRetired(const DDA& dda, void *context) noexcept;

		// Hand the controller's stop report to DCS unchanged, so it can decide what the drives should
		// have ended up at. Raw rather than a conclusion: this side knows where the drives were, but
		// only DCS knows what the move was for and what should be done about it.
		void PostMotionStopped(uint32_t whenTriggered, uint32_t moveId,
							   std::span<const duet::spi::protocol::MotionStoppedDriver> drivers);

		void PostMoveCompleted(unsigned int ring, uint32_t moveId);
		void PostMoveFailed(unsigned int ring, uint32_t moveId, MovementError error);

		LinkService *m_link;
		LinkScheduleMoveSink m_sink;

		// The machine, and the rings that plan for it. Owned here rather than reached through a
		// global, so that the engine has no static state and a second instance is a second machine
		// rather than the same one twice.
		Motion::MotionSystem m_move;
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

		// One position DCS has forced, waiting to be adopted by the motion thread
		struct ForcedPositions
		{
			uint32_t driveMask = 0;
			int32_t positions[maxAxesPlusExtruders]{};
		};

		// Forced positions waiting to be adopted. Drained before the submissions, so that a move
		// queued after a position was forced is planned from that position and not from the one it
		// replaced.
		RingBuffer m_forcedPositions;
		std::atomic<uint32_t> m_forcedPositionsApplied{0};

		// Feedhold requests waiting to be acted on, and what the last one did. Drained before the
		// forced positions and the submissions: a feedhold changes where the machine will come to
		// rest, so anything queued behind it has to be planned from that point and not from where
		// the discarded moves would have left it.
		RingBuffer m_feedholdRequests;
		mutable std::atomic<uint32_t> m_feedholdSequence{0};
		FeedholdResult m_feedholdResult;

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

			// What the drives were commanded to, which is where the last retired segment left them.
			// The planner resynchronises against this after a move is cut short, so it has to be the
			// commanded position and not an interpolated one.
			int32_t positions[maxAxesPlusExtruders]{};

			// Where the drives actually are, interpolated within the segment each is running. Only
			// for reporting - two meanings, kept apart, rather than one that has to serve both.
			int32_t livePositions[maxAxesPlusExtruders]{};
		};
		mutable std::atomic<uint32_t> m_snapshotSequence{0};
		PositionSnapshot m_snapshot;

		size_t ReadSnapshot(std::span<int32_t> positions, uint32_t *whenTicks,
							const int32_t (&source)[maxAxesPlusExtruders]) const;

		std::thread m_thread;
		std::atomic<bool> m_stop{false};
		bool m_initialised = false;
	};
}

#endif /* SRC_MOTION_MOTIONSERVICE_H_ */
