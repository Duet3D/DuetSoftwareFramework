using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>
/// M120 and M121, pushing and popping the channel's input state. A pushed level belongs to the file that pushed it: a
/// macro, a job file or the channel's own interactive level. M121 pops only a level of its own file, and the levels a
/// file leaves open are discarded when it ends. Macro levels and M120 levels share one stack, whose depth is
/// inputs[].stackDepth
/// </summary>
/// <remarks>
/// RRF truth: GCodes2.cpp cases 120 and 121, GCodeBuffer::PushState and PopState, and the GCodeMachineState copy
/// constructor. RRF replies OK to an overflow or underflow and reports it as a message, and an M121 in a macro with no
/// level of its own pops the macro's level; DSF refuses both with an error reply instead
/// </remarks>
[TestFixture]
[GCode("M120", "M121")]
public class M120M121Tests : BenchFixture
{
    /// <summary>
    /// M120 pushes the channel's interpreter state and M121 restores it: the relativity flags,
    /// the distance unit, the inverse time flag and the feed rate all return to their pushed
    /// values
    /// </summary>
    /// <remarks>
    /// RRF GCodes2.cpp case 120/121 calls Push/Pop, and GCodeMachineState.cpp CopyStateFrom lists
    /// what the pop restores: selectedPlane, drivesRelative, axesRelative, feedRate,
    /// volumetricExtrusion, usingInches and inverseTimeMode
    /// </remarks>
    [Test]
    public async Task M120AndM121PushAndPopInputState()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);
        int http = await bench.Host.InputIndexAsync();

        // A known baseline, then push it
        await bench.Host.ExecuteCodeAsync("G90");
        await bench.Host.ExecuteCodeAsync("M82");
        await bench.Host.ExecuteCodeAsync("G21");
        await bench.Host.ExecuteCodeAsync("G94");
        await bench.Host.ExecuteCodeAsync("G1 F3000");
        Assert.That((await bench.Host.ExecuteCodeAsync("M120")).Trim(), Is.Empty, "M120 replies nothing");
        Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.EqualTo(1), "M120 pushes one level");

        // Change every piece of state the pop must restore
        await bench.Host.ExecuteCodeAsync("G91");
        await bench.Host.ExecuteCodeAsync("M83");
        await bench.Host.ExecuteCodeAsync("G20");
        await bench.Host.ExecuteCodeAsync("G1 F600");
        await bench.Host.ExecuteCodeAsync("G93");
        InputChannel? input = await bench.Host.ReadModelAsync(model => model.Inputs[http]);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(input!.AxesRelative, Is.True,
                        "G91 after M120 changes inputs[].axesRelative");
            Assert.That(input!.DrivesRelative, Is.True,
                        "M83 after M120 changes inputs[].drivesRelative");
            Assert.That(input!.DistanceUnit, Is.EqualTo(DistanceUnit.Inch),
                        "G20 after M120 changes inputs[].distanceUnit");
            Assert.That(input!.FeedRate, Is.EqualTo(600.0).Within(1e-3),
                        "G1 F600 after M120 changes inputs[].feedRate");
            Assert.That(input!.InverseTimeMode, Is.True,
                        "G93 after M120 changes inputs[].inverseTimeMode");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M121")).Trim(), Is.Empty, "M121 replies nothing");
        input = await bench.Host.ReadModelAsync(model => model.Inputs[http]);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(input!.AxesRelative, Is.False,
                        "M121 restores inputs[].axesRelative (RRF GCodeMachineState.cpp CopyStateFrom)");
            Assert.That(input!.DrivesRelative, Is.False,
                        "M121 restores inputs[].drivesRelative (RRF GCodeMachineState.cpp CopyStateFrom)");
            Assert.That(input!.DistanceUnit, Is.EqualTo(DistanceUnit.MM),
                        "M121 restores inputs[].distanceUnit (RRF GCodeMachineState.cpp CopyStateFrom)");
            Assert.That(input!.FeedRate, Is.EqualTo(3000.0).Within(1e-3),
                        "M121 restores inputs[].feedRate (RRF GCodeMachineState.cpp CopyStateFrom)");
            Assert.That(input!.InverseTimeMode, Is.False,
                        "M121 restores inputs[].inverseTimeMode (RRF GCodeMachineState.cpp CopyStateFrom)");
            Assert.That(input!.StackDepth, Is.Zero, "M121 pops the level M120 pushed");
        });
    }

    /// <summary>The level M120 pushes starts with the state of the level below it, inverse time mode included</summary>
    /// <remarks>RRF truth: the GCodeMachineState copy constructor keeps inverseTimeMode when withinSameFile</remarks>
    [Test]
    public async Task M120KeepsTheCurrentState()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync("G91");
        await bench.Host.ExecuteCodeAsync("G93");
        await bench.Host.ExecuteCodeAsync("M120");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadInputAsync(input => input.AxesRelative), Is.True, "the pushed level is still relative");
            Assert.That(await bench.Host.ReadInputAsync(input => input.InverseTimeMode), Is.True, "and still in inverse time mode");
        });
    }

    /// <summary>M121 with no level to pop is an error and leaves the state as it was</summary>
    /// <remarks>RRF truth: GCodeBuffer::PopState fails at the base level. RRF reports it as a message and replies OK</remarks>
    [Test]
    public async Task M121WithoutM120IsAnError()
    {
        await using Bench bench = await Bench.StartAsync();
        await bench.Host.ExecuteCodeAsync("G91");

        Assert.That((await bench.Host.ExecuteCodeAsync("M121")).TrimEnd(), Does.StartWith("Error: M121:").And.Contain("stack underflow"));
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadInputAsync(input => input.AxesRelative), Is.True, "nothing was restored");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "nothing was popped");
        });
    }

    /// <summary>
    /// M121 in a macro with no M120 of its own is an error: it cannot reach the level its caller pushed. The macro carries
    /// on, and the caller's M121 still pops the caller's level
    /// </summary>
    /// <remarks>RRF pops the macro's own level instead, which ends the macro but leaves its buffered lines to run</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M121InAMacroCannotPopTheCallersLevel()
    {
        await using Bench bench = await Bench.StartAsync("global depth = -1\nglobal relative = false\nglobal after = 0");
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-m121-caller.g", $"""
            M121
            set global.depth = inputs[{http}].stackDepth
            set global.relative = inputs[{http}].axesRelative
            set global.after = 1
            """);

        await bench.Host.ExecuteCodeAsync("G90");
        await bench.Host.ExecuteCodeAsync("M120");
        await bench.Host.ExecuteCodeAsync("G91");
        await bench.Host.ExecuteCodeAsync("M98 P\"drt-m121-caller.g\"");
        string error = await bench.Host.WaitForMessageAsync(MessageType.Error, "stack underflow");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(error, Does.Contain("M121"), "the error names M121");
            Assert.That(await bench.Host.GlobalAsync("after"), Is.EqualTo(1), "the macro carried on after the error");
            Assert.That(await bench.Host.GlobalAsync("depth"), Is.EqualTo(2), "the caller's level and the macro's are both still there");
            Assert.That(await bench.Host.GlobalFlagAsync("relative"), Is.True, "nothing was restored inside the macro");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.EqualTo(1), "the caller's level survives the macro");
            Assert.That(await bench.Host.ReadInputAsync(input => input.AxesRelative), Is.True, "and so does the caller's G91");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M121")).Trim(), Is.Empty, "the caller's own M121 pops its level");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadInputAsync(input => input.AxesRelative), Is.False, "restoring the G90 pushed before the macro");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero);
        });
    }

    /// <summary>An M120 and M121 pair inside a macro push and pop a level above the macro's own</summary>
    /// <remarks>RRF truth: PushState(true) stacks on the macro's machine state and PopState(true) pops back to it</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M120AndM121InAMacroPushAndPopAboveTheMacro()
    {
        await using Bench bench = await Bench.StartAsync("""
            global pushedDepth = -1
            global poppedDepth = -1
            global pushedInMacro = false
            global relative = true
            """);
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-m120-pair.g", $"""
            M120
            G91
            set global.pushedDepth = inputs[{http}].stackDepth
            set global.pushedInMacro = inputs[{http}].inMacro
            M121
            set global.poppedDepth = inputs[{http}].stackDepth
            set global.relative = inputs[{http}].axesRelative
            """);

        await bench.Host.ExecuteCodeAsync("G90");
        Assert.That((await bench.Host.ExecuteCodeAsync("M98 P\"drt-m120-pair.g\"")).Trim(), Is.Empty);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("pushedDepth"), Is.EqualTo(2), "M120 in a macro stacks on the macro's level");
            Assert.That(await bench.Host.GlobalFlagAsync("pushedInMacro"), Is.True, "a level pushed in a macro is still in the macro");
            Assert.That(await bench.Host.GlobalAsync("poppedDepth"), Is.EqualTo(1), "M121 pops back to the macro's level");
            Assert.That(await bench.Host.GlobalFlagAsync("relative"), Is.False, "restoring the macro's G90");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero);
        });
    }

    /// <summary>
    /// stackDepth counts macro levels and M120 levels together, and inMacro is set for any level a macro is under,
    /// including one pushed by M120 inside it
    /// </summary>
    /// <remarks>
    /// RRF truth: GetStackDepth counts every machine state above the base, and the GCodeMachineState copy constructor
    /// copies doingFileMacro into an M120 level
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task StackDepthCountsMacroAndM120LevelsTogether()
    {
        await using Bench bench = await Bench.StartAsync("""
            global macroDepth = -1
            global macroInMacro = false
            global pushedDepth = -1
            global pushedInMacro = false
            """);
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-depth.g", $"""
            set global.macroDepth = inputs[{http}].stackDepth
            set global.macroInMacro = inputs[{http}].inMacro
            M120
            set global.pushedDepth = inputs[{http}].stackDepth
            set global.pushedInMacro = inputs[{http}].inMacro
            M121
            """);

        await bench.Host.ExecuteCodeAsync("M120");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.EqualTo(1));
            Assert.That(await bench.Host.ReadInputAsync(input => input.InMacro), Is.False, "M120 outside a macro is not in a macro");
        });

        await bench.Host.ExecuteCodeAsync("M98 P\"drt-depth.g\"");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroDepth"), Is.EqualTo(2), "the macro stacks on the caller's M120 level");
            Assert.That(await bench.Host.GlobalFlagAsync("macroInMacro"), Is.True);
            Assert.That(await bench.Host.GlobalAsync("pushedDepth"), Is.EqualTo(3), "and the macro's M120 on the macro");
            Assert.That(await bench.Host.GlobalFlagAsync("pushedInMacro"), Is.True);
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.EqualTo(1), "back at the caller's M120 level");
            Assert.That(await bench.Host.ReadInputAsync(input => input.InMacro), Is.False);
        });
    }

    /// <summary>
    /// An M120 left open in a macro is discarded when the macro ends: the caller gets its own state back, and has no
    /// level left for an M121 to pop
    /// </summary>
    /// <remarks>RRF truth: the end of a macro pops every machine state down to and including the macro's own</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M120LeftOpenInAMacroIsDiscardedWhenItEnds()
    {
        await using Bench bench = await Bench.StartAsync("global depth = -1");
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-m120-open.g", $"""
            M120
            G91
            M120
            set global.depth = inputs[{http}].stackDepth
            """);

        await bench.Host.ExecuteCodeAsync("G90");
        Assert.That((await bench.Host.ExecuteCodeAsync("M98 P\"drt-m120-open.g\"")).Trim(), Is.Empty, "leaving a level open is not an error");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("depth"), Is.EqualTo(3), "the macro had two levels of its own open");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "both were discarded with the macro");
            Assert.That(await bench.Host.ReadInputAsync(input => input.AxesRelative), Is.False, "the caller's G90 is restored");
        });

        Assert.That((await bench.Host.ExecuteCodeAsync("M121")).TrimEnd(), Does.StartWith("Error: M121:").And.Contain("stack underflow"),
                    "the caller has no level of its own to pop");
    }

    /// <summary>M121 restores the macro restartable flag M120 saved, undoing an M98 R between them</summary>
    /// <remarks>RRF truth: the GCodeMachineState copy constructor copies macroRestartable when withinSameFile</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M121RestoresTheRestartableFlag()
    {
        await using Bench bench = await Bench.StartAsync();

        await bench.Host.ExecuteCodeAsync("M98 R1");
        await bench.Host.ExecuteCodeAsync("M120");
        Assert.That(await bench.Host.ReadInputAsync(input => input.MacroRestartable), Is.True, "the pushed level keeps the flag");
        await bench.Host.ExecuteCodeAsync("M98 R0");
        Assert.That(await bench.Host.ReadInputAsync(input => input.MacroRestartable), Is.False);
        await bench.Host.ExecuteCodeAsync("M121");
        Assert.That(await bench.Host.ReadInputAsync(input => input.MacroRestartable), Is.True, "M121 restores the flag M120 saved");
    }

    /// <summary>The stack holds a fixed number of levels; M120 beyond it is an error and pushes nothing</summary>
    /// <remarks>RRF truth: GCodeBuffer::PushState fails once GetStackDepth reaches MaxStackDepth</remarks>
    [Test]
    public async Task M120BeyondTheStackLimitIsAnError()
    {
        await using Bench bench = await Bench.StartAsync();
        await bench.Host.ExecuteCodeAsync("G90");

        for (int i = 0; i < InterpreterStateStack.MaxDepth; i++)
        {
            Assert.That((await bench.Host.ExecuteCodeAsync("M120")).Trim(), Is.Empty, $"push {i + 1} fits");
        }
        await bench.Host.ExecuteCodeAsync("G91");

        Assert.That((await bench.Host.ExecuteCodeAsync("M120")).TrimEnd(), Does.StartWith("Error: M120:").And.Contain("stack overflow"));
        Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.EqualTo(InterpreterStateStack.MaxDepth), "nothing was pushed");

        await bench.Host.ExecuteCodeAsync("M121");
        Assert.That(await bench.Host.ReadInputAsync(input => input.AxesRelative), Is.False,
                    "the top level is still the last one pushed, so the G91 after the refused M120 is undone");
    }

    /// <summary>
    /// An overflow in a macro aborts every macro on the channel, so that a loop or a recursion pushing levels stops rather
    /// than failing on every pass. The levels the macros pushed go with them; those of the channel's own level stay
    /// </summary>
    /// <remarks>RRF truth: GCodes::Push reports the overflow and calls AbortPrint, which unwinds every file on the channel</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task OverflowInAMacroAbortsIt()
    {
        await using Bench bench = await Bench.StartAsync("global pushes = 0\nglobal after = 0");
        bench.Host.Sd.WriteSys("drt-m120-loop.g", """
            while iterations < 20
              M120
              set global.pushes = global.pushes + 1
            set global.after = 1
            """);

        await bench.Host.ExecuteCodeAsync("M120");
        await bench.Host.ExecuteCodeAsync("M98 P\"drt-m120-loop.g\"");
        await bench.Host.WaitForMessageAsync(MessageType.Error, "stack overflow");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("pushes"), Is.EqualTo(InterpreterStateStack.MaxDepth - 2),
                        "the caller's level and the macro's own count towards the limit");
            Assert.That(await bench.Host.GlobalAsync("after"), Is.Zero, "the macro was aborted at the overflow");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.EqualTo(1),
                        "the macro's levels were discarded and the caller's M120 level kept");
        });
    }

    /// <summary>An overflow in a job file aborts the job, and the levels the job pushed go with it</summary>
    /// <remarks>RRF truth: AbortPrint stops the print when the overflow is on the file channel</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task OverflowInAJobAbortsTheJob()
    {
        await using Bench bench = await Bench.StartAsync("global started = 0\nglobal pushes = 0\nglobal after = 0");
        bench.Host.Sd.WriteGCode("job.gcode", """
            set global.started = 1
            while iterations < 20
              M120
              set global.pushes = global.pushes + 1
            set global.after = 1
            """);

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForGlobalAsync("started", 1);
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        await bench.Host.WaitForMessageAsync(MessageType.Error, "stack overflow");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("pushes"), Is.EqualTo(InterpreterStateStack.MaxDepth), "a job runs at the base, so all the levels were its own");
            Assert.That(await bench.Host.GlobalAsync("after"), Is.Zero, "the job was aborted at the overflow");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileAborted), Is.True);
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth, CodeChannel.File), Is.Zero, "the job's levels were discarded");
        });
    }

    /// <summary>The levels a job leaves open are discarded when it finishes, and the state it ended with stands</summary>
    /// <remarks>RRF pushes no level for the job file itself, so there is no state from before the job to restore</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task LevelsOpenWhenAJobFinishesAreDiscarded()
    {
        await using Bench bench = await Bench.StartAsync("global depth = -1\nglobal done = 0");
        int file = await bench.Host.InputIndexAsync(CodeChannel.File);
        bench.Host.Sd.WriteGCode("job.gcode", $"""
            G90
            M120
            G91
            set global.depth = inputs[{file}].stackDepth
            set global.done = 1
            """);

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForGlobalAsync("done", 1);
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("depth"), Is.EqualTo(1));
            Assert.That(await bench.Host.ReadModelAsync(model => model.Job.LastFileAborted), Is.False, "leaving a level open is not an error");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth, CodeChannel.File), Is.Zero, "the open level was discarded");
            Assert.That(await bench.Host.ReadInputAsync(input => input.AxesRelative, CodeChannel.File), Is.True, "without restoring the G90 it held");
        });
    }

    /// <summary>The levels a job leaves open are discarded when it is aborted</summary>
    /// <remarks>RRF truth: GCodes::AbortPrint pops every machine state while the file channel is doing a file</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task LevelsOpenWhenAJobIsAbortedAreDiscarded()
    {
        await using Bench bench = await Bench.StartAsync("global depth = -1\nglobal after = 0");
        int file = await bench.Host.InputIndexAsync(CodeChannel.File);
        bench.Host.Sd.WriteGCode("job.gcode", $"""
            M120
            M120
            set global.depth = inputs[{file}].stackDepth
            abort "drt"
            set global.after = 1
            """);

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForModelAsync(model => model.Job.LastFileAborted, true, "job.lastFileAborted");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("depth"), Is.EqualTo(2));
            Assert.That(await bench.Host.GlobalAsync("after"), Is.Zero);
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth, CodeChannel.File), Is.Zero, "both open levels were discarded");
        });
    }
}
