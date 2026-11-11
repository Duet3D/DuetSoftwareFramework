using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Compensation;

/// <summary>M561, clearing bed compensation</summary>
[TestFixture]
[GCode("M561")]
public class M561Tests : BenchFixture
{
    /// <summary>
    /// M561 disables bed compensation: the loaded map is dropped, the compensation type reports
    /// none and the file is cleared
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 561 calls GCodes::ClearBedMapping in GCodes6.cpp; usingMesh is cleared so
    /// move.compensation.type reports "none" and .file null (OM tables in Movement/Move.cpp)
    /// </remarks>
    [Test]
    public async Task M561ClearsBedCompensation()
    {
        await using Bench bench = await StartXyzAsync(withHeightMap: true);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M375");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.Type), Is.EqualTo(MoveCompensationType.Mesh),
                    "M375 loads the map M561 clears (RRF GCodes::LoadHeightMap)");

        string clearReply = await host.ExecuteCodeAsync("M561");
        Assert.That(clearReply.Trim(), Is.Empty, "M561 answers silently (RRF GCodes2.cpp case 561)");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.Type), Is.EqualTo(MoveCompensationType.None),
                    "M561 sets move.compensation.type back to none (RRF GCodes::ClearBedMapping)");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.File), Is.Null,
                    "M561 clears move.compensation.file");
    }
}
