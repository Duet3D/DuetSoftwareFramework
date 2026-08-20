// Minimal working example of the code class table as dispatch table, from
// MOTION_SYNCHRONISED_ACTIONS.md §5.1. Self-contained: the DSF types (Code, Message, MacroRunner)
// are replaced by stand-ins, and handlers print what the real ones would do.
//
// Run with:  dotnet run CodeClassifierExample.cs
//
// What it demonstrates:
//  - each handler class declares one static CodeTable of { code, class, handler } entries, keyed
//    by major number (104), by (major, minor) for a fractional code, or by a list of numbers
//    sharing one row ([0, 1, 2]); the letter comes from the table's constructor;
//  - each handler owns its own classification: ICodeHandler.Classify and ProcessAsync are
//    one-liners over the handler's table, and the pipeline routes by code letter, as
//    Code.ProcessInternallyAsync already does;
//  - class resolvers for rows whose class depends on parameters (M906, G10);
//  - one row per implemented fractional code, so the minor is part of the entry, not a switch;
//  - exact lookup: a fraction with no row never reaches the bare major's handler;
//  - the miss path: MacroRunner.TryRunAsync runs the macro named after the code and reports
//    whether it existed, as M98 does; false means the code resolves as unsupported;
//  - TCodeHandler has no table: the tool number is a value, not a code identity, so its Classify
//    is a plain expression and its ProcessAsync dispatches directly;
//  - the shape of the data-driven class test: expected classes diffed both ways against the
//    tables' class columns, which are readable without instantiating any handler;
//  - nothing allocates per code: keys are structs, each table freezes its dictionary on first
//    lookup, and the entries and their lambdas are singletons created at static initialisation.

using System.Collections;
using System.Collections.Frozen;
using static CodeClass;

// ---------------------------------------------------------------------------------------------
// Demo run
// ---------------------------------------------------------------------------------------------

MacroRunner macroRunner = new();
MCodeHandler mCodes = new();
GCodeHandler gCodes = new();
TCodeHandler tCodes = new();

string[] job =
[
    "M106 S255",        // Deferred row: the worked example
    "M104 S210",        // Deferred row sharing its handler with M109, wait: baked into the entry
    "M109 S210",        // FlushAndStandstill row, same handler, wait: true
    "M0",               // shared row: [0, 1, 2] is one entry
    "M115",             // Immediate row
    "M906 X800",        // resolver row: drive letter present -> FlushAndStandstill
    "M906",             // resolver row: bare report -> Immediate
    "M906.1",           // no row: not a fractional code DSF implements -> no macro -> unsupported
    "M569.1 P0.1 S5",   // fractional row: dispatches directly, no minor switch in the handler
    "M569.3",           // no row -> unsupported (RRF implements M569.3; DSF does not: a §10-style gap)
    "M36.1 P\"job.gcode\" S0",  // fractional row: the minor is a boolean argument in the entry
    "G10 P0 X5",        // resolver row: axis letter -> FlushAndStandstill (sets tool offsets)
    "G10 P0 S210",      // resolver row: no axis letter -> Deferred (tool temperatures)
    "M1234 X5",         // no row, but sys/M1234.g exists -> the macro runs with param.X = 5
    "T1",               // no table: TCodeHandler classifies with an expression, FlushAndStandstill here
];

foreach (string line in job)
{
    await ProcessAsync(Code.Parse(line));
}

Console.WriteLine();
RunClassTableTest();
return;

// The per-type routing Code.ProcessInternallyAsync already performs
ICodeHandler HandlerFor(Code code) => code.Type switch
{
    CodeType.MCode => mCodes,
    CodeType.GCode => gCodes,
    CodeType.TCode => tCodes,
    _ => throw new InvalidOperationException($"no handler for {code.Type}")
};

// The ProcessInternally pipeline stage: ask the code's handler for its class, perform the class's
// synchronisation, then invoke the handler. A miss never dispatches: macro, then unsupported.
async Task ProcessAsync(Code code)
{
    ICodeHandler handler = HandlerFor(code);
    CodeClass? cls = handler.Classify(code);
    if (cls is null)
    {
        // What Code.TryRunCodeMacroAsync does: TryRunAsync resolves the file itself, runs it with
        // the code's parameters as param.*, and returns false when no such macro exists
        if (await macroRunner.TryRunAsync(code.Channel, code.MacroName, code, isSystemMacro: false,
                                          cancellationToken: CancellationToken.None))
        {
            Print(code, "(no row)", $"sys/{code.MacroName} run with the code's parameters as param.*");
        }
        else
        {
            Print(code, "(no row)", $"no sys/{code.MacroName} -> Warning: {code}: Command is not supported");
        }
        return;
    }

    string sync = cls switch
    {
        Immediate => "dispatch now",
        Flush => "flush, dispatch",
        FlushAndStandstill => "flush, wait for standstill, dispatch",
        Deferred => "flush, defer to the channel's anchor",
        _ => throw new InvalidOperationException($"unexpected class {cls}")
    };
    Message? result = await handler.ProcessAsync(code, CancellationToken.None);
    Print(code, cls.ToString()!, $"{sync} | {result?.Content}");
}

void Print(Code code, string cls, string action) => Console.WriteLine($"{code,-22} {cls,-10} {action}");

// The shape of the §5.1 unit test: a data-driven list of every code and its expected class,
// diffed both ways against the tables' class columns. No handler instance is created; a null
// class marks a resolver row, whose class depends on the parameters.
void RunClassTableTest()
{
    (string Code, CodeClass? Class)[] expected =
    [
        ("M0", FlushAndStandstill), ("M1", FlushAndStandstill), ("M2", FlushAndStandstill),
        ("M36", Immediate), ("M36.1", Immediate), ("M36.2", Immediate),
        ("M104", Deferred), ("M106", Deferred), ("M109", FlushAndStandstill), ("M115", Immediate),
        ("M569", FlushAndStandstill), ("M569.1", FlushAndStandstill), ("M569.2", FlushAndStandstill),
        ("M569.4", FlushAndStandstill), ("M569.6", FlushAndStandstill), ("M569.7", FlushAndStandstill),
        ("M906", null),     // resolver: FlushAndStandstill with a drive letter
        ("G10", null),      // resolver: FlushAndStandstill with an axis letter
    ];
    Dictionary<CodeKey, CodeClass?> expectedByKey = expected.ToDictionary(e => CodeKey.Parse(e.Code), e => e.Class);

    Dictionary<CodeKey, CodeClass?> actual = [];
    foreach (var column in (Dictionary<CodeKey, CodeClass?>[])[MCodeHandler.Rows.ClassColumn, GCodeHandler.Rows.ClassColumn])
    {
        foreach ((CodeKey key, CodeClass? cls) in column)
        {
            actual[key] = cls;
        }
    }

    List<string> failures = [];
    foreach ((CodeKey key, CodeClass? cls) in expectedByKey)
    {
        if (!actual.TryGetValue(key, out CodeClass? actualCls))
        {
            failures.Add($"expected row missing from the table: {key}");
        }
        else if (actualCls != cls)
        {
            failures.Add($"{key}: expected {cls?.ToString() ?? "resolver"}, table says {actualCls?.ToString() ?? "resolver"}");
        }
    }
    foreach (CodeKey key in actual.Keys.Where(key => !expectedByKey.ContainsKey(key)))
    {
        failures.Add($"table row not in the expected list: {key}");
    }

    Console.WriteLine(failures.Count == 0
        ? $"class table test: OK, {actual.Count} rows match the expected list both ways"
        : $"class table test: FAILED\n  {string.Join("\n  ", failures)}");
}

// ---------------------------------------------------------------------------------------------
// The class table (Codes/CodeClass.cs, Codes/CodeTable.cs in the plan)
// ---------------------------------------------------------------------------------------------

public enum CodeClass
{
    Immediate,   // act now, do not wait for the channel's pending codes
    Flush,     // flush the pipeline (order + expressions), no standstill; the move carries the value
    Deferred,    // the effect belongs at a point in the path
    FlushAndStandstill      // flush, then wait for standstill before the handler runs
}

/// <summary>A code's identity: type, major and minor number. Minor is null for the fraction-less
/// form.</summary>
public readonly record struct CodeKey(CodeType Type, int Major, int? Minor)
{
    public static CodeKey Parse(string name)
    {
        string[] number = name[1..].Split('.', 2);
        return new((CodeType)name[0], int.Parse(number[0]), number.Length > 1 ? int.Parse(number[1]) : null);
    }

    public override string ToString() => $"{(char)Type}{Major}{(Minor is null ? "" : $".{Minor}")}";
}

/// <summary>How a table entry names a code: a major number alone (<c>104</c>), or a (major, minor)
/// pair for a fractional code (<c>(569, 1)</c>). The letter comes from the table the number is
/// used in.</summary>
public readonly record struct CodeNumber(int Major, int? Minor = null)
{
    public static implicit operator CodeNumber(int major) => new(major);
    public static implicit operator CodeNumber((int Major, int Minor) code) => new(code.Major, code.Minor);
}

/// <summary>A handler method with its instance still unbound, as the static tables store it</summary>
public delegate ValueTask<Message?> CodeHandler<in THandler>(THandler handler, Code code, CancellationToken cancellationToken);

/// <summary>A handler class's half of the pipeline contract: what class a code has (null meaning
/// "no such code", the macro-then-unsupported path), and running it. GCodeHandler and
/// MCodeHandler answer both from their tables; TCodeHandler needs no table, its tool number is a
/// value, so it answers with plain expressions</summary>
public interface ICodeHandler
{
    CodeClass? Classify(Code code);
    ValueTask<Message?> ProcessAsync(Code code, CancellationToken cancellationToken);
}

/// <summary>
/// A handler class's declaration of the codes it implements, for the letter given to the
/// constructor. Each entry is <c>{ code(s), class, handler }</c>: one <see cref="CodeNumber"/> or
/// a list of them sharing one row, the row's class (a fixed <see cref="CodeClass"/>, or a
/// resolver when it depends on the parameters), and the handler. Declared statically, so
/// <see cref="ClassColumn"/> is readable without an instance; the handler's
/// <see cref="ICodeHandler"/> members are one-liners over <see cref="Classify"/> and
/// <see cref="Invoke"/>.
/// </summary>
public sealed class CodeTable<THandler>(CodeType type) : IEnumerable
{
    private sealed record Entry(CodeClass? FixedClass, Func<Code, CodeClass>? ClassResolver, CodeHandler<THandler> Handler);

    private readonly Dictionary<CodeKey, Entry> _entries = [];
    private FrozenDictionary<CodeKey, Entry>? _lookup;

    // Frozen on first use, after the collection initializer has added every row; read-only and
    // allocation-free from then on
    private FrozenDictionary<CodeKey, Entry> Lookup => _lookup ??= _entries.ToFrozenDictionary();

    public void Add(CodeNumber code, CodeClass cls, CodeHandler<THandler> handler) => Add([code], new Entry(cls, null, handler));
    public void Add(CodeNumber[] codes, CodeClass cls, CodeHandler<THandler> handler) => Add(codes, new Entry(cls, null, handler));
    public void Add(CodeNumber code, Func<Code, CodeClass> resolver, CodeHandler<THandler> handler) => Add([code], new Entry(null, resolver, handler));
    public void Add(CodeNumber[] codes, Func<Code, CodeClass> resolver, CodeHandler<THandler> handler) => Add(codes, new Entry(null, resolver, handler));

    private void Add(CodeNumber[] codes, Entry entry)
    {
        foreach (CodeNumber code in codes)
        {
            _entries.Add(new CodeKey(type, code.Major, code.Minor), entry);   // throws on a duplicate row
        }
    }

    private CodeKey KeyOf(Code code) => new(type, code.Major, code.Minor);

    /// <summary>The row's class, or null when the code has no row</summary>
    public CodeClass? Classify(Code code)
        => Lookup.TryGetValue(KeyOf(code), out Entry? entry) ? entry.FixedClass ?? entry.ClassResolver!(code) : null;

    /// <summary>Run the code's row on the given handler instance</summary>
    public ValueTask<Message?> Invoke(THandler handler, Code code, CancellationToken cancellationToken)
        => Lookup[KeyOf(code)].Handler(handler, code, cancellationToken);

    /// <summary>The statically readable class column; null marks a resolver row</summary>
    public Dictionary<CodeKey, CodeClass?> ClassColumn
        => _entries.ToDictionary(e => e.Key, e => e.Value.FixedClass);

    IEnumerator IEnumerable.GetEnumerator() => _entries.GetEnumerator();   // for the collection initializer
}

// ---------------------------------------------------------------------------------------------
// Handler classes with their tables. In DCS these are the existing MCodeHandler / GCodeHandler
// partial classes; the switch in ProcessAsync becomes the table
// ---------------------------------------------------------------------------------------------

public sealed class MCodeHandler : ICodeHandler
{
    // Keep numerically ordered for easier maintenance. M569 lists its implemented minors in one
    // entry; M569.3/.5/.8/.9 have no row, so a code using them takes the macro-then-unsupported
    // path without reaching this class
    public static readonly CodeTable<MCodeHandler> Rows = new(CodeType.MCode)
    {
        { [0, 1, 2],  FlushAndStandstill,   (h, c, ct) => h.HandleStopAsync(c, ct) },
        { 36,         Immediate, (h, c, ct) => h.HandleFileInfoAsync(c, fragment: null, ct) },
        { (36, 1),    Immediate, (h, c, ct) => h.HandleFileInfoAsync(c, fragment: true, ct) },
        { (36, 2),    Immediate, (h, c, ct) => h.HandleFileInfoAsync(c, fragment: false, ct) },
        { 104,        Deferred,  (h, c, ct) => h.SetTemperaturesAsync(c, wait: false, ct) },
        { 106,        Deferred,  (h, c, ct) => h.HandleFanSpeedAsync(c, ct) },
        { 109,        FlushAndStandstill,   (h, c, ct) => h.SetTemperaturesAsync(c, wait: true, ct) },
        { 115,        Immediate, (h, c, ct) => h.HandleFirmwareVersionAsync(c, ct) },
        { [569, (569, 1), (569, 2), (569, 4), (569, 6), (569, 7)],
                      FlushAndStandstill,   (h, c, ct) => h.SendDriverConfigAsync(c, c.Minor ?? 0, ct) },
        { 906,        c => c.HasAny("XYZE") ? FlushAndStandstill : Immediate,
                                 (h, c, ct) => h.HandleMotorCurrentsAsync(c, ct) },
    };

    public CodeClass? Classify(Code code) => Rows.Classify(code);
    public ValueTask<Message?> ProcessAsync(Code code, CancellationToken cancellationToken) => Rows.Invoke(this, code, cancellationToken);

    private static ValueTask<Message?> Done(string text) => ValueTask.FromResult<Message?>(new(MessageType.Success, text));

    public ValueTask<Message?> HandleStopAsync(Code code, CancellationToken ct) => Done($"job stopped by M{code.Major}");
    public ValueTask<Message?> HandleFileInfoAsync(Code code, bool? fragment, CancellationToken ct)
        => Done(fragment is null ? "file info parsed" : fragment.Value ? "thumbnail fragment read" : "file fragment read");
    public ValueTask<Message?> SetTemperaturesAsync(Code code, bool wait, CancellationToken ct)
        => Done(wait ? $"target {code.Param('S')} °C set, waiting until reached" : $"target {code.Param('S')} °C set");
    public ValueTask<Message?> HandleFanSpeedAsync(Code code, CancellationToken ct) => Done($"fan PWM set to {code.Param('S')}");
    public ValueTask<Message?> HandleFirmwareVersionAsync(Code code, CancellationToken ct) => Done("DuetControlServer version report");
    public ValueTask<Message?> SendDriverConfigAsync(Code code, int minor, CancellationToken ct)
        => Done($"CanMessageM569{(minor > 0 ? $"Point{minor}" : "")} sent to the driver's board");
    public ValueTask<Message?> HandleMotorCurrentsAsync(Code code, CancellationToken ct)
        => Done(code.HasAny("XYZE") ? "motor currents pushed to the boards" : "motor currents report");
}

public sealed class GCodeHandler : ICodeHandler
{
    public static readonly CodeTable<GCodeHandler> Rows = new(CodeType.GCode)
    {
        // FlushAndStandstill with an axis letter (offsets change what queued moves mean),
        // Deferred without (tool temperatures belong at the point in the path)
        { 10, c => c.HasAny("XYZUVWABC") ? FlushAndStandstill : Deferred,
              (h, c, ct) => h.HandleSetOffsetsOrTemperaturesAsync(c, ct) },
    };

    public CodeClass? Classify(Code code) => Rows.Classify(code);
    public ValueTask<Message?> ProcessAsync(Code code, CancellationToken cancellationToken) => Rows.Invoke(this, code, cancellationToken);

    public ValueTask<Message?> HandleSetOffsetsOrTemperaturesAsync(Code code, CancellationToken ct)
        => ValueTask.FromResult<Message?>(new(MessageType.Success,
            code.HasAny("XYZUVWABC") ? "tool offsets set" : "tool temperatures set"));
}

public sealed class TCodeHandler : ICodeHandler
{
    // No table: every T code is handled the same way, so there is no number to key on. The class
    // is an expression instead; in DCS a bare T is a report of the selected tool, Immediate, and
    // anything else is a tool change, FlushAndStandstill
    public CodeClass? Classify(Code code) => FlushAndStandstill;

    public ValueTask<Message?> ProcessAsync(Code code, CancellationToken cancellationToken)
        => ValueTask.FromResult<Message?>(new(MessageType.Success, $"tool change to T{code.Major}"));
}

// ---------------------------------------------------------------------------------------------
// Stand-ins for DuetAPI and DuetControlServer types
// ---------------------------------------------------------------------------------------------

public enum CodeType { GCode = 'G', MCode = 'M', TCode = 'T' }

public enum MessageType { Success, Warning, Error }

public sealed record Message(MessageType Type, string Content);

/// <summary>Stand-in for Files.MacroRunner. The real TryRunAsync resolves the file against the
/// system directory, returns false when it does not exist, and otherwise runs it on the code's
/// channel with the code's parameters as param.*</summary>
public sealed class MacroRunner
{
    public ValueTask<bool> TryRunAsync(string channel, string fileName, Code code, bool isSystemMacro = true,
                                       CancellationToken cancellationToken = default)
        => ValueTask.FromResult(fileName == "M1234.g");  // the only macro in this example's sys directory
}

public sealed class Code
{
    public required CodeType Type { get; init; }
    public required int Major { get; init; }
    public int? Minor { get; init; }
    public required string Parameters { get; init; }
    public string Channel { get; init; } = "File";

    public static Code Parse(string line)
    {
        string[] parts = line.Split(' ', 2);
        string command = parts[0];
        string[] number = command[1..].Split('.', 2);
        return new()
        {
            Type = (CodeType)command[0],
            Major = int.Parse(number[0]),
            Minor = number.Length > 1 ? int.Parse(number[1]) : null,
            Parameters = parts.Length > 1 ? parts[1] : string.Empty
        };
    }

    // Resolvers run per code, so these scan in place; the real Code walks its parsed parameter
    // list the same way
    public bool HasAny(string letters)
    {
        for (int i = 0; i < Parameters.Length; i++)
        {
            if ((i == 0 || Parameters[i - 1] == ' ') && letters.Contains(Parameters[i]))
            {
                return true;
            }
        }
        return false;
    }

    public string Param(char letter)
    {
        for (int i = 0; i < Parameters.Length; i++)
        {
            if ((i == 0 || Parameters[i - 1] == ' ') && Parameters[i] == letter)
            {
                int end = Parameters.IndexOf(' ', i);
                return Parameters[(i + 1)..(end < 0 ? Parameters.Length : end)];
            }
        }
        return "";
    }

    public string MacroName => Minor is null ? $"{(char)Type}{Major}.g" : $"{(char)Type}{Major}.{Minor}.g";

    public override string ToString() => $"{(char)Type}{Major}{(Minor is null ? "" : $".{Minor}")}{(Parameters.Length > 0 ? $" {Parameters}" : "")}";
}
