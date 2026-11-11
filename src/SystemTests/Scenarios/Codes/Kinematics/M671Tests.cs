using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Kinematics;

/// <summary>M671, the leadscrew positions</summary>
[TestFixture]
[GCode("M671")]
public class M671Tests : BenchFixture
{
    /// <summary>
    /// M671 on cartesian kinematics stores the Z leadscrew coordinates and correction parameters
    /// under move.kinematics.tiltCorrection, and configuring positions zeroes the last corrections
    /// </summary>
    /// <remarks>
    /// ZLeadscrewKinematics::Configure in Movement/Kinematics/ZLeadscrewKinematics.cpp: X/Y arrays
    /// set the screw positions, S the maximum correction, P the screw pitch, F the correction
    /// factor. OM fields from the ZLeadscrewKinematics tables in the same file. M669 K0 first, so
    /// the test does not depend on the boot default kinematics
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M671SetsLeadscrewPositions()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K0");
        string configureReply = await host.ExecuteCodeAsync("M671 X10:190 Y5:195 S2.5 P0.7 F1.2");
        Assert.That(configureReply.Trim(), Is.Empty,
                    "cartesian kinematics accepts M671 silently (RRF ZLeadscrewKinematics::Configure)");

        double screwX0 = await host.ReadModelAsync(model => ((ZLeadscrewKinematics)model.Move.Kinematics).TiltCorrection.ScrewX[0]);
        double screwX1 = await host.ReadModelAsync(model => ((ZLeadscrewKinematics)model.Move.Kinematics).TiltCorrection.ScrewX[1]);
        double screwY0 = await host.ReadModelAsync(model => ((ZLeadscrewKinematics)model.Move.Kinematics).TiltCorrection.ScrewY[0]);
        double screwY1 = await host.ReadModelAsync(model => ((ZLeadscrewKinematics)model.Move.Kinematics).TiltCorrection.ScrewY[1]);
        double maxCorrection = await host.ReadModelAsync(model => ((ZLeadscrewKinematics)model.Move.Kinematics).TiltCorrection.MaxCorrection);
        double screwPitch = await host.ReadModelAsync(model => ((ZLeadscrewKinematics)model.Move.Kinematics).TiltCorrection.ScrewPitch);
        double correctionFactor = await host.ReadModelAsync(model => ((ZLeadscrewKinematics)model.Move.Kinematics).TiltCorrection.CorrectionFactor);
        Assert.Multiple(() =>
        {
            Assert.That(screwX0, Is.EqualTo(10).Within(1e-3), "M671 X sets move.kinematics.tiltCorrection.screwX[0] (RRF ZLeadscrewKinematics::Configure)");
            Assert.That(screwX1, Is.EqualTo(190).Within(1e-3), "M671 X sets move.kinematics.tiltCorrection.screwX[1]");
            Assert.That(screwY0, Is.EqualTo(5).Within(1e-3), "M671 Y sets move.kinematics.tiltCorrection.screwY[0]");
            Assert.That(screwY1, Is.EqualTo(195).Within(1e-3), "M671 Y sets move.kinematics.tiltCorrection.screwY[1]");
            Assert.That(maxCorrection, Is.EqualTo(2.5).Within(1e-3), "M671 S sets move.kinematics.tiltCorrection.maxCorrection");
            Assert.That(screwPitch, Is.EqualTo(0.7).Within(1e-3), "M671 P sets move.kinematics.tiltCorrection.screwPitch");
            Assert.That(correctionFactor, Is.EqualTo(1.2).Within(1e-3), "M671 F sets move.kinematics.tiltCorrection.correctionFactor");
        });

        // The count guards the element read: an element read on a shorter array cannot evaluate
        Assert.That(await host.ReadModelAsync(model => ((ZLeadscrewKinematics)model.Move.Kinematics).TiltCorrection.LastCorrections.Count), Is.EqualTo(2),
                    "M671 sizes move.kinematics.tiltCorrection.lastCorrections to the leadscrew count (RRF numLeadscrews)");
        Assert.That(await host.ReadModelAsync(model => ((ZLeadscrewKinematics)model.Move.Kinematics).TiltCorrection.LastCorrections[0]), Is.EqualTo(0),
                    "configuring positions zeroes move.kinematics.tiltCorrection.lastCorrections");

        string report = await host.ExecuteCodeAsync("M671");
        Assert.That(report.Trim(), Is.EqualTo("Z leadscrew coordinates (10.0,5.0) (190.0,195.0), factor 1.20, maximum correction 2.50mm, manual adjusting screw pitch 0.70mm"),
                    "bare M671 reports the leadscrews in RRF's format (ZLeadscrewKinematics::Configure)");
    }
}
