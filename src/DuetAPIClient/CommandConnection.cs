using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;

namespace DuetAPIClient
{
    /// <summary>
    /// Connection class for sending commands to the control server
    /// </summary>
    /// <seealso cref="ConnectionMode.Command"/>
    public class CommandConnection : BaseCommandConnection
    {
        /// <summary>
        /// Create a new connection in command mode
        /// </summary>
        public CommandConnection() : base(ConnectionMode.Command) { }

        /// <summary>
        /// Establish a connection to the given UNIX socket file
        /// </summary>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Init message could not be processed</exception>
        public Task Connect(string socketPath = Defaults.FullSocketPath, CancellationToken cancellationToken = default)
        {
            CommandInitMessage initMessage = new CommandInitMessage();
            return Connect(initMessage, socketPath, cancellationToken);
        }
    }
}