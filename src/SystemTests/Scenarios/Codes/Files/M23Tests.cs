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

/// <summary>M23, selecting a file to print</summary>
[TestFixture]
[GCode("M23")]
public class M23Tests : BenchFixture
{
    /// <summary>
    /// M23 selects a file and publishes what the file info parser found in it, without starting it
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 23 calls <c>PrintMonitor::StartingPrint</c>, which parses the file and
    /// publishes it through <c>PrintingFileInfoUpdated</c>. The fields are the PrintMonitor.cpp
    /// OBJECT_MODEL table of <c>job.file</c>: size, height and layerHeight to two decimals,
    /// numLayers, generatedBy, printTime, and the filament array. Only M24 starts the job
    /// (<c>StartedPrint</c>), so the machine is still idle afterwards
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M23SelectsTheFileAndPublishesItsInfo()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("info.gcode", InfoFile))
                                             .StartAsync();
        long size = new FileInfo(Physical(bench, "gcodes", "info.gcode")).Length;

        // The startup files have to be done with before "still idle" says anything about M23
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        string reply = await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/info.gcode\"");

        GCodeFileInfo jobFile = await bench.Host.ReadModelAsync(model => model.Job.File);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(reply, Does.Contain("info.gcode"),
                        "M23 names the file it selected (GCodes2.cpp case 23)");
            Assert.That(jobFile!.FileName, Is.EqualTo("0:/gcodes/info.gcode"),
                        "M23 sets job.file.fileName (PrintMonitor.cpp filenameBeingPrinted)");
            Assert.That(jobFile!.Size, Is.EqualTo(size),
                        "M23 sets job.file.size to the file length in bytes (PrintMonitor.cpp fileSize)");
            Assert.That(jobFile!.Height, Is.EqualTo(2.0).Within(0.005),
                        "M23 sets job.file.height in mm (PrintMonitor.cpp objectHeight)");
            Assert.That(jobFile!.LayerHeight, Is.EqualTo(0.25).Within(0.005),
                        "M23 sets job.file.layerHeight in mm (PrintMonitor.cpp layerHeight)");
            Assert.That(jobFile!.NumLayers, Is.EqualTo(8),
                        "M23 sets job.file.numLayers (PrintMonitor.cpp numLayers)");
            Assert.That(jobFile!.GeneratedBy, Is.EqualTo("TestSlicer 1.2"),
                        "M23 sets job.file.generatedBy (PrintMonitor.cpp generatedBy)");
            Assert.That(await NumberAsync(bench.Host, model => model.Job.File.PrintTime), Is.EqualTo(3600),
                        "M23 sets job.file.printTime in seconds (PrintMonitor.cpp printTime)");
            Assert.That(jobFile!.Filament[0], Is.EqualTo(123.4).Within(0.05),
                        "M23 sets job.file.filament[0] in mm (PrintMonitor.cpp filamentNeeded)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.Status), Is.EqualTo(MachineStatus.Idle),
                        "M23 selects without starting, so the machine stays idle (RepRap.cpp GetStatusIndex)");
        });
    }
}
