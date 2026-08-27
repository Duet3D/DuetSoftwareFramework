using System;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios;

/// <summary>
/// Pauses commanded while a macro runs. Macros are not pausable unless they declare M98 R1, so a
/// pause during a plain macro is deferred until the job is back out of it and fires as a
/// synchronous pause; a pause during an M98 R1 macro feedholds immediately, and because a macro's
/// file position cannot be recorded, the resume rewinds to the invoking M98 line and the macro
/// runs again from the beginning. A tool change is a sequence of plain macros, so a pause during
/// it defers the same way
/// </summary>
[TestFixture]
public class DeferredPauseTests : BenchFixture
{
    /// <summary>Four slow edges the pause can land in, counting its runs</summary>
    private const string SquareMacro = """
        set global.macroRuns = global.macroRuns + 1
        G90
        G1 X150 Y50 F3000
        G1 X150 Y150 F3000
        G1 X50 Y150 F3000
        G1 X50 Y50 F3000
        """;

    /// <summary>
    /// Two pauses around a job that defers codes. A Deferred-class code is dispatched without being
    /// awaited and its handler waits for the move it was anchored to, so a stop that leaves those
    /// codes owed has to say what becomes of them. A stop that comes to rest without dropping any
    /// queued move is the case that names no purge boundary, and the pause must still settle rather
    /// than wait on codes whose anchors will never retire
    /// </summary>
    [Test]
    public async Task PauseTwiceAroundDeferredCodes()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: "M950 F0 C\"1.out3\" Q500\n" + JobControlBench.SegmentedMoves,
            prepareSd: sd => sd.WriteGCode("job.gcode", """
                G91
                G1 X100 F6000
                G1 X100
                M106 S1
                M106 S0.5
                M106 S0.2
                G1 X-100
                G1 X-100
                M107
                G90
                G60 S3
                """));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.CanMaster.WaitForSbcPacketAsync(SbcRequest.ScheduleMove);
        await Task.Delay(TimeSpan.FromSeconds(1));

        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Processing);
        await Task.Delay(TimeSpan.FromSeconds(1));

        // The second stop is the one that hangs: it comes to rest with nothing left to purge, so
        // there is no boundary to cancel the owed codes against
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job finished normally");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((0.0, 0.0)),
                        "the out and back moves netted out however the pauses fell");
            Assert.That(bench.CanMaster.ScheduledSteps(driver: 0), Is.Zero, "and so did the steps");
        });
    }

    /// <summary>
    /// A pause during a non-pausable macro defers: every move of the macro completes, a second
    /// M25 while the first is pending is refused with a warning, the pause fires after the macro
    /// as a synchronous pause, and the macro is not rerun on resume
    /// </summary>
    [Test]
    public async Task PauseDuringPlainMacroDefers()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: "global afterMacro = 0\n",
            prepareSd: sd =>
            {
                sd.WriteMacro("plain-moves.g", SquareMacro);
                sd.WriteGCode("job.gcode", """
                    G90
                    G1 X50 Y50 F6000
                    M98 P"0:/macros/plain-moves.g"
                    set global.afterMacro = 1
                    G1 X10 Y10 F6000
                    G60 S3
                    """);
            });

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.CanMaster.WaitForSbcPacketAsync(SbcRequest.ScheduleMove);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // The pause is accepted but pending; a second one is told so
        await bench.Host.ExecuteCodeAsync("M25");
        Assert.That(await bench.Host.ExecuteCodeAsync("M25"), Does.Contain("Pausing is already pending"),
                    "a second M25 while the first is pending");

        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(1), "the macro completed once");
            Assert.That(await bench.Host.RestorePointAsync(1), Is.EqualTo((50.0, 50.0)),
                        "the deferred pause fired after the macro's last move: nothing was purged mid-macro");
            Assert.That(await bench.Host.GlobalAsync("afterMacro"), Is.Zero,
                        "and before the code after the M98 line");
            Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(1), "pause.g ran");
        });

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(1),
                        "the macro was not rerun: the deferred pause found nothing part-way");
            Assert.That(await bench.Host.GlobalAsync("afterMacro"), Is.EqualTo(1), "the job carried on after it");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)), "and finished");
        });
    }

    /// <summary>
    /// A pause during an M98 R1 macro feedholds immediately, and the resume reruns the whole
    /// macro from its first line: one M98 call, two executions
    /// </summary>
    /// TODO this might be an intermitant failure
    [Test]
    public async Task PauseDuringPausableMacroRerunsIt()
    {
        await using JobBench bench = await JobControlBench.StartAsync(prepareSd: sd =>
        {
            sd.WriteMacro("pausable-moves.g", "M98 R1\n" + SquareMacro);
            sd.WriteGCode("job.gcode", """
                G90
                G1 X50 Y50 F6000
                M98 P"0:/macros/pausable-moves.g"
                G1 X10 Y10 F6000
                G60 S3
                """);
        });

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.CanMaster.WaitForSbcPacketAsync(SbcRequest.ScheduleMove);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(1), "one execution so far");
            Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(1), "pause.g ran");
        });

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(2),
                        "the resume rewound to the M98 line and ran the macro again from the beginning");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job finished normally");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)),
                        "at its final position: the macro's absolute moves close the square either way");
        });
    }

    /// <summary>
    /// A pause during a tool change: the tool change macros are plain macros, so the pause defers
    /// past the one that is running, and the job resumes to a coherent end with the tool selected.
    /// RepRapFirmware would hold the deferred pause until the whole tool change is over; nothing
    /// here tracks that yet, so this scenario pins down only what must hold either way
    /// </summary>
    [Test]
    public async Task PauseDuringToolChange()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: """
                M563 P0 D0
                global tfreeRan = 0
                global tpreRan = 0
                global tpostRan = 0
                """ + "\nT0\n",
            prepareSd: sd =>
            {
                sd.WriteSys("tfree0.g", "set global.tfreeRan = global.tfreeRan + 1\nG90\nG1 X20 F3000\n");
                sd.WriteSys("tpre0.g", "set global.tpreRan = global.tpreRan + 1\nG90\nG1 X40 F3000\n");
                sd.WriteSys("tpost0.g", "set global.tpostRan = global.tpostRan + 1\nG90\nG1 X60 F3000\n");
                sd.WriteGCode("job.gcode", """
                    G90
                    G1 X10 Y10 F6000
                    T-1
                    T0
                    G1 X10 Y10 F6000
                    G60 S3
                    """);
            });

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.CanMaster.WaitForSbcPacketAsync(SbcRequest.ScheduleMove);
        await Task.Delay(TimeSpan.FromSeconds(1));

        // Land the pause while the tool change macros' slow moves are being made
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(1), "the deferred pause settled");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("tfreeRan"), Is.EqualTo(1), "tfree0.g ran once");
            Assert.That(await bench.Host.GlobalAsync("tpreRan"), Is.EqualTo(1), "tpre0.g ran once");
            Assert.That(await bench.Host.GlobalAsync("tpostRan"), Is.EqualTo(1), "tpost0.g ran once");
            Assert.That(await bench.Host.EvaluateRawAsync("state.currentTool"), Is.EqualTo("0"),
                        "the job ends with tool 0 selected");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "and finished normally");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)), "at its final position");
        });
    }
}
