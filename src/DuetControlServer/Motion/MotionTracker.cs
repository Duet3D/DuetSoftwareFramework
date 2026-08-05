using System;
using System.Threading;
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
    }

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
        }
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
                Array.Clear(state.Endpoints);
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
