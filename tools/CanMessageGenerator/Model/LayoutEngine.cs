namespace CanMessageGenerator.Model;

/// <summary>
/// One contiguous run of bitfields, split into segments that the C# emitter can back with a single
/// integer. A segment that cannot be backed by a 1, 2, 4 or 8 byte integer falls back to a byte buffer.
/// </summary>
public sealed class BitSegment
{
    /// <summary>Byte offset of the segment from the start of the struct.</summary>
    public int Offset;

    /// <summary>Size of the segment in bytes.</summary>
    public int ByteCount;

    /// <summary>True if the segment must be backed by a byte buffer rather than a single integer.</summary>
    public bool NeedsByteBuffer;

    /// <summary>The bitfields in this segment, in declaration order.</summary>
    public List<BitFieldDef> Fields = [];

    /// <summary>Index of the segment within its struct, used to name the backing field.</summary>
    public int Index;
}

/// <summary>
/// Computes the memory layout of every struct in the schema.
/// </summary>
/// <remarks>
/// This reproduces the layout that GCC gives a <c>__attribute__((packed))</c> struct on a little-endian
/// target, which was verified empirically against g++:
/// <list type="bullet">
/// <item>a bit cursor runs through the struct;</item>
/// <item>a bitfield is placed at the cursor and advances it by its width, with no padding and no regard
/// for the declared storage type, so bitfields straddle both byte and storage-unit boundaries and
/// consecutive groups of different storage types pack together;</item>
/// <item>a non-bitfield member first rounds the cursor up to the next byte boundary;</item>
/// <item>the struct size is the cursor rounded up to a whole number of bytes.</item>
/// </list>
/// Non-packed structs (which in this schema contain only naturally aligned members) additionally round
/// each member up to its own alignment and the total size up to the largest member alignment.
/// </remarks>
public static class LayoutEngine
{
    /// <summary>Bit segments of each struct, keyed by struct name.</summary>
    private static readonly Dictionary<string, List<BitSegment>> Segments = [];

    public static void ComputeAll(CanSchema schema)
    {
        foreach (StructDef s in schema.Structs)
        {
            Compute(schema, s);
        }
    }

    public static List<BitSegment> SegmentsOf(StructDef s) => Segments.TryGetValue(s.Name, out List<BitSegment>? v) ? v : [];

    public static void Compute(CanSchema schema, StructDef s)
    {
        if (s.LayoutDone)
        {
            return;
        }
        s.LayoutDone = true;                                    // set first so that a cycle is reported as a missing size rather than a hang

        if (s.TemplateParam is not null)
        {
            return;                                             // a template has no layout of its own; its expansions do
        }

        if (!s.IsGenerated(Language.Cpp) && !s.IsGenerated(Language.CSharp) && s.Size > 0)
        {
            return;                                             // a purely referenced type with a declared size, e.g. CanTiming
        }

        foreach (MemberDef m in s.Members)
        {
            ResolveLengths(schema, m);
        }

        if (s.IsUnion)
        {
            ComputeUnion(schema, s);
        }
        else
        {
            ComputeStruct(schema, s);
        }
    }

    private static void ResolveLengths(CanSchema schema, MemberDef m)
    {
        if (m.Kind == MemberKind.Array)
        {
            m.ResolvedLength = ResolveLength(schema, m.Length ?? throw new InvalidDataException($"array {m.Name} has no length"));
        }
        foreach (MemberDef alt in m.Alternatives)
        {
            ResolveLengths(schema, alt);
        }
    }

    private static int ResolveLength(CanSchema schema, string length) =>
        int.TryParse(length, out int n) ? n
            : schema.Constants.TryGetValue(length, out int c) ? c
            : throw new InvalidDataException($"unknown array length '{length}'");

    private static void ComputeUnion(CanSchema schema, StructDef s)
    {
        int size = 0;
        List<BitSegment> segments = [];
        foreach (MemberDef m in s.Members)
        {
            if (m.Kind == MemberKind.Bitfield)
            {
                int bits = m.Fields.Sum(f => f.Width);
                int bytes = (bits + 7) / 8;
                int bit = 0;
                foreach (BitFieldDef f in m.Fields)
                {
                    f.BitOffset = bit;
                    bit += f.Width;
                }
                m.Offset = 0;
                m.Size = bytes;
                segments.Add(new BitSegment
                {
                    Offset = 0,
                    ByteCount = bytes,
                    NeedsByteBuffer = bytes is not (1 or 2 or 4 or 8),
                    Fields = [.. m.Fields],
                    Index = segments.Count
                });
                size = Math.Max(size, bytes);
            }
            else
            {
                m.Offset = 0;
                m.Size = MemberSize(schema, m);
                size = Math.Max(size, m.Size);
            }
        }
        s.Size = size;
        Segments[s.Name] = segments;
    }

    private static void ComputeStruct(CanSchema schema, StructDef s)
    {
        int bitCursor = 0;
        int maxAlign = 1;
        List<BitSegment> segments = [];
        List<List<BitFieldDef>> pendingRun = [];
        int pendingRunStartBit = 0;

        void FlushRun()
        {
            if (pendingRun.Count == 0)
            {
                return;
            }
            segments.AddRange(SplitRun(pendingRun, pendingRunStartBit, segments.Count));
            pendingRun = [];
        }

        foreach (MemberDef m in s.Members)
        {
            if (m.Kind == MemberKind.Bitfield)
            {
                if (pendingRun.Count == 0)
                {
                    pendingRunStartBit = bitCursor;
                }
                foreach (BitFieldDef f in m.Fields)
                {
                    if (f.Width > 8 * Types.Primitive(m.Storage).Size)
                    {
                        throw new InvalidDataException($"{s.Name}.{f.Name} is {f.Width} bits wide, which does not fit in {m.Storage}");
                    }
                    f.BitOffset = bitCursor;
                    bitCursor += f.Width;
                }
                pendingRun.Add(m.Fields);
                m.Offset = m.Fields.Count > 0 ? m.Fields[0].BitOffset / 8 : (bitCursor + 7) / 8;
                m.Size = (m.Fields.Sum(f => f.Width) + 7) / 8;
                continue;
            }

            FlushRun();

            // A non-bitfield member starts on a byte boundary
            int byteCursor = (bitCursor + 7) / 8;
            if (!s.Packed)
            {
                int align = MemberAlignment(schema, m);
                maxAlign = Math.Max(maxAlign, align);
                byteCursor = (byteCursor + align - 1) / align * align;
            }

            if (m.Kind == MemberKind.Union)
            {
                int unionSize = 0;
                foreach (MemberDef alt in m.Alternatives)
                {
                    alt.Offset = byteCursor;
                    alt.Size = MemberSize(schema, alt);
                    unionSize = Math.Max(unionSize, alt.Size);
                }
                m.Offset = byteCursor;
                m.Size = unionSize;
                bitCursor = 8 * (byteCursor + unionSize);
            }
            else
            {
                m.Offset = byteCursor;
                m.Size = MemberSize(schema, m);
                bitCursor = 8 * (byteCursor + m.Size);
            }
        }

        FlushRun();

        int size = (bitCursor + 7) / 8;
        if (!s.Packed && maxAlign > 1)
        {
            size = (size + maxAlign - 1) / maxAlign * maxAlign;
        }
        s.Size = size;
        Segments[s.Name] = segments;
    }

    /// <summary>
    /// Split a run of consecutive bitfields into segments that the C# emitter can back with one integer
    /// each. Declared groups are accumulated in order and a segment is closed as soon as it is a whole
    /// number of bytes whose length is 1, 2, 4 or 8; anything left over at the end of the run becomes a
    /// byte-buffer segment addressed by absolute bit offset.
    /// </summary>
    /// <remarks>
    /// Splitting on declared groups rather than individual fields keeps the backing storage aligned with
    /// how CANlib declares the message, so a 32-bit group stays one <c>uint</c> instead of four bytes.
    /// </remarks>
    private static IEnumerable<BitSegment> SplitRun(List<List<BitFieldDef>> groups, int startBit, int firstIndex)
    {
        List<BitSegment> result = [];
        List<BitFieldDef> current = [];
        int segmentStartBit = startBit;
        int bits = 0;

        foreach (List<BitFieldDef> group in groups)
        {
            current.AddRange(group);
            bits += group.Sum(f => f.Width);
            if (bits % 8 == 0 && bits / 8 is 1 or 2 or 4 or 8)
            {
                result.Add(new BitSegment
                {
                    Offset = segmentStartBit / 8,
                    ByteCount = bits / 8,
                    NeedsByteBuffer = false,
                    Fields = [.. current],
                    Index = firstIndex + result.Count
                });
                segmentStartBit += bits;
                current = [];
                bits = 0;
            }
        }

        if (current.Count > 0)
        {
            int endBit = current[^1].BitOffset + current[^1].Width;
            result.Add(new BitSegment
            {
                Offset = segmentStartBit / 8,
                ByteCount = (endBit - segmentStartBit + 7) / 8,
                NeedsByteBuffer = true,
                Fields = [.. current],
                Index = firstIndex + result.Count
            });
        }
        return result;
    }

    private static int MemberSize(CanSchema schema, MemberDef m) => m.Kind switch
    {
        MemberKind.Field => Types.SizeOf(schema, m.Type),
        MemberKind.Array => m.ResolvedLength * Types.SizeOf(schema, m.Type),
        _ => throw new InvalidOperationException($"member {m.Name} has no simple size")
    };

    private static int MemberAlignment(CanSchema schema, MemberDef m)
    {
        if (Types.IsPrimitive(m.Type))
        {
            return Types.Primitive(m.Type).Size;
        }
        StructDef s = schema.Find(m.Type) ?? throw new InvalidDataException($"unknown type '{m.Type}'");
        if (s.Packed)
        {
            return 1;
        }
        return s.Members.Where(x => x.Kind is MemberKind.Field or MemberKind.Array)
                        .Select(x => MemberAlignment(schema, x))
                        .DefaultIfEmpty(1)
                        .Max();
    }
}
