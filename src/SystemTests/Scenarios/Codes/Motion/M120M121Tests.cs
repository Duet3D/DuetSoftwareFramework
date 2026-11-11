using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M120 and M121, pushing and popping the channel's input state</summary>
[TestFixture]
[GCode("M120", "M121")]
public class M120M121Tests : BenchFixture
{
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
        await using Bench bench = await Bench.StartAsync(Machines.Xye);
        int http = await bench.Host.InputIndexAsync();

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
}
