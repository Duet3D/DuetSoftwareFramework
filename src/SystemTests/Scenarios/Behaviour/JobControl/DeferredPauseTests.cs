using System;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Behaviour.JobControl;

/// <summary>
/// Pauses commanded while a macro runs. Macros are not pausable unless they declare M98 R1, so a
/// pause during a plain macro is deferred until the job is back out of it and fires as a
/// synchronous pause; a pause during an M98 R1 macro feedholds immediately, and because a macro's
/// file position cannot be recorded, the resume rewinds to the invoking M98 line and the macro
/// runs again from the beginning. A tool change is a sequence of plain macros, so a pause during
/// it defers the same way
/// </summary>
[TestFixture]
[GCode("M25", "M98")]
public class DeferredPauseTests : BenchFixture
{
    /// <summary>Four slow edges the pause can land in, counting its runs</summary>
    /// <remarks>
    /// The second and third edges take over 2 seconds between them, so the fourth does not fit into the
    /// queue and the macro is still running while the first one is made (DDARing::CanAddMove)
    /// </remarks>
    private const string SquareMacro = """
        set global.macroRuns = global.macroRuns + 1
        G90
        G1 X50 Y20 F3000
        G1 X50 Y50 F1600
        G1 X20 Y50 F1600
        G1 X20 Y20 F6000
        """;

    /// <summary>
    /// Two pauses around a job that defers codes. A Deferred-class code is dispatched without being
    /// awaited and its handler waits for the move it was anchored to, so a stop that leaves those
    /// codes owed has to say what becomes of them. A stop that comes to rest without dropping any
    /// queued move is the case that names no purge boundary, and the pause must still settle rather
    /// than wait on codes whose anchors will never retire
    /// </summary>
    [Test]
    [Category("LongRunning")]
    public async Task PauseTwiceAroundDeferredCodes()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, $"M950 F0 C\"{Expansion.Out(Machines.DriverBoard, 3)}\" Q500", Machines.Segmented, Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("job.gcode", """
                G91
                G1 X60 F6000
                G1 X60
                M106 S1
                M106 S0.5
                M106 S0.2
                G1 X-60
                G1 X-60
                M107
                G90
                G60 S3
                """)).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.RunToPositionAsync(axis: 0, position: 30);

        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Processing);
        await bench.RunToPositionAsync(axis: 0, position: 90);

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
            Assert.That(bench.CanMaster.ScheduledSteps(Machines.XDriver), Is.Zero, "and so did the steps");
        });
    }

    /// <summary>
    /// A pause during a non-pausable macro defers: every move of the macro completes, the M25 is not
    /// answered until the macro has ended, a second M25 from another input is told that one is pending,
    /// the pause fires after the macro as a synchronous pause, and the macro is not rerun on resume
    /// </summary>
    [Test]
    [Category("LongRunning")]
    public async Task PauseDuringPlainMacroDefers()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, "global afterMacro = 0\n", Machines.Xye.Homed)
                                             .WithSd(sd =>
            {
                sd.WriteMacro("plain-moves.g", SquareMacro);
                sd.WriteGCode("job.gcode", """
                    G90
                    G1 X20 Y20 F6000
                    M98 P"0:/macros/plain-moves.g"
                    set global.afterMacro = 1
                    G1 X10 Y10 F6000
                    G60 S3
                    """);
            }).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.RunToPositionAsync(axis: 0, position: 30);

        // RRF holds the M25 in GCodeState::doingDeferredPause, and runs an M226 of the file in its place
        Task<string> pause = bench.Host.ExecuteCodeAsync("M25");
        await bench.RunToPositionAsync(axis: 0, position: 40);
        Assert.That(await bench.Host.ExecuteCodeAsync("M25", CodeChannel.Telnet), Reply.IsSuccess("Pausing is already pending"),
                    "a second M25 while the first is pending");
        Assert.That(pause.IsCompleted, Is.False, "the first M25 waits for the macro to end");

        Assert.That(await pause, Reply.IsEmpty, "the M25 reports nothing: the pause is made by the file");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(1), "the macro completed once");
            Assert.That(await bench.Host.RestorePointAsync(1), Is.EqualTo((20.0, 20.0)),
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

    /// <summary>The macro of a deferred pause runs on the input of the job</summary>
    [Test]
    public async Task DeferredPauseRunsThePauseMacroOnTheFileInput()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, "global onFile = 0", Machines.Xye.Homed)
                                             .WithSd(sd =>
        {
            sd.WriteMacro("plain.g", "set global.macroRuns = 1\nG4 P500\n");
            sd.WriteGCode("job.gcode", "M98 P\"0:/macros/plain.g\"\nG4 P0\n");
        }).StartAsync();
        int file = await bench.Host.InputIndexAsync(CodeChannel.File);
        bench.Host.Sd.WriteSys("pause.g", $"if inputs[{file}].inMacro\n    set global.onFile = 1\n");

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForGlobalAsync("macroRuns", 1);
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        Assert.That(await bench.Host.GlobalAsync("onFile"), Is.EqualTo(1));
    }

    /// <summary>
    /// A condition that has been read when the macro ends is evaluated after the resume, once the codes
    /// before it have run
    /// </summary>
    [TestCase("if var.x == 1", TestName = "{m}(if)")]
    [TestCase("while iterations < var.x", TestName = "{m}(while)")]
    public async Task DeferredPauseBeforeACondition(string condition)
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, "global met = 0", Machines.Xye.Homed)
                                             .WithSd(sd =>
        {
            sd.WriteMacro("plain.g", "set global.macroRuns = global.macroRuns + 1\nG4 P500\n");
            sd.WriteGCode("job.gcode", $"""
                M98 P"0:/macros/plain.g"
                var x = 1
                {condition}
                    set global.met = global.met + 1
                """);
        }).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForGlobalAsync("macroRuns", 1);
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.GlobalAsync("met"), Is.Zero, "the pause follows the macro");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(1), "the macro is not run again");
            Assert.That(await bench.Host.GlobalAsync("met"), Is.EqualTo(1));
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job ended normally");
        });
    }

    /// <summary>An M25 that waits for a macro of the job lets that macro move, although its own input holds the movement lock</summary>
    [Test]
    public async Task DeferredPauseFromAnInputThatHoldsTheMovementLock()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
        {
            sd.WriteSys("homeall.g", "M25\n");
            sd.WriteMacro("plain.g", "set global.macroRuns = 1\nG4 P500\nG1 X20 F6000\n");
            sd.WriteGCode("job.gcode", "M98 P\"0:/macros/plain.g\"\nG4 P0\n");
        }).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForGlobalAsync("macroRuns", 1);
        await bench.Host.ExecuteCodeAsync("G28");

        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(bench.CanMaster.MoveDistances(), Is.EqualTo(new[] { 20.0f }).Within(0.1f), "the move of the macro was made");
    }

    /// <summary>A pause that waits for a macro called in a loop leaves the iterations that are left</summary>
    [Test]
    [Category("LongRunning")]
    public async Task PauseDuringMacroInsideWhileLoop()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
        {
            sd.WriteMacro("plain-moves.g", SquareMacro);
            sd.WriteGCode("job.gcode", """
                G90
                G1 X20 Y20 F6000
                while iterations < 2
                    M98 P"0:/macros/plain-moves.g"
                G1 X10 Y10 F6000
                G60 S3
                """);
        }).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.RunToPositionAsync(axis: 0, position: 30);
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(1), "the pause follows the first iteration");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(2), "the second iteration ran");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)), "final position");
        });
    }

    /// <summary>
    /// A pause during an M98 R1 macro feedholds immediately, and the resume reruns the whole
    /// macro from its first line: one M98 call, two executions
    /// </summary>
    [Test]
    [Category("LongRunning")]
    public async Task PauseDuringPausableMacroRerunsIt()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd =>
        {
            sd.WriteMacro("pausable-moves.g", "M98 R1\n" + SquareMacro);
            sd.WriteGCode("job.gcode", """
                G90
                G1 X20 Y20 F6000
                M98 P"0:/macros/pausable-moves.g"
                G1 X10 Y10 F6000
                G60 S3
                """);
        }).StartAsync();

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.RunToPositionAsync(axis: 0, position: 30);

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
    [Category("LongRunning")]
    public async Task PauseDuringToolChange()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, """
                M563 P0 D0
                global tfreeRan = 0
                global tpreRan = 0
                global tpostRan = 0
                """, "\nT0", Machines.Xye.Homed).WithSd(sd =>
            {
                sd.WriteSys("tfree0.g", "set global.tfreeRan = global.tfreeRan + 1\nG91\nG1 X30 F1500\nM400\n");
                sd.WriteSys("tpre0.g", "set global.tpreRan = global.tpreRan + 1\nG91\nG1 X30 F6000\nM400\n");
                sd.WriteSys("tpost0.g", "set global.tpostRan = global.tpostRan + 1\nG91\nG1 X30 F6000\nM400\n");
                sd.WriteGCode("job.gcode", """
                    G90
                    G1 X10 Y10 F6000
                    T-1
                    T0
                    G1 X10 Y10 F6000
                    G60 S3
                    """);
            }).StartAsync();

        // config.g runs `T0`
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("tfreeRan"), Is.EqualTo(0), "tfree0.g hasn't run");
            Assert.That(await bench.Host.GlobalAsync("tpreRan"), Is.EqualTo(1), "tpre0.g ran once");
            Assert.That(await bench.Host.GlobalAsync("tpostRan"), Is.EqualTo(1), "tpost0.g ran once");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0),
                        "the config ends with tool 0 selected");
        });

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForGlobalAsync("tfreeRan", 1);

        // Land the pause while tfree0.g's slow move is being made
        await bench.Host.ExecuteCodeAsync("M25");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(1), "the deferred pause settled");
            Assert.That(await bench.Host.GlobalAsync("tfreeRan"), Is.EqualTo(1), "tfree0.g ran once");
            Assert.That(await bench.Host.GlobalAsync("tpreRan"), Is.EqualTo(1), "tpre0.g ran once");
            Assert.That(await bench.Host.GlobalAsync("tpostRan"), Is.EqualTo(1), "tpost0.g ran once");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(-1),
                        "the pause after T-1");
        });

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("tfreeRan"), Is.EqualTo(1), "tfree0.g ran once");
            Assert.That(await bench.Host.GlobalAsync("tpreRan"), Is.EqualTo(2), "tpre0.g ran twice");
            Assert.That(await bench.Host.GlobalAsync("tpostRan"), Is.EqualTo(2), "tpost0.g ran twice");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.CurrentTool), Is.EqualTo(0),
                        "the job ends with tool 0 selected");
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "and finished normally");
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)), "at its final position");
        });
    }
}
