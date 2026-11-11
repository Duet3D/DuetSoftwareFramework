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

/// <summary>M26, where a job starts</summary>
[TestFixture]
[GCode("M26")]
public class M26Tests : BenchFixture
{
    /// <summary>
    /// M26 says where in the selected file the job is to start, and the file position the job then
    /// reports never goes back before it
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 26 stores the offset in <c>ms.fileOffsetToPrint</c>, which case 24 uses when
    /// it starts the file (<c>StartPrinting(fromStart)</c>); the position the job reads from there on
    /// is what PrintMonitor.cpp reports as <c>filePosition</c>
    /// </remarks>
    [Test]
    public async Task M26SetsWhereTheJobStarts()
    {
        const string header = "G90\nG1 X20 Y20 F3000\n";
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteGCode("job.gcode", header + JobDriving.FillerMoves(pairs: 60, feed: 600))).StartAsync();

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/job.gcode\"");
        await bench.Host.ExecuteCodeAsync($"M26 S{header.Length}", timeoutMs: ReportTimeoutMs);
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Processing);

        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        Assert.That(await NumberAsync(bench.Host, model => model.Job.FilePosition), Is.GreaterThanOrEqualTo(header.Length),
                    "M26 S starts the job at that offset, so job.filePosition never precedes it (GCodes2.cpp case 26)");

        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
    }
}
