namespace DuetAPI.Connection.InitMessages
{
    /// <summary>
    /// An instance of this class is sent by the server to the client in JSON format once a connection has been established.
    /// </summary>
    public sealed class ServerInitMessage : InitMessage
    {
        /// <summary>
        /// Version of the server-side API
        /// </summary>
        public int Version { get; } = Defaults.ProtocolVersion;
        
        /// <summary>
        /// Unique connection ID assigned by the control server to allow clients to track their commands
        /// </summary>
        public int Id { get; set; }
    }
}