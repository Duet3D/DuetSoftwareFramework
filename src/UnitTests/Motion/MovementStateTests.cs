using DuetControlServer.Motion;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// The endstop latch: what a homing move knows about how it ended
/// </summary>
/// <remarks>
/// Every rule here is one that fails silently if it is broken. An axis that is wrongly considered
/// homed reports success and then plans every later move from a position the machine was never at,
/// and an axis that is wrongly considered unhomed fails a G28 that actually worked
/// </remarks>
[TestFixture]
public class MovementStateTests
{
    [Test]
    public void NothingIsTriggeredToBeginWith()
    {
        MovementState state = new();
        Assert.That(state.EndstopsTriggered, Is.Zero);
    }

    [Test]
    public void StopsAccumulateWithinOneMove()
    {
        // A Cartesian homing X, Y and Z in one move reaches its three switches at three different
        // times, so the move is reported stopped three times. Assigning rather than accumulating
        // would home only the last axis to trigger
        MovementState state = new();

        state.ArmEndstops();
        state.RecordEndstopTriggered(1u << 0);
        state.RecordEndstopTriggered(1u << 2);

        Assert.That(state.EndstopsTriggered, Is.EqualTo((1u << 0) | (1u << 2)));
    }

    [Test]
    public void ArmingForgetsTheLastMove()
    {
        // The rule that makes a failed homing move visible. Without it a second G1 H1 that hits
        // nothing would still be credited with the first one's switch
        MovementState state = new();
        state.RecordEndstopTriggered(1u << 1);

        state.ArmEndstops();

        Assert.That(state.EndstopsTriggered, Is.Zero);
    }

    [Test]
    public void ResettingForgetsAnUnfinishedMove()
    {
        // A move part-way through is what other channels wait on, so a reset that left the count set
        // would stop every other channel moving until something else happened to clear it
        MovementState state = new();
        state.SegmentsLeft = 7;

        state.Reset();

        Assert.That(state.SegmentsLeft, Is.Zero);
    }

    [Test]
    public void ResettingForgetsTheLastMove()
    {
        // Reset is for when the machine position stops meaning anything, and what stopped the last
        // move is part of what it meant
        MovementState state = new();
        state.CurrentUserPosition[0] = 42.0f;
        state.RecordEndstopTriggered(1u << 1);

        state.Reset();

        Assert.Multiple(() =>
        {
            Assert.That(state.EndstopsTriggered, Is.Zero);
            Assert.That(state.CurrentUserPosition[0], Is.Zero);
        });
    }
}
