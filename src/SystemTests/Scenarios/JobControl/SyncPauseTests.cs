using System.IO;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// Synchronous pauses: M25 inside the job file, M226 with and without its pause macro, M601, and
/// M600 with its filament-change.g and the fallback to pause.g. A synchronous pause performs no
/// feedhold: everything queued before it runs to completion, nothing is purged, no resume fraction
/// is recorded, and the resume carries on at the code after it
/// </summary>
[TestFixture]
public class SyncPauseTests : BenchFixture
{
    /// <summary>
    /// M25 inside the job file: the queue drains, the restore point is the last move's target
    /// (taken before pause.g parks), and the resume runs the codes after the M25
    /// </summary>
    [Test]
    public async Task PauseFromWithinTheFile()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: "global after25 = 0\n",
            prepareSd: sd => sd.WriteGCode("job.gcode", """
                G90
                G1 X100 Y50 F6000
                G1 X100 Y100 F6000
                M25
                set global.after25 = 1
                G1 X10 Y10 F6000
                G60 S3
                """));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(1), "pause.g ran once");
            Assert.That(await bench.Host.RestorePointAsync(1), Is.EqualTo((100.0, 100.0)),
                        "the restore point is the completed last move's target, not pause.g's park");
            Assert.That(await bench.Host.GlobalAsync("after25"), Is.Zero, "nothing after the M25 ran yet");
        });

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("resumeRan"), Is.EqualTo(1), "resume.g ran once");
            Assert.That(await bench.Host.GlobalAsync("after25"), Is.EqualTo(1), "the code after M25 ran on resume");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job finished normally");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)),
                        "the job ended at its final position");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 0), Is.EqualTo(10 * 80),
                        "the scheduled X steps net out at the final position: the park and the move back cancel");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 1), Is.EqualTo(10 * 80),
                        "and so do the Y steps");
        });
    }

    /// <summary>
    /// M226 pauses like an in-file M25; M226 P0 still pauses and still writes the restore point,
    /// but runs no pause macro at all, while its M24 still runs resume.g
    /// </summary>
    [Test]
    public async Task M226AndM226P0()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X60 Y20 F6000
                M226
                G1 X60 Y60 F6000
                M226 P0
                G1 X10 Y10 F6000
                """));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(1), "M226 ran pause.g");
            Assert.That(await bench.Host.RestorePointAsync(1), Is.EqualTo((60.0, 20.0)), "first pause point");
        });

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForPauseAtAsync(60.0, 60.0);
        Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(1), "M226 P0 ran no pause macro");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("resumeRan"), Is.EqualTo(2), "both resumes ran resume.g");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job finished normally");
        });
    }

    /// <summary>M601 pauses like M226, and M24 P0 skips resume.g while still resuming</summary>
    [Test]
    public async Task M601ResumedWithM24P0()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X80 Y40 F6000
                M601
                G1 X10 Y10 F6000
                G60 S3
                """));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(1), "M601 ran pause.g");
            Assert.That(await bench.Host.RestorePointAsync(1), Is.EqualTo((80.0, 40.0)), "the pause point");
        });

        await bench.Host.ExecuteCodeAsync("M24 P0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("resumeRan"), Is.Zero, "M24 P0 skipped resume.g");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job still finished normally");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)),
                        "the move back to the pause point still happened and the job completed its moves");
        });
    }

    /// <summary>M600 runs filament-change.g, not pause.g</summary>
    [Test]
    public async Task M600RunsFilamentChangeMacro()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X120 Y60 F6000
                M600
                G1 X10 Y10 F6000
                """));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("filChangeRan"), Is.EqualTo(1), "filament-change.g ran");
            Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.Zero, "pause.g did not");
            Assert.That(await bench.Host.RestorePointAsync(1), Is.EqualTo((120.0, 60.0)), "the pause point");
        });

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job finished normally");
    }

    /// <summary>Without a filament-change.g, M600 falls back to pause.g</summary>
    [Test]
    public async Task M600FallsBackToPauseMacro()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
        {
            File.Delete(Path.Combine(sd.Root, "sys", "filament-change.g"));
            sd.WriteGCode("job.gcode", """
                G90
                G1 X120 Y60 F6000
                M600
                G1 X10 Y10 F6000
                """);
        });

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(1),
                    "pause.g ran as the fallback for the missing filament-change.g");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job finished normally");
    }
}
