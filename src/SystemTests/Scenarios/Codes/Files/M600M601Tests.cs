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

/// <summary>M600 and M601, pausing for a filament change and pausing from within the file</summary>
[TestFixture]
[GCode("M600", "M601")]
public class M600M601Tests : BenchFixture
{
    /// <summary>
    /// M600 and M601 pause the same way M226 does, differing only in the reason and the macro, and
    /// all three are refused outside a job file
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp cases 226, 600 and 601: outside a file channel they answer
    /// "use M226/600/601 only within a file being printed". M600 pauses for
    /// <c>PrintPausedReason::filamentChange</c> and M601 for <c>PrintPausedReason::gcode</c>, both
    /// through the same <c>DoSynchronousPause</c>
    /// </remarks>
    [Test]
    [Category("LongRunning")]
    public async Task M600AndM601PauseFromWithinTheFile()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X30 Y30 F3000
                M600
                G1 X60 Y60 F3000
                M601
                G1 X10 Y10 F3000
                """)).StartAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ExecuteCodeAsync("M600"),
                        Does.Contain("use M226/600/601 only within a file being printed"),
                        "M600 outside a job file is refused (GCodes2.cpp case 600)");
            Assert.That(await bench.Host.ExecuteCodeAsync("M601"),
                        Does.Contain("use M226/600/601 only within a file being printed"),
                        "M601 outside a job file is refused (GCodes2.cpp case 601)");
        });

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await NumberAsync(bench.Host, model => model.Job.FilePosition), Is.GreaterThan(0),
                    "M600 paused with the file position of the pause point (PrintMonitor.cpp filePosition)");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForPauseAtAsync(60.0, 60.0);
        Assert.That(await bench.Host.ReadModelAsync(model => model.State.Status), Is.EqualTo(MachineStatus.Paused),
                    "M601 pauses the job in turn (RepRap.cpp GetStatusIndex)");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.That(await SettledValueAsync(bench.Host, model => model.Job.LastFileName, "0:/gcodes/job.gcode"), Is.EqualTo("0:/gcodes/job.gcode"),
                    "the job ran to its end through both pauses (PrintMonitor.cpp lastFileName)");
    }
}
