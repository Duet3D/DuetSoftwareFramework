using System.Linq;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// What an axis and an extruder may do before anything has configured them
/// </summary>
/// <remarks>
/// <para>
/// A machine whose <c>config.g</c> never mentions an axis in <c>M201</c>, <c>M203</c> or
/// <c>M566</c> still has to be able to move it, which is why RepRapFirmware's <c>Move::Init</c>
/// fills every axis slot before <c>config.g</c> runs.
/// </para>
/// <para>
/// A zero acceleration in particular is not a slow axis but a stuck one: the planner works a move's
/// duration out by dividing by it, so every move on that axis is rejected as infinitely long and the
/// only symptom is an axis that never moves. <c>DdaRingTests</c> covers that rejection from the other
/// side
/// </para>
/// </remarks>
[TestFixture]
public class AxisDefaultsTests
{
    [Test]
    public void ANewAxisCanBeMoved()
    {
        Axis axis = new();

        Assert.Multiple(() =>
        {
            Assert.That(axis.Acceleration, Is.GreaterThan(0.0f), "an axis with no acceleration cannot move at all");
            Assert.That(axis.ReducedAcceleration, Is.GreaterThan(0.0f), "nor can it probe or stall-home");
            Assert.That(axis.Speed, Is.GreaterThan(0.0f));
            Assert.That(axis.Jerk, Is.GreaterThan(0.0f));
            Assert.That(axis.PrintingJerk, Is.GreaterThan(0.0f));
            Assert.That(axis.StepsPerMm, Is.GreaterThan(0.0f), "and a move on it would round to no steps");
        });
    }

    [Test]
    public void ANewProbeRisesBetweenTaps()
    {
        // A dive height of zero leaves the nozzle on the bed between taps, so every tap after the
        // first measures nothing
        Probe probe = new();

        Assert.That(probe.DiveHeights, Is.All.GreaterThan(0.0f));
    }

    [Test]
    public void ANewExtruderCanBeMoved()
    {
        Extruder extruder = new();

        Assert.Multiple(() =>
        {
            Assert.That(extruder.Acceleration, Is.GreaterThan(0.0f));
            Assert.That(extruder.Speed, Is.GreaterThan(0.0f));
            Assert.That(extruder.Jerk, Is.GreaterThan(0.0f));
            Assert.That(extruder.PrintingJerk, Is.GreaterThan(0.0f));
        });
    }

    /// <summary>
    /// Every default has to be a value the machine could also have been configured with
    /// </summary>
    /// <remarks>
    /// A configuration code clamps what it is given to a floor, so a default below that floor would
    /// be a machine that starts life outside the range it is allowed to be configured into: the
    /// first <c>M201</c>, <c>M566</c> or <c>M92</c> to name the drive would raise the value rather
    /// than lower it, and a bare <c>M203</c> would speed the axis up. Nothing would report that,
    /// because each half is reasonable on its own
    /// </remarks>
    [Test]
    public void EveryDefaultIsAboveTheFloorItsCodeClampsTo()
    {
        Move move = new();
        float minSpeed = move.MinimumMovementSpeed * 60.0f;      // the floor M203 applies, in mm/min

        Assert.Multiple(() =>
        {
            foreach ((string name, float accel) in new[]
                     {
                         ("Axis.DefaultAcceleration", Axis.DefaultAcceleration),
                         ("Axis.DefaultZAcceleration", Axis.DefaultZAcceleration),
                         ("Extruder.DefaultAcceleration", Extruder.DefaultAcceleration)
                     })
            {
                Assert.That(accel, Is.GreaterThanOrEqualTo(Move.MinimumAcceleration), name);
            }

            foreach ((string name, float jerk) in new[]
                     {
                         ("Axis.DefaultJerk", Axis.DefaultJerk),
                         ("Axis.DefaultZJerk", Axis.DefaultZJerk),
                         ("Extruder.DefaultJerk", Extruder.DefaultJerk)
                     })
            {
                Assert.That(jerk, Is.GreaterThanOrEqualTo(Move.MinimumJerk), name);
            }

            foreach ((string name, float steps) in new[]
                     {
                         ("Axis.DefaultStepsPerMm", Axis.DefaultStepsPerMm),
                         ("Axis.DefaultZStepsPerMm", Axis.DefaultZStepsPerMm),
                         ("Extruder.DefaultStepsPerMm", Extruder.DefaultStepsPerMm)
                     })
            {
                Assert.That(steps, Is.GreaterThanOrEqualTo(Move.MinimumStepsPerMm), name);
            }

            foreach ((string name, float speed) in new[]
                     {
                         ("Axis.DefaultSpeed", Axis.DefaultSpeed),
                         ("Axis.DefaultZSpeed", Axis.DefaultZSpeed),
                         ("Extruder.DefaultSpeed", Extruder.DefaultSpeed)
                     })
            {
                Assert.That(speed, Is.GreaterThanOrEqualTo(minSpeed), name);
            }
        });
    }

    /// <summary>
    /// A default axis has somewhere to move between
    /// </summary>
    /// <remarks>
    /// The only pair of defaults that bound each other rather than being bounded by a constant.
    /// Equal limits would be an axis of no length, which <c>M208</c> would have to be used to undo
    /// before the axis could move at all
    /// </remarks>
    [Test]
    public void ANewAxisHasTravelBetweenItsLimits()
    {
        Axis axis = new();

        Assert.That(axis.Max, Is.GreaterThan(axis.Min));
    }
}
