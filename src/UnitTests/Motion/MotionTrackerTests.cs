using System;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Native;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// What the tracker does with the three motion events the native engine reports
/// </summary>
/// <remarks>
/// The endpoint handling is the part that matters for correctness. Moves are submitted as absolute
/// machine positions and planned as a delta from the previous move's endpoints, so a correction that
/// is dropped, applied twice, or applied to the wrong ring moves the machine by the difference
/// </remarks>
[TestFixture]
public class MotionTrackerTests
{
    private static MotionTracker NewTracker() => new(NullLogger<MotionTracker>.Instance);

    [Test]
    public void CompletedMovesAreRecordedPerRing()
    {
        MotionTracker tracker = NewTracker();
        tracker.MoveCompleted(0, moveId: 7, completedMoves: 1);
        tracker.MoveCompleted(1, moveId: 99, completedMoves: 4);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.GetLastCompletedMoveId(0), Is.EqualTo(7u));
            Assert.That(tracker.GetCompletedMoves(0), Is.EqualTo(1u));
            Assert.That(tracker.GetLastCompletedMoveId(1), Is.EqualTo(99u));
            Assert.That(tracker.GetCompletedMoves(1), Is.EqualTo(4u));
        });
    }

    [Test]
    public void OutOfRangeRingsAreIgnored()
    {
        MotionTracker tracker = NewTracker();
        tracker.MoveCompleted(MotionLimits.MaxRings, moveId: 1, completedMoves: 1);
        tracker.MoveCompleted(-1, moveId: 2, completedMoves: 2);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.GetCompletedMoves(0), Is.EqualTo(0u));
            Assert.That(tracker.GetCompletedMoves(1), Is.EqualTo(0u));
        });
    }

    [Test]
    public void InvalidateForgetsEveryRing()
    {
        // The moves those totals refer to are gone with the link, so a move planned after the
        // reconnect must not be checked against them
        MotionTracker tracker = NewTracker();
        tracker.MoveCompleted(0, moveId: 3, completedMoves: 3);

        tracker.Invalidate();

        Assert.Multiple(() =>
        {
            Assert.That(tracker.GetCompletedMoves(0), Is.EqualTo(0u));
            Assert.That(tracker.GetLastCompletedMoveId(0), Is.EqualTo(0u));
        });
    }

    [Test]
    public void MoveFailedDoesNotDisturbTheCompletionTotals()
    {
        MotionTracker tracker = NewTracker();
        tracker.MoveCompleted(0, moveId: 1, completedMoves: 1);
        tracker.MoveFailed(0, moveId: 2, NativeMovementError.MoveDurationTooLong);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.GetCompletedMoves(0), Is.EqualTo(1u));
            Assert.That(tracker.GetLastCompletedMoveId(0), Is.EqualTo(1u));
        });
    }
}
