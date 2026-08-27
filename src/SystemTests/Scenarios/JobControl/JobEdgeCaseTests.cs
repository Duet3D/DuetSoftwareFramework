using System;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// The job control edge cases: how long a job lasts, a pause inside an open while block, G60
/// restore points beside the pause point, a pause during the final move of a job, and pausing with
/// unhomed axes
/// </summary>
[TestFixture]
public class JobEdgeCaseTests : BenchFixture
{
    /// <summary>
    /// A job lasts as long as its motion, not as long as its reading. A movement code finishes once
    /// its move is queued, so a job file of a few long moves is read to the end in milliseconds -
    /// and if that ended the job, the machine would report itself idle with the head still moving,
    /// <c>stop.g</c> would run against a machine that had not finished, and every pause commanded
    /// from that point on would be refused for want of a job to pause
    /// </summary>
    [Test]
    public async Task JobLastsAsLongAsItsMotion()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X190 F3000
                G1 X190 Y100 F6000
                G60 S3
                """));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.CanMaster.WaitForSbcPacketAsync(SbcRequest.ScheduleMove);

        // 190 mm at 50 mm/s and 100 mm at 100 mm/s is close to five seconds of motion; one second
        // in, every code has been read and none of the motion has been made
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.Status), Is.EqualTo(MachineStatus.Processing),
                        "the job is still running while the machine works through what it queued");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.Zero, "so stop.g has not run yet");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].MachinePosition), Is.LessThan(189.0),
                        "and the head has not reached the end of the first move");
        });

        // A pause is still possible, which is the point of staying in the job until the motion ends
        Assert.That(await bench.Host.ExecuteCodeAsync("M25"), Does.Not.Contain("no file is being printed"),
                    "the job is there to be paused");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1),
                        "the job ended once, after the machine had stopped");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((190.0, 100.0)),
                        "having made all of its motion");
        });
    }

    /// <summary>
    /// Pausing inside an open while block: block state does not survive the seek, the job re-parses
    /// from the recorded position and the loop counts again, so the job runs more bodies in total
    /// than an unpaused run and nothing errors when the seek lands inside the block
    /// </summary>
    [Test]
    public async Task PauseInsideWhileLoop()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: "global loopCount = 0\n",
            prepareSd: sd => sd.WriteGCode("job.gcode", """
                G90
                G1 X20 Y20 F6000
                while iterations < 4
                    G1 X{40 + iterations * 40} Y100 F6000
                    G1 X20 Y20 F6000
                    set global.loopCount = global.loopCount + 1
                G60 S3
                """));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.CanMaster.WaitForSbcPacketAsync(SbcRequest.ScheduleMove);
        await Task.Delay(TimeSpan.FromSeconds(3));

        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(1), "the feedhold pause settled");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1),
                        "the seek into the block did not error and the job finished");
            Assert.That(await bench.Host.GlobalAsync("loopCount"), Is.GreaterThan(4),
                        "the re-parsed while began counting again, so more than 4 bodies ran");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((20.0, 20.0)), "final position");
        });
    }

    /// <summary>
    /// G60 restore points beside the pause point: a bare G60 writes slot 0, G60 S3 slot 3, and the
    /// pause writes slot 1 without touching either
    /// </summary>
    [Test]
    public async Task RestorePointSlotsAreIsolated()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X30 Y40 F6000
                M400
                G60
                G1 X60 Y80 F6000
                M400
                G60 S3
                G1 X100 Y120 F6000
                M25
                G1 X10 Y10 F6000
                """));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.RestorePointAsync(0), Is.EqualTo((30.0, 40.0)), "bare G60 wrote slot 0");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((60.0, 80.0)), "G60 S3 wrote slot 3");
            Assert.That(await bench.Host.RestorePointAsync(1), Is.EqualTo((100.0, 120.0)),
                        "the pause wrote slot 1");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.RestorePoints[1].FeedRate), Is.EqualTo(100.0).Within(0.01),
                        "with the job's feed rate in mm/s");
        });

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.That(await bench.Host.RestorePointAsync(0), Is.EqualTo((30.0, 40.0)),
                    "slot 0 survived the pause untouched");
    }

    /// <summary>
    /// A pause during the final move of a job: the file position near EOF seeks back cleanly, the
    /// resume completes the remainder, and the job then finishes normally instead of hanging
    /// </summary>
    [Test]
    public async Task PauseDuringFinalMove()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: JobControlBench.SegmentedMoves, prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X10 Y10 F6000
                G1 X10 Y150 F6000
                G1 X200 Y150 F3000
                """));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.CanMaster.WaitForSbcPacketAsync(SbcRequest.ScheduleMove);

        // The two leading moves take about 1.6 s; land the pause inside the 190 mm final move
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        (double pausedX, double pausedY) = await bench.Host.RestorePointAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(pausedX, Is.InRange(10.5, 199.5), "the pause landed inside the final move");
            Assert.That(pausedY, Is.EqualTo(150.0), "on its line");
        });

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job ended normally");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileName), Does.EndWith("job.gcode"),
                        "as the last file printed");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileCancelled), Is.False,
                        "not cancelled");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 0), Is.EqualTo(200 * 80),
                        "the final move completed in full");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 1), Is.EqualTo(150 * 80));
        });
    }

    /// <summary>
    /// Pausing with unhomed axes: pause.g and resume.g are both skipped (they are written to lift
    /// and park, which is meaningless on a machine that does not know where it is), but the pause
    /// itself still happens, synchronously and asynchronously
    /// </summary>
    [Test]
    public async Task PauseUnhomedSkipsTheMacros()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                M18
                G91
                G1 X20 F3000
                M25
                G1 X150 F3000
                G1 X-150 F3000
                G90
                """));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.Zero,
                    "the synchronous pause settled without running pause.g");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Processing);
        Assert.That(await bench.Host.GlobalAsync("resumeRan"), Is.Zero, "and resume.g was skipped too");

        // The asynchronous variant behaves the same way
        await Task.Delay(TimeSpan.FromSeconds(1));
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.Zero, "the feedhold pause skipped pause.g as well");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("resumeRan"), Is.Zero, "no resume.g on the second resume either");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job finished normally");
        });
    }
}
