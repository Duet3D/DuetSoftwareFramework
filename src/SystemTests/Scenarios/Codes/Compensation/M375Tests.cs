using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Compensation;

/// <summary>M375, loading a height map</summary>
[TestFixture]
[GCode("M375")]
public class M375Tests : BenchFixture
{
    /// <summary>
    /// M375 loads sys/heightmap.csv and enables mesh compensation, exactly as G29 S1 does
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 375 calls GCodes::LoadHeightMap in GCodes6.cpp; OM effects as for G29 S1
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M375LoadsHeightMap()
    {
        await using Bench bench = await StartXyzAsync(withHeightMap: true);
        DcsTestHost host = bench.Host;

        string loadReply = await host.ExecuteCodeAsync("M375");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.Type), Is.EqualTo(MoveCompensationType.Mesh),
                    "M375 sets move.compensation.type to mesh (RRF Move::GetCompensationTypeString)");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.File), Does.EndWith("heightmap.csv"),
                    "M375 sets move.compensation.file to the loaded map");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.LiveGrid!.Spacings[0]), Is.EqualTo(40).Within(1e-3),
                    "move.compensation.liveGrid.spacings[0] comes from the file's grid");
        Assert.That(loadReply.Trim(), Is.Empty, "M375 loads the map silently with no probe configured (RRF GCodes::LoadHeightMap)");
    }
}
