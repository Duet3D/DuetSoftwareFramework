using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Kinematics;

/// <summary>M665, the delta geometry</summary>
[TestFixture]
[GCode("M665")]
public class M665Tests : BenchFixture
{
    /// <summary>
    /// M665 with L switches to delta kinematics and writes the delta geometry, including the axis
    /// limits derived from B and H, and clears the homed flags
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 665 (mode switch when L is seen and the kinematics is not delta, then
    /// SetAllAxesNotHomed) and LinearDeltaKinematics::Configure in
    /// Movement/Kinematics/LinearDeltaKinematics.cpp: B sets X/Y minima to -printRadius and maxima
    /// to printRadius, H sets the Z maximum. OM fields from the LinearDeltaKinematics tables in
    /// the same file, name "delta" from LinearDeltaKinematics::GetName(true)
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M665ConfiguresDeltaGeometry()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        Assert.That(await host.ReadModelAsync(model => model.Move.Axes[2].Homed), Is.True, "the config's G92 homed Z");

        await host.ExecuteCodeAsync("M665 L300 R150 B120 H350 X0.5 Y-0.5 Z0.25");
        KinematicsName name = await host.ReadModelAsync(model => model.Move.Kinematics.Name);
        double towerCount = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).Towers.Count);
        Assert.Multiple(() =>
        {
            Assert.That(name, Is.EqualTo("delta"), "M665 L switches move.kinematics.name to delta (RRF GCodes2.cpp case 665)");
            Assert.That(towerCount, Is.EqualTo(3), "a single L value gives three towers (RRF numTowers)");
        });

        double deltaRadius = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).DeltaRadius);
        double printRadius = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).PrintRadius);
        double homedHeight = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).HomedHeight);
        double diagonal0 = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).Towers[0].Diagonal);
        double diagonal2 = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).Towers[2].Diagonal);
        double angle0 = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).Towers[0].AngleCorrection);
        double angle1 = await host.ReadModelAsync(model => ((DeltaKinematics)model.Move.Kinematics).Towers[1].AngleCorrection);
        double xMin = await host.ReadModelAsync(model => model.Move.Axes[0].Min);
        double xMax = await host.ReadModelAsync(model => model.Move.Axes[0].Max);
        double zMax = await host.ReadModelAsync(model => model.Move.Axes[2].Max);
        bool zHomed = await host.ReadModelAsync(model => model.Move.Axes[2].Homed);
        Assert.Multiple(() =>
        {
            Assert.That(deltaRadius, Is.EqualTo(150).Within(1e-3), "M665 R sets move.kinematics.deltaRadius");
            Assert.That(printRadius, Is.EqualTo(120).Within(1e-3), "M665 B sets move.kinematics.printRadius");
            Assert.That(homedHeight, Is.EqualTo(350).Within(1e-3), "M665 H sets move.kinematics.homedHeight");
            Assert.That(diagonal0, Is.EqualTo(300).Within(1e-3), "M665 L sets move.kinematics.towers[0].diagonal");
            Assert.That(diagonal2, Is.EqualTo(300).Within(1e-3), "M665 L propagates to move.kinematics.towers[2].diagonal");
            Assert.That(angle0, Is.EqualTo(0.5).Within(1e-3), "M665 X sets move.kinematics.towers[0].angleCorrection");
            Assert.That(angle1, Is.EqualTo(-0.5).Within(1e-3), "M665 Y sets move.kinematics.towers[1].angleCorrection");
            Assert.That(xMin, Is.EqualTo(-120).Within(1e-3), "M665 B sets move.axes[0].min to -printRadius (RRF LinearDeltaKinematics::Configure case 665)");
            Assert.That(xMax, Is.EqualTo(120).Within(1e-3), "M665 B sets move.axes[0].max to printRadius");
            Assert.That(zMax, Is.EqualTo(350).Within(1e-3), "M665 H sets move.axes[2].max to the homed height");
            Assert.That(zHomed, Is.False, "M665 clears the homed flags (RRF SetAllAxesNotHomed)");
        });
    }
}
