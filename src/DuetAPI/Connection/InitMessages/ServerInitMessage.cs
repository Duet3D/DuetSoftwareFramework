namespace DuetAPI.Connection.InitMessages
{
    /// <summary>
    /// An instance of this class is sent by the server to the client in JSON format once a connection has been established.
    /// </summary>
    public sealed class ServerInitMessage
    {
        /// <summary>
        /// Version of the server-side API. A client is supposed to check if this API level s greater than or equal
        /// to this value once a connection has been etablished in order to ensure that all of the required commands
        /// are actually supported by the control server.
        /// </summary>
        public int Version { get; } = 1;
        
        /// <summary>
        /// Unique connection ID assigned by the control server to allow clients to track their commands
        /// </summary>
        public int Id { get; set; }
    }
}