using System;
using DuetControlServer.Motion.Kinematics;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// Where each geometry says a drive is when its endstop fires
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>Kinematics::GetEndstopPosition</c> and its overrides. The distinction this pins
/// down is that outside a Cartesian machine the answer is <em>not</em> the axis limit: a delta tower's
/// switch is at a carriage height, a rotary delta's at an arm angle, a polar's at a radius. Homing
/// used to set the axis to its limit whatever the geometry, which on a delta is a coordinate the
/// carriage was never at.
/// </para>
/// <para>
/// The values here are what RepRapFirmware returns for the same machine, so the axis limits passed in
/// are deliberately nothing like the expected answers - a test that passed with the base
/// implementation would prove nothing
/// </para>
/// </remarks>
[TestFixture]
public class EndstopPositionTests
{
    /// <summary>Axis limits chosen so that returning them would be obvious</summary>
    private const float AxisMin = -111.0f, AxisMax = 999.0f;

    private static float Ask(KinematicsEngine engine, int drive, bool highEnd = true,
                             ReadOnlySpan<int> endPoints = default, ReadOnlySpan<float> stepsPerMm = default)
        => engine.GetEndstopPosition(drive, highEnd, AxisMin, AxisMax, endPoints, stepsPerMm);

    [Test]
    public void ACartesianAxisHomesToItsOwnLimit()
    {
        KinematicsEngine engine = CoreKinematicsEngine.TryCreate("cartesian")!;
        Assert.Multiple(() =>
        {
            Assert.That(Ask(engine, 0, highEnd: true), Is.EqualTo(AxisMax));
            Assert.That(Ask(engine, 0, highEnd: false), Is.EqualTo(AxisMin));
            Assert.That(engine.HomesIndividualDrives, Is.False, "so the axis limit is the right answer");
        });
    }

    [Test]
    public void ADeltaTowerHomesToItsCarriageHeight()
    {
        LinearDeltaKinematicsEngine engine = LinearDeltaKinematicsEngine.CreateDefault();
        Assert.Multiple(() =>
        {
            for (int tower = 0; tower < LinearDeltaKinematicsEngine.UsualNumTowers; tower++)
            {
                Assert.That(Ask(engine, tower), Is.EqualTo(engine.GetHomedCarriageHeight(tower)),
                            $"tower {tower}");
            }

            // A delta's switches are at the top. There is no low-end answer, so it falls through to
            // the axis limit exactly as RepRapFirmware's does
            Assert.That(Ask(engine, 0, highEnd: false), Is.EqualTo(AxisMin));

            // An axis past the towers is an ordinary one again
            Assert.That(Ask(engine, LinearDeltaKinematicsEngine.UsualNumTowers), Is.EqualTo(AxisMax));
        });
    }

    [Test]
    public void ARotaryDeltaArmHomesToItsMaximumAngle()
    {
        RotaryDeltaKinematicsEngine engine = new();
        Assert.Multiple(() =>
        {
            for (int tower = 0; tower < RotaryDeltaKinematicsEngine.DeltaAxes; tower++)
            {
                Assert.That(Ask(engine, tower),
                            Is.EqualTo(engine.MaxArmAngle + engine.GetEndstopAdjustment(tower)).Within(1e-4f),
                            $"arm {tower}, including its M666 adjustment");
            }
            Assert.That(Ask(engine, 0, highEnd: false), Is.EqualTo(AxisMin));
        });
    }

    [Test]
    public void APolarRadiusHomesToItsHomedRadiusAndTheTurntableToZero()
    {
        PolarKinematicsEngine engine = new(minRadius: 0.0f, maxRadius: 150.0f, homedRadius: 25.0f,
                                          maxTurntableSpeed: 30.0f, maxTurntableAcceleration: 30.0f);
        Assert.Multiple(() =>
        {
            Assert.That(Ask(engine, 0), Is.EqualTo(engine.HomedRadius), "the radius arm");

            // Zero whichever end the switch is at: the turntable's home is an angle, not a limit
            Assert.That(Ask(engine, 1, highEnd: true), Is.Zero, "the turntable");
            Assert.That(Ask(engine, 1, highEnd: false), Is.Zero, "whichever end its switch is");
        });
    }

    [Test]
    public void AScaraDistalJointAllowsForWhereTheProximalJointIs()
    {
        // Crosstalk is the whole point: turning the proximal arm drags the distal one, so where the
        // distal switch sits depends on where the proximal arm was left. Homing a SCARA in the wrong
        // order gives the wrong answer, in RepRapFirmware too
        ScaraKinematicsEngine engine = new();

        ReadOnlySpan<float> stepsPerMm = [100.0f, 200.0f, 400.0f];
        ReadOnlySpan<int> atOrigin = [0, 0, 0];
        ReadOnlySpan<int> proximalTurned = [1000, 0, 0];

        float psiAtOrigin = Ask(engine, 1, endPoints: atOrigin, stepsPerMm: stepsPerMm);
        float psiAfterTurning = Ask(engine, 1, endPoints: proximalTurned, stepsPerMm: stepsPerMm);

        Assert.That(psiAtOrigin, Is.Not.EqualTo(AxisMax), "the distal joint's limit, not the axis'");

        // With the default crosstalk of zero the two agree; the point of the test is that the
        // proximal position reaches the calculation at all, so drive it with a known factor
        ScaraKinematicsEngine crossTalking = new(crosstalk: [0.5f, 0.0f, 0.0f]);
        float crossedAtOrigin = crossTalking.GetEndstopPosition(1, true, AxisMin, AxisMax, atOrigin, stepsPerMm);
        float crossedAfterTurning = crossTalking.GetEndstopPosition(1, true, AxisMin, AxisMax, proximalTurned, stepsPerMm);

        Assert.Multiple(() =>
        {
            Assert.That(psiAfterTurning, Is.EqualTo(psiAtOrigin),
                        "no crosstalk configured, so the proximal arm does not matter");

            // 1000 steps at 100 steps/mm is 10 units of proximal movement; half of that dragged onto
            // a joint counted at 200 steps/mm is 1000 * 0.5 * 100 / 200 = 250
            Assert.That(crossedAtOrigin - crossedAfterTurning, Is.EqualTo(250.0f).Within(1e-3f),
                        "the proximal arm drags the distal joint's switch");
        });
    }

    [Test]
    public void AFiveBarScaraActuatorHomesToItsWorkModesAngle()
    {
        FiveBarScaraKinematicsEngine engine = new(xOrigL: -50.0f, yOrigL: 0.0f, xOrigR: 50.0f, yOrigR: 0.0f,
                                                  proximalL: 100.0f, proximalR: 100.0f, distalL: 100.0f, distalR: 100.0f);
        Assert.Multiple(() =>
        {
            Assert.That(Ask(engine, 0), Is.EqualTo(engine.HomingAngleLeft));
            Assert.That(Ask(engine, 1), Is.EqualTo(engine.HomingAngleRight));
            Assert.That(Ask(engine, 2), Is.EqualTo(AxisMax), "Z is an ordinary axis");
        });
    }

    [Test]
    public void AHangprinterFallsBackToTheAxisLimit()
    {
        // RepRapFirmware says outright that hangprinter homing is not supported, and returns the base
        // answer. Reproduced rather than left to chance, so the engine does not quietly grow one
        KinematicsEngine engine = HangprinterKinematicsEngine.CreateDefault();
        Assert.That(Ask(engine, 0), Is.EqualTo(AxisMax));
    }
}
