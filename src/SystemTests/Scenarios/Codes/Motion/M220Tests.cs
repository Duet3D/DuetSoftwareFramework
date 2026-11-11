using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M220, the speed factor</summary>
[TestFixture]
[GCode("M220")]
public class M220Tests : BenchFixture
{
    /// <summary>
    /// M220 sets the speed factor, reported as a fraction in move.speedFactor and as a percentage
    /// in the report reply
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 220 stores S * 0.01 in ms.speedFactor, Move.cpp reports it as
    /// move.speedFactor, and the report reply prints "Speed factor: %.1f%%"</remarks>
    [Test]
    public async Task M220SetsSpeedFactor()
    {
        await using Bench bench = await Bench.StartAsync();

        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.SpeedFactor), Is.EqualTo(1.0).Within(1e-3),
                    "the initial move.speedFactor is 1.0 (RRF Move.cpp speedFactor)");

        await bench.Host.ExecuteCodeAsync("M220 S150");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.SpeedFactor), Is.EqualTo(1.5).Within(1e-3),
                    "M220 S150 sets move.speedFactor to 1.5 (RRF GCodes2.cpp case 220)");

        string reply = await bench.Host.ExecuteCodeAsync("M220");
        Assert.That(reply, Does.Contain("Speed factor: 150.0%"),
                    "M220 reports the percentage consistent with move.speedFactor (RRF GCodes2.cpp case 220)");

        await bench.Host.ExecuteCodeAsync("M220 S100");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.SpeedFactor), Is.EqualTo(1.0).Within(1e-3),
                    "M220 S100 restores move.speedFactor to 1.0 (RRF GCodes2.cpp case 220)");
    }
}
