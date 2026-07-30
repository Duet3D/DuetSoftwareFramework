using CanMessageGenerator.Expressions;
using CanMessageGenerator.Model;

namespace CanMessageGenerator.Emit;

/// <summary>
/// Emits the C# side of the CAN message formats.
/// </summary>
/// <remarks>
/// Every struct uses <c>LayoutKind.Explicit</c> with generator-computed field offsets rather than
/// relying on the CLR's sequential layout rules, so the byte layout is stated outright and matches the
/// packed C++ structs exactly. Bitfields become properties over a private backing integer (or, when a
/// bitfield run does not start and end on convenient boundaries, over a byte buffer).
/// </remarks>
public sealed class CSharpEmitter(CanSchema schema)
{
    private readonly SortedSet<string> _buffers = [];
    private readonly List<(string Field, string? Doc)> _stringAccessors = [];

    /// <summary>The schema type used for the C# property of a bitfield.</summary>
    public static string BitFieldSchemaType(BitFieldDef f) => f.Bool ? "bool"
        : f.Signed ? (f.Width <= 32 ? "i32" : "i64")
        : f.Width <= 8 ? "u8"
        : f.Width <= 16 ? "u16"
        : f.Width <= 32 ? "u32"
        : "u64";

    public string EmitStructs()
    {
        CodeWriter writer = new("    ");
        Header(writer);
        writer.Line("using System;");
        writer.Line("using System.Numerics;");
        writer.Line("using System.Runtime.InteropServices;");
        foreach (string u in schema.CSharpUsings)
        {
            writer.Line($"using {u};");
        }
        writer.Line();
        writer.Line($"namespace {schema.CSharpNamespace};");
        writer.Line();

        foreach (StructDef s in schema.Structs.Where(s => s.IsGenerated(Language.CSharp)))
        {
            EmitStruct(writer, s);
        }
        return writer.ToString();
    }

    public string EmitBuffers()
    {
        CodeWriter writer = new("    ");
        Header(writer);
        writer.Line("using System;");
        writer.Line("using System.Runtime.CompilerServices;");
        writer.Line();
        writer.Line($"namespace {schema.CSharpNamespace};");
        writer.Line();
        foreach (string buffer in _buffers)
        {
            string[] parts = buffer.Split('|');
            writer.XmlDoc($"Blittable inline buffer of {parts[1]} × {parts[2]}");
            writer.Line($"[InlineArray({parts[1]})]");
            using (writer.Block($"public struct {parts[0]}", "}"))
            {
                writer.Outdent();
                writer.Line("{");
                writer.Indent();
                writer.XmlDoc("Number of elements in this buffer");
                writer.Line($"public const int Length = {parts[1]};");
                writer.Line();
                writer.Line($"private {parts[2]} _element0;");
            }
            writer.Line();
        }
        return writer.ToString();
    }

    public string EmitSupport()
    {
        CodeWriter writer = new("    ");
        Header(writer);
        writer.Line("using System;");
        writer.Line("using System.Buffers.Binary;");
        writer.Line();
        writer.Line($"namespace {schema.CSharpNamespace};");
        writer.Line();

        writer.XmlDoc("System-wide limits shared by the CAN message formats");
        using (writer.Block("public static class CanLimits", "}"))
        {
            writer.Outdent();
            writer.Line("{");
            writer.Indent();
            foreach ((string name, int value) in schema.Constants)
            {
                writer.Line($"public const int {name} = {value};");
            }
        }
        writer.Line();

        writer.XmlDoc("Helpers for the null-terminated text fields carried by some CAN messages");
        using (writer.Block("public static class CanText", "}"))
        {
            writer.Outdent();
            writer.Line("{");
            writer.Indent();
            writer.XmlDoc("Length of the null-terminated string at the start of a text field, capped at maxLength");
            using (writer.Block("public static uint Strnlen(ReadOnlySpan<byte> text, int maxLength)", "}"))
            {
                writer.Outdent();
                writer.Line("{");
                writer.Indent();
                writer.Line("ReadOnlySpan<byte> window = text[..Math.Min(maxLength, text.Length)];");
                writer.Line("int end = window.IndexOf((byte)0);");
                writer.Line("return (uint)(end < 0 ? window.Length : end);");
            }
            writer.Line();
            writer.XmlDoc("Decode a null-terminated text field as a string");
            writer.Line("public static string GetString(ReadOnlySpan<byte> text) => System.Text.Encoding.UTF8.GetString(text[..(int)Strnlen(text, text.Length)]);");
            writer.Line();
            writer.XmlDoc("Copy a string into a fixed-size text field, truncating it if necessary and zero-filling the rest");
            using (writer.Block("public static void SetString(Span<byte> destination, string value)", "}"))
            {
                writer.Outdent();
                writer.Line("{");
                writer.Indent();
                writer.Line("destination.Clear();");
                writer.Line("System.Text.Encoding.UTF8.GetBytes(value.AsSpan(), destination);");
            }
        }
        writer.Line();

        writer.XmlDoc("""
            Read and write bitfields that span a byte buffer.
            A few CANlib messages declare bitfield runs that neither start nor end on a boundary that a
            single backing integer could cover, so their fields are addressed by absolute bit offset instead.
            """);
        using (writer.Block("public static class CanBitFields", "}"))
        {
            writer.Outdent();
            writer.Line("{");
            writer.Indent();
            writer.XmlDoc("Extract width bits starting at bitOffset from a little-endian bit stream");
            using (writer.Block("public static ulong Get(ReadOnlySpan<byte> data, int bitOffset, int width)", "}"))
            {
                writer.Outdent();
                writer.Line("{");
                writer.Indent();
                writer.Line("ulong result = 0;");
                using (writer.Block("for (int i = 0; i < width; i++)", "}"))
                {
                    writer.Outdent();
                    writer.Line("{");
                    writer.Indent();
                    writer.Line("int bit = bitOffset + i;");
                    using (writer.Block("if ((data[bit >> 3] & (1 << (bit & 7))) != 0)", "}"))
                    {
                        writer.Outdent();
                        writer.Line("{");
                        writer.Indent();
                        writer.Line("result |= 1UL << i;");
                    }
                }
                writer.Line("return result;");
            }
            writer.Line();
            writer.XmlDoc("Store width bits of value starting at bitOffset in a little-endian bit stream");
            using (writer.Block("public static void Set(Span<byte> data, int bitOffset, int width, ulong value)", "}"))
            {
                writer.Outdent();
                writer.Line("{");
                writer.Indent();
                using (writer.Block("for (int i = 0; i < width; i++)", "}"))
                {
                    writer.Outdent();
                    writer.Line("{");
                    writer.Indent();
                    writer.Line("int bit = bitOffset + i;");
                    writer.Line("byte mask = (byte)(1 << (bit & 7));");
                    writer.Line("data[bit >> 3] = (byte)(((value >> i) & 1) != 0 ? data[bit >> 3] | mask : data[bit >> 3] & ~mask);");
                }
            }
            writer.Line();
            writer.XmlDoc("Sign-extend a width-bit two's complement value into an int (width must be 32 or less)");
            writer.Line("public static int SignExtend(ulong raw, int width) => width >= 32 ? (int)raw : (int)((uint)raw << (32 - width)) >> (32 - width);");
            writer.Line();
            writer.XmlDoc("Sign-extend a width-bit two's complement value into a long");
            writer.Line("public static long SignExtend64(ulong raw, int width) => width >= 64 ? (long)raw : (long)(raw << (64 - width)) >> (64 - width);");
        }
        writer.Line();
        return writer.ToString();
    }

    public string EmitUnion()
    {
        UnionDef? union = schema.MessageUnion;
        if (union is null)
        {
            return "";
        }

        CodeWriter writer = new("    ");
        Header(writer);
        writer.Line("using System.Runtime.InteropServices;");
        writer.Line();
        writer.Line($"namespace {schema.CSharpNamespace};");
        writer.Line();
        writer.XmlDoc(union.Doc);
        writer.Line($"[StructLayout(LayoutKind.Explicit, Pack = 1, Size = {union.MaxSize})]");
        using (writer.Block($"public struct {union.Name}", "}"))
        {
            writer.Outdent();
            writer.Line("{");
            writer.Indent();
            foreach (UnionMemberDef m in union.Members)
            {
                string type;
                if (m.Length is not null)
                {
                    string element = Types.CSharp(schema, m.Type);
                    type = BufferName(element, int.Parse(m.Length));
                }
                else
                {
                    type = m.CSharpType ?? m.Type;
                }
                writer.Line($"[FieldOffset(0)] public {type} {Naming.Pascal(m.Name)};");
            }
        }
        writer.Line();
        return writer.ToString();
    }

    private static void Header(CodeWriter writer)
    {
        writer.Line("// <auto-generated>");
        writer.Line("//     Generated by tools/CanMessageGenerator from Schema/can-messages.json.");
        writer.Line("//     Do not edit this file directly: edit the schema and re-run the generator, otherwise");
        writer.Line("//     the C# and C++ definitions of these messages will drift apart.");
        writer.Line("// </auto-generated>");
        writer.Line("#nullable enable");
        writer.Line();
    }

    private void EmitStruct(CodeWriter writer, StructDef s)
    {
        writer.XmlDoc(Describe(s));

        bool isMessage = s.Name.StartsWith("CanMessage", StringComparison.Ordinal) && s.TemplateParam is null;
        string bases = isMessage ? $" : ICanMessage<{s.Name}>" : "";
        writer.Line($"[StructLayout(LayoutKind.Explicit, Pack = 1, Size = {Math.Max(s.Size, 1)})]");
        using (writer.Block($"public struct {s.Name}{bases}", "}"))
        {
            writer.Outdent();
            writer.Line("{");
            writer.Indent();

            bool needsBlank = false;
            if (s.MessageType is not null)
            {
                writer.Line("/// <inheritdoc cref=\"ICanMessage{TSelf}.MessageType\" />");
                writer.Line($"public static CanMessageType MessageType => CanMessageType.{Naming.MessageTypeMember(s.MessageType)};");
                needsBlank = true;
            }

            foreach (ConstantDef c in s.Constants)
            {
                if (needsBlank)
                {
                    writer.Line();
                }
                writer.XmlDoc(c.Doc);
                EmitContext constantContext = new(schema, s, Language.CSharp, []);
                string value = new CSharpExprEmitter(constantContext).Render(ExprParser.Parse(c.Value));
                writer.Line($"public const {Types.CSharp(schema, c.Type)} {ConstantName(s, c)} = unchecked(({Types.CSharp(schema, c.Type)})({value}));");
                needsBlank = true;
            }

            _stringAccessors.Clear();
            EmitFields(writer, s, ref needsBlank);
            EmitStringAccessors(writer, ref needsBlank);
            EmitBitFieldProperties(writer, s, ref needsBlank);
            EmitMethods(writer, s, ref needsBlank);
        }
        writer.Line();
    }

    private string Describe(StructDef s)
    {
        string doc = s.Doc ?? s.Name;
        string origin = s.TemplateOf is not null
            ? $"Instantiation of CANlib's {s.TemplateOf}&lt;{Types.Cpp(schema, s.TemplateArg!)}&gt;."
            : s.Existing?.CppHeader is not null
                ? $"Mirrors {s.Name} in CANlib's {s.Existing.CppHeader}."
                : $"Mirrors {s.Name} in CANlib's CanMessageFormats.h.";
        return $"{doc}\n\n{origin} This layout is {Math.Max(s.Size, 1)} bytes.";
    }

    private void EmitFields(CodeWriter writer, StructDef s, ref bool needsBlank)
    {
        // Declare the storage in offset order so that the layout can be read straight off the file
        List<(int Offset, Action Emit)> declarations = [];

        foreach (MemberDef m in s.FlatMembers.Where(m => m.Kind is MemberKind.Field or MemberKind.Array))
        {
            MemberDef member = m;
            declarations.Add((member.Offset, () =>
            {
                writer.XmlDoc(member.Doc);
                string visibility = member.CppPrivate ? "private" : "public";
                string type = member.Kind == MemberKind.Array
                    ? BufferName(member.Type == "char" ? "char" : Types.CSharpIn(schema, s, member.Type), member.ResolvedLength)
                    : Types.CSharpIn(schema, s, member.Type);
                writer.Line($"[FieldOffset({member.Offset})] {visibility} {type} {Naming.Pascal(member.Name)};");
                if (member.Kind == MemberKind.Array && member.Type == "char")
                {
                    _stringAccessors.Add((Naming.Pascal(member.Name), member.Doc));
                }
            }
            ));
        }

        foreach (BitSegment segment in LayoutEngine.SegmentsOf(s))
        {
            BitSegment seg = segment;
            declarations.Add((seg.Offset, () =>
            {
                string names = string.Join(", ", seg.Fields.Select(f => $"{f.Name}:{f.Width}"));
                writer.XmlDoc($"Backing storage for the bitfields {names}");
                string type = seg.NeedsByteBuffer ? BufferName("byte", seg.ByteCount) : Types.CSharpBackingType(seg.ByteCount);
                writer.Line($"[FieldOffset({seg.Offset})] private {type} _bits{seg.Index};");
            }
            ));
        }

        foreach ((_, Action emit) in declarations.OrderBy(d => d.Offset))
        {
            if (needsBlank)
            {
                writer.Line();
            }
            needsBlank = true;
            emit();
        }
    }

    /// <summary>
    /// Emit a string view over each fixed-size char array. CANlib treats these fields as null-terminated
    /// text, so exposing them as strings saves every caller from re-implementing the same decoding.
    /// </summary>
    private void EmitStringAccessors(CodeWriter writer, ref bool needsBlank)
    {
        foreach ((string field, string? doc) in _stringAccessors)
        {
            if (needsBlank)
            {
                writer.Line();
            }
            needsBlank = true;
            writer.XmlDoc(doc is null
                ? $"{field} as a string, decoded up to the first null byte"
                : $"{doc}\n(decoded up to the first null byte; setting it truncates to the field size and zero-fills the rest)");
            using (writer.Block($"public string {field}String", "}"))
            {
                writer.Outdent();
                writer.Line("{");
                writer.Indent();
                writer.Line($"readonly get => CanText.GetString({field});");
                writer.Line($"set => CanText.SetString({field}, value);");
            }
        }
    }

    private void EmitBitFieldProperties(CodeWriter writer, StructDef s, ref bool needsBlank)
    {
        foreach (BitSegment segment in LayoutEngine.SegmentsOf(s))
        {
            foreach (BitFieldDef f in segment.Fields)
            {
                if (needsBlank)
                {
                    writer.Line();
                }
                needsBlank = true;

                string type = Types.CSharp(schema, BitFieldSchemaType(f));
                // Reserved fields stay public, exactly as they are in the C++ structs, so that the
                // generated conformance test can pin down their bit positions too
                const string visibility = "public";
                string range = f.Width == 1 ? $"bit {f.BitOffset}" : $"bits {f.BitOffset}-{f.BitOffset + f.Width - 1}";
                string doc = f.Doc is null
                    ? $"{Naming.Pascal(f.Name)} ({f.Width}-bit field, {range} of the message)"
                    : $"{f.Doc}\n({f.Width}-bit field, {range} of the message)";
                writer.XmlDoc(doc);

                using (writer.Block($"{visibility} {type} {Naming.Pascal(f.Name)}", "}"))
                {
                    writer.Outdent();
                    writer.Line("{");
                    writer.Indent();
                    writer.Line($"readonly get => {Getter(segment, f, type)};");
                    writer.Line($"set => {Setter(segment, f, type)};");
                }
            }
        }
    }

    /// <summary>
    /// The sign-extension helper that matches the property type chosen by <see cref="BitFieldSchemaType"/>:
    /// fields up to 32 bits wide are exposed as int, wider ones as long.
    /// </summary>
    private static string SignExtender(BitFieldDef f) => f.Width <= 32 ? "SignExtend" : "SignExtend64";

    private static string Getter(BitSegment segment, BitFieldDef f, string type)
    {
        int shift = f.BitOffset - 8 * segment.Offset;
        if (segment.NeedsByteBuffer)
        {
            string raw = $"CanBitFields.Get(_bits{segment.Index}, {shift}, {f.Width})";
            return f.Bool ? $"{raw} != 0"
                : f.Signed ? $"CanBitFields.{SignExtender(f)}({raw}, {f.Width})"
                : $"({type}){raw}";
        }

        string backing = Types.CSharpBackingType(segment.ByteCount);
        string arith = backing == "ulong" ? "ulong" : "uint";
        string suffix = arith == "ulong" ? "UL" : "U";
        string shifted = shift == 0 ? $"(({arith})_bits{segment.Index})" : $"((({arith})_bits{segment.Index}) >> {shift})";
        string mask = Mask(f.Width, suffix);
        if (f.Bool)
        {
            return $"({shifted} & 1{suffix}) != 0";
        }
        return f.Signed
            ? $"CanBitFields.{SignExtender(f)}({shifted} & {mask}, {f.Width})"
            : $"({type})({shifted} & {mask})";
    }

    private static string Setter(BitSegment segment, BitFieldDef f, string type)
    {
        int shift = f.BitOffset - 8 * segment.Offset;
        if (segment.NeedsByteBuffer)
        {
            string v = f.Bool ? "(value ? 1UL : 0UL)" : $"unchecked((ulong)value)";
            return $"CanBitFields.Set(_bits{segment.Index}, {shift}, {f.Width}, {v})";
        }

        string backing = Types.CSharpBackingType(segment.ByteCount);
        string arith = backing == "ulong" ? "ulong" : "uint";
        string suffix = arith == "ulong" ? "UL" : "U";
        string mask = Mask(f.Width, suffix);
        string clear = shift == 0 ? $"~{mask}" : $"~({mask} << {shift})";
        string raw = f.Bool ? $"(value ? 1{suffix} : 0{suffix})" : $"(unchecked(({arith})value) & {mask})";
        string placed = shift == 0 ? raw : $"({raw} << {shift})";
        return $"_bits{segment.Index} = ({backing})(((({arith})_bits{segment.Index}) & {clear}) | {placed})";
    }

    private static string Mask(int width, string suffix)
    {
        ulong mask = width >= 64 ? ulong.MaxValue : (1UL << width) - 1;
        return $"0x{mask:X}{suffix}";
    }

    private void EmitMethods(CodeWriter writer, StructDef s, ref bool needsBlank)
    {
        List<MethodDef> methods = [.. s.Methods.Where(m => m.Emit.Contains(Language.CSharp) && !m.DeclarationOnly)];
        MethodDef? synthesised = Synthesise.RequestIdOrClear(schema, s);
        if (synthesised is not null)
        {
            methods.Add(synthesised);
        }

        foreach (MethodDef m in methods)
        {
            if (needsBlank)
            {
                writer.Line();
            }
            needsBlank = true;
            writer.XmlDoc(m.Doc);

            string returnType = m.ReturnType == "void" ? "void" : Types.CSharpIn(schema, s, m.ReturnType);
            string parameters = string.Join(", ", m.Params.Select(p => $"{Types.CSharpIn(schema, s, p.Type)} {p.Name}"));
            string modifiers = m.Static ? "public static" : m.Const ? "public readonly" : "public";

            EmitContext context = new(schema, s, Language.CSharp, m.Params.Select(p => p.Name));
            foreach (ParamDef p in m.Params.Where(p => p.Type == "bool"))
            {
                context.MarkBoolLocal(p.Name);
            }

            List<Stmt> body = ExprParser.ParseBody(m.Body);
            CSharpStatementEmitter statements = new(context, m.ReturnType);
            string name = Naming.Pascal(m.Name);

            if (body.Count == 0)
            {
                writer.Line($"{modifiers} {returnType} {name}({parameters}) {{ }}");
                continue;
            }
            if (body.Count == 1 && body[0] is ReturnStmt { Value: not null } single)
            {
                writer.Line($"{modifiers} {returnType} {name}({parameters}) => {statements.Render(single)[7..^1]};");
                continue;
            }

            using (writer.Block($"{modifiers} {returnType} {name}({parameters})", "}"))
            {
                writer.Outdent();
                writer.Line("{");
                writer.Indent();
                statements.Write(writer, body);
            }
        }
    }

    /// <summary>
    /// The C# name of a struct constant. C++ distinguishes a constant such as <c>Passwd</c> from a field
    /// named <c>passwd</c> by case alone, which PascalCasing would collapse, so such constants take a
    /// "Value" suffix.
    /// </summary>
    public static string ConstantName(StructDef s, ConstantDef c)
    {
        string name = Naming.Pascal(c.Name);
        bool collides = s.ScalarNames.Any(n => Naming.Pascal(n) == name)
                        || s.FlatMembers.Any(m => Naming.Pascal(m.Name) == name);
        return collides ? name + "Value" : name;
    }

    /// <summary>Register and name the inline array type used to represent a fixed-length C array.</summary>
    private string BufferName(string element, int length)
    {
        string csharpElement = element == "char" ? "byte" : element;
        string name = $"{char.ToUpperInvariant(element[0])}{element[1..]}Array{length}";
        _buffers.Add($"{name}|{length}|{csharpElement}");
        return name;
    }
}
