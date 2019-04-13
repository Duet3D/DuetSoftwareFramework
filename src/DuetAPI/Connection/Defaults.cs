using System;
using System.Collections.Generic;
using System.Text;

namespace DuetAPI.Connection
{
    /// <summary>
    /// Static class that holds the connection defaults
    /// </summary>
    public static class Defaults
    {
        /// <summary>
        /// Default path to the UNIX file socket
        /// </summary>
        public const string SocketPath = "/var/run/duet.sock";

        /// <summary>
        /// Default code channel to use
        /// </summary>
        public const CodeChannel Channel = CodeChannel.SPI;
    }
}
