using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Machine;
using DuetAPIClient.Exceptions;

namespace DuetAPIClient
{
    /// <summary>
    /// Connection class for sending commands to the control server
    /// </summary>
    /// <seealso cref="ConnectionMode.Command"/>
    public class CommandConnection : BaseConnection
    {
        /// <summary>
        /// Creates a new connection in command mode
        /// </summary>
        public CommandConnection() : base(ConnectionMode.Command) { }
        
        /// <summary>
        /// Protected constructor for derived modes that can issue regular commands
        /// </summary>
        /// <param name="mode">Connection type</param>
        protected CommandConnection(ConnectionMode mode) : base(mode) { }

        /// <summary>
        /// Establishes a connection to the given UNIX socket file
        /// </summary>
        /// <param name="socketPath">Path to the UNIX socket file</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <exception cref="IncompatibleVersionException">API level is incompatible</exception>
        /// <exception cref="IOException">Connection mode is unavailable</exception>
        public Task Connect(string socketPath = "/tmp/duet.sock", CancellationToken cancellationToken = default(CancellationToken))
        {
            CommandInitMessage initMessage = new CommandInitMessage();
            return base.Connect(initMessage, socketPath, cancellationToken);
        }

        /// <summary>
        /// Instructs the control server to flush all pending commands and to finish all pending moves (like M400 in RepRapFirmware)
        /// </summary>
        /// <seealso cref="DuetAPI.Commands.Flush"/>
        public async Task Flush(CancellationToken cancellationToken = default(CancellationToken))
        {
            await PerformCommand(new Flush(), cancellationToken);
        }

        /// <summary>
        /// Parses a G-code file and returns file information about it
        /// </summary>
        /// <param name="fileName">The file to parse</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Retrieved file information</returns>
        /// <seealso cref="GetFileInfo"/>
        public Task<FileInfo> GetFileInfo(string fileName, CancellationToken cancellationToken = default(CancellationToken))
        {
            return PerformCommand<FileInfo>(new GetFileInfo { FileName = fileName }, cancellationToken);
        }

        /// <summary>
        /// Executes an arbitrary pre-parsed code
        /// </summary>
        /// <param name="code">The code to execute</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The result of the given code</returns>
        /// <seealso cref="Code"/>
        public Task<CodeResult> PerformCode(Code code, CancellationToken cancellationToken = default(CancellationToken))
        {
            return PerformCommand<CodeResult>(code, cancellationToken);
        }

        /// <summary>
        /// Executes an arbitrary G/M/T-code in text form and returns the result as a string
        /// </summary>
        /// <param name="code">The code to execute</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The code result as a string</returns>
        /// <seealso cref="SimpleCode"/>
        public Task<string> PerformSimpleCode(string code, CancellationToken cancellationToken = default(CancellationToken))
        {
            return PerformCommand<string>(new SimpleCode { Code = code }, cancellationToken);
        }
        
        /// <summary>
        /// Retrieves the full object model of the machine
        /// In subscription mode this is the first command that has to be called once a connection has been established.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The current machine model</returns>
        public Task<Model> GetMachineModel(CancellationToken cancellationToken = default(CancellationToken))
        {
            return PerformCommand<Model>(new GetMachineModel(), cancellationToken);
        }
        
        /// <summary>
        /// Optimized method to query the machine model JSON in any mode.
        /// May be used to get machine model patches as well.
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Machine model JSON</returns>
        public async Task<string> GetSerializedMachineModel(CancellationToken cancellationToken = default(CancellationToken))
        {
            await Send(new GetMachineModel());
            return await ReceiveSerializedJsonResponse(nameof(GetMachineModel), cancellationToken);
        }

        /// <summary>
        /// Resolve a RepRapFirmware-style file path to a real file path
        /// </summary>
        /// <param name="path">File path to resolve</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Resolved file path</returns>
        public Task<string> ResolvePath(string path, CancellationToken cancellationToken = default(CancellationToken))
        {
            return PerformCommand<string>(new ResolvePath { Path = path }, cancellationToken);
        }
    }
}