using System;
using System.Runtime.InteropServices;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Native;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using OmDriverId = DuetAPI.Utility.DriverId;

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
    {
        KinematicsEngine geometry = KinematicsFactory.Create(move.Kinematics);
        MotionParameters.ApplyAxisLimits(move, geometry);
        return MotionParameters.FromObjectModel(move, geometry);
    }
    private const int NumDrives = MotionLimits.MaxAxesPlusExtruders;
    private const float StepClockRate = MotionLimits.StepClockRate;

    /// <summary>
    /// A three-axis Cartesian machine with one extruder, described the way config.g would
    /// </summary>
    /// <remarks>
    /// Built in the object model, because that is where the configuration lives. Speeds are mm/min
    /// there and accelerations mm/s^2, which is what the M-codes that set them use
    /// </remarks>
    private static Move CartesianMachine(string geometry = "cartesian")
    {
        Move move = new()
        {
            MinimumMovementSpeed = 0.5f
        };

        // The move-wide acceleration caps live on the motion system, which is where M204 writes them
        move.MotionSystems.Add(new MotionSystem
        {
            PrintingAcceleration = 10000.0f,
            TravelAcceleration = 10000.0f
        });

        AddAxis(move, 'X', stepsPerMm: 80.0f, speedMmPerMin: 300.0f * 60.0f, acceleration: 1000.0f, board: 0, port: 0);
        AddAxis(move, 'Y', stepsPerMm: 80.0f, speedMmPerMin: 300.0f * 60.0f, acceleration: 1000.0f, board: 0, port: 1);
        AddAxis(move, 'Z', stepsPerMm: 400.0f, speedMmPerMin: 10.0f * 60.0f, acceleration: 100.0f, board: 0, port: 2);

        Extruder extruder = new()
        {
            StepsPerMm = 400.0f,
            Speed = 60.0f * 60.0f,
            Acceleration = 2000.0f,
            Driver = new OmDriverId(0, 3)
        };
        move.Extruders.Add(extruder);

        if (geometry != "cartesian")
        {
            move.Kinematics = MakeCoreKinematics(geometry);
        }
        return move;
    }

    private static void AddAxis(Move move, char letter, float stepsPerMm, float speedMmPerMin, float acceleration, int board, int port)
    {
        Axis axis = new()
        {
            Letter = letter,
            StepsPerMm = stepsPerMm,
            Speed = speedMmPerMin,
            Acceleration = acceleration,
            Visible = true
        };
        axis.Drivers.Add(new OmDriverId(board, port));
        move.Axes.Add(axis);
    }

    /// <summary>
    /// The object model kinematics for one of the named core geometries
    /// </summary>
    private static CoreKinematics MakeCoreKinematics(string name)
    {
        float[][] inverse = name switch
        {
            "corexy" => [[1, 1, 0], [1, -1, 0], [0, 0, 1]],
            "corexz" => [[1, 0, 1], [0, 1, 0], [1, 0, -1]],
            _ => [[1, 0, 0], [0, 1, 0], [0, 0, 1]]
        };

        CoreKinematics kinematics = new();
        kinematics.InverseMatrix.Clear();
        foreach (float[] row in inverse)
        {
            kinematics.InverseMatrix.Add(row);
        }
        return kinematics;
    }

    private static MoveBuilder NewBuilder(Move move) => new(Snapshot(move));

    /// <summary>A handle with the given raw value, which is the form the record carries it in</summary>
    /// <param name="all">Type, major and minor packed as the boards read them</param>
    /// <returns>The handle</returns>
    private static RemoteInputHandle Handle(ushort all) => new() { All = all };

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
        MoveBuilder builder = NewBuilder(CartesianMachine());
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
        MoveBuilder builder = NewBuilder(CartesianMachine());

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
        MoveBuilder builder = NewBuilder(CartesianMachine());
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
        MoveBuilder builder = NewBuilder(CartesianMachine());
        Build(builder, LinearMove(1, 10.0f, 0.0f, 0.0f));

        (MoveBuildResult result, _) = Build(builder, LinearMove(2, 10.0f, 0.0f, 0.0f));

        Assert.That(result.Error, Is.EqualTo(NativeMovementError.NoMovement));
    }

    [Test]
    public void ASubMicrostepMoveStillAdvancesTheCoordinates()
    {
        // Otherwise the rounding accumulates: a run of moves each too small to make a step would
        // never move the machine even though together they cover millimetres
        MoveBuilder builder = NewBuilder(CartesianMachine());

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
        MoveBuilder builder = NewBuilder(CartesianMachine());
        (_, Submission? move) = Build(builder, LinearMove(1, 10.0f, 0.0f, 0.0f, feedRateMmPerSec: 50.0f));

        Assert.That(move!.Header.RequestedSpeed, Is.EqualTo(50.0f / StepClockRate).Within(1e-9f));
    }

    [Test]
    public void TheFeedRateIsCappedByTheSlowestAxisInvolved()
    {
        // Z is configured for 10mm/sec, so a Z move cannot run at the 50mm/sec asked for
        MoveBuilder builder = NewBuilder(CartesianMachine());
        (_, Submission? move) = Build(builder, LinearMove(1, 0.0f, 0.0f, 5.0f, feedRateMmPerSec: 50.0f));

        Assert.That(move!.Header.RequestedSpeed, Is.EqualTo(10.0f / StepClockRate).Within(1e-9f));
    }

    [Test]
    public void ADiagonalMoveMayExceedEitherAxisOnACartesianMachine()
    {
        // X and Y are limited independently, so a 45-degree move is allowed to be sqrt(2) times
        // faster than either axis on its own
        MoveBuilder builder = NewBuilder(CartesianMachine());
        (_, Submission? move) = Build(builder, LinearMove(1, 100.0f, 100.0f, 0.0f, feedRateMmPerSec: 1000.0f));

        float expected = 300.0f * MathF.Sqrt(2.0f) / StepClockRate;
        Assert.That(move!.Header.RequestedSpeed, Is.EqualTo(expected).Within(1e-7f));
    }

    [Test]
    public void ACoreXyDiagonalMoveIsLimitedByTheSharedMotors()
    {
        MoveBuilder builder = NewBuilder(CartesianMachine("corexy"));
        (_, Submission? move) = Build(builder, LinearMove(1, 100.0f, 100.0f, 0.0f, feedRateMmPerSec: 1000.0f));

        // Moving X and Y together at 45 degrees turns motor A at sqrt(2) times the move speed, so
        // the move is held to 300/sqrt(2) rather than the 300*sqrt(2) a Cartesian machine allows
        float expected = 300.0f / MathF.Sqrt(2.0f) / StepClockRate;
        Assert.That(move!.Header.RequestedSpeed, Is.EqualTo(expected).Within(1e-7f));
    }

    [Test]
    public void CoreXyEndpointsDriveBothMotors()
    {
        MoveBuilder builder = NewBuilder(CartesianMachine("corexy"));
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
        MoveBuilder builder = NewBuilder(CartesianMachine());
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
        MoveBuilder builder = NewBuilder(CartesianMachine());
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
        MoveBuilder builder = NewBuilder(CartesianMachine());
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
        MoveBuilder builder = NewBuilder(CartesianMachine());
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
        MoveBuilder builder = NewBuilder(CartesianMachine());
        (_, Submission? move) = Build(builder, LinearMove(1, 0.0f, 0.0f, 5.0f));

        // Z accelerates at 100mm/sec^2
        float expected = 100.0f / (StepClockRate * StepClockRate);
        Assert.That(move!.Header.MaxAcceleration, Is.EqualTo(expected).Within(1e-14f));
    }

    [Test]
    public void M204CapsTravelAndPrintingMovesSeparately()
    {
        Move machine = CartesianMachine();
        machine.MotionSystems[0].TravelAcceleration = 500.0f;
        machine.MotionSystems[0].PrintingAcceleration = 200.0f;

        MoveBuilder builder = NewBuilder(machine);
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
        MoveBuilder builder = NewBuilder(CartesianMachine());
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
        MoveBuilder builder = NewBuilder(CartesianMachine());
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
        MoveBuilder builder = NewBuilder(CartesianMachine());
        Build(builder, LinearMove(1, 10.0f, 0.0f, 0.0f));

        builder.SetAxisPosition(0, 0.0f);

        (_, Submission? next) = Build(builder, LinearMove(2, 5.0f, 0.0f, 0.0f));
        Assert.That(next!.Header.TotalDistance, Is.EqualTo(5.0f).Within(1e-4f));
    }

    [Test]
    public void ADriveTheMoveDoesNotOwnIsLeftWhereItWas()
    {
        MoveBuilder builder = NewBuilder(CartesianMachine());
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
        MoveBuilder builder = NewBuilder(CartesianMachine());
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
        MoveBuilder builder = NewBuilder(CartesianMachine());
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
        MoveBuilder builder = NewBuilder(CartesianMachine());
        (_, Submission? move) = Build(builder, LinearMove(4242, 10.0f, 0.0f, 0.0f));
        Assert.That(move!.Header.MoveId, Is.EqualTo(4242u));
    }

    [Test]
    public void TheEndstopSwitchesReachTheRecordInDriverOrder()
    {
        // The native side reads these back by pointer arithmetic, so the bytes have to land where
        // MoveStopInput says they do. A misplaced board means a homing move watching another board's
        // switch, which the boards themselves cannot detect
        MoveBuilder builder = NewBuilder(CartesianMachine());
        RawMove move = LinearMove(1, 10.0f, 0.0f, 0.0f);
        move.CheckEndstops = true;
        move.StopOnInput[0].SetShared(Handle(0x1042), 3);
        move.StopOnInput[1].SetPerDriver(Handle(0x1080), [1, 4]);

        byte[] buffer = new byte[MoveParams.Length(NumDrives)];
        MoveBuildResult result = builder.Build(move, buffer);
        Assert.That(result.HasMove, Is.True);

        int stopsAt = Marshal.SizeOf<MoveParamsHeader>() + (NumDrives * (sizeof(int) + sizeof(float)));
        byte[] x = buffer[stopsAt..(stopsAt + MoveStopInput.Length)];
        byte[] y = buffer[(stopsAt + MoveStopInput.Length)..(stopsAt + (2 * MoveStopInput.Length))];
        byte[] z = buffer[(stopsAt + (2 * MoveStopInput.Length))..(stopsAt + (3 * MoveStopInput.Length))];

        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt16(x), Is.EqualTo(0x1042), "X's handle");
            Assert.That(x[2], Is.EqualTo(1), "X watches one switch for the whole axis");
            Assert.That(x[3], Is.EqualTo(3), "on board 3");

            Assert.That(BitConverter.ToUInt16(y), Is.EqualTo(0x1080), "Y's handle");
            Assert.That(y[2], Is.EqualTo(2), "Y watches a switch per driver");
            Assert.That(y[3], Is.EqualTo(1), "the first motor's board");
            Assert.That(y[4], Is.EqualTo(4), "the second motor's board, which need not be the first's");

            Assert.That(z[2], Is.Zero, "a drive with no endstop watches nothing");
        });
    }

    [Test]
    public void AMotorAlreadyOnItsSwitchIsMarkedHeldRatherThanTheAxisBeingStopped()
    {
        // The last byte of the entry says which motors of the axis were already down when the move
        // was built. The engine gives those no steps while the rest of the axis moves, which is what
        // squares a gantry that starts skewed with one side already on its switch
        MoveBuilder builder = NewBuilder(CartesianMachine());
        RawMove move = LinearMove(1, 0.0f, 10.0f, 0.0f);
        move.CheckEndstops = true;
        move.StopOnInput[1].SetPerDriver(Handle(0x1080), [1, 4]);
        move.StopOnInput[1].HoldDriver(1);

        byte[] buffer = new byte[MoveParams.Length(NumDrives)];
        Assert.That(builder.Build(move, buffer).HasMove, Is.True);

        int stopsAt = Marshal.SizeOf<MoveParamsHeader>() + (NumDrives * (sizeof(int) + sizeof(float)));
        byte[] y = buffer[(stopsAt + MoveStopInput.Length)..(stopsAt + (2 * MoveStopInput.Length))];

        Assert.Multiple(() =>
        {
            Assert.That(y[^1], Is.EqualTo(0b10), "the second motor is held, the first is not");
            Assert.That(y[2], Is.EqualTo(2), "and the drive still watches a switch per driver");
            Assert.That(y[3], Is.EqualTo(1), "each keeping the board it was given");
            Assert.That(y[4], Is.EqualTo(4));
        });
    }

    [Test]
    public void ClearingAStopInputForgetsWhichMotorsWereHeld()
    {
        // An entry is reused between moves, and a motor that was down for the last one says nothing
        // about this one. Leaving the bit set would give that motor no steps for a move that never
        // armed anything
        MoveStopInput stopInput = new();
        stopInput.SetPerDriver(Handle(0x1080), [1, 4]);
        stopInput.HoldDriver(0);
        Assert.That(stopInput.HeldDrivers, Is.EqualTo(1));

        stopInput.SetShared(Handle(0x1042), 3);
        Assert.That(stopInput.HeldDrivers, Is.Zero, "re-arming the drive forgets it");

        stopInput.HoldDriver(0);
        stopInput.Clear();
        Assert.That(stopInput.HeldDrivers, Is.Zero, "and so does clearing it");
    }

    [Test]
    public void AnH1MoveOnACoreXyGoesThroughTheKinematics()
    {
        // RepRapFirmware's Move::IsRawMotorMove: a homing move is a raw motor move only where the
        // geometry homes individual drives. On a CoreXY the endstop belongs to an axis, so G1 H1 X-10
        // has to be transformed like any other axis move - both motors turn
        MoveBuilder builder = NewBuilder(CartesianMachine("corexy"));

        RawMove move = LinearMove(1, -10.0f, 0.0f, 0.0f);
        move.MoveType = MoveType.Homing;
        move.CheckEndstops = true;

        (_, Submission? built) = Build(builder, move);

        Assert.Multiple(() =>
        {
            // X = -10 on a CoreXY is both motors turning by -10mm worth of steps, in opposite senses
            Assert.That(built!.EndPoints[0], Is.EqualTo(-800), "motor A");
            Assert.That(built.EndPoints[1], Is.EqualTo(-800), "motor B");
        });
    }

    [Test]
    public void ADeltaTowerEndpointReDerivesTheCarriagePosition()
    {
        // Homing a delta knows where a tower's motor is, not where the head is: the switch is at the
        // top of the tower and the head position follows from all three carriages. So the endpoint is
        // set and the coordinates fall out of it, which is the opposite direction from SetAxisPosition
        Move machine = CartesianMachine();
        machine.Kinematics = new DeltaKinematics
        {
            DeltaRadius = LinearDeltaKinematicsEngine.DefaultDeltaRadius,
            HomedHeight = LinearDeltaKinematicsEngine.DefaultHomedHeight,
            PrintRadius = LinearDeltaKinematicsEngine.DefaultPrintRadius
        };
        foreach (DeltaTower tower in machine.Kinematics is DeltaKinematics d ? d.Towers : [])
        {
            tower.Diagonal = LinearDeltaKinematicsEngine.DefaultDiagonal;
        }

        MoveBuilder builder = NewBuilder(machine);
        LinearDeltaKinematicsEngine engine = (LinearDeltaKinematicsEngine)Snapshot(machine).Geometry;

        // Put all three carriages at their homed heights, which is what homing a delta ends up doing
        for (int tower = 0; tower < LinearDeltaKinematicsEngine.UsualNumTowers; tower++)
        {
            int steps = (int)MathF.Round(engine.GetHomedCarriageHeight(tower) * machine.Axes[tower].StepsPerMm);
            builder.SetDriveEndpoint(tower, steps);
        }

        Assert.Multiple(() =>
        {
            // All three carriages level means the head is on the axis, at the homed height
            Assert.That(builder.StartCoordinates[0], Is.EqualTo(0.0f).Within(0.01f), "X");
            Assert.That(builder.StartCoordinates[1], Is.EqualTo(0.0f).Within(0.01f), "Y");
            Assert.That(builder.StartCoordinates[2], Is.EqualTo(engine.HomedHeight).Within(0.01f), "Z");
        });
    }

    [Test]
    public void InverseTimeModeTurnsTheDurationIntoASpeed()
    {
        // G93 F30 asks for the move to take one thirtieth of a minute, i.e. 2 seconds. Over 10mm
        // that is 5mm/sec, whatever F would have meant in G94
        MoveBuilder builder = NewBuilder(CartesianMachine());

        RawMove move = LinearMove(1, 10.0f, 0.0f, 0.0f);
        move.InverseTimeMode = true;
        move.DurationSec = 2.0f;

        (_, Submission? built) = Build(builder, move);

        Assert.That(built!.Header.RequestedSpeed * StepClockRate, Is.EqualTo(5.0f).Within(1e-4f));
    }

    [Test]
    public void AnH2MoveAddressesTheMotorsDirectly()
    {
        // H2 is a raw motor move on every geometry, so the coordinate is that one motor's position
        // and the kinematics are bypassed
        MoveBuilder builder = NewBuilder(CartesianMachine("corexy"));

        RawMove move = LinearMove(1, -10.0f, 0.0f, 0.0f);
        move.MoveType = MoveType.RawMotor;

        (_, Submission? built) = Build(builder, move);

        Assert.Multiple(() =>
        {
            Assert.That(built!.EndPoints[0], Is.EqualTo(-800), "motor A alone");
            Assert.That(built.EndPoints[1], Is.Zero, "motor B does not move");
        });
    }
}
