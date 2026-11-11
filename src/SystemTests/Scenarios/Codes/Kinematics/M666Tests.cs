using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Kinematics;

/// <summary>M666, the delta endstop adjustments</summary>
[TestFixture]
[GCode("M666")]
public class M666Tests : BenchFixture
{
    /// <summary>
    /// M666 on a delta writes the per-tower endstop adjustments and the bed tilt as
    /// percentage/100, and a bare M666 reports them in RRF's wording
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 666 and LinearDeltaKinematics::Configure case 666 in
    /// Movement/Kinematics/LinearDeltaKinematics.cpp: X/Y/Z set endstopAdjustments, A and B store
    /// xTilt and yTilt divided by 100. OM fields from the LinearDeltaKinematics tables
    /// </remarks>
    [Test]
    public async Task M666SetsDeltaEndstopAdjustments()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M665 L300 R150 B120 H350");
        Assert.That(await host.ReadModelAsync(model => model.Move.Kinematics.Name), Is.EqualTo(KinematicsName.LinearDelta),
                    "M665 L switches to delta before M666 (RRF GCodes2.cpp case 665)");

        await host.ExecuteCodeAsync("M666 X0.15 Y-0.2 Z0.05 A1 B-0.5");
        double adjustment0 = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).Towers[0].EndstopAdjustment);
        double adjustment1 = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).Towers[1].EndstopAdjustment);
        double adjustment2 = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).Towers[2].EndstopAdjustment);
        double xTilt = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).XTilt);
        double yTilt = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).YTilt);
        Assert.Multiple(() =>
        {
            Assert.That(adjustment0, Is.EqualTo(0.15).Within(1e-3), "M666 X sets move.kinematics.towers[0].endstopAdjustment (RRF LinearDeltaKinematics::Configure case 666)");
            Assert.That(adjustment1, Is.EqualTo(-0.2).Within(1e-3), "M666 Y sets move.kinematics.towers[1].endstopAdjustment");
            Assert.That(adjustment2, Is.EqualTo(0.05).Within(1e-3), "M666 Z sets move.kinematics.towers[2].endstopAdjustment");
            Assert.That(xTilt, Is.EqualTo(0.01).Within(1e-4), "M666 A sets move.kinematics.xTilt as A/100");
            Assert.That(yTilt, Is.EqualTo(-0.005).Within(1e-4), "M666 B sets move.kinematics.yTilt as B/100");
        });

        string report = await host.ExecuteCodeAsync("M666");
        Assert.That(report.Trim(), Is.EqualTo("Endstop adjustments X0.15 Y-0.20 Z0.05, tilt X1.00% Y-0.50%"),
                    "bare M666 reports the adjustments in RRF's format (LinearDeltaKinematics::Configure)");
    }
}
