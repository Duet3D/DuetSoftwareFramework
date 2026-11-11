using System;
using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Compensation;

/// <summary>M374, saving the height map</summary>
[TestFixture]
[GCode("M374")]
public class M374Tests : BenchFixture
{
    /// <summary>
    /// M374 saves the loaded height map to a named file in sys, in RRF's v2 format. M374 after a
    /// real probing run needs probe hardware, so the map under test is loaded from a file first
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 374 calls GCodes::SaveHeightMap; reply wording from
    /// GCodes::TrySaveHeightMap in GCodes6.cpp ("height map saved to file ..."), file format from
    /// HeightMap::SaveToFile and GridDefinition::WriteHeadingAndParameters in
    /// Movement/BedProbing/Grid.cpp
    /// </remarks>
    [Test]
    public async Task M374SavesLoadedHeightMap()
    {
        await using Bench bench = await StartXyzAsync(withHeightMap: true);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M375");
        Assert.That(await host.ReadModelAsync(model => model.Move.Compensation.Type), Is.EqualTo(MoveCompensationType.Mesh),
                    "M375 loads the map M374 saves (RRF GCodes::LoadHeightMap)");

        string saveReply = await host.ExecuteCodeAsync("M374 P\"saved-map.csv\"");
        string savedPath = Path.Combine(host.Sd.Root, "sys", "saved-map.csv");
        Assert.That(File.Exists(savedPath), Is.True, "M374 P writes the map to 0:/sys (RRF GCodes::SaveHeightMap)");
        string[] savedLines = await File.ReadAllLinesAsync(savedPath);
        Assert.Multiple(() =>
        {
            Assert.That(savedLines[0], Does.StartWith("RepRapFirmware height map file v2"),
                        "a saved map starts with RRF's v2 header comment (HeightMap::SaveToFile)");
            Assert.That(savedLines[1], Is.EqualTo("axis0,axis1,min0,max0,min1,max1,radius,spacing0,spacing1,num0,num1"),
                        "a saved map carries RRF's v2 label line (GridDefinition::WriteHeadingAndParameters)");
            Assert.That(savedLines[2], Does.StartWith("X,Y,20.00,180.00,20.00,180.00,"),
                        "the saved grid parameters match the loaded grid");
            Assert.That(saveReply, Does.Contain("Height map saved to file"),
                        "M374 reports the save in RRF's wording (GCodes::TrySaveHeightMap)");
        });
    }
}
