using System.Text.Json.Serialization;

namespace DuetAPI.Connection.InitMessages
{
    /// <summary>
    /// An instance of this class is sent from the client to the server as a response to the <see cref="ServerInitMessage"/>.
    /// It allows a client to select the connection mode (<see cref="ConnectionMode"/>).
    /// </summary>
    [JsonDerivedType(typeof(CodeStreamInitMessage))]
    [JsonDerivedType(typeof(CommandInitMessage))]
    [JsonDerivedType(typeof(InterceptInitMessage))]
    [JsonDerivedType(typeof(PluginServiceInitMessage))]
    [JsonDerivedType(typeof(SubscribeInitMessage))]
    public class ClientInitMessage : InitMessage
    {
        /// <summary>
        /// Desired mode of the new connection
        /// </summary>
        public ConnectionMode Mode { get; set; } = ConnectionMode.Unknown;

        /// <summary>
        /// Version number of the client-side API
        /// </summary>
        /// <seealso cref="Defaults.ProtocolVersion"/>
        /// <remarks>
        /// If this version is incompatible to DCS, a <see cref="IncompatibleVersionException"/> is returned when a connection is being established
        /// </remarks>
        public int Version { get; set; }
    }
}