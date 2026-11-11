using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Compensation;

/// <summary>M557, the probe grid</summary>
[TestFixture]
[GCode("M557")]
public class M557Tests : BenchFixture
{
    /// <summary>
    /// M557 with axis ranges and S spacing defines the default probe grid, reported under
    /// move.compensation.probeGrid; the P point-count form derives the spacing from the range
    /// </summary>
    /// <remarks>
    /// GCodes::DefineGrid in GCodes6.cpp writes the default grid; with P the spacing becomes
    /// (max - min)/(P - 1) * 0.9999. OM fields from the GridDefinition tables in
    /// Movement/BedProbing/Grid.cpp (mins, maxs, spacings at 1 decimal place, radius -1 for a
    /// rectangular grid)
    /// </remarks>
    [Test]
    public async Task M557DefinesProbeGrid()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M557 X20:180 Y20:180 S40");
        char axis0 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Axes[0]);
        char axis1 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Axes[1]);
        double min0 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Mins[0]);
        double max0 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Maxs[0]);
        double min1 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Mins[1]);
        double max1 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Maxs[1]);
        double spacing0 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Spacings[0]);
        double spacing1 = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Spacings[1]);
        double radius = await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Radius);
        Assert.Multiple(() =>
        {
            Assert.That(axis0, Is.EqualTo('X'), "M557 sets move.compensation.probeGrid.axes[0] (RRF GCodes::DefineGrid)");
            Assert.That(axis1, Is.EqualTo('Y'), "M557 sets move.compensation.probeGrid.axes[1]");
            Assert.That(min0, Is.EqualTo(20), "M557 X range sets move.compensation.probeGrid.mins[0]");
            Assert.That(max0, Is.EqualTo(180), "M557 X range sets move.compensation.probeGrid.maxs[0]");
            Assert.That(min1, Is.EqualTo(20), "M557 Y range sets move.compensation.probeGrid.mins[1]");
            Assert.That(max1, Is.EqualTo(180), "M557 Y range sets move.compensation.probeGrid.maxs[1]");
            Assert.That(spacing0, Is.EqualTo(40), "M557 S sets move.compensation.probeGrid.spacings[0]");
            Assert.That(spacing1, Is.EqualTo(40), "M557 S sets move.compensation.probeGrid.spacings[1]");
            Assert.That(radius, Is.EqualTo(-1), "a rectangular M557 grid keeps move.compensation.probeGrid.radius = -1");
        });

        string report = await host.ExecuteCodeAsync("M557");
        Assert.That(report.Trim(), Is.EqualTo("Grid: X20.0:180.0, Y20.0:180.0, radius -1.0, X spacing 40.0, Y spacing 40.0, 25 points"),
                    "bare M557 reports the grid in RRF's format (GridDefinition::PrintParameters)");

        await host.ExecuteCodeAsync("M557 X0:100 Y0:100 P5");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.ProbeGrid.Spacings[0]), Is.EqualTo(100.0 / 4.0 * 0.9999).Within(1e-3),
                    "M557 P derives move.compensation.probeGrid.spacings[0] as range/(P-1) * 0.9999 (RRF DefineGrid)");
    }
}
