using System;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using OmDriverId = DuetAPI.Utility.DriverId;

namespace UnitTests.Motion;

/// <summary>
/// The translation from the object model into what the planner and the native engine take
/// </summary>
/// <remarks>
/// This is a unit conversion boundary, and unit conversions are where a configuration bug hides
/// without ever looking wrong. The object model is in the units its properties are documented in -
/// mm/min for speeds and jerk, mm/s^2 for acceleration, seconds for pressure advance - and both
/// consumers work in step clocks
/// </remarks>
[TestFixture]
public class MotionParametersTests
{

    /// <summary>
    /// Snapshot a machine, building its geometry from the object model as the factory does
    /// </summary>
    /// <param name="move">The move subsystem</param>
    /// <returns>The snapshot</returns>
    /// <remarks>
    /// The planner owns its geometry rather than deriving it (§14), so the snapshot is handed one.
    /// These tests describe a machine as an object model and want the geometry that describes, which
    /// is what KinematicsFactory.Create is for
    /// </remarks>
    private static MotionParameters Snapshot(Move move)
        => MotionParameters.FromObjectModel(move, KinematicsFactory.Create(move.Kinematics));
    private const float StepClockRate = MotionLimits.StepClockRate;
    private const int NumDrives = MotionLimits.MaxAxesPlusExtruders;

    private static Move MachineWithOneOfEach()
    {
        Move move = new()
        {
            MinimumMovementSpeed = 0.5f,
            JerkPolicy = 1,
            BacklashFactor = 7
        };

        // The move-wide acceleration caps live on the motion system, which is where M204 writes them
        move.MotionSystems.Add(new MotionSystem
        {
            PrintingAcceleration = 500.0f,
            TravelAcceleration = 1000.0f
        });

        Axis x = new()
        {
            Letter = 'X',
            StepsPerMm = 80.0f,
            Speed = 6000.0f,            // mm/min, i.e. 100 mm/sec
            Acceleration = 1000.0f,     // mm/sec^2
            Jerk = 900.0f,              // mm/min, i.e. 15 mm/sec
            PrintingJerk = 600.0f,      // mm/min, i.e. 10 mm/sec
            Backlash = 0.1f,
            ReducedAcceleration = 250.0f
        };
        x.Drivers.Add(new OmDriverId(1, 2));
        move.Axes.Add(x);

        Axis c = new() { Letter = 'C', StepsPerMm = 10.0f, Speed = 3000.0f, Acceleration = 500.0f, Rotational = true, ContinuousRotation = true };
        move.Axes.Add(c);

        Extruder e = new()
        {
            StepsPerMm = 420.0f,
            Speed = 3600.0f,            // mm/min, i.e. 60 mm/sec
            Acceleration = 2000.0f,
            Jerk = 120.0f,              // mm/min, i.e. 2 mm/sec
            Driver = new OmDriverId(1, 3)
        };
        e.PressAdv.K0 = 0.05f;          // seconds
        move.Extruders.Add(e);

        MoveQueueItem queue = new() { Length = 30, GracePeriod = 0.02f };
        move.Queue.Add(queue);
        return move;
    }

    [Test]
    public void SpeedsAreConvertedFromMmPerMinuteToStepClocks()
    {
        MotionParameters parameters = Snapshot(MachineWithOneOfEach());

        Assert.Multiple(() =>
        {
            // 6000 mm/min is 100 mm/sec
            Assert.That(parameters.MaxFeedrates[0], Is.EqualTo(100.0f / StepClockRate).Within(1e-10f));
            Assert.That(parameters.MaxFeedrates[MotionParameters.ExtruderToDrive(0)],
                        Is.EqualTo(60.0f / StepClockRate).Within(1e-10f));
        });
    }

    [Test]
    public void AccelerationsAreConvertedFromMmPerSecondSquared()
    {
        MotionParameters parameters = Snapshot(MachineWithOneOfEach());
        float clockSquared = StepClockRate * StepClockRate;

        Assert.Multiple(() =>
        {
            Assert.That(parameters.Accelerations[0], Is.EqualTo(1000.0f / clockSquared).Within(1e-16f));
            Assert.That(parameters.ReducedAccelerations[0], Is.EqualTo(250.0f / clockSquared).Within(1e-16f));
            Assert.That(parameters.MaxPrintingAcceleration, Is.EqualTo(500.0f / clockSquared).Within(1e-16f));
            Assert.That(parameters.MaxTravelAcceleration, Is.EqualTo(1000.0f / clockSquared).Within(1e-16f));
        });
    }

    [Test]
    public void AnAxisWithoutAReducedAccelerationFallsBackToItsNormalOne()
    {
        // Zero means "not configured", not "cannot accelerate": taking it literally would stop every
        // probing move dead
        Move move = MachineWithOneOfEach();
        move.Axes[0].ReducedAcceleration = 0.0f;

        MotionParameters parameters = Snapshot(move);
        Assert.That(parameters.ReducedAccelerations[0], Is.EqualTo(parameters.Accelerations[0]));
    }

    [Test]
    public void RotationalAxesAreSeparatedFromLinearOnes()
    {
        MotionParameters parameters = Snapshot(MachineWithOneOfEach());

        Assert.Multiple(() =>
        {
            Assert.That(parameters.LinearAxes, Is.EqualTo(0b01u), "X is linear");
            Assert.That(parameters.RotationalAxes, Is.EqualTo(0b10u), "C is rotational");
        });
    }

    [Test]
    public void ExtrudersOccupyTheTopOfTheDriveSpace()
    {
        MotionParameters parameters = Snapshot(MachineWithOneOfEach());

        Assert.Multiple(() =>
        {
            Assert.That(MotionParameters.ExtruderToDrive(0), Is.EqualTo(NumDrives - 1));
            Assert.That(parameters.FirstExtruderDrive, Is.EqualTo(NumDrives - 1));
            Assert.That(parameters.StepsPerMm[NumDrives - 1], Is.EqualTo(420.0f));
            Assert.That(parameters.DriveToExtruder(NumDrives - 1), Is.EqualTo(0));
            Assert.That(parameters.DriveToExtruder(0), Is.EqualTo(-1), "an axis is not an extruder");
        });
    }

    [Test]
    public void UnconfiguredDrivesHaveNonZeroStepsPerMm()
    {
        // This divides when converting motor steps back to a position, so a zero would turn an
        // untouched drive's position into an infinity
        MotionParameters parameters = Snapshot(MachineWithOneOfEach());
        for (int drive = 0; drive < NumDrives; drive++)
        {
            Assert.That(parameters.StepsPerMm[drive], Is.Not.Zero, $"drive {drive}");
        }
    }

    [Test]
    public void PressureAdvanceIsConvertedFromSecondsToStepClocks()
    {
        MotionParameters parameters = Snapshot(MachineWithOneOfEach());

        // A time multiplies by the clock rate rather than dividing by it, which is the conversion
        // most easily got backwards
        Assert.That(parameters.PressureAdvanceClocks[NumDrives - 1],
                    Is.EqualTo(0.05f * StepClockRate).Within(1.0f));
    }

    [Test]
    public void TheNativeConfigurationCarriesTheConvertedLimits()
    {
        Move move = MachineWithOneOfEach();
        MotionParameters parameters = Snapshot(move);
        MotionConfig config = parameters.ToMotionConfig(move);

        Assert.Multiple(() =>
        {
            Assert.That(config.NumVisibleAxes, Is.EqualTo(2));
            Assert.That(config.NumExtruders, Is.EqualTo(1));
            Assert.That(config.DriveStepsPerMm[0], Is.EqualTo(80.0f));

            // 900 mm/min is 15 mm/sec
            Assert.That(config.InstantDvs[0], Is.EqualTo(15.0f / StepClockRate).Within(1e-10f));
            Assert.That(config.PrintingInstantDvs[0], Is.EqualTo(10.0f / StepClockRate).Within(1e-10f));
            Assert.That(config.InstantDvs[NumDrives - 1], Is.EqualTo(2.0f / StepClockRate).Within(1e-10f));

            Assert.That(config.JerkPolicy, Is.EqualTo(1u));
            Assert.That(config.BacklashCorrectionDistanceFactor, Is.EqualTo(7u));
            Assert.That(config.BacklashSteps[0], Is.EqualTo(8), "0.1mm at 80 steps/mm");
        });
    }

    [Test]
    public void TheRingConfigurationComesFromTheQueue()
    {
        Move move = MachineWithOneOfEach();
        MotionConfig config = Snapshot(move).ToMotionConfig(move);

        Assert.Multiple(() =>
        {
            Assert.That(config.NumDdasPerRing, Is.EqualTo(30));
            Assert.That(config.GracePeriodMs, Is.EqualTo(20u), "0.02s");
        });
    }

    [Test]
    public void ContinuousRotationIsReportedOnlyForRotationalAxes()
    {
        Move move = MachineWithOneOfEach();
        move.Axes[0].ContinuousRotation = true;         // X is linear, so this must be ignored

        MotionConfig config = Snapshot(move).ToMotionConfig(move);
        Assert.That(config.ContinuousRotationAxes, Is.EqualTo(0b10u), "only the rotational C axis");
    }

    [Test]
    public void TheGeometryAddsItsOwnContinuousRotationAxes()
    {
        // A polar bed goes round whether or not anything in the object model says so, and the native
        // planner needs to know that or it will take the long way round on every angular move
        Move move = MachineWithOneOfEach();
        move.Axes.Add(new Axis { Letter = 'Z', StepsPerMm = 400.0f });
        move.Kinematics = new PolarKinematics { RadiusMax = 150.0f };

        MotionConfig config = Snapshot(move).ToMotionConfig(move);

        // Bit 1 is the turntable, which the polar geometry contributes; the C axis at bit 1 declares
        // it as well, so this is really a check that neither source is lost
        Assert.That(config.ContinuousRotationAxes & 0b010u, Is.EqualTo(0b010u));
    }

    [Test]
    public void ContinuousRotationIsNotClaimedForAxesTheMachineDoesNotHave()
    {
        // A five-bar SCARA calls both its actuators continuous, but a machine configured with one
        // axis has no second axis for that to be true of
        Move move = new();
        move.Axes.Add(new Axis { Letter = 'X', StepsPerMm = 80.0f });
        move.Kinematics = new PolarKinematics { RadiusMax = 150.0f };

        MotionConfig config = Snapshot(move).ToMotionConfig(move);
        Assert.That(config.ContinuousRotationAxes, Is.EqualTo(0u));
    }

    [Test]
    public void DriversAreCarriedThroughWithTheirBoardAddress()
    {
        Move move = MachineWithOneOfEach();
        MotionConfig config = Snapshot(move).ToMotionConfig(move);

        Assert.Multiple(() =>
        {
            Assert.That(config.AxisDrivers[0].NumDrivers, Is.EqualTo(1));
            Assert.That(config.AxisDrivers[0].DriverNumbers[0].BoardAddress, Is.EqualTo(1));
            Assert.That(config.AxisDrivers[0].DriverNumbers[0].LocalDriver, Is.EqualTo(2));
            Assert.That(config.ExtruderDrivers[0].BoardAddress, Is.EqualTo(1));
            Assert.That(config.ExtruderDrivers[0].LocalDriver, Is.EqualTo(3));
        });
    }

    [Test]
    public void AnAxisWithNoDriverGetsNoBoardRatherThanBoardZero()
    {
        // Board 0 is the main board, so a default driver id would address an unconfigured axis to a
        // real board
        Move move = MachineWithOneOfEach();
        MotionConfig config = Snapshot(move).ToMotionConfig(move);

        Assert.That(config.AxisDrivers[1].NumDrivers, Is.EqualTo(0), "the C axis has no driver");
        Assert.That(config.AxisDrivers[1].DriverNumbers[0].BoardAddress, Is.EqualTo(DriverId.NoCanAddress));
    }

    [Test]
    public void MoreDrivesThanTheSpaceHoldsAreTruncatedRatherThanOverlapped()
    {
        // Axes count up from zero and extruders down from the top, so overlapping them would make a
        // drive both an axis and an extruder at once
        Move move = new();
        for (int i = 0; i < MotionLimits.MaxAxes; i++)
        {
            move.Axes.Add(new Axis { Letter = 'A', StepsPerMm = 80.0f });
        }
        for (int i = 0; i < MotionLimits.MaxExtruders; i++)
        {
            move.Extruders.Add(new Extruder());
        }

        MotionParameters parameters = Snapshot(move);

        Assert.Multiple(() =>
        {
            Assert.That(parameters.NumAxes, Is.EqualTo(MotionLimits.MaxAxes));
            Assert.That(parameters.NumAxes + parameters.NumExtruders, Is.LessThanOrEqualTo(NumDrives));
            Assert.That(parameters.FirstExtruderDrive, Is.GreaterThanOrEqualTo(parameters.NumAxes));
        });
    }

    [Test]
    public void TheKinematicsMatrixFromTheObjectModelIsUsed()
    {
        // M669 can set an arbitrary matrix, which is the point of the matrix form
        Move move = MachineWithOneOfEach();
        CoreKinematics kinematics = new();
        kinematics.InverseMatrix.Clear();
        kinematics.InverseMatrix.Add([1, 1, 0]);
        kinematics.InverseMatrix.Add([1, -1, 0]);
        kinematics.InverseMatrix.Add([0, 0, 1]);
        move.Kinematics = kinematics;

        MotionParameters parameters = Snapshot(move);

        // CoreXY: holding X still needs both motors
        Assert.That(parameters.Geometry.GetControllingDrives(0), Is.EqualTo(0b011u));
    }

    [Test]
    public void ADeltaInTheObjectModelBuildsADeltaEngine()
    {
        Move move = MachineWithOneOfEach();
        DeltaKinematics kinematics = new() { DeltaRadius = 105.6f, HomedHeight = 240.0f, PrintRadius = 80.0f };
        for (int tower = 0; tower < 3; tower++)
        {
            kinematics.Towers.Add(new DeltaTower { Diagonal = 215.0f });
        }
        move.Kinematics = kinematics;

        MotionParameters parameters = Snapshot(move);

        Assert.Multiple(() =>
        {
            Assert.That(parameters.Geometry.Name, Is.EqualTo("delta"));

            // The giveaway that this is not a Cartesian machine: all three towers move together
            Assert.That(parameters.Geometry.GetControllingDrives(0), Is.EqualTo(0b111u));
        });
    }

    [Test]
    public void ADeltaWithNothingConfiguredStillGetsAUsableGeometry()
    {
        // Before M665 has run the object model has zeroes in it. A delta radius of zero would put all
        // three towers on top of each other, which is not a machine - so the defaults stand in
        Move move = MachineWithOneOfEach();
        move.Kinematics = new DeltaKinematics();

        MotionParameters parameters = Snapshot(move);
        float[] machinePos = new float[NumDrives];
        int[] motorPos = new int[NumDrives];

        Assert.That(parameters.Geometry.CartesianToMotorSteps(machinePos, parameters.StepsPerMm, 3, 3, motorPos),
                    Is.EqualTo(DuetControlServer.Link.Native.NativeMovementError.Ok));
    }

    [Test]
    public void AScaraInTheObjectModelBuildsAScaraEngine()
    {
        Move move = MachineWithOneOfEach();
        ScaraKinematics kinematics = new() { ProximalLength = 100.0f, DistalLength = 100.0f };
        kinematics.ThetaLimits[0] = -90.0f;
        kinematics.ThetaLimits[1] = 90.0f;
        kinematics.PsiLimits[0] = -135.0f;
        kinematics.PsiLimits[1] = 135.0f;
        move.Kinematics = kinematics;

        MotionParameters parameters = Snapshot(move);

        Assert.Multiple(() =>
        {
            Assert.That(parameters.Geometry.Name, Is.EqualTo("Scara"));
            Assert.That(parameters.Geometry.GetControllingDrives(0), Is.EqualTo(0b011u), "both arm motors");
        });
    }

    [Test]
    public void APolarInTheObjectModelCarriesItsTurntableLimitsInStepClocks()
    {
        Move move = MachineWithOneOfEach();
        move.Kinematics = new PolarKinematics { RadiusMin = 20.0f, RadiusMax = 150.0f, TTSpeedMax = 30.0f, TTAccMax = 60.0f };

        MotionParameters parameters = Snapshot(move);
        float clockSquared = StepClockRate * StepClockRate;

        Assert.That(parameters.Geometry, Is.InstanceOf<DuetControlServer.Motion.Kinematics.PolarKinematicsEngine>());

        var polar = (DuetControlServer.Motion.Kinematics.PolarKinematicsEngine)parameters.Geometry;
        Assert.Multiple(() =>
        {
            Assert.That(polar.MaxTurntableSpeed, Is.EqualTo(30.0f / StepClockRate).Within(1e-10f));
            Assert.That(polar.MaxTurntableAcceleration, Is.EqualTo(60.0f / clockSquared).Within(1e-16f));
            Assert.That(polar.ContinuousRotationAxes, Is.EqualTo(0b010u));
        });
    }

    [Test]
    public void AHangprinterInTheObjectModelCarriesItsAnchors()
    {
        Move move = MachineWithOneOfEach();
        move.Kinematics = new HangprinterKinematics();

        MotionParameters parameters = Snapshot(move);

        Assert.Multiple(() =>
        {
            Assert.That(parameters.Geometry.Name, Is.EqualTo("Hangprinter"));
            Assert.That(parameters.Geometry.GetControllingDrives(0), Is.EqualTo(0b1111u), "all four lines");
        });
    }

    [Test]
    public void AGeometryWithNoEngineStillPlansAsCartesian()
    {
        // Refusing to plan at all would be worse than planning for a machine we can describe, and the
        // fallback is the geometry that maps one motor to one axis
        Move move = MachineWithOneOfEach();
        move.Kinematics = new Kinematics();

        MotionParameters parameters = Snapshot(move);
        Assert.That(parameters.Geometry.GetControllingDrives(0), Is.EqualTo(0b001u));
    }

    [Test]
    public void AnEmptyMachineProducesAConfigurationThatCannotMove()
    {
        // The honest state before config.g has run: it fails by refusing to plan rather than by
        // moving something unconfigured
        MotionParameters parameters = MotionParameters.CreateDefault();

        Assert.Multiple(() =>
        {
            Assert.That(parameters.NumAxes, Is.Zero);
            Assert.That(parameters.NumExtruders, Is.Zero);
        });
    }

    [Test]
    public void ASnapshotMatchesTheMachineItWasTakenFrom()
    {
        Move move = MachineWithOneOfEach();
        Assert.That(Snapshot(move).MatchesObjectModel(move), Is.True);
    }

    [Test]
    public void AnEmptySnapshotDoesNotMatchAConfiguredMachine()
    {
        // What the planner holds before the motion service has configured it. Planning against it
        // would address no drives at all, so it has to be visible rather than silently do nothing
        Assert.Multiple(() =>
        {
            Assert.That(MotionParameters.CreateDefault().MatchesObjectModel(MachineWithOneOfEach()), Is.False);
            Assert.That(MotionParameters.CreateDefault().MatchesObjectModel(new Move()), Is.True);
        });
    }

    [Test]
    public void AnAxisAddedAfterTheSnapshotIsADivergence()
    {
        // M584 creates axes and reconfigures afterwards. If that reconfiguration did not happen the
        // snapshot describes a machine that no longer exists, and every drive above the new axis has
        // moved in the drive space
        Move move = MachineWithOneOfEach();
        MotionParameters parameters = Snapshot(move);

        move.Axes.Add(new Axis { Letter = 'Y', StepsPerMm = 80.0f });

        Assert.Multiple(() =>
        {
            Assert.That(parameters.MatchesObjectModel(move), Is.False);
            Assert.That(parameters.SharedAxisCount(move), Is.EqualTo(2), "bounded by what was snapshotted");
        });
    }

    [Test]
    public void AnAxisRemovedAfterTheSnapshotIsADivergence()
    {
        Move move = MachineWithOneOfEach();
        MotionParameters parameters = Snapshot(move);

        move.Axes.RemoveAt(1);

        Assert.Multiple(() =>
        {
            Assert.That(parameters.MatchesObjectModel(move), Is.False);
            Assert.That(parameters.SharedAxisCount(move), Is.EqualTo(1), "bounded by what the object model still has");
        });
    }

    [Test]
    public void AnExtruderChangeAfterTheSnapshotIsADivergence()
    {
        Move move = MachineWithOneOfEach();
        MotionParameters parameters = Snapshot(move);

        move.Extruders.Add(new Extruder());

        Assert.Multiple(() =>
        {
            Assert.That(parameters.MatchesObjectModel(move), Is.False);
            Assert.That(parameters.SharedExtruderCount(move), Is.EqualTo(1));
        });
    }

    [Test]
    public void SettingAnAxisLimitFollowsThroughToTheGeometry()
    {
        // G1 H3 measures an axis and writes the limit it found. The geometry holds the copy that
        // every move is clamped against, so it has to follow without the whole snapshot being rebuilt
        Move move = MachineWithOneOfEach();
        move.Axes[0].Min = -5.0f;
        move.Axes[0].Max = 200.0f;

        MotionParameters parameters = Snapshot(move);
        Assert.That(parameters.Geometry.AxisMaxima[0], Is.EqualTo(200.0f));

        parameters.SetAxisLimits(0, -5.0f, 187.5f);

        Assert.Multiple(() =>
        {
            Assert.That(parameters.Geometry.AxisMinima[0], Is.EqualTo(-5.0f));
            Assert.That(parameters.Geometry.AxisMaxima[0], Is.EqualTo(187.5f));
        });
    }

    [Test]
    public void SettingTheLimitOfAnAxisThatDoesNotExistDoesNothing()
    {
        MotionParameters parameters = Snapshot(MachineWithOneOfEach());

        Assert.DoesNotThrow(() =>
        {
            parameters.SetAxisLimits(-1, 0.0f, 100.0f);
            parameters.SetAxisLimits(parameters.NumAxes, 0.0f, 100.0f);
        });
        Assert.That(parameters.Geometry.AxisMaxima[parameters.NumAxes], Is.Zero);
    }
}
