using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Thrown when a value does not fit the parameter it is being assigned to, or when the message would
/// overflow. Building a generic message can fail on data that came from a G-code command, so this is a
/// normal outcome rather than a bug.
/// </summary>
public sealed class CanGenericParamException(string message) : Exception(message);

/// <summary>
/// Packs parameters into the data area of a <see cref="CanMessageGeneric"/>.
/// </summary>
/// <remarks>
/// The wire format is the one CANlib's <c>CanMessageGenericParser</c> reads back. Every parameter the
/// message carries has its bit set in <c>paramMap</c> — bit <c>i</c> for entry <c>i</c> of the table — and
/// the values sit in the data area in table order with no padding of any kind:
/// <list type="bullet">
/// <item>a fixed-size parameter takes <see cref="CanParamDescriptor.ItemSize"/> bytes, little-endian;</item>
/// <item>a string takes its bytes plus a null terminator;</item>
/// <item>an array takes one length byte followed by that many elements.</item>
/// </list>
/// Parameters may be added in any order, so each value is inserted at the position its table entry gives
/// it rather than appended, shifting whatever follows.
/// </remarks>
public sealed class CanGenericWriter(ImmutableArray<CanParamDescriptor> table)
{
    private CanMessageGeneric _message;
    private int _dataLength;

    /// <summary>Parameter table this message is being built against.</summary>
    public ImmutableArray<CanParamDescriptor> Table { get; } = table;

    /// <summary>Number of data bytes written so far, i.e. what the message's parameters occupy.</summary>
    public int DataLength => _dataLength;

    /// <summary>
    /// The message as built so far. Only the first <see cref="ActualDataLength"/> bytes are meaningful.
    /// </summary>
    public CanMessageGeneric Message => _message;

    /// <summary>
    /// Number of payload bytes to transmit, i.e. the parameter data plus the request ID and parameter map.
    /// </summary>
    public uint ActualDataLength => CanMessageGeneric.GetActualDataLength((uint)_dataLength);

    /// <summary>
    /// The parameter data written so far, without the request ID or parameter map.
    /// </summary>
    /// <remarks>
    /// <see cref="Message"/> is a copy of a struct, so a span cannot be taken over it; this hands back the
    /// meaningful part of the data area instead of the whole 60-byte field.
    /// </remarks>
    public byte[] GetData()
    {
        byte[] data = new byte[_dataLength];
        ((ReadOnlySpan<byte>)_message.Data)[.._dataLength].CopyTo(data);
        return data;
    }

    /// <summary>Add an unsigned parameter, which must be one of the unsigned fixed-size types.</summary>
    public void AddUInt(char letter, uint value)
    {
        CanGenericSlot slot = Reserve(letter);
        switch (slot.Descriptor.Type)
        {
            case CanParamType.UInt32:
                Insert(slot, Bytes(stackalloc byte[4], value, 4));
                break;

            case CanParamType.UInt16 or CanParamType.PwmFreq:
                Require(value <= ushort.MaxValue, letter, $"{value} does not fit in 16 bits");
                Insert(slot, Bytes(stackalloc byte[2], value, 2));
                break;

            case CanParamType.UInt8 or CanParamType.LocalDriver:
                Require(value <= byte.MaxValue, letter, $"{value} does not fit in 8 bits");
                Insert(slot, Bytes(stackalloc byte[1], value, 1));
                break;

            case CanParamType.UInt64:
                Insert(slot, Bytes(stackalloc byte[8], value, 8));
                break;

            default:
                throw WrongType(letter, slot.Descriptor, "an unsigned integer");
        }
    }

    /// <summary>Add a 64-bit unsigned parameter.</summary>
    public void AddUInt64(char letter, ulong value)
    {
        CanGenericSlot slot = Reserve(letter);
        if (slot.Descriptor.Type != CanParamType.UInt64)
        {
            throw WrongType(letter, slot.Descriptor, "a 64-bit unsigned integer");
        }
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Insert(slot, bytes);
    }

    /// <summary>Add a signed parameter, which must be one of the signed fixed-size types.</summary>
    public void AddInt(char letter, int value)
    {
        CanGenericSlot slot = Reserve(letter);
        switch (slot.Descriptor.Type)
        {
            case CanParamType.Int32:
                Insert(slot, Bytes(stackalloc byte[4], (uint)value, 4));
                break;

            case CanParamType.Int16:
                Require(value is >= short.MinValue and <= short.MaxValue, letter, $"{value} does not fit in a signed 16 bits");
                Insert(slot, Bytes(stackalloc byte[2], (uint)value, 2));
                break;

            case CanParamType.Int8:
                Require(value is >= sbyte.MinValue and <= sbyte.MaxValue, letter, $"{value} does not fit in a signed 8 bits");
                Insert(slot, Bytes(stackalloc byte[1], (uint)value, 1));
                break;

            default:
                throw WrongType(letter, slot.Descriptor, "a signed integer");
        }
    }

    /// <summary>Add a floating-point parameter, as either a float or a half.</summary>
    public void AddFloat(char letter, float value)
    {
        CanGenericSlot slot = Reserve(letter);
        switch (slot.Descriptor.Type)
        {
            case CanParamType.Float:
            {
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
                Insert(slot, bytes);
                break;
            }

            case CanParamType.Float16:
            {
                Span<byte> bytes = stackalloc byte[2];
                BinaryPrimitives.WriteHalfLittleEndian(bytes, (Half)value);
                Insert(slot, bytes);
                break;
            }

            default:
                throw WrongType(letter, slot.Descriptor, "a floating-point value");
        }
    }

    /// <summary>Add a single-character parameter.</summary>
    public void AddChar(char letter, char value)
    {
        CanGenericSlot slot = Reserve(letter);
        if (slot.Descriptor.Type != CanParamType.Char)
        {
            throw WrongType(letter, slot.Descriptor, "a character");
        }
        Require(value <= 0x7F, letter, $"'{value}' is not an ASCII character");
        Insert(slot, [(byte)value]);
    }

    /// <summary>
    /// Add a string parameter, null-terminated on the wire. A reduced string is expected to have had its
    /// board address stripped already, exactly as in RepRapFirmware.
    /// </summary>
    public void AddString(char letter, string value)
    {
        CanGenericSlot slot = Reserve(letter);
        if (slot.Descriptor.Type is not (CanParamType.String or CanParamType.ReducedString))
        {
            throw WrongType(letter, slot.Descriptor, "a string");
        }

        int length = Encoding.UTF8.GetByteCount(value);
        Span<byte> bytes = length + 1 <= 64 ? stackalloc byte[length + 1] : new byte[length + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        bytes[length] = 0;
        Insert(slot, bytes);
    }

    /// <summary>Add a local driver number, which CANlib carries as a single byte.</summary>
    public void AddDriverId(char letter, byte localDriver)
    {
        CanGenericSlot slot = Reserve(letter);
        if (slot.Descriptor.Type != CanParamType.LocalDriver)
        {
            throw WrongType(letter, slot.Descriptor, "a driver ID");
        }
        Insert(slot, [localDriver]);
    }

    /// <summary>Add an unsigned array parameter of any of the three unsigned array types.</summary>
    public void AddUIntArray(char letter, ReadOnlySpan<uint> values)
    {
        CanGenericSlot slot = Reserve(letter);
        if (slot.Descriptor.Type is not (CanParamType.UInt8Array or CanParamType.UInt16Array or CanParamType.UInt32Array))
        {
            throw WrongType(letter, slot.Descriptor, "an unsigned array");
        }
        RequireArrayLength(letter, slot.Descriptor, values.Length);

        int itemSize = slot.Descriptor.ItemSize;
        Span<byte> bytes = stackalloc byte[1 + (values.Length * itemSize)];
        bytes[0] = (byte)values.Length;
        for (int i = 0; i < values.Length; i++)
        {
            uint value = values[i];
            long limit = itemSize == 4 ? uint.MaxValue : (1L << (8 * itemSize)) - 1;
            Require(value <= limit, letter, $"element {i} ({value}) does not fit in {8 * itemSize} bits");
            Bytes(bytes.Slice(1 + (i * itemSize), itemSize), value, itemSize);
        }
        Insert(slot, bytes);
    }

    /// <summary>Add a float array parameter.</summary>
    public void AddFloatArray(char letter, ReadOnlySpan<float> values)
    {
        CanGenericSlot slot = Reserve(letter);
        if (slot.Descriptor.Type != CanParamType.FloatArray)
        {
            throw WrongType(letter, slot.Descriptor, "a float array");
        }
        RequireArrayLength(letter, slot.Descriptor, values.Length);

        Span<byte> bytes = stackalloc byte[1 + (values.Length * sizeof(float))];
        bytes[0] = (byte)values.Length;
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(1 + (i * sizeof(float)), sizeof(float)), values[i]);
        }
        Insert(slot, bytes);
    }

    /// <summary>
    /// Locate where a parameter's value belongs. The position is the sum of the sizes of the parameters
    /// that precede it in the table and are already present, because the receiver finds a value by walking
    /// the table the same way. The parameter is not marked present yet: <see cref="Insert"/> does that once
    /// the value has actually been written, so a validation failure in between (wrong width, wrong type,
    /// message overflow) never leaves <c>paramMap</c> claiming a value that was never stored.
    /// </summary>
    private CanGenericSlot Reserve(char letter)
    {
        if (!CanGenericLayout.TryLocate(_message.Data, _message.ParamMap, Table, letter, out CanGenericSlot slot))
        {
            throw new CanGenericParamException($"'{letter}' is not a parameter of this message");
        }
        if (slot.IsPresent)
        {
            throw new CanGenericParamException($"parameter '{letter}' has already been set");
        }
        return slot;
    }

    private void Insert(CanGenericSlot slot, ReadOnlySpan<byte> value)
    {
        if (_dataLength + value.Length > ByteArray60.Length)
        {
            throw new CanGenericParamException($"CAN message too long: {_dataLength + value.Length} data bytes, maximum is {ByteArray60.Length}");
        }

        Span<byte> data = _message.Data;
        data[slot.Position.._dataLength].CopyTo(data[(slot.Position + value.Length)..]);
        value.CopyTo(data[slot.Position..]);
        _dataLength += value.Length;
        _message.ParamMap |= 1u << slot.Index;
    }

    /// <summary>Write the low <paramref name="size"/> bytes of a value little-endian.</summary>
    private static Span<byte> Bytes(Span<byte> destination, uint value, int size)
    {
        for (int i = 0; i < size; i++)
        {
            destination[i] = (byte)(value >> (8 * i));
        }
        return destination;
    }

    private static Span<byte> Bytes(Span<byte> destination, ulong value, int size)
    {
        for (int i = 0; i < size; i++)
        {
            destination[i] = (byte)(value >> (8 * i));
        }
        return destination;
    }

    private void RequireArrayLength(char letter, CanParamDescriptor descriptor, int length) =>
        Require(length <= descriptor.MaxArrayLength, letter,
            $"{length} elements exceeds the {descriptor.MaxArrayLength} the table allows");

    private static void Require(bool condition, char letter, string problem)
    {
        if (!condition)
        {
            throw new CanGenericParamException($"parameter '{letter}': {problem}");
        }
    }

    private static CanGenericParamException WrongType(char letter, CanParamDescriptor descriptor, string wanted) =>
        new($"parameter '{letter}' is {descriptor.Type}, not {wanted}");
}
