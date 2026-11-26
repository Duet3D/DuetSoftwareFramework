using System;
using System.Globalization;
using System.Linq;
using DuetControlServer.Link.Protocol.Shared;

namespace SystemTests.Host;

/// <summary>
/// The wording of the refusals every code shares, from reading its parameters, from not being
/// supported or from a board not answering, written once for every scenario that expects one
/// </summary>
/// <remarks>
/// These are RepRapFirmware's words (GCodeBuffer, StringParser and GCodes), which DuetAPI's
/// parameter readers reproduce. Scenarios match them with <see cref="Reply.IsError"/>
/// </remarks>
internal static class CodeErrors
{
    /// <summary>A parameter the code needs is not there</summary>
    public static string MissingParameter(char letter) => $"missing parameter '{letter}'";

    /// <summary>A letter written with no value after it</summary>
    public static string ExpectedNumber(char letter) => $"expected number after '{letter}'";

    /// <summary>A value below the lowest the parameter takes</summary>
    public static string ParameterTooLow(char letter) => $"parameter '{letter}' too low";

    /// <summary>A value above the highest the parameter takes</summary>
    public static string ParameterTooHigh(char letter) => $"parameter '{letter}' too high";

    /// <summary>A value of the wrong type for the parameter, such as a string where a number belongs</summary>
    public static string InvalidParameterValue(char letter) => $"invalid value for parameter '{letter}'";

    /// <summary>A value that names none of the values the parameter takes</summary>
    public static string InvalidEnumValue(char letter, params long[] valid) => $"valid parameter values for '{letter}' are {string.Join(", ", valid)}";

    /// <summary>A value that names none of an enumeration's members, which are the values the parameter takes</summary>
    /// <remarks>The list follows the enumeration, so a scenario still writes the values it sends as numbers</remarks>
    public static string InvalidEnumValue<T>(char letter) where T : struct, Enum
        => InvalidEnumValue(letter, [.. Enum.GetValues<T>().Select(member => Convert.ToInt64(member, CultureInfo.InvariantCulture)).Distinct().Order()]);

    /// <summary>More values than the parameter takes</summary>
    public static string ArrayTooLong(char letter) => $"array too long for parameter '{letter}'";

    /// <summary>A list of values that is not as long as the parameter needs</summary>
    public static string WrongArrayLength(int expected) => $"Wrong number of values in array, expected {expected}";

    /// <summary>Zero or less for a value that has to be positive</summary>
    public const string NotGreaterThanZero = "value must be greater than zero";

    /// <summary>A code, or form of a code, that nothing handles</summary>
    public const string NotSupported = "Command is not supported";

    /// <summary>A board that did not answer a request, and the type of the request if the scenario names it</summary>
    /// <remarks>The request id that ends the line is the transmission token, which differs from run to run, so it is left out</remarks>
    public static string CanResponseTimeout(int board, CanMessageType? request = null)
        => $"CAN response timeout: board {board}" + (request is CanMessageType type ? $", req type {(ushort)type}" : string.Empty);
}
