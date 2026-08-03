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
/// Parameters may be set in any order, so each value is inserted at the position its table entry gives it
/// rather than appended, shifting whatever follows; setting one that is already present replaces it in
/// place, and setting it to <c>null</c> takes it out of the message again.
/// <para>
/// This is the letter-keyed path, for the caller that only knows the table at run time. Where the parameter
/// is known at the call site, prefer the generated message types (<see cref="CanMessageM950Fan"/> and
/// friends), whose properties carry the letter and its type for you.
/// </para>
/// </remarks>
public static partial class CanGenericWriter
{
    /// <summary>Set or, if <paramref name="value"/> is null, remove an unsigned parameter.</summary>
    /// <exception cref="CanGenericParamException">The letter is not in the table, the entry is not one of the
    /// unsigned fixed-size types, or the value does not fit it.</exception>
    public static void SetUInt(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter, uint? value)
    {
        if (value is not uint number)
        {
            Remove(ref message, table, letter);
            return;
        }

        CanParamDescriptor descriptor = Entry(table, letter);
        switch (descriptor.Type)
        {
            case CanParamType.UInt32:
                Set(ref message, table, letter, Bytes(stackalloc byte[4], number, 4));
                break;

            case CanParamType.UInt16 or CanParamType.PwmFreq:
                Require(number <= ushort.MaxValue, letter, $"{number} does not fit in 16 bits");
                Set(ref message, table, letter, Bytes(stackalloc byte[2], number, 2));
                break;

            case CanParamType.UInt8 or CanParamType.LocalDriver:
                Require(number <= byte.MaxValue, letter, $"{number} does not fit in 8 bits");
                Set(ref message, table, letter, Bytes(stackalloc byte[1], number, 1));
                break;

            case CanParamType.UInt64:
                Set(ref message, table, letter, Bytes(stackalloc byte[8], number, 8));
                break;

            default:
                throw WrongType(letter, descriptor, "an unsigned integer");
        }
    }

    /// <summary>Set or, if <paramref name="value"/> is null, remove a 64-bit unsigned parameter.</summary>
    /// <exception cref="CanGenericParamException">The letter is not in the table, or the entry is not a 64-bit unsigned integer.</exception>
    public static void SetUInt64(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter, ulong? value)
    {
        if (value is not ulong number)
        {
            Remove(ref message, table, letter);
            return;
        }

        CanParamDescriptor descriptor = Entry(table, letter);
        if (descriptor.Type != CanParamType.UInt64)
        {
            throw WrongType(letter, descriptor, "a 64-bit unsigned integer");
        }
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, number);
        Set(ref message, table, letter, bytes);
    }

    /// <summary>Set or, if <paramref name="value"/> is null, remove a signed parameter.</summary>
    /// <exception cref="CanGenericParamException">The letter is not in the table, the entry is not one of the
    /// signed fixed-size types, or the value does not fit it.</exception>
    public static void SetInt(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter, int? value)
    {
        if (value is not int number)
        {
            Remove(ref message, table, letter);
            return;
        }

        CanParamDescriptor descriptor = Entry(table, letter);
        switch (descriptor.Type)
        {
            case CanParamType.Int32:
                Set(ref message, table, letter, Bytes(stackalloc byte[4], (uint)number, 4));
                break;

            case CanParamType.Int16:
                Require(number is >= short.MinValue and <= short.MaxValue, letter, $"{number} does not fit in a signed 16 bits");
                Set(ref message, table, letter, Bytes(stackalloc byte[2], (uint)number, 2));
                break;

            case CanParamType.Int8:
                Require(number is >= sbyte.MinValue and <= sbyte.MaxValue, letter, $"{number} does not fit in a signed 8 bits");
                Set(ref message, table, letter, Bytes(stackalloc byte[1], (uint)number, 1));
                break;

            default:
                throw WrongType(letter, descriptor, "a signed integer");
        }
    }

    /// <summary>Set or, if <paramref name="value"/> is null, remove a floating-point parameter.</summary>
    /// <exception cref="CanGenericParamException">The letter is not in the table, or the entry is neither a float nor a half.</exception>
    public static void SetFloat(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter, float? value)
    {
        if (value is not float number)
        {
            Remove(ref message, table, letter);
            return;
        }

        CanParamDescriptor descriptor = Entry(table, letter);
        switch (descriptor.Type)
        {
            case CanParamType.Float:
            {
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(bytes, number);
                Set(ref message, table, letter, bytes);
                break;
            }

            case CanParamType.Float16:
            {
                Span<byte> bytes = stackalloc byte[2];
                BinaryPrimitives.WriteHalfLittleEndian(bytes, (Half)number);
                Set(ref message, table, letter, bytes);
                break;
            }

            default:
                throw WrongType(letter, descriptor, "a floating-point value");
        }
    }

    /// <summary>Set or, if <paramref name="value"/> is null, remove a single-character parameter.</summary>
    /// <exception cref="CanGenericParamException">The letter is not in the table, the entry is not a character, or the value is not ASCII.</exception>
    public static void SetChar(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter, char? value)
    {
        if (value is not char character)
        {
            Remove(ref message, table, letter);
            return;
        }

        CanParamDescriptor descriptor = Entry(table, letter);
        if (descriptor.Type != CanParamType.Char)
        {
            throw WrongType(letter, descriptor, "a character");
        }
        Require(character <= 0x7F, letter, $"'{character}' is not an ASCII character");
        Set(ref message, table, letter, [(byte)character]);
    }

    /// <summary>
    /// Set or, if <paramref name="value"/> is null, remove a string parameter, null-terminated on the wire.
    /// </summary>
    /// <remarks>
    /// A reduced string is written as given: only <see cref="FromCode"/> strips the board address off one,
    /// because that is part of reading a port name off a command, not of packing a string.
    /// </remarks>
    /// <exception cref="CanGenericParamException">The letter is not in the table, or the entry is not a string.</exception>
    public static void SetString(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter, string? value)
    {
        if (value is null)
        {
            Remove(ref message, table, letter);
            return;
        }

        CanParamDescriptor descriptor = Entry(table, letter);
        if (descriptor.Type is not (CanParamType.String or CanParamType.ReducedString))
        {
            throw WrongType(letter, descriptor, "a string");
        }

        int length = Encoding.UTF8.GetByteCount(value);
        Span<byte> bytes = length + 1 <= 64 ? stackalloc byte[length + 1] : new byte[length + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        bytes[length] = 0;
        Set(ref message, table, letter, bytes);
    }

    /// <summary>Set or, if <paramref name="localDriver"/> is null, remove a local driver number, which CANlib carries as a single byte.</summary>
    /// <exception cref="CanGenericParamException">The letter is not in the table, or the entry is not a driver ID.</exception>
    public static void SetDriverId(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter, byte? localDriver)
    {
        if (localDriver is not byte driver)
        {
            Remove(ref message, table, letter);
            return;
        }

        CanParamDescriptor descriptor = Entry(table, letter);
        if (descriptor.Type != CanParamType.LocalDriver)
        {
            throw WrongType(letter, descriptor, "a driver ID");
        }
        Set(ref message, table, letter, [driver]);
    }

    /// <summary>Set or, if <paramref name="values"/> is null, remove an unsigned array parameter.</summary>
    /// <exception cref="CanGenericParamException">The letter is not in the table, the entry is not an unsigned
    /// array, there are more elements than it allows, or one of them does not fit the element width.</exception>
    public static void SetUIntArray(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter, uint[]? values)
    {
        if (values is null)
        {
            Remove(ref message, table, letter);
            return;
        }

        CanParamDescriptor descriptor = Entry(table, letter);
        if (descriptor.Type is not (CanParamType.UInt8Array or CanParamType.UInt16Array or CanParamType.UInt32Array))
        {
            throw WrongType(letter, descriptor, "an unsigned array");
        }
        RequireArrayLength(letter, descriptor, values.Length);

        int itemSize = descriptor.ItemSize;
        Span<byte> bytes = stackalloc byte[1 + (values.Length * itemSize)];
        bytes[0] = (byte)values.Length;
        for (int i = 0; i < values.Length; i++)
        {
            uint value = values[i];
            long limit = itemSize == 4 ? uint.MaxValue : (1L << (8 * itemSize)) - 1;
            Require(value <= limit, letter, $"element {i} ({value}) does not fit in {8 * itemSize} bits");
            Bytes(bytes.Slice(1 + (i * itemSize), itemSize), value, itemSize);
        }
        Set(ref message, table, letter, bytes);
    }

    /// <summary>Set or, if <paramref name="values"/> is null, remove a float array parameter.</summary>
    /// <exception cref="CanGenericParamException">The letter is not in the table, the entry is not a float
    /// array, or there are more elements than it allows.</exception>
    public static void SetFloatArray(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter, float[]? values)
    {
        if (values is null)
        {
            Remove(ref message, table, letter);
            return;
        }

        CanParamDescriptor descriptor = Entry(table, letter);
        if (descriptor.Type != CanParamType.FloatArray)
        {
            throw WrongType(letter, descriptor, "a float array");
        }
        RequireArrayLength(letter, descriptor, values.Length);

        Span<byte> bytes = stackalloc byte[1 + (values.Length * sizeof(float))];
        bytes[0] = (byte)values.Length;
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(1 + (i * sizeof(float)), sizeof(float)), values[i]);
        }
        Set(ref message, table, letter, bytes);
    }

    /// <summary>
    /// Take a parameter out of the message, closing the gap its value leaves in the data area.
    /// </summary>
    /// <returns>False if the message was not carrying the parameter, which is not an error.</returns>
    /// <exception cref="CanGenericParamException">The letter is not in the table at all.</exception>
    public static bool Remove(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter)
    {
        CanGenericSlot slot = Locate(ref message, table, letter);
        if (!slot.IsPresent)
        {
            return false;
        }

        Span<byte> data = message.Data;
        int dataLength = CanGenericLayout.DataLength(data, message.ParamMap, table);
        Delete(data, slot.Position, CanGenericLayout.SizeAt(data, slot.Position, slot.Descriptor), dataLength);
        message.ParamMap &= ~(1u << slot.Index);
        return true;
    }

    /// <summary>
    /// Write a value into the slot its table entry gives it, replacing whatever was there before.
    /// </summary>
    /// <remarks>
    /// The room the value needs is checked before anything is moved, so a message that would overflow is
    /// left exactly as it was rather than losing the value that is being replaced. For the same reason
    /// <c>paramMap</c> is only touched once the bytes are in place: a map claiming a parameter the data area
    /// does not contain would shift every later parameter's offset for the receiver.
    /// </remarks>
    private static void Set(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter, ReadOnlySpan<byte> value)
    {
        CanGenericSlot slot = Locate(ref message, table, letter);
        Span<byte> data = message.Data;
        int dataLength = CanGenericLayout.DataLength(data, message.ParamMap, table);
        int existing = slot.IsPresent ? CanGenericLayout.SizeAt(data, slot.Position, slot.Descriptor) : 0;

        int required = dataLength - existing + value.Length;
        if (required > ByteArray60.Length)
        {
            throw new CanGenericParamException($"CAN message too long: {required} data bytes, maximum is {ByteArray60.Length}");
        }

        if (existing > 0)
        {
            dataLength = Delete(data, slot.Position, existing, dataLength);
        }
        data[slot.Position..dataLength].CopyTo(data[(slot.Position + value.Length)..]);
        value.CopyTo(data[slot.Position..]);
        message.ParamMap |= 1u << slot.Index;
    }

    /// <summary>Close the gap a removed value leaves behind, and report the data length that remains.</summary>
    private static int Delete(Span<byte> data, int position, int size, int dataLength)
    {
        data[(position + size)..dataLength].CopyTo(data[position..]);
        return dataLength - size;
    }

    /// <summary>Find where a parameter's value sits, or would sit, insisting that the table declares it.</summary>
    private static CanGenericSlot Locate(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, char letter)
    {
        if (!CanGenericLayout.TryLocate(message.Data, message.ParamMap, table, letter, out CanGenericSlot slot))
        {
            throw new CanGenericParamException($"'{letter}' is not a parameter of this message");
        }
        return slot;
    }

    /// <summary>The table entry for a letter, which says how the value has to be packed.</summary>
    private static CanParamDescriptor Entry(ImmutableArray<CanParamDescriptor> table, char letter)
    {
        foreach (CanParamDescriptor descriptor in table)
        {
            if (descriptor.Letter == letter)
            {
                return descriptor;
            }
        }
        throw new CanGenericParamException($"'{letter}' is not a parameter of this message");
    }

    /// <summary>
    /// Write the low <paramref name="size"/> bytes of a value little-endian, via the same
    /// <see cref="BinaryPrimitives"/> helpers <see cref="SetUInt64"/> and <see cref="SetFloat"/> use, so an
    /// endianness fix to one write path can't miss the others.
    /// </summary>
    private static Span<byte> Bytes(Span<byte> destination, uint value, int size)
    {
        switch (size)
        {
            case 1:
                destination[0] = (byte)value;
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)value);
                break;
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
                break;
            default:
                BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
                break;
        }
        return destination;
    }

    private static void RequireArrayLength(char letter, CanParamDescriptor descriptor, int length) =>
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
