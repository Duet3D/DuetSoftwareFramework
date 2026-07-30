namespace DuetCanMessage.SourceGenerators.Model;

/// <summary>
/// A primitive type of the neutral schema, with its rendering in each target language.
/// </summary>
public sealed record PrimitiveType(string Id, string Cpp, string CSharp, int Size, bool Signed, bool Float)
{
    public bool Integer => !Float;
}

/// <summary>
/// The primitive types understood by the schema, and the rules for mapping schema type names into
/// each target language.
/// </summary>
public static class Types
{
    private static readonly PrimitiveType[] All =
    [
        new("u8",    "uint8_t",  "byte",   1, false, false),
        new("i8",    "int8_t",   "sbyte",  1, true,  false),
        new("u16",   "uint16_t", "ushort", 2, false, false),
        new("i16",   "int16_t",  "short",  2, true,  false),
        new("u32",   "uint32_t", "uint",   4, false, false),
        new("i32",   "int32_t",  "int",    4, true,  false),
        new("u64",   "uint64_t", "ulong",  8, false, false),
        new("i64",   "int64_t",  "long",   8, true,  false),
        new("f16",   "float16_t","Half",   2, true,  true),
        new("f32",   "float",    "float",  4, true,  true),
        new("f64",   "double",   "double", 8, true,  true),
        new("char",  "char",     "byte",   1, false, false),
        new("bool",  "bool",     "bool",   1, false, false),
        // CAN request IDs are 12-bit values carried in a 16-bit field; CANlib gives them their own alias
        new("CanRequestId", "CanRequestId", "ushort", 2, false, false),
        // 'usize' is only used for method return values and parameters, never for a struct field
        new("usize", "size_t",   "uint",   0, false, false)
    ];

    private static readonly Dictionary<string, PrimitiveType> ById = All.ToDictionary(t => t.Id);

    public static bool IsPrimitive(string id) => ById.ContainsKey(id);

    public static PrimitiveType Primitive(string id) =>
        ById.TryGetValue(id, out PrimitiveType? t) ? t : throw new InvalidDataException($"unknown primitive type '{id}'");

    /// <summary>
    /// The unsigned integer type that is wide enough to hold a bitfield of the given width, used as
    /// the backing storage of a bitfield segment on the C# side.
    /// </summary>
    public static string CSharpBackingType(int byteCount) => byteCount switch
    {
        1 => "byte",
        2 => "ushort",
        4 => "uint",
        8 => "ulong",
        _ => throw new InvalidOperationException($"no backing integer of {byteCount} bytes")
    };

    /// <summary>The size in bytes of a schema type, resolving struct references through the schema.</summary>
    public static int SizeOf(CanSchema schema, string type)
    {
        if (IsPrimitive(type))
        {
            PrimitiveType p = Primitive(type);
            if (p.Size == 0)
            {
                throw new InvalidDataException($"type '{type}' has no storage size and cannot be used as a field");
            }
            return p.Size;
        }

        StructDef s = schema.Find(type) ?? throw new InvalidDataException($"unknown type '{type}'");
        if (!s.LayoutDone && s.Size == 0)
        {
            LayoutEngine.Compute(schema, s);
        }
        return s.Size;
    }

    /// <summary>Render a schema type as C++.</summary>
    public static string Cpp(CanSchema schema, string type)
    {
        if (IsPrimitive(type))
        {
            return Primitive(type).Cpp;
        }
        StructDef? s = schema.Find(type);
        return s?.Existing?.CppName ?? type;
    }

    /// <summary>Render a schema type as C#.</summary>
    public static string CSharp(CanSchema schema, string type)
    {
        if (IsPrimitive(type))
        {
            return Primitive(type).CSharp;
        }
        StructDef? s = schema.Find(type);
        return s?.Existing?.CSharpName ?? type;
    }

    /// <summary>
    /// Render a schema type as C# in the context of a struct, substituting the concrete argument of an
    /// expanded template for its parameter.
    /// </summary>
    public static string CSharpIn(CanSchema schema, StructDef context, string type) =>
        CSharp(schema, context.TemplateArg is not null && type == context.TemplateParamName ? context.TemplateArg : type);

    /// <summary>True if the type is a signed integer, which matters when extracting bitfields.</summary>
    public static bool IsSignedInteger(string type) => IsPrimitive(type) && Primitive(type) is { Signed: true, Float: false };
}
