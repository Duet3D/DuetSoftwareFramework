using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Files.FileSetup;

namespace SystemTests.Scenarios.Codes.Files;

/// <summary>M36, file information</summary>
[TestFixture]
[GCode("M36")]
public class M36Tests : BenchFixture
{
    /// <summary>
    /// M36 answers with the same information for a named file and for the selected one
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 36 with fraction 0 calls <c>RepRap::GetFileInfoResponse</c> (RepRap.cpp),
    /// which answers <c>err</c> 0 with fileName, size, height, layerHeight, numLayers, printTime,
    /// filament and generatedBy for a valid file, and <c>err</c> 1 for one it cannot read. Without a
    /// filename it answers for the file being printed
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M36ReportsFileInformation()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("info.gcode", InfoFile))
                                             .StartAsync();
        long size = new FileInfo(Physical(bench, "gcodes", "info.gcode")).Length;

        JsonElement root = ParseJson("M36", await bench.Host.ExecuteCodeAsync("M36 \"0:/gcodes/info.gcode\""));
        string missing = await bench.Host.ExecuteCodeAsync("M36 \"0:/gcodes/nothing.gcode\"");
        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/info.gcode\"");
        string selected = await bench.Host.ExecuteCodeAsync("M36");

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("err").GetInt32(), Is.Zero,
                        "M36 answers err 0 for a file it could read (RepRap.cpp GetFileInfoResponse)");
            Assert.That(root.GetProperty("size").GetInt64(), Is.EqualTo(size),
                        "M36 reports the file size in bytes (RepRap.cpp GetFileInfoResponse)");
            Assert.That(root.GetProperty("height").GetDouble(), Is.EqualTo(2.0).Within(0.005),
                        "M36 reports the object height in mm (RepRap.cpp GetFileInfoResponse)");
            Assert.That(root.GetProperty("layerHeight").GetDouble(), Is.EqualTo(0.25).Within(0.005),
                        "M36 reports the layer height in mm (RepRap.cpp GetFileInfoResponse)");
            Assert.That(root.GetProperty("numLayers").GetInt32(), Is.EqualTo(8),
                        "M36 reports the number of layers (RepRap.cpp GetFileInfoResponse)");
            Assert.That(root.GetProperty("printTime").GetInt32(), Is.EqualTo(3600),
                        "M36 reports the slicer print time in seconds (RepRap.cpp GetFileInfoResponse)");
            Assert.That(root.GetProperty("generatedBy").GetString(), Is.EqualTo("TestSlicer 1.2"),
                        "M36 reports the slicer that generated the file (RepRap.cpp GetFileInfoResponse)");
            Assert.That(root.GetProperty("filament")[0].GetDouble(), Is.EqualTo(123.4).Within(0.05),
                        "M36 reports the filament needed per extruder in mm (RepRap.cpp GetFileInfoResponse)");
            Assert.That(ParseJson("M36 with no filename", selected).GetProperty("err").GetInt32(), Is.Zero,
                        "M36 with no filename answers for the selected file (GCodes2.cpp case 36)");
            Assert.That(ParseJson("M36 with no filename", selected).GetProperty("size").GetInt64(), Is.EqualTo(size),
                        "M36 with no filename reports the selected file's size");
            Assert.That(ParseJson("M36 of a missing file", missing).GetProperty("err").GetInt32(), Is.EqualTo(1),
                        "M36 answers err 1 for a file it cannot read (RepRap.cpp GetFileInfoResponse)");
        });
    }
}
