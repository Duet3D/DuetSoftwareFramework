using DuetAPI.ObjectModel;
using DuetAPI.Utility;

namespace DuetAPI.Commands;

/// <summary>
/// Write an arbitrary generic message
/// </summary>
/// <remarks>If neither <c>OutputMessage</c> is true, the message is written to the console output</remarks>
[RequiredPermissions(SbcPermissions.CommandExecution | SbcPermissions.ObjectModelReadWrite)]
public partial class WriteMessage : Command
{
    /// <summary>
    /// Type of the message to write
    /// </summary>
    public MessageType Type { get; set; }

    /// <summary>
    /// Content of the message to write
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Output the message on the console and via the object model
    /// </summary>
    public bool OutputMessage { get; set; } = true;

    /// <summary>
    /// Log level of this message
    /// </summary>
    public EventLogLevel? LogLevel { get; set; }
}
