using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.Files;

/// <summary>M98, running a macro</summary>
[TestFixture]
[GCode("M98")]
public class M98Tests : BenchFixture
{
    /// <summary>
    /// The index of the HTTP channel in inputs[], found by name because the channel order is not
    /// part of the contract under test
    /// </summary>
    private static async Task<int> HttpInputIndexAsync(DcsTestHost host)
    {
        using (await host.Model.AccessReadOnlyAsync(CancellationToken.None))
        {
            for (int i = 0; i < host.Model.Inputs.Count; i++)
            {
                if (host.Model.Inputs[i]?.Name == CodeChannel.HTTP)
                {
                    return i;
                }
            }
        }
        throw new AssertionException("inputs[] does not contain the HTTP channel");
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
    public async Task M98RunsAMacroAndReportsTheMacroState()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, JobMacros.Instrumented, "global depthInMacro = 0\nglobal inMacro = false\nglobal macroFile = \"none\"\nglobal restartable = false\n", Machines.Xye.Homed);
        int http = await HttpInputIndexAsync(bench.Host);
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

        Assert.That(await bench.Host.ExecuteCodeAsync("M98 P\"missing.g\""),
                    Does.Contain("Macro file missing.g not found"),
                    "M98 reports a macro it cannot find (GCodes2.cpp case 98)");
    }
}
