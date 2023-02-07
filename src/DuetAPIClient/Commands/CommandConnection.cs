using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Connection.InitMessages;
using DuetAPI.ObjectModel;

namespace DuetAPIClient
{
    /// <summary>
    /// Connection class for sending commands to the control server
    /// </summary>
    /// <seealso cref="ConnectionMode.Command"/>
    public sealed class CommandConnection : BaseCommandConnection
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
            CommandInitMessage initMessage = new();
            return Connect(initMessage, socketPath, cancellationToken);
        }

        /// <summary>
        /// Update the status of a network protocol. Reserved for internal purposes, do not use
        /// </summary>
        /// <param name="protocol">Protocol to update</param>
        /// <param name="enabled">If it is enabled or not</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public Task SetNetworkProtocol(NetworkProtocol protocol, bool enabled, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new SetNetworkProtocol { Protocol = protocol, Enabled = enabled }, cancellationToken);
        }

        /// <summary>
        /// Update the process id of a given plugin. Reserved for internal purposes, do not use
        /// </summary>
        /// <param name="name">Name of the plugin</param>
        /// <param name="pid">New process id</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public Task SetPluginProcess(string name, int pid, CancellationToken cancellationToken = default)
        {
            return PerformCommand(new SetPluginProcess { Plugin = name, Pid = pid }, cancellationToken);
        }
    }
}