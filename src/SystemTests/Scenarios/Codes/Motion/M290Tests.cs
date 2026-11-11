using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M290, babystepping</summary>
[TestFixture]
[GCode("M290")]
public class M290Tests : BenchFixture
{
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
        // Homed, as babystepping an axis needs
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, Machines.Xyz.Homed);
        DcsTestHost host = bench.Host;

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
}
