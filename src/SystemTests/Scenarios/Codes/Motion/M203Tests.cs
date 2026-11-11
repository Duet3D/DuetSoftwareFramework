using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M203, the speed limits</summary>
[TestFixture]
[GCode("M203")]
public class M203Tests : BenchFixture
{
    /// <summary>
    /// M203 sets the minimum and maximum speeds. The parameter is mm/min by default and mm/s with S1, and the
    /// object model reports mm/min either way
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 203 (GetSpeedFromMm(usingMmPerSec)); move.axes[].speed is reported
    /// through InverseConvertSpeedToMmPerMin (Move.cpp axes table)
    /// </remarks>
    [Test]
    public async Task M203SetSpeeds()
    {
        // TODO test setting multiple extruders
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        await bench.Host.ExecuteCodeAsync("M203 X9000 E4200");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes[0].Speed).Result, Is.EqualTo(9000.0).Within(1e-2),
                        "M203 X sets move.axes[0].speed (mm/min, RRF Move.cpp maxFeedrate)");
            Assert.That(bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Speed).Result, Is.EqualTo(4200.0).Within(1e-2),
                        "M203 E sets move.extruders[0].speed (mm/min, RRF Move.cpp maxFeedrate)");
        });

        await bench.Host.ExecuteCodeAsync("M203 Y100 S1");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Speed), Is.EqualTo(6000.0).Within(1e-2),
                    "M203 Y100 S1 is 100 mm/s, reported as 6000 mm/min in move.axes[1].speed (RRF GCodes2.cpp case 203 usingMmPerSec)");

        await bench.Host.ExecuteCodeAsync("M203 I10 S1");
        Assert.That(bench.Host.ReadModelAsync(model => model.Move.MinSpeed).Result, Is.EqualTo(10).Within(1e-2),
                    "M203 I10 S1 sets the minimum movement speed to 10 mm/s");


        {
            double minSpeed = 300;
            await bench.Host.ExecuteCodeAsync($"M203 X900 Y200 E200 I{minSpeed}");
            Assert.Multiple(() =>
            {
                Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes[0].Speed).Result, Is.EqualTo(900.0).Within(1e-2),
                            "M203 X sets move.axes[0].speed (mm/min, RRF Move.cpp maxFeedrate)");
                Assert.That(bench.Host.ReadModelAsync(model => model.Move.Axes[1].Speed).Result, Is.EqualTo(minSpeed).Within(1e-2),
                            "Y max speed is clamped by the min speed");
                Assert.That(bench.Host.ReadModelAsync(model => model.Move.Extruders[0].Speed).Result, Is.EqualTo(minSpeed).Within(1e-2),
                            "E max speed is clamped by the min speed");
            });
        }
    }
}
