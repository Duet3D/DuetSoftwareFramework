using System;
using DuetAPI.Commands;

namespace DuetAPI;

/// <summary>
/// Exception to be called when a G/M/T code fails
/// </summary>
/// <param name="reason">Why the code failed</param>
/// <param name="column">Column of the value it is about, if it is about one</param>
public class GCodeException(string reason, int column = CodeParameter.NoColumn) : Exception(reason)
{
    /// <summary>
    /// Reason the code through the exception
    /// </summary>
    public string Reason { get; } = reason;

    /// <summary>
    /// Column the reason is about, or <see cref="CodeParameter.NoColumn"/> for a reason about no one
    /// place in the code
    /// </summary>
    /// <remarks>
    /// RepRapFirmware quotes the column for exactly these - a value out of range, a value that was
    /// not a number - and for nothing else, because a rule about the whole command has no one
    /// character to point at (GCodeException::GetMessage)
    /// </remarks>
    public int Column { get; } = column;
}
