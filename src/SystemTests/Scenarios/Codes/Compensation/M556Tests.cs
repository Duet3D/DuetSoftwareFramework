using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Compensation;

/// <summary>M556, skew compensation</summary>
[TestFixture]
[GCode("M556")]
public class M556Tests : BenchFixture
{
    /// <summary>
    /// M556 S with axis factors writes the skew tangents as value/S, and P selects whether XY or
    /// YX is compensated. A bare M556 reports the tangents in RRF's exact wording
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 556: X sets tangents[0] (tanXY), Y sets tangents[1] (tanYZ), Z sets
    /// tangents[2] (tanXZ), each divided by S (Move::SetAxisCompensation, tan accessors in
    /// Move.h); P &lt;= 0 sets compensateXY true (Move::SetXYCompensation). OM fields from the
    /// move.compensation.skew table in Movement/Move.cpp
    /// </remarks>
    [Test]
    public async Task M556SetsSkewCompensation()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M556 S100 X2 Y1 Z0.5");
        double tanXY = await host.ReadModelAsync(model => model.Move.Compensation.Skew.TanXY);
        double tanYZ = await host.ReadModelAsync(model => model.Move.Compensation.Skew.TanYZ);
        double tanXZ = await host.ReadModelAsync(model => model.Move.Compensation.Skew.TanXZ);
        bool compensateXY = await host.ReadModelAsync(model => model.Move.Compensation.Skew.CompensateXY);
        Assert.Multiple(() =>
        {
            Assert.That(tanXY, Is.EqualTo(0.02).Within(1e-5), "M556 X sets move.compensation.skew.tanXY = X/S (RRF GCodes2.cpp case 556, Move::SetAxisCompensation)");
            Assert.That(tanYZ, Is.EqualTo(0.01).Within(1e-5), "M556 Y sets move.compensation.skew.tanYZ = Y/S (RRF tangents[1] is tanYZ)");
            Assert.That(tanXZ, Is.EqualTo(0.005).Within(1e-5), "M556 Z sets move.compensation.skew.tanXZ = Z/S (RRF tangents[2] is tanXZ)");
            Assert.That(compensateXY, Is.True, "move.compensation.skew.compensateXY defaults to true and M556 without P leaves it");
        });

        await host.ExecuteCodeAsync("M556 P1");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.Skew.CompensateXY), Is.False,
                    "M556 P1 clears move.compensation.skew.compensateXY (RRF SetXYCompensation(P <= 0))");

        string report = await host.ExecuteCodeAsync("M556");
        Assert.That(report.Trim(), Is.EqualTo("Axis compensations - YX: 0.02000, YZ: 0.01000, ZX: 0.00500"),
                    "bare M556 reports the tangents in RRF's format (GCodes2.cpp case 556)");
    }
}
