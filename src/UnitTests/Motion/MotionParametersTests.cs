using System;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
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
            PressureAdvance = 0.05f,    // seconds
            Driver = new OmDriverId(1, 3)
        };
        move.Extruders.Add(e);

        MoveQueueItem queue = new() { Length = 30, GracePeriod = 0.02f };
        move.Queue.Add(queue);
        return move;
    }

    [Test]
    public void SpeedsAreConvertedFromMmPerMinuteToStepClocks()
    {
        MotionParameters parameters = MotionParameters.FromObjectModel(MachineWithOneOfEach());

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
        MotionParameters parameters = MotionParameters.FromObjectModel(MachineWithOneOfEach());
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

        MotionParameters parameters = MotionParameters.FromObjectModel(move);
        Assert.That(parameters.ReducedAccelerations[0], Is.EqualTo(parameters.Accelerations[0]));
    }

    [Test]
    public void RotationalAxesAreSeparatedFromLinearOnes()
    {
        MotionParameters parameters = MotionParameters.FromObjectModel(MachineWithOneOfEach());

        Assert.Multiple(() =>
        {
            Assert.That(parameters.LinearAxes, Is.EqualTo(0b01u), "X is linear");
            Assert.That(parameters.RotationalAxes, Is.EqualTo(0b10u), "C is rotational");
        });
    }

    [Test]
    public void ExtrudersOccupyTheTopOfTheDriveSpace()
    {
        MotionParameters parameters = MotionParameters.FromObjectModel(MachineWithOneOfEach());

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
        MotionParameters parameters = MotionParameters.FromObjectModel(MachineWithOneOfEach());
        for (int drive = 0; drive < NumDrives; drive++)
        {
            Assert.That(parameters.StepsPerMm[drive], Is.Not.Zero, $"drive {drive}");
        }
    }

    [Test]
    public void PressureAdvanceIsConvertedFromSecondsToStepClocks()
    {
        MotionParameters parameters = MotionParameters.FromObjectModel(MachineWithOneOfEach());

        // A time multiplies by the clock rate rather than dividing by it, which is the conversion
        // most easily got backwards
        Assert.That(parameters.PressureAdvanceClocks[NumDrives - 1],
                    Is.EqualTo(0.05f * StepClockRate).Within(1.0f));
    }

    [Test]
    public void TheNativeConfigurationCarriesTheConvertedLimits()
    {
        Move move = MachineWithOneOfEach();
        MotionParameters parameters = MotionParameters.FromObjectModel(move);
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
        MotionConfig config = MotionParameters.FromObjectModel(move).ToMotionConfig(move);

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

        MotionConfig config = MotionParameters.FromObjectModel(move).ToMotionConfig(move);
        Assert.That(config.ContinuousRotationAxes, Is.EqualTo(0b10u), "only the rotational C axis");
    }

    [Test]
    public void DriversAreCarriedThroughWithTheirBoardAddress()
    {
        Move move = MachineWithOneOfEach();
        MotionConfig config = MotionParameters.FromObjectModel(move).ToMotionConfig(move);

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
        MotionConfig config = MotionParameters.FromObjectModel(move).ToMotionConfig(move);

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

        MotionParameters parameters = MotionParameters.FromObjectModel(move);

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

        MotionParameters parameters = MotionParameters.FromObjectModel(move);

        // CoreXY: holding X still needs both motors
        Assert.That(parameters.Geometry.GetControllingDrives(0), Is.EqualTo(0b011u));
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
}
