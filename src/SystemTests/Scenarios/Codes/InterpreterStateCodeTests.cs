using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// G-code interpreter state and positioning codes, asserted against the object model fields
/// RepRapFirmware writes for them: the per-channel inputs[] state (distance unit, relativity
/// flags, inverse time mode, raw feed rate), the axis positions, restore points, speed and
/// extrusion factors, and babystepping
/// </summary>
[TestFixture]
public class InterpreterStateCodeTests : SystemTests.Host.BenchFixture
{
    private static string SocketPath() => Path.Combine(Path.GetTempPath(), $"dsf-fake-{Guid.NewGuid():N}.sock");

    /// <summary>
    /// X, Y and Z axes on board 1, marked homed by G92 so relative babystepping is allowed
    /// </summary>
    private const string XyzConfig = """
        M953
        M569 P1.0 S1
        M569 P1.1 S1
        M569 P1.2 S1
        M584 X1.0 Y1.1 Z1.2
        M92 X80 Y80 Z400
        M906 X800 Y800 Z800
        M201 X500 Y500 Z100
        M203 X6000 Y6000 Z600
        M566 X900 Y900 Z60
        M208 X0:200 Y0:200 Z0:200
        M564 H0 S0
        G92 X0 Y0 Z0
        """;

    /// <summary>
    /// The index of the HTTP channel in inputs[], found by name because ExecuteCodeAsync runs
    /// codes on that channel and the channel order is not part of the contract under test
    /// </summary>
    private static Task<int> HttpInputIndexAsync(DcsTestHost host) => ChannelInputIndexAsync(host, CodeChannel.HTTP);

    /// <summary>The index of a named channel in inputs[], so its interpreter state can be read</summary>
    private static async Task<int> ChannelInputIndexAsync(DcsTestHost host, CodeChannel channel)
    {
        using (await host.Model.AccessReadOnlyAsync(CancellationToken.None))
        {
            for (int i = 0; i < host.Model.Inputs.Count; i++)
            {
                if (host.Model.Inputs[i]?.Name == channel)
                {
                    return i;
                }
            }
        }
        throw new AssertionException($"inputs[] does not contain the {channel} channel");
    }

    /// <summary>Poll the live machine position of an axis until it reaches the expected value</summary>
    private static Task WaitForMachinePositionAsync(JobBench bench, int axis, double expected)
        => bench.CanMaster.WaitUntilAsync(
            () => bench.Host.Model.Move.Axes.Count > axis
                  && bench.Host.Model.Move.Axes[axis].MachinePosition is float position
                  && Math.Abs(position - expected) < 1e-3,
            what: $"move.axes[{axis}].machinePosition reaching {expected}");

    /// <summary>
    /// G0 and G1 update the channel's feed rate from F and move the user position to the
    /// commanded coordinates
    /// </summary>
    /// <remarks>
    /// RRF GCodes.cpp LoadFeedrateFromGCode keeps the raw F value in the GCodeBuffer
    /// ("we now keep the raw value ... as we don't know whether to convert from inches yet"),
    /// GCodeMachineState.cpp initialises feedRate to DefaultFeedRate (3000 mm/min,
    /// Configuration.h), and Move.cpp reports userPosition from GetUserCoordinate and
    /// machinePosition from LiveMachineCoordinate. The raw feedRate semantics are also
    /// documented in rrf-differences.md section 5
    /// </remarks>
    [Test]
    public async Task G0AndG1SetFeedRateAndUserPosition()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        int http = await HttpInputIndexAsync(bench.Host);

        InputChannel? httpChannel = await bench.Host.ReadModelAsync(model => model.Inputs[http]);
        Axis axis0 = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);
        Axis axis1 = await bench.Host.ReadModelAsync(model => model.Move.Axes[1]);

        Assert.That(httpChannel?.FeedRate, Is.EqualTo(3000.0).Within(1e-3),
                    "the initial inputs[].feedRate is DefaultFeedRate, 3000 mm/min (RRF GCodeMachineState.cpp, Configuration.h)");

        await bench.Host.ExecuteCodeAsync("G1 X10 Y5 F1200");
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.Multiple(() =>
        {
            Assert.That(httpChannel?.FeedRate, Is.EqualTo(1200.0).Within(1e-3),
                        "G1 F sets inputs[].feedRate to the raw F value (RRF GCodes.cpp LoadFeedrateFromGCode)");
            Assert.That(axis0.UserPosition, Is.EqualTo(10.0).Within(1e-3),
                        "G1 X10 sets move.axes[0].userPosition (RRF Move.cpp userPosition)");
            Assert.That(axis1.UserPosition, Is.EqualTo(5.0).Within(1e-3),
                        "G1 Y5 sets move.axes[1].userPosition (RRF Move.cpp userPosition)");
        });
        await WaitForMachinePositionAsync(bench, 0, 10.0);

        await bench.Host.ExecuteCodeAsync("G0 X20 F2400");
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.Multiple(() =>
        {
            Assert.That(httpChannel?.FeedRate, Is.EqualTo(2400.0).Within(1e-3),
                        "G0 F sets inputs[].feedRate to the raw F value (RRF GCodes.cpp LoadFeedrateFromGCode)");
            Assert.That(axis0.UserPosition, Is.EqualTo(20.0).Within(1e-3),
                        "G0 X20 sets move.axes[0].userPosition (RRF Move.cpp userPosition)");
        });
    }

    /// <summary>
    /// G20 selects inches and G21 selects millimetres: the distance unit is per channel, the
    /// coordinates of a move are interpreted in the selected unit, the object model positions
    /// stay in millimetres, and the stored feed rate stays raw
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 20/21 calls gb.UseInches, GCodeBuffer.cpp GetDistanceUnits reports
    /// "in"/"mm" for inputs[].distanceUnit, and Move.cpp reports userPosition in millimetres.
    /// The raw feedRate is per rrf-differences.md section 5
    /// </remarks>
    [Test]
    public async Task G20AndG21SwitchDistanceUnit()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        int http = await HttpInputIndexAsync(bench.Host);

        InputChannel? httpChannel = await bench.Host.ReadModelAsync(model => model.Inputs[http]);
        Axis axis = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);

        Assert.That(httpChannel?.DistanceUnit, Is.EqualTo(DistanceUnit.MM),
                    "the initial inputs[].distanceUnit is mm (RRF GCodeBuffer.cpp GetDistanceUnits)");

        await bench.Host.ExecuteCodeAsync("G91");
        await bench.Host.ExecuteCodeAsync("G20");
        Assert.That(httpChannel?.DistanceUnit, Is.EqualTo(DistanceUnit.Inch),
                    "G20 sets inputs[].distanceUnit to in (RRF GCodes2.cpp case 20, GCodeBuffer.cpp GetDistanceUnits)");

        await bench.Host.ExecuteCodeAsync("G1 X1 F60");
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.Multiple(() =>
        {
            Assert.That(axis.UserPosition, Is.EqualTo(25.4).Within(1e-3),
                        "a relative G1 X1 under G20 moves one inch and move.axes[0].userPosition reports millimetres (RRF Move.cpp userPosition)");
            Assert.That(httpChannel?.FeedRate, Is.EqualTo(60.0).Within(1e-3),
                        "inputs[].feedRate keeps the raw F value under G20 (RRF GCodes.cpp LoadFeedrateFromGCode, rrf-differences.md section 5)");
        });

        await bench.Host.ExecuteCodeAsync("G21");
        Assert.That(httpChannel?.DistanceUnit, Is.EqualTo(DistanceUnit.MM),
                    "G21 sets inputs[].distanceUnit back to mm (RRF GCodes2.cpp case 21)");

        await bench.Host.ExecuteCodeAsync("G1 X1 F600");
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].UserPosition), Is.EqualTo(26.4).Within(1e-3),
                    "a relative G1 X1 under G21 moves one millimetre (RRF Move.cpp userPosition)");
    }

    /// <summary>
    /// G90 and G91 toggle the channel's axesRelative flag and change how the coordinates of the
    /// next moves are interpreted
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 90/91 writes gb.LatestMachineState().axesRelative, reported by
    /// GCodeBuffer.cpp inputs[].axesRelative</remarks>
    [Test]
    public async Task G90AndG91ToggleAxesRelative()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        int http = await HttpInputIndexAsync(bench.Host);
        InputChannel? httpChannel = await bench.Host.ReadModelAsync(model => model.Inputs[http]);
        Axis axis = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);

        Assert.That(httpChannel?.AxesRelative, Is.False,
                    "the initial inputs[].axesRelative is false (RRF GCodeMachineState.cpp constructor)");

        await bench.Host.ExecuteCodeAsync("G91");
        Assert.That(httpChannel?.AxesRelative, Is.True,
                    "G91 sets inputs[].axesRelative (RRF GCodes2.cpp case 91)");

        await bench.Host.ExecuteCodeAsync("G1 X5 F6000");
        await bench.Host.ExecuteCodeAsync("G1 X5 F6000");
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].UserPosition), Is.EqualTo(10.0).Within(1e-3),
                    "two relative G1 X5 moves accumulate to move.axes[0].userPosition 10 (RRF Move.cpp userPosition)");

        await bench.Host.ExecuteCodeAsync("G90");
        Assert.That(httpChannel?.AxesRelative, Is.False,
                    "G90 clears inputs[].axesRelative (RRF GCodes2.cpp case 90)");

        await bench.Host.ExecuteCodeAsync("G1 X3 F6000");
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.That(axis.UserPosition, Is.EqualTo(3.0).Within(1e-3),
                    "an absolute G1 X3 moves to move.axes[0].userPosition 3 (RRF Move.cpp userPosition)");
    }

    /// <summary>
    /// G92 sets the current position without motion: the user position and, with no offsets in
    /// play, the machine position of the named axis take the given value and other axes keep theirs
    /// </summary>
    /// <remarks>RRF GCodes3.cpp SetPositions writes currentUserPosition for the axes mentioned;
    /// Move.cpp reports userPosition and machinePosition</remarks>
    [Test]
    public async Task G92SetsUserAndMachinePosition()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Axis axis0 = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);
        Axis axis1 = await bench.Host.ReadModelAsync(model => model.Move.Axes[1]);

        await bench.Host.ExecuteCodeAsync("G1 X10 F6000");
        await bench.Host.ExecuteCodeAsync("M400");
        await bench.Host.ExecuteCodeAsync("G92 X50");

        Assert.Multiple(() =>
        {
            Assert.That(axis0.UserPosition, Is.EqualTo(50.0).Within(1e-3),
                        "G92 X50 sets move.axes[0].userPosition (RRF GCodes3.cpp SetPositions)");
            Assert.That(axis1.UserPosition, Is.EqualTo(0.0).Within(1e-3),
                        "G92 X50 leaves move.axes[1].userPosition alone (RRF GCodes3.cpp SetPositions)");
        });
        await WaitForMachinePositionAsync(bench, 0, 50.0);
        Assert.That(axis0.MachinePosition, Is.EqualTo(50.0).Within(1e-3),
                    "with no offsets G92 X50 sets move.axes[0].machinePosition too (RRF Move.cpp machinePosition)");
    }

    /// <summary>
    /// G93 selects inverse time mode, in which every move must carry its own F and the stored
    /// feed rate is left alone; G94 returns to normal feed rate mode
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 93/94 writes gb.LatestMachineState().inverseTimeMode, reported by
    /// GCodeBuffer.cpp inputs[].inverseTimeMode. GCodes.cpp LoadFeedrateFromGCode throws
    /// "Feed rate must be specified with every move when using inverse time mode" without F and
    /// does not update the machine state feed rate from an inverse time F
    /// </remarks>
    [Test]
    public async Task G93AndG94ToggleInverseTimeMode()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        int http = await HttpInputIndexAsync(bench.Host);
        InputChannel? httpChannel = await bench.Host.ReadModelAsync(model => model.Inputs[http]);
        Axis axis0 = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);

        Assert.That(httpChannel?.InverseTimeMode, Is.False,
                    "the initial inputs[].inverseTimeMode is false (RRF GCodeMachineState.cpp constructor)");

        await bench.Host.ExecuteCodeAsync("G93");
        Assert.That(httpChannel?.InverseTimeMode, Is.True,
                    "G93 sets inputs[].inverseTimeMode (RRF GCodes2.cpp case 93)");

        string reply = await bench.Host.ExecuteCodeAsync("G1 X10");
        Assert.That(reply, Does.Contain("Feed rate must be specified with every move when using inverse time mode"),
                    "an inverse time move without F is refused (RRF GCodes.cpp LoadFeedrateFromGCode)");

        // F60 asks for the whole move in one second; the stored feed rate must not pick it up
        await bench.Host.ExecuteCodeAsync("G1 X10 F60");
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.Multiple(() =>
        {
            Assert.That(axis0.UserPosition, Is.EqualTo(10.0).Within(1e-3),
                        "the inverse time G1 X10 reaches move.axes[0].userPosition 10 (RRF Move.cpp userPosition)");
            Assert.That(httpChannel?.FeedRate, Is.EqualTo(3000.0).Within(1e-3),
                        "an inverse time F is a duration and leaves inputs[].feedRate alone (RRF GCodes.cpp LoadFeedrateFromGCode)");
        });

        await bench.Host.ExecuteCodeAsync("G94");
        Assert.That(httpChannel?.InverseTimeMode, Is.False,
                    "G94 clears inputs[].inverseTimeMode (RRF GCodes2.cpp case 94)");

        await bench.Host.ExecuteCodeAsync("G1 X0 F6000");
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.That(httpChannel?.FeedRate, Is.EqualTo(6000.0).Within(1e-3),
                    "after G94 a G1 F updates inputs[].feedRate again (RRF GCodes.cpp LoadFeedrateFromGCode)");
    }

    /// <summary>
    /// G60 saves the current user position, feed rate and tool into the restore point slot S
    /// names, slot 0 by default
    /// </summary>
    /// <remarks>
    /// RRF GCodes3.cpp SavePosition (G60) calls MovementState::SavePosition (RawMove.cpp), which
    /// copies currentUserPosition into moveCoords, the raw machine state feed rate into
    /// originalFeedRate and the current tool number; RestorePoint.cpp reports them as coords,
    /// feedRate and toolNumber
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task G60SavesRestorePoint()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("G1 X12 Y7 F1800");
        await bench.Host.ExecuteCodeAsync("M400");

        string reply = await bench.Host.ExecuteCodeAsync("G60 S2");
        Assert.That(reply.Trim(), Is.Empty, "G60 S2 executes without error (RRF GCodes3.cpp SavePosition)");
#pragma warning disable CS0618
        RestorePoint restorePoint = await bench.Host.ReadModelAsync(model => model.State.RestorePoints[2]);
#pragma warning restore
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(restorePoint!.Coords[0], Is.EqualTo(12.0).Within(1e-3),
                        "G60 S2 saves X into state.restorePoints[2].coords[0] (RRF RawMove.cpp MovementState::SavePosition)");
            Assert.That(restorePoint!.Coords[1], Is.EqualTo(7.0).Within(1e-3),
                        "G60 S2 saves Y into state.restorePoints[2].coords[1] (RRF RawMove.cpp MovementState::SavePosition)");
            Assert.That(restorePoint!.FeedRate, Is.EqualTo(1800.0).Within(1e-3),
                        "G60 S2 saves the raw feed rate into state.restorePoints[2].feedRate (RRF RestorePoint.cpp originalFeedRate)");
            Assert.That(restorePoint!.ToolNumber, Is.EqualTo(-1.0),
                        "with no tool selected state.restorePoints[2].toolNumber is -1 (RRF RawMove.cpp MovementState::SavePosition)");
        });

        await bench.Host.ExecuteCodeAsync("G60");
#pragma warning disable CS0618
        Assert.That(await bench.Host.ReadModelAsync(model => model.State.RestorePoints[0].Coords[0]), Is.EqualTo(12.0).Within(1e-3),
                    "G60 without S saves into state.restorePoints[0] (RRF GCodes3.cpp SavePosition, default S0)");
#pragma warning restore
    }

    /// <summary>Extrusion starts out absolute, as after M82</summary>
    /// <remarks>RRF GCodeMachineState.cpp initialises drivesRelative to false, reported by
    /// GCodeBuffer.cpp inputs[].drivesRelative</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task DrivesRelativeDefaultsToAbsolute()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        int http = await HttpInputIndexAsync(bench.Host);

        Assert.That(await bench.Host.ReadModelAsync(model => model.Inputs[http]?.DrivesRelative), Is.False,
                    "the initial inputs[].drivesRelative is false (RRF GCodeMachineState.cpp constructor)");
    }

    /// <summary>
    /// M82 and M83 toggle the channel's drivesRelative flag, and the flag decides whether an E
    /// coordinate is a target or a distance, observed in the extruder steps the boards are sent
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 82/83 writes gb.LatestMachineState().drivesRelative, reported
    /// by GCodeBuffer.cpp inputs[].drivesRelative</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M82AndM83ToggleDrivesRelative()
    {
        // Extrusion needs a selected tool; tool 0 drives extruder 0 and has no heater
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: "M563 P0 D0\n");
        int http = await HttpInputIndexAsync(bench.Host);
        InputChannel? httpChannel = await bench.Host.ReadModelAsync(model => model.Inputs[http]);
        await bench.Host.ExecuteCodeAsync("T0");

        await bench.Host.ExecuteCodeAsync("M83");
        Assert.That(httpChannel?.DrivesRelative, Is.True,
                    "M83 sets inputs[].drivesRelative (RRF GCodes2.cpp case 83)");

        // Two relative E1 moves extrude 2 mm at 420 steps/mm
        await bench.Host.ExecuteCodeAsync("G1 E1 F300");
        await bench.Host.ExecuteCodeAsync("G1 E1 F300");
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.That(bench.CanMaster.ScheduledSteps(2), Is.EqualTo(840),
                    "M83 makes G1 E1 a 1 mm extrusion each time (RRF GCodes2.cpp case 83)");

        await bench.Host.ExecuteCodeAsync("M82");
        Assert.That(httpChannel?.DrivesRelative, Is.False,
                    "M82 clears inputs[].drivesRelative (RRF GCodes2.cpp case 82)");

        // In absolute mode E2 is a target: one 2 mm extrusion, then a repeat that adds nothing
        await bench.Host.ExecuteCodeAsync("G92 E0");
        await bench.Host.ExecuteCodeAsync("G1 E2 F300");
        await bench.Host.ExecuteCodeAsync("G1 E2 F300");
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.That(bench.CanMaster.ScheduledSteps(2), Is.EqualTo(840 + 840),
                    "M82 makes G1 E2 an absolute target, so repeating it extrudes nothing more (RRF GCodes2.cpp case 82)");
    }

    /// <summary>M114 reports the axis user positions consistently with the object model</summary>
    /// <remarks>RRF GCodes.cpp HandleM114 prints each visible axis as "L:%.3f " from
    /// GetUserCoordinate, then the machine coordinates after "Machine"</remarks>
    [Test]
    public async Task M114ReportsObjectModelPosition()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("G1 X10 Y5 F6000");
        await bench.Host.ExecuteCodeAsync("M400");

        double x = await bench.Host.ReadModelAsync(model => model.Move.Axes[0].UserPosition!.Value);
        double y = await bench.Host.ReadModelAsync(model => model.Move.Axes[1].UserPosition!.Value);
        string reply = await bench.Host.ExecuteCodeAsync("M114");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Contain($"X:{x.ToString("F3", CultureInfo.InvariantCulture)}"),
                        "M114 reports X consistent with move.axes[0].userPosition (RRF GCodes.cpp HandleM114)");
            Assert.That(reply, Does.Contain($"Y:{y.ToString("F3", CultureInfo.InvariantCulture)}"),
                        "M114 reports Y consistent with move.axes[1].userPosition (RRF GCodes.cpp HandleM114)");
            Assert.That(reply, Does.Contain("Count"), "M114 includes the motor position section (RRF GCodes.cpp HandleM114)");
            Assert.That(reply, Does.Contain("Machine"), "M114 includes the machine coordinate section (RRF GCodes.cpp HandleM114)");
        });
    }

    /// <summary>
    /// M120 pushes the channel's interpreter state and M121 restores it: the relativity flags,
    /// the distance unit, the inverse time flag and the feed rate all return to their pushed
    /// values
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 120/121 calls Push/Pop, and GCodeMachineState.cpp CopyStateFrom lists
    /// what the pop restores: selectedPlane, drivesRelative, axesRelative, feedRate,
    /// volumetricExtrusion, usingInches and inverseTimeMode
    /// </remarks>
    [Test]
    public async Task M120AndM121PushAndPopInputState()
    {
        await using JobBench bench = await JobControlBench.StartAsync();
        int http = await HttpInputIndexAsync(bench.Host);

        // A known baseline, then push it
        await bench.Host.ExecuteCodeAsync("G90");
        await bench.Host.ExecuteCodeAsync("M82");
        await bench.Host.ExecuteCodeAsync("G21");
        await bench.Host.ExecuteCodeAsync("G94");
        await bench.Host.ExecuteCodeAsync("G1 F3000");
        await bench.Host.ExecuteCodeAsync("M120");

        // Change every piece of state the pop must restore
        await bench.Host.ExecuteCodeAsync("G91");
        await bench.Host.ExecuteCodeAsync("M83");
        await bench.Host.ExecuteCodeAsync("G20");
        await bench.Host.ExecuteCodeAsync("G1 F600");
        await bench.Host.ExecuteCodeAsync("G93");
        InputChannel? input = await bench.Host.ReadModelAsync(model => model.Inputs[http]);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(input!.AxesRelative, Is.True,
                        "G91 after M120 changes inputs[].axesRelative");
            Assert.That(input!.DrivesRelative, Is.True,
                        "M83 after M120 changes inputs[].drivesRelative");
            Assert.That(input!.DistanceUnit, Is.EqualTo(DistanceUnit.Inch),
                        "G20 after M120 changes inputs[].distanceUnit");
            Assert.That(input!.FeedRate, Is.EqualTo(600.0).Within(1e-3),
                        "G1 F600 after M120 changes inputs[].feedRate");
            Assert.That(input!.InverseTimeMode, Is.True,
                        "G93 after M120 changes inputs[].inverseTimeMode");
        });

        await bench.Host.ExecuteCodeAsync("M121");
        input = await bench.Host.ReadModelAsync(model => model.Inputs[http]);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(input!.AxesRelative, Is.False,
                        "M121 restores inputs[].axesRelative (RRF GCodeMachineState.cpp CopyStateFrom)");
            Assert.That(input!.DrivesRelative, Is.False,
                        "M121 restores inputs[].drivesRelative (RRF GCodeMachineState.cpp CopyStateFrom)");
            Assert.That(input!.DistanceUnit, Is.EqualTo(DistanceUnit.MM),
                        "M121 restores inputs[].distanceUnit (RRF GCodeMachineState.cpp CopyStateFrom)");
            Assert.That(input!.FeedRate, Is.EqualTo(3000.0).Within(1e-3),
                        "M121 restores inputs[].feedRate (RRF GCodeMachineState.cpp CopyStateFrom)");
            Assert.That(input!.InverseTimeMode, Is.False,
                        "M121 restores inputs[].inverseTimeMode (RRF GCodeMachineState.cpp CopyStateFrom)");
        });
    }

    /// <summary>
    /// M220 sets the speed factor, reported as a fraction in move.speedFactor and as a percentage
    /// in the report reply
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 220 stores S * 0.01 in ms.speedFactor, Move.cpp reports it as
    /// move.speedFactor, and the report reply prints "Speed factor: %.1f%%"</remarks>
    [Test]
    public async Task M220SetsSpeedFactor()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.SpeedFactor), Is.EqualTo(1.0).Within(1e-3),
                    "the initial move.speedFactor is 1.0 (RRF Move.cpp speedFactor)");

        await bench.Host.ExecuteCodeAsync("M220 S150");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.SpeedFactor), Is.EqualTo(1.5).Within(1e-3),
                    "M220 S150 sets move.speedFactor to 1.5 (RRF GCodes2.cpp case 220)");

        string reply = await bench.Host.ExecuteCodeAsync("M220");
        Assert.That(reply, Does.Contain("Speed factor: 150.0%"),
                    "M220 reports the percentage consistent with move.speedFactor (RRF GCodes2.cpp case 220)");

        await bench.Host.ExecuteCodeAsync("M220 S100");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.SpeedFactor), Is.EqualTo(1.0).Within(1e-3),
                    "M220 S100 restores move.speedFactor to 1.0 (RRF GCodes2.cpp case 220)");
    }

    /// <summary>
    /// M221 sets an extruder's extrusion factor, reported as a fraction in
    /// move.extruders[].factor; without D it needs a current tool
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 221 stores S * 0.01 via ChangeExtrusionFactor, Move.cpp
    /// reports it as move.extruders[].factor, the D report prints
    /// "Extrusion factor for extruder %u: %.1f%%", and with no D and no tool it answers
    /// "No tool selected"</remarks>
    [Test]
    public async Task M221SetsExtrusionFactor()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Factor), Is.EqualTo(1.0).Within(1e-3),
                    "the initial move.extruders[0].factor is 1.0 (RRF Move.cpp extruders factor)");

        await bench.Host.ExecuteCodeAsync("M221 S50 D0");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Factor), Is.EqualTo(0.5).Within(1e-3),
                    "M221 S50 D0 sets move.extruders[0].factor to 0.5 (RRF GCodes2.cpp case 221)");

        string report = await bench.Host.ExecuteCodeAsync("M221 D0");
        Assert.That(report, Does.Contain("Extrusion factor for extruder 0: 50.0%"),
                    "M221 D0 reports the percentage consistent with move.extruders[0].factor (RRF GCodes2.cpp case 221)");

        string noTool = await bench.Host.ExecuteCodeAsync("M221 S120");
        Assert.That(noTool, Does.Contain("No tool selected"),
                    "M221 without D needs a current tool (RRF GCodes2.cpp case 221)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Factor), Is.EqualTo(0.5).Within(1e-3),
                    "a refused M221 leaves move.extruders[0].factor alone (RRF GCodes2.cpp case 221)");

        Assert.That(await bench.Host.ExecuteCodeAsync("M221 S120 D9"), Does.Contain("Invalid extruder number"),
                    "a D past the last extruder is refused (RRF GetLimitedUIValue against numExtruders)");
    }

    /// <summary>
    /// M221 with no D addresses every extruder the current tool collects, and reports them together
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 221: without D the factor goes through the current tool's
    /// IterateExtruders, and the report is "Extrusion factor(s) for current tool:" followed by one
    /// percentage per extruder. Only a machine with no tool at all answers "No tool selected"
    /// </remarks>
    [Test]
    public async Task M221WithoutDAddressesTheToolsExtruders()
    {
        await using JobBench bench = await JobControlBench.StartAsync(configExtra: "M563 P0 D0");

        await bench.Host.ExecuteCodeAsync("T0");
        await bench.Host.ExecuteCodeAsync("M221 S75");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Factor), Is.EqualTo(0.75).Within(1e-3),
                    "M221 S75 with a tool selected reaches the tool's extruder (RRF Tool::IterateExtruders)");

        string report = await bench.Host.ExecuteCodeAsync("M221");
        Assert.That(report, Does.Contain("Extrusion factor(s) for current tool: 75.0%"),
                    "and a bare M221 reports them together (RRF GCodes2.cpp case 221)");
    }

    /// <summary>
    /// M290 babysteps an axis: S is a synonym for Z, amounts accumulate in relative mode, R0
    /// makes the value absolute, and the report prints the offsets
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 290 treats S as Z ("S is a synonym for Z"), adds relative amounts to
    /// currentBabyStepOffsets, replaces them when R0 is given, and reports
    /// "Baby stepping offsets (mm):" with each axis; Move.cpp reports GetTotalBabyStepOffset as
    /// move.axes[].babystep
    /// </remarks>
    [Test]
    public async Task M290BabystepsAxes()
    {
        using ScriptedCanMaster canMaster = new(SocketPath());
        canMaster.AckCanRequestsWithStandardReplies();
        await using DcsTestHost host = await DcsTestHost.StartAsync(canMaster,
            sd => sd.WriteSys("config.g", XyzConfig + DcsTestHost.ConfigDoneMarker));
        await host.WaitForConfigDoneAsync();

        Assert.That(await host.ReadModelAsync(model => model.Move.Axes[2].Babystep), Is.EqualTo(0.0).Within(1e-4),
                    "the initial move.axes[2].babystep is 0 (RRF Move.cpp babystep)");

        await host.ExecuteCodeAsync("M290 S0.05");
        Assert.That(await host.ReadModelAsync(model => model.Move.Axes[2].Babystep), Is.EqualTo(0.05).Within(1e-4),
                    "M290 S babysteps Z, S being a synonym for Z (RRF GCodes2.cpp case 290)");

        await host.ExecuteCodeAsync("M290 S0.02");
        Assert.That(await host.ReadModelAsync(model => model.Move.Axes[2].Babystep), Is.EqualTo(0.07).Within(1e-4),
                    "relative M290 amounts accumulate in move.axes[2].babystep (RRF GCodes2.cpp case 290)");

        await host.ExecuteCodeAsync("M290 X0.1");
        Assert.That(await host.ReadModelAsync(model => model.Move.Axes[0].Babystep), Is.EqualTo(0.1).Within(1e-4),
                    "M290 X babysteps the X axis into move.axes[0].babystep (RRF GCodes2.cpp case 290)");

        await host.ExecuteCodeAsync("M290 R0 S0.01");
        Assert.That(await host.ReadModelAsync(model => model.Move.Axes[2].Babystep), Is.EqualTo(0.01).Within(1e-4),
                    "M290 R0 sets move.axes[2].babystep absolutely (RRF GCodes2.cpp case 290)");

        string report = await host.ExecuteCodeAsync("M290");
        Assert.Multiple(() =>
        {
            Assert.That(report, Does.Contain("Baby stepping offsets (mm)"),
                        "M290 without parameters reports the offsets (RRF GCodes2.cpp case 290)");
            Assert.That(report, Does.Contain("Z:0.010"),
                        "the M290 report is consistent with move.axes[2].babystep (RRF GCodes2.cpp case 290)");
        });
    }

    /// <summary>
    /// M400 waits for the queued moves to finish, so afterwards the live machine position has
    /// reached the commanded target
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 400 locks the movement system and waits for standstill; the
    /// live position is Move.cpp machinePosition</remarks>
    [Test]
    public async Task M400WaitsForStandstill()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        await bench.Host.ExecuteCodeAsync("G91");
        await bench.Host.ExecuteCodeAsync("G1 X5 F6000");
        string reply = await bench.Host.ExecuteCodeAsync("M400");
        Assert.That(reply.Trim(), Is.Empty, "M400 executes without error (RRF GCodes2.cpp case 400)");

        Assert.That(bench.CanMaster.ScheduledSteps(0), Is.EqualTo(400),
                    "after M400 the whole 5 mm move has been sent, 5 mm at 80 steps/mm (RRF GCodes2.cpp case 400)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].UserPosition), Is.EqualTo(5.0).Within(1e-3),
                    "after M400 move.axes[0].userPosition is at the target (RRF Move.cpp userPosition)");
        await WaitForMachinePositionAsync(bench, 0, 5.0);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].MachinePosition), Is.EqualTo(5.0).Within(1e-3),
                    "after M400 move.axes[0].machinePosition settles at the target (RRF Move.cpp machinePosition)");
    }

    /// <summary>
    /// The startup files - config.g, config-override.g (M501) and dsf-config.g - and any macro they
    /// call retain their interpreter state when they finish, rather than having it restored the way
    /// an ordinary macro does, so an M83 or G91 in config.g is the machine's setting afterwards
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>CheckFinishedRunningConfigFile</c> copies config.g's state up rather than
    /// discarding it with the frame, "so that M83 etc. in nested files don't get forgotten"
    /// (GCodes.cpp). DuetControlServer keeps the same rule for all three startup files and the macros
    /// they call: the restore <see cref="AMacroDoesNotLeakItsInterpreterStateToTheCaller"/> checks is
    /// skipped while a startup file is running. State is retained on the channel the file ran on -
    /// the Trigger channel for config.g at boot, and the invoking channel for M501 and dsf-config.g
    /// </remarks>
    [Test]
    public async Task ConfigRetainsInterpreterState()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            // config.g sets the feed rate (before G93, so F is stored not read as a duration), the
            // relativity and the time mode itself, and a sub-macro sets the rest; all must remain
            configExtra: """
                G1 F1234
                G91
                G93
                M98 P"0:/macros/config-sub.g"
                """,
            prepareSd: sd =>
            {
                sd.WriteMacro("config-sub.g", "M83\nG20\n");
                sd.WriteMacro("override-sub.g", "G91\nG20\n");
                sd.WriteMacro("dsfconfig-sub.g", "M83\nG20\n");
                sd.WriteSys("config-override.g", "G1 F2345\nM98 P\"0:/macros/override-sub.g\"\n");
                sd.WriteSys("dsf-config.g", "G91\nM98 P\"0:/macros/dsfconfig-sub.g\"\n");
            });

        // config.g ran at boot on the Trigger channel; its state and its sub-macro's state stand
        int trigger = await ChannelInputIndexAsync(bench.Host, CodeChannel.Trigger);
        InputChannel triggerChannel = (await bench.Host.ReadModelAsync(model => model.Inputs[trigger]))!;
        Assert.Multiple(() =>
        {
            Assert.That(triggerChannel.AxesRelative, Is.True, "config.g's G91 is retained");
            Assert.That(triggerChannel.InverseTimeMode, Is.True, "config.g's G93 is retained");
            Assert.That(triggerChannel.FeedRate, Is.EqualTo(1234.0).Within(1e-3), "config.g's feed rate is retained");
            Assert.That(triggerChannel.DrivesRelative, Is.True, "the M83 in config.g's sub-macro is retained");
            Assert.That(triggerChannel.DistanceUnit, Is.EqualTo(DistanceUnit.Inch), "the G20 in config.g's sub-macro is retained");
        });

        int http = await HttpInputIndexAsync(bench.Host);
        InputChannel httpChannel = (await bench.Host.ReadModelAsync(model => model.Inputs[http]))!;

        // config-override.g via M501, on the HTTP channel: its feed rate and its sub-macro's state
        // stand, unlike an ordinary M98 macro
        await bench.Host.ExecuteCodeAsync("M501");
        Assert.Multiple(() =>
        {
            Assert.That(httpChannel.FeedRate, Is.EqualTo(2345.0).Within(1e-3), "config-override.g's feed rate is retained");
            Assert.That(httpChannel.AxesRelative, Is.True, "the G91 in config-override.g's sub-macro is retained");
            Assert.That(httpChannel.DistanceUnit, Is.EqualTo(DistanceUnit.Inch), "the G20 in config-override.g's sub-macro is retained");
        });

        // Put the channel back to the defaults so what dsf-config.g leaves is unmistakably its own
        await bench.Host.ExecuteCodeAsync("G90");
        await bench.Host.ExecuteCodeAsync("M82");
        await bench.Host.ExecuteCodeAsync("G21");

        // dsf-config.g run as a macro on the HTTP channel; its filename is what marks it, so it and
        // its sub-macro retain their state the same way
        await bench.Host.ExecuteCodeAsync("M98 P\"dsf-config.g\"");
        Assert.Multiple(() =>
        {
            Assert.That(httpChannel.AxesRelative, Is.True, "dsf-config.g's G91 is retained");
            Assert.That(httpChannel.DrivesRelative, Is.True, "the M83 in dsf-config.g's sub-macro is retained");
            Assert.That(httpChannel.DistanceUnit, Is.EqualTo(DistanceUnit.Inch), "the G20 in dsf-config.g's sub-macro is retained");
        });
    }

    /// <summary>
    /// A macro carries its own copy of the caller's interpreter state, so a modal code inside it does
    /// not leak out: when the macro ends the caller's feed rate, relativity, units and time mode are
    /// as they were before the M98
    /// </summary>
    /// <remarks>
    /// <para>
    /// RepRapFirmware pushes a <c>GCodeMachineState</c> for every macro frame
    /// (<c>GCodeMachineState.cpp</c> copy constructor) and pops it on return, so the modal state a
    /// macro changes is discarded with the frame. The full set of modal fields the copy constructor
    /// stacks is: <c>axesRelative</c> (G90/G91), <c>drivesRelative</c> (M82/M83), <c>usingInches</c>
    /// (G20/G21), <c>inverseTimeMode</c> (G93/G94), <c>feedRate</c> (F), <c>selectedPlane</c>
    /// (G17/G18/G19), <c>volumetricExtrusion</c> (M200, reset to off in the child) and
    /// <c>compatibility</c> (M555).
    /// </para>
    /// <para>
    /// This exercises the five DuetControlServer implements a code for today, all published on
    /// <c>inputs[]</c>. The remaining three - <c>selectedPlane</c>, <c>volumetric</c> and
    /// <c>compatibility</c> - have their <c>inputs[]</c> fields but no code sets them yet, so they
    /// are added here when G17/G18/G19, M200 and M555 land.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AMacroDoesNotLeakItsInterpreterStateToTheCaller()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            // G94 before the F so the feed rate registers rather than being read as an inverse time,
            // then each modal set to the opposite of what the caller holds
            sd.WriteMacro("modal-state.g", """
                G90
                M82
                G21
                G94
                G1 F6000
                """));
        int http = await HttpInputIndexAsync(bench.Host);
        InputChannel? httpChannel = await bench.Host.ReadModelAsync(model => model.Inputs[http]);

        // The caller's state: the feed rate first, while still in the default non-inverse-time mode,
        // so F is stored rather than taken as a duration
        await bench.Host.ExecuteCodeAsync("G1 F1000");
        await bench.Host.ExecuteCodeAsync("G91");
        await bench.Host.ExecuteCodeAsync("M83");
        await bench.Host.ExecuteCodeAsync("G20");
        await bench.Host.ExecuteCodeAsync("G93");
        double callerFeedRate = httpChannel!.FeedRate;

        // The macro changes every one of them; RRF discards those changes with the macro frame
        await bench.Host.ExecuteCodeAsync("M98 P\"0:/macros/modal-state.g\"");

        Assert.Multiple(() =>
        {
            Assert.That(httpChannel.AxesRelative, Is.True,
                        "G90 in the macro does not clear the caller's G91 (RRF GCodeMachineState axesRelative)");
            Assert.That(httpChannel.DrivesRelative, Is.True,
                        "M82 in the macro does not clear the caller's M83 (RRF GCodeMachineState drivesRelative)");
            Assert.That(httpChannel.DistanceUnit, Is.EqualTo(DistanceUnit.Inch),
                        "G21 in the macro does not undo the caller's G20 (RRF GCodeMachineState usingInches)");
            Assert.That(httpChannel.InverseTimeMode, Is.True,
                        "G94 in the macro does not clear the caller's G93 (RRF GCodeMachineState inverseTimeMode)");
            Assert.That(httpChannel.FeedRate, Is.EqualTo(callerFeedRate).Within(1e-3),
                        "the F in the macro does not change the caller's feed rate (RRF GCodeMachineState feedRate)");
        });
    }
}
