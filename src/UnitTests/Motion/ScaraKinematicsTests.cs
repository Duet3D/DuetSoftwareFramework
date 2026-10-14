using System;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// The two SCARA geometries, serial and five-bar parallel
/// </summary>
/// <remarks>
/// Both work in joint angles rather than distances, so steps per mm on the arm drives are really steps
/// per degree. That makes the round trip the test that matters: it is the only check that the angles
/// the inverse transform produces are the ones the forward transform reads back
/// </remarks>
[TestFixture]
public class ScaraKinematicsTests
{
    private const int NumDrives = MotionLimits.MaxAxesPlusExtruders;

    /// <summary>Steps per degree on the arm drives, and steps per mm on Z</summary>
    private const float StepsPerUnit = 100.0f;

    private static float[] UniformStepsPerMm()
    {
        float[] stepsPerMm = new float[NumDrives];
        Array.Fill(stepsPerMm, StepsPerUnit);
        return stepsPerMm;
    }

    private static int[] ToMotorSteps(KinematicsEngine engine, float[] machinePos, int numAxes, bool isCoordinated = false,
                                      NativeMovementError expected = NativeMovementError.Ok)
    {
        int[] motorPos = new int[NumDrives];
        NativeMovementError error = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), numAxes, numAxes, motorPos, isCoordinated);
        Assert.That(error, Is.EqualTo(expected));
        return motorPos;
    }

    private static float[] ToCartesian(KinematicsEngine engine, int[] motorPos, int numAxes)
    {
        float[] machinePos = new float[NumDrives];
        engine.MotorStepsToCartesian(motorPos, UniformStepsPerMm(), numAxes, numAxes, machinePos);
        return machinePos;
    }

    // ---- Serial SCARA -------------------------------------------------------------------------------

    [TestCase(150.0f, 0.0f, 5.0f)]
    [TestCase(120.0f, 60.0f, 0.0f)]
    [TestCase(100.0f, -80.0f, 12.5f)]
    [TestCase(0.0f, 150.0f, -3.0f)]
    public void ScaraPositionsSurviveTheRoundTrip(float x, float y, float z)
    {
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Scara, KinematicsAxes.From());

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
    public void ArmsStraightOutPutTheHeadAtTheirCombinedLength()
    {
        // theta = 0 points the proximal arm along +X, and psi = 0 leaves the distal one in line with
        // it, so both angles zero is the one pose whose answer is obvious
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Scara, KinematicsAxes.From());

        int[] motorPos = new int[NumDrives];
        float[] machinePos = ToCartesian(engine, motorPos, 3);

        Assert.Multiple(() =>
        {
            Assert.That(machinePos[0], Is.EqualTo(200.0f).Within(0.01f));
            Assert.That(machinePos[1], Is.EqualTo(0.0f).Within(0.01f));
        });
    }

    [Test]
    public void TheBedOffsetShiftsWhereTheArmThinksTheOriginIs()
    {
        // xOffset and yOffset say where bed zero is relative to the pillar, so the same joint angles
        // report a position shifted by exactly that much
        KinematicsEngine offset = new ScaraKinematicsEngine(xOffset: 30.0f, yOffset: -20.0f).WithAxes();
        KinematicsEngine plain = KinematicsFactory.Create(KinematicsName.Scara, KinematicsAxes.From());

        int[] motorPos = new int[NumDrives];
        float[] offsetPos = ToCartesian(offset, motorPos, 3);
        float[] plainPos = ToCartesian(plain, motorPos, 3);

        Assert.Multiple(() =>
        {
            Assert.That(offsetPos[0], Is.EqualTo(plainPos[0] - 30.0f).Within(0.01f));
            Assert.That(offsetPos[1], Is.EqualTo(plainPos[1] + 20.0f).Within(0.01f));
        });
    }

    [Test]
    public void APositionTooFarOutIsUnreachable()
    {
        // Beyond the reach of both arms straightened out there is no pose at all
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Scara, KinematicsAxes.From());

        float[] machinePos = new float[NumDrives];
        machinePos[0] = 300.0f;

        int[] motorPos = new int[NumDrives];
        NativeMovementError error = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), 3, 3, motorPos);

        Assert.That(error, Is.EqualTo(NativeMovementError.UnreachablePosition));
    }

    [Test]
    public void APositionTooCloseInIsUnreachable()
    {
        // Folded back on itself the arm reaches a minimum radius, and inside that the two arms would
        // have to be more than folded
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Scara, KinematicsAxes.From());

        float[] machinePos = new float[NumDrives];
        machinePos[0] = 5.0f;

        int[] motorPos = new int[NumDrives];
        NativeMovementError error = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), 3, 3, motorPos);

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.EqualTo(NativeMovementError.UnreachablePosition));
        });
    }

    [Test]
    public void ACoordinatedMoveWillNotSwitchArmMode()
    {
        // Changing which way the elbow bends is a movement of its own, so a coordinated move that
        // would need one is refused rather than taken with the head wandering off the commanded line
        // With the proximal joint confined to the fourth quadrant, a point up and to the right needs
        // the elbow folded the other way: the pose the arm starts in would need theta above zero
        KinematicsEngine engine = new ScaraKinematicsEngine(thetaLimits: [-90.0f, 0.0f], psiLimits: [-135.0f, 135.0f]).WithAxes();
        float[] machinePos = [120.0f, 60.0f, 0.0f];

        int[] motorPos = new int[NumDrives];
        NativeMovementError coordinated = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), 3, 3, motorPos, isCoordinated: true);
        NativeMovementError uncoordinated = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), 3, 3, motorPos, isCoordinated: false);

        Assert.Multiple(() =>
        {
            Assert.That(coordinated, Is.EqualTo(NativeMovementError.UnreachablePosition), "a coordinated move may not fold the arm over");
            Assert.That(uncoordinated, Is.EqualTo(NativeMovementError.Ok), "an uncoordinated one may");
        });
    }

    [Test]
    public void CrosstalkIsPutInGoingOutAndTakenBackOutComingIn()
    {
        // On machines like the Helios, turning an arm drags the other joint and the Z column with it.
        // The correction is linear and fixed, so the round trip must come out exactly where it started
        KinematicsEngine engine = new ScaraKinematicsEngine(crosstalk: [0.1f, 0.05f, -0.02f]).WithAxes();

        int[] motorPos = ToMotorSteps(engine, [140.0f, 50.0f, 20.0f], 3);
        float[] roundTripped = ToCartesian(engine, motorPos, 3);

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped[0], Is.EqualTo(140.0f).Within(0.05f));
            Assert.That(roundTripped[1], Is.EqualTo(50.0f).Within(0.05f));
            Assert.That(roundTripped[2], Is.EqualTo(20.0f).Within(0.05f));
        });
    }

    [Test]
    public void ArmToZCrosstalkPullsTheZMotorIntoTheControllingDrives()
    {
        // Without crosstalk, holding X still needs the two arm motors. With arm-to-Z crosstalk it
        // needs the Z motor too, because letting an arm turn would drop the head
        KinematicsEngine plain = KinematicsFactory.Create(KinematicsName.Scara, KinematicsAxes.From());
        KinematicsEngine coupled = new ScaraKinematicsEngine(crosstalk: [0.0f, 0.05f, 0.0f]).WithAxes();

        Assert.Multiple(() =>
        {
            Assert.That(plain.GetControllingDrives(0), Is.EqualTo(0b011u));
            Assert.That(plain.GetControllingDrives(2), Is.EqualTo(0b100u), "Z stands alone");
            Assert.That(coupled.GetControllingDrives(0), Is.EqualTo(0b111u));
            Assert.That(coupled.GetControllingDrives(2), Is.EqualTo(0b111u));
        });
    }

    [Test]
    public void AJointThatTurnsMoreThanFullCircleIsContinuous()
    {
        KinematicsEngine limited = KinematicsFactory.Create(KinematicsName.Scara, KinematicsAxes.From());
        KinematicsEngine continuous = new ScaraKinematicsEngine(thetaLimits: [-200.0f, 200.0f], psiLimits: [-135.0f, 135.0f]).WithAxes();

        Assert.Multiple(() =>
        {
            Assert.That(limited.ContinuousRotationAxes, Is.EqualTo(0u));
            Assert.That(continuous.ContinuousRotationAxes, Is.EqualTo(0b01u), "the proximal joint goes round");
        });
    }

    [Test]
    public void AxesAboveZAreLinear()
    {
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Scara, KinematicsAxes.From());

        float[] machinePos = new float[NumDrives];
        machinePos[0] = 150.0f;
        machinePos[3] = 7.5f;

        int[] motorPos = ToMotorSteps(engine, machinePos, 4);
        Assert.That(motorPos[3], Is.EqualTo((int)(7.5f * StepsPerUnit)));

        float[] roundTripped = ToCartesian(engine, motorPos, 4);
        Assert.That(roundTripped[3], Is.EqualTo(7.5f).Within(0.01f));
    }

    // ---- Five-bar parallel SCARA --------------------------------------------------------------------

    [TestCase(0.0f, 150.0f, 5.0f)]
    [TestCase(20.0f, 140.0f, 0.0f)]
    [TestCase(-25.0f, 130.0f, 10.0f)]
    public void FiveBarPositionsSurviveTheRoundTrip(float x, float y, float z)
    {
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.FiveBarScara, KinematicsAxes.From());

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
    public void TheWorkModeDecidesWhichWayTheElbowsBend()
    {
        // Work mode 2 buckles the left elbow and bulges the right, which on a symmetric linkage is the
        // symmetric pose: the left angle measured from +X mirrors the right one measured from 180.
        // Work mode 1 turns both elbows the same way instead, so the same point gives a lopsided pose
        KinematicsEngine symmetric = new FiveBarScaraKinematicsEngine(
            xOrigL: -50.0f, yOrigL: 0.0f, xOrigR: 50.0f, yOrigR: 0.0f,
            proximalL: 100.0f, proximalR: 100.0f, distalL: 100.0f, distalR: 100.0f, workMode: 2).WithAxes();

        int[] symmetricPos = ToMotorSteps(symmetric, [0.0f, 150.0f, 0.0f], 3);
        Assert.That((symmetricPos[0] + symmetricPos[1]) / StepsPerUnit, Is.EqualTo(180.0f).Within(0.05f));

        int[] lopsidedPos = ToMotorSteps(KinematicsFactory.Create(KinematicsName.FiveBarScara, KinematicsAxes.From()), [0.0f, 150.0f, 0.0f], 3);
        Assert.That(lopsidedPos[0], Is.Not.EqualTo(symmetricPos[0]), "work mode 1 reaches the same point another way");
    }

    [Test]
    public void EachWorkModeRoundTripsInItsOwnPose()
    {
        // The forward transform has to pick the same one of the two circle intersections that the
        // inverse transform did, or the machine reads back a pose it is not in
        int modesTested = 0;
        foreach (int workMode in new[] { 1, 2, 4 })
        {
            KinematicsEngine engine = new FiveBarScaraKinematicsEngine(
                xOrigL: -50.0f, yOrigL: 0.0f, xOrigR: 50.0f, yOrigR: 0.0f,
                proximalL: 100.0f, proximalR: 100.0f, distalL: 100.0f, distalR: 100.0f, workMode: workMode).WithAxes();

            int[] motorPos = new int[NumDrives];
            if (engine.CartesianToMotorSteps([15.0f, 145.0f, 0.0f], UniformStepsPerMm(), 3, 3, motorPos) == NativeMovementError.UnreachablePosition)
            {
                continue;               // this pose is not one this work mode can hold
            }
            modesTested++;

            float[] roundTripped = ToCartesian(engine, motorPos, 3);

            Assert.Multiple(() =>
            {
                Assert.That(roundTripped[0], Is.EqualTo(15.0f).Within(0.1f), $"work mode {workMode} X");
                Assert.That(roundTripped[1], Is.EqualTo(145.0f).Within(0.1f), $"work mode {workMode} Y");
            });
        }

        Assert.That(modesTested, Is.GreaterThan(1), "more than one work mode reaches that point");
    }

    [Test]
    public void APositionTheArmsCannotSpanIsUnreachable()
    {
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.FiveBarScara, KinematicsAxes.From());

        float[] machinePos = new float[NumDrives];
        machinePos[1] = 400.0f;

        int[] motorPos = new int[NumDrives];
        NativeMovementError error = engine.CartesianToMotorSteps(machinePos, UniformStepsPerMm(), 3, 3, motorPos);

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.EqualTo(NativeMovementError.UnreachablePosition));
        });
    }

    [Test]
    public void APoseThatFoldsTheDistalArmsTogetherIsRefused()
    {
        // Far out, the two distal arms come nearly into line with each other and the linkage loses
        // control of the head. The head angle limit is what keeps the machine out of there
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.FiveBarScara, KinematicsAxes.From());

        int[] motorPos = new int[NumDrives];
        Assert.Multiple(() =>
        {
            Assert.That(engine.CartesianToMotorSteps([0.0f, 150.0f, 0.0f], UniformStepsPerMm(), 3, 3, motorPos),
                        Is.EqualTo(NativeMovementError.Ok));
            Assert.That(engine.CartesianToMotorSteps([0.0f, 195.0f, 0.0f], UniformStepsPerMm(), 3, 3, motorPos),
                        Is.EqualTo(NativeMovementError.UnreachablePosition), "the arms would be almost straight");
        });
    }

    [Test]
    public void ACantileveredHeadReachesPastTheSharedJoint()
    {
        // With a cantilever the head is not at the joint but out along the left distal arm, so the
        // same joint angles put it somewhere else entirely
        KinematicsEngine plain = KinematicsFactory.Create(KinematicsName.FiveBarScara, KinematicsAxes.From());
        KinematicsEngine cantilevered = new FiveBarScaraKinematicsEngine(
            xOrigL: -50.0f, yOrigL: 0.0f, xOrigR: 50.0f, yOrigR: 0.0f,
            proximalL: 100.0f, proximalR: 100.0f, distalL: 100.0f, distalR: 100.0f, cantL: 40.0f).WithAxes();

        int[] motorPos = ToMotorSteps(plain, [0.0f, 150.0f, 0.0f], 3);

        float[] plainPos = ToCartesian(plain, motorPos, 3);
        float[] cantileveredPos = ToCartesian(cantilevered, motorPos, 3);

        float shift = MathF.Sqrt(MathF.Pow(cantileveredPos[0] - plainPos[0], 2) + MathF.Pow(cantileveredPos[1] - plainPos[1], 2));
        Assert.That(shift, Is.EqualTo(40.0f).Within(0.05f), "the head is a cantilever length past the joint");
    }

    [Test]
    public void ACantileveredRoundTripStillComesBack()
    {
        KinematicsEngine engine = new FiveBarScaraKinematicsEngine(
            xOrigL: -50.0f, yOrigL: 0.0f, xOrigR: 50.0f, yOrigR: 0.0f,
            proximalL: 100.0f, proximalR: 100.0f, distalL: 100.0f, distalR: 100.0f, cantL: 40.0f).WithAxes();

        int[] motorPos = ToMotorSteps(engine, [10.0f, 160.0f, 0.0f], 3);
        float[] roundTripped = ToCartesian(engine, motorPos, 3);

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped[0], Is.EqualTo(10.0f).Within(0.1f));
            Assert.That(roundTripped[1], Is.EqualTo(160.0f).Within(0.1f));
        });
    }

    [Test]
    public void BothActuatorsHoldEitherAxisStill()
    {
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.FiveBarScara, KinematicsAxes.From());

        Assert.Multiple(() =>
        {
            Assert.That(engine.GetControllingDrives(0), Is.EqualTo(0b011u));
            Assert.That(engine.GetControllingDrives(1), Is.EqualTo(0b011u));
            Assert.That(engine.GetControllingDrives(2), Is.EqualTo(0b100u), "Z has a motor to itself");
            Assert.That(engine.ContinuousRotationAxes, Is.EqualTo(0b011u), "both actuators turn about a fixed point");
        });
    }
}
