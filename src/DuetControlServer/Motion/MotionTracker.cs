using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Native;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Motion;

/// <summary>
/// What the native motion engine has reported about the moves this side submitted
/// </summary>
/// <remarks>
/// <para>
/// The engine runs asynchronously: a move is submitted, planned against its neighbours, and executed
/// some time later. This is where what came back is kept, so that the link dispatcher can record it
/// without knowing anything about move generation and <see cref="MotionService"/> can consult it
/// without having to talk to the dispatcher.
/// </para>
/// <para>
/// The endpoints are the part that matters for correctness. Moves are submitted as absolute machine
/// positions and planned as a delta from the previous move's endpoints, so this side has to know
/// where the machine actually is. Normally it does, because it chose the endpoint; a move that
/// watches endstops can stop short, and then the engine reports where the drives really ended up.
/// Submitting another move before applying that would move the machine by the whole discrepancy
/// </para>
/// </remarks>
/// <param name="logger">Logger</param>
internal sealed class MotionTracker(ILogger<MotionTracker> logger)
{
    /// <summary>
    /// Per-ring state
    /// </summary>
    private sealed class RingState
    {
        /// <summary>Id of the last move the engine reported as completed</summary>
        public uint LastCompletedMoveId;

        /// <summary>The engine's running total at that point</summary>
        public uint CompletedMoves;

        /// <summary>Whether <see cref="CompletedMoves"/> holds a reading yet</summary>
        public bool HaveCompletedMoves;

        /// <summary>Endpoints reported after a move that could stop short, in microsteps</summary>
        public readonly int[] Endpoints = new int[MotionLimits.MaxAxesPlusExtruders];

        /// <summary>Whether <see cref="Endpoints"/> holds a reading that has not been applied yet</summary>
        public bool EndpointsPending;

        /// <summary>Waits pending on a move's retirement, each keyed by the move id it waits for</summary>
        public readonly List<Waiter> Waiters = [];

        /// <summary>Lowest id the last sweep failed, one past the move the stop left standing</summary>
        public uint FirstFailedMoveId;

        /// <summary>Highest id the last sweep failed, the last one submitted when it ran</summary>
        public uint LastFailedMoveId;

        /// <summary>Whether the two above describe a sweep that has happened</summary>
        public bool HaveFailedRange;
    }

    /// <summary>
    /// A wait pending on a move's retirement
    /// </summary>
    /// <param name="MoveId">Move id the wait is for</param>
    /// <param name="Tcs">
    /// Completion source released when that move has retired, with true if it ran and false if a
    /// stop dropped it
    /// </param>
    private sealed record Waiter(uint MoveId, TaskCompletionSource<bool> Tcs);

    private readonly RingState[] _rings = CreateRings();

    private readonly Lock _lock = new();

    private static RingState[] CreateRings()
    {
        RingState[] rings = new RingState[MotionLimits.MaxRings];
        for (int i = 0; i < rings.Length; i++)
        {
            rings[i] = new RingState();
        }
        return rings;
    }

    /// <summary>
    /// Record that a move finished executing
    /// </summary>
    /// <param name="ring">Ring the move was queued on</param>
    /// <param name="moveId">Id this side gave the move</param>
    /// <param name="completedMoves">The ring's running total of completed moves</param>
    /// <remarks>
    /// The running total is what makes a dropped event visible. Events travel through a fixed-size
    /// ring that the native side drops from when it fills, so "the move I am waiting for never
    /// completed" and "the event saying so was lost" look the same without it
    /// </remarks>
    public void MoveCompleted(int ring, uint moveId, uint completedMoves)
    {
        if (!IsValidRing(ring))
        {
            return;
        }

        lock (_lock)
        {
            RingState state = _rings[ring];
            if (state.HaveCompletedMoves && completedMoves != state.CompletedMoves + 1)
            {
                logger.LogWarning(
                    "Missed {Count} move completion event(s) on ring {Ring}: total went from {Previous} to {Current}",
                    completedMoves - state.CompletedMoves - 1, ring, state.CompletedMoves, completedMoves);
            }

            state.LastCompletedMoveId = moveId;
            state.CompletedMoves = completedMoves;
            state.HaveCompletedMoves = true;

            // Moves retire in submission order, so every wait at or before this id is released -
            // which is also what covers a wait whose own completion event was dropped
            for (int i = state.Waiters.Count - 1; i >= 0; i--)
            {
                Waiter waiter = state.Waiters[i];
                if ((int)(waiter.MoveId - moveId) <= 0)
                {
                    state.Waiters.RemoveAt(i);
                    waiter.Tcs.TrySetResult(true);
                }
            }
        }
    }

    /// <summary>
    /// Release every wait for a move a stop dropped
    /// </summary>
    /// <param name="ring">Ring that was stopped</param>
    /// <param name="lastSurvivingMoveId">Id of the last move the stop left standing</param>
    /// <param name="lastSubmittedMoveId">Id of the last move submitted when the stop ran</param>
    /// <remarks>
    /// <para>
    /// Purged DDAs and discarded submissions are reported to nobody: the engine cannot send one
    /// event per dropped id, because the inbound ring drops events when it fills and a purge of a
    /// whole ring is the moment it would. So the boundary is applied here instead, in one sweep,
    /// with the same signed-distance comparison <see cref="MoveCompleted"/> uses. Every id above
    /// the survivor and at or below the last one submitted is one the machine will never make, so
    /// a wait on it ends now rather than when some later move happens to complete.
    /// </para>
    /// <para>
    /// The range is remembered as well as swept, because a wait may be registered after the stop:
    /// the standstill wait asks for the last submitted id, and a deferred code chained behind
    /// another reaches its own wait later still. Only the latest range is kept - a second stop
    /// starts above the first one's survivor, and by the time it runs every wait the first swept
    /// has been answered
    /// </para>
    /// </remarks>
    public void FailAfter(int ring, uint lastSurvivingMoveId, uint lastSubmittedMoveId)
    {
        if (!IsValidRing(ring))
        {
            return;
        }

        lock (_lock)
        {
            RingState state = _rings[ring];
            if ((int)(lastSubmittedMoveId - lastSurvivingMoveId) <= 0)
            {
                // The stop dropped nothing this side was holding an id for
                return;
            }

            state.FirstFailedMoveId = lastSurvivingMoveId + 1;
            state.LastFailedMoveId = lastSubmittedMoveId;
            state.HaveFailedRange = true;

            for (int i = state.Waiters.Count - 1; i >= 0; i--)
            {
                Waiter waiter = state.Waiters[i];
                if (IsInFailedRange(state, waiter.MoveId))
                {
                    state.Waiters.RemoveAt(i);
                    waiter.Tcs.TrySetResult(false);
                }
            }
        }
    }

    /// <summary>
    /// Whether a move id falls in the range the last sweep failed
    /// </summary>
    /// <param name="state">Ring state, whose lock the caller holds</param>
    /// <param name="moveId">Id to test</param>
    /// <returns>True if the move was dropped by a stop</returns>
    private static bool IsInFailedRange(RingState state, uint moveId)
        => state.HaveFailedRange
           && (int)(moveId - state.FirstFailedMoveId) >= 0
           && (int)(moveId - state.LastFailedMoveId) <= 0;

    /// <summary>
    /// Whether the given move has been reported as completed
    /// </summary>
    /// <param name="ring">Ring the move was queued on</param>
    /// <param name="moveId">Id of the move to check</param>
    /// <returns>True if the move has retired</returns>
    public bool HasRetired(int ring, uint moveId)
    {
        if (!IsValidRing(ring))
        {
            return true;
        }

        lock (_lock)
        {
            RingState state = _rings[ring];
            return (state.HaveCompletedMoves && (int)(moveId - state.LastCompletedMoveId) <= 0)
                   || IsInFailedRange(state, moveId);
        }
    }

    /// <summary>
    /// Wait until the given move has retired, whether it ran or a stop dropped it
    /// </summary>
    /// <param name="ring">Ring the move was queued on</param>
    /// <param name="moveId">Id of the move to wait for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True once the move has completed, false if a stop dropped it</returns>
    /// <remarks>
    /// Move ids are handed out in submission order and skip zero on wrap, so whether a move has
    /// retired is a signed-distance comparison against the last reported id, and a wait for a move
    /// that already retired completes immediately. Every id terminates: one the machine makes
    /// completes, and one a stop dropped is failed by <see cref="FailAfter"/>, so nothing waits on
    /// a move that will never be reported. <see cref="Invalidate"/> cancels every waiter: the moves
    /// they were waiting for are gone with the link
    /// </remarks>
    public Task<bool> WaitForRetirementAsync(int ring, uint moveId, CancellationToken cancellationToken)
    {
        if (!IsValidRing(ring))
        {
            return Task.FromResult(true);
        }

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Waiter waiter = new(moveId, tcs);
        lock (_lock)
        {
            RingState state = _rings[ring];
            if (state.HaveCompletedMoves && (int)(moveId - state.LastCompletedMoveId) <= 0)
            {
                return Task.FromResult(true);
            }
            if (IsInFailedRange(state, moveId))
            {
                return Task.FromResult(false);
            }
            state.Waiters.Add(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                lock (_lock)
                {
                    _rings[ring].Waiters.Remove(waiter);
                }
                tcs.TrySetCanceled(cancellationToken);
            });

            // The registration must not outlive the wait, or a purged move's registration would
            // accumulate on a long-lived token
            tcs.Task.ContinueWith(static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                                  registration, CancellationToken.None,
                                  TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return tcs.Task;
    }

    /// <summary>
    /// Record that a move was rejected or could not be executed
    /// </summary>
    /// <param name="ring">Ring the move was submitted to</param>
    /// <param name="moveId">Id this side gave the move</param>
    /// <param name="error">Why it failed</param>
    public void MoveFailed(int ring, uint moveId, NativeMovementError error)
    {
        logger.LogError("Move {MoveId} on ring {Ring} failed: {Error}", moveId, ring, error);
        if (!IsValidRing(ring))
        {
            return;
        }

        // A move that will not be executed still has to end the waits on it. Only this id: the
        // moves submitted before it are still the engine's to run and report
        lock (_lock)
        {
            RingState state = _rings[ring];
            for (int i = state.Waiters.Count - 1; i >= 0; i--)
            {
                Waiter waiter = state.Waiters[i];
                if (waiter.MoveId == moveId)
                {
                    state.Waiters.RemoveAt(i);
                    waiter.Tcs.TrySetResult(false);
                }
            }
        }
    }

    /// <summary>
    /// The number of moves the given ring has completed, or 0 if it has not reported yet
    /// </summary>
    /// <param name="ring">Ring to read</param>
    /// <returns>Number of completed moves</returns>
    public uint GetCompletedMoves(int ring)
    {
        if (!IsValidRing(ring))
        {
            return 0;
        }

        lock (_lock)
        {
            return _rings[ring].CompletedMoves;
        }
    }

    /// <summary>
    /// The id of the last move the given ring reported as completed, or 0 if it has not reported yet
    /// </summary>
    /// <param name="ring">Ring to read</param>
    /// <returns>Move id</returns>
    public uint GetLastCompletedMoveId(int ring)
    {
        if (!IsValidRing(ring))
        {
            return 0;
        }

        lock (_lock)
        {
            return _rings[ring].LastCompletedMoveId;
        }
    }

    /// <summary>
    /// Forget everything, because the link went down and the engine is no longer the same one
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            foreach (RingState state in _rings)
            {
                state.LastCompletedMoveId = 0;
                state.CompletedMoves = 0;
                state.HaveCompletedMoves = false;
                state.EndpointsPending = false;
                state.HaveFailedRange = false;
                state.FirstFailedMoveId = state.LastFailedMoveId = 0;
                Array.Clear(state.Endpoints);

                foreach (Waiter waiter in state.Waiters)
                {
                    waiter.Tcs.TrySetCanceled();
                }
                state.Waiters.Clear();
            }
        }
    }

    private bool IsValidRing(int ring)
    {
        if (ring >= 0 && ring < _rings.Length)
        {
            return true;
        }

        logger.LogWarning("Discarding motion event for out-of-range ring {Ring}", ring);
        return false;
    }
}
