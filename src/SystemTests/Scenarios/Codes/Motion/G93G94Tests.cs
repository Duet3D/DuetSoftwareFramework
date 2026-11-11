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

/// <summary>G93 and G94, inverse time mode</summary>
[TestFixture]
[GCode("G93", "G94")]
public class G93G94Tests : BenchFixture
{
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
        await using Bench bench = await Bench.StartAsync(Machines.Xye);
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
}
