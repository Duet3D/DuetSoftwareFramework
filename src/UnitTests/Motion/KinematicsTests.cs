using System;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// The matrix-driven geometries, against the machines they describe
/// </summary>
/// <remarks>
/// The property that matters most is the round trip: a position converted to motor steps and back
/// must come out where it started. That is what says the forward matrix really is the inverse of the
/// inverse matrix, which is derived rather than configured and so has no other check on it
/// </remarks>
[TestFixture]
public class KinematicsTests
{
    private const int NumDrives = MotionLimits.MaxAxesPlusExtruders;
    private const float StepsPerMm = 80.0f;

    private static float[] UniformStepsPerMm()
    {
        float[] stepsPerMm = new float[NumDrives];
        Array.Fill(stepsPerMm, StepsPerMm);
        return stepsPerMm;
    }

    private static int[] ToMotorSteps(KinematicsEngine engine, float[] machinePos, int numAxes)
    {
        int[] motorPos = new int[NumDrives];
        NativeMovementError error = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), numAxes, numAxes, motorPos);
        Assert.That(error, Is.EqualTo(NativeMovementError.Ok));
        return motorPos;
    }

    [Test]
    public void CartesianMovesOneMotorPerAxis()
    {
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate("cartesian")!;
        Assert.That(engine.IsValid, Is.True);

        int[] motorPos = ToMotorSteps(engine, [10.0f, 20.0f, 5.0f], 3);
        Assert.Multiple(() =>
        {
            Assert.That(motorPos[0], Is.EqualTo(800));
            Assert.That(motorPos[1], Is.EqualTo(1600));
            Assert.That(motorPos[2], Is.EqualTo(400));
            Assert.That(engine.HasSharedMotor(0), Is.False, "no motor is shared on a Cartesian machine");
        });
    }

    [Test]
    public void CoreXyMovesBothMotorsForEitherAxis()
    {
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate("corexy")!;
        Assert.That(engine.IsValid, Is.True);

        // A = X + Y, B = X - Y. Moving X alone turns both motors the same way; moving Y alone turns
        // them opposite ways. That is the whole of CoreXY
        int[] xOnly = ToMotorSteps(engine, [10.0f, 0.0f, 0.0f], 3);
        Assert.Multiple(() =>
        {
            Assert.That(xOnly[0], Is.EqualTo(800));
            Assert.That(xOnly[1], Is.EqualTo(800));
            Assert.That(xOnly[2], Is.EqualTo(0));
        });

        int[] yOnly = ToMotorSteps(engine, [0.0f, 10.0f, 0.0f], 3);
        Assert.Multiple(() =>
        {
            Assert.That(yOnly[0], Is.EqualTo(800));
            Assert.That(yOnly[1], Is.EqualTo(-800));
        });

        Assert.Multiple(() =>
        {
            Assert.That(engine.HasSharedMotor(0), Is.True, "X shares its motors with Y");
            Assert.That(engine.HasSharedMotor(1), Is.True, "Y shares its motors with X");
            Assert.That(engine.HasSharedMotor(2), Is.False, "Z has a motor to itself");
        });
    }

    [Test]
    public void CoreXyControllingDrivesCoverBothMotors()
    {
        // The native planner uses this to decide which drivers to energise. Holding X still on a
        // CoreXY needs both motors: either one turning alone would move it
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate("corexy")!;
        Assert.Multiple(() =>
        {
            Assert.That(engine.GetControllingDrives(0), Is.EqualTo(0b011u));
            Assert.That(engine.GetControllingDrives(1), Is.EqualTo(0b011u));
            Assert.That(engine.GetControllingDrives(2), Is.EqualTo(0b100u));
        });
    }

    [Test]
    public void CartesianControllingDrivesAreOneEach()
    {
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate("cartesian")!;
        Assert.Multiple(() =>
        {
            Assert.That(engine.GetControllingDrives(0), Is.EqualTo(0b001u));
            Assert.That(engine.GetControllingDrives(1), Is.EqualTo(0b010u));
            Assert.That(engine.GetControllingDrives(2), Is.EqualTo(0b100u));
        });
    }

    [TestCase("cartesian", 3)]
    [TestCase("corexy", 3)]
    [TestCase("corexz", 3)]
    [TestCase("markforged", 3)]
    [TestCase("corexyu", 4)]
    [TestCase("corexyuv", 5)]
    public void PositionsSurviveTheRoundTrip(string name, int numAxes)
    {
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate(name)!;
        Assert.That(engine.IsValid, Is.True, "the forward matrix could be derived");

        float[] machinePos = new float[NumDrives];
        for (int axis = 0; axis < numAxes; axis++)
        {
            // Whole numbers of microsteps, so the comparison is not measuring rounding
            machinePos[axis] = (axis + 1) * 2.5f;
        }

        int[] motorPos = ToMotorSteps(engine, machinePos, numAxes);

        float[] roundTripped = new float[NumDrives];
        engine.MotorStepsToCartesian(motorPos, UniformStepsPerMm(), numAxes, numAxes, roundTripped);

        for (int axis = 0; axis < numAxes; axis++)
        {
            Assert.That(roundTripped[axis], Is.EqualTo(machinePos[axis]).Within(1.0f / StepsPerMm),
                        $"{name} axis {axis} returns to where it started");
        }
    }

    [Test]
    public void UndescribedAxesKeepTheirOwnMotor()
    {
        // A 3x3 matrix describes a three-axis machine. A fourth axis added later must still get a
        // motor of its own rather than inheriting a row of zeroes and never moving
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate("cartesian")!;

        float[] machinePos = new float[NumDrives];
        machinePos[3] = 10.0f;
        int[] motorPos = ToMotorSteps(engine, machinePos, 4);

        Assert.That(motorPos[3], Is.EqualTo(800));
    }

    [Test]
    public void MotorsNoVisibleAxisDrivesAreLeftAlone()
    {
        // CartesianToMotorSteps leaves such a motor as it found it, which is how the caller carries
        // an untouched drive forward from the previous move
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate("cartesian")!;

        int[] motorPos = new int[NumDrives];
        motorPos[5] = 12345;

        NativeMovementError error = engine.CartesianToMotorSteps(
            new float[NumDrives], UniformStepsPerMm(), numVisibleAxes: 3, numTotalAxes: 3, motorPos);

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.EqualTo(NativeMovementError.Ok));
            Assert.That(motorPos[5], Is.EqualTo(12345));
        });
    }

    [Test]
    public void APositionTooFarFromTheOriginIsReported()
    {
        // Endpoints are 32-bit microstep counts and moves are differences between them, so a value
        // that does not fit does not lose precision, it wraps - and the move that reads it commands
        // the drive most of the way round the 32-bit range
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate("cartesian")!;

        float[] machinePos = new float[NumDrives];
        machinePos[0] = 1.0e9f;             // 1e9 mm at 80 steps/mm is 8e10 microsteps

        int[] motorPos = new int[NumDrives];
        NativeMovementError error = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), 3, 3, motorPos);

        Assert.That(error, Is.EqualTo(NativeMovementError.MicrostepPositionTooLarge));
    }

    [Test]
    public void CoreXyLimitsADiagonalMoveByTheSharedMotors()
    {
        // On a Cartesian machine a 45-degree move may go faster than either axis alone, because the
        // two axes are limited independently. On CoreXY both motors turn for either axis, so the
        // move has to be slowed to what a single motor can manage
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate("corexy")!;

        float[] maxFeedrates = new float[NumDrives];
        float[] accelerations = new float[NumDrives];
        Array.Fill(maxFeedrates, 100.0f);
        Array.Fill(accelerations, 1000.0f);

        // Unit vector at 45 degrees in XY, in the positive hyperquadrant as the caller supplies it
        float[] direction = new float[NumDrives];
        direction[0] = MathF.Sqrt(0.5f);
        direction[1] = MathF.Sqrt(0.5f);

        MoveLimits limits = new() { RequestedSpeed = 100.0f, MaxAcceleration = 1000.0f };
        PlannedMove move = new() { NormalisedDirectionVector = direction, NumVisibleAxes = 3 };
        engine.LimitSpeedAndAcceleration(ref limits, move, maxFeedrates, accelerations);

        // Motor A moves (X+Y)/sqrt(2) = sqrt(2) times as far as the move itself, so the move is
        // limited to 1/sqrt(2) of what the motor can do
        float expected = 100.0f / MathF.Sqrt(2.0f);
        Assert.Multiple(() =>
        {
            Assert.That(limits.RequestedSpeed, Is.EqualTo(expected).Within(0.01f));
            Assert.That(limits.MaxAcceleration, Is.EqualTo(1000.0f / MathF.Sqrt(2.0f)).Within(0.1f));
        });
    }

    [Test]
    public void CartesianAddsNoLimitOfItsOwn()
    {
        CoreKinematicsEngine engine = CoreKinematicsEngine.TryCreate("cartesian")!;

        float[] maxFeedrates = new float[NumDrives];
        float[] accelerations = new float[NumDrives];
        Array.Fill(maxFeedrates, 100.0f);
        Array.Fill(accelerations, 1000.0f);

        float[] direction = new float[NumDrives];
        direction[0] = MathF.Sqrt(0.5f);
        direction[1] = MathF.Sqrt(0.5f);

        MoveLimits limits = new() { RequestedSpeed = 100.0f, MaxAcceleration = 1000.0f };
        PlannedMove move = new() { NormalisedDirectionVector = direction, NumVisibleAxes = 3 };
        engine.LimitSpeedAndAcceleration(ref limits, move, maxFeedrates, accelerations);

        Assert.Multiple(() =>
        {
            Assert.That(limits.RequestedSpeed, Is.EqualTo(100.0f), "no motor is shared, so nothing is restricted");
            Assert.That(limits.MaxAcceleration, Is.EqualTo(1000.0f));
        });
    }

    [Test]
    public void AnUnknownGeometryIsNotCreated()
    {
        Assert.That(CoreKinematicsEngine.TryCreate("delta"), Is.Null);
    }

    [Test]
    public void ASingularMatrixIsRejected()
    {
        // Two axes driving the same motor identically: no set of motor positions distinguishes them,
        // so there is no forward transform and the geometry does not describe a real machine
        CoreKinematicsEngine engine = new("broken", [[1, 0, 0], [1, 0, 0], [0, 0, 1]]);
        Assert.That(engine.IsValid, Is.False);
    }
}
