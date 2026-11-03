using System;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Native;
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
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Cartesian, KinematicsAxes.NewMoveWithAxes());
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
        // Homed, the effector is at the centre of the bed at the homed height, so each carriage is
        // where the inverse transform puts it for that position, moved by its M666 adjustment
        KinematicsEngine engine = new LinearDeltaKinematicsEngine(
            numTowers: 3, radius: 105.6f,
            diagonals: [215.0f, 215.0f, 215.0f],
            angleCorrections: [0.0f, 0.0f, 0.0f],
            endstopAdjustments: [0.5f, -0.25f, 0.0f],
            homedHeight: 250.0f, printRadius: 80.0f).WithAxes();
        DeltaKinematics reported = KinematicsAxes.Reported<DeltaKinematics>(engine);

        const float stepsPerMm = 1000.0f;
        float[] homed = new float[KinematicsAxes.Canonical.Length];
        homed[2] = reported.HomedHeight;
        float[] uniformStepsPerMm = new float[homed.Length];
        Array.Fill(uniformStepsPerMm, stepsPerMm);
        int[] motorPos = new int[homed.Length];
        Assert.That(engine.CartesianToMotorSteps(homed, uniformStepsPerMm, homed.Length, homed.Length, motorPos),
                    Is.EqualTo(NativeMovementError.Ok));

        Assert.Multiple(() =>
        {
            for (int tower = 0; tower < LinearDeltaKinematicsEngine.UsualNumTowers; tower++)
            {
                // Within a step of the transform's answer, which is rounded to whole steps
                float carriageHeight = (motorPos[tower] / stepsPerMm) + reported.Towers[tower].EndstopAdjustment;
                Assert.That(Ask(engine, tower), Is.EqualTo(carriageHeight).Within(1.0f / stepsPerMm), $"tower {tower}");
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
        // RepRapFirmware's default maximum arm angle is 45 degrees, and nothing has adjusted an endstop
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.RotaryDelta, KinematicsAxes.NewMoveWithAxes());
        Assert.Multiple(() =>
        {
            for (int arm = 0; arm < 3; arm++)
            {
                Assert.That(Ask(engine, arm), Is.EqualTo(45.0f).Within(1e-4f), $"arm {arm}");
            }
            Assert.That(Ask(engine, 0, highEnd: false), Is.EqualTo(AxisMin));
        });
    }

    [Test]
    public void APolarRadiusHomesToItsHomedRadiusAndTheTurntableToZero()
    {
        KinematicsEngine engine = new PolarKinematicsEngine(minRadius: 0.0f, maxRadius: 150.0f, homedRadius: 25.0f,
                                                            maxTurntableSpeed: 30.0f, maxTurntableAcceleration: 30.0f).WithAxes();
        Assert.Multiple(() =>
        {
            Assert.That(Ask(engine, 0), Is.EqualTo(25.0f), "the radius arm, at its homed radius");

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
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Scara, KinematicsAxes.NewMoveWithAxes());

        ReadOnlySpan<float> stepsPerMm = [100.0f, 200.0f, 400.0f];
        ReadOnlySpan<int> atOrigin = [0, 0, 0];
        ReadOnlySpan<int> proximalTurned = [1000, 0, 0];

        float psiAtOrigin = Ask(engine, 1, endPoints: atOrigin, stepsPerMm: stepsPerMm);
        float psiAfterTurning = Ask(engine, 1, endPoints: proximalTurned, stepsPerMm: stepsPerMm);

        Assert.That(psiAtOrigin, Is.Not.EqualTo(AxisMax), "the distal joint's limit, not the axis'");

        // With the default crosstalk of zero the two agree; the point of the test is that the
        // proximal position reaches the calculation at all, so drive it with a known factor
        KinematicsEngine crossTalking = new ScaraKinematicsEngine(crosstalk: [0.5f, 0.0f, 0.0f]).WithAxes();
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
        // Work mode 1, whose homing angles RepRapFirmware's FiveBarScaraKinematics::Configure defaults
        // to 20 degrees on the left and 10 on the right
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.FiveBarScara, KinematicsAxes.NewMoveWithAxes());
        Assert.Multiple(() =>
        {
            Assert.That(Ask(engine, 0), Is.EqualTo(20.0f));
            Assert.That(Ask(engine, 1), Is.EqualTo(10.0f));
            Assert.That(Ask(engine, 2), Is.EqualTo(AxisMax), "Z is an ordinary axis");
        });
    }

    [Test]
    public void AHangprinterFallsBackToTheAxisLimit()
    {
        // RepRapFirmware says outright that hangprinter homing is not supported, and returns the base
        // answer. Reproduced rather than left to chance, so the engine does not quietly grow one
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Hangprinter, KinematicsAxes.NewMoveWithAxes());
        Assert.That(Ask(engine, 0), Is.EqualTo(AxisMax));
    }
}
