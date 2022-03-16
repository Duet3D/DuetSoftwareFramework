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
        public const int ProtocolVersion = 12;

        /// <summary>
        /// Default directory in which DSF-related UNIX sockets reside
        /// </summary>
        public const string SocketDirectory = "/run/dsf";

        /// <summary>
        /// Default UNIX socket file for DuetControlServer
        /// </summary>
        public const string SocketFile = "dcs.sock";

        /// <summary>
        /// Default fully-qualified path to the UNIX socket for DuetControlServer
        /// </summary>
        public const string FullSocketPath = "/run/dsf/dcs.sock";

        /// <summary>
        /// Default file to contain the last start error of DCS. Once DCS starts successfully, it is deleted
        /// </summary>
        public const string StartErrorFile = "/run/dsf/dcs.err";

        /// <summary>
        /// Default code channel to use
        /// </summary>
        public const CodeChannel InputChannel = CodeChannel.SBC;

        /// <summary>
        /// Default number of codes to buffer in <see cref="ConnectionMode.CodeStream"/> mode 
        /// </summary>
        public const int CodeBufferSize = 32;

        /// <summary>
        /// Maximum number of codes to buffer in <see cref="ConnectionMode.CodeStream"/> mode
        /// </summary>
        public const int MaxCodeBufferSize = 256;

        /// <summary>
        /// Default password
        /// </summary>
        public const string Password = "reprap";
    }
}
