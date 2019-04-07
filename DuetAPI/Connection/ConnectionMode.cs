using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DuetAPI.Connection
{
    /// <summary>
    /// Supported connection types for client connections
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
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
        Subscribe
    }
}