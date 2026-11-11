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

/// <summary>M28 and M29, writing codes to a file</summary>
[TestFixture]
[GCode("M28", "M29")]
public class M28M29Tests : BenchFixture
{
    /// <summary>
    /// M28 captures the codes that follow it into a file instead of executing them, and M29 closes
    /// the capture without being captured itself
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 28 opens the file in the G-code directory and answers
    /// "Writing to file: &lt;name&gt;"; case 29 is intercepted before it reaches the handler, which
    /// is why the file holds every code between the two and not the M29
    /// </remarks>
    [Test]
    public async Task M28CapturesCodesUntilM29()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, JobMacros.Instrumented, "global captured = 0\n", Machines.Xye.Homed);

        Assert.That(await bench.Host.ExecuteCodeAsync("M28 \"0:/gcodes/capture.gcode\""),
                    Does.Contain("Writing to file: 0:/gcodes/capture.gcode"),
                    "M28 names the file it is writing to (GCodes2.cpp case 28)");

        await bench.Host.ExecuteCodeAsync("G91");
        await bench.Host.ExecuteCodeAsync("G1 X5 F600");
        await bench.Host.ExecuteCodeAsync("set global.captured = 1");
        await bench.Host.ExecuteCodeAsync("M29");

        string captured = await File.ReadAllTextAsync(Physical(bench, "gcodes", "capture.gcode"));
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(captured, Does.Contain("G1 X5"),
                        "the move between M28 and M29 was written to the file (GCodes2.cpp case 28)");
            Assert.That(captured, Does.Contain("global.captured"),
                        "every code between M28 and M29 is captured, meta commands included");
            Assert.That(captured, Does.Not.Contain("M29"),
                        "M29 ends the capture rather than being captured (GCodes2.cpp case 29)");
            Assert.That(await bench.Host.GlobalAsync("captured"), Is.Zero,
                        "a captured code does not execute");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].UserPosition), Is.Zero,
                        "the captured move did not move the machine (move.axes[0].userPosition)");
        });

        // The captured file is a job file like any other, so it can be selected and inspected
        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/capture.gcode\"");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Job.File.Size),
                    Is.EqualTo(new FileInfo(Physical(bench, "gcodes", "capture.gcode")).Length),
                    "job.file.size reports what M28 wrote (PrintMonitor.cpp fileSize)");
    }
}
