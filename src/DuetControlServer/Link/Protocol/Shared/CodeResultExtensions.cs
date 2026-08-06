using DuetAPI.ObjectModel;

namespace DuetControlServer.Link.Protocol.Shared;

/// <summary>
/// How to read the result code an expansion board answers a request with.
/// </summary>
public static class CodeResultExtensions
{
    /// <summary>
    /// Whether the board carried the request out
    /// </summary>
    /// <param name="result">Result code the board replied with</param>
    /// <returns>True if the request was carried out</returns>
    /// <remarks>
    /// The same test as <c>Succeeded()</c> in CANlib's GCodeResult.h: a warning still means it was done.
    /// </remarks>
    public static bool Succeeded(this CodeResult result) => result is CodeResult.Ok or CodeResult.Warning;

    /// <summary>
    /// How a reply carrying this result code should be reported
    /// </summary>
    /// <param name="result">Result code the board replied with</param>
    /// <returns>Message type to report the reply as</returns>
    /// <remarks>
    /// Everything from <see cref="CodeResult.Error"/> onwards is an error, which is also how
    /// RepRapFirmware reads it. <see cref="CodeResult.NotFinished"/> means the board has not dealt with
    /// the request yet and is not something it can answer with, so it counts as an error too.
    /// </remarks>
    public static MessageType ToMessageType(this CodeResult result) => result switch
    {
        CodeResult.Ok => MessageType.Success,
        CodeResult.Warning or CodeResult.WarningNotSupported => MessageType.Warning,
        _ => MessageType.Error
    };
}
