namespace DuetAPI.Connection
{
    /// <summary>
    /// An instance of this class is sent from the client to the server as a response to the <see cref="ServerInitMessage"/>.
    /// It allows a client to select the connection mode (<see cref="ConnectionMode"/>).
    /// </summary>
    public class ClientInitMessage
    {
        /// <summary>
        /// Desired mode of the new connection
        /// </summary>
        public ConnectionMode Mode { get; set; } = ConnectionMode.Unknown;
    }
}