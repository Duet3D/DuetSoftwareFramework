using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M572, pressure advance</summary>
[TestFixture]
[GCode("M572")]
public class M572Tests : BenchFixture
{
    /// <summary>
    /// M572 sets the pressure advance. A single S value is the classic coefficient; two values with
    /// L set the second coefficient and its transition
    /// </summary>
    /// <remarks>
    /// RRF Move2.cpp Move::ConfigurePressureAdvance and ExtruderShaper.cpp object model table:
    /// move.extruders[].pressureAdvance reports k0 in seconds, move.extruders[].pressAdv holds k0,
    /// k1 and d. With one S value RRF copies k0 into k1. The pressAdv.k1 and .d fields being kept on
    /// this side is rrf-differences.md section 3
    /// </remarks>
    [Test]
    public async Task M572SetsPressureAdvance()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        Extruder extruder = await bench.Host.ReadModelAsync(model => model.Move.Extruders[0]);

        await bench.Host.ExecuteCodeAsync("M572 D0 S0.05");
        Assert.Multiple(() =>
        {
#pragma warning disable CS0618
            Assert.That(extruder.PressureAdvance, Is.EqualTo(0.05).Within(1e-4),
                        "M572 S sets move.extruders[0].pressureAdvance in seconds (RRF ExtruderShaper GetK0Seconds)");
#pragma warning restore
            Assert.That(extruder.PressAdv.K0, Is.EqualTo(0.05).Within(1e-4),
                        "M572 S sets move.extruders[0].pressAdv.k0 in seconds (RRF ExtruderShaper GetK0Seconds)");
            Assert.That(extruder.PressAdv.K1, Is.EqualTo(0.05).Within(1e-4),
                        "a single M572 S value copies k0 into move.extruders[0].pressAdv.k1 (RRF Move2.cpp ConfigurePressureAdvance)");
        });

        await bench.Host.ExecuteCodeAsync("M572 D0 S0.06:0.08 L2");
        Assert.Multiple(() =>
        {
#pragma warning disable CS0618
            Assert.That(extruder.PressureAdvance, Is.EqualTo(0.06).Within(1e-4),
                        "M572 S sets move.extruders[0].pressureAdvance in seconds (RRF ExtruderShaper GetK0Seconds)");
#pragma warning restore
            Assert.That(extruder.PressAdv.K0, Is.EqualTo(0.06).Within(1e-4),
                        "M572 S0.06:0.08 sets move.extruders[0].pressAdv.k0 (RRF Move2.cpp ConfigurePressureAdvance)");
            Assert.That(extruder.PressAdv.K1, Is.EqualTo(0.08).Within(1e-4),
                        "M572 S0.06:0.08 sets move.extruders[0].pressAdv.k1 (RRF Move2.cpp ConfigurePressureAdvance)");
            Assert.That(extruder.PressAdv.D, Is.EqualTo(2.0).Within(1e-3),
                        "M572 L sets move.extruders[0].pressAdv.d (RRF Move2.cpp ConfigurePressureAdvance dk)");
        });
    }
}
