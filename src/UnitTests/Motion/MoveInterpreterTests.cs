using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Code = DuetAPI.Commands.Code;
using OmDriverId = DuetAPI.Utility.DriverId;

namespace UnitTests.Motion;

/// <summary>
/// Turning a movement code into the move the engine is asked to run
/// </summary>
/// <remarks>
/// Steps 1 to 6 of RepRapFirmware's <c>GCodes::DoStraightMove</c>. The move that comes out is what
/// the machine will do, so what is checked here is mostly where the axes end up and how fast they
/// get there - and, just as much, what the interpreter's own position is left at, because the next
/// move is measured from it
/// </remarks>
[TestFixture]
public class MoveInterpreterTests
{
    /// <summary>Everything a built move can be inspected through</summary>
    private sealed record Machine(DuetControlServer.Model.ObjectModel Model, MoveBuilder Builder,
                                  MovementState State, MoveInterpreter Interpreter);

    private static DuetControlServer.Model.ObjectModel NewModel()
        => new(null!, NullLogger<DuetControlServer.Model.ObjectModel>.Instance, Options.Create(new DuetControlServer.Settings()));

    /// <summary>
    /// A three-axis Cartesian machine with one extruder, described the way config.g would
    /// </summary>
    /// <param name="tool">The selected tool, or null if none is</param>
    /// <param name="kinematics">Geometry name</param>
    /// <param name="closedSwitches">Which switches of an endstop are already closed</param>
    /// <returns>The machine and the interpreter that plans for it</returns>
    private static Machine NewMachine(Tool? tool = null, string kinematics = "cartesian",
                                      Func<int, uint>? closedSwitches = null, int numExtruders = 1,
                                      bool withEndstops = false)
    {
        DuetControlServer.Model.ObjectModel model = NewModel();
        Move move = model.Move;
        move.MinimumMovementSpeed = 0.5f;
        move.MotionSystems.Add(new MotionSystem { PrintingAcceleration = 10000.0f, TravelAcceleration = 10000.0f });

        AddAxis(move, 'X', stepsPerMm: 80.0f, speedMmPerMin: 18000.0f, acceleration: 1000.0f, driver: 0);
        AddAxis(move, 'Y', stepsPerMm: 80.0f, speedMmPerMin: 18000.0f, acceleration: 1000.0f, driver: 1);
        AddAxis(move, 'Z', stepsPerMm: 400.0f, speedMmPerMin: 600.0f, acceleration: 100.0f, driver: 2);
        for (int extruder = 0; extruder < numExtruders; extruder++)
        {
            move.Extruders.Add(new Extruder
            {
                StepsPerMm = 400.0f,
                Speed = 3600.0f,
                Acceleration = 2000.0f,
                Driver = new OmDriverId(0, 3 + extruder)
            });
        }

        if (withEndstops)
        {
            // A homing move has to have something to stop on, or the arming refuses it outright
            for (int axis = 0; axis < move.Axes.Count; axis++)
            {
                // On an expansion board, because the main board carries no CAN hardware of its own
                model.Sensors.Endstops.Add(new Endstop { Type = EndstopType.InputPin, Port = $"1.io{axis}.in" });
            }
        }

        if (kinematics != "cartesian")
        {
            CoreKinematics core = new();
            core.InverseMatrix.Clear();
            float[][] inverse = kinematics == "corexy"
                ? [[1, 1, 0], [1, -1, 0], [0, 0, 1]]
                : [[1, 0, 0], [0, 1, 0], [0, 0, 1]];
            foreach (float[] row in inverse)
            {
                core.InverseMatrix.Add(row);
            }
            move.Kinematics = core;
        }

        if (tool is not null)
        {
            model.Tools.Add(tool);
            model.State.CurrentTool = tool.Number;
        }

        KinematicsEngine geometry = KinematicsFactory.Create(move.Kinematics);
        MotionParameters.ApplyAxisLimits(move, geometry);

        MoveBuilder builder = new(MotionParameters.FromObjectModel(move, geometry));
        MovementState state = new();
        MoveInterpreter interpreter = new(model, builder, state, new BedCompensation(model),
                                          new EndstopCorrection(null!, null!, NullLogger<EndstopCorrection>.Instance),
                                          () => tool, closedSwitches ?? (_ => 0));
        return new Machine(model, builder, state, interpreter);
    }

    /// <summary>How fast a homing move runs here, in mm/sec, which only a stall endstop's arming reads</summary>
    private const float HomingSpeedMmPerSec = 30.0f;

    /// <summary>
    /// Plan what the move watches and then build it, which is the order a move really happens in
    /// </summary>
    /// <remarks>
    /// The two are separate calls because the planning may go over the CAN bus and the building runs
    /// inside a lock that may not await. Going through both here rather than handing the builder an
    /// empty plan is what keeps these tests exercising the pair
    /// </remarks>
    private static RawMove Build(Machine machine, Code code, InputChannel input, bool isCoordinated,
                                 MoveType moveType)
    {
        MotionParameters parameters = machine.Builder.Parameters;
        List<EndstopPlan> plans = moveType.ChecksEndstops()
            ? EndstopPlanner.Plan(code, machine.Model.Move, machine.Model.Sensors, parameters.Geometry,
                                  parameters.SharedAxisCount(machine.Model.Move), parameters.StepsPerMm,
                                  HomingSpeedMmPerSec)
            : [];
        return machine.Interpreter.BuildRawMove(code, input, isCoordinated, moveType, plans);
    }

    private static void AddAxis(Move move, char letter, float stepsPerMm, float speedMmPerMin,
                                float acceleration, int driver, bool homed = true)
    {
        Axis axis = new()
        {
            Letter = letter,
            StepsPerMm = stepsPerMm,
            Speed = speedMmPerMin,
            Acceleration = acceleration,
            Visible = true,
            Homed = homed,
            Min = -200.0f,
            Max = 200.0f
        };
        axis.Drivers.Add(new OmDriverId(0, driver));
        move.Axes.Add(axis);
    }

    /// <summary>A channel in the state a freshly started job would leave it</summary>
    private static InputChannel NewInput(bool axesRelative = false, bool drivesRelative = true,
                                         bool inverseTime = false, DistanceUnit unit = DistanceUnit.MM,
                                         float feedRate = 50.0f)
        => new()
        {
            AxesRelative = axesRelative,
            DrivesRelative = drivesRelative,
            InverseTimeMode = inverseTime,
            DistanceUnit = unit,
            FeedRate = feedRate
        };

    private static Code G(string text) => new(text);

    /// <summary>
    /// A tool an E word can address, filled in the way M563 leaves one
    /// </summary>
    /// <param name="number">Tool number</param>
    /// <param name="extruders">Extruders it drives, defaulting to the first</param>
    /// <returns>The tool</returns>
    /// <remarks>
    /// The even mix matters: a single E value is shared out by the ratios, so a tool with none
    /// recorded extrudes nothing. ToolManager.Define fills them in for the same reason
    /// </remarks>
    private static Tool NewTool(int number = 0, params int[] extruders)
    {
        Tool tool = new() { Number = number };
        int[] drives = extruders.Length > 0 ? extruders : [0];
        float share = 1.0f / drives.Length;
        foreach (int extruder in drives)
        {
            tool.Extruders.Add(extruder);
            tool.Mix.Add(share);
        }
        return tool;
    }

    /// <summary>Select a workplace and give one axis an offset in it</summary>
    private static void SetWorkplaceOffset(Machine machine, int axis, int workplace, float offset)
    {
        ObservableCollection<float> offsets = machine.Model.Move.Axes[axis].WorkplaceOffsets;
        while (offsets.Count <= workplace)
        {
            offsets.Add(0.0f);
        }
        offsets[workplace] = offset;
        machine.Model.Move.MotionSystems[0].WorkplaceNumber = workplace;
    }

    [Test]
    public void AnAbsoluteMoveTargetsTheCoordinateItNames()
    {
        Machine machine = NewMachine();
        RawMove raw = Build(machine, G("G1 X10 Y20 F3000"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(raw.Coords[0], Is.EqualTo(10.0f));
            Assert.That(raw.Coords[1], Is.EqualTo(20.0f));
            Assert.That(machine.State.CurrentUserPosition[0], Is.EqualTo(10.0f), "the interpreter moved on with the machine");
            Assert.That(raw.LinearAxesMentioned, Is.True);
            Assert.That(raw.RotationalAxesMentioned, Is.False);
        });
    }

    [Test]
    public void AnAxisTheCodeDoesNotNameStaysWhereItWas()
    {
        // A RawMove is built fresh for every move, so an axis that is not written would be commanded
        // to zero - which is a dive to the origin rather than the axis being left alone
        Machine machine = NewMachine();
        Build(machine, G("G1 X10 Y20 Z5"), NewInput(), isCoordinated: true, MoveType.Normal);

        RawMove second = Build(machine, G("G1 X30"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(second.Coords[0], Is.EqualTo(30.0f));
            Assert.That(second.Coords[1], Is.EqualTo(20.0f), "Y holds the position the last move left it at");
            Assert.That(second.Coords[2], Is.EqualTo(5.0f), "and so does Z");
        });
    }

    [Test]
    public void ARelativeMoveIsMeasuredFromWhereTheLastOneEnded()
    {
        Machine machine = NewMachine();
        Build(machine, G("G1 X10"), NewInput(), isCoordinated: true, MoveType.Normal);

        RawMove raw = Build(machine, G("G1 X15"), NewInput(axesRelative: true), isCoordinated: true, MoveType.Normal);

        Assert.That(raw.Coords[0], Is.EqualTo(25.0f));
    }

    [Test]
    public void TheWorkplaceOffsetIsAddedToAnOrdinaryMove()
    {
        Machine machine = NewMachine();
        SetWorkplaceOffset(machine, axis: 0, workplace: 1, offset: 7.0f);

        RawMove raw = Build(machine, G("G1 X10"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.That(raw.Coords[0], Is.EqualTo(17.0f));
    }

    [Test]
    public void G53AndSystemMacrosNameMachineCoordinates()
    {
        // Both bypass the workplace offset, for different reasons: G53 says so on the line, and a
        // system macro is the machine's own code rather than the operator's
        Machine machine = NewMachine();
        SetWorkplaceOffset(machine, axis: 0, workplace: 1, offset: 7.0f);

        Code g53 = G("G1 X10");
        g53.Flags |= CodeFlags.EnforceAbsolutePosition;
        Code macro = G("G1 X10");
        macro.Flags |= CodeFlags.IsFromSystemMacro;

        Assert.Multiple(() =>
        {
            Assert.That(Build(machine, g53, NewInput(), true, MoveType.Normal).Coords[0], Is.EqualTo(10.0f));
            Assert.That(Build(machine, macro, NewInput(), true, MoveType.Normal).Coords[0], Is.EqualTo(10.0f));
        });
    }

    [Test]
    public void InchesScaleALinearAxisButNotARotationalOne()
    {
        Machine machine = NewMachine();
        machine.Model.Move.Axes[1].Rotational = true;

        RawMove raw = Build(machine, G("G1 X1 Y90"), NewInput(unit: DistanceUnit.Inch),
                                                       isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(raw.Coords[0], Is.EqualTo(25.4f).Within(1e-4f), "an inch of X");
            Assert.That(raw.Coords[1], Is.EqualTo(90.0f), "degrees are degrees whatever G20 says");
            Assert.That(raw.RotationalAxesMentioned, Is.True);
            Assert.That(raw.LinearAxesMentioned, Is.True);
        });
    }

    [Test]
    public void AnEndstopMoveCannotBePausedAfter()
    {
        // It may stop short, so where it would resume from is not known until it has finished
        Machine machine = NewMachine(withEndstops: true);
        RawMove homing = Build(machine, G("G1 H1 X-250"), NewInput(), isCoordinated: true, MoveType.Homing);
        RawMove ordinary = Build(machine, G("G1 X10"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(homing.CheckEndstops, Is.True);
            Assert.That(homing.CanPauseAfter, Is.False);
            Assert.That(ordinary.CheckEndstops, Is.False);
            Assert.That(ordinary.CanPauseAfter, Is.True);
        });
    }

    [Test]
    public void AnEndstopMoveThatAlsoExtrudesIsRefused()
    {
        // The extruder speeds an extruder endstop is validated against are worked out from the move's
        // total extrusion, which an axis moving at the same time invalidates
        Machine machine = NewMachine(NewTool(), withEndstops: true);

        Assert.Throws<GCodeException>(
            () => Build(machine, G("G1 H1 X-250 E5"), NewInput(), isCoordinated: true, MoveType.Homing));
    }

    [Test]
    public void ASpecialMoveLeavesTheInterpreterPositionAlone()
    {
        // A motor position is not an axis position, so writing one back would tell the interpreter the
        // machine is somewhere it is not
        Machine machine = NewMachine();
        Build(machine, G("G1 X10"), NewInput(), isCoordinated: true, MoveType.Normal);

        RawMove raw = Build(machine, G("G1 H2 X5"), NewInput(axesRelative: true),
                                                       isCoordinated: true, MoveType.RawMotor);

        Assert.Multiple(() =>
        {
            Assert.That(machine.State.CurrentUserPosition[0], Is.EqualTo(10.0f), "unchanged by the motor move");
            Assert.That(raw.Coords[0], Is.EqualTo(5.0f), "which starts from the machine position, still zero");
        });
    }

    [Test]
    public void AnUnhomedAxisCannotBeMovedToACoordinate()
    {
        Machine machine = NewMachine();
        machine.Model.Move.Axes[0].Homed = false;
        machine.Model.Move.NoMovesBeforeHoming = true;

        GCodeException error = Assert.Throws<GCodeException>(
            () => Build(machine, G("G1 X10"), NewInput(), isCoordinated: true, MoveType.Normal))!;

        Assert.That(error.Message, Does.Contain("X"));
    }

    [Test]
    public void M564S0AllowsMovingAnUnhomedAxis()
    {
        // Which is what makes a homing macro's own moves possible
        Machine machine = NewMachine();
        machine.Model.Move.Axes[0].Homed = false;
        machine.Model.Move.NoMovesBeforeHoming = false;

        Assert.DoesNotThrow(() => Build(machine, G("G1 X10"), NewInput(), isCoordinated: true, MoveType.Normal));
    }

    [Test]
    public void AnExtruderOnlyMoveLeavesTheAxesAlone()
    {
        Machine machine = NewMachine(NewTool());
        Build(machine, G("G1 X10"), NewInput(), isCoordinated: true, MoveType.Normal);

        RawMove raw = Build(machine, G("G1 E2"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(raw.Coords[0], Is.EqualTo(10.0f), "the axes keep the coordinates they were seeded with");
            Assert.That(raw.Coords[MotionParameters.ExtruderToDrive(0)], Is.EqualTo(2.0f));
            Assert.That(raw.HasPositiveExtrusion, Is.True);
        });
    }

    [Test]
    public void ExtrudingWithNoToolSelectedIsRefused()
    {
        // A slicer that emits E before T is describing a print for a machine it thinks is set up
        Machine machine = NewMachine();

        Assert.Throws<GCodeException>(
            () => Build(machine, G("G1 X10 E2"), NewInput(), isCoordinated: true, MoveType.Normal));
    }

    [Test]
    public void AbsoluteExtrusionIsTheDifferenceFromWhereTheExtruderWas()
    {
        Machine machine = NewMachine(NewTool());
        machine.Model.Move.Extruders[0].RawPosition = 8.0f;

        RawMove raw = Build(machine, G("G1 X10 E10"), NewInput(drivesRelative: false),
                                                       isCoordinated: true, MoveType.Normal);

        Assert.That(raw.Coords[MotionParameters.ExtruderToDrive(0)], Is.EqualTo(2.0f));
    }

    [Test]
    public void AMixingToolSplitsOneValueByItsRatios()
    {
        Tool tool = NewTool(extruders: [0, 1]);
        tool.Mix.Clear();                       // M567 replaces the even split the tool was defined with
        tool.Mix.Add(0.25f);
        tool.Mix.Add(0.75f);

        Machine machine = NewMachine(tool, numExtruders: 2);

        RawMove raw = Build(machine, G("G1 X10 E4"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(raw.Coords[MotionParameters.ExtruderToDrive(0)], Is.EqualTo(1.0f));
            Assert.That(raw.Coords[MotionParameters.ExtruderToDrive(1)], Is.EqualTo(3.0f));
        });
    }

    [Test]
    public void AnExtrusionValuePerDriveMustMatchTheToolsDriveCount()
    {
        // A count that matches neither one value nor one per drive is a mistake rather than something
        // to interpret
        Tool tool = NewTool(extruders: [0, 1]);
        Machine machine = NewMachine(tool, numExtruders: 2);

        Assert.Throws<GCodeException>(
            () => Build(machine, G("G1 X10 E1:2:3"), NewInput(), isCoordinated: true, MoveType.Normal));
    }

    [Test]
    public void M221ScalesTheExtrusionOfAPrintingMove()
    {
        Machine machine = NewMachine(NewTool());
        machine.Model.Move.Extruders[0].Factor = 0.5f;

        RawMove raw = Build(machine, G("G1 X10 E4"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.That(raw.Coords[MotionParameters.ExtruderToDrive(0)], Is.EqualTo(2.0f));
    }

    [Test]
    public void AFeedRatePersistsAcrossCodesAndIsConvertedToMmPerSecond()
    {
        Machine machine = NewMachine();
        InputChannel input = NewInput();

        RawMove first = Build(machine, G("G1 X10 F3000"), input, isCoordinated: true, MoveType.Normal);
        RawMove second = Build(machine, G("G1 X20"), input, isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(first.FeedRateMmPerSec, Is.EqualTo(50.0f).Within(1e-4f));
            Assert.That(input.FeedRate, Is.EqualTo(3000.0f), "kept unconverted, which is what inputs[].feedRate reports");
            Assert.That(second.FeedRateMmPerSec, Is.EqualTo(50.0f).Within(1e-4f), "and carries to the next move");
        });
    }

    [Test]
    public void M220ScalesAPrintingMoveButNotASystemMacro()
    {
        Machine machine = NewMachine();
        machine.Model.Move.SpeedFactor = 2.0f;

        RawMove job = Build(machine, G("G1 X10 F3000"), NewInput(), isCoordinated: true, MoveType.Normal);

        Code macro = G("G1 X10 F3000");
        macro.Flags |= CodeFlags.IsFromSystemMacro;
        RawMove fromMacro = Build(machine, macro, NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(job.ApplyM220M221, Is.True);
            Assert.That(job.FeedRateMmPerSec, Is.EqualTo(100.0f).Within(1e-4f));
            Assert.That(fromMacro.ApplyM220M221, Is.False);
            Assert.That(fromMacro.FeedRateMmPerSec, Is.EqualTo(50.0f).Within(1e-4f));
        });
    }

    [Test]
    public void AG0IsARapidOnlyWhenTheMachineIsNotPrinting()
    {
        // On a mill or a laser F describes the cut and a G0 is the move between cuts; on an FFF
        // machine G0 honours the speed the slicer chose for the travel move
        Machine machine = NewMachine();
        machine.Model.State.MachineMode = MachineMode.CNC;
        RawMove rapid = Build(machine, G("G0 X10 F3000"), NewInput(), isCoordinated: false, MoveType.Normal);

        machine.Model.State.MachineMode = MachineMode.FFF;
        RawMove travel = Build(machine, G("G0 X20 F3000"), NewInput(), isCoordinated: false, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(rapid.FeedRateMmPerSec, Is.EqualTo(1000.0f).Within(1e-4f), "MaximumG0FeedRate");
            Assert.That(rapid.UsingStandardFeedrate, Is.False);
            Assert.That(rapid.ApplyM220M221, Is.False, "a rapid is not part of the print");
            Assert.That(travel.FeedRateMmPerSec, Is.EqualTo(50.0f).Within(1e-4f));
            Assert.That(travel.UsingStandardFeedrate, Is.True);
        });
    }

    [Test]
    public void InverseTimeModeNeedsAFeedRateOnEveryMove()
    {
        // G93's F describes this move's length rather than a speed, so it cannot carry over
        Machine machine = NewMachine();

        Assert.Multiple(() =>
        {
            Assert.Throws<GCodeException>(
                () => Build(machine, G("G1 X10"), NewInput(inverseTime: true), isCoordinated: true, MoveType.Normal));

            RawMove raw = Build(machine, G("G1 X10 F2"), NewInput(inverseTime: true),
                                                           isCoordinated: true, MoveType.Normal);
            Assert.That(raw.DurationSec, Is.EqualTo(30.0f).Within(1e-4f), "one over two minutes");
            Assert.That(raw.InverseTimeMode, Is.True);
        });
    }

    [Test]
    public void TheSpeedFactorShortensAnInverseTimeMoveRatherThanLengtheningIt()
    {
        // F is a duration here, so M220 S200 should make the move take half as long
        Machine machine = NewMachine();
        machine.Model.Move.SpeedFactor = 2.0f;

        RawMove raw = Build(machine, G("G1 X10 F2"), NewInput(inverseTime: true),
                                                       isCoordinated: true, MoveType.Normal);

        Assert.That(raw.DurationSec, Is.EqualTo(15.0f).Within(1e-4f));
    }

    [Test]
    public void AMoveNamingOnlyRotationalAxesIgnoresTheInchScaleOnItsFeedRate()
    {
        Machine machine = NewMachine();
        machine.Model.Move.Axes[1].Rotational = true;

        RawMove raw = Build(machine, G("G1 Y90 F600"), NewInput(unit: DistanceUnit.Inch),
                                                       isCoordinated: true, MoveType.Normal);

        Assert.That(raw.FeedRateMmPerSec, Is.EqualTo(10.0f).Within(1e-4f), "degrees per minute, unconverted");
    }

    [Test]
    public void PressureAdvanceFollowsMovementInSomethingOtherThanZ()
    {
        Machine machine = NewMachine(NewTool());

        RawMove printing = Build(machine, G("G1 X10 E1"), NewInput(), isCoordinated: true, MoveType.Normal);
        RawMove zOnly = Build(machine, G("G1 Z1 E1"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(printing.UsePressureAdvance, Is.True);
            Assert.That(zOnly.UsePressureAdvance, Is.False);
        });
    }

    [Test]
    public void AnUnreachableAbsoluteTargetIsRefusedAndARelativeOneIsClamped()
    {
        // An absolute move names a place, and moving somewhere else instead would be wrong; a
        // relative move names a direction, so going as far as the machine can is the sensible reading
        Machine machine = NewMachine();

        Assert.Throws<GCodeException>(
            () => Build(machine, G("G1 X5000"), NewInput(), isCoordinated: true, MoveType.Normal));

        RawMove raw = Build(machine, G("G1 X5000"), NewInput(axesRelative: true),
                                                       isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(raw.Coords[0], Is.EqualTo(200.0f), "clamped to the M208 maximum");
            Assert.That(machine.State.CurrentUserPosition[0], Is.EqualTo(200.0f),
                        "and the interpreter told, or the next relative move starts from a place the machine never reached");
        });
    }

    [Test]
    public void ACartesianMoveIsNotSegmented()
    {
        Machine machine = NewMachine();
        RawMove raw = Build(machine, G("G1 X100 F3000"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.That(raw.SegmentCount, Is.EqualTo(1));
    }

    [Test]
    public void AMoveTooLongForTheStepClockIsSplit()
    {
        // The step clock is 32 bits at 750kHz, so a move occupying a large part of it cannot be timed
        // against it whatever the geometry says
        Machine machine = NewMachine();
        RawMove raw = Build(machine, G("G1 X200 F0.1"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.That(raw.SegmentCount, Is.GreaterThan(1));
    }

    [Test]
    public void TheAxisLetterBitmapsFollowTheConfiguration()
    {
        Machine machine = NewMachine();
        RawMove raw = Build(machine, G("G1 X10"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(raw.XAxes, Is.EqualTo(1u << 0));
            Assert.That(raw.YAxes, Is.EqualTo(1u << 1));
        });
    }

    [Test]
    public void AxesMentionedNamesTheAxesTheCodeCarries()
    {
        Machine machine = NewMachine();

        Assert.Multiple(() =>
        {
            Assert.That(machine.Interpreter.AxesMentioned(G("G1 X10 Z5"), 3), Is.EqualTo(0b101u));
            Assert.That(machine.Interpreter.AxesMentioned(G("G1 E2"), 3), Is.Zero);
        });
    }

    [Test]
    public void MentionsAxisOtherThanZIgnoresZAlone()
    {
        Machine machine = NewMachine();

        Assert.Multiple(() =>
        {
            Assert.That(machine.Interpreter.MentionsAxisOtherThanZ(G("G1 Z5"), 3), Is.False);
            Assert.That(machine.Interpreter.MentionsAxisOtherThanZ(G("G1 X1 Z5"), 3), Is.True);
        });
    }

    [Test]
    public void TheWorkplaceOffsetOfAnAxisThatHasNoneIsZero()
    {
        Axis axis = new();

        Assert.Multiple(() =>
        {
            Assert.That(MoveInterpreter.WorkplaceOffset(axis, -1), Is.Zero);
            Assert.That(MoveInterpreter.WorkplaceOffset(axis, axis.WorkplaceOffsets.Count), Is.Zero);
        });
    }

    [Test]
    public void ASpecialMoveStartsFromTheMachinePositionRatherThanTheAxisPosition()
    {
        Machine machine = NewMachine();
        machine.Builder.SetAxisPosition(0, 12.0f);

        RawMove raw = new() { MoveType = MoveType.RawMotor };
        machine.Interpreter.SeedSpecialMoveCoordinates(raw, 3);

        Assert.That(raw.Coords[0], Is.EqualTo(12.0f).Within(1e-3f));
    }

    [Test]
    public void ARawMotorMoveIsMeasuredInMotorPositionsAndAnythingElseInAxisPositions()
    {
        // A raw motor move starts from the motor endpoints converted back to mm per drive; H3 and H4
        // are still axis moves, so they start from the axis coordinates
        Machine machine = NewMachine();
        machine.Builder.SetAxisPosition(0, 12.0f);

        RawMove motors = new() { MoveType = MoveType.RawMotor };
        RawMove axes = new() { MoveType = MoveType.SenseLength };
        machine.Interpreter.SeedSpecialMoveCoordinate(motors, 0);
        machine.Interpreter.SeedSpecialMoveCoordinate(axes, 0);

        Assert.Multiple(() =>
        {
            Assert.That(motors.Coords[0], Is.EqualTo(12.0f).Within(1e-3f), "960 microsteps at 80 steps/mm");
            Assert.That(axes.Coords[0], Is.EqualTo(12.0f).Within(1e-4f));
        });
    }

    [Test]
    public void AHomingMoveArmsTheAxesItNamesAndNoOthers()
    {
        // A homing move naming X must not be stopped by Z's switch happening to be closed already
        Machine machine = NewMachine(withEndstops: true);
        RawMove raw = Build(machine, G("G1 H1 X-250"), NewInput(), isCoordinated: true, MoveType.Homing);

        Assert.Multiple(() =>
        {
            Assert.That(raw.ArmedAxes, Is.EqualTo(new[] { 0 }));
            Assert.That(raw.StopOnInput[0].NumSwitches, Is.EqualTo(1), "X watches its switch");
            Assert.That(raw.StopOnInput[1].NumSwitches, Is.Zero, "Y watches nothing");
            Assert.That(raw.StopOnInput[2].NumSwitches, Is.Zero, "and neither does Z");
        });
    }

    [Test]
    public void AnEndstopMoveOnAnAxisWithNoEndstopIsRefused()
    {
        // Carrying on would run the move to its full commanded length with nothing to stop it, which
        // for a homing move means driving into the end of the axis
        Machine machine = NewMachine();

        Assert.Throws<GCodeException>(
            () => Build(machine, G("G1 H1 X-250"), NewInput(), isCoordinated: true, MoveType.Homing));
    }

    [Test]
    public void ArmingAMoveForgetsWhatStoppedTheLastOne()
    {
        // A move that ran its full length is never reported as stopped, so the latch has to be
        // cleared where the next one is armed rather than where the last one finished
        Machine machine = NewMachine(withEndstops: true);
        machine.State.RecordEndstopTriggered(0b111);

        Build(machine, G("G1 H1 X-250"), NewInput(), isCoordinated: true, MoveType.Homing);

        Assert.That(machine.State.EndstopsTriggered, Is.Zero);
    }

    [Test]
    public void AnAxisAlreadyOnItsSwitchIsRecordedAsTriggeredAndHeldWhereItIs()
    {
        // Such an axis never moves, so no input changes and no stop is ever reported - and yet it is
        // at its switch, which is the whole question a homing move asks
        Machine machine = NewMachine(withEndstops: true, closedSwitches: _ => 0b1);
        machine.Model.Sensors.Endstops[0]!.Triggered = true;
        machine.Builder.SetAxisPosition(0, 3.0f);

        RawMove raw = Build(machine, G("G1 H1 X-250"), NewInput(), isCoordinated: true, MoveType.Homing);

        Assert.Multiple(() =>
        {
            Assert.That(machine.State.EndstopsTriggered & 0b1, Is.EqualTo(0b1u));
            Assert.That(raw.Coords[0], Is.EqualTo(3.0f).Within(1e-3f), "commanded to stay where it is");
        });
    }

    [Test]
    public void EachSegmentEndsAFractionOfTheWayAlongAndTheLastOneExactlyAtTheTarget()
    {
        // Otherwise a long move would accumulate rounding and stop short of where it was asked to go
        Machine machine = NewMachine();
        RawMove raw = Build(machine, G("G1 X30 F3000"), NewInput(), isCoordinated: true, MoveType.Normal);
        raw.SegmentCount = 3;
        SegmentedMove segments = SegmentedMove.From(raw, [0.0f, 0.0f, 0.0f], 3, MotionLimits.MaxAxesPlusExtruders - 1);

        machine.Interpreter.PrepareSegment(raw, segments, 1);
        float first = raw.Coords[0];
        machine.Interpreter.PrepareSegment(raw, segments, 3);
        float last = raw.Coords[0];

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(10.0f).Within(1e-4f), "a third of the way, of three segments");
            Assert.That(last, Is.EqualTo(30.0f), "and the last lands exactly on the target");
        });
    }

    [Test]
    public void EverySegmentIsItsOwnMoveToTheEngine()
    {
        Machine machine = NewMachine();
        RawMove raw = Build(machine, G("G1 X30 F3000"), NewInput(), isCoordinated: true, MoveType.Normal);
        raw.MoveId = 42;

        machine.Interpreter.PrepareSegment(raw, SegmentedMove.From(raw, [0.0f, 0.0f, 0.0f], 3, MotionLimits.MaxAxesPlusExtruders - 1), 1);

        Assert.That(raw.MoveId, Is.Zero, "so it is given a correlation id of its own when it is queued");
    }

    [Test]
    public void ASegmentCarriesItsShareOfTheExtrusionRatherThanTheWholeMoves()
    {
        Machine machine = NewMachine(NewTool());
        RawMove raw = Build(machine, G("G1 X30 E6 F3000"), NewInput(), isCoordinated: true, MoveType.Normal);
        raw.SegmentCount = 3;

        SegmentedMove segments = SegmentedMove.From(raw, [0.0f, 0.0f, 0.0f], 3, MotionLimits.MaxAxesPlusExtruders - 1);
        machine.Interpreter.PrepareSegment(raw, segments, 1);

        Assert.That(raw.Coords[MotionParameters.ExtruderToDrive(0)], Is.EqualTo(2.0f).Within(1e-4f));
    }

    [Test]
    public void ASpecialMoveIsNotPutThroughTheAxisAndBedTransform()
    {
        // It bypasses the user coordinate system entirely, so a skew correction would be applied to a
        // motor position that was never in that space
        Machine machine = NewMachine();
        machine.Model.Move.Compensation.Skew.TanXY = 0.01f;

        RawMove special = new() { MoveType = MoveType.RawMotor };
        special.Coords[1] = 10.0f;
        SegmentedMove segments = SegmentedMove.From(special, [0.0f, 0.0f, 0.0f], 3, MotionLimits.MaxAxesPlusExtruders - 1);

        machine.Interpreter.PrepareSegment(special, segments, 1);

        Assert.That(special.Coords[0], Is.Zero, "X picked up no cross term from Y");
    }

    [Test]
    public void AnOrdinarySegmentCarriesTheSkewCorrection()
    {
        Machine machine = NewMachine();
        machine.Model.Move.Compensation.Skew.TanXY = 0.01f;

        RawMove raw = new() { MoveType = MoveType.Normal };
        raw.Coords[1] = 10.0f;
        SegmentedMove segments = SegmentedMove.From(raw, [0.0f, 0.0f, 0.0f], 3, MotionLimits.MaxAxesPlusExtruders - 1);

        machine.Interpreter.PrepareSegment(raw, segments, 1);

        Assert.That(raw.Coords[0], Is.EqualTo(0.1f).Within(1e-4f), "X is corrected when Y moves, which is what CompensateXY defaults to");
    }
}
