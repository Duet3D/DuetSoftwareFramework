using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Motion.InputChannels;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M82 and M83, absolute and relative extrusion</summary>
[TestFixture]
[GCode("M82", "M83")]
public class M82M83Tests : BenchFixture
{
    /// <summary>Extrusion starts out absolute, as after M82</summary>
    /// <remarks>RRF GCodeMachineState.cpp initialises drivesRelative to false, reported by
    /// GCodeBuffer.cpp inputs[].drivesRelative</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task DrivesRelativeDefaultsToAbsolute()
    {
        await using Bench bench = await Bench.StartAsync();
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
        await using Bench bench = await Bench.StartAsync(Machines.Xye, "M563 P0 D0");
        int http = await HttpInputIndexAsync(bench.Host);
        InputChannel? httpChannel = await bench.Host.ReadModelAsync(model => model.Inputs[http]);
        await bench.Host.ExecuteCodeAsync("T0");

        await bench.Host.ExecuteCodeAsync("M83");
        Assert.That(httpChannel?.DrivesRelative, Is.True,
                    "M83 sets inputs[].drivesRelative (RRF GCodes2.cpp case 83)");

        // Two relative E1 moves extrude 2 mm
        await bench.Host.ExecuteCodeAsync("G1 E1 F300");
        await bench.Host.ExecuteCodeAsync("G1 E1 F300");
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.That(bench.CanMaster.ScheduledSteps(Machines.EDriver), Is.EqualTo(2 * Machines.EStepsPerMm),
                    "M83 makes G1 E1 a 1 mm extrusion each time (RRF GCodes2.cpp case 83)");

        await bench.Host.ExecuteCodeAsync("M82");
        Assert.That(httpChannel?.DrivesRelative, Is.False,
                    "M82 clears inputs[].drivesRelative (RRF GCodes2.cpp case 82)");

        // In absolute mode E2 is a target: one 2 mm extrusion, then a repeat that adds nothing
        await bench.Host.ExecuteCodeAsync("G92 E0");
        await bench.Host.ExecuteCodeAsync("G1 E2 F300");
        await bench.Host.ExecuteCodeAsync("G1 E2 F300");
        await bench.Host.ExecuteCodeAsync("M400");
        Assert.That(bench.CanMaster.ScheduledSteps(Machines.EDriver), Is.EqualTo(4 * Machines.EStepsPerMm),
                    "M82 makes G1 E2 an absolute target, so repeating it extrudes nothing more (RRF GCodes2.cpp case 82)");
    }
}
