using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M208, the axis limits</summary>
[TestFixture]
[GCode("M208")]
public class M208Tests : BenchFixture
{
    /// <summary>
    /// M208 sets the axis limits: two values are min and max, a single value is the max, and a
    /// single value with S1 is the min
    /// </summary>
    /// <remarks>RRF Move2.cpp Move::ConfigureAxisLimits; move.axes[].min/.max in mm</remarks>
    [Test]
    public async Task M208SetsAxisLimits()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        Axis axis0 = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);
        Axis axis1 = await bench.Host.ReadModelAsync(model => model.Move.Axes[1]);

        await bench.Host.ExecuteCodeAsync("M208 X-5:250");
        Assert.Multiple(() =>
        {
            Assert.That(axis0.Min, Is.EqualTo(-5.0).Within(1e-2),
                        "M208 X-5:250 sets move.axes[0].min (RRF Move2.cpp ConfigureAxisLimits, two values)");
            Assert.That(axis0.Max, Is.EqualTo(250.0).Within(1e-2),
                        "M208 X-5:250 sets move.axes[0].max (RRF Move2.cpp ConfigureAxisLimits, two values)");
        });

        await bench.Host.ExecuteCodeAsync("M208 Y-2 S1");
        await bench.Host.ExecuteCodeAsync("M208 Y240");
        Assert.Multiple(() =>
        {
            Assert.That(axis1.Min, Is.EqualTo(-2.0).Within(1e-2),
                        "M208 Y-2 S1 sets move.axes[1].min (RRF Move2.cpp ConfigureAxisLimits, setMin)");
            Assert.That(axis1.Max, Is.EqualTo(240.0).Within(1e-2),
                        "M208 Y240 sets move.axes[1].max (RRF Move2.cpp ConfigureAxisLimits, single value)");
        });
    }
}
