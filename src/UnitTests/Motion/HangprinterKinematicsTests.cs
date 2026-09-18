using System;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// The hangprinter geometry, where the effector hangs on lines from anchors around the workspace
/// </summary>
/// <remarks>
/// The inverse transform is checkable by hand - it is a distance - but the forward one is an iterative
/// least squares solve with no closed form to compare against. What can be checked is that it inverts
/// the transform it is supposed to invert, and that it refuses rather than guesses when the line
/// positions do not describe a point the machine can be at
/// </remarks>
[TestFixture]
public class HangprinterKinematicsTests
{
    private const int NumDrives = MotionLimits.MaxAxesPlusExtruders;

    /// <summary>Steps per mm of line</summary>
    private const float StepsPerMm = 100.0f;

    private static float[] UniformStepsPerMm()
    {
        float[] stepsPerMm = new float[NumDrives];
        Array.Fill(stepsPerMm, StepsPerMm);
        return stepsPerMm;
    }

    /// <summary>The four-anchor machine RepRapFirmware assumes before M669 has been seen</summary>
    private static HangprinterKinematicsEngine CreateHangprinter() => HangprinterKinematicsEngine.CreateDefault();

    private static int[] ToMotorSteps(KinematicsEngine engine, float[] machinePos)
    {
        int[] motorPos = new int[NumDrives];
        NativeMovementError error = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), 3, 3, motorPos);
        Assert.That(error, Is.EqualTo(NativeMovementError.Ok));
        return motorPos;
    }

    private static float[] ToCartesian(KinematicsEngine engine, int[] motorPos)
    {
        float[] machinePos = new float[NumDrives];
        engine.MotorStepsToCartesian(motorPos, UniformStepsPerMm(), 3, 3, machinePos);
        return machinePos;
    }

    [Test]
    public void AtTheOriginNoLineHasBeenPaidOut()
    {
        // Motor positions count line paid out since the origin, so the origin itself is by definition
        // all zeroes whatever the anchors are
        HangprinterKinematicsEngine engine = CreateHangprinter();

        int[] motorPos = ToMotorSteps(engine, new float[NumDrives]);
        for (int anchor = 0; anchor < 4; anchor++)
        {
            Assert.That(motorPos[anchor], Is.EqualTo(0), $"line {anchor}");
        }
    }

    [Test]
    public void LinePositionsAreTheChangeInDistanceToTheAnchor()
    {
        // The one part of this geometry with an answer that can be worked out by hand
        HangprinterKinematicsEngine engine = CreateHangprinter();

        float[] machinePos = [100.0f, 50.0f, 200.0f];
        int[] motorPos = ToMotorSteps(engine, machinePos);

        for (int anchor = 0; anchor < 4; anchor++)
        {
            float dx = machinePos[0] - engine.GetAnchor(anchor, 0);
            float dy = machinePos[1] - engine.GetAnchor(anchor, 1);
            float dz = machinePos[2] - engine.GetAnchor(anchor, 2);
            float expected = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) - engine.GetDistanceAtOrigin(anchor);

            Assert.That(motorPos[anchor] / StepsPerMm, Is.EqualTo(expected).Within(0.01f), $"line {anchor}");
        }
    }

    [TestCase(0.0f, 0.0f, 0.0f)]
    [TestCase(100.0f, 0.0f, 200.0f)]
    [TestCase(-150.0f, 250.0f, 400.0f)]
    [TestCase(300.0f, -300.0f, 100.0f)]
    [TestCase(0.0f, 0.0f, 1000.0f)]
    public void HangprinterPositionsSurviveTheRoundTrip(float x, float y, float z)
    {
        HangprinterKinematicsEngine engine = CreateHangprinter();

        int[] motorPos = ToMotorSteps(engine, [x, y, z]);
        float[] roundTripped = ToCartesian(engine, motorPos);

        // Looser than the geometric transforms: this one is iterative, and it stops when the step it
        // is taking has got small rather than when the answer is exact
        Assert.Multiple(() =>
        {
            Assert.That(roundTripped[0], Is.EqualTo(x).Within(0.5f));
            Assert.That(roundTripped[1], Is.EqualTo(y).Within(0.5f));
            Assert.That(roundTripped[2], Is.EqualTo(z).Within(0.5f));
        });
    }

    [Test]
    public void ImpossibleLinePositionsLeaveThePositionAlone()
    {
        // No point in space is that far from all four anchors at once. The solver has to say so
        // rather than settling on whatever least-squares compromise fits worst
        HangprinterKinematicsEngine engine = CreateHangprinter();

        float[] machinePos = new float[NumDrives];
        machinePos[0] = 42.0f;
        machinePos[1] = -17.0f;
        machinePos[2] = 8.0f;

        int[] motorPos = new int[NumDrives];
        for (int anchor = 0; anchor < 4; anchor++)
        {
            motorPos[anchor] = (int)(-5000.0f * StepsPerMm);
        }

        engine.MotorStepsToCartesian(motorPos, UniformStepsPerMm(), 3, 3, machinePos);

        Assert.Multiple(() =>
        {
            Assert.That(machinePos[0], Is.EqualTo(42.0f), "the caller's position is left as it was");
            Assert.That(machinePos[1], Is.EqualTo(-17.0f));
            Assert.That(machinePos[2], Is.EqualTo(8.0f));
        });
    }

    [Test]
    public void EveryLineHoldsEveryAxis()
    {
        // There is nothing rigid in a hangprinter: let any one line go and the effector moves in all
        // three directions at once
        HangprinterKinematicsEngine engine = CreateHangprinter();

        Assert.Multiple(() =>
        {
            Assert.That(engine.GetControllingDrives(0), Is.EqualTo(0b1111u));
            Assert.That(engine.GetControllingDrives(1), Is.EqualTo(0b1111u));
            Assert.That(engine.GetControllingDrives(2), Is.EqualTo(0b1111u));
            Assert.That(engine.GetControllingDrives(3), Is.EqualTo(0b1111u));
            Assert.That(engine.GetControllingDrives(4), Is.EqualTo(0b10000u), "past the anchors there are no more lines");
        });
    }

    [Test]
    public void APositionOutsideThePrintRadiusIsNotReachable()
    {
        HangprinterKinematicsEngine engine = CreateHangprinter();

        Assert.Multiple(() =>
        {
            Assert.That(engine.IsReachable(0.0f, 0.0f), Is.True);
            Assert.That(engine.IsReachable(HangprinterKinematicsEngine.DefaultPrintRadius + 1.0f, 0.0f), Is.False);
        });
    }

    [Test]
    public void AFiveAnchorMachineUsesAllFiveLines()
    {
        // More anchors than unknowns is the normal case; a fifth just makes the least squares problem
        // more over-determined, which is if anything easier to solve
        HangprinterKinematicsEngine engine = new(
        [
            [0.0f, -2000.0f, -100.0f],
            [2000.0f, 1000.0f, -100.0f],
            [-2000.0f, 1000.0f, -100.0f],
            [0.0f, 0.0f, 3000.0f],
            [1500.0f, -1500.0f, 2000.0f]
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(engine.NumAnchors, Is.EqualTo(5));
            Assert.That(engine.GetControllingDrives(0), Is.EqualTo(0b11111u));
        });

        int[] motorPos = ToMotorSteps(engine, [120.0f, -80.0f, 300.0f]);
        Assert.That(motorPos[4], Is.Not.EqualTo(0), "the fifth line moves too");

        float[] roundTripped = ToCartesian(engine, motorPos);
        Assert.Multiple(() =>
        {
            Assert.That(roundTripped[0], Is.EqualTo(120.0f).Within(0.5f));
            Assert.That(roundTripped[1], Is.EqualTo(-80.0f).Within(0.5f));
            Assert.That(roundTripped[2], Is.EqualTo(300.0f).Within(0.5f));
        });
    }

    [Test]
    public void AHangprinterLimitsADiagonalMoveToTheSlowerOfXAndY()
    {
        // Nothing on a hangprinter moves one axis alone, so the inherited XY limit applies
        HangprinterKinematicsEngine engine = CreateHangprinter();

        float[] maxFeedrates = new float[NumDrives];
        float[] accelerations = new float[NumDrives];
        Array.Fill(maxFeedrates, 100.0f);
        Array.Fill(accelerations, 1000.0f);
        maxFeedrates[0] = 40.0f;

        float[] direction = new float[NumDrives];
        direction[0] = MathF.Sqrt(0.5f);
        direction[1] = MathF.Sqrt(0.5f);

        MoveLimits limits = new() { RequestedSpeed = 100.0f, MaxAcceleration = 1000.0f };
        PlannedMove move = new() { NormalisedDirectionVector = direction, NumVisibleAxes = 3 };
        engine.LimitSpeedAndAcceleration(ref limits, move, maxFeedrates, accelerations);

        Assert.That(limits.RequestedSpeed, Is.EqualTo(70.0f).Within(0.01f));
    }
}
