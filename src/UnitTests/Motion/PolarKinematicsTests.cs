using System;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// The polar geometry, where a radius arm works over a turning bed
/// </summary>
/// <remarks>
/// The transform is textbook polar coordinates, so most of what is worth testing is what happens at
/// the awkward places: the centre, where the angle stops meaning anything, and the speed limit that
/// keeps the turntable from being spun faster than it can go when a move passes close to it
/// </remarks>
[TestFixture]
public class PolarKinematicsTests
{
    private const int NumDrives = MotionLimits.MaxAxesPlusExtruders;

    /// <summary>Steps per mm on the radius drive, and steps per degree on the turntable</summary>
    private const float StepsPerUnit = 100.0f;

    private static float[] UniformStepsPerMm()
    {
        float[] stepsPerMm = new float[NumDrives];
        Array.Fill(stepsPerMm, StepsPerUnit);
        return stepsPerMm;
    }

    /// <summary>
    /// A bed of 150mm radius with the turntable limits RepRapFirmware assumes
    /// </summary>
    /// <remarks>
    /// The limits go in as M669 F and A give them, degrees per second. The engine converts to step
    /// clocks for the planner, so a speed limit these tests assert on is expressed through
    /// <see cref="PolarKinematicsEngine.MaxTurntableSpeed"/> rather than in degrees per second
    /// </remarks>
    private static PolarKinematicsEngine CreatePolar(float maxTurntableSpeed = 30.0f, float maxTurntableAcceleration = 30.0f)
        => new(minRadius: 0.0f, maxRadius: 150.0f, homedRadius: 0.0f,
               maxTurntableSpeed: maxTurntableSpeed, maxTurntableAcceleration: maxTurntableAcceleration);

    private static int[] ToMotorSteps(KinematicsEngine engine, float[] machinePos, int numAxes)
    {
        int[] motorPos = new int[NumDrives];
        NativeMovementError error = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), numAxes, numAxes, motorPos);
        Assert.That(error, Is.EqualTo(NativeMovementError.Ok));
        return motorPos;
    }

    private static float[] ToCartesian(KinematicsEngine engine, int[] motorPos, int numAxes)
    {
        float[] machinePos = new float[NumDrives];
        engine.MotorStepsToCartesian(motorPos, UniformStepsPerMm(), numAxes, numAxes, machinePos);
        return machinePos;
    }

    [Test]
    public void APositionOnThePlusXAxisIsAllRadiusAndNoAngle()
    {
        PolarKinematicsEngine engine = CreatePolar();

        int[] motorPos = ToMotorSteps(engine, [100.0f, 0.0f, 5.0f], 3);
        Assert.Multiple(() =>
        {
            Assert.That(motorPos[0], Is.EqualTo((int)(100.0f * StepsPerUnit)));
            Assert.That(motorPos[1], Is.EqualTo(0));
            Assert.That(motorPos[2], Is.EqualTo((int)(5.0f * StepsPerUnit)), "Z is linear");
        });
    }

    [Test]
    public void AQuarterTurnPutsTheHeadOnThePlusYAxis()
    {
        PolarKinematicsEngine engine = CreatePolar();

        int[] motorPos = ToMotorSteps(engine, [0.0f, 100.0f, 0.0f], 3);
        Assert.Multiple(() =>
        {
            Assert.That(motorPos[0], Is.EqualTo((int)(100.0f * StepsPerUnit)));
            Assert.That(motorPos[1] / StepsPerUnit, Is.EqualTo(90.0f).Within(0.01f));
        });
    }

    [TestCase(100.0f, 0.0f, 0.0f)]
    [TestCase(0.0f, 120.0f, 5.0f)]
    [TestCase(-60.0f, 80.0f, -2.5f)]
    [TestCase(70.0f, -70.0f, 10.0f)]
    public void PolarPositionsSurviveTheRoundTrip(float x, float y, float z)
    {
        PolarKinematicsEngine engine = CreatePolar();

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
    public void DeadCentreLeavesTheTurntableWhereItIs()
    {
        // At the centre every angle puts the head in the same place, so turning the bed would be
        // movement for nothing - and the angle itself is not defined there
        PolarKinematicsEngine engine = CreatePolar();

        int[] motorPos = new int[NumDrives];
        motorPos[1] = 12345;

        NativeMovementError error = engine.CartesianToMotorSteps(new float[NumDrives], UniformStepsPerMm(), 3, 3, motorPos);

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.EqualTo(NativeMovementError.Ok));
            Assert.That(motorPos[0], Is.EqualTo(0));
            Assert.That(motorPos[1], Is.EqualTo(0));
        });
    }

    [Test]
    public void BothMotorsHoldEitherOfXAndYStill()
    {
        PolarKinematicsEngine engine = CreatePolar();

        Assert.Multiple(() =>
        {
            Assert.That(engine.GetControllingDrives(0), Is.EqualTo(0b011u));
            Assert.That(engine.GetControllingDrives(1), Is.EqualTo(0b011u));
            Assert.That(engine.GetControllingDrives(2), Is.EqualTo(0b100u), "Z has a motor to itself");
        });
    }

    [Test]
    public void TheTurntableIsAContinuousRotationAxis()
    {
        PolarKinematicsEngine engine = CreatePolar();
        Assert.That(engine.ContinuousRotationAxes, Is.EqualTo(0b010u));
    }

    [Test]
    public void AnnulusLimitsAreEnforcedAtBothEnds()
    {
        PolarKinematicsEngine engine = new(minRadius: 20.0f, maxRadius: 150.0f, homedRadius: 20.0f,
                                           maxTurntableSpeed: 30.0f, maxTurntableAcceleration: 30.0f);

        Assert.Multiple(() =>
        {
            Assert.That(engine.IsReachable(100.0f, 0.0f), Is.True);
            Assert.That(engine.IsReachable(10.0f, 0.0f), Is.False, "inside the minimum radius");
            Assert.That(engine.IsReachable(200.0f, 0.0f), Is.False, "outside the bed");
        });
    }

    [Test]
    public void ATightArcNearTheCentreIsSlowedToWhatTheTurntableCanDo()
    {
        // Half a turn over one millimetre of travel is 180 degrees of rotation per mm, and the
        // turntable will only do 30 degrees per second, so the move is held to a sixth of a mm/s
        PolarKinematicsEngine engine = CreatePolar(maxTurntableSpeed: 30.0f, maxTurntableAcceleration: 30.0f);

        int[] start = new int[NumDrives];
        int[] end = new int[NumDrives];
        end[1] = (int)(180.0f * StepsPerUnit);

        float[] direction = new float[NumDrives];
        direction[0] = 1.0f;

        MoveLimits limits = new() { RequestedSpeed = 100.0f, MaxAcceleration = 1000.0f };
        PlannedMove move = new()
        {
            NormalisedDirectionVector = direction,
            StartMotorPos = start,
            EndMotorPos = end,
            StepsPerMm = UniformStepsPerMm(),
            NumVisibleAxes = 3,
            TotalDistance = 1.0f,
            ContinuousRotationShortcut = false
        };
        engine.LimitSpeedAndAcceleration(ref limits, move, new float[NumDrives], new float[NumDrives]);

        Assert.Multiple(() =>
        {
            Assert.That(limits.RequestedSpeed, Is.EqualTo(engine.MaxTurntableSpeed / 180.0f).Within(1.0e-10f));
            Assert.That(limits.MaxAcceleration, Is.EqualTo(engine.MaxTurntableAcceleration / 180.0f).Within(1.0e-16f));
        });
    }

    [Test]
    public void AMoveThatDoesNotTurnTheBedIsNotSlowed()
    {
        // Straight in and out along a radius: the turntable does not move, so it imposes nothing
        PolarKinematicsEngine engine = CreatePolar();

        int[] start = new int[NumDrives];
        int[] end = new int[NumDrives];
        end[0] = (int)(50.0f * StepsPerUnit);

        MoveLimits limits = new() { RequestedSpeed = 100.0f, MaxAcceleration = 1000.0f };
        PlannedMove move = new()
        {
            NormalisedDirectionVector = new float[NumDrives],
            StartMotorPos = start,
            EndMotorPos = end,
            StepsPerMm = UniformStepsPerMm(),
            NumVisibleAxes = 3,
            TotalDistance = 50.0f
        };
        engine.LimitSpeedAndAcceleration(ref limits, move, new float[NumDrives], new float[NumDrives]);

        Assert.Multiple(() =>
        {
            Assert.That(limits.RequestedSpeed, Is.EqualTo(100.0f));
            Assert.That(limits.MaxAcceleration, Is.EqualTo(1000.0f));
        });
    }

    [Test]
    public void GoingTheShortWayRoundIsWhatGetsLimited()
    {
        // A commanded turn of 350 degrees is really 10 degrees the other way, and the limit has to be
        // worked out for the rotation the machine will actually make rather than the one it was asked
        // for - otherwise a move that barely turns the bed gets throttled as if it spun it right round
        PolarKinematicsEngine engine = CreatePolar();

        int[] start = new int[NumDrives];
        int[] end = new int[NumDrives];
        end[1] = (int)(350.0f * StepsPerUnit);

        float[] stepsPerMm = UniformStepsPerMm();

        MoveLimits shortcut = new() { RequestedSpeed = 100.0f, MaxAcceleration = 1000.0f };
        PlannedMove withShortcut = new()
        {
            NormalisedDirectionVector = new float[NumDrives],
            StartMotorPos = start,
            EndMotorPos = end,
            StepsPerMm = stepsPerMm,
            NumVisibleAxes = 3,
            TotalDistance = 1.0f,
            ContinuousRotationShortcut = true
        };
        engine.LimitSpeedAndAcceleration(ref shortcut, withShortcut, new float[NumDrives], new float[NumDrives]);

        MoveLimits longWay = new() { RequestedSpeed = 100.0f, MaxAcceleration = 1000.0f };
        PlannedMove withoutShortcut = new()
        {
            NormalisedDirectionVector = new float[NumDrives],
            StartMotorPos = start,
            EndMotorPos = end,
            StepsPerMm = stepsPerMm,
            NumVisibleAxes = 3,
            TotalDistance = 1.0f,
            ContinuousRotationShortcut = false
        };
        engine.LimitSpeedAndAcceleration(ref longWay, withoutShortcut, new float[NumDrives], new float[NumDrives]);

        Assert.Multiple(() =>
        {
            Assert.That(shortcut.RequestedSpeed, Is.EqualTo(engine.MaxTurntableSpeed / 10.0f).Within(1.0e-10f), "ten degrees the short way");
            Assert.That(longWay.RequestedSpeed, Is.EqualTo(engine.MaxTurntableSpeed / 350.0f).Within(1.0e-10f), "three hundred and fifty the long way");
        });
    }

    [Test]
    public void AxesAboveZAreLinear()
    {
        PolarKinematicsEngine engine = CreatePolar();

        float[] machinePos = new float[NumDrives];
        machinePos[0] = 100.0f;
        machinePos[3] = 4.0f;

        int[] motorPos = ToMotorSteps(engine, machinePos, 4);
        Assert.That(motorPos[3], Is.EqualTo((int)(4.0f * StepsPerUnit)));

        float[] roundTripped = ToCartesian(engine, motorPos, 4);
        Assert.That(roundTripped[3], Is.EqualTo(4.0f).Within(0.01f));
    }
}
