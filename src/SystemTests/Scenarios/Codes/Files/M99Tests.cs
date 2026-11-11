using System.Threading.Tasks;
using DuetAPI;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Files;

/// <summary>
/// M99, returning from a macro, asserted against RepRapFirmware's GCodes2.cpp case 99 and GCodes::FileMacroCyclesReturn,
/// and against the regression suite's m99-return-from-macro reference
/// </summary>
[TestFixture]
[GCode("M99")]
public class M99Tests : BenchFixture
{
    /// <summary>M99 ends the macro at that line: nothing after it runs, and the M98 that called it completes normally</summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 99, FileMacroCyclesReturn popping the macro's machine state. Regression:
    /// m99-return-from-macro
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task ReturnsFromTheMacro()
    {
        await using Bench bench = await Bench.StartAsync("global step = \"\"\nglobal depth = -1");
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-m99.g", $"""
            set global.step = "before"
            set global.depth = inputs[{http}].stackDepth
            M99
            set global.step = "after"
            """);

        Assert.That((await bench.Host.ExecuteCodeAsync("M98 P\"drt-m99.g\"")).Trim(), Is.Empty, "the M98 completes without an error");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalTextAsync("step"), Is.EqualTo("before"), "the line after M99 did not run");
            Assert.That(await bench.Host.GlobalAsync("depth"), Is.EqualTo(1), "the macro ran one stack level deep");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "the stack is back at its base");
            Assert.That(await bench.Host.ReadInputAsync(input => input.InMacro), Is.False, "and out of the macro");
        });
    }

    /// <summary>M99 in a nested macro returns from that macro alone: the macro that called it carries on</summary>
    /// <remarks>RRF truth: FileMacroCyclesReturn closes the current file and pops one machine state</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task ReturnsFromTheInnermostMacroOnly()
    {
        await using Bench bench = await Bench.StartAsync("""
            global innerAfter = 0
            global outerAfter = 0
            global innerDepth = -1
            global outerDepthAfter = -1
            """);
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-m99-outer.g", $"""
            M98 P"drt-m99-inner.g"
            set global.outerAfter = 1
            set global.outerDepthAfter = inputs[{http}].stackDepth
            """);
        bench.Host.Sd.WriteSys("drt-m99-inner.g", $"""
            set global.innerDepth = inputs[{http}].stackDepth
            M99
            set global.innerAfter = 1
            """);

        await bench.Host.ExecuteCodeAsync("M98 P\"drt-m99-outer.g\"");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("innerAfter"), Is.Zero, "the inner macro stopped at its M99");
            Assert.That(await bench.Host.GlobalAsync("outerAfter"), Is.EqualTo(1), "the outer macro ran on after the inner one returned");
            Assert.That(await bench.Host.GlobalAsync("innerDepth"), Is.EqualTo(2), "the inner macro ran two stack levels deep");
            Assert.That(await bench.Host.GlobalAsync("outerDepthAfter"), Is.EqualTo(1), "M99 popped the inner macro's level alone");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "the stack is back at its base once the outer macro returned");
        });
    }

    /// <summary>M99 inside a loop and a conditional block still returns from the whole macro</summary>
    /// <remarks>RRF truth: FileMacroCyclesReturn closes the file whatever block it is in; blocks are BlockStates within one machine state</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task ReturnsFromInsideABlock()
    {
        await using Bench bench = await Bench.StartAsync("global passes = 0\nglobal after = 0\nglobal depth = -1");
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-m99-loop.g", $"""
            while iterations < 10
              set global.passes = global.passes + 1
              if iterations == 2
                set global.depth = inputs[{http}].stackDepth
                M99
            set global.after = 1
            """);

        Assert.That((await bench.Host.ExecuteCodeAsync("M98 P\"drt-m99-loop.g\"")).Trim(), Is.Empty);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("passes"), Is.EqualTo(3), "the loop ran until the M99");
            Assert.That(await bench.Host.GlobalAsync("after"), Is.Zero, "nothing after the loop ran");
            Assert.That(await bench.Host.GlobalAsync("depth"), Is.EqualTo(1), "a block is not a stack level, so the macro is one level deep inside it");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "the stack is back at its base");
        });
    }

    /// <summary>The M120 levels a macro has open when it reaches M99 are discarded with it, and the caller's state is restored</summary>
    /// <remarks>RRF truth: FileMacroCyclesReturn pops every machine state down to and including the macro's own</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task DiscardsTheMacrosOpenM120Levels()
    {
        await using Bench bench = await Bench.StartAsync("global depth = -1");
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-m99-m120.g", $"""
            M120
            G91
            set global.depth = inputs[{http}].stackDepth
            M99
            """);

        await bench.Host.ExecuteCodeAsync("G90");
        Assert.That((await bench.Host.ExecuteCodeAsync("M98 P\"drt-m99-m120.g\"")).Trim(), Is.Empty);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("depth"), Is.EqualTo(2), "the macro had a level of its own open");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "it was discarded with the macro");
            Assert.That(await bench.Host.ReadInputAsync(input => input.AxesRelative), Is.False, "the caller's G90 is restored");
        });
    }

    /// <summary>M99 outside a macro does nothing and replies nothing</summary>
    /// <remarks>RRF truth: FileMacroCyclesReturn acts only if IsDoingFileMacro</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task DoesNothingOutsideAMacro()
    {
        await using Bench bench = await Bench.StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M99")).Trim(), Is.Empty);
        Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "there was no level to pop");
    }

    /// <summary>M99 in a job file, outside any macro, does not end the job</summary>
    /// <remarks>RRF truth: the file channel is not doing a file macro at the job's own level, so FileMacroCyclesReturn does nothing</remarks>
    [Test]
    public async Task DoesNothingInAJobFile()
    {
        await using Bench bench = await Bench.StartAsync("global after = 0\nglobal depthBefore = -1\nglobal depthAfter = -1");
        int file = await bench.Host.InputIndexAsync(CodeChannel.File);
        bench.Host.Sd.WriteGCode("job.gcode", $"""
            set global.depthBefore = inputs[{file}].stackDepth
            M99
            set global.depthAfter = inputs[{file}].stackDepth
            set global.after = 1
            """);

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForGlobalAsync("after", 1);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("depthBefore"), Is.Zero, "the job file runs at the base of the stack");
            Assert.That(await bench.Host.GlobalAsync("depthAfter"), Is.Zero, "and M99 popped nothing");
        });
    }
}
