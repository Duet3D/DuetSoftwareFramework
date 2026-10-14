using System;
using DuetAPI.ObjectModel;
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
        // Where the towers stand is what the object model reports for each of them
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From());
        StaticModelCollection<DeltaTower> towers = KinematicsAxes.Reported<DeltaKinematics>(engine).Towers;

        // A is at -150 degrees, B at -30 and C at +90, all at the delta radius from the centre
        for (int tower = 0; tower < LinearDeltaKinematicsEngine.UsualNumTowers; tower++)
        {
            float distance = MathF.Sqrt(MathF.Pow(towers[tower].XPos, 2) + MathF.Pow(towers[tower].YPos, 2));
            Assert.That(distance, Is.EqualTo(LinearDeltaKinematicsEngine.DefaultDeltaRadius).Within(0.01f),
                        $"tower {tower} stands at the delta radius");
        }

        Assert.Multiple(() =>
        {
            Assert.That(towers[2].XPos, Is.EqualTo(0.0f).Within(0.01f), "the C tower is straight back");
            Assert.That(towers[2].YPos, Is.EqualTo(LinearDeltaKinematicsEngine.DefaultDeltaRadius).Within(0.01f));
            Assert.That(towers[0].XPos, Is.EqualTo(-towers[1].XPos).Within(0.01f), "A and B are mirrored in X");
            Assert.That(towers[0].YPos, Is.EqualTo(towers[1].YPos).Within(0.01f), "and level in Y");
        });
    }

    [Test]
    public void AtTheCentreAllThreeCarriagesAreLevel()
    {
        // Symmetry: the centre of the bed is the same distance from every tower, so every rod hangs at
        // the same angle and every carriage is at the same height
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From());

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
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From());

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
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From());

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
        LinearDeltaKinematicsEngine engine = new LinearDeltaKinematicsEngine(
            numTowers: 3, radius: 105.6f,
            diagonals: [215.0f, 217.5f, 213.0f],
            angleCorrections: [0.4f, -0.3f, 0.15f],
            endstopAdjustments: [0.2f, -0.1f, 0.05f],
            homedHeight: 240.0f, printRadius: 80.0f).WithAxes();

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
        LinearDeltaKinematicsEngine engine = new LinearDeltaKinematicsEngine(
            numTowers: 3, radius: 105.6f,
            diagonals: [215.0f, 215.0f, 215.0f],
            angleCorrections: [0.0f, 0.0f, 0.0f],
            endstopAdjustments: [0.0f, 0.0f, 0.0f],
            homedHeight: 240.0f, printRadius: 80.0f,
            xTilt: 0.01f, yTilt: -0.005f).WithAxes();

        float[] direction = [3.0f, 4.0f, 1.0f];
        engine.ApplyTiltCorrection(direction, 3);

        Assert.Multiple(() =>
        {
            Assert.That(direction[0], Is.EqualTo(3.0f), "X itself is not corrected");
            Assert.That(direction[1], Is.EqualTo(4.0f), "Y itself is not corrected");
            Assert.That(direction[2], Is.EqualTo(1.0f + (3.0f * 0.01f) + (4.0f * -0.005f)).Within(1e-6f));
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
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From());

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
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From());

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
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From());

        float[] machinePos = new float[NumDrives];
        machinePos[2] = 100.0f;
        machinePos[3] = 12.5f;

        int[] motorPos = ToMotorSteps(engine, machinePos, 4);
        Assert.That(motorPos[3], Is.EqualTo((int)(12.5f * StepsPerMm)));

        float[] roundTripped = ToCartesian(engine, motorPos, 4);
        Assert.That(roundTripped[3], Is.EqualTo(12.5f).Within(0.01f));
    }

    [Test]
    public void TheHeightReachableEverywhereIsBelowTheHomedHeight()
    {
        // The ceiling sags away from the centre, so the height that is reachable everywhere is lower
        // than the height reachable at the middle, which is just under the homed height of 240mm
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From());

        float[] middle = [0.0f, 0.0f, 239.9f];
        float[] edge = [LinearDeltaKinematicsEngine.DefaultPrintRadius, 0.0f, 239.9f];
        Assert.Multiple(() =>
        {
            Assert.That(engine.LimitPosition(middle, [], 3, 0b111, isCoordinated: true, applyM208Limits: false),
                        Is.EqualTo(LimitPositionResult.Ok));
            Assert.That(engine.LimitPosition(edge, [], 3, 0b111, isCoordinated: true, applyM208Limits: false),
                        Is.EqualTo(LimitPositionResult.Adjusted));
            Assert.That(edge[2], Is.LessThan(middle[2]), "brought down below the homed height");
        });
    }

    [Test]
    public void APositionOutsideThePrintRadiusIsNotReachable()
    {
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From());

        float justOutside = LinearDeltaKinematicsEngine.DefaultPrintRadius + 1.0f;
        float[] outside = [justOutside, 0.0f, 50.0f];
        float[] inside = [0.0f, 0.0f, 50.0f];
        Assert.Multiple(() =>
        {
            Assert.That(engine.LimitPosition(outside, [], 3, 0b111, isCoordinated: true, applyM208Limits: true),
                        Is.EqualTo(LimitPositionResult.Adjusted));
            Assert.That(engine.LimitPosition(inside, [], 3, 0b111, isCoordinated: true, applyM208Limits: true),
                        Is.EqualTo(LimitPositionResult.Ok));
        });
    }

    // ---- Rotary delta ------------------------------------------------------------------------------

    [Test]
    public void RotaryDeltaArmsAreLevelAtTheCentre()
    {
        // The same symmetry argument as for the linear delta: at the centre every arm is swung to the
        // same angle
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.RotaryDelta, KinematicsAxes.From());

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
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.RotaryDelta, KinematicsAxes.From());

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
        RotaryDeltaKinematicsEngine engine = new RotaryDeltaKinematicsEngine(
            radius: 50.0f,
            armLengths: [100.0f, 104.0f, 97.0f],
            rodLengths: [200.0f, 203.0f, 198.0f],
            bearingHeights: [250.0f, 251.5f, 249.0f],
            angleCorrections: [0.3f, -0.2f, 0.1f],
            endstopAdjustments: [0.0f, 0.0f, 0.0f]).WithAxes();

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
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.RotaryDelta, KinematicsAxes.From());

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
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.RotaryDelta, KinematicsAxes.From());
        Assert.Multiple(() =>
        {
            Assert.That(engine.GetControllingDrives(0), Is.EqualTo(0b111u));
            Assert.That(engine.GetControllingDrives(2), Is.EqualTo(0b111u));
            Assert.That(engine.GetControllingDrives(4), Is.EqualTo(0b10000u));
        });
    }

    // ---- Both --------------------------------------------------------------------------------------

    [Test]
    public void ADeltaLimitsADiagonalMoveToTheSlowerOfXAndY()
    {
        // The default limit, which every non-Cartesian geometry inherits: X and Y are not independent,
        // so a diagonal move gets no more than the axes it is made of
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From());

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
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.LinearDelta, KinematicsAxes.From());

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
