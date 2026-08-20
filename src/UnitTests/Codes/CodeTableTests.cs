using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using NUnit.Framework;

namespace UnitTests.Codes;

/// <summary>
/// The CodeTable mechanism from MOTION_SYNCHRONISED_ACTIONS.md §5.1, exercised on a table built
/// for the test rather than the handlers' own tables. The handler tables are declarations; what
/// needs proving is that a table classifies and dispatches the way the declarations mean:
/// exact fractional lookup, resolver rows, the miss path, and the row lambdas deciding the minor
/// </summary>
public class CodeTableTests
{
    /// <summary>
    /// Stand-in handler recording which row's lambda ran
    /// </summary>
    private sealed class Recorder
    {
        public string? Called;

        public ValueTask<Message> Handle(string what)
        {
            Called = what;
            return ValueTask.FromResult(new Message(MessageType.Success, what));
        }
    }

    private static readonly CodeTable<Recorder> Table = new(CodeType.MCode)
    {
        // One row shared by several codes, as M0/M1/M2 share HandleStopAsync
        { [0, 1, 2], CodeClass.Immediate, (h, c, ct) => h.Handle("stop") },
        // A bare major and two of its fractions, each row passing what the minor decided
        { 100, CodeClass.Deferred, (h, c, ct) => h.Handle("bare") },
        { (100, 1), CodeClass.Deferred, (h, c, ct) => h.Handle("minor 1") },
        { (100, 2), CodeClass.Ordered, (h, c, ct) => h.Handle("minor 2") },
        // A resolver row, whose class depends on the parameters, as M906 does
        { 200, c => c.Parameters.Count > 0 ? CodeClass.Barrier : CodeClass.Immediate, (h, c, ct) => h.Handle("resolver") },
    };

    private static DuetAPI.Commands.Code Parse(string text) => new(text);

    [TestCase("M0", CodeClass.Immediate)]
    [TestCase("M1", CodeClass.Immediate)]
    [TestCase("M2", CodeClass.Immediate)]
    [TestCase("M100", CodeClass.Deferred)]
    [TestCase("M100.1", CodeClass.Deferred)]
    [TestCase("M100.2", CodeClass.Ordered)]
    public void FixedRowsClassify(string code, CodeClass expected)
    {
        Assert.That(Table.Classify(Parse(code)), Is.EqualTo(expected));
    }

    [Test]
    public void ResolverRowsClassifyFromParameters()
    {
        Assert.That(Table.Classify(Parse("M200")), Is.EqualTo(CodeClass.Immediate));
        Assert.That(Table.Classify(Parse("M200 X1")), Is.EqualTo(CodeClass.Barrier));
    }

    /// <summary>
    /// A fraction with no row is "no such code": it never falls back to the bare major, which is
    /// the M906.1-executes-as-M906 bug this design removes
    /// </summary>
    [TestCase("M100.3")]
    [TestCase("M200.1")]
    [TestCase("M42")]
    public void MissingRowsClassifyNull(string code)
    {
        Assert.That(Table.Classify(Parse(code)), Is.Null);
    }

    /// <summary>
    /// A minor of zero is the fraction-less form, as RepRapFirmware's "fraction > 0" gates read it
    /// </summary>
    [Test]
    public void MinorZeroIsTheBareForm()
    {
        Assert.That(Table.Classify(Parse("M100.0")), Is.EqualTo(CodeClass.Deferred));
        Assert.That(Table.Classify(Parse("M0.0")), Is.EqualTo(CodeClass.Immediate));
    }

    [TestCase("M2", "stop")]
    [TestCase("M100", "bare")]
    [TestCase("M100.1", "minor 1")]
    [TestCase("M100.2", "minor 2")]
    [TestCase("M200 X1", "resolver")]
    public async Task InvokeRunsTheRowTheCodeSelected(string code, string expected)
    {
        Recorder recorder = new();
        Message result = await Table.Invoke(recorder, MakeCommand(code), CancellationToken.None);
        Assert.That(recorder.Called, Is.EqualTo(expected));
        Assert.That(result.Content, Is.EqualTo(expected));
    }

    [Test]
    public void DuplicateRowsRefuseToRegister()
    {
        Assert.Throws<ArgumentException>(() => _ = new CodeTable<Recorder>(CodeType.MCode)
        {
            { 1, CodeClass.Immediate, (h, c, ct) => h.Handle("first") },
            { [1, 2], CodeClass.Barrier, (h, c, ct) => h.Handle("second") },
        });
    }

    /// <summary>
    /// The statically readable class column: fixed classes as declared, null marking resolver rows
    /// </summary>
    [Test]
    public void ClassColumnReportsEveryRow()
    {
        Dictionary<CodeKey, CodeClass?> column = Table.ClassColumn;
        Assert.That(column, Has.Count.EqualTo(7));
        Assert.That(column[new CodeKey(CodeType.MCode, 0, null)], Is.EqualTo(CodeClass.Immediate));
        Assert.That(column[new CodeKey(CodeType.MCode, 100, 2)], Is.EqualTo(CodeClass.Ordered));
        Assert.That(column[new CodeKey(CodeType.MCode, 200, null)], Is.Null);
    }

    /// <summary>
    /// Invoke takes the control server's code type, whose constructor wants the services the
    /// pipeline injects; classification and dispatch touch none of them, so stand-ins suffice
    /// </summary>
    private static DuetControlServer.Commands.Code MakeCommand(string text)
        => new(text, codeProcessor: null!, expressions: null!, gCodes: null!, mCodes: null!,
               tCodes: null!, keywords: null!, lifetime: null!, macroRunner: null!,
               logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<DuetControlServer.Commands.Code>.Instance,
               settings: Microsoft.Extensions.Options.Options.Create(new DuetControlServer.Settings()));
}
