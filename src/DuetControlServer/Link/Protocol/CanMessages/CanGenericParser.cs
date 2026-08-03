using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Reads parameters back out of a <see cref="CanMessageGeneric"/>.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="CanGenericWriter"/>, and a port of CANlib's
/// <c>CanMessageGenericParser</c>. It is what the generated message types read their properties through, and
/// what lets a test check that the two ends agree about the format — the same job CANlib's parser does on
/// the expansion board.
/// <para>
/// A parameter that the message does not carry reads back as null, as does one whose table entry is of
/// another kind than the getter asks for; a letter that is not in the table at all can only come from the
/// letter-keyed path, and reads back as null too.
/// </para>
/// </remarks>
public static class CanGenericParser
{
    /// <summary>True if the message carries the given parameter.</summary>
    public static bool Has(in CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter) =>
        TryFind(message, table, letter, out _, out _);

    /// <summary>Read an unsigned parameter, or null if the message does not carry it.</summary>
    public static uint? GetUInt(in CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter)
    {
        if (!TryFind(message, table, letter, out int position, out CanParamDescriptor descriptor))
        {
            return null;
        }
        ReadOnlySpan<byte> data = Data(message, position, descriptor.ItemSize);
        return descriptor.Type switch
        {
            CanParamType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(data),
            CanParamType.UInt16 or CanParamType.PwmFreq => BinaryPrimitives.ReadUInt16LittleEndian(data),
            CanParamType.UInt8 or CanParamType.LocalDriver => data[0],
            _ => null
        };
    }

    /// <summary>Read a 64-bit unsigned parameter, or null if the message does not carry it.</summary>
    public static ulong? GetUInt64(in CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter) =>
        TryFind(message, table, letter, out int position, out CanParamDescriptor descriptor) && descriptor.Type == CanParamType.UInt64
            ? BinaryPrimitives.ReadUInt64LittleEndian(Data(message, position, 8))
            : null;

    /// <summary>Read a signed parameter, or null if the message does not carry it.</summary>
    public static int? GetInt(in CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter)
    {
        if (!TryFind(message, table, letter, out int position, out CanParamDescriptor descriptor))
        {
            return null;
        }
        ReadOnlySpan<byte> data = Data(message, position, descriptor.ItemSize);
        return descriptor.Type switch
        {
            CanParamType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(data),
            CanParamType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(data),
            CanParamType.Int8 => (sbyte)data[0],
            _ => null
        };
    }

    /// <summary>Read a floating-point parameter, or null if the message does not carry it.</summary>
    public static float? GetFloat(in CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter)
    {
        if (!TryFind(message, table, letter, out int position, out CanParamDescriptor descriptor))
        {
            return null;
        }
        ReadOnlySpan<byte> data = Data(message, position, descriptor.ItemSize);
        return descriptor.Type switch
        {
            CanParamType.Float => BinaryPrimitives.ReadSingleLittleEndian(data),
            CanParamType.Float16 => (float)BinaryPrimitives.ReadHalfLittleEndian(data),
            _ => null
        };
    }

    /// <summary>Read a single-character parameter, or null if the message does not carry it.</summary>
    public static char? GetChar(in CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter) =>
        TryFind(message, table, letter, out int position, out CanParamDescriptor descriptor) && descriptor.Type == CanParamType.Char
            ? (char)message.Data[position]
            : null;

    /// <summary>Read a string parameter, or null if the message does not carry it.</summary>
    public static string? GetString(in CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter)
    {
        if (!TryFind(message, table, letter, out int position, out CanParamDescriptor descriptor)
            || descriptor.Type is not (CanParamType.String or CanParamType.ReducedString))
        {
            return null;
        }
        ReadOnlySpan<byte> data = message.Data;
        int end = data[position..].IndexOf((byte)0);
        return Encoding.UTF8.GetString(data.Slice(position, end < 0 ? data.Length - position : end));
    }

    /// <summary>Read an unsigned array parameter, or null if the message does not carry it.</summary>
    public static uint[]? GetUIntArray(in CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter)
    {
        if (!TryFind(message, table, letter, out int position, out CanParamDescriptor descriptor)
            || descriptor.Type is not (CanParamType.UInt8Array or CanParamType.UInt16Array or CanParamType.UInt32Array))
        {
            return null;
        }

        ReadOnlySpan<byte> data = message.Data;
        int count = data[position];
        int itemSize = descriptor.ItemSize;
        uint[] values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> element = data.Slice(position + 1 + (i * itemSize), itemSize);
            values[i] = itemSize switch
            {
                1 => element[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(element),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(element)
            };
        }
        return values;
    }

    /// <summary>Read a float array parameter, or null if the message does not carry it.</summary>
    public static float[]? GetFloatArray(in CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter)
    {
        if (!TryFind(message, table, letter, out int position, out CanParamDescriptor descriptor) || descriptor.Type != CanParamType.FloatArray)
        {
            return null;
        }

        ReadOnlySpan<byte> data = message.Data;
        int count = data[position];
        float[] values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(position + 1 + (i * sizeof(float)), sizeof(float)));
        }
        return values;
    }

    /// <summary>
    /// The parameter data the message carries, without the request ID or parameter map.
    /// </summary>
    /// <remarks>
    /// The data area is a fixed 60 bytes; this hands back only the part of it that the present parameters
    /// occupy, which is what goes on the bus.
    /// </remarks>
    public static byte[] GetData(in CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table)
    {
        ReadOnlySpan<byte> data = message.Data;
        return data[..CanGenericLayout.DataLength(data, message.ParamMap, table)].ToArray();
    }

    /// <summary>
    /// Locate a parameter by walking the table and skipping over the parameters that precede it and are
    /// present, which is exactly how CANlib's parser finds it on the expansion board.
    /// </summary>
    private static bool TryFind(in CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter, out int position, out CanParamDescriptor descriptor)
    {
        bool inTable = CanGenericLayout.TryLocate(message.Data, message.ParamMap, table, letter, out CanGenericSlot slot);
        position = slot.Position;
        descriptor = slot.Descriptor;
        return inTable && slot.IsPresent;
    }

    private static ReadOnlySpan<byte> Data(in CanMessageGeneric message, int position, int size) =>
        ((ReadOnlySpan<byte>)message.Data).Slice(position, size);
}
