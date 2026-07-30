using System.Text.Json;
using System.Text.Json.Nodes;

namespace CanMessageGenerator.Model;

/// <summary>
/// Languages that the generator can emit.
/// </summary>
public enum Language { Cpp, CSharp }

/// <summary>
/// Kinds of struct member. The kind determines both how the member is laid out and how it is emitted.
/// </summary>
public enum MemberKind { Field, Array, Bitfield, Union }

/// <summary>
/// A named constant declared inside a struct.
/// </summary>
public sealed class ConstantDef
{
    public string Name = "";
    public string Type = "u32";
    public string Value = "";
    public string? Doc;
}

/// <summary>
/// One bitfield within a bitfield group.
/// </summary>
public sealed class BitFieldDef
{
    public string Name = "";
    public int Width;
    public bool Bool;
    public bool Signed;
    public bool Reserved;
    public string? Doc;

    /// <summary>How the field is reached in CANlib's own C++ struct, when that differs from the flat schema view.</summary>
    public string? CppAccessPath;

    /// <summary>Bit offset from the start of the containing struct. Filled in by <see cref="LayoutEngine"/>.</summary>
    public int BitOffset;
}

/// <summary>
/// A member of a struct: a scalar field, a fixed-length array, a group of bitfields or an anonymous union.
/// </summary>
public sealed class MemberDef
{
    public MemberKind Kind;
    public string Name = "";
    public string Type = "";
    public string? Doc;

    // Array members
    public string? Length;
    public int ResolvedLength;

    // Bitfield members
    public string Storage = "u32";
    public List<BitFieldDef> Fields = [];

    // Union members
    public bool Anonymous;
    public List<MemberDef> Alternatives = [];

    /// <summary>Set for fields that are not naturally aligned, so the C++ side must use the Load/StoreLE helpers.</summary>
    public bool Unaligned;

    /// <summary>Set for spare fields that must be zeroed; these get no public accessor.</summary>
    public bool Reserved;

    /// <summary>Set for fields that C++ keeps private (because they are unaligned) and exposes through accessors.</summary>
    public bool CppPrivate;

    /// <summary>How the member is reached in CANlib's own C++ struct, when that differs from the flat schema view.</summary>
    public string? CppAccessPath;

    /// <summary>Byte offset from the start of the containing struct. Filled in by <see cref="LayoutEngine"/>.</summary>
    public int Offset;

    /// <summary>Total size in bytes. Filled in by <see cref="LayoutEngine"/>.</summary>
    public int Size;

    public IEnumerable<MemberDef> SelfAndAlternatives => Kind == MemberKind.Union ? Alternatives : [this];
}

/// <summary>
/// A method parameter.
/// </summary>
public sealed class ParamDef
{
    public string Name = "";
    public string Type = "";

    /// <summary>Default argument, C++ only (C# structs in this schema never need one).</summary>
    public string? Default;
}

/// <summary>
/// One entry of a C++ constructor's member initialiser list.
/// </summary>
public sealed class InitDef
{
    public string Name = "";
    public string Value = "";
}

/// <summary>
/// A method declared on a struct. The body is written in the neutral statement/expression language
/// (see <see cref="Expressions"/>) so that it can be rendered into either target language.
/// </summary>
public sealed class MethodDef
{
    public string Name = "";
    public string ReturnType = "void";
    public bool Static;
    public bool Const = true;
    public string? Doc;
    public List<ParamDef> Params = [];
    public JsonArray? Body;
    public HashSet<Language> Emit = [Language.Cpp, Language.CSharp];

    /// <summary>Only a declaration is emitted; the definition lives in hand-written code (e.g. CanMessageFormats.cpp).</summary>
    public bool DeclarationOnly;

    /// <summary>Verbatim C++ declaration, for the few signatures that the neutral language cannot express.</summary>
    public string? CppSignature;

    /// <summary>
    /// True for a constructor: it has no return type, is named after its struct and may carry a member
    /// initialiser list. C# structs cannot reproduce a zero-initialising parameterless constructor
    /// (<c>default</c> and array allocation bypass it), so constructors are emitted for C++ only.
    /// </summary>
    public bool Constructor;

    /// <summary>Whether a single-argument constructor is declared <c>explicit</c>.</summary>
    public bool Explicit;

    /// <summary>Member initialiser list of a constructor.</summary>
    public List<InitDef> Init = [];
}

/// <summary>
/// A concrete instantiation of a template struct.
/// </summary>
public sealed class InstantiationDef
{
    public string Suffix = "";
    public string Arg = "";
}

/// <summary>
/// Where a type that the generator does not emit can be found in a given language.
/// </summary>
public sealed class ExistingDef
{
    public string? CppHeader;
    public string? CppName;
    public string? CSharpNamespace;
    public string? CSharpName;
}

/// <summary>
/// A struct (or union) definition.
/// </summary>
public sealed class StructDef
{
    public string Name = "";
    public string? Doc;
    public bool Packed = true;
    public bool IsUnion;
    public bool CppFinal;
    public string? MessageType;
    public string? NestedIn;
    public string? TemplateParam;
    public List<InstantiationDef> Instantiations = [];
    public HashSet<Language> Emit = [Language.Cpp, Language.CSharp];
    public ExistingDef? Existing;
    public List<ConstantDef> Constants = [];
    public List<MemberDef> Members = [];
    public List<MethodDef> Methods = [];
    public List<string> CppStaticAsserts = [];

    /// <summary>Name of the request ID field, if this message carries one. Drives generation of SetRequestId.</summary>
    public string? RequestIdField;

    /// <summary>Extra non-reserved fields that SetRequestId also clears.</summary>
    public List<string> SetRequestIdAlsoClears = [];

    /// <summary>Extra non-reserved fields that ClearReservedFields also clears.</summary>
    public List<string> ClearAlsoClears = [];

    /// <summary>Size in bytes; either declared (for types the generator does not emit) or computed by <see cref="LayoutEngine"/>.</summary>
    public int Size;

    /// <summary>Set once <see cref="LayoutEngine"/> has processed this struct.</summary>
    public bool LayoutDone;

    /// <summary>For a struct produced by expanding a template, the concrete type substituted for the parameter.</summary>
    public string? TemplateArg;

    /// <summary>For a struct produced by expanding a template, the name of the parameter that was substituted.</summary>
    public string? TemplateParamName;

    /// <summary>For a struct produced by expanding a template, the template it came from.</summary>
    public string? TemplateOf;

    /// <summary>Set for the few non-message structs that nevertheless declare ClearReservedFields.</summary>
    public bool ForceClearReservedFields;

    public bool IsGenerated(Language language) => Emit.Contains(language);

    public IEnumerable<MemberDef> FlatMembers
    {
        get
        {
            foreach (MemberDef member in Members)
            {
                foreach (MemberDef flat in member.SelfAndAlternatives)
                {
                    yield return flat;
                }
            }
        }
    }

    /// <summary>All bitfields of the struct, in declaration order.</summary>
    public IEnumerable<BitFieldDef> AllBitFields =>
        FlatMembers.Where(m => m.Kind == MemberKind.Bitfield).SelectMany(m => m.Fields);

    /// <summary>Every named scalar value in the struct: plain fields plus bitfields.</summary>
    public IEnumerable<string> ScalarNames =>
        FlatMembers.Where(m => m.Kind == MemberKind.Field).Select(m => m.Name)
            .Concat(AllBitFields.Select(f => f.Name));
}

/// <summary>
/// The type of a parameter in a generic message's parameter table.
/// </summary>
/// <remarks>
/// These mirror <c>ParamDescriptor::ParamType</c> in CANlib's <c>CanMessageGenericTableFormat.h</c>. The
/// numeric value is not on the wire, but its low nibble is the element size in bytes, which is what
/// decides how much space the parameter takes in the packed data, so the values have to agree with
/// CANlib's for the two sides to pack identically.
/// </remarks>
public sealed record ParamType(string Id, string CppEnumerator, string CSharpName, int Value, bool IsArray)
{
    /// <summary>Element size in bytes, or 0 for the two string types, which are null-terminated.</summary>
    public int ItemSize => Value & 0x0F;
}

/// <summary>
/// The parameter types understood by the generic message tables.
/// </summary>
public static class ParamTypes
{
    private const int Length1 = 0x01, Length2 = 0x02, Length4 = 0x04, Length8 = 0x08, IsArray = 0x80;

    private static readonly ParamType[] All =
    [
        new("uint64",        "uint64",        "UInt64",        0x00 | Length8, false),
        new("uint32",        "uint32",        "UInt32",        0x00 | Length4, false),
        new("uint16",        "uint16",        "UInt16",        0x00 | Length2, false),
        new("uint8",         "uint8",         "UInt8",         0x00 | Length1, false),
        new("int32",         "int32",         "Int32",         0x10 | Length4, false),
        new("int16",         "int16",         "Int16",         0x10 | Length2, false),
        new("int8",          "int8",          "Int8",          0x10 | Length1, false),
        new("string",        "string",        "String",        0x10,           false),
        new("float",         "float_p",       "Float",         0x20 | Length4, false),
        new("pwmFreq",       "pwmFreq",       "PwmFreq",       0x20 | Length2, false),
        new("char",          "char_p",        "Char",          0x20 | Length1, false),
        new("reducedString", "reducedString", "ReducedString", 0x20,           false),
        new("localDriver",   "localDriver",   "LocalDriver",   0x40 | Length1, false),
        new("float16",       "float16_p",     "Float16",       0x40 | Length2, false),
        new("uint32Array",   "uint32_array",  "UInt32Array",   0x00 | Length4 | IsArray, true),
        new("uint16Array",   "uint16_array",  "UInt16Array",   0x00 | Length2 | IsArray, true),
        new("uint8Array",    "uint8_array",   "UInt8Array",    0x00 | Length1 | IsArray, true),
        new("floatArray",    "float_array",   "FloatArray",    0x20 | Length4 | IsArray, true)
    ];

    private static readonly Dictionary<string, ParamType> ById = All.ToDictionary(t => t.Id);

    public static IReadOnlyList<ParamType> Ordered => All;

    public static ParamType Find(string id) =>
        ById.TryGetValue(id, out ParamType? t) ? t : throw new InvalidDataException($"unknown generic parameter type '{id}'");
}

/// <summary>
/// One parameter of a generic message, identified in the message by its G-code letter.
/// </summary>
public sealed class GenericParamDef
{
    public char Letter;
    public ParamType Type = null!;
    public string? Doc;

    /// <summary>Number of elements, for the array types only.</summary>
    public int MaxLength;

    /// <summary>
    /// True if a G-code command may supply this parameter, i.e. its letter is in A..Z.
    /// </summary>
    /// <remarks>
    /// CANlib puts a parameter outside A..Z to keep it away from G-code while holding its table position, so
    /// that the parameters after it stay on the bits the receiver expects. That covers two different cases,
    /// and only the first is a retired entry:
    /// <list type="bullet">
    /// <item><c>M569.1</c>'s <c>h</c> and <c>M569.6</c>'s status-only <c>v</c>, which are never sent at all;</item>
    /// <item><c>M915</c>'s and <c>ConfigureFilamentMonitor</c>'s <c>d</c>, which carry a driver number that
    /// the sender fills in itself rather than reading off the command.</item>
    /// </list>
    /// Since the two are indistinguishable in the table, the rule is only about where the value may come
    /// from: never from a command, always available to a caller that knows what it is doing.
    /// </remarks>
    public bool CanComeFromGCode => Letter is >= 'A' and <= 'Z';
}

/// <summary>
/// A parameter table for one generic message. The table is the contract between the two ends: the
/// sender packs the present parameters in table order and sets the matching bit in the message's
/// paramMap, and the receiver walks the same table to find them again.
/// </summary>
public sealed class GenericTableDef
{
    public string Name = "";
    public string? Doc;
    public List<GenericParamDef> Params = [];

    /// <summary>
    /// The message type this table is normally sent under, where there is exactly one. Three tables
    /// (M42, M280, M959) have no message type of their own in this version of CANlib.
    /// </summary>
    public string? MessageType;

    /// <summary>The table name without CANlib's "Params" suffix, used to name the generated builder.</summary>
    public string BaseName => Name.EndsWith("Params", StringComparison.Ordinal) ? Name[..^"Params".Length] : Name;
}

/// <summary>
/// One arm of the all-messages union.
/// </summary>
public sealed class UnionMemberDef
{
    public string Name = "";
    public string Type = "";
    public string? CSharpType;
    public string? Length;
}

/// <summary>
/// The all-messages union.
/// </summary>
public sealed class UnionDef
{
    public string Name = "";
    public string? Doc;
    public int MaxSize = 64;
    public List<UnionMemberDef> Members = [];
}

/// <summary>
/// The whole schema, i.e. the neutral description of every CAN message format.
/// </summary>
public sealed class CanSchema
{
    public string CppHeaderGuard = "SRC_CAN_CANMESSAGEFORMATS_H_";
    public string CppTablesHeaderGuard = "SRC_CANMESSAGEGENERICTABLES_H_";
    public List<string> CppIncludes = [];
    public string CSharpNamespace = "";
    public List<string> CSharpUsings = [];
    public Dictionary<string, int> Constants = [];
    public List<StructDef> Structs = [];
    public List<GenericTableDef> GenericTables = [];
    public UnionDef? MessageUnion;

    private Dictionary<string, StructDef>? _byName;

    public Dictionary<string, StructDef> ByName => _byName ??= Structs.ToDictionary(s => s.Name);

    public StructDef? Find(string name) => ByName.GetValueOrDefault(name);

    public static CanSchema Load(string path)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidDataException($"{path} is empty");
        return Parse(root.AsObject());
    }

    private static CanSchema Parse(JsonObject o)
    {
        CanSchema schema = new()
        {
            CppHeaderGuard = Str(o, "cppHeaderGuard") ?? "SRC_CAN_CANMESSAGEFORMATS_H_",
            CppTablesHeaderGuard = Str(o, "cppTablesHeaderGuard") ?? "SRC_CANMESSAGEGENERICTABLES_H_",
            CppIncludes = StrList(o, "cppIncludes"),
            CSharpNamespace = Str(o, "csharpNamespace") ?? "",
            CSharpUsings = StrList(o, "csharpUsings")
        };

        if (o["constants"] is JsonObject constants)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in constants)
            {
                schema.Constants[kv.Key] = kv.Value!.GetValue<int>();
            }
        }

        foreach (JsonNode? node in o["structs"]?.AsArray() ?? [])
        {
            schema.Structs.Add(ParseStruct(node!.AsObject()));
        }

        foreach (JsonNode? node in o["genericTables"]?.AsArray() ?? [])
        {
            schema.GenericTables.Add(ParseGenericTable(node!.AsObject()));
        }

        if (o["messageUnion"] is JsonObject union)
        {
            schema.MessageUnion = new UnionDef
            {
                Name = Str(union, "name") ?? "CanMessage",
                Doc = Doc(union),
                MaxSize = Int(union, "maxSize") ?? 64,
                Members = [.. (union["members"]?.AsArray() ?? []).Select(n => new UnionMemberDef
                {
                    Name = Str(n!.AsObject(), "name") ?? "",
                    Type = Str(n.AsObject(), "type") ?? "",
                    CSharpType = Str(n.AsObject(), "csharpType"),
                    Length = Str(n.AsObject(), "length")
                })]
            };
        }
        return schema;
    }

    private static StructDef ParseStruct(JsonObject o)
    {
        StructDef s = new()
        {
            Name = Str(o, "name") ?? throw new InvalidDataException("struct without a name"),
            Doc = Doc(o),
            Packed = Bool(o, "packed") ?? true,
            IsUnion = Bool(o, "isUnion") ?? false,
            CppFinal = Bool(o, "cppFinal") ?? false,
            MessageType = Str(o, "messageType"),
            NestedIn = Str(o, "nestedIn"),
            TemplateParam = Str(o, "templateParam"),
            RequestIdField = Str(o, "requestIdField"),
            SetRequestIdAlsoClears = StrList(o, "setRequestIdAlsoClears"),
            ClearAlsoClears = StrList(o, "clearAlsoClears"),
            CppStaticAsserts = StrList(o, "cppStaticAsserts"),
            ForceClearReservedFields = Bool(o, "clearReservedFields") ?? false,
            Size = Int(o, "size") ?? 0
        };

        if (o["emit"] is JsonArray emit)
        {
            s.Emit = [.. emit.Select(n => ParseLanguage(n!.GetValue<string>()))];
        }

        if (o["existing"] is JsonObject existing)
        {
            s.Existing = new ExistingDef
            {
                CppHeader = existing["cpp"] is JsonObject c ? Str(c, "header") : null,
                CppName = existing["cpp"] is JsonObject c2 ? Str(c2, "name") : null,
                CSharpNamespace = existing["csharp"] is JsonObject cs ? Str(cs, "namespace") : null,
                CSharpName = existing["csharp"] is JsonObject cs2 ? Str(cs2, "name") : null
            };
        }

        foreach (JsonNode? node in o["instantiations"]?.AsArray() ?? [])
        {
            s.Instantiations.Add(new InstantiationDef
            {
                Suffix = Str(node!.AsObject(), "suffix") ?? "",
                Arg = Str(node.AsObject(), "arg") ?? ""
            });
        }

        foreach (JsonNode? node in o["constants"]?.AsArray() ?? [])
        {
            JsonObject c = node!.AsObject();
            s.Constants.Add(new ConstantDef
            {
                Name = Str(c, "name") ?? "",
                Type = Str(c, "type") ?? "u32",
                Value = Str(c, "value") ?? "0",
                Doc = Doc(c)
            });
        }

        foreach (JsonNode? node in o["members"]?.AsArray() ?? [])
        {
            s.Members.Add(ParseMember(node!.AsObject()));
        }

        foreach (JsonNode? node in o["methods"]?.AsArray() ?? [])
        {
            s.Methods.Add(ParseMethod(node!.AsObject()));
        }

        foreach (JsonNode? node in o["constructors"]?.AsArray() ?? [])
        {
            s.Methods.Add(ParseConstructor(node!.AsObject(), s.Name));
        }
        return s;
    }

    private static GenericTableDef ParseGenericTable(JsonObject o)
    {
        GenericTableDef table = new()
        {
            Name = Str(o, "name") ?? throw new InvalidDataException("generic table without a name"),
            Doc = Doc(o),
            MessageType = Str(o, "messageType")
        };

        foreach (JsonNode? node in o["params"]?.AsArray() ?? [])
        {
            JsonObject p = node!.AsObject();
            string letter = Str(p, "letter") ?? throw new InvalidDataException($"{table.Name} has a parameter without a letter");
            if (letter.Length != 1)
            {
                throw new InvalidDataException($"{table.Name} parameter '{letter}' must be a single letter");
            }
            ParamType type = ParamTypes.Find(Str(p, "type") ?? throw new InvalidDataException($"{table.Name}.{letter} has no type"));
            int maxLength = Int(p, "maxLength") ?? 0;
            if (type.IsArray == (maxLength == 0))
            {
                throw new InvalidDataException(type.IsArray
                    ? $"{table.Name}.{letter} is an array and needs a maxLength"
                    : $"{table.Name}.{letter} is not an array and must not have a maxLength");
            }
            table.Params.Add(new GenericParamDef
            {
                Letter = letter[0],
                Type = type,
                MaxLength = maxLength,
                Doc = Doc(p)
            });
        }

        // A letter identifies a parameter within its table, so a repeat would make one of them
        // unreachable. Letters that differ only by case would collide in the generated C# builder.
        foreach (IGrouping<char, GenericParamDef> group in table.Params.GroupBy(p => char.ToUpperInvariant(p.Letter)))
        {
            if (group.Count() > 1)
            {
                throw new InvalidDataException(
                    $"{table.Name} declares {string.Join(" and ", group.Select(p => $"'{p.Letter}'"))}, which cannot be told apart");
            }
        }
        return table;
    }

    private static MemberDef ParseMember(JsonObject o)
    {
        string kind = Str(o, "kind") ?? "field";
        MemberDef m = new()
        {
            Kind = kind switch
            {
                "field" => MemberKind.Field,
                "array" => MemberKind.Array,
                "bitfield" => MemberKind.Bitfield,
                "union" => MemberKind.Union,
                _ => throw new InvalidDataException($"unknown member kind '{kind}'")
            },
            Name = Str(o, "name") ?? "",
            Type = Str(o, "type") ?? "",
            Doc = Doc(o),
            Length = Str(o, "length"),
            Storage = Str(o, "storage") ?? "u32",
            Anonymous = Bool(o, "anonymous") ?? false,
            Unaligned = Bool(o, "unaligned") ?? false,
            Reserved = Bool(o, "reserved") ?? false,
            CppPrivate = Bool(o, "cppPrivate") ?? false,
            CppAccessPath = Str(o, "cppAccessPath")
        };

        foreach (JsonNode? node in o["fields"]?.AsArray() ?? [])
        {
            JsonObject f = node!.AsObject();
            m.Fields.Add(new BitFieldDef
            {
                Name = Str(f, "name") ?? "",
                Width = Int(f, "width") ?? throw new InvalidDataException($"bitfield {Str(f, "name")} has no width"),
                Bool = Bool(f, "bool") ?? false,
                Signed = Bool(f, "signed") ?? false,
                Reserved = Bool(f, "reserved") ?? false,
                Doc = Doc(f),
                CppAccessPath = m.CppAccessPath
            });
        }

        foreach (JsonNode? node in o["alternatives"]?.AsArray() ?? [])
        {
            m.Alternatives.Add(ParseMember(node!.AsObject()));
        }
        return m;
    }

    private static MethodDef ParseMethod(JsonObject o)
    {
        MethodDef m = new()
        {
            Name = Str(o, "name") ?? "",
            ReturnType = Str(o, "returnType") ?? "void",
            Static = Bool(o, "static") ?? false,
            Const = Bool(o, "const") ?? true,
            Doc = Doc(o),
            Body = o["body"]?.AsArray(),
            DeclarationOnly = Bool(o, "declarationOnly") ?? false,
            CppSignature = Str(o, "cppSignature")
        };
        if (o["emit"] is JsonArray emit)
        {
            m.Emit = [.. emit.Select(n => ParseLanguage(n!.GetValue<string>()))];
        }
        ParseParams(o, m);
        return m;
    }

    /// <summary>
    /// Parse a constructor. Constructors exist only to keep the generated C++ header a source-level
    /// drop-in for CANlib's, so they are never emitted for C#.
    /// </summary>
    private static MethodDef ParseConstructor(JsonObject o, string structName)
    {
        MethodDef m = new()
        {
            Name = structName,
            ReturnType = "void",
            Const = false,
            Constructor = true,
            Explicit = Bool(o, "explicit") ?? false,
            Doc = Doc(o),
            Body = o["body"]?.AsArray(),
            Emit = [Language.Cpp]
        };
        foreach (JsonNode? node in o["init"]?.AsArray() ?? [])
        {
            m.Init.Add(new InitDef
            {
                Name = Str(node!.AsObject(), "name") ?? "",
                Value = Str(node.AsObject(), "value") ?? "0"
            });
        }
        ParseParams(o, m);
        return m;
    }

    private static void ParseParams(JsonObject o, MethodDef m)
    {
        foreach (JsonNode? node in o["params"]?.AsArray() ?? [])
        {
            m.Params.Add(new ParamDef
            {
                Name = Str(node!.AsObject(), "name") ?? "",
                Type = Str(node.AsObject(), "type") ?? "",
                Default = Str(node.AsObject(), "default")
            });
        }
    }

    private static Language ParseLanguage(string s) => s switch
    {
        "cpp" => Language.Cpp,
        "csharp" => Language.CSharp,
        _ => throw new InvalidDataException($"unknown language '{s}'")
    };

    private static string? Str(JsonObject o, string key) => o[key]?.GetValue<string>();
    private static bool? Bool(JsonObject o, string key) => o[key]?.GetValue<bool>();
    private static int? Int(JsonObject o, string key) => o[key]?.GetValue<int>();
    private static List<string> StrList(JsonObject o, string key) =>
        [.. (o[key]?.AsArray() ?? []).Select(n => n!.GetValue<string>())];

    /// <summary>Documentation may be a single string or an array of lines.</summary>
    private static string? Doc(JsonObject o) => o["doc"] switch
    {
        JsonArray a => string.Join('\n', a.Select(n => n!.GetValue<string>())),
        JsonValue v => v.GetValue<string>(),
        _ => null
    };
}
