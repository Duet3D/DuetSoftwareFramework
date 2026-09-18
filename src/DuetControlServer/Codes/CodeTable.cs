using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;

namespace DuetControlServer.Codes;

/// <summary>
/// A code's identity: type, major and minor number. Minor is null for the fraction-less form
/// </summary>
/// <param name="Type">Code type</param>
/// <param name="Major">Major number</param>
/// <param name="Minor">Minor number, or null for the fraction-less form</param>
public readonly record struct CodeKey(CodeType Type, int Major, int? Minor)
{
    /// <inheritdoc />
    public override string ToString() => $"{(char)Type}{Major}{(Minor is null ? "" : $".{Minor}")}";
}

/// <summary>
/// How a table entry names a code: a major number alone (<c>104</c>), or a (major, minor) pair for
/// a fractional code (<c>(569, 1)</c>). The letter comes from the table the number is used in
/// </summary>
/// <param name="Major">Major number</param>
/// <param name="Minor">Minor number, or null for the fraction-less form</param>
public readonly record struct CodeNumber(int Major, int? Minor = null)
{
    /// <summary>
    /// A bare major number
    /// </summary>
    /// <param name="major">Major number</param>
    public static implicit operator CodeNumber(int major) => new(major);

    /// <summary>
    /// A fractional code
    /// </summary>
    /// <param name="code">Major and minor number</param>
    public static implicit operator CodeNumber((int Major, int Minor) code) => new(code.Major, code.Minor);
}

/// <summary>
/// A handler method with its instance still unbound, as the static tables store it
/// </summary>
/// <param name="handler">Handler instance to run on</param>
/// <param name="code">Code to process</param>
/// <param name="cancellationToken">Cancellation token</param>
/// <returns>Result of the code</returns>
public delegate ValueTask<Message> CodeHandlerFunc<in THandler>(THandler handler, Commands.Code code, CancellationToken cancellationToken);

/// <summary>
/// A handler class's declaration of the codes it implements, for the letter given to the
/// constructor. Each entry is <c>{ code(s), class, handler }</c>: one <see cref="CodeNumber"/> or a
/// list of them sharing one row, the row's <see cref="CodeClass"/> (fixed, or a resolver when it
/// depends on the parameters), and the handler. Declared statically, so <see cref="ClassColumn"/>
/// is readable without an instance; the handler's <see cref="Handlers.ICodeHandler"/> members are
/// one-liners over <see cref="Classify"/> and <see cref="Invoke"/>.
/// </summary>
/// <remarks>
/// The table is the complete list of codes the handler supports, fractional and bare. Lookup is
/// exact: a code with a fraction looks up its own row and never falls back to the bare-major row,
/// and a code with no row classifies as null, meaning "no such code", which sends it down the
/// macro-then-unsupported path instead of a handler. Nothing here allocates per code: the key is a
/// struct, the dictionary freezes on first lookup, and the entries and their lambdas are singletons
/// created at static initialisation
/// </remarks>
/// <param name="type">The code letter every entry of this table is for</param>
public sealed class CodeTable<THandler>(CodeType type) : IEnumerable
{
    /// <summary>
    /// One row: the code's class (fixed, or resolved from its parameters) and its handler
    /// </summary>
    private sealed record Entry(CodeClass? FixedClass, Func<DuetAPI.Commands.Code, CodeClass>? ClassResolver, CodeHandlerFunc<THandler> Handler);

    private readonly Dictionary<CodeKey, Entry> _entries = [];
    private FrozenDictionary<CodeKey, Entry>? _lookup;

    // Frozen on first use, after the collection initializer has added every row; read-only and
    // allocation-free from then on
    private FrozenDictionary<CodeKey, Entry> Lookup => _lookup ??= _entries.ToFrozenDictionary();

    /// <summary>
    /// Add a row for one code with a fixed class
    /// </summary>
    public void Add(CodeNumber code, CodeClass cls, CodeHandlerFunc<THandler> handler) => Add([code], new Entry(cls, null, handler));

    /// <summary>
    /// Add one row shared by several codes with a fixed class
    /// </summary>
    public void Add(CodeNumber[] codes, CodeClass cls, CodeHandlerFunc<THandler> handler) => Add(codes, new Entry(cls, null, handler));

    /// <summary>
    /// Add a row for one code whose class depends on its parameters
    /// </summary>
    public void Add(CodeNumber code, Func<DuetAPI.Commands.Code, CodeClass> resolver, CodeHandlerFunc<THandler> handler) => Add([code], new Entry(null, resolver, handler));

    /// <summary>
    /// Add one row shared by several codes whose class depends on their parameters
    /// </summary>
    public void Add(CodeNumber[] codes, Func<DuetAPI.Commands.Code, CodeClass> resolver, CodeHandlerFunc<THandler> handler) => Add(codes, new Entry(null, resolver, handler));

    private void Add(CodeNumber[] codes, Entry entry)
    {
        foreach (CodeNumber code in codes)
        {
            _entries.Add(new CodeKey(type, code.Major, code.Minor), entry);   // throws on a duplicate row
        }
    }

    /// <summary>
    /// The code's key in this table. The parser reports "no fraction" as a negative minor, and a
    /// minor of zero is the fraction-less form, as in RepRapFirmware, whose fraction gates all read
    /// "fraction > 0": M569.0 is M569
    /// </summary>
    private CodeKey KeyOf(DuetAPI.Commands.Code code)
        => new(type, code.MajorNumber ?? int.MinValue, code.MinorNumber <= 0 ? null : code.MinorNumber);

    /// <summary>
    /// The code's class, or null when it has no row, which means "no such code"
    /// </summary>
    /// <param name="code">The code</param>
    /// <returns>Class of the code, or null without a row</returns>
    public CodeClass? Classify(DuetAPI.Commands.Code code)
        => Lookup.TryGetValue(KeyOf(code), out Entry? entry) ? entry.FixedClass ?? entry.ClassResolver!(code) : null;

    /// <summary>
    /// Run the code's row on the given handler instance
    /// </summary>
    /// <param name="handler">Handler instance</param>
    /// <param name="code">Code to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the code</returns>
    public ValueTask<Message> Invoke(THandler handler, Commands.Code code, CancellationToken cancellationToken)
        => Lookup[KeyOf(code)].Handler(handler, code, cancellationToken);

    /// <summary>
    /// The statically readable class column; null marks a resolver row, whose class depends on the
    /// code's parameters
    /// </summary>
    public Dictionary<CodeKey, CodeClass?> ClassColumn => _entries.ToDictionary(e => e.Key, e => e.Value.FixedClass);

    /// <inheritdoc />
    /// <remarks>Required for the collection initializer; the table is not meant to be enumerated</remarks>
    IEnumerator IEnumerable.GetEnumerator() => _entries.GetEnumerator();
}
