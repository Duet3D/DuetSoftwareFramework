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

/// <summary>G20 and G21, the distance unit</summary>
[TestFixture]
[GCode("G20", "G21")]
public class G20G21Tests : BenchFixture
{
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
        await using Bench bench = await Bench.StartAsync(Machines.Xye);
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
}
