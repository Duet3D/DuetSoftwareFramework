namespace DuetAPI.Connection
{
    /// <summary>
    /// Static class that holds the connection defaults
    /// </summary>
    public static class Defaults
    {
        /// <summary>
        /// Current API protocol version number
        /// </summary>
        public const int ProtocolVersion = 4;

        /// <summary>
        /// Default directory in which DSF-related UNIX sockets reside
        /// </summary>
        public const string SocketDirectory = "/var/run/dsf";

        /// <summary>
        /// Default UNIX socket file for DuetControlServer
        /// </summary>
        public const string SocketFile = "dcs.sock";

        /// <summary>
        /// Default fully-qualified path to the UNIX socket for DuetControlServer
        /// </summary>
        public const string FullSocketPath = "/var/run/dsf/dcs.sock";

        /// <summary>
        /// Default code channel to use
        /// </summary>
        public const CodeChannel InputChannel = CodeChannel.SBC;
    }
}
