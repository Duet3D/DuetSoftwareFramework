using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Compensation;

/// <summary>M376, the taper height</summary>
[TestFixture]
[GCode("M376")]
public class M376Tests : BenchFixture
{
    /// <summary>
    /// M376 H above 1 enables tapered bed compensation and publishes the height; H at or below 1
    /// disables the taper, which reports as a null fade height
    /// </summary>
    /// <remarks>
    /// Move::SetTaperHeight in Movement/Move3.cpp: useTaper = (h &gt; 1.0). The
    /// move.compensation.fadeHeight entry in Movement/Move.cpp reports taperHeight only while
    /// useTaper is set and NaN otherwise, which serialises as null. Report wording from
    /// GCodes2.cpp case 376
    /// </remarks>
    [Test]
    public async Task M376SetsTaperHeight()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M376 H10");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Compensation.FadeHeight), Is.EqualTo(10).Within(1e-3),
                    "M376 H sets move.compensation.fadeHeight (RRF Move::SetTaperHeight)");
        Assert.That((await bench.Host.ExecuteCodeAsync("M376")).Trim(), Is.EqualTo("Bed compensation taper height is 10.0mm"),
                    "bare M376 reports the taper height in RRF's format (GCodes2.cpp case 376)");

        await bench.Host.ExecuteCodeAsync("M376 H0");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Compensation.FadeHeight), Is.Null,
                    "M376 H0 disables the taper: move.compensation.fadeHeight reports null (RRF useTaper = h > 1.0)");
        Assert.That((await bench.Host.ExecuteCodeAsync("M376")).Trim(), Is.EqualTo("Bed compensation is not tapered"),
                    "bare M376 with the taper disabled reports so (GCodes2.cpp case 376)");
    }
}
