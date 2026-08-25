using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// Asynchronous pauses: M25 from the console while the job runs, which is the feedhold path. The
/// motion engine stops at the first boundary it can still stop at, everything queued behind it is
/// purged, and the restore point names the interrupted code with the fraction already made of it,
/// so the resume completes the remainder and replays the purged moves. The scenarios assert the
/// composition end to end: whatever fraction the stop landed on, the job must finish at exactly
/// the positions and extrusion totals an unpaused run reaches
/// </summary>
[TestFixture]
public class AsyncPauseTests : BenchFixture
{
    /// <summary>
    /// Start the given job and pause it from the console mid-flight: after the first scheduled
    /// move appears, wait out <paramref name="intoJob"/> of execution time and send M25
    /// </summary>
    private static async Task PauseMidJobAsync(JobBench bench, string jobFile, TimeSpan intoJob)
    {
        await bench.Host.ExecuteCodeAsync($"M32 \"{jobFile}\"");
        await bench.CanMaster.WaitForSbcPacketAsync(SbcRequest.ScheduleMove);
        await Task.Delay(intoJob);
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
    }

    /// <summary>
    /// The plain feedhold: the head stops mid-move rather than finishing it, the restore point
    /// records the interrupted G1 and its unscaled feed rate, and after the resume nothing is
    /// skipped and nothing is doubled
    /// </summary>
    [Test]
    public async Task FeedholdStopsMidMove()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X190 F3000
                G1 X190 Y100 F6000
                G1 X10 Y100 F6000
                G60 S3
                """));

        // 190 mm at 50 mm/s: one second in, the first move is mid-flight
        // TODO there is a race condition that means the `WaitForStatusAsync(MachineStatus.Paused)` is never true
        await PauseMidJobAsync(bench, "0:/gcodes/job.gcode", TimeSpan.FromSeconds(1));

        (double pausedX, double pausedY) = await bench.Host.RestorePointAsync(1);
        Assert.Multiple(async () =>
        {
            Assert.That(pausedX, Is.InRange(1.0, 189.0), "the head stopped mid-move, not at either end");
            Assert.That(pausedY, Is.Zero, "still on the first move's line");
            Assert.That(await bench.Host.EvaluateRawAsync("state.restorePoints[1].gCommandNumber"), Is.EqualTo("1"),
                        "the interrupted code is a G1");
            Assert.That(await bench.Host.EvaluateAsync("state.restorePoints[1].feedRate"), Is.EqualTo(50.0).Within(0.01),
                        "the interrupted move's feed rate in mm/s");
            Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(1), "the feedhold still ran pause.g");
        });

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("resumeRan"), Is.EqualTo(1), "resume.g ran once");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job finished normally");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 100.0)),
                        "the interrupted move and the purged moves all completed");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 0), Is.EqualTo(10 * 80),
                        "the scheduled X steps net out at the final position");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 1), Is.EqualTo(100 * 80),
                        "and the Y steps");
        });
    }

    /// <summary>
    /// The resume fraction applied to relative axis words and relative extrusion: a G91 word is a
    /// distance to travel, so the remainder is the word scaled by the fraction not yet made. If
    /// the scaling were wrong the square would not close and the extrusion total would drift
    /// </summary>
    [Test]
    public async Task ResumeFractionScalesRelativeMoves()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X10 Y10 F6000
                M83
                G91
                G1 X180 E10 F3000
                G1 Y180 E10 F6000
                G1 X-180 E10 F6000
                G1 Y-180 E10 F6000
                G90
                G60 S3
                """));

        // Mid the first relative edge (180 mm at 50 mm/s)
        await PauseMidJobAsync(bench, "0:/gcodes/job.gcode", TimeSpan.FromSeconds(1.5));
        (double pausedX, _) = await bench.Host.RestorePointAsync(1);
        Assert.That(pausedX, Is.InRange(10.5, 189.5), "the pause landed inside the first relative edge");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)),
                        "the relative square closed exactly: the interrupted word was scaled, not re-run whole");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 0), Is.EqualTo(10 * 80), "net X steps");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 1), Is.EqualTo(10 * 80), "net Y steps");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 2), Is.EqualTo(40 * 420),
                        "total extrusion equals an unpaused run's 40 mm");
        });
    }

    /// <summary>
    /// The resume fraction applied to absolute extrusion: an extruder has no start to move back
    /// to, so the M82 amount itself is scaled by the fraction not yet made. A wrong implementation
    /// either re-extrudes the whole line or skips the remainder
    /// </summary>
    [Test]
    public async Task ResumeFractionScalesAbsoluteExtrusion()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X10 F6000
                M82
                G92 E0
                G1 X190 E40 F3000
                G60 S3
                """));

        // Mid the long combined X+E move (180 mm at 50 mm/s)
        await PauseMidJobAsync(bench, "0:/gcodes/job.gcode", TimeSpan.FromSeconds(1.5));
        (double pausedX, _) = await bench.Host.RestorePointAsync(1);
        Assert.That(pausedX, Is.InRange(10.5, 189.5), "the pause landed inside the extruding move");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That((await bench.Host.RestorePointAsync(3)).X, Is.EqualTo(190.0), "the move completed");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 2), Is.EqualTo(40 * 420),
                        "total extrusion is exactly the absolute 40 mm: no blob, no gap");
        });
    }

    /// <summary>
    /// A pause on a bare modal line: seeking throws the parser's modal state away, so the pause
    /// records the modal G command and the feed rate the line was read with, and the resume puts
    /// both back before re-reading the line
    /// </summary>
    [Test]
    public async Task PauseOnBareModalLine()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X10 Y10 F6000
                G1 X190 Y10 F6000
                X190 Y190
                X10 Y190
                X10 Y10
                G60 S3
                """));

        // Past the explicit G1 edge (3 s at 100 mm/s), into the bare lines
        await PauseMidJobAsync(bench, "0:/gcodes/job.gcode", TimeSpan.FromSeconds(3.5));

        (double pausedX, double pausedY) = await bench.Host.RestorePointAsync(1);
        Assert.Multiple(async () =>
        {
            Assert.That(pausedY, Is.GreaterThan(10.5), "the pause landed on one of the bare modal lines");
            Assert.That(await bench.Host.EvaluateRawAsync("state.restorePoints[1].gCommandNumber"), Is.EqualTo("1"),
                        "the modal G command was recorded");
            Assert.That(await bench.Host.EvaluateAsync("state.restorePoints[1].feedRate"), Is.EqualTo(100.0).Within(0.01),
                        "with the feed rate the line was read with");
        });

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1),
                        "the re-read bare line completed as a G1 and the job finished");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)), "the square closed");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 0), Is.EqualTo(10 * 80), "net X steps");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 1), Is.EqualTo(10 * 80), "net Y steps");
        });
    }

    /// <summary>
    /// The recorded feed rate is the one the line was read with, unscaled by M220: if the scaled
    /// rate were recorded, every pause under M220 would fold the factor into the file's own feed
    /// rate and the job would get slower each time
    /// </summary>
    [Test]
    public async Task RecordedFeedRateIsUnscaledByM220()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X10 Y10 F6000
                M220 S50
                G1 X190 Y10 F3000
                G1 X10 Y10 F3000
                M220 S100
                G60 S3
                """));

        // The long edge runs at the scaled 25 mm/s, so 1.5 s in it is mid-flight
        await PauseMidJobAsync(bench, "0:/gcodes/job.gcode", TimeSpan.FromSeconds(1.5));

        Assert.That(await bench.Host.EvaluateAsync("state.restorePoints[1].feedRate"), Is.EqualTo(50.0).Within(0.01),
                    "the recorded rate is the file's F3000, not the M220-scaled one");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)), "the job completed");
    }

    /// <summary>
    /// Two pauses inside the same long line: the recorded fraction is a fraction of the whole
    /// code however many times the job has been stopped inside it, so the second stop composes
    /// with the first instead of repeating or cutting short part of the line
    /// </summary>
    [Test]
    public async Task TwoPausesInsideOneLine()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X5 Y100 F6000
                M83
                G1 X195 E20 F1500
                G60 S3
                """));

        // The long line runs 190 mm at 25 mm/s; the first stop lands in its first half
        await PauseMidJobAsync(bench, "0:/gcodes/job.gcode", TimeSpan.FromSeconds(2));
        (double firstStop, _) = await bench.Host.RestorePointAsync(1);
        Assert.That(firstStop, Is.InRange(5.5, 194.5), "the first pause landed inside the line");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Processing);

        // And the second in what remains of the same line
        await Task.Delay(TimeSpan.FromSeconds(2));
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        (double secondStop, _) = await bench.Host.RestorePointAsync(1);
        Assert.That(secondStop, Is.GreaterThan(firstStop), "the second stop is further along the same line");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((195.0, 100.0)),
                        "the line still finished exactly at its target");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 2), Is.EqualTo(20 * 420),
                        "the extrusion over the whole line totals 20 mm regardless of the two stops");
        });
    }
}
