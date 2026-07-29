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
    public void EndpointsAreTakenOnceAndOnlyForTheMaskedDrives()
    {
        MotionTracker tracker = NewTracker();
        int[] reported = new int[MotionLimits.MaxAxesPlusExtruders];
        reported[0] = 111;
        reported[1] = 222;
        reported[2] = 333;

        // Only X and Z, so the Y entry must be left as it was rather than overwritten with the
        // value that happened to be in the record
        tracker.EndpointsReported(0, moveId: 1, driveMask: 0b101, reported);

        int[] taken = new int[MotionLimits.MaxAxesPlusExtruders];
        Assert.That(tracker.TryTakeEndpoints(0, taken), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(taken[0], Is.EqualTo(111));
            Assert.That(taken[1], Is.EqualTo(0), "an unmasked drive keeps its previous endpoint");
            Assert.That(taken[2], Is.EqualTo(333));
        });

        // Taking rather than peeking: applying the same correction twice would move the machine by
        // the discrepancy a second time
        Assert.That(tracker.TryTakeEndpoints(0, taken), Is.False);
    }

    [Test]
    public void EndpointsDoNotLeakBetweenRings()
    {
        MotionTracker tracker = NewTracker();
        int[] reported = new int[MotionLimits.MaxAxesPlusExtruders];
        reported[0] = 4242;
        tracker.EndpointsReported(1, moveId: 1, driveMask: 0b1, reported);

        int[] taken = new int[MotionLimits.MaxAxesPlusExtruders];
        Assert.Multiple(() =>
        {
            Assert.That(tracker.TryTakeEndpoints(0, taken), Is.False, "ring 0 has nothing pending");
            Assert.That(tracker.TryTakeEndpoints(1, taken), Is.True);
        });
        Assert.That(taken[0], Is.EqualTo(4242));
    }

    [Test]
    public void ShortEndpointArraysAreAccepted()
    {
        // The event carries numDrives entries, which need not be the full drive space
        MotionTracker tracker = NewTracker();
        tracker.EndpointsReported(0, moveId: 1, driveMask: uint.MaxValue, new int[] { 5, 6, 7 });

        int[] taken = new int[MotionLimits.MaxAxesPlusExtruders];
        Assert.That(tracker.TryTakeEndpoints(0, taken), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(taken[0], Is.EqualTo(5));
            Assert.That(taken[2], Is.EqualTo(7));
            Assert.That(taken[3], Is.EqualTo(0));
        });
    }

    [Test]
    public void InvalidateDropsPendingEndpoints()
    {
        // The moves those endpoints refer to are gone with the link. Applying the reading to a move
        // planned after the reconnect would be a jump
        MotionTracker tracker = NewTracker();
        tracker.MoveCompleted(0, moveId: 3, completedMoves: 3);
        tracker.EndpointsReported(0, moveId: 3, driveMask: 0b1, new int[] { 900 });

        tracker.Invalidate();

        int[] taken = new int[MotionLimits.MaxAxesPlusExtruders];
        Assert.Multiple(() =>
        {
            Assert.That(tracker.TryTakeEndpoints(0, taken), Is.False);
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
