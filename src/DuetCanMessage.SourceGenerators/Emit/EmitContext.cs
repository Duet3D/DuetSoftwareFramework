using System.Text.Json.Nodes;
using DuetCanMessage.SourceGenerators.Model;

namespace DuetCanMessage.SourceGenerators.Emit;

/// <summary>
/// What a bare identifier in an expression refers to.
/// </summary>
public enum SymbolKind { Local, Field, Array, BitField, StructConstant, SchemaConstant, TypeName, TemplateParam, Method, Unknown }

/// <summary>
/// The context in which an expression or statement is rendered: which struct it belongs to, which
/// language it is being rendered into, and which names are locals rather than members.
/// </summary>
public sealed class EmitContext(CanSchema schema, StructDef? owner, Language language, IEnumerable<string> locals)
{
    public CanSchema Schema { get; } = schema;
    public StructDef? Owner { get; } = owner;
    public Language Language { get; } = language;
    public HashSet<string> Locals { get; } = [.. locals];

    private readonly HashSet<string> _boolLocals = [];
    private readonly HashSet<string> _intLocals = [];

    public void MarkBoolLocal(string name) => _boolLocals.Add(name);

    public bool IsBoolLocal(string name) => _boolLocals.Contains(name);

    /// <summary>Record a local that C# gives type <c>int</c>, such as a counted-loop variable.</summary>
    public void MarkIntLocal(string name) => _intLocals.Add(name);

    public bool IsIntLocal(string name) => _intLocals.Contains(name);

    /// <summary>
    /// Introduce a local for the duration of a block, so that a loop variable does not stay visible to
    /// the statements that follow the loop and shadow a member of the same name.
    /// </summary>
    public IDisposable Scope(string name, bool isInt = false)
    {
        LocalScope restore = new(this, name, Locals.Contains(name), _intLocals.Contains(name), _boolLocals.Contains(name));
        Locals.Add(name);
        if (isInt)
        {
            MarkIntLocal(name);
        }
        return restore;
    }

    /// <summary>
    /// Restores whatever binding <paramref name="name"/> had before the scope began, rather than clearing it
    /// outright — an outer loop reusing the same variable name as an inner one must still see it as a local
    /// (and, if applicable, an int local) once the inner scope closes.
    /// </summary>
    private sealed class LocalScope(EmitContext context, string name, bool wasLocal, bool wasIntLocal, bool wasBoolLocal) : IDisposable
    {
        public void Dispose()
        {
            Restore(context.Locals, wasLocal);
            Restore(context._intLocals, wasIntLocal);
            Restore(context._boolLocals, wasBoolLocal);
        }

        private void Restore(HashSet<string> set, bool wasPresent)
        {
            if (wasPresent)
            {
                set.Add(name);
            }
            else
            {
                set.Remove(name);
            }
        }
    }

    public SymbolKind Classify(string name)
    {
        if (Locals.Contains(name))
        {
            return SymbolKind.Local;
        }
        if (Owner is not null)
        {
            if (name == Owner.TemplateParam)
            {
                return SymbolKind.TemplateParam;
            }
            MemberDef? member = Owner.FlatMembers.FirstOrDefault(m => m.Name == name);
            if (member is not null)
            {
                return member.Kind == MemberKind.Array ? SymbolKind.Array : SymbolKind.Field;
            }
            if (Owner.AllBitFields.Any(f => f.Name == name))
            {
                return SymbolKind.BitField;
            }
            if (Owner.Constants.Any(c => c.Name == name))
            {
                return SymbolKind.StructConstant;
            }
            if (Owner.Methods.Any(m => m.Name == name))
            {
                return SymbolKind.Method;
            }
        }
        if (Schema.Constants.ContainsKey(name))
        {
            return SymbolKind.SchemaConstant;
        }
        if (Types.IsPrimitive(name) || Schema.Find(name) is not null)
        {
            return SymbolKind.TypeName;
        }
        return SymbolKind.Unknown;
    }

    public MemberDef? Member(string name) => Owner?.FlatMembers.FirstOrDefault(m => m.Name == name);

    public BitFieldDef? BitField(string name) => Owner?.AllBitFields.FirstOrDefault(f => f.Name == name);

    /// <summary>Resolve a type name, substituting the template argument of an expanded struct for its parameter.</summary>
    public string ResolveType(string type) =>
        Owner?.TemplateArg is not null && type == Owner.TemplateParamName ? Owner.TemplateArg : type;

    /// <summary>The size in bytes of a schema type or of one of the owner's members.</summary>
    public int SizeOfSymbol(string name)
    {
        string resolved = ResolveType(name);
        if (Types.IsPrimitive(resolved) || Schema.Find(resolved) is not null)
        {
            return Types.SizeOf(Schema, resolved);
        }
        MemberDef member = Member(name) ?? throw new InvalidDataException($"sizeof({name}): unknown type or member");
        return member.Size;
    }
}

/// <summary>
/// Generation of the boilerplate helpers that every message carries: SetRequestId and ClearReservedFields.
/// These are derived from the schema rather than written out for each message, which is what keeps the
/// reserved-field handling consistent between the two languages.
/// </summary>
public static class Synthesise
{
    /// <summary>
    /// Build the SetRequestId method for messages that carry a request ID, or the ClearReservedFields
    /// method for those that do not. Returns null for structs that are not messages.
    /// </summary>
    public static MethodDef? RequestIdOrClear(CanSchema schema, StructDef s)
    {
        if (s.RequestIdField is not null)
        {
            List<string> toClear = [.. s.FlatMembers.Where(m => m.Reserved).Select(m => m.Name),
                                    .. s.AllBitFields.Where(f => f.Reserved).Select(f => f.Name),
                                    .. s.SetRequestIdAlsoClears];
            JsonArray body =
            [
                new JsonObject { ["set"] = s.RequestIdField, ["to"] = "rid" },
                .. toClear.Select(name => new JsonObject { ["set"] = name, ["to"] = ZeroFor(s, name) })
            ];
            return new MethodDef
            {
                Name = "SetRequestId",
                ReturnType = "void",
                Const = false,
                Params = [new ParamDef { Name = "rid", Type = "CanRequestId" }],
                Body = body,
                Doc = "Set the request ID of this message and clear its reserved fields"
            };
        }

        if (s.MessageType is null && !s.ForceClearReservedFields)
        {
            return null;
        }

        List<string> reserved = [.. s.FlatMembers.Where(m => m.Reserved).Select(m => m.Name),
                                 .. s.AllBitFields.Where(f => f.Reserved).Select(f => f.Name),
                                 .. s.ClearAlsoClears];
        return new MethodDef
        {
            Name = "ClearReservedFields",
            ReturnType = "void",
            Const = false,
            Body = [.. reserved.Select(name => new JsonObject { ["set"] = name, ["to"] = ZeroFor(s, name) })],
            Doc = "Clear the reserved fields of this message so that it stays compatible with future uses"
        };
    }

    private static string ZeroFor(StructDef s, string name) =>
        s.AllBitFields.FirstOrDefault(f => f.Name == name)?.Bool == true ? "false" : "0";
}
