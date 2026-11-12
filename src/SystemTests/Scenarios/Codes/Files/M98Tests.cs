using System;
using System.Globalization;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
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
    /// Every parameter of M98 but P reaches the macro as param.&lt;letter&gt;, as the value it reads as an expression: a
    /// quoted string is a string, a whole number an integer and a decimal a float, and an expression keeps the type it
    /// evaluated to, so a string stays a string even when it holds digits and an array stays an array
    /// </summary>
    /// <remarks>
    /// RRF truth: StringParser::AddParameters skips P only when codeRunning is 98, and parses every other value with
    /// ExpressionParser::Parse. Regression:
    /// m98-call-macro-with-parameters
    /// </remarks>
    [Test]
    public async Task PassesItsParametersToTheMacro()
    {
        const string text = "alpha";
        const int integer = 42;
        const int negative = -5;
        const float fraction = 1.25f;
        const string digits = "42";
        int[] array = [1, 2, 3];

        await using Bench bench = await Bench.StartAsync("""
            global text = ""
            global hasP = true
            global integer = ""
            global negative = 0
            global fraction = ""
            global digits = 0
            global flag = false
            global array = null
            global expr = 0
            global depth = -1
            """);
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-m98-params.g", $"""
            set global.text = param.A
            set global.hasP = exists(param.P)
            set global.integer = param.S ^ ""
            set global.negative = param.C
            set global.fraction = param.X ^ ""
            set global.digits = param.E
            set global.flag = param.V
            set global.array = param.Y
            set global.expr = param.B
            set global.depth = inputs[{http}].stackDepth
            """);

        string fractionText = fraction.ToString(CultureInfo.InvariantCulture);
        Assert.That((await bench.Host.ExecuteCodeAsync($"M98 P\"drt-m98-params.g\" A\"{text}\" S{integer} C{negative} X{fractionText} E{{\"{digits}\"}} V{{true}} Y{{{string.Join(",", array)}}} B{{3+4}}")).Trim(), Is.Empty);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalTextAsync("text"), Is.EqualTo(text), "a quoted string arrives as a string");
            Assert.That(await bench.Host.GlobalFlagAsync("hasP"), Is.False, "P names the file and is not passed on");
            Assert.That(await bench.Host.GlobalTextAsync("integer"), Is.EqualTo(integer.ToString(CultureInfo.InvariantCulture)), "a whole number arrives as an integer");
            Assert.That(await bench.Host.GlobalAsync("negative"), Is.EqualTo(negative), "a signed number arrives as a number");
            Assert.That(await bench.Host.GlobalTextAsync("fraction"), Is.EqualTo(fractionText), "a decimal arrives as a float");
            Assert.That(await bench.Host.GlobalTextAsync("digits"), Is.EqualTo(digits), "a string expression stays a string though it holds digits");
            Assert.That(await bench.Host.GlobalFlagAsync("flag"), Is.True, "a boolean expression arrives as a boolean");
            Assert.That(await bench.Host.GlobalArrayAsync("array"), Is.EqualTo(array), "an array expression arrives as an array");
            Assert.That(await bench.Host.GlobalAsync("expr"), Is.EqualTo(7), "an expression is evaluated before the macro starts");
            Assert.That(await bench.Host.GlobalAsync("depth"), Is.EqualTo(1), "the macro ran one stack level deep");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "the stack is back at its base once the macro returned");
        });
    }

    /// <summary>A colon-separated list is not an array: the macro gets its first item, typed as that item reads</summary>
    /// <remarks>
    /// RRF truth: StringParser::AddParameters reads one expression from where the value starts, and
    /// ExpressionParser::ParseInternal stops at the colon, which is not an operator
    /// </remarks>
    [TestCase("1:2:3", "1")]
    [TestCase("1.5:2", "1.5")]
    public async Task AColonListGivesItsFirstItem(string list, string first)
    {
        await using Bench bench = await Bench.StartAsync("global first = \"\"");
        bench.Host.Sd.WriteSys("drt-m98-list.g", "set global.first = param.F ^ \"\"");

        Assert.That((await bench.Host.ExecuteCodeAsync($"M98 P\"drt-m98-list.g\" F{list}")).Trim(), Is.Empty);
        Assert.That(await bench.Host.GlobalTextAsync("first"), Is.EqualTo(first));
    }

    /// <summary>
    /// A parameter with no value is an error quoting where the value should have started, and the macro does not run
    /// </summary>
    /// <remarks>
    /// RRF truth: StringParser::AddParameters throws "invalid value for parameter" when the value does not start with a
    /// digit, a quote, a brace, a point or a sign, before DoFileMacro pushes a machine state. RRF names the character it
    /// found rather than the letter
    /// </remarks>
    [TestCase("S")]
    [TestCase("S X1")]
    public async Task AParameterWithoutAValueIsAnError(string parameters)
    {
        await using Bench bench = await Bench.StartAsync("global ran = 0");
        bench.Host.Sd.WriteSys("drt-m98-empty.g", "set global.ran = 1");

        string line = $"M98 P\"drt-m98-empty.g\" {parameters}";
        int column = line.IndexOf(" S", StringComparison.Ordinal) + 3;
        Assert.That((await bench.Host.ExecuteCodeAsync(line)).TrimEnd(),
                    Is.EqualTo($"Error: at column {column}: M98: invalid value for parameter 'S'"));
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalAsync("ran"), Is.Zero, "the macro did not run");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "nothing was pushed");
        });
    }

    /// <summary>With P present, R is a parameter for the macro like any other letter rather than the restartable flag</summary>
    /// <remarks>RRF truth: GCodes2.cpp case 98 tests P before R, and AddParameters passes R on</remarks>
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

    /// <summary>
    /// A macro nested deeper than the stack holds is an error that aborts every macro on the channel, so a recursion stops
    /// at the limit and nothing after any of its calls runs
    /// </summary>
    /// <remarks>
    /// RRF truth: DoFileMacro reports the failed Push and calls AbortPrint, which unwinds every file on the channel. RRF
    /// reports the overflow as a message and replies OK to the M98 that overflowed; DSF refuses that M98 with an error,
    /// which a macro reports as a message
    /// </remarks>
    [Test]
    public async Task NestingBeyondTheStackLimitAbortsEveryMacro()
    {
        await using Bench bench = await Bench.StartAsync("global calls = 0\nglobal deepest = -1\nglobal unwound = 0");
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-recurse.g", $"""
            set global.calls = global.calls + 1
            set global.deepest = inputs[{http}].stackDepth
            M98 P"drt-recurse.g"
            set global.unwound = global.unwound + 1
            """);

        Assert.That((await bench.Host.ExecuteCodeAsync("M98 P\"drt-recurse.g\"")).Trim(), Is.Empty,
                    "the overflow is reported where it happened, not again by the M98 that started the recursion");
        string error = await bench.Host.WaitForMessageAsync(MessageType.Error, "stack overflow");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(error, Does.Contain("M98"), "the error names the M98 that overflowed");
            Assert.That(await bench.Host.GlobalAsync("calls"), Is.EqualTo(InterpreterStateStack.MaxDepth), "every level up to the limit ran");
            Assert.That(await bench.Host.GlobalAsync("deepest"), Is.EqualTo(InterpreterStateStack.MaxDepth), "the deepest macro was at the limit");
            Assert.That(await bench.Host.GlobalAsync("unwound"), Is.Zero, "no macro ran on after the overflow");
            Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "every macro's level was popped");
            Assert.That(await bench.Host.ReadInputAsync(input => input.InMacro), Is.False);
        });
    }

    /// <summary>A macro starts with inverse time mode off whatever its caller uses, and the caller's mode is restored after it</summary>
    /// <remarks>RRF truth: the GCodeMachineState copy constructor keeps inverseTimeMode only when withinSameFile</remarks>
    [Test]
    public async Task AMacroStartsWithInverseTimeModeOff()
    {
        await using Bench bench = await Bench.StartAsync("global inverseTime = true");
        int http = await bench.Host.InputIndexAsync();
        bench.Host.Sd.WriteSys("drt-g93.g", $"set global.inverseTime = inputs[{http}].inverseTimeMode");

        await bench.Host.ExecuteCodeAsync("G93");
        await bench.Host.ExecuteCodeAsync("M98 P\"drt-g93.g\"");
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await bench.Host.GlobalFlagAsync("inverseTime"), Is.False, "the macro started in G94");
            Assert.That(await bench.Host.ReadInputAsync(input => input.InverseTimeMode), Is.True, "the caller is back in G93");
        });
    }

    /// <summary>M98 in a file that is being simulated still runs the macro, so the simulation follows the file's path</summary>
    /// <remarks>RRF truth: GCodes2.cpp HandleMcode runs M98 while simulating, where it skips most M-codes</remarks>
    [Test]
    public async Task RunsTheMacroWhileSimulating()
    {
        await using Bench bench = await Bench.StartAsync("global ran = 0\nglobal depth = -1");
        int file = await bench.Host.InputIndexAsync(CodeChannel.File);
        bench.Host.Sd.WriteSys("drt-sim.g", $"""
            set global.depth = inputs[{file}].stackDepth
            set global.ran = global.ran + 1
            """);
        bench.Host.Sd.WriteGCode("sim.gcode", "M98 P\"drt-sim.g\"\n");

        await bench.Host.ExecuteCodeAsync("M37 P\"0:/gcodes/sim.gcode\" F0");
        await bench.Host.WaitForGlobalAsync("ran", 1);
        Assert.That(await bench.Host.GlobalAsync("depth"), Is.EqualTo(1), "the macro ran one stack level deep");
    }

    /// <summary>A macro that is not there is an error, quoting the name as it was given</summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 98 calls DoFileMacroWithParameters with reportMissing false and turns the failure into
    /// GCodeResult::error, before DoFileMacro pushes a machine state. Regression: error-missing-macro
    /// </remarks>
    [TestCase("/sys/drt-does-not-exist.g")]
    [TestCase("missing.g")]
    public async Task MissingMacroIsAnError(string fileName)
    {
        await using Bench bench = await Bench.StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync($"M98 P\"{fileName}\"")).TrimEnd(),
                    Is.EqualTo($"Error: M98: Macro file {fileName} not found"));
        Assert.That(await bench.Host.ReadInputAsync(input => (int)input.StackDepth), Is.Zero, "nothing was pushed for a macro that is not there");
    }

    /// <summary>M98 with neither P nor R is an error</summary>
    /// <remarks>RRF truth: GCodes2.cpp case 98, the final else. Regression: error-missing-parameters-system-2</remarks>
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
