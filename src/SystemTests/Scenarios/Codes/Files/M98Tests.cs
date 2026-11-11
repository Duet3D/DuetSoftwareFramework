using System.Globalization;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Files;

/// <summary>
/// M98, calling a macro and flagging a level restartable, asserted against RepRapFirmware's GCodes2.cpp case 98,
/// GCodes::DoFileMacro and StringParser::AddParameters, and against the regression suite's m98-call-macro,
/// m98-call-macro-with-parameters, m98-macro-pause-flag, error-missing-macro and error-missing-parameters-system-2
/// references
/// </summary>
/// <remarks>
/// M117 is not implemented yet, so where the regression cases put their evidence in the status message these record it
/// in globals instead
/// </remarks>
[TestFixture]
[GCode("M98")]
public class M98Tests : BenchFixture
{
    /// <summary>M98 P runs every line of the macro, in order, one stack level deeper, and replies nothing</summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 98, DoFileMacroWithParameters, and GCodeBuffer::PushState and PopState for the
    /// stack depth. Regression: m98-call-macro
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task RunsTheMacroToItsEnd()
    {
        await using Bench bench = await Bench.StartAsync("global line = 0\nglobal depth = -1");
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-m98.g", $"""
            set global.line = 1
            set global.depth = inputs[{http}].stackDepth
            set global.line = global.line * 10 + 2
            """);

        Assert.That((await bench.Host.ExecuteCodeAsync("M98 P\"drt-m98.g\"")).Trim(), Is.Empty, "M98 replies nothing");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("line"), Is.EqualTo(12), "both lines ran, in order");
            Assert.That(await bench.Host.GlobalAsync("depth"), Is.EqualTo(1), "the macro ran one stack level deep");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "the stack is back at its base once the macro returned");
        });
    }

    /// <summary>
    /// M98 runs a macro and the channel reports it: one stack level deeper, in a macro, with the
    /// macro named as the file it is running. M98 R inside the macro flags it as restartable
    /// </summary>
    /// <remarks>
    /// GCodeBuffer.cpp OBJECT_MODEL table: <c>stackDepth</c> is <c>GetStackDepth</c>, the number of
    /// machine states above the base one; <c>inMacro</c> is <c>machineState->doingFileMacro</c>;
    /// <c>currentFile</c> is <c>machineState->fname</c>, which <c>GCodeBuffer::StartNewFile</c>
    /// assigns the name the macro was started with; and <c>macroRestartable</c> is the flag
    /// GCodes2.cpp case 98 sets from R, reporting it through <c>reprap.InputsUpdated()</c>
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task ReportsTheMacroStateInsideTheMacro()
    {
        await using Bench bench = await Bench.StartAsync("""
            global macroRuns = 0
            global depthInMacro = 0
            global inMacro = false
            global macroFile = "none"
            global restartable = false
            """);
        int http = await bench.Host.InputIndexAsync();
        InputChannel? httpChannel = await bench.Host.ReadModelAsync(model => model.Inputs[http]);

        bench.Host.Sd.WriteSys("depth.g", $"""
            set global.macroRuns = global.macroRuns + 1
            set global.depthInMacro = inputs[{http}].stackDepth
            set global.inMacro = inputs[{http}].inMacro
            set global.macroFile = inputs[{http}].currentFile
            M98 R1
            set global.restartable = inputs[{http}].macroRestartable
            """);

        await bench.Host.ExecuteCodeAsync("M98 P\"depth.g\"");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("macroRuns"), Is.EqualTo(1),
                        "M98 P ran the macro once (GCodes2.cpp case 98)");
            Assert.That(await bench.Host.GlobalAsync("depthInMacro"), Is.EqualTo(1),
                        "inside the macro the channel is one level deep (GCodeBuffer.cpp stackDepth)");
            Assert.That(await bench.Host.GlobalFlagAsync("inMacro"), Is.True,
                        "inside the macro inputs[].inMacro is true");
            Assert.That(await bench.Host.GlobalTextAsync("macroFile"), Does.Contain("depth.g"),
                        "inside the macro inputs[].currentFile names it (GCodeBuffer.cpp currentFile, machineState->fname)");
            Assert.That(await bench.Host.GlobalFlagAsync("restartable"), Is.True,
                        "M98 R1 flags the running macro as restartable (GCodes2.cpp case 98, SetMacroRestartable)");
            Assert.That(httpChannel?.StackDepth, Is.Zero,
                        "the stack is back to its base once the macro returned (GCodeBuffer.cpp stackDepth)");
            Assert.That(httpChannel?.InMacro, Is.False,
                        "inputs[].inMacro is false once the macro returned");
        });
    }

    /// <summary>
    /// Every parameter of M98 but P reaches the macro as param.&lt;letter&gt;, keeping its type: a quoted string stays a
    /// string, an integer and a float are numbers, and an expression is evaluated before the macro starts
    /// </summary>
    /// <remarks>
    /// RRF truth: StringParser::AddParameters skips P only when codeRunning is 98, and parses every other value as an
    /// expression. Regression: m98-call-macro-with-parameters
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task PassesItsParametersToTheMacro()
    {
        const string text = "alpha";
        const int integer = 42;
        const float fraction = 1.25f;

        await using Bench bench = await Bench.StartAsync("""
            global text = ""
            global hasP = true
            global integer = 0
            global fraction = 0
            global expr = 0
            global depth = -1
            """);
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-m98-params.g", $"""
            set global.text = param.A
            set global.hasP = exists(param.P)
            set global.integer = param.S
            set global.fraction = param.X
            set global.expr = param.B
            set global.depth = inputs[{http}].stackDepth
            """);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M98 P\"drt-m98-params.g\" A\"{text}\" S{integer} X{fraction.ToString(CultureInfo.InvariantCulture)} B{{3+4}}")).Trim(), Is.Empty);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalTextAsync("text"), Is.EqualTo(text), "a quoted string arrives as a string");
            Assert.That(await bench.Host.GlobalFlagAsync("hasP"), Is.False, "P names the file and is not passed on");
            Assert.That(await bench.Host.GlobalAsync("integer"), Is.EqualTo(integer), "an integer arrives as a number");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Global["fraction"]!.Value.GetDouble()), Is.EqualTo(fraction).Within(1e-6),
                        "a float arrives as a number");
            Assert.That(await bench.Host.GlobalAsync("expr"), Is.EqualTo(7), "an expression is evaluated before the macro starts");
            Assert.That(await bench.Host.GlobalAsync("depth"), Is.EqualTo(1), "the macro ran one stack level deep");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "the stack is back at its base once the macro returned");
        });
    }

    /// <summary>With P present, R is a parameter for the macro like any other letter rather than the restartable flag</summary>
    /// <remarks>RRF truth: GCodes2.cpp case 98 tests P before R, and AddParameters passes R on</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task RWithPIsAParameterNotTheFlag()
    {
        await using Bench bench = await Bench.StartAsync("global r = 0\nglobal restartable = true\nglobal depth = -1");
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-m98-r.g", $"""
            set global.r = param.R
            set global.restartable = inputs[{http}].macroRestartable
            set global.depth = inputs[{http}].stackDepth
            """);

        await bench.Host.ExecuteCodeAsync("M98 P\"drt-m98-r.g\" R1");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("r"), Is.EqualTo(1), "the macro reads param.R");
            Assert.That(await bench.Host.GlobalFlagAsync("restartable"), Is.False, "and is not flagged restartable by it");
            Assert.That(await bench.Host.ReadInputAsync(input => input.MacroRestartable), Is.False, "nor is the caller");
            Assert.That(await bench.Host.GlobalAsync("depth"), Is.EqualTo(1), "M98 P with R still runs the macro one stack level deep");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "the stack is back at its base once the macro returned");
        });
    }

    /// <summary>A macro that is not there is an error, quoting the name as it was given</summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 98 calls DoFileMacroWithParameters with reportMissing false and turns the failure into
    /// GCodeResult::error, before DoFileMacro pushes a machine state. Regression: error-missing-macro
    /// </remarks>
    public async Task MissingMacroIsAnError(string fileName)
    {
        await using Bench bench = await Bench.StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync($"M98 P\"{fileName}\"")).TrimEnd(),
                    Is.EqualTo($"Error: M98: Macro file {fileName} not found"));
        Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "nothing was pushed for a macro that is not there");
    }

    /// <summary>M98 with neither P nor R is an error</summary>
    /// <remarks>RRF truth: GCodes2.cpp case 98, the final else. Regression: error-missing-parameters-system-2</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task NoPOrRIsAnError()
    {
        await using Bench bench = await Bench.StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M98")).TrimEnd(), Is.EqualTo("Error: M98: No P or R parameter"));
        Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "nothing was pushed");
    }

    /// <summary>
    /// M98 R sets inputs[].macroRestartable for the level it runs on. A macro starts unflagged whatever its caller is,
    /// its own setting is discarded when it ends, and outside a macro the flag is set all the same
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 98 SetMacroRestartable on LatestMachineState, the GCodeMachineState constructor
    /// clearing macroRestartable for a new file, PopState restoring the caller's state, and GCodeBuffer.cpp's
    /// macroRestartable object model entry. Regression: m98-macro-pause-flag
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task RSetsTheRestartableFlagOfItsOwnLevel()
    {
        await using Bench bench = await Bench.StartAsync("""
            global atStart = true
            global afterR0 = true
            global afterR1 = false
            global innerAtStart = true
            global afterInner = false
            global outerDepth = -1
            global innerDepth = -1
            global depthAfterInner = -1
            """);
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-m98r.g", $"""
            set global.atStart = inputs[{http}].macroRestartable
            set global.outerDepth = inputs[{http}].stackDepth
            M98 R0
            set global.afterR0 = inputs[{http}].macroRestartable
            G4 P0
            M98 R1
            set global.afterR1 = inputs[{http}].macroRestartable
            M98 P"drt-m98r-inner.g"
            set global.afterInner = inputs[{http}].macroRestartable
            set global.depthAfterInner = inputs[{http}].stackDepth
            """);
        bench.Host.Sd.WriteSys("drt-m98r-inner.g", $"""
            set global.innerAtStart = inputs[{http}].macroRestartable
            set global.innerDepth = inputs[{http}].stackDepth
            M98 R0
            """);

        // Outside any macro, as the regression case's second code does
        Assert.That((await bench.Host.ExecuteCodeAsync("M98 R1")).Trim(), Is.Empty, "M98 R replies nothing");
        Assert.That(await bench.Host.ReadInputAsync(input => input.MacroRestartable), Is.True, "M98 R1 outside a macro sets the flag");
        Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "M98 R pushes nothing");

        Assert.That((await bench.Host.ExecuteCodeAsync("M98 P\"0:/sys/drt-m98r.g\"")).Trim(), Is.Empty);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalFlagAsync("atStart"), Is.False, "a macro starts unflagged though its caller is flagged");
            Assert.That(await bench.Host.GlobalFlagAsync("afterR0"), Is.False, "M98 R0 clears it");
            Assert.That(await bench.Host.GlobalFlagAsync("afterR1"), Is.True, "M98 R1 sets it");
            Assert.That(await bench.Host.GlobalFlagAsync("innerAtStart"), Is.False, "a nested macro starts unflagged too");
            Assert.That(await bench.Host.GlobalFlagAsync("afterInner"), Is.True, "the nested macro's R0 is discarded when it returns");
            Assert.That(await bench.Host.ReadInputAsync(input => input.MacroRestartable), Is.True, "and the macro's own settings when it returns");
            Assert.That(await bench.Host.GlobalAsync("outerDepth"), Is.EqualTo(1), "the macro ran one stack level deep");
            Assert.That(await bench.Host.GlobalAsync("innerDepth"), Is.EqualTo(2), "the nested macro ran two levels deep");
            Assert.That(await bench.Host.GlobalAsync("depthAfterInner"), Is.EqualTo(1), "the nested macro's level was popped when it returned");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "and the macro's own when it returned");
        });

        await bench.Host.ExecuteCodeAsync("M98 R0");
        Assert.That(await bench.Host.ReadInputAsync(input => input.MacroRestartable), Is.False, "M98 R0 outside a macro clears the flag");
    }
}
