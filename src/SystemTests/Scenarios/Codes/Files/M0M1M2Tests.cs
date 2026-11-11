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

/// <summary>M0, M1 and M2, ending a job</summary>
[TestFixture]
[GCode("M0", "M1", "M2")]
public class M0M1M2Tests : BenchFixture
{
    /// <summary>
    /// M0, M1 and M2 outside a job file cancel a paused print, and are refused when there is nothing
    /// to cancel
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp cases 0, 1 and 2: from a file channel they stop the print as its normal
    /// completion; from anywhere else they cancel a print that is paused, running cancel.g or, if
    /// that is missing, stop.g; and with no paused print they answer
    /// "Pause the print before attempting to cancel it" as an error, running neither macro
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M0M1AndM2OutsideAJobAreRefused()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed);

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ExecuteCodeAsync("M0"),
                        Does.Contain("Pause the print before attempting to cancel it"),
                        "M0 with no paused print is refused (GCodes2.cpp case 0)");
            Assert.That(await bench.Host.ExecuteCodeAsync("M1"),
                        Does.Contain("Pause the print before attempting to cancel it"),
                        "M1 with no paused print is refused (GCodes2.cpp case 1)");
            Assert.That(await bench.Host.ExecuteCodeAsync("M2"),
                        Does.Contain("Pause the print before attempting to cancel it"),
                        "M2 with no paused print is refused (GCodes2.cpp case 2)");
        });

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.Zero,
                        "a refused M0 runs no stop.g (GCodes2.cpp case 0)");
            Assert.That(await bench.Host.GlobalAsync("cancelRan"), Is.Zero,
                        "a refused M0 runs no cancel.g (GCodes2.cpp case 0)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.Status), Is.EqualTo(MachineStatus.Idle),
                        "the machine stays idle (RepRap.cpp GetStatusIndex)");
        });
    }

    /// <summary>
    /// M2 inside the job file ends the job, exactly as M0 and M1 do from there
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp cases 0, 1 and 2 from a file channel wait for standstill and call
    /// <c>StopPrint(StopPrintReason::normalCompletion)</c>, which runs stop.g and moves the file
    /// name into <c>job.lastFileName</c> (PrintMonitor.cpp)
    /// </remarks>
    [Test]
    public async Task M2InsideTheJobFileEndsTheJob()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteGCode("job.gcode", "G90\nG1 X25 Y25 F3000\n" + JobDriving.FillerMoves(pairs: 30, feed: 600) +
                                       "M2\nG1 X100 Y100 F3000\n")).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Processing);
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1),
                        "M2 in the file ends the job through stop.g (GCodes.cpp StopPrint)");
            Assert.That(await SettledValueAsync(bench.Host, model => model.Job.LastFileName, "0:/gcodes/job.gcode"), Is.EqualTo("0:/gcodes/job.gcode"),
                        "the ended job's file becomes job.lastFileName (PrintMonitor.cpp lastFileName)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileAborted), Is.False,
                        "ending a job with M2 is a normal completion (job.lastFileAborted)");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].UserPosition), Is.EqualTo(25.0).Within(0.01),
                        "nothing after the M2 ran (move.axes[0].userPosition)");
        });
    }
}
