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

/// <summary>M24 and M27, running a job and reporting its position</summary>
[TestFixture]
[GCode("M24", "M27")]
public class M24M27Tests : BenchFixture
{
    /// <summary>
    /// M24 starts the selected file and the job counters follow it: M27 reports the same file
    /// position the object model holds, against the same file length, and stopping the job moves the
    /// file name into job.lastFileName
    /// </summary>
    /// <remarks>
    /// PrintMonitor.cpp OBJECT_MODEL table: <c>filePosition</c> is
    /// <c>GCodes::GetPrintingFilePosition</c>, which is what GCodes2.cpp case 27 prints alongside
    /// the length of the file being printed; <c>duration</c> and <c>lastFileName</c> are reported
    /// while printing and once it has stopped respectively. Case 27 answers "Not SD printing." while
    /// no file is being printed, which a file that is only selected is not
    /// (PrintMonitor.cpp <c>StartedPrint</c> is what sets <c>isPrinting</c>)
    /// </remarks>
    [Test]
    public async Task M24AndM27TrackTheFilePosition()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteGCode("job.gcode", "G90\nG1 X50 Y50 F3000\n" + JobDriving.FillerMoves(pairs: 60, feed: 600))).StartAsync();
        long size = new FileInfo(Physical(bench, "gcodes", "job.gcode")).Length;

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/job.gcode\"");
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Processing);
        Assert.That(await bench.Host.ReadModelAsync(model => model.State.Status), Is.EqualTo(MachineStatus.Processing),
                    "M24 puts the machine into processing (RepRap.cpp GetStatusIndex)");

        // The pause holds the file position still, so the model describes one moment
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        long modelPosition = 0;
        await Assert.MultipleAsync(async () =>
        {
            modelPosition = (long)await NumberAsync(bench.Host, model => model.Job.FilePosition);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.File.Size), Is.EqualTo(size),
                        "job.file.size is the length of the file being printed (PrintMonitor.cpp fileSize)");
            Assert.That(modelPosition, Is.GreaterThan(0).And.LessThanOrEqualTo(size),
                        "job.filePosition lies within the file being printed (PrintMonitor.cpp filePosition)");
            Assert.That(await NumberAsync(bench.Host, model => model.Job.Duration), Is.GreaterThanOrEqualTo(0),
                        "job.duration counts the job in seconds while it is printing (PrintMonitor.cpp duration)");
            Assert.IsNull(await bench.Host.ReadModelAsync(model => model.Job.LastFileName),
                        "job.lastFileName stays null while the file is printing (PrintMonitor.cpp lastFileName)");
        });

        (long position, long length) = ParsePrintStatus(await bench.Host.ExecuteCodeAsync("M27", timeoutMs: ReportTimeoutMs));
        Assert.Multiple(() =>
        {
            Assert.That(length, Is.EqualTo(size),
                        "M27 reports the length of the file being printed (GCodes2.cpp case 27)");
            Assert.That(position, Is.EqualTo(modelPosition),
                        "M27 reports job.filePosition (GCodes2.cpp case 27, GCodes::GetPrintingFilePosition)");
        });

        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await SettledValueAsync(bench.Host, model => model.Job.LastFileName, "0:/gcodes/job.gcode"), Is.EqualTo("0:/gcodes/job.gcode"),
                        "the stopped job's file becomes job.lastFileName (PrintMonitor.cpp lastFileName)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileSimulated), Is.False,
                        "a printed job is not a simulated one (job.lastFileSimulated)");
            Assert.IsNull(await bench.Host.ReadModelAsync(model => model.Job.Duration),
                        "job.duration is reported only while printing (PrintMonitor.cpp duration)");
            Assert.That(await SettledNumberAsync(bench.Host, model => model.Job.LastDuration), Is.GreaterThanOrEqualTo(0),
                        "job.lastDuration holds what the finished job took, in seconds (PrintMonitor.cpp lastDuration)");
            Assert.That(await bench.Host.ExecuteCodeAsync("M27", timeoutMs: ReportTimeoutMs),
                        Does.Contain("Not SD printing."),
                        "M27 reports no print once the job has stopped (GCodes2.cpp case 27)");
        });
    }
}
