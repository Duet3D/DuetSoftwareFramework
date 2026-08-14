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
        });
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
    /// Speeds and jerks are per minute here and per second in RepRapFirmware, so a default copied
    /// across without converting is out by sixty - which is invisible in a machine that configures
    /// its axes and cripplingly slow in one that does not
    /// </summary>
    [Test]
    public void SpeedsAndJerksAreConvertedFromRepRapFirmwaresPerSecondConstants()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Axis.DefaultSpeed, Is.EqualTo(6000.0f), "DefaultAxisMaxFeedrate, 100 mm/s");
            Assert.That(Axis.DefaultZSpeed, Is.EqualTo(1200.0f), "DefaultZMaxFeedrate, 20 mm/s");
            Assert.That(Axis.DefaultJerk, Is.EqualTo(900.0f), "DefaultAxisInstantDv, 15 mm/s");
            Assert.That(Axis.DefaultZJerk, Is.EqualTo(600.0f), "DefaultZInstantDv, 10 mm/s");
            Assert.That(Extruder.DefaultSpeed, Is.EqualTo(6000.0f), "DefaultEMaxFeedrate, 100 mm/s");
            Assert.That(Extruder.DefaultJerk, Is.EqualTo(300.0f), "DefaultEInstantDv, 5 mm/s");

            // Accelerations are mm/s^2 on both sides, so these are RepRapFirmware's numbers as they
            // stand - which is why acceleration was the field whose default was noticed missing
            Assert.That(Axis.DefaultAcceleration, Is.EqualTo(1000.0f), "DefaultAxisAcceleration");
            Assert.That(Axis.DefaultZAcceleration, Is.EqualTo(200.0f), "DefaultZAcceleration");
            Assert.That(Extruder.DefaultAcceleration, Is.EqualTo(500.0f), "DefaultEAcceleration");
        });
    }
}
