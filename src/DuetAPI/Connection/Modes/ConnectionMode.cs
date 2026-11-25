using System.Text.Json.Serialization;

namespace DuetAPI.Connection;

/// <summary>
/// Supported connection types for client connections
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionMode>))]
public enum ConnectionMode
{
    /// <summary>
    /// Unknown connection type. If this is used, the connection is immediately terminated
    /// </summary>
    Unknown,

    /// <summary>
    /// Command mode. This allows clients to send general purpose messages to the control server like
    /// G-codes or requests of the full object model
    /// </summary>
    /// <seealso cref="InitMessages.CommandInitMessage"/>
    Command,

    /// <summary>
    /// Interception mode. This allows clients to intercept G/M/T-codes before or after they are initially processed
    /// </summary>
    /// <seealso cref="InitMessages.InterceptInitMessage"/>
    Intercept,

    /// <summary>
    /// Subscription mode. In this mode object model updates are transmitted to the client after each update
    /// </summary>
    /// <seealso cref="InitMessages.SubscribeInitMessage"/>
    Subscribe,

    /// <summary>
    /// Code stream mode. This mode lets users send and receive code replies asynchronously using a single socket connection.
    /// Codes are sent as text lines and replies are received as text, like on a serial port of the machine.
    /// Files can be uploaded with M559 and M560, whose file content follows the code as raw bytes.
    /// </summary>
    /// <seealso cref="InitMessages.CodeStreamInitMessage"/>
    CodeStream,

    /// <summary>
    /// Plugin service mode. This mode is used internally and should not be used by third-party plugins!
    /// </summary>
    PluginService
}
