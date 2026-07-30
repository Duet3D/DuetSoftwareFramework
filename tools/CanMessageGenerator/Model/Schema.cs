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
    public List<string> CppIncludes = [];
    public string CSharpNamespace = "";
    public List<string> CSharpUsings = [];
    public Dictionary<string, int> Constants = [];
    public List<StructDef> Structs = [];
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
