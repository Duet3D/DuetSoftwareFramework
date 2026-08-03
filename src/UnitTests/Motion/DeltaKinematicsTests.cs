using System;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// The two delta geometries, against the machines they describe
/// </summary>
/// <remarks>
/// Neither transform has an obvious closed form to check against, so the tests lean on the round trip
/// and on the few positions whose answer follows from the geometry alone. A delta at the centre of the
/// bed, for instance, must have all three carriages at the same height whatever the rod length is
/// </remarks>
[TestFixture]
public class DeltaKinematicsTests
{
    private const int NumDrives = MotionLimits.MaxAxesPlusExtruders;

    /// <summary>Steps per mm for a delta carriage, and steps per degree for a rotary arm</summary>
    private const float StepsPerMm = 100.0f;

    private static float[] UniformStepsPerMm()
    {
        float[] stepsPerMm = new float[NumDrives];
        Array.Fill(stepsPerMm, StepsPerMm);
        return stepsPerMm;
    }

    /// <summary>A machine with the dimensions RepRapFirmware assumes before M665 has been seen</summary>
    private static LinearDeltaKinematicsEngine CreateLinearDelta() => LinearDeltaKinematicsEngine.CreateDefault();

    private static float[] ToCartesian(KinematicsEngine engine, int[] motorPos, int numAxes)
    {
        float[] machinePos = new float[NumDrives];
        engine.MotorStepsToCartesian(motorPos, UniformStepsPerMm(), numAxes, numAxes, machinePos);
        return machinePos;
    }

    private static int[] ToMotorSteps(KinematicsEngine engine, float[] machinePos, int numAxes, NativeMovementError expected = NativeMovementError.Ok)
    {
        int[] motorPos = new int[NumDrives];
        NativeMovementError error = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), numAxes, numAxes, motorPos);
        Assert.That(error, Is.EqualTo(expected));
        return motorPos;
    }

    // ---- Linear delta ------------------------------------------------------------------------------

    [Test]
    public void TowersAreEvenlySpacedAroundTheBed()
    {
        LinearDeltaKinematicsEngine engine = CreateLinearDelta();

        // A is at -150 degrees, B at -30 and C at +90, all at the delta radius from the centre
        for (int tower = 0; tower < LinearDeltaKinematicsEngine.UsualNumTowers; tower++)
        {
            float distance = MathF.Sqrt(MathF.Pow(engine.GetTowerX(tower), 2) + MathF.Pow(engine.GetTowerY(tower), 2));
            Assert.That(distance, Is.EqualTo(LinearDeltaKinematicsEngine.DefaultDeltaRadius).Within(0.01f),
                        $"tower {tower} stands at the delta radius");
        }

        Assert.Multiple(() =>
        {
            Assert.That(engine.GetTowerX(2), Is.EqualTo(0.0f).Within(0.01f), "the C tower is straight back");
            Assert.That(engine.GetTowerY(2), Is.EqualTo(LinearDeltaKinematicsEngine.DefaultDeltaRadius).Within(0.01f));
            Assert.That(engine.GetTowerX(0), Is.EqualTo(-engine.GetTowerX(1)).Within(0.01f), "A and B are mirrored in X");
            Assert.That(engine.GetTowerY(0), Is.EqualTo(engine.GetTowerY(1)).Within(0.01f), "and level in Y");
        });
    }

    [Test]
    public void AtTheCentreAllThreeCarriagesAreLevel()
    {
        // Symmetry: the centre of the bed is the same distance from every tower, so every rod hangs at
        // the same angle and every carriage is at the same height
        LinearDeltaKinematicsEngine engine = CreateLinearDelta();

        int[] motorPos = ToMotorSteps(engine, [0.0f, 0.0f, 100.0f], 3);
        Assert.Multiple(() =>
        {
            Assert.That(motorPos[1], Is.EqualTo(motorPos[0]));
            Assert.That(motorPos[2], Is.EqualTo(motorPos[0]));
        });

        // And the height itself: the rod's vertical part plus the effector height
        float expected = 100.0f + MathF.Sqrt(MathF.Pow(LinearDeltaKinematicsEngine.DefaultDiagonal, 2)
                                             - MathF.Pow(LinearDeltaKinematicsEngine.DefaultDeltaRadius, 2));
        Assert.That(motorPos[0] / StepsPerMm, Is.EqualTo(expected).Within(0.01f));
    }

    [Test]
    public void RaisingTheEffectorRaisesEveryCarriageEqually()
    {
        LinearDeltaKinematicsEngine engine = CreateLinearDelta();

        int[] low = ToMotorSteps(engine, [10.0f, -20.0f, 50.0f], 3);
        int[] high = ToMotorSteps(engine, [10.0f, -20.0f, 60.0f], 3);

        // Z is the one axis a delta moves without changing the geometry, so all three carriages take
        // exactly the same number of steps
        for (int tower = 0; tower < 3; tower++)
        {
            Assert.That(high[tower] - low[tower], Is.EqualTo((int)(10.0f * StepsPerMm)), $"tower {tower}");
        }
    }

    [TestCase(0.0f, 0.0f, 120.0f)]
    [TestCase(30.0f, 0.0f, 100.0f)]
    [TestCase(-25.0f, 40.0f, 80.0f)]
    [TestCase(60.0f, -60.0f, 150.0f)]
    public void LinearDeltaPositionsSurviveTheRoundTrip(float x, float y, float z)
    {
        LinearDeltaKinematicsEngine engine = CreateLinearDelta();

        int[] motorPos = ToMotorSteps(engine, [x, y, z], 3);
        float[] roundTripped = ToCartesian(engine, motorPos, 3);

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped[0], Is.EqualTo(x).Within(0.05f));
            Assert.That(roundTripped[1], Is.EqualTo(y).Within(0.05f));
            Assert.That(roundTripped[2], Is.EqualTo(z).Within(0.05f));
        });
    }

    [Test]
    public void ARoundTripSurvivesUnequalRodsAndTowerCorrections()
    {
        // The forward transform derives its constants from the tower positions and rod lengths, so a
        // machine where they are not all the same is what actually exercises them
        LinearDeltaKinematicsEngine engine = new(
            numTowers: 3, radius: 105.6f,
            diagonals: [215.0f, 217.5f, 213.0f],
            angleCorrections: [0.4f, -0.3f, 0.15f],
            endstopAdjustments: [0.2f, -0.1f, 0.05f],
            homedHeight: 240.0f, printRadius: 80.0f);

        int[] motorPos = ToMotorSteps(engine, [22.0f, -35.0f, 90.0f], 3);
        float[] roundTripped = ToCartesian(engine, motorPos, 3);

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped[0], Is.EqualTo(22.0f).Within(0.05f));
            Assert.That(roundTripped[1], Is.EqualTo(-35.0f).Within(0.05f));
            Assert.That(roundTripped[2], Is.EqualTo(90.0f).Within(0.05f));
        });
    }

    [Test]
    public void BedTiltIsAddedGoingOutAndTakenOffComingBack()
    {
        // Tilt correction is a fudge applied to Z as the head moves in X and Y, to square up a bed
        // that is not level. It must not leak into the position the machine thinks it is at
        LinearDeltaKinematicsEngine engine = new(
            numTowers: 3, radius: 105.6f,
            diagonals: [215.0f, 215.0f, 215.0f],
            angleCorrections: [0.0f, 0.0f, 0.0f],
            endstopAdjustments: [0.0f, 0.0f, 0.0f],
            homedHeight: 240.0f, printRadius: 80.0f,
            xTilt: 0.01f, yTilt: -0.005f);

        Assert.Multiple(() =>
        {
            Assert.That(engine.GetTiltCorrection(0), Is.EqualTo(0.01f));
            Assert.That(engine.GetTiltCorrection(1), Is.EqualTo(-0.005f));
            Assert.That(engine.GetTiltCorrection(2), Is.EqualTo(0.0f), "Z itself is not corrected");
        });

        int[] motorPos = ToMotorSteps(engine, [40.0f, 30.0f, 70.0f], 3);
        float[] roundTripped = ToCartesian(engine, motorPos, 3);

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped[0], Is.EqualTo(40.0f).Within(0.05f));
            Assert.That(roundTripped[1], Is.EqualTo(30.0f).Within(0.05f));
            Assert.That(roundTripped[2], Is.EqualTo(70.0f).Within(0.05f));
        });
    }

    [Test]
    public void APositionTheRodsCannotSpanIsUnreachable()
    {
        // Further from a tower than the rod is long: there is no carriage height at all that puts the
        // effector there, and the square root goes imaginary
        LinearDeltaKinematicsEngine engine = CreateLinearDelta();

        float[] machinePos = new float[NumDrives];
        machinePos[0] = 1000.0f;

        int[] motorPos = new int[NumDrives];
        NativeMovementError error = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), 3, 3, motorPos);

        Assert.That(error, Is.EqualTo(NativeMovementError.UnreachablePosition));
    }

    [Test]
    public void HoldingAnyAxisStillNeedsAllThreeMotors()
    {
        // Nothing on a delta moves one motor alone, so all three have to be energised whichever axis
        // the caller asks about
        LinearDeltaKinematicsEngine engine = CreateLinearDelta();

        Assert.Multiple(() =>
        {
            Assert.That(engine.GetControllingDrives(0), Is.EqualTo(0b111u));
            Assert.That(engine.GetControllingDrives(1), Is.EqualTo(0b111u));
            Assert.That(engine.GetControllingDrives(2), Is.EqualTo(0b111u));
            Assert.That(engine.GetControllingDrives(3), Is.EqualTo(0b1000u), "a fourth axis has a motor of its own");
        });
    }

    [Test]
    public void AxesAboveTheTowersAreLinear()
    {
        LinearDeltaKinematicsEngine engine = CreateLinearDelta();

        float[] machinePos = new float[NumDrives];
        machinePos[2] = 100.0f;
        machinePos[3] = 12.5f;

        int[] motorPos = ToMotorSteps(engine, machinePos, 4);
        Assert.That(motorPos[3], Is.EqualTo((int)(12.5f * StepsPerMm)));

        float[] roundTripped = ToCartesian(engine, motorPos, 4);
        Assert.That(roundTripped[3], Is.EqualTo(12.5f).Within(0.01f));
    }

    [Test]
    public void TheAlwaysReachableHeightIsBelowTheHomedHeight()
    {
        // The ceiling sags away from the centre, so the height that is reachable everywhere is lower
        // than the height reachable at the middle
        LinearDeltaKinematicsEngine engine = CreateLinearDelta();

        Assert.That(engine.AlwaysReachableHeight, Is.LessThan(engine.HomedHeight));

        float[] atTheLimit = [0.0f, 0.0f, engine.AlwaysReachableHeight];
        Assert.That(engine.IsReachable(atTheLimit), Is.True);
    }

    [Test]
    public void APositionOutsideThePrintRadiusIsNotReachable()
    {
        LinearDeltaKinematicsEngine engine = CreateLinearDelta();

        float justOutside = LinearDeltaKinematicsEngine.DefaultPrintRadius + 1.0f;
        Assert.Multiple(() =>
        {
            Assert.That(engine.IsReachable([justOutside, 0.0f, 50.0f]), Is.False);
            Assert.That(engine.IsReachable([0.0f, 0.0f, 50.0f]), Is.True);
        });
    }

    // ---- Rotary delta ------------------------------------------------------------------------------

    [Test]
    public void RotaryDeltaArmsAreLevelAtTheCentre()
    {
        // The same symmetry argument as for the linear delta: at the centre every arm is swung to the
        // same angle
        RotaryDeltaKinematicsEngine engine = new();

        int[] motorPos = ToMotorSteps(engine, [0.0f, 0.0f, 100.0f], 3);
        Assert.Multiple(() =>
        {
            Assert.That(motorPos[1], Is.EqualTo(motorPos[0]));
            Assert.That(motorPos[2], Is.EqualTo(motorPos[0]));
        });
    }

    [TestCase(0.0f, 0.0f, 100.0f)]
    [TestCase(20.0f, 0.0f, 110.0f)]
    [TestCase(-15.0f, 25.0f, 95.0f)]
    [TestCase(30.0f, -30.0f, 120.0f)]
    public void RotaryDeltaPositionsSurviveTheRoundTrip(float x, float y, float z)
    {
        RotaryDeltaKinematicsEngine engine = new();

        int[] motorPos = ToMotorSteps(engine, [x, y, z], 3);
        float[] roundTripped = ToCartesian(engine, motorPos, 3);

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped[0], Is.EqualTo(x).Within(0.05f));
            Assert.That(roundTripped[1], Is.EqualTo(y).Within(0.05f));
            Assert.That(roundTripped[2], Is.EqualTo(z).Within(0.05f));
        });
    }

    [Test]
    public void ARotaryRoundTripSurvivesUnequalArmsAndRods()
    {
        // The trilateration constants are all derived per tower, so a machine whose arms differ is
        // what proves each one is being indexed by its own tower
        RotaryDeltaKinematicsEngine engine = new(
            radius: 50.0f,
            armLengths: [100.0f, 104.0f, 97.0f],
            rodLengths: [200.0f, 203.0f, 198.0f],
            bearingHeights: [250.0f, 251.5f, 249.0f],
            angleCorrections: [0.3f, -0.2f, 0.1f],
            endstopAdjustments: [0.0f, 0.0f, 0.0f]);

        int[] motorPos = ToMotorSteps(engine, [18.0f, -22.0f, 105.0f], 3);
        float[] roundTripped = ToCartesian(engine, motorPos, 3);

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped[0], Is.EqualTo(18.0f).Within(0.1f));
            Assert.That(roundTripped[1], Is.EqualTo(-22.0f).Within(0.1f));
            Assert.That(roundTripped[2], Is.EqualTo(105.0f).Within(0.1f));
        });
    }

    [Test]
    public void ARotaryDeltaPositionTheRodsCannotReachIsUnreachable()
    {
        RotaryDeltaKinematicsEngine engine = new();

        float[] machinePos = new float[NumDrives];
        machinePos[0] = 5000.0f;
        machinePos[2] = 100.0f;

        int[] motorPos = new int[NumDrives];
        NativeMovementError error = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), 3, 3, motorPos);

        Assert.That(error, Is.EqualTo(NativeMovementError.UnreachablePosition));
    }

    [Test]
    public void RotaryDeltaHoldingAnyAxisStillNeedsAllThreeMotors()
    {
        RotaryDeltaKinematicsEngine engine = new();
        Assert.Multiple(() =>
        {
            Assert.That(engine.GetControllingDrives(0), Is.EqualTo(0b111u));
            Assert.That(engine.GetControllingDrives(2), Is.EqualTo(0b111u));
            Assert.That(engine.GetControllingDrives(4), Is.EqualTo(0b10000u));
        });
    }

    [Test]
    public void RotaryDeltaRefusesAnArmAngleOutsideItsRange()
    {
        // The angle limits are the mechanism, not the bed: past them the arm fouls something. With the
        // default 100mm arms on 250mm bearings, reaching all the way down to the bed needs about 55
        // degrees of swing, which is past the 45 the arms are allowed
        RotaryDeltaKinematicsEngine engine = new();

        Assert.Multiple(() =>
        {
            Assert.That(engine.IsReachable([0.0f, 0.0f, 100.0f]), Is.True);
            Assert.That(engine.IsReachable([0.0f, 0.0f, 0.0f]), Is.False, "too far below the bearings");
        });
    }

    // ---- Both --------------------------------------------------------------------------------------

    [Test]
    public void ADeltaLimitsADiagonalMoveToTheSlowerOfXAndY()
    {
        // The default limit, which every non-Cartesian geometry inherits: X and Y are not independent,
        // so a diagonal move gets no more than the axes it is made of
        LinearDeltaKinematicsEngine engine = CreateLinearDelta();

        float[] maxFeedrates = new float[NumDrives];
        float[] accelerations = new float[NumDrives];
        Array.Fill(maxFeedrates, 100.0f);
        Array.Fill(accelerations, 1000.0f);
        maxFeedrates[1] = 60.0f;                    // Y is the slower axis

        float[] direction = new float[NumDrives];
        direction[0] = MathF.Sqrt(0.5f);
        direction[1] = MathF.Sqrt(0.5f);

        MoveLimits limits = new() { RequestedSpeed = 100.0f, MaxAcceleration = 1000.0f };
        PlannedMove move = new() { NormalisedDirectionVector = direction, NumVisibleAxes = 3 };
        engine.LimitSpeedAndAcceleration(ref limits, move, maxFeedrates, accelerations);

        // Halfway between the two limits, because the move is halfway between the two axes
        Assert.That(limits.RequestedSpeed, Is.EqualTo(80.0f).Within(0.01f));
    }

    [Test]
    public void APureZMoveIsNotRestrictedByTheXyLimits()
    {
        LinearDeltaKinematicsEngine engine = CreateLinearDelta();

        float[] maxFeedrates = new float[NumDrives];
        float[] accelerations = new float[NumDrives];
        Array.Fill(maxFeedrates, 10.0f);
        Array.Fill(accelerations, 100.0f);

        float[] direction = new float[NumDrives];
        direction[2] = 1.0f;

        MoveLimits limits = new() { RequestedSpeed = 200.0f, MaxAcceleration = 2000.0f };
        PlannedMove move = new() { NormalisedDirectionVector = direction, NumVisibleAxes = 3 };
        engine.LimitSpeedAndAcceleration(ref limits, move, maxFeedrates, accelerations);

        Assert.Multiple(() =>
        {
            Assert.That(limits.RequestedSpeed, Is.EqualTo(200.0f), "there is no XY component to limit");
            Assert.That(limits.MaxAcceleration, Is.EqualTo(2000.0f));
        });
    }
}
