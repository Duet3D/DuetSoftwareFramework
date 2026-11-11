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

/// <summary>M226, pausing from within the file</summary>
[TestFixture]
[GCode("M226")]
public class M226Tests : BenchFixture
{
    /// <summary>
    /// M226, M600 and M601 pause the job from within the file, and the object model describes where
    /// it stopped: paused, with the file position of the pause and a pause duration that counts
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp cases 226, 600 and 601 all reach <c>DoSynchronousPause</c>, which records the
    /// restore point and the file position (GCodes.cpp, <c>ms.GetPauseRestorePoint().filePos</c>).
    /// PrintMonitor.cpp reports <c>pauseDuration</c> and keeps <c>duration</c> running while paused,
    /// and RepRap.cpp <c>GetStatusIndex</c> reports "paused"
    /// </remarks>
    [Test]
    public async Task M226PausesWithThePausePointInTheModel()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X40 Y40 F3000
                M226
                G1 X10 Y10 F3000
                """)).StartAsync();
        long size = new FileInfo(Physical(bench, "gcodes", "job.gcode")).Length;

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        long modelPosition = 0;
        await Assert.MultipleAsync(async () =>
        {
            modelPosition = (long)await NumberAsync(bench.Host, model => model.Job.FilePosition);
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.Status), Is.EqualTo(MachineStatus.Paused),
                        "M226 pauses the job (RepRap.cpp GetStatusIndex)");
            Assert.That(modelPosition, Is.GreaterThan(0).And.LessThanOrEqualTo(size),
                        "job.filePosition is where the file paused (PrintMonitor.cpp filePosition)");
            Assert.That(await NumberAsync(bench.Host, model => model.Job.PauseDuration), Is.GreaterThanOrEqualTo(0),
                        "job.pauseDuration counts the pause in seconds (PrintMonitor.cpp pauseDuration)");
            Assert.That(await NumberAsync(bench.Host, model => model.Job.Duration), Is.GreaterThanOrEqualTo(0),
                        "job.duration keeps running while the job is paused (PrintMonitor.cpp duration)");
        });

        string printStatus = await bench.Host.ExecuteCodeAsync("M27", timeoutMs: ReportTimeoutMs);

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        (long position, long length) = ParsePrintStatus(printStatus);
        Assert.Multiple(async () =>
        {
            Assert.That(await SettledValueAsync(bench.Host, model => model.Job.LastFileName, "0:/gcodes/job.gcode"), Is.EqualTo("0:/gcodes/job.gcode"),
                        "the resumed job ran to its end (PrintMonitor.cpp lastFileName)");
            Assert.That(length, Is.EqualTo(size), "M27 still reports the length of the file being printed");
            Assert.That(position, Is.EqualTo(modelPosition),
                        "M27 reports the paused job's file position (GCodes2.cpp case 27)");
        });
    }
}
