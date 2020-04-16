using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Machine;
using DuetAPI.Utility;

namespace DuetAPIClient
{
    /// <summary>
    /// Base connection class for sending commands to the control server
    /// </summary>
    /// <seealso cref="ConnectionMode.Command"/>
    public abstract class BaseCommandConnection : BaseConnection
    {
        /// <summary>
        /// Protected constructor for derived modes that can issue regular commands
        /// </summary>
        /// <param name="mode">Connection type</param>
        protected BaseCommandConnection(ConnectionMode mode) : base(mode) { }

        /// <summary>
        /// Add a new third-party HTTP endpoint in the format /machine/{ns}/{path}
        /// </summary>
        /// <param name="endpointType">HTTP request type</param>
        /// <param name="ns">Namespace of the plugin</param>
        /// <param name="path">Endpoint path</param>
        /// <param name="backlog">Number of simultaneously pending connections</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Wrapper around the UNIX socket for accepting HTTP endpoint requests</returns>
        /// <exception cref="ArgumentException">Endpoint namespace is reserved</exception>
        /// <exception cref="InvalidOperationException">Endpoint is already in use</exception>
        /// <exception cref="IOException">UNIX socket could not be opened</exception>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public async Task<HttpEndpointUnixSocket> AddHttpEndpoint(HttpEndpointType endpointType, string ns, string path, int backlog = HttpEndpointUnixSocket.DefaultBacklog, CancellationToken cancellationToken = default)
        {
            string socketPath = await PerformCommand<string>(new AddHttpEndpoint { EndpointType = endpointType, Namespace = ns, Path = path }, cancellationToken);
            return new HttpEndpointUnixSocket(endpointType, ns, path, socketPath, backlog);
        }

        /// <summary>
        /// Add a new user session
        /// </summary>
        /// <param name="access">Access level of this session</param>
        /// <param name="type">Type of this session</param>
        /// <param name="origin">Origin of the user session (e.g. IP address)</param>
        /// <param name="originPort">Origin of the user session (e.g. WebSocket port). Defaults to the current PID</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>New session ID</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public Task<int> AddUserSession(AccessLevel access, SessionType type, string origin, int? originPort = null, CancellationToken cancellationToken = default)
        {
            if (originPort == null)
            {
                originPort = Process.GetCurrentProcess().Id;
            }
            return PerformCommand<int>(new AddUserSession { AccessLevel = access, SessionType = type, Origin = origin, OriginPort = originPort.Value }, cancellationToken);
        }

        /// <summary>
        /// Wait for all pending codes of the given channel to finish
        /// </summary>
        /// <param name="channel">Code channel to wait for</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if all pending codes could be flushed</returns>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public Task<bool> Flush(CodeChannel channel = CodeChannel.SBC, CancellationToken cancellationToken = default)
        {
            return PerformCommand<bool>(new Flush { Channel = channel }, cancellationToken);
        }

        /// <summary>
        /// Parse a G-code file and returns file information about it
        /// </summary>
        /// <param name="fileName">The file to parse</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Information about the parsed file</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <seealso cref="GetFileInfo"/>
        public Task<ParsedFileInfo> GetFileInfo(string fileName, CancellationToken cancellationToken = default)
        {
            return PerformCommand<ParsedFileInfo>(new GetFileInfo { FileName = fileName }, cancellationToken);
        }

        /// <summary>
        /// Retrieve the full object model of the machine.
        /// In subscription mode this is the first command that has to be called once a connection has been established
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The current machine model</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public Task<MachineModel> GetMachineModel(CancellationToken cancellationToken = default)
        {
            return PerformCommand<MachineModel>(new GetMachineModel(), cancellationToken);
        }

        /// <summary>
        /// Optimized method to directly query the machine model UTF-8 JSON
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Machine model JSON</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public async Task<MemoryStream> GetSerializedMachineModel(CancellationToken cancellationToken = default)
        {
            await Send(new GetMachineModel(), cancellationToken);
            return await JsonHelper.ReceiveUtf8Json(_unixSocket, cancellationToken);
        }

        /// <summary>
        /// Lock the machine model for read/write access.
        /// It is MANDATORY to call <see cref="UnlockMachineModel"/> when write access has finished
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public Task LockMachineModel(CancellationToken cancellationToken = default)
        {
            return PerformCommand(new LockMachineModel(), cancellationToken);
        }

        /// <summary>
        /// Execute an arbitrary pre-parsed code
        /// </summary>
        /// <param name="code">The code to execute</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Result of the given code</returns>
        /// <exception cref="OperationCanceledException">Code or operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <remarks>Cancelling the read operation does not cancel the code execution</remarks>
        /// <seealso cref="Code"/>
        public Task<CodeResult> PerformCode(Code code, CancellationToken cancellationToken = default)
        {
            return PerformCommand<CodeResult>(code, cancellationToken);
        }

        /// <summary>
        /// Execute an arbitrary G/M/T-code in text form and return the result as a string
        /// </summary>
        /// <param name="code">The code to execute</param>
        /// <param name="channel">Optional destination channel of this code</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Result of the given code converted to a string</returns>
        /// <exception cref="OperationCanceledException">Code or operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        /// <remarks>Cancelling the read operation does not cancel the code execution</remarks>
        /// <seealso cref="SimpleCode"/>
        public Task<string> PerformSimpleCode(string code, CodeChannel channel = Defaults.InputChannel, CancellationToken cancellationToken = default)
        {
            return PerformCommand<string>(new SimpleCode { Code = code, Channel = channel }, cancellationToken);
        }

        /// <summary>
        /// Remove an existing HTTP endpoint
        /// </summary>
        /// <param name="endpointType">Type of the endpoint to remove</param>
        /// <param name="ns">Namespace of the endpoint to remove</param>
        /// <param name="path">Endpoint to remove</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if the endpoint could be removed</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public Task<bool> RemoveHttpEndpoint(HttpEndpointType endpointType, string ns, string path, CancellationToken cancellationToken = default)
        {
            return PerformCommand<bool>(new RemoveHttpEndpoint { EndpointType = endpointType, Namespace = ns, Path = path }, cancellationToken);
        }

        /// <summary>
        /// Remove an existing user session
        /// </summary>
        /// <param name="id">Identifier of the session</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if the session could be removed</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public Task<bool> RemoveUserSession(int id, CancellationToken cancellationToken = default)
        {
            return PerformCommand<bool>(new RemoveUserSession { Id = id }, cancellationToken);
        }

        /// <summary>
        /// Resolve a RepRapFirmware-style file path to a real file path
        /// </summary>
        /// <param name="path">File path to resolve</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Resolved file path</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public Task<string> ResolvePath(string path, CancellationToken cancellationToken = default)
        {
            return PerformCommand<string>(new ResolvePath { Path = path }, cancellationToken);
        }

        /// <summary>
        /// Set a given property to a certain value. Make sure to lock the object model before calling this
        /// </summary>
        /// <param name="path">Path to the property</param>
        /// <param name="value">New value as string</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>True if the property could be updated</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public Task<bool> SetMachineModel(string path, string value, CancellationToken cancellationToken = default)
        {
            return PerformCommand<bool>(new SetMachineModel { PropertyPath = path, Value = value }, cancellationToken);
        }

        /// <summary>
        /// Wait for the full machine model to be updated from RepRapFirmware
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public Task SyncMachineModel(CancellationToken cancellationToken = default)
        {
            return PerformCommand(new SyncMachineModel(), cancellationToken);
        }

        /// <summary>
        /// Unlock the machine model again
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation has been cancelled</exception>
        /// <exception cref="SocketException">Command could not be processed</exception>
        public Task UnlockMachineModel(CancellationToken cancellationToken)
        {
            return PerformCommand(new UnlockMachineModel(), cancellationToken);
        }
    }
}