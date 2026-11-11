using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>G90 and G91, absolute and relative axis coordinates</summary>
[TestFixture]
[GCode("G90", "G91")]
public class G90G91Tests : BenchFixture
{
    /// <summary>
    /// G90 and G91 toggle the channel's axesRelative flag and change how the coordinates of the
    /// next moves are interpreted
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 90/91 writes gb.LatestMachineState().axesRelative, reported by
    /// GCodeBuffer.cpp inputs[].axesRelative</remarks>
    [Test]
    public async Task G90AndG91ToggleAxesRelative()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);
        int http = await bench.Host.InputIndexAsync();
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
}
