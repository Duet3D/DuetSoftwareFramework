using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Native;
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
                                  MovementState State, MoveInterpreter Interpreter, BedCompensation Bed);

    private static DuetControlServer.Model.ObjectModel NewModel()
        => new(null!, NullLogger<DuetControlServer.Model.ObjectModel>.Instance, Options.Create(new DuetControlServer.Settings()));

    /// <summary>
    /// A three-axis Cartesian machine with one extruder, described the way config.g would
    /// </summary>
    /// <param name="tool">The selected tool, or null if none is</param>
    /// <param name="kinematics">Geometry name</param>
    /// <param name="closedSwitches">Which switches of an endstop are already closed</param>
    /// <param name="withU">Whether the machine has a U axis after Z, as an IDEX machine's second carriage</param>
    /// <returns>The machine and the interpreter that plans for it</returns>
    private static Machine NewMachine(Tool? tool = null, string kinematics = "cartesian",
                                      Func<int, uint>? closedSwitches = null, int numExtruders = 1,
                                      bool withEndstops = false, bool withU = false)
    {
        DuetControlServer.Model.ObjectModel model = NewModel();
        Move move = model.Move;
        move.MinimumMovementSpeed = 0.5f;
        move.MotionSystems.Add(new MotionSystem { PrintingAcceleration = 10000.0f, TravelAcceleration = 10000.0f });

        AddAxis(move, 'X', stepsPerMm: 80.0f, speedMmPerMin: 18000.0f, acceleration: 1000.0f, driver: 0);
        AddAxis(move, 'Y', stepsPerMm: 80.0f, speedMmPerMin: 18000.0f, acceleration: 1000.0f, driver: 1);
        AddAxis(move, 'Z', stepsPerMm: 400.0f, speedMmPerMin: 600.0f, acceleration: 100.0f, driver: 2);
        if (withU)
        {
            AddAxis(move, 'U', stepsPerMm: 80.0f, speedMmPerMin: 18000.0f, acceleration: 1000.0f, driver: 10);
        }
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

        MoveBuilder builder = new(KinematicsAxes.Snapshot(move));
        MovementState state = new();
        BedCompensation bed = new(model);
        MoveInterpreter interpreter = new(model, builder, state, bed,
                                          new EndstopCorrection(null!, null!, NullLogger<EndstopCorrection>.Instance),
                                          () => tool, closedSwitches ?? (_ => 0));
        return new Machine(model, builder, state, interpreter, bed);
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

    /// <summary>A code as the job file channel would deliver it</summary>
    private static Code Job(string text) => new(text) { Channel = CodeChannel.File };

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
    public void AResumedMoveAsksOnlyForWhatIsLeftOfIt()
    {
        // A feedhold stops at a segment boundary, so the code that produced the segments may be part
        // done when the job file is rewound to it. What is relative - the axis word and the
        // extrusion alike - is an amount, and only the rest of that amount is still owed
        Machine machine = NewMachine(NewTool());
        machine.State.MoveFractionToSkip = 0.25f;

        RawMove raw = Build(machine, Job("G1 X10 E4"), NewInput(axesRelative: true), isCoordinated: true,
                            MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(raw.Coords[0], Is.EqualTo(7.5f).Within(1e-4f));
            Assert.That(machine.State.CurrentUserPosition[0], Is.EqualTo(7.5f).Within(1e-4f));
            Assert.That(raw.Coords[MotionParameters.ExtruderToDrive(0)], Is.EqualTo(3.0f).Within(1e-4f));
        });
    }

    [Test]
    public void AResumedMoveStillTargetsTheCoordinateItNames()
    {
        // The machine restarts from where it stopped, so an absolute target is already the rest of
        // the move. Scaling it as well would leave the line short of where the file said
        Machine machine = NewMachine(NewTool());
        machine.State.MoveFractionToSkip = 0.25f;

        RawMove raw = Build(machine, Job("G1 X10 E4"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(raw.Coords[0], Is.EqualTo(10.0f));
            Assert.That(raw.Coords[MotionParameters.ExtruderToDrive(0)], Is.EqualTo(3.0f).Within(1e-4f));
        });
    }

    [Test]
    public void TheResumedFractionIsSpentOnOneMove()
    {
        Machine machine = NewMachine(NewTool());
        machine.State.MoveFractionToSkip = 0.5f;

        Build(machine, Job("G1 X10 E4"), NewInput(), isCoordinated: true, MoveType.Normal);
        RawMove next = Build(machine, Job("G1 X20 E4"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(machine.State.MoveFractionToSkip, Is.Zero);
            Assert.That(next.Coords[MotionParameters.ExtruderToDrive(0)], Is.EqualTo(4.0f).Within(1e-4f));
        });
    }

    [TestCase(CodeChannel.Daemon)]
    [TestCase(CodeChannel.File2)]
    public void AnotherChannelsMoveDoesNotSpendTheResumedFraction(CodeChannel channel)
    {
        // The interpreter position is shared by every channel here, and the fraction describes the
        // job's next line on the first file channel. A daemon.g move landing in the window before the
        // job reads it must not be shortened - nor consume what the job is owed - and neither must a
        // fork of the job, which has no restore point of its own to have recorded a fraction with
        Machine machine = NewMachine(NewTool());
        machine.State.MoveFractionToSkip = 0.5f;

        Code other = G("G1 X10 E4");
        other.Channel = channel;
        RawMove raw = Build(machine, other, NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(raw.Coords[MotionParameters.ExtruderToDrive(0)], Is.EqualTo(4.0f).Within(1e-4f));
            Assert.That(machine.State.MoveFractionToSkip, Is.EqualTo(0.5f), "the job still gets it");
        });
    }

    [Test]
    public void AMoveRecordsTheFeedRateItWasReadWithRatherThanTheOneItRunsAt()
    {
        // What a pause saves and a resume puts back is the file's own F, so the speed factor must not
        // be folded into it - and a rapid, which ignores F altogether, must still record it
        Machine machine = NewMachine();
        machine.Model.Move.SpeedFactor = 2.0f;

        RawMove printing = Build(machine, G("G1 X10 F3000"), NewInput(), isCoordinated: true, MoveType.Normal);

        Assert.Multiple(() =>
        {
            Assert.That(printing.FeedRateMmPerSec, Is.EqualTo(100.0f).Within(1e-4f));
            Assert.That(printing.OriginalFeedRateMmPerSec, Is.EqualTo(50.0f).Within(1e-4f));
        });
    }

    [Test]
    public void TheInterpreterCanBePutBackInStepWithTheMachine()
    {
        // Where the machine has ended up somewhere the interpreter did not put it: a homing or
        // probing move that stopped short, or a feedhold that discarded the moves it had built. The
        // builder holds what the machine was really commanded, in machine coordinates, so coming
        // back the other way is the transform inverted
        Tool tool = NewTool();
        tool.Offsets.Add(2.0f);
        tool.Offsets.Add(0.0f);
        tool.Offsets.Add(0.0f);
        Machine machine = NewMachine(tool);
        machine.Builder.SetAxisPosition(0, 4.0f);

        // Built but never queued, which is what the interpreter running ahead of the machine looks
        // like from here
        Build(machine, G("G1 X100"), NewInput(), isCoordinated: true, MoveType.Normal);
        Assert.That(machine.State.CurrentUserPosition[0], Is.EqualTo(100.0f), "the interpreter ran ahead");

        machine.Interpreter.SyncInterpreterToMachine();

        // A tool offset of 2 puts the machine 2 below the coordinate the user named, so a machine
        // position of 4 is a user position of 6 - the transform run the other way
        Assert.That(machine.State.CurrentUserPosition[0], Is.EqualTo(6.0f).Within(1e-4f),
                    "and comes back to where the machine really is");
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
    public void MentionsAxisOtherThanZCountsALowerCaseZ()
    {
        // M584 'z0.5 creates an axis distinct from Z, so moving it is not a Z-only move
        Machine machine = NewMachine();
        AddAxis(machine.Model.Move, 'z', stepsPerMm: 80.0f, speedMmPerMin: 600.0f, acceleration: 100.0f, driver: 5);

        Assert.Multiple(() =>
        {
            Assert.That(machine.Interpreter.MentionsAxisOtherThanZ(G("G1 'z5"), 4), Is.True);
            Assert.That(machine.Interpreter.MentionsAxisOtherThanZ(G("G1 Z5"), 4), Is.False);
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

    // ---- Moves DSF generates itself ----------------------------------------------------------------

    /// <summary>A tool that maps X onto U alone, as on the second carriage of an IDEX machine</summary>
    /// <param name="uOffset">Its offset on U</param>
    private static Tool XOnUTool(float uOffset = 0.0f)
    {
        Tool tool = NewTool();
        tool.Axes.Add([3]);
        tool.Axes.Add([1]);
        tool.Axes.Add([2]);
        tool.Offsets.Add(0.0f);
        tool.Offsets.Add(0.0f);
        tool.Offsets.Add(0.0f);
        tool.Offsets.Add(uOffset);
        return tool;
    }

    /// <summary>A height map tilted along X, reading 0.1 mm higher for every 10 mm of X from 0 at X0</summary>
    private const string TiltedMap = """
        RepRapFirmware height map file v2 generated at 2026-01-01 12:00
        axis0,axis1,min0,max0,min1,max1,radius,spacing0,spacing1,num0,num1
        X,Y,0.00,20.00,0.00,20.00,-1.00,10.00,10.00,3,3
          0.000,  0.100,  0.200
          0.000,  0.100,  0.200
          0.000,  0.100,  0.200
        """;

    [Test]
    public void AGeneratedMoveStartsWhereTheInterpreterIsThroughTheToolsOffsets()
    {
        Tool tool = NewTool();
        foreach (float offset in new[] { 2.0f, -1.0f, 0.5f })
        {
            tool.Offsets.Add(offset);
        }
        Machine machine = NewMachine(tool);
        new[] { 10.0f, 20.0f, 5.0f }.CopyTo(machine.State.CurrentUserPosition, 0);

        RawMove raw = machine.Interpreter.StartGeneratedMove();

        Assert.Multiple(() =>
        {
            Assert.That(raw.Coords[..3], Is.EqualTo(new[] { 8.0f, 21.0f, 4.5f }),
                        "the interpreter's position less the tool's offsets, which is where a G1 starts too");
            Assert.That(raw.InitialCoords[..3], Is.EqualTo(raw.Coords[..3]), "and that is where it starts");
            Assert.That(raw.MoveType, Is.EqualTo(MoveType.Normal));
        });
    }

    [Test]
    public void AGeneratedMoveCountsTheAxesLetteredXAndYWhateverTheToolMaps()
    {
        Machine machine = NewMachine(XOnUTool(), withU: true);

        RawMove raw = machine.Interpreter.StartGeneratedMove();

        Assert.Multiple(() =>
        {
            Assert.That(raw.XAxes, Is.EqualTo(1u << 0), "X is the axis lettered X, not U");
            Assert.That(raw.YAxes, Is.EqualTo(1u << 1));
        });
    }

    [Test]
    public void AnAxisTheToolMapsAwayFromStaysWhereTheMachineIs()
    {
        // The tool transform gives X no coordinate, so the move has to keep it where it is - with the
        // skew it carries as the machine position taken off and put back on again
        Machine machine = NewMachine(XOnUTool(), withU: true);
        machine.Model.Move.Compensation.Skew.TanXY = 0.01f;
        machine.Builder.SetAxisPosition(0, 7.3f);
        machine.Builder.SetAxisPosition(1, 30.0f);
        machine.State.CurrentUserPosition[1] = 30.0f;

        RawMove raw = machine.Interpreter.StartGeneratedMove();
        float started = raw.Coords[0];
        machine.Interpreter.FinishGeneratedMove(raw);

        Assert.Multiple(() =>
        {
            Assert.That(started, Is.EqualTo(7.0f).Within(1e-4f), "in the move's own coordinates, before the skew");
            Assert.That(raw.Coords[0], Is.EqualTo(7.3f).Within(1e-4f), "and back where the machine has it once transformed");
        });
    }

    [Test]
    public void AGeneratedMoveIsSkewedWithNoTool()
    {
        // With the tool selected a G1 would correct U, the tool's X; a generated move corrects X
        Machine machine = NewMachine(XOnUTool(), withU: true);
        machine.Model.Move.Compensation.Skew.TanXY = 0.01f;

        RawMove raw = machine.Interpreter.StartGeneratedMove();
        raw.Coords[1] = 50.0f;
        machine.Interpreter.FinishGeneratedMove(raw);

        Assert.Multiple(() =>
        {
            Assert.That(raw.Coords[0], Is.EqualTo(0.5f).Within(1e-4f), "X by tanXY × Y");
            Assert.That(raw.Coords[3], Is.Zero, "U is not corrected");
        });
    }

    [Test]
    public void AGeneratedMoveFollowsZWithTheSkewOfXAndY()
    {
        Machine machine = NewMachine();
        machine.Model.Move.Compensation.Skew.TanXZ = 0.1f;
        machine.Model.Move.Compensation.Skew.TanYZ = 0.05f;

        RawMove raw = machine.Interpreter.StartGeneratedMove();
        raw.Coords[2] = -101.0f;
        machine.Interpreter.FinishGeneratedMove(raw);

        Assert.That(raw.Coords[..3], Is.EqualTo(new[] { -10.1f, -5.05f, -101.0f }).Within(1e-4f));
    }

    [Test]
    public void AGeneratedMoveCarriesTheHeightMapCorrection()
    {
        Machine machine = NewMachine();
        Assert.That(machine.Bed.LoadAsync(new System.IO.StringReader(TiltedMap), "heightmap.csv", default)
                                   .AsTask().GetAwaiter().GetResult(), Is.Null);

        RawMove raw = machine.Interpreter.StartGeneratedMove();
        raw.Coords[0] = 15.0f;
        raw.Coords[2] = -1.0f;
        machine.Interpreter.FinishGeneratedMove(raw);

        Assert.That(raw.Coords[2], Is.EqualTo(-0.85f).Within(1e-4f), "Z raised by the map's 0.15 mm at X15");
    }

    [Test]
    public void TheInterpreterFollowsWhereAGeneratedMoveStoppedWithItsTransformTakenOff()
    {
        // Where a skewed dive left the motors reads as the X and Y it was asked for
        Machine machine = NewMachine();
        machine.Model.Move.Compensation.Skew.TanXZ = 0.1f;
        machine.Model.Move.Compensation.Skew.TanYZ = 0.05f;
        machine.Builder.SetAxisPosition(0, -10.1f);
        machine.Builder.SetAxisPosition(1, -5.05f);
        machine.Builder.SetAxisPosition(2, -101.0f);

        machine.Interpreter.SyncGeneratedMoveToMachine();

        Assert.That(machine.State.CurrentUserPosition[..3], Is.EqualTo(new[] { 0.0f, 0.0f, -101.0f }).Within(1e-4f));
    }

    [Test]
    public void TheInterpreterFollowsWhereAGeneratedMoveStoppedIntoTheToolsCoordinates()
    {
        // The skew came off the axis lettered X and not off U, and U and X are then the selected
        // tool's coordinates: the machine position plus its offset
        Machine machine = NewMachine(XOnUTool(uOffset: 3.0f), withU: true);
        machine.Model.Move.Compensation.Skew.TanXZ = 0.1f;
        machine.Builder.SetAxisPosition(2, -1.0f);
        machine.Builder.SetAxisPosition(3, 15.0f);

        machine.Interpreter.SyncGeneratedMoveToMachine();

        Assert.Multiple(() =>
        {
            Assert.That(machine.State.CurrentUserPosition[3], Is.EqualTo(18.0f).Within(1e-4f), "U");
            Assert.That(machine.State.CurrentUserPosition[0], Is.EqualTo(18.0f).Within(1e-4f), "X, which the tool maps onto U");
            Assert.That(machine.State.CurrentUserPosition[2], Is.EqualTo(-1.0f).Within(1e-4f), "Z");
        });
    }

    /// <summary>A tool that duplicates X onto X and U, U offset by <paramref name="uOffset"/></summary>
    private static Tool DuplicatingTool(float uOffset)
    {
        Tool tool = NewTool();
        tool.Axes.Add([0, 3]);
        tool.Axes.Add([1]);
        tool.Axes.Add([2]);
        tool.Offsets.Add(0.0f);
        tool.Offsets.Add(0.0f);
        tool.Offsets.Add(0.0f);
        tool.Offsets.Add(uOffset);
        return tool;
    }

    /// <summary>
    /// A machine with the <see cref="DuplicatingTool"/> selected, X at 0 and U at 20, which is where
    /// the tool's map has them with the interpreter at X0
    /// </summary>
    private static Machine DuplicatingMachine()
    {
        Machine machine = NewMachine(DuplicatingTool(uOffset: -20.0f), withU: true);
        machine.Builder.SetAxisPosition(3, 20.0f);
        return machine;
    }

    /// <summary>Queue a generated move as the planner would, moving the builder on to its end</summary>
    private static void Queue(Machine machine, RawMove raw)
    {
        machine.Interpreter.FinishGeneratedMove(raw);
        raw.FeedRateMmPerSec = 50.0f;
        raw.LinearAxesMentioned = true;
        Assert.That(machine.Builder.Build(raw, new byte[MoveParams.Length(MotionLimits.MaxAxesPlusExtruders)]).Error,
                    Is.EqualTo(NativeMovementError.Ok));
    }

    [Test]
    public void AGeneratedMoveStartsWhereTheGeneratedMoveBeforeItLeftEveryAxis()
    {
        // The travel puts X at 20 and leaves U at 20. Going through the tool's map again would read
        // X as the mean of what X and U say, 10, and drive both carriages there
        Machine machine = DuplicatingMachine();
        RawMove travel = machine.Interpreter.StartGeneratedMove();
        travel.Coords[0] = 20.0f;
        Queue(machine, travel);

        RawMove dive = machine.Interpreter.StartGeneratedMove();

        Assert.Multiple(() =>
        {
            Assert.That(dive.Coords[0], Is.EqualTo(20.0f), "X where the travel left it");
            Assert.That(dive.Coords[3], Is.EqualTo(20.0f), "and U where it was");
            Assert.That(machine.State.CurrentUserPosition[0], Is.Zero, "the interpreter is not moved by a probing move");
        });
    }

    [Test]
    public void AGeneratedMoveThePlannerDidNotTakeIsNotStartedFrom()
    {
        // A move the ring had no room for is built again from the move before it
        Machine machine = DuplicatingMachine();
        RawMove travel = machine.Interpreter.StartGeneratedMove();
        travel.Coords[0] = 20.0f;
        Queue(machine, travel);
        RawMove refused = machine.Interpreter.StartGeneratedMove();
        refused.Coords[2] = -1.0f;
        machine.Interpreter.FinishGeneratedMove(refused);

        RawMove retry = machine.Interpreter.StartGeneratedMove();

        Assert.That(retry.Coords[..4], Is.EqualTo(new[] { 20.0f, 0.0f, 0.0f, 20.0f }));
    }

    [Test]
    public void AGeneratedMoveAfterAnyOtherMoveStartsThroughTheToolsTransform()
    {
        // Once something else has put the machine somewhere, the generated move before is history,
        // and the interpreter - which a G1 or G92 moves on - is where the move starts
        Machine machine = DuplicatingMachine();
        RawMove travel = machine.Interpreter.StartGeneratedMove();
        travel.Coords[0] = 20.0f;
        Queue(machine, travel);
        machine.Builder.SetAxisPosition(1, 5.0f);
        machine.State.CurrentUserPosition[1] = 5.0f;

        RawMove next = machine.Interpreter.StartGeneratedMove();

        Assert.That(next.Coords[..4], Is.EqualTo(new[] { 0.0f, 5.0f, 0.0f, 20.0f }),
                    "X0 through the tool's map: X at 0 and U at 20");
    }

    [Test]
    public void TheInterpreterFollowsTheLastGeneratedMoveOnceTheyAreDone()
    {
        Machine machine = DuplicatingMachine();
        RawMove travel = machine.Interpreter.StartGeneratedMove();
        travel.Coords[0] = 20.0f;
        travel.Coords[1] = 10.0f;
        Queue(machine, travel);

        machine.Interpreter.SyncInterpreterToGeneratedMove();

        Assert.Multiple(() =>
        {
            Assert.That(machine.State.CurrentUserPosition[0], Is.EqualTo(10.0f),
                        "X is the mean of what the tool's carriages say, 20 on X and 20 - 20 on U");
            Assert.That(machine.State.CurrentUserPosition[1], Is.EqualTo(10.0f));
            Assert.That(machine.State.CurrentUserPosition[3], Is.Zero, "U is its own coordinate plus the tool's offset");
        });
    }

    [Test]
    public void TheInterpreterIsLeftAloneWhenTheLastMoveWasNotGenerated()
    {
        Machine machine = DuplicatingMachine();
        machine.State.CurrentUserPosition[0] = 7.0f;

        machine.Interpreter.SyncInterpreterToGeneratedMove();

        Assert.That(machine.State.CurrentUserPosition[0], Is.EqualTo(7.0f));
    }

    [Test]
    public void TheInterpreterIsLeftAloneWhenSomethingElseMovedAfterTheLastGeneratedMove()
    {
        // The travel would read as X10 Y0 through the tool's map, but a move since has put the machine
        // somewhere else and the interpreter already follows that move
        Machine machine = DuplicatingMachine();
        RawMove travel = machine.Interpreter.StartGeneratedMove();
        travel.Coords[0] = 20.0f;
        Queue(machine, travel);
        machine.Builder.SetAxisPosition(1, 5.0f);
        machine.State.CurrentUserPosition[0] = 7.0f;
        machine.State.CurrentUserPosition[1] = 5.0f;

        machine.Interpreter.SyncInterpreterToGeneratedMove();

        Assert.That(machine.State.CurrentUserPosition[..2], Is.EqualTo(new[] { 7.0f, 5.0f }));
    }

    [Test]
    public void AGeneratedMoveAfterOneThatStoppedStartsWhereItStopped()
    {
        // The dive stopped at Z -0.5 with X skewed by tanXZ; the lift starts from there with the skew
        // taken back off, and the carriages where the travel left them
        Machine machine = DuplicatingMachine();
        machine.Model.Move.Compensation.Skew.TanXZ = 0.1f;
        RawMove travel = machine.Interpreter.StartGeneratedMove();
        travel.Coords[0] = 20.0f;
        Queue(machine, travel);
        machine.Builder.SetAxisPosition(0, 19.95f);
        machine.Builder.SetAxisPosition(2, -0.5f);

        machine.Interpreter.SyncGeneratedMoveToMachine();
        RawMove lift = machine.Interpreter.StartGeneratedMove();

        Assert.That(lift.Coords[..4], Is.EqualTo(new[] { 20.0f, 0.0f, -0.5f, 20.0f }).Within(1e-4f));
    }
}
