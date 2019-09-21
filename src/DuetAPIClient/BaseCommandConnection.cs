using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Machine;

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
        /// Wait for all pending codes of the given channel to finish
        /// </summary>
        /// <param name="channel">Code channel to wait for</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task<bool> Flush(CodeChannel channel = CodeChannel.SPI, CancellationToken cancellationToken = default)
        {
            return PerformCommand<bool>(new Flush() { Channel = channel }, cancellationToken);
        }

        /// <summary>
        /// Parse a G-code file and returns file information about it
        /// </summary>
        /// <param name="fileName">The file to parse</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Retrieved file information</returns>
        /// <seealso cref="GetFileInfo"/>
        public Task<ParsedFileInfo> GetFileInfo(string fileName, CancellationToken cancellationToken = default)
        {
            return PerformCommand<ParsedFileInfo>(new GetFileInfo { FileName = fileName }, cancellationToken);
        }

        /// <summary>
        /// Execute an arbitrary pre-parsed code
        /// </summary>
        /// <param name="code">The code to execute</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Result of the given code</returns>
        /// <exception cref="TaskCanceledException">Code has been cancelled (buffer cleared)</exception>
        /// <remarks>Cancelling the operation does not cause the code to be cancelled</remarks>
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
        /// <exception cref="TaskCanceledException">Code has been cancelled (buffer cleared)</exception>
        /// <remarks>Cancelling the operation does not cause the code to be cancelled</remarks>
        /// <seealso cref="SimpleCode"/>
        public Task<string> PerformSimpleCode(string code, CodeChannel channel = Defaults.Channel, CancellationToken cancellationToken = default)
        {
            return PerformCommand<string>(new SimpleCode { Code = code, Channel = channel }, cancellationToken);
        }
        
        /// <summary>
        /// Retrieve the full object model of the machine.
        /// In subscription mode this is the first command that has to be called once a connection has been established
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The current machine model</returns>
        public Task<MachineModel> GetMachineModel(CancellationToken cancellationToken = default)
        {
            return PerformCommand<MachineModel>(new GetMachineModel(), cancellationToken);
        }
        
        /// <summary>
        /// Optimized method to query the machine model JSON in any mode.
        /// May be used to get machine model patches as well
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Machine model JSON</returns>
        public async Task<string> GetSerializedMachineModel(CancellationToken cancellationToken = default)
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
        public Task<string> ResolvePath(string path, CancellationToken cancellationToken = default)
        {
            return PerformCommand<string>(new ResolvePath { Path = path }, cancellationToken);
        }

        /// <summary>
        /// Wait for the full machine model to be updated from RepRapFirmware
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public Task SyncMachineModel(CancellationToken cancellationToken = default)
        {
            return PerformCommand(new SyncMachineModel(), cancellationToken);
        }
    }
}