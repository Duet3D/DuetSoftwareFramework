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
        /// Parse a G-code file and returns file information about it
        /// </summary>
        /// <param name="fileName">The file to parse</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Retrieved file information</returns>
        /// <seealso cref="GetFileInfo"/>
        public Task<ParsedFileInfo> GetFileInfo(string fileName, CancellationToken cancellationToken = default(CancellationToken))
        {
            return PerformCommand<ParsedFileInfo>(new GetFileInfo { FileName = fileName }, cancellationToken);
        }

        /// <summary>
        /// Execute an arbitrary pre-parsed code
        /// </summary>
        /// <param name="code">The code to execute</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The result of the given code</returns>
        /// <remarks>Cancelling the operation does not cause the code to be cancelled</remarks>
        /// <seealso cref="Code"/>
        public Task<CodeResult> PerformCode(Code code, CancellationToken cancellationToken = default(CancellationToken))
        {
            return PerformCommand<CodeResult>(code, cancellationToken);
        }

        /// <summary>
        /// Execute an arbitrary G/M/T-code in text form and return the result as a string
        /// </summary>
        /// <param name="code">The code to execute</param>
        /// <param name="channel">Optional destination channel of this code</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The code result as a string</returns>
        /// <remarks>Cancelling the operation does not cause the code to be cancelled</remarks>
        /// <seealso cref="SimpleCode"/>
        public Task<string> PerformSimpleCode(string code, CodeChannel channel = Defaults.Channel, CancellationToken cancellationToken = default(CancellationToken))
        {
            return PerformCommand<string>(new SimpleCode { Code = code, Channel = channel }, cancellationToken);
        }
        
        /// <summary>
        /// Retrieve the full object model of the machine.
        /// In subscription mode this is the first command that has to be called once a connection has been established
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>The current machine model</returns>
        public Task<MachineModel> GetMachineModel(CancellationToken cancellationToken = default(CancellationToken))
        {
            return PerformCommand<MachineModel>(new GetMachineModel(), cancellationToken);
        }
        
        /// <summary>
        /// Optimized method to query the machine model JSON in any mode.
        /// May be used to get machine model patches as well
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