using System;
using System.Collections.Immutable;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Where one parameter sits in the data area of a generic message.
/// </summary>
/// <param name="Index">Position in the parameter table, which is also the parameter's bit in the parameter map.</param>
/// <param name="Position">Byte offset of the value in the data area, or where it would go if it is absent.</param>
/// <param name="Descriptor">Table entry for the parameter.</param>
/// <param name="IsPresent">True if the message currently carries the parameter.</param>
public readonly record struct CanGenericSlot(int Index, int Position, CanParamDescriptor Descriptor, bool IsPresent);

/// <summary>
/// The layout rules of a generic message's data area.
/// </summary>
/// <remarks>
/// A generic message carries the values of whichever parameters it is sending, packed in table order with no
/// padding, and a bit per table position saying which those are. So a value's offset is the sum of the sizes
/// of the parameters that precede it in the table and are present — which means it can only be found by
/// walking, and both ends have to walk it the same way. Everything that needs to do so goes through here:
/// the writer to find an insertion point, the parser to find a value, and each generic message type to work
/// out how many bytes it actually occupies.
/// </remarks>
public static class CanGenericLayout
{
    /// <summary>
    /// Number of data bytes the parameter at the given position occupies.
    /// </summary>
    /// <param name="data">Data area of the message.</param>
    /// <param name="position">Byte offset of the parameter's value.</param>
    /// <param name="descriptor">Table entry for the parameter.</param>
    public static int SizeAt(ReadOnlySpan<byte> data, int position, CanParamDescriptor descriptor)
    {
        if (descriptor.IsArray)
        {
            // One length byte, then that many elements
            return 1 + (data[position] * descriptor.ItemSize);
        }
        if (descriptor.ItemSize != 0)
        {
            return descriptor.ItemSize;
        }

        // The only zero-size entries are the strings, which run to and include their null terminator
        int end = position;
        while (data[end] != 0)
        {
            end++;
        }
        return end - position + 1;
    }

    /// <summary>
    /// Find where a parameter's value sits, or would sit.
    /// </summary>
    /// <returns>False if the letter is not in the table at all, in which case the message can never carry it.</returns>
    public static bool TryLocate(ReadOnlySpan<byte> data, uint paramMap, ImmutableArray<CanParamDescriptor> table, char letter, out CanGenericSlot slot)
    {
        int position = 0;
        ReadOnlySpan<CanParamDescriptor> entries = table.AsSpan();
        for (int index = 0; index < entries.Length; index++)
        {
            CanParamDescriptor descriptor = entries[index];
            bool present = (paramMap & (1u << index)) != 0;
            if (descriptor.Letter == letter)
            {
                slot = new CanGenericSlot(index, position, descriptor, present);
                return true;
            }
            if (present)
            {
                position += SizeAt(data, position, descriptor);
            }
        }
        slot = default;
        return false;
    }

    /// <summary>
    /// Total number of data bytes the present parameters occupy, i.e. how much of the data area is
    /// meaningful. This is what a generic message reports as its actual data length, so that only the bytes
    /// it is really using go on the bus.
    /// </summary>
    public static int DataLength(ReadOnlySpan<byte> data, uint paramMap, ImmutableArray<CanParamDescriptor> table)
    {
        int position = 0;
        ReadOnlySpan<CanParamDescriptor> entries = table.AsSpan();
        for (int index = 0; index < entries.Length; index++)
        {
            if ((paramMap & (1u << index)) != 0)
            {
                position += SizeAt(data, position, entries[index]);
            }
        }
        return position;
    }
}
