using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Files;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Code = DuetControlServer.Commands.Code;
using DcsModel = DuetControlServer.Model.ObjectModel;

namespace UnitTests.File;

/// <summary>What a pause rewinds the job file to</summary>
[TestFixture]
public class CodeFileStateTests
{
    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private string _path;

    [SetUp]
    public void SetUp() => _path = Path.GetTempFileName();

    [TearDown]
    public void TearDown() => System.IO.File.Delete(_path);

    /// <summary>A file with nothing to execute its codes, so it must not need them flushed</summary>
    private CodeFile NewFile()
    {
        IOptions<Settings> settings = Options.Create(new Settings());
        DcsModel model = new(new TestLifetime(), NullLogger<DcsModel>.Instance, settings);
        model.State.MachineMode = DuetAPI.ObjectModel.MachineMode.CNC;
        return new CodeFile(new CodeFilePath("job.gcode", _path), CodeChannel.File, null!, null!, null!, model,
                            NullLoggerFactory.Instance, settings);
    }

    /// <summary>A code, which is used for its properties alone</summary>
    private static Code NewCode()
    {
        Code code = (Code)RuntimeHelpers.GetUninitializedObject(typeof(Code));
        code.Parameters = [];
        return code;
    }

    /// <summary>Read the next code of a file</summary>
    private static async Task<Code> ReadAsync(CodeFile file)
    {
        Code code = await file.ReadCodeAsync(NewCode());
        code?.SaveVariables();
        return code;
    }

    /// <summary>Everything about the rest of a file that depends on where in it the reading started</summary>
    private static async Task<List<string>> ReadRestAsync(CodeFile file)
    {
        List<string> rest = [];
        while (await ReadAsync(file) is Code code)
        {
            rest.Add($"{code} at {code.FilePosition}+{code.Length} line {code.LineNumber} indent {code.Indent} depth {code.FileState.Block?.Depth} {code.Flags}");
        }
        return rest;
    }

    /// <summary>A file reads on from any of its codes as it did the first time, in this instance or in another</summary>
    /// <remarks>The codes cover an indented line of several, a modal G command and the G53 of a line</remarks>
    [Test]
    public async Task ReadingOnFromACodeReadsWhatFollowedIt([Range(0, 7)] int index, [Values] bool atEnd, [Values] bool fork)
    {
        await System.IO.File.WriteAllTextAsync(_path, "G90\nvar x = 1\n    var y = 2\n    G1 X1 F600 G1 X2 M106 S1\n    X3\n    G53 G1 X4 G1 X5\n");

        using CodeFile first = NewFile();
        CodeFileState state = first.State;
        for (int i = 0; i <= index; i++)
        {
            state = (await ReadAsync(first)).FileState;
        }
        List<string> expected = await ReadRestAsync(first);

        using CodeFile second = fork ? NewFile() : first;
        using (second.Lock(CancellationToken.None))
        {
            second.Rewind(state, atEnd);
        }
        Assert.That(second.Position, Is.EqualTo(atEnd ? state.EndPosition : state.StartPosition));
        if (!atEnd)
        {
            Assert.That((await ReadAsync(second)).FilePosition, Is.EqualTo(state.StartPosition), "the code itself");
        }
        Assert.That(await ReadRestAsync(second), Is.EqualTo(expected));
    }

    [Test]
    public async Task RewindingPutsTheVariablesOfTheCodeBack()
    {
        await System.IO.File.WriteAllTextAsync(_path, "var x = 1\nG1 X1\nG1 X2\n");
        using CodeFile file = NewFile();
        Code var = await file.ReadCodeAsync(NewCode());
        file.Variables.TryCreateVariable("x", 1, var.FileState.Block!.Depth);
        CodeFileState state = (await ReadAsync(file)).FileState;
        file.Variables.DeleteVariables(var.FileState.Block!.Depth);

        file.Rewind(state, atEnd: true);

        Assert.That(file.Variables.TryGetVariable("x", out object value), Is.True);
        Assert.That(value, Is.EqualTo(1));
    }

    [Test]
    public void LeavingALoopSkipsTheRestOfTheBlocksInIt()
    {
        static CodeBlock NewBlock(KeywordType keyword, CodeBlock parent)
            => new(new DuetAPI.Commands.Code { Keyword = keyword }, true, parent);
        CodeBlock outer = NewBlock(KeywordType.While, null), loop = NewBlock(KeywordType.While, outer) with { Iterations = 2 };
        CodeBlock inner = NewBlock(KeywordType.Else, NewBlock(KeywordType.If, loop));

        CodeBlock left = inner.Leave(continueLoop: true);

        Assert.Multiple(() =>
        {
            Assert.That(inner.Depth, Is.EqualTo(4));
            Assert.That(inner.Loop, Is.SameAs(loop));
            Assert.That(left.ProcessBlock, Is.False);
            Assert.That(left.Parent!.ProcessBlock, Is.False);
            Assert.That(left.Loop!.ProcessBlock, Is.False);
            Assert.That(left.Loop!.ContinueLoop, Is.True);
            Assert.That(left.Loop!.Iterations, Is.EqualTo(2));
            Assert.That(left.Loop!.Parent, Is.SameAs(outer), "the loop around it carries on");
            Assert.That(inner.Loop!.ProcessBlock, Is.True, "what a code refers to does not change");
            Assert.That(inner.Leave(continueLoop: false).IsInsideActiveLoop, Is.True, "the outer loop");
            Assert.That(loop.Leave(continueLoop: false).Parent!.Leave(continueLoop: false).IsInsideActiveLoop, Is.False);
        });
    }
}
