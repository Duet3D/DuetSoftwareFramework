using System;
using System.Runtime.InteropServices;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// Steps 1 to 6 of the move planner, i.e. everything that depends on one move alone
/// </summary>
/// <remarks>
/// The submission these produce is what the native engine plans against, so what is checked here is
/// mostly the arithmetic that decides how far the machine goes and how fast: the endpoints, the
/// direction vector, the distance, and the speed and acceleration after every limit has been applied
/// </remarks>
[TestFixture]
public class MoveBuilderTests
{
    private const int NumDrives = MotionLimits.MaxAxesPlusExtruders;
    private const float StepClockRate = MotionLimits.StepClockRate;

    private static MachineConfig CartesianMachine()
    {
        MachineConfig config = new();
        AxisConfig[] axes =
        [
            new() { Letter = 'X', StepsPerMm = 80.0f, MaxFeedrateMmPerSec = 300.0f, AccelerationMmPerSecSquared = 1000.0f, Drivers = [new DriverId(0, 0)] },
            new() { Letter = 'Y', StepsPerMm = 80.0f, MaxFeedrateMmPerSec = 300.0f, AccelerationMmPerSecSquared = 1000.0f, Drivers = [new DriverId(0, 1)] },
            new() { Letter = 'Z', StepsPerMm = 400.0f, MaxFeedrateMmPerSec = 10.0f, AccelerationMmPerSecSquared = 100.0f, Drivers = [new DriverId(0, 2)] }
        ];
        ExtruderConfig[] extruders =
        [
            new() { StepsPerMm = 400.0f, MaxFeedrateMmPerSec = 60.0f, AccelerationMmPerSecSquared = 2000.0f, Driver = new DriverId(0, 3) }
        ];
        config.Configure(axes, extruders, CoreKinematicsEngine.TryCreate("cartesian")!);
        return config;
    }

    /// <summary>The submission decoded back into the pieces the native side reads</summary>
    private sealed record Submission(MoveParamsHeader Header, int[] EndPoints, float[] DirectionVector);

    private static Submission Decode(ReadOnlySpan<byte> record)
    {
        int headerSize = Marshal.SizeOf<MoveParamsHeader>();
        MoveParamsHeader header = MemoryMarshal.Read<MoveParamsHeader>(record);

        ReadOnlySpan<byte> tail = record[headerSize..];
        ReadOnlySpan<int> endPoints = MemoryMarshal.Cast<byte, int>(tail[..(header.NumDrives * sizeof(int))]);
        ReadOnlySpan<float> directions = MemoryMarshal.Cast<byte, float>(
            tail.Slice(header.NumDrives * sizeof(int), header.NumDrives * sizeof(float)));

        return new Submission(header, endPoints.ToArray(), directions.ToArray());
    }

    private static (MoveBuildResult Result, Submission? Move) Build(MoveBuilder builder, RawMove move)
    {
        byte[] buffer = new byte[MoveParams.Length(NumDrives)];
        MoveBuildResult result = builder.Build(move, buffer);
        return (result, result.HasMove ? Decode(buffer.AsSpan(0, result.Length)) : null);
    }

    private static RawMove LinearMove(uint moveId, float x, float y, float z, float feedRateMmPerSec = 50.0f)
    {
        RawMove move = new() { MoveId = moveId, FeedRateMmPerSec = feedRateMmPerSec, LinearAxesMentioned = true };
        move.Coords[0] = x;
        move.Coords[1] = y;
        move.Coords[2] = z;
        return move;
    }

    [Test]
    public void AMoveInXProducesTheExpectedEndpointAndDirection()
    {
        MoveBuilder builder = new(CartesianMachine());
        (MoveBuildResult result, Submission? move) = Build(builder, LinearMove(1, 10.0f, 0.0f, 0.0f));

        Assert.That(result.Error, Is.EqualTo(NativeMovementError.Ok));
        Assert.Multiple(() =>
        {
            Assert.That(move!.EndPoints[0], Is.EqualTo(800), "10mm at 80 steps/mm");
            Assert.That(move.Header.TotalDistance, Is.EqualTo(10.0f).Within(1e-4f));
            Assert.That(move.DirectionVector[0], Is.EqualTo(1.0f).Within(1e-5f), "the direction vector is unit length");
            Assert.That(move.DirectionVector[1], Is.EqualTo(0.0f));
        });
    }

    [Test]
    public void MovesAreRelativeToWhereTheLastOneEnded()
    {
        // The endpoints are absolute, and the native planner takes the difference against the
        // previous move. Getting this wrong is not a small error - it moves the machine by the whole
        // distance again
        MoveBuilder builder = new(CartesianMachine());

        Build(builder, LinearMove(1, 10.0f, 0.0f, 0.0f));
        (_, Submission? second) = Build(builder, LinearMove(2, 25.0f, 0.0f, 0.0f));

        Assert.Multiple(() =>
        {
            Assert.That(second!.EndPoints[0], Is.EqualTo(2000), "25mm absolute, not 10 + 25");
            Assert.That(second.Header.TotalDistance, Is.EqualTo(15.0f).Within(1e-4f), "the distance is the delta");
        });
    }

    [Test]
    public void ADiagonalMoveHasTheRightLengthAndDirection()
    {
        MoveBuilder builder = new(CartesianMachine());
        (_, Submission? move) = Build(builder, LinearMove(1, 3.0f, 4.0f, 0.0f));

        Assert.Multiple(() =>
        {
            Assert.That(move!.Header.TotalDistance, Is.EqualTo(5.0f).Within(1e-4f));
            Assert.That(move.DirectionVector[0], Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(move.DirectionVector[1], Is.EqualTo(0.8f).Within(1e-5f));
        });
    }

    [Test]
    public void AMoveThatGoesNowhereIsReportedAsNoMovement()
    {
        MoveBuilder builder = new(CartesianMachine());
        Build(builder, LinearMove(1, 10.0f, 0.0f, 0.0f));

        (MoveBuildResult result, _) = Build(builder, LinearMove(2, 10.0f, 0.0f, 0.0f));

        Assert.That(result.Error, Is.EqualTo(NativeMovementError.NoMovement));
    }

    [Test]
    public void ASubMicrostepMoveStillAdvancesTheCoordinates()
    {
        // Otherwise the rounding accumulates: a run of moves each too small to make a step would
        // never move the machine even though together they cover millimetres
        MoveBuilder builder = new(CartesianMachine());

        for (int i = 1; i <= 100; i++)
        {
            Build(builder, LinearMove((uint)i, i * 0.001f, 0.0f, 0.0f));
        }

        // 100 moves of 1 micron each is 0.1mm, which is 8 microsteps
        (_, Submission? move) = Build(builder, LinearMove(200, 1.0f, 0.0f, 0.0f));
        Assert.That(move!.Header.TotalDistance, Is.EqualTo(0.9f).Within(1e-3f),
                    "the last move covers what is left, not the whole millimetre");
    }

    [Test]
    public void TheFeedRateIsConvertedIntoStepClocks()
    {
        MoveBuilder builder = new(CartesianMachine());
        (_, Submission? move) = Build(builder, LinearMove(1, 10.0f, 0.0f, 0.0f, feedRateMmPerSec: 50.0f));

        Assert.That(move!.Header.RequestedSpeed, Is.EqualTo(50.0f / StepClockRate).Within(1e-9f));
    }

    [Test]
    public void TheFeedRateIsCappedByTheSlowestAxisInvolved()
    {
        // Z is configured for 10mm/sec, so a Z move cannot run at the 50mm/sec asked for
        MoveBuilder builder = new(CartesianMachine());
        (_, Submission? move) = Build(builder, LinearMove(1, 0.0f, 0.0f, 5.0f, feedRateMmPerSec: 50.0f));

        Assert.That(move!.Header.RequestedSpeed, Is.EqualTo(10.0f / StepClockRate).Within(1e-9f));
    }

    [Test]
    public void ADiagonalMoveMayExceedEitherAxisOnACartesianMachine()
    {
        // X and Y are limited independently, so a 45-degree move is allowed to be sqrt(2) times
        // faster than either axis on its own
        MoveBuilder builder = new(CartesianMachine());
        (_, Submission? move) = Build(builder, LinearMove(1, 100.0f, 100.0f, 0.0f, feedRateMmPerSec: 1000.0f));

        float expected = 300.0f * MathF.Sqrt(2.0f) / StepClockRate;
        Assert.That(move!.Header.RequestedSpeed, Is.EqualTo(expected).Within(1e-7f));
    }

    [Test]
    public void ACoreXyDiagonalMoveIsLimitedByTheSharedMotors()
    {
        MachineConfig config = CartesianMachine();
        config.Configure(config.Axes, config.Extruders, CoreKinematicsEngine.TryCreate("corexy")!);

        MoveBuilder builder = new(config);
        (_, Submission? move) = Build(builder, LinearMove(1, 100.0f, 100.0f, 0.0f, feedRateMmPerSec: 1000.0f));

        // Moving X and Y together at 45 degrees turns motor A at sqrt(2) times the move speed, so
        // the move is held to 300/sqrt(2) rather than the 300*sqrt(2) a Cartesian machine allows
        float expected = 300.0f / MathF.Sqrt(2.0f) / StepClockRate;
        Assert.That(move!.Header.RequestedSpeed, Is.EqualTo(expected).Within(1e-7f));
    }

    [Test]
    public void CoreXyEndpointsDriveBothMotors()
    {
        MachineConfig config = CartesianMachine();
        config.Configure(config.Axes, config.Extruders, CoreKinematicsEngine.TryCreate("corexy")!);

        MoveBuilder builder = new(config);
        (_, Submission? move) = Build(builder, LinearMove(1, 10.0f, 0.0f, 0.0f));

        Assert.Multiple(() =>
        {
            Assert.That(move!.EndPoints[0], Is.EqualTo(800), "motor A is X+Y");
            Assert.That(move.EndPoints[1], Is.EqualTo(800), "motor B is X-Y");
            Assert.That(move.Header.TotalDistance, Is.EqualTo(10.0f).Within(1e-4f), "but the move is still 10mm");
        });
    }

    [Test]
    public void AnExtrudingMoveIsFlaggedAsPrinting()
    {
        MoveBuilder builder = new(CartesianMachine());
        RawMove move = LinearMove(1, 10.0f, 0.0f, 0.0f);
        move.Coords[NumDrives - 1] = 0.5f;              // the first extruder

        (_, Submission? built) = Build(builder, move);

        Assert.Multiple(() =>
        {
            Assert.That(built!.Header.Flags & MoveFlags.IsPrintingMove, Is.Not.Zero);
            Assert.That(built.Header.Flags & MoveFlags.XyMoving, Is.Not.Zero);
            Assert.That(built.Header.Flags & MoveFlags.HasForwardExtrusion, Is.Not.Zero);
            Assert.That(built.Header.Flags & MoveFlags.IsNonPrintingExtruderMove, Is.Zero);
        });
    }

    [Test]
    public void RetractingWhileMovingIsNotAPrintingMove()
    {
        // Requires forward extrusion, so that wipe-while-retracting does not count as printing and
        // pick up the printing jerk limits
        MoveBuilder builder = new(CartesianMachine());
        RawMove move = LinearMove(1, 10.0f, 0.0f, 0.0f);
        move.Coords[NumDrives - 1] = -0.5f;

        (_, Submission? built) = Build(builder, move);

        Assert.Multiple(() =>
        {
            Assert.That(built!.Header.Flags & MoveFlags.IsPrintingMove, Is.Zero);
            Assert.That(built.Header.Flags & MoveFlags.HasForwardExtrusion, Is.Zero);
        });
    }

    [Test]
    public void ExtrusionDoesNotLengthenTheMove()
    {
        // The feed rate applies to the linear movement, so the extruder must not make the move look
        // longer and be run proportionately slow
        MoveBuilder builder = new(CartesianMachine());
        RawMove move = LinearMove(1, 10.0f, 0.0f, 0.0f);
        move.Coords[NumDrives - 1] = 3.0f;

        (_, Submission? built) = Build(builder, move);

        Assert.Multiple(() =>
        {
            Assert.That(built!.Header.TotalDistance, Is.EqualTo(10.0f).Within(1e-4f));
            Assert.That(built.DirectionVector[NumDrives - 1], Is.EqualTo(0.3f).Within(1e-5f),
                        "the extruder is scaled along with everything else");
        });
    }

    [Test]
    public void AnExtruderOnlyMoveUsesTheExtrusionAsItsDistance()
    {
        MoveBuilder builder = new(CartesianMachine());
        RawMove move = new() { MoveId = 1, FeedRateMmPerSec = 10.0f };
        move.Coords[NumDrives - 1] = 2.0f;

        (MoveBuildResult result, Submission? built) = Build(builder, move);

        Assert.That(result.Error, Is.EqualTo(NativeMovementError.Ok));
        Assert.Multiple(() =>
        {
            Assert.That(built!.Header.TotalDistance, Is.EqualTo(2.0f).Within(1e-4f));
            Assert.That(built.Header.Flags & MoveFlags.IsNonPrintingExtruderMove, Is.Not.Zero);
            Assert.That(built.Header.Flags & MoveFlags.XyMoving, Is.Zero);
        });
    }

    [Test]
    public void TheAccelerationIsCappedByTheSlowestDriveInvolved()
    {
        MoveBuilder builder = new(CartesianMachine());
        (_, Submission? move) = Build(builder, LinearMove(1, 0.0f, 0.0f, 5.0f));

        // Z accelerates at 100mm/sec^2
        float expected = 100.0f / (StepClockRate * StepClockRate);
        Assert.That(move!.Header.MaxAcceleration, Is.EqualTo(expected).Within(1e-14f));
    }

    [Test]
    public void M204CapsTravelAndPrintingMovesSeparately()
    {
        MachineConfig config = CartesianMachine();
        config.MaxTravelAccelerationMmPerSecSquared = 500.0f;
        config.MaxPrintingAccelerationMmPerSecSquared = 200.0f;

        MoveBuilder builder = new(config);
        float clockSquared = StepClockRate * StepClockRate;

        (_, Submission? travel) = Build(builder, LinearMove(1, 10.0f, 0.0f, 0.0f));
        Assert.That(travel!.Header.MaxAcceleration, Is.EqualTo(500.0f / clockSquared).Within(1e-14f));

        RawMove printing = LinearMove(2, 20.0f, 0.0f, 0.0f);
        printing.Coords[NumDrives - 1] = 0.5f;
        (_, Submission? built) = Build(builder, printing);
        Assert.That(built!.Header.MaxAcceleration, Is.EqualTo(200.0f / clockSquared).Within(1e-14f));
    }

    [Test]
    public void AnEndstopMoveIsIsolated()
    {
        // It may stop short, so it must not be melded with its neighbours
        MoveBuilder builder = new(CartesianMachine());
        RawMove move = LinearMove(1, 10.0f, 0.0f, 0.0f);
        move.CheckEndstops = true;

        (_, Submission? built) = Build(builder, move);

        Assert.Multiple(() =>
        {
            Assert.That(built!.Header.Flags & MoveFlags.CheckEndstops, Is.Not.Zero);
            Assert.That(built.Header.Flags & MoveFlags.IsolatedMove, Is.Not.Zero);
        });
    }

    [Test]
    public void ResyncingEndpointsMovesTheBuildersIdeaOfWhereTheMachineIs()
    {
        // What happens after an endstop move stops short. The next move is planned as a delta from
        // these endpoints, so continuing from the planned ones would move by the whole discrepancy
        MoveBuilder builder = new(CartesianMachine());
        Build(builder, LinearMove(1, 100.0f, 0.0f, 0.0f));

        int[] actual = new int[NumDrives];
        actual[0] = 800;                    // it really stopped at 10mm, not 100mm
        builder.ResyncEndpoints(actual);

        (_, Submission? next) = Build(builder, LinearMove(2, 20.0f, 0.0f, 0.0f));

        Assert.Multiple(() =>
        {
            Assert.That(next!.EndPoints[0], Is.EqualTo(1600), "20mm absolute");
            Assert.That(next.Header.TotalDistance, Is.EqualTo(10.0f).Within(1e-3f),
                        "10mm from where it actually stopped, not 80mm from where it was told to stop");
        });
    }

    [Test]
    public void SettingAnAxisPositionDoesNotMoveAnything()
    {
        // G92: the machine has not moved, but the coordinate that describes it has changed
        MoveBuilder builder = new(CartesianMachine());
        Build(builder, LinearMove(1, 10.0f, 0.0f, 0.0f));

        builder.SetAxisPosition(0, 0.0f);

        (_, Submission? next) = Build(builder, LinearMove(2, 5.0f, 0.0f, 0.0f));
        Assert.That(next!.Header.TotalDistance, Is.EqualTo(5.0f).Within(1e-4f));
    }

    [Test]
    public void ADriveTheMoveDoesNotOwnIsLeftWhereItWas()
    {
        MoveBuilder builder = new(CartesianMachine());
        Build(builder, LinearMove(1, 10.0f, 5.0f, 0.0f));

        RawMove move = LinearMove(2, 20.0f, 15.0f, 0.0f);
        move.OwnedDrives = 0b001;           // X only
        (_, Submission? built) = Build(builder, move);

        Assert.Multiple(() =>
        {
            Assert.That(built!.EndPoints[0], Is.EqualTo(1600), "X moved");
            Assert.That(built.EndPoints[1], Is.EqualTo(400), "Y stayed where the previous move left it");
            Assert.That(built.DirectionVector[1], Is.EqualTo(0.0f));
        });
    }

    [Test]
    public void APositionTooFarFromTheOriginIsRejectedWithoutAdvancingAnything()
    {
        MoveBuilder builder = new(CartesianMachine());
        Build(builder, LinearMove(1, 10.0f, 0.0f, 0.0f));

        (MoveBuildResult bad, _) = Build(builder, LinearMove(2, 1.0e9f, 0.0f, 0.0f));
        Assert.That(bad.Error, Is.EqualTo(NativeMovementError.MicrostepPositionTooLarge));

        // The rejected move must not have moved the builder on, or the next one is planned from a
        // position the machine was never in
        (_, Submission? next) = Build(builder, LinearMove(3, 20.0f, 0.0f, 0.0f));
        Assert.That(next!.Header.TotalDistance, Is.EqualTo(10.0f).Within(1e-4f));
    }

    [Test]
    public void TheSubmissionCarriesTheFullDriveSpace()
    {
        // The native lookahead and preparation index densely by logical drive, so every drive is
        // present whether or not it moves
        MoveBuilder builder = new(CartesianMachine());
        (MoveBuildResult result, Submission? move) = Build(builder, LinearMove(1, 10.0f, 0.0f, 0.0f));

        Assert.Multiple(() =>
        {
            Assert.That(move!.Header.NumDrives, Is.EqualTo(NumDrives));
            Assert.That(result.Length, Is.EqualTo(MoveParams.Length(NumDrives)));
            Assert.That(move.EndPoints, Has.Length.EqualTo(NumDrives));
            Assert.That(move.DirectionVector, Has.Length.EqualTo(NumDrives));
        });
    }

    [Test]
    public void TheMoveIdIsCarriedThrough()
    {
        MoveBuilder builder = new(CartesianMachine());
        (_, Submission? move) = Build(builder, LinearMove(4242, 10.0f, 0.0f, 0.0f));
        Assert.That(move!.Header.MoveId, Is.EqualTo(4242u));
    }
}
