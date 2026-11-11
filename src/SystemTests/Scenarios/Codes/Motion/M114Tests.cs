using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M114, reporting the position</summary>
[TestFixture]
[GCode("M114")]
public class M114Tests : BenchFixture
{
    /// <summary>M114 reports the axis user positions consistently with the object model</summary>
    /// <remarks>RRF GCodes.cpp HandleM114 prints each visible axis as "L:%.3f " from
    /// GetUserCoordinate, then the machine coordinates after "Machine"</remarks>
    [Test]
    public async Task M114ReportsObjectModelPosition()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

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
}
