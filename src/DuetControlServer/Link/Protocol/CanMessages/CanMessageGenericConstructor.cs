using System;
using System.Collections.Immutable;
using DuetAPI.Commands;
using DuetAPI.Utility;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Builds a generic CAN message from a G-code command.
/// </summary>
/// <remarks>
/// This is the counterpart of RepRapFirmware's <c>CanMessageGenericConstructor::PopulateFromCommand</c>:
/// it walks the message's parameter table and, for each entry the command mentions, converts the value to
/// the type the table declares and packs it. Every generic message is defined by which G-code parameters it
/// can carry, so a command is the natural input.
/// <para>
/// Only letters A..Z are considered. CANlib moves a parameter out of that range to hold its table position
/// — keeping the parameters after it on the paramMap bits the receiver expects — while making sure a command
/// can never supply it. Some of those are retired entries; others, such as the driver number in
/// <c>M915</c>'s <c>d</c>, are filled in by the caller instead, which the builders still allow.
/// </para>
/// <para>
/// Where the parameter is known at the call site, prefer the generated builders
/// (<c>M950FanBuilder</c> and friends): they check the letter and its type at compile time. This exists for
/// the case where the parameters are whatever the user typed.
/// </para>
/// </remarks>
public static class CanMessageGenericConstructor
{
    /// <summary>
    /// Build a generic message from the parameters of a G-code command that appear in the given table.
    /// </summary>
    /// <param name="table">Parameter table of the message being built.</param>
    /// <param name="code">Command to take the parameter values from.</param>
    /// <returns>The writer holding the message, its parameter map and its actual data length.</returns>
    /// <exception cref="CanGenericParamException">
    /// A value does not fit the parameter it was given for, or the message would overflow.
    /// </exception>
    public static CanGenericWriter FromCode(ImmutableArray<CanParamDescriptor> table, Code code)
    {
        CanGenericWriter writer = new(table);
        foreach (CanParamDescriptor descriptor in table)
        {
            if (!descriptor.CanComeFromGCode)
            {
                continue;
            }
            if (!code.TryGetParameter(descriptor.Letter, out CodeParameter? parameter) || parameter.IsNull)
            {
                continue;
            }
            Add(writer, descriptor, parameter);
        }
        return writer;
    }

    /// <summary>
    /// Convert one G-code parameter to the type its table entry declares and pack it. The conversions
    /// mirror RepRapFirmware's, including clamping the integer types rather than rejecting a value that is
    /// merely too large for the field.
    /// </summary>
    private static void Add(CanGenericWriter writer, CanParamDescriptor descriptor, CodeParameter parameter)
    {
        char letter = descriptor.Letter;
        switch (descriptor.Type)
        {
            case CanParamType.UInt64:
                writer.AddUInt64(letter, (ulong)(long)parameter);
                break;

            case CanParamType.UInt32:
                writer.AddUInt(letter, (uint)parameter);
                break;

            case CanParamType.UInt16 or CanParamType.PwmFreq:
                writer.AddUInt(letter, Math.Min((uint)parameter, ushort.MaxValue));
                break;

            case CanParamType.UInt8:
                writer.AddUInt(letter, Math.Min((uint)parameter, byte.MaxValue));
                break;

            case CanParamType.Int32:
                writer.AddInt(letter, (int)parameter);
                break;

            case CanParamType.Int16:
                writer.AddInt(letter, Math.Clamp((int)parameter, short.MinValue, short.MaxValue));
                break;

            case CanParamType.Int8:
                writer.AddInt(letter, Math.Clamp((int)parameter, sbyte.MinValue, sbyte.MaxValue));
                break;

            case CanParamType.LocalDriver:
            {
                DriverId driver = (DriverId?)parameter ?? throw new CanGenericParamException($"parameter '{letter}' is not a driver ID");
                writer.AddDriverId(letter, (byte)driver.Port);
                break;
            }

            case CanParamType.Float or CanParamType.Float16:
                writer.AddFloat(letter, (float)parameter);
                break;

            case CanParamType.Char:
            {
                string text = Text(letter, parameter);
                if (text.Length != 1)
                {
                    throw new CanGenericParamException($"parameter '{letter}' expects a single character but got \"{text}\"");
                }
                writer.AddChar(letter, text[0]);
                break;
            }

            case CanParamType.String:
                writer.AddString(letter, Text(letter, parameter));
                break;

            case CanParamType.ReducedString:
                // Expansion boards address their own ports, so the board number has to come off first
                writer.AddString(letter, RemoveBoardAddress(Text(letter, parameter)));
                break;

            case CanParamType.UInt8Array or CanParamType.UInt16Array or CanParamType.UInt32Array:
                writer.AddUIntArray(letter, UIntArray(letter, parameter, descriptor));
                break;

            case CanParamType.FloatArray:
                writer.AddFloatArray(letter, FloatArray(letter, parameter, descriptor));
                break;

            default:
                throw new CanGenericParamException($"parameter '{letter}' has unsupported type {descriptor.Type}");
        }
    }

    private static string Text(char letter, CodeParameter parameter) =>
        (string?)parameter ?? throw new CanGenericParamException($"parameter '{letter}' expects a string");

    /// <summary>
    /// Strip a leading board address from a port name, so that "1.out2" becomes "out2". The expansion
    /// board knows only its own ports, and RepRapFirmware does the same before sending.
    /// </summary>
    private static string RemoveBoardAddress(string portName)
    {
        // A leading "<digits>." is a board address; anything else, including a '!' or '^' modifier, is not
        int start = portName.Length > 0 && portName[0] is '!' or '^' ? 1 : 0;
        int dot = portName.IndexOf('.', start);
        if (dot <= start)
        {
            return portName;
        }
        for (int i = start; i < dot; i++)
        {
            if (!char.IsAsciiDigit(portName[i]))
            {
                return portName;
            }
        }
        return string.Concat(portName.AsSpan(0, start), portName.AsSpan(dot + 1));
    }

    private static uint[] UIntArray(char letter, CodeParameter parameter, CanParamDescriptor descriptor)
    {
        uint[] values = (uint[]?)parameter ?? throw new CanGenericParamException($"parameter '{letter}' expects an unsigned array");
        return Truncate(values, descriptor.MaxArrayLength);
    }

    private static float[] FloatArray(char letter, CodeParameter parameter, CanParamDescriptor descriptor)
    {
        float[] values = (float[]?)parameter ?? throw new CanGenericParamException($"parameter '{letter}' expects a float array");
        return Truncate(values, descriptor.MaxArrayLength);
    }

    /// <summary>
    /// Keep at most as many elements as the table allows, matching RepRapFirmware, which asks the command
    /// for no more than the table's maximum rather than failing on a longer list.
    /// </summary>
    private static T[] Truncate<T>(T[] values, int maxLength) => values.Length <= maxLength ? values : values[..maxLength];
}
