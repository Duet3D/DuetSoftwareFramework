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

/// <summary>G0 and G1: the channel's feed rate and user position</summary>
[TestFixture]
[GCode("G0", "G1")]
public class G0G1Tests : BenchFixture
{
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
        await using Bench bench = await Bench.StartAsync(Machines.Xye);
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
}
