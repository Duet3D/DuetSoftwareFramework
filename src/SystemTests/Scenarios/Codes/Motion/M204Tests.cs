using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M204, the printing and travel accelerations</summary>
[TestFixture]
[GCode("M204")]
public class M204Tests : BenchFixture
{
    /// <summary>
    /// M204 sets the per-move acceleration limits: P for printing moves, T for travel moves, and S
    /// sets both for Marlin compatibility
    /// </summary>
    /// <remarks>
    /// RRF GCodes5.cpp GCodes::ConfigureAccelerations; move.printingAcceleration and
    /// move.travelAcceleration are reported in mm/s^2 (Move.cpp table)
    /// </remarks>
    [Test]
    public async Task M204SetsPrintingAndTravelAcceleration()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        // Default accelerations
#pragma warning disable CS0618
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.PrintingAcceleration), Is.EqualTo(Move.DefaultPrintingAcceleration).Within(1e-2));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.TravelAcceleration), Is.EqualTo(Move.DefaultTravelAcceleration).Within(1e-2));
        });

        await bench.Host.ExecuteCodeAsync("M204 P900 T1600");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.PrintingAcceleration).Result, Is.EqualTo(900.0).Within(1e-2),
                        "M204 P sets move.printingAcceleration (mm/s^2, RRF GCodes5.cpp ConfigureAccelerations)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.TravelAcceleration).Result, Is.EqualTo(1600.0).Within(1e-2),
                        "M204 T sets move.travelAcceleration (mm/s^2, RRF GCodes5.cpp ConfigureAccelerations)");
        });

        await bench.Host.ExecuteCodeAsync("M204 S700");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.PrintingAcceleration).Result, Is.EqualTo(700.0).Within(1e-2),
                        "M204 S sets move.printingAcceleration too (RRF GCodes5.cpp ConfigureAccelerations)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.TravelAcceleration).Result, Is.EqualTo(700.0).Within(1e-2),
                        "M204 S sets move.travelAcceleration too (RRF GCodes5.cpp ConfigureAccelerations)");
        });
#pragma warning restore
    }
}
