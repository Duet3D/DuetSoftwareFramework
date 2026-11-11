using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Compensation;

/// <summary>M425, backlash compensation</summary>
[TestFixture]
[GCode("M425")]
public class M425Tests : BenchFixture
{
    /// <summary>
    /// M425 axis letters set the per-axis backlash distance and S the correction distance factor
    /// </summary>
    /// <remarks>
    /// Move::ConfigureBacklashCompensation in Movement/Move2.cpp. OM fields:
    /// move.axes[].backlash (3 decimal places) and move.backlashFactor in Movement/Move.cpp
    /// </remarks>
    [Test]
    public async Task M425SetsBacklashCompensation()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M425 X0.5 Y0.25 S5");
        double backlashX = await host.ReadModelAsync(model => model.Move.Axes[0].Backlash);
        double backlashY = await host.ReadModelAsync(model => model.Move.Axes[1].Backlash);
        double backlashZ = await host.ReadModelAsync(model => model.Move.Axes[2].Backlash);
        double factor = await host.ReadModelAsync(model => model.Move.BacklashFactor);
        Assert.Multiple(() =>
        {
            Assert.That(backlashX, Is.EqualTo(0.5).Within(1e-4), "M425 X sets move.axes[0].backlash (RRF Move::ConfigureBacklashCompensation)");
            Assert.That(backlashY, Is.EqualTo(0.25).Within(1e-4), "M425 Y sets move.axes[1].backlash");
            Assert.That(backlashZ, Is.EqualTo(0), "an axis M425 does not name keeps zero backlash");
            Assert.That(factor, Is.EqualTo(5), "M425 S sets move.backlashFactor (RRF backlashCorrectionDistanceFactor)");
        });

        string report = await host.ExecuteCodeAsync("M425");
        Assert.That(report.Trim(), Is.EqualTo("Backlash correction (mm) X: 0.500 Y: 0.250 Z: 0.000, correction distance multiplier 5"),
                    "bare M425 reports the backlash in RRF's format (Move::ConfigureBacklashCompensation)");
    }
}
