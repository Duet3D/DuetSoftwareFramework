using System;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// The refusals and no-ops of the job control codes, with the exact replies the operator would
/// read. One host serves each group of checks, because the refusals depend only on the machine
/// state around them
/// </summary>
[TestFixture]
public class JobControlRefusalTests : BenchFixture
{
    /// <summary>Every refusal reachable with no job selected at all</summary>
    [Test]
    public async Task RefusalsWithNoJob()
    {
        await using JobBench bench = await JobControlBench.StartAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ExecuteCodeAsync("M25"),
                        Does.Contain("Cannot pause print, because no file is being printed!"),
                        "M25 with no job");
            Assert.That(await bench.Host.ExecuteCodeAsync("M226"),
                        Does.Contain("use M226/600/601 only within a file being printed"),
                        "M226 from the console");
            Assert.That(await bench.Host.ExecuteCodeAsync("M600"),
                        Does.Contain("use M226/600/601 only within a file being printed"),
                        "M600 from the console");
            Assert.That(await bench.Host.ExecuteCodeAsync("M601"),
                        Does.Contain("use M226/600/601 only within a file being printed"),
                        "M601 from the console");
            Assert.That(await bench.Host.ExecuteCodeAsync("M24"),
                        Does.Contain("Cannot print, because no file is selected!"),
                        "M24 with no file selected");
            Assert.That((await bench.Host.ExecuteCodeAsync("M0")).Trim(), Is.Empty,
                        "M0 with no job at all is accepted");
            Assert.That(await bench.Host.ExecuteCodeAsync("M26"),
                        Does.Contain("Not printing a file"),
                        "M26 with no file selected");
            Assert.That(await bench.Host.ExecuteCodeAsync("G60 S9"),
                        Does.Contain("S parameter must be between"),
                        "G60 S9 is out of range");
            Assert.That(await bench.Host.ExecuteCodeAsync("M505"),
                        Does.Contain("Sys file path is"),
                        "M505 without P reports the sys path");
            Assert.That(await bench.Host.ExecuteCodeAsync("M505 P\"0:/sys/nope\""),
                        Does.Contain("Directory not found"),
                        "M505 with a missing directory");
        });
    }

    /// <summary>
    /// The refusals that need a job in flight: cancelling before pausing, selecting over a running
    /// job, and pausing twice
    /// </summary>
    [Test]
    public async Task RefusalsAroundARunningJob()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
        {
            sd.WriteGCode("job.gcode", "G90\nG1 X190 F1200\nG1 X10 F1200\nG1 X190 F1200\nG1 X10 F1200\n");
            sd.WriteGCode("other.gcode", "G91\nG1 X1 F6000\n");
        });

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Processing);

        Assert.That(await bench.Host.ExecuteCodeAsync("M0"),
                    Does.Contain("Pause the print before attempting to cancel it"),
                    "M0 while running unpaused");
        Assert.That(await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/other.gcode\""),
                    Does.Contain("Cannot set file to print, because a file is already being printed"),
                    "M23 while printing");
        Assert.That(await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/other.gcode\""),
                    Does.Contain("Cannot set file to print, because a file is already being printed"),
                    "M32 while printing");

        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.ExecuteCodeAsync("M25"),
                    Does.Contain("Printing is already paused!"),
                    "M25 while already paused");
        Assert.That(await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/other.gcode\""),
                    Does.Contain("Cannot set file to print, because a file is already being printed"),
                    "M23 while paused");

        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
    }

    /// <summary>
    /// The transition no-ops: M24 while the machine is still pausing, and again while it is
    /// resuming, is silently ignored with an empty reply rather than refused
    /// </summary>
    [Test]
    public async Task M24DuringTransitionsIsIgnored()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", """
                G90
                G1 X190 Y190 F3000
                G1 X10 Y10 F3000
                G1 X190 Y190 F3000
                G1 X10 Y10 F3000
                """));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.CanMaster.WaitForSbcPacketAsync(SbcRequest.ScheduleMove);
        await Task.Delay(TimeSpan.FromSeconds(1));

        // While the machine is pausing (pause.g's park is still on its way to X0 Y0)...
        Task<string> pausing = bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Pausing);
        Assert.That((await bench.Host.ExecuteCodeAsync("M24", DuetAPI.CodeChannel.Telnet)).Trim(), Is.Empty,
                    "M24 while pausing is silently ignored");
        await pausing;
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        // ...and while it is resuming (moving back to the pause point)
        Task<string> resuming = bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Resuming);
        Assert.That((await bench.Host.ExecuteCodeAsync("M24", DuetAPI.CodeChannel.Telnet)).Trim(), Is.Empty,
                    "a second M24 while resuming is ignored, not refused");
        await resuming;
        await bench.Host.WaitForStatusAsync(MachineStatus.Processing);

        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
    }

    /// <summary>
    /// M26 out of range on a selected file, and the M23-then-M24 path: a file selected but never
    /// started is BEGUN by M24, start.g included
    /// </summary>
    [Test]
    public async Task SelectedFileChecks()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
            sd.WriteGCode("job.gcode", "G91\nG1 X5 F6000\n"));

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/job.gcode\"");
        Assert.That(await bench.Host.ExecuteCodeAsync("M26 S-1"),
                    Does.Contain("Position is out of range"),
                    "M26 S-1 on a selected file");
        Assert.That(await bench.Host.GlobalAsync("startRan"), Is.Zero, "M23 alone must not start the job");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.That(await bench.Host.GlobalAsync("startRan"), Is.EqualTo(1),
                    "M24 on a selected file begins the job through start.g");
        Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "and the short job ran to its end");
    }
}
