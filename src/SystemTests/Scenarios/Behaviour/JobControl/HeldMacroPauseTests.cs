using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Behaviour.JobControl;

/// <summary>
/// Pauses that stop the job inside a macro and carry on in it after the resume: a pause a restartable
/// macro of the job asks for, and M25 in any macro of the job
/// </summary>
/// <remarks>
/// RRF GCodes::DoSynchronousPause (GCodes.cpp) keeps the machine state of the macro. M25 reaches it without
/// the CanRestartMacro check of M226, M600 and M601 (GCodes2.cpp)
/// </remarks>
[TestFixture]
[GCode("M25", "M26", "M98", "M226", "M600", "M601")]
public class HeldMacroPauseTests : BenchFixture
{
    /// <summary>How long a job that must not read on is given to prove otherwise</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>Globals that count how far the job and its macros got</summary>
    private const string Progress = """
        global afterPause = 0
        global macroEnd = 0
        global jobAfter = 0
        """;

    /// <summary>A job that calls a macro and has a line to run after it</summary>
    private const string CallingJob = """
        G90
        G1 X20 Y20 F6000
        M98 P"0:/macros/outer.g"
        set global.jobAfter = global.jobAfter + 1
        G1 X10 Y10 F6000
        G60 S3
        """;

    /// <summary>A macro with a code in the middle and something to see on both sides of it</summary>
    private static string MacroAround(string code, string head = "") => $"""
        {head}
        set global.macroRuns = global.macroRuns + 1
        G90
        G1 X50 Y20 F6000
        {code}
        set global.afterPause = global.afterPause + 1
        G1 X50 Y50 F6000
        set global.macroEnd = global.macroEnd + 1
        """;

    private static Task<Bench> StartAsync(string macro, string job = CallingJob, params ConfigPart[] parts)
        => Bench.With([Machines.Xye, JobMacros.Instrumented, Progress, .. parts, Machines.Xye.Homed]).WithSd(sd =>
        {
            sd.WriteMacro("outer.g", macro);
            sd.WriteGCode("job.gcode", job);
        }).StartAsync();

    private static Task<string> StartJobAsync(Bench bench) => bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");

    private static async Task ResumeToTheEndAsync(Bench bench)
    {
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
    }

    /// <summary>The job is paused inside its macro, with nothing behind the pause run</summary>
    private static Task AssertPausedInMacroAsync(Bench bench, int pauses = 1) => Assert.MultipleAsync(async () =>
    {
        Assert.That(await bench.Host.RestorePointAsync(1), Is.EqualTo((50.0, 20.0)), "the pause point is inside the macro");
        Assert.That(await bench.Host.GlobalAsync("afterPause"), Is.Zero, "the code after the pause has not run");
        Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(pauses), "pause.g ran");
        Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.Zero, "the job is not out of the macro");
    });

    /// <summary>The macro ran once from start to end and the job finished after it</summary>
    private static Task AssertMacroRanOnceAsync(Bench bench) => Assert.MultipleAsync(async () =>
    {
        Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(1), "the macro is not run again");
        Assert.That(await bench.Host.GlobalAsync("afterPause"), Is.EqualTo(1), "the code after the pause ran once");
        Assert.That(await bench.Host.GlobalAsync("macroEnd"), Is.EqualTo(1), "the macro ran to its end");
        Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.EqualTo(1), "the job carried on after the macro");
        Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job ended normally");
        Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((10.0, 10.0)), "at its final position");
        Assert.That(bench.CanMaster.ScheduledSteps(Machines.XDriver), Is.EqualTo(10 * Machines.XyStepsPerMm), "X steps");
        Assert.That(bench.CanMaster.ScheduledSteps(Machines.YDriver), Is.EqualTo(10 * Machines.XyStepsPerMm), "Y steps");
    });

    /// <summary>A pause the macro asks for lands on its line, and the resume carries on at the next</summary>
    [TestCase("M25", "", TestName = "{m}(M25, plain macro)")]
    [TestCase("M25", "M98 R1", TestName = "{m}(M25, restartable macro)")]
    [TestCase("M226", "M98 R1", TestName = "{m}(M226, restartable macro)")]
    [TestCase("M601", "M98 R1", TestName = "{m}(M601, restartable macro)")]
    public async Task PauseAskedForByTheMacro(string code, string head)
    {
        await using Bench bench = await StartAsync(MacroAround(code, head));

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await AssertPausedInMacroAsync(bench);

        await ResumeToTheEndAsync(bench);
        await AssertMacroRanOnceAsync(bench);
    }

    /// <summary>M600 in a restartable macro runs filament-change.g</summary>
    [Test]
    public async Task M600InARestartableMacro()
    {
        await using Bench bench = await StartAsync(MacroAround("M600", "M98 R1"));

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.GlobalAsync("filChangeRan"), Is.EqualTo(1));
        await AssertPausedInMacroAsync(bench, pauses: 0);

        await ResumeToTheEndAsync(bench);
        await AssertMacroRanOnceAsync(bench);
    }

    /// <summary>M226, M600 and M601 in a macro that cannot be restarted wait for its end (RRF GCodes2.cpp case 226)</summary>
    [TestCase("M226")]
    [TestCase("M600")]
    [TestCase("M601")]
    public async Task OtherPausesWaitForAPlainMacro(string code)
    {
        await using Bench bench = await StartAsync(MacroAround(code));

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.RestorePointAsync(1), Is.EqualTo((50.0, 50.0)), "the pause follows the macro");
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.Zero, "and precedes the code after it");
        });

        await ResumeToTheEndAsync(bench);
        await AssertMacroRanOnceAsync(bench);
    }

    /// <summary>The pause macro may move and call macros although the macro of the job is held</summary>
    [Test]
    public async Task PauseMacroCallsAMacro()
    {
        await using Bench bench = await StartAsync(MacroAround("M25"), parts: "global nested = 0");
        bench.Host.Sd.WriteMacro("nested.g", "set global.nested = global.nested + 1\nG1 X5 F6000\n");
        bench.Host.Sd.WriteSys("pause.g", "set global.pauseRan = global.pauseRan + 1\nM98 P\"0:/macros/nested.g\"\n");

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.GlobalAsync("nested"), Is.EqualTo(1));
        await AssertPausedInMacroAsync(bench);

        await ResumeToTheEndAsync(bench);
        await AssertMacroRanOnceAsync(bench);
    }

    /// <summary>A macro that pauses twice is paused twice</summary>
    [Test]
    public async Task PausesTwice()
    {
        await using Bench bench = await StartAsync(MacroAround("M25") + "\nM25\nset global.macroEnd = 2");

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await AssertPausedInMacroAsync(bench);

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForPauseAtAsync(50.0, 50.0);
        Assert.That(await bench.Host.GlobalAsync("macroEnd"), Is.EqualTo(1), "the code after the second M25 has not run");

        await ResumeToTheEndAsync(bench);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroEnd"), Is.EqualTo(2));
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.EqualTo(1));
            Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(2));
        });
    }

    /// <summary>The macro that called the macro that paused is held too, and carries on after it</summary>
    [Test]
    public async Task NestedMacro()
    {
        await using Bench bench = await StartAsync("""
            set global.macroRuns = global.macroRuns + 1
            M98 P"0:/macros/inner.g"
            set global.macroEnd = global.macroEnd + 1
            """);
        bench.Host.Sd.WriteMacro("inner.g", "G90\nG1 X50 Y20 F6000\nM25\nset global.afterPause = global.afterPause + 1\n");

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await AssertPausedInMacroAsync(bench);
        Assert.That(await bench.Host.GlobalAsync("macroEnd"), Is.Zero);

        await ResumeToTheEndAsync(bench);
        await AssertMacroRanOnceAsync(bench);
    }

    /// <summary>A pause on the last line of a macro leaves the job inside it, so nothing after the call runs</summary>
    [Test]
    public async Task PauseOnTheLastLine()
    {
        await using Bench bench = await StartAsync("set global.macroRuns = global.macroRuns + 1\nG90\nG1 X50 Y20 F6000\nM25\n", """
            G90
            M98 P"0:/macros/outer.g"
            if global.macroRuns == 1
                set global.jobAfter = global.jobAfter + 1
            """);

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await AssertPausedInMacroAsync(bench);

        await ResumeToTheEndAsync(bench);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(1));
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.EqualTo(1));
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1));
        });
    }

    /// <summary>A condition behind the pause is evaluated after the resume</summary>
    [TestCase("if global.resumeRan == 1", TestName = "{m}(if)")]
    [TestCase("while iterations < global.resumeRan", TestName = "{m}(while)")]
    public async Task ConditionBehindThePause(string condition)
    {
        await using Bench bench = await StartAsync($"""
            set global.macroRuns = global.macroRuns + 1
            G90
            G1 X50 Y20 F6000
            M25
            {condition}
                set global.afterPause = global.afterPause + 1
            """);

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await AssertPausedInMacroAsync(bench);

        await ResumeToTheEndAsync(bench);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("afterPause"), Is.EqualTo(1));
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.EqualTo(1));
        });
    }

    /// <summary>The macro keeps its local variables, its loop and its modes across the pause</summary>
    [Test]
    public async Task MacroKeepsItsState()
    {
        await using Bench bench = await StartAsync("""
            set global.macroRuns = global.macroRuns + 1
            var x = 0
            G90
            G1 X50 Y20 F6000
            G91
            while iterations < 3
                set var.x = var.x + 1
                if iterations == 1
                    M25
                G1 X1
            set global.afterPause = var.x
            """);

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.RestorePointAsync(1), Is.EqualTo((51.0, 20.0)), "one iteration of the loop was made");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForGlobalAsync("afterPause", 3);
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.That(bench.CanMaster.ScheduledSteps(Machines.XDriver), Is.EqualTo(10 * Machines.XyStepsPerMm), "X steps");
        Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(1));
    }

    /// <summary>Cancelling a job that is paused in a macro drops the macro, and another job can run</summary>
    [Test]
    public async Task CancelWhilePausedInAMacro()
    {
        await using Bench bench = await StartAsync(MacroAround("M25"));
        bench.Host.Sd.WriteGCode("second.gcode", "set global.jobAfter = 5\n");

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("cancelRan"), Is.EqualTo(1));
            Assert.That(await bench.Host.GlobalAsync("afterPause"), Is.Zero, "the rest of the macro is dropped");
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.Zero);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Inputs[CodeChannel.File]!.StackDepth), Is.Zero, "the macro's level is popped");
        });

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/second.gcode\"");
        await bench.Host.WaitForGlobalAsync("jobAfter", 5);
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
    }

    /// <summary>An abort in the pause macro ends the job, with the macro it was paused in</summary>
    [Test]
    public async Task AbortInThePauseMacro()
    {
        await using Bench bench = await StartAsync(MacroAround("M25"));
        bench.Host.Sd.WriteSys("pause.g", "set global.pauseRan = global.pauseRan + 1\nabort\n");

        await StartJobAsync(bench);
        await bench.Host.WaitForGlobalAsync("pauseRan", 1);
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("afterPause"), Is.Zero, "the rest of the macro is dropped");
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.Zero);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Inputs[CodeChannel.File]!.StackDepth), Is.Zero, "the macro's level is popped");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileAborted), Is.True);
        });
    }

    /// <summary>M26 while the job is paused in a macro drops the macro and moves the job</summary>
    [Test]
    public async Task M26WhilePausedInAMacro()
    {
        const string Head = "G90\nM98 P\"0:/macros/outer.g\"\nset global.macroEnd = 5\n";
        await using Bench bench = await StartAsync(MacroAround("M25"), Head + "set global.jobAfter = global.jobAfter + 1\n");

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await bench.Host.ExecuteCodeAsync($"M26 S{Head.Length}");
        Assert.That(await bench.Host.ExecuteCodeAsync("M27"), Does.Contain($"byte {Head.Length}/"), "the job stands at the offset at once");

        await ResumeToTheEndAsync(bench);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("afterPause"), Is.Zero, "the rest of the macro is dropped");
            Assert.That(await bench.Host.GlobalAsync("macroEnd"), Is.Zero, "nothing before the offset is run");
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.EqualTo(1), "the job carried on at the offset");
        });
    }

    /// <summary>A job that M26 moved out of the macro it is paused in can be cancelled</summary>
    [Test]
    public async Task CancelAfterM26WhilePausedInAMacro()
    {
        await using Bench bench = await StartAsync(MacroAround("M25"));

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await bench.Host.ExecuteCodeAsync("M26 S0");
        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("cancelRan"), Is.EqualTo(1));
            Assert.That(await bench.Host.GlobalAsync("afterPause"), Is.Zero);
            Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(1));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Inputs[CodeChannel.File]!.StackDepth), Is.Zero, "the macro's level is popped");
        });
    }

    /// <summary>M26 drops an M600 that waits for the end of the macro with the macro, so a later pause is made</summary>
    [Test]
    public async Task M26DropsTheM600OfTheMacro()
    {
        const string Head = "G90\nM98 P\"0:/macros/outer.g\"\n";
        await using Bench bench = await StartAsync(MacroAround("M600\nM25"),
                                                   Head + "M98 P\"0:/macros/second.g\"\nset global.jobAfter = global.jobAfter + 1\n");
        bench.Host.Sd.WriteMacro("second.g", "set global.macroEnd = 7\nG4 P500\nset global.macroEnd = 8\n");

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await bench.Host.ExecuteCodeAsync($"M26 S{Head.Length}");
        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForGlobalAsync("macroEnd", 7);
        Assert.That((await bench.Host.ExecuteCodeAsync("M25")).Trim(), Is.Empty, "the M25 is deferred to the end of the second macro");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroEnd"), Is.EqualTo(8));
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.Zero);
            Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(2));
            Assert.That(await bench.Host.GlobalAsync("filChangeRan"), Is.Zero);
        });

        await ResumeToTheEndAsync(bench);
        Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.EqualTo(1));
    }

    /// <summary>What a macro has behind its M25 to fail at once when the M25 has completed: a line that cannot be parsed, just out of reach of the read-ahead</summary>
    private static readonly string UnreadableOnceThePauseIsMade
        = string.Concat(Enumerable.Repeat("G4 P0\n", new DuetControlServer.Settings().BufferedMacroCodes - 1)) + "G1 X{1\n";

    /// <summary>A macro that fails while the job is paused in it ends, and the job stays paused and carries on behind it</summary>
    /// <param name="after">What the job has behind the macro</param>
    [TestCase("set global.jobAfter = global.jobAfter + 1\n")]
    [TestCase("")]
    public async Task MacroEndsWhileTheJobIsPaused(string after)
    {
        await using Bench bench = await StartAsync($"set global.macroRuns = 1\nG90\nG1 X50 Y20 F6000\nM25\n{UnreadableOnceThePauseIsMade}",
                                                   $"G90\nM98 P\"0:/macros/outer.g\"\n{after}");

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await bench.Host.WaitForModelAsync(model => model.Inputs[CodeChannel.File]!.StackDepth, 0, "the macro has ended");
        await Task.Delay(Settle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.Zero, "the job does not read on");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.Status), Is.EqualTo(MachineStatus.Paused));
        });

        await ResumeToTheEndAsync(bench);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(1));
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.EqualTo(after.Length > 0 ? 1 : 0));
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1), "the job ended normally");
        });
    }

    /// <summary>An M600 that waits for a macro which fails while the job is paused in it is made when the job is resumed</summary>
    [Test]
    public async Task MacroWithAPendingM600EndsWhileTheJobIsPaused()
    {
        await using Bench bench = await StartAsync($"set global.macroRuns = 1\nG90\nG1 X50 Y20 F6000\nM600\nM25\n{UnreadableOnceThePauseIsMade}");

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await bench.Host.WaitForModelAsync(model => model.Inputs[CodeChannel.File]!.StackDepth, 0, "the macro has ended");

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForGlobalAsync("filChangeRan", 1);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.Zero);

        await ResumeToTheEndAsync(bench);
        Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.EqualTo(1));
    }

    /// <summary>M25 in a tool change macro pauses in it, other inputs can move meanwhile, and the resume ends the tool change</summary>
    /// <remarks>RRF releases the locks of a paused file (GCodes::StartNextGCode)</remarks>
    [Test]
    public async Task PauseInAToolChangeMacro()
    {
        await using Bench bench = await StartAsync("", """
            G90
            G1 X20 Y20 F6000
            T0
            set global.jobAfter = global.jobAfter + 1
            """, "M563 P0 D0\nglobal postRan = 0");
        bench.Host.Sd.WriteSys("tpre0.g", MacroAround("M25"));
        bench.Host.Sd.WriteSys("tpost0.g", "set global.postRan = global.postRan + 1\n");

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await AssertPausedInMacroAsync(bench);
        Assert.That((await bench.Host.ExecuteCodeAsync("G1 X30 F6000")).Trim(), Is.Empty, "another input may move");

        await ResumeToTheEndAsync(bench);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroEnd"), Is.EqualTo(1), "tpre0.g ran to its end");
            Assert.That(await bench.Host.GlobalAsync("postRan"), Is.EqualTo(1), "tpost0.g ran");
            Assert.That(await bench.Host.ReadModelAsync(model => model.State.CurrentTool), Is.Zero, "the tool is selected");
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.EqualTo(1));
        });
    }

    /// <summary>A move that waits for the resume to move the head back is made after the tool change, not before its macro carries on</summary>
    [Test]
    public async Task AMoveDuringTheResumeWaitsForTheToolChange()
    {
        await using Bench bench = await StartAsync("", """
            G90
            G1 X20 Y20 F6000
            T0
            set global.jobAfter = global.jobAfter + 1
            """, "M563 P0 D0");
        bench.Host.Sd.WriteSys("tpre0.g", "set global.macroRuns = 1\nG90\nG1 X50 Y20 F6000\nM25\nG91\nG1 X10\nG60 S3\nG90\n");

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Task<string> resume = bench.Host.ExecuteCodeAsync("M24", CodeChannel.Telnet);
        await bench.Host.WaitForGlobalAsync("resumeRan", 1);
        await bench.Host.ExecuteCodeAsync("G1 X100 F6000");
        await resume;
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.RestorePointAsync(3), Is.EqualTo((60.0, 20.0)), "the macro carried on at the pause point");
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.EqualTo(1));
        });
    }

    /// <summary>A pause in a macro that both streams of a forked job are in holds it on both of them</summary>
    [TestCase("M25", "")]
    [TestCase("M226", "M98 R1")]
    public async Task ForkedJob(string code, string head)
    {
        await using Bench bench = await StartAsync($"""
            {head}
            set global.macroRuns = global.macroRuns + 1
            {code}
            G4 P100
            set global.afterPause = global.afterPause + 1
            """, """
            M606 S1
            M98 P"0:/macros/outer.g"
            set global.jobAfter = global.jobAfter + 1
            """);

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await Task.Delay(Settle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("afterPause"), Is.Zero, "neither stream carries on");
            Assert.That(await bench.Host.GlobalAsync("pauseRan"), Is.EqualTo(1));
        });

        int macroRuns = await bench.Host.GlobalAsync("macroRuns");
        await ResumeToTheEndAsync(bench);
        Assert.Multiple(async () =>
        {
            // The streams count in the same globals, so a count may be one less than the runs
            Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(macroRuns), "the macro is not run again");
            Assert.That(await bench.Host.GlobalAsync("afterPause"), Is.Positive);
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.Positive);
            Assert.That(await bench.Host.GlobalAsync("stopRan"), Is.EqualTo(1));
        });
    }

    /// <summary>Cancelling a forked job that is paused in a macro drops the macro of both streams</summary>
    [Test]
    public async Task CancelAForkedJob()
    {
        await using Bench bench = await StartAsync("set global.macroRuns = global.macroRuns + 1\nM25\nG4 P100\nset global.afterPause = global.afterPause + 1\n", """
            M606 S1
            M98 P"0:/macros/outer.g"
            set global.jobAfter = global.jobAfter + 1
            """);

        await StartJobAsync(bench);
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        await bench.Host.ExecuteCodeAsync("M0");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("afterPause"), Is.Zero);
            Assert.That(await bench.Host.GlobalAsync("jobAfter"), Is.Zero);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Inputs[CodeChannel.File]!.StackDepth), Is.Zero);
            Assert.That(await bench.Host.ReadModelAsync(model => model.Inputs[CodeChannel.File2]!.StackDepth), Is.Zero);
        });
    }
}
