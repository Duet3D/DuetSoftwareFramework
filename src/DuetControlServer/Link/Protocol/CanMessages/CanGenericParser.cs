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
/// <c>CanMessageGenericParser</c>. DCS only sends these messages, so this exists mainly so that a test can
/// read back what the writer produced and check that the two agree about the format — the same job CANlib's
/// parser does on the expansion board.
/// </remarks>
public sealed class CanGenericParser(CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table)
{
    private readonly CanMessageGeneric _message = message;

    /// <summary>Parameter table this message is being read against.</summary>
    public ImmutableArray<CanParamDescriptor> Table { get; } = table;

    /// <summary>True if the message carries the given parameter.</summary>
    public bool Has(char letter) => TryFind(letter, out _, out _);

    /// <summary>Read an unsigned parameter, or null if the message does not carry it.</summary>
    public uint? GetUInt(char letter)
    {
        if (!TryFind(letter, out int position, out CanParamDescriptor descriptor))
        {
            return null;
        }
        ReadOnlySpan<byte> data = Data(position, descriptor.ItemSize);
        return descriptor.Type switch
        {
            CanParamType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(data),
            CanParamType.UInt16 or CanParamType.PwmFreq => BinaryPrimitives.ReadUInt16LittleEndian(data),
            CanParamType.UInt8 or CanParamType.LocalDriver => data[0],
            _ => null
        };
    }

    /// <summary>Read a 64-bit unsigned parameter, or null if the message does not carry it.</summary>
    public ulong? GetUInt64(char letter) =>
        TryFind(letter, out int position, out CanParamDescriptor descriptor) && descriptor.Type == CanParamType.UInt64
            ? BinaryPrimitives.ReadUInt64LittleEndian(Data(position, 8))
            : null;

    /// <summary>Read a signed parameter, or null if the message does not carry it.</summary>
    public int? GetInt(char letter)
    {
        if (!TryFind(letter, out int position, out CanParamDescriptor descriptor))
        {
            return null;
        }
        ReadOnlySpan<byte> data = Data(position, descriptor.ItemSize);
        return descriptor.Type switch
        {
            CanParamType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(data),
            CanParamType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(data),
            CanParamType.Int8 => (sbyte)data[0],
            _ => null
        };
    }

    /// <summary>Read a floating-point parameter, or null if the message does not carry it.</summary>
    public float? GetFloat(char letter)
    {
        if (!TryFind(letter, out int position, out CanParamDescriptor descriptor))
        {
            return null;
        }
        ReadOnlySpan<byte> data = Data(position, descriptor.ItemSize);
        return descriptor.Type switch
        {
            CanParamType.Float => BinaryPrimitives.ReadSingleLittleEndian(data),
            CanParamType.Float16 => (float)BinaryPrimitives.ReadHalfLittleEndian(data),
            _ => null
        };
    }

    /// <summary>Read a single-character parameter, or null if the message does not carry it.</summary>
    public char? GetChar(char letter) =>
        TryFind(letter, out int position, out CanParamDescriptor descriptor) && descriptor.Type == CanParamType.Char
            ? (char)_message.Data[position]
            : null;

    /// <summary>Read a string parameter, or null if the message does not carry it.</summary>
    public string? GetString(char letter)
    {
        if (!TryFind(letter, out int position, out CanParamDescriptor descriptor)
            || descriptor.Type is not (CanParamType.String or CanParamType.ReducedString))
        {
            return null;
        }
        ReadOnlySpan<byte> data = _message.Data;
        int end = data[position..].IndexOf((byte)0);
        return Encoding.UTF8.GetString(data.Slice(position, end < 0 ? data.Length - position : end));
    }

    /// <summary>Read an unsigned array parameter, or null if the message does not carry it.</summary>
    public uint[]? GetUIntArray(char letter)
    {
        if (!TryFind(letter, out int position, out CanParamDescriptor descriptor)
            || descriptor.Type is not (CanParamType.UInt8Array or CanParamType.UInt16Array or CanParamType.UInt32Array))
        {
            return null;
        }

        ReadOnlySpan<byte> data = _message.Data;
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
    public float[]? GetFloatArray(char letter)
    {
        if (!TryFind(letter, out int position, out CanParamDescriptor descriptor) || descriptor.Type != CanParamType.FloatArray)
        {
            return null;
        }

        ReadOnlySpan<byte> data = _message.Data;
        int count = data[position];
        float[] values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(position + 1 + (i * sizeof(float)), sizeof(float)));
        }
        return values;
    }

    /// <summary>
    /// Locate a parameter by walking the table and skipping over the parameters that precede it and are
    /// present, which is exactly how CANlib's parser finds it on the expansion board.
    /// </summary>
    private bool TryFind(char letter, out int position, out CanParamDescriptor descriptor)
    {
        position = 0;
        descriptor = default;

        ReadOnlySpan<CanParamDescriptor> table = Table.AsSpan();
        ReadOnlySpan<byte> data = _message.Data;
        for (int index = 0; index < table.Length; index++)
        {
            CanParamDescriptor candidate = table[index];
            bool present = (_message.ParamMap & (1u << index)) != 0;
            if (candidate.Letter == letter)
            {
                descriptor = candidate;
                return present;
            }
            if (!present)
            {
                continue;
            }

            if (candidate.IsArray)
            {
                position += 1 + (data[position] * candidate.ItemSize);
            }
            else if (candidate.ItemSize != 0)
            {
                position += candidate.ItemSize;
            }
            else
            {
                // The only zero-size entries are the strings, which run to and include their terminator
                int end = position;
                while (data[end] != 0)
                {
                    end++;
                }
                position = end + 1;
            }
        }
        return false;
    }

    private ReadOnlySpan<byte> Data(int position, int size) => ((ReadOnlySpan<byte>)_message.Data).Slice(position, size);
}
