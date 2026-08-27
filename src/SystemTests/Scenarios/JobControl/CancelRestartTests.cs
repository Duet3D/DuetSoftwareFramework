using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// Cancelling a paused job with M0, and restarting a job from a recorded file position with M26:
/// the existing half of power-fail resume. M26 S seeks the selected file; P (the fraction already
/// made) and C (the modal G command) are stored and applied only at M24, after start.g has run
/// </summary>
[TestFixture]
public class CancelRestartTests : BenchFixture
{
    /// <summary>
    /// M0 while paused runs cancel.g, closes the file, and nothing after the pause point ever
    /// runs, not even after a later attempt to resume
    /// </summary>
    [Test]
    public async Task CancelWhilePausedRunsCancelMacro()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: "global after25 = 0\n",
            prepareSd: sd => sd.WriteGCode("job.gcode", """
                G90
                G1 X70 Y70 F6000
                M25
                set global.after25 = 1
                """));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("cancelRan"), Is.EqualTo(1), "cancel.g ran");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.Zero, "stop.g did not: cancel.g exists");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileCancelled), Is.True,
                        "the job records itself as cancelled");
            Assert.That(await bench.Host.GlobalAsync("after25"), Is.Zero,
                        "nothing after the pause point ran: the file is closed");
        });

        Assert.That(await bench.Host.ExecuteCodeAsync("M24"),
                    Does.Contain("Cannot print, because no file is selected!"),
                    "a resume after the cancel finds no file");
        Assert.That(await bench.Host.GlobalAsync("after25"), Is.Zero, "and runs nothing");
    }

    /// <summary>Without a cancel.g, cancelling a paused job falls back to stop.g</summary>
    [Test]
    public async Task CancelFallsBackToStopMacro()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
        {
            File.Delete(Path.Combine(sd.Root, "sys", "cancel.g"));
            sd.WriteGCode("job.gcode", "G90\nG1 X70 Y70 F6000\nM25\n");
        });

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1),
                        "stop.g ran as the fallback for the missing cancel.g");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileCancelled), Is.True,
                        "the job still records itself as cancelled");
        });
    }

    /// <summary>
    /// Eight segments of distinct lengths, so the capture says exactly which of them were made.
    /// The X65 line is bare: reading it needs a modal G1, which the restart's C parameter provides
    /// </summary>
    private const string SegmentsJob = "G90\nG1 X11 F6000\nG1 X23 F6000\nG1 X36 F6000\nG1 X50 F6000\nX65\nG1 X81 F6000\nG1 X98 F6000\nG1 X116 F6000\n";

    /// <summary>start.g with a move of its own, to prove a restart fraction does not shorten it</summary>
    private const string StartWithMove = "set global.startRan = global.startRan + 1\nG90\nG1 X5 Y5 F6000\n";

    /// <summary>The scheduled move distances, rounded to make the distinct segments recognisable</summary>
    private static double[] RoundedDistances(JobBench bench)
        => bench.CanMaster.MoveDistances().Select(d => Math.Round((double)d, 1)).ToArray();

    /// <summary>
    /// M26 S restarts the selected file at a byte offset: start.g runs first and only the
    /// segments at or after the offset are made
    /// </summary>
    [Test]
    public async Task RestartFromFilePosition()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
        {
            sd.WriteSys("start.g", StartWithMove);
            sd.WriteGCode("job.gcode", SegmentsJob);
        });

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/job.gcode\"");
        await bench.Host.ExecuteCodeAsync($"M26 S{SegmentsJob.IndexOf("G1 X81")}");
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        double[] distances = RoundedDistances(bench);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("startRan"), Is.EqualTo(1), "the restart ran start.g");
            Assert.That(distances, Does.Contain(7.1), "start.g's own move, whole");
            Assert.That(distances, Does.Contain(76.0), "the segment at the offset, from start.g's X5");
            Assert.That(distances, Does.Contain(17.0).And.Contain(18.0), "the segments after it");
            Assert.That(distances.Where(d => d is 11.0 or 12.0 or 13.0 or 14.0 or 15.0), Is.Empty,
                        "no segment before the offset was made");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the restarted job ended normally");
        });
    }

    /// <summary>
    /// M26 P and C are applied only at M24, after start.g: the line at the offset is read as a
    /// modal G1 with the given fraction already made, so only its remainder is scheduled, while
    /// start.g's own move stays whole
    /// </summary>
    [Test]
    public async Task RestartWithFractionAndModalCommand()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
        {
            sd.WriteSys("start.g", StartWithMove);
            sd.WriteGCode("job.gcode", SegmentsJob);
        });

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/job.gcode\"");
        await bench.Host.ExecuteCodeAsync($"M26 S{SegmentsJob.IndexOf("X65")} P0.5 C1");
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        double[] distances = RoundedDistances(bench);
        Assert.Multiple(async () =>
        {
            Assert.That(distances, Does.Contain(7.1), "start.g's move was not shortened by the fraction");
            Assert.That(distances, Does.Contain(30.0),
                        "the bare X65 line was read as a G1 with half of its 60 mm (from start.g's X5) already made");
            Assert.That(distances, Does.Contain(16.0).And.Contain(17.0).And.Contain(18.0),
                        "the segments after it, whole");
            Assert.That(distances, Does.Not.Contain(60.0), "the halved segment was not re-run whole");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the restarted job ended normally");
        });
    }
}
