using System;
using System.Collections.Immutable;
using DuetAPI.Commands;
using DuetAPI.Utility;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Takes the parameters of a generic CAN message from a G-code command.
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
/// <c>M915</c>'s <c>d</c>, are filled in by the caller instead, which the message types still allow.
/// </para>
/// </remarks>
public static partial class CanGenericWriter
{
    /// <summary>
    /// Set the parameters of a G-code command that appear in the given table, leaving the ones it does not
    /// mention as they are.
    /// </summary>
    /// <param name="message">Message to populate.</param>
    /// <param name="table">Parameter table of the message being built.</param>
    /// <param name="code">Command to take the parameter values from.</param>
    /// <exception cref="CanGenericParamException">
    /// A value does not fit the parameter it was given for, or the message would overflow.
    /// </exception>
    public static void FromCode(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, Code code)
    {
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
            Add(ref message, table, descriptor, parameter);
        }
    }

    /// <summary>
    /// Convert one G-code parameter to the type its table entry declares and pack it. The conversions
    /// mirror RepRapFirmware's, including clamping the integer types rather than rejecting a value that is
    /// merely too large for the field.
    /// </summary>
    private static void Add(ref CanMessageGeneric message, ImmutableArray<CanParamDescriptor> table, CanParamDescriptor descriptor, CodeParameter parameter)
    {
        char letter = descriptor.Letter;
        switch (descriptor.Type)
        {
            case CanParamType.UInt64:
                SetUInt64(ref message, table, letter, (ulong)(long)parameter);
                break;

            case CanParamType.UInt32:
                SetUInt(ref message, table, letter, ToUInt(parameter));
                break;

            case CanParamType.UInt16 or CanParamType.PwmFreq:
                SetUInt(ref message, table, letter, Math.Min(ToUInt(parameter), ushort.MaxValue));
                break;

            case CanParamType.UInt8:
                SetUInt(ref message, table, letter, Math.Min(ToUInt(parameter), byte.MaxValue));
                break;

            case CanParamType.Int32:
                SetInt(ref message, table, letter, (int)parameter);
                break;

            case CanParamType.Int16:
                SetInt(ref message, table, letter, Math.Clamp((int)parameter, short.MinValue, short.MaxValue));
                break;

            case CanParamType.Int8:
                SetInt(ref message, table, letter, Math.Clamp((int)parameter, sbyte.MinValue, sbyte.MaxValue));
                break;

            case CanParamType.LocalDriver:
            {
                DriverId driver = (DriverId?)parameter ?? throw new CanGenericParamException($"parameter '{letter}' is not a driver ID");
                SetDriverId(ref message, table, letter, (byte)driver.Port);
                break;
            }

            case CanParamType.Float or CanParamType.Float16:
                SetFloat(ref message, table, letter, (float)parameter);
                break;

            case CanParamType.Char:
            {
                string text = Text(letter, parameter);
                if (text.Length != 1)
                {
                    throw new CanGenericParamException($"parameter '{letter}' expects a single character but got \"{text}\"");
                }
                SetChar(ref message, table, letter, text[0]);
                break;
            }

            case CanParamType.String:
                SetString(ref message, table, letter, Text(letter, parameter));
                break;

            case CanParamType.ReducedString:
                // Expansion boards address their own ports, so the board number has to come off first
                SetString(ref message, table, letter, RemoveBoardAddress(Text(letter, parameter)));
                break;

            case CanParamType.UInt8Array or CanParamType.UInt16Array or CanParamType.UInt32Array:
                SetUIntArray(ref message, table, letter, UIntArray(letter, parameter, descriptor));
                break;

            case CanParamType.FloatArray:
                SetFloatArray(ref message, table, letter, FloatArray(letter, parameter, descriptor));
                break;

            default:
                throw new CanGenericParamException($"parameter '{letter}' has unsupported type {descriptor.Type}");
        }
    }

    /// <summary>
    /// Convert a G-code integer parameter to <see cref="uint"/> the way RepRapFirmware's <c>strtoul</c>-based
    /// parser does: a negative literal wraps around rather than being rejected (<c>(uint)parameter</c> would
    /// throw <see cref="OverflowException"/> instead, since a negative value is stored as a signed
    /// <see cref="int"/>). Callers then clamp the result to the field width, matching the firmware's
    /// <c>min&lt;uint32_t&gt;(gb.GetUIValue(), ...)</c>.
    /// </summary>
    private static uint ToUInt(CodeParameter parameter) =>
        parameter.Type == typeof(int) ? unchecked((uint)(int)parameter) : (uint)parameter;

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
