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

/// <summary>M73, the slicer's time estimate</summary>
[TestFixture]
[GCode("M73")]
public class M73Tests : BenchFixture
{
    /// <summary>
    /// M73 gives the job monitor the slicer's own estimate, which is what job.timesLeft.slicer
    /// reports while the file is printing
    /// </summary>
    /// <remarks>
    /// PrintMonitor.cpp <c>ProcessM73</c> stores R as the time left in minutes; the OBJECT_MODEL
    /// entry <c>timesLeft.slicer</c> is <c>EstimateTimeLeft(slicerBased)</c>, which reports the
    /// stored value less the time since it was stored, and null when no file is being printed
    /// </remarks>
    /// TODO this test is intermitant
    [Test]
    public async Task M73SetsTheSlicerTimeEstimate()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
            sd.WriteGCode("job.gcode", "G90\nG1 X50 Y50 F3000\n" + JobDriving.FillerMoves(pairs: 60, feed: 600))).StartAsync();

        await bench.Host.ExecuteCodeAsync("M73 R10");
        int? withNoJob = await bench.Host.ReadModelAsync(model => model.Job.TimesLeft.Slicer);

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Processing);
        await bench.Host.ExecuteCodeAsync("M73 R10");
        double whilePrinting = await SettledNumberAsync(bench.Host, model => model.Job.TimesLeft.Slicer);

        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        int? afterTheJob = await bench.Host.ReadModelAsync(model => model.Job.TimesLeft.Slicer);

        Assert.Multiple(() =>
        {
            Assert.That(withNoJob, Is.Null,
                        "job.timesLeft.slicer is null while no file is printing (PrintMonitor.cpp EstimateTimeLeft)");
            Assert.That(whilePrinting, Is.GreaterThan(9 * 60).And.LessThanOrEqualTo(10 * 60),
                        "M73 R10 sets job.timesLeft.slicer to ten minutes in seconds (PrintMonitor.cpp ProcessM73)");
            Assert.That(afterTheJob, Is.Null,
                        "the estimate goes back to null once the job has stopped (PrintMonitor.cpp EstimateTimeLeft)");
        });
    }
}
