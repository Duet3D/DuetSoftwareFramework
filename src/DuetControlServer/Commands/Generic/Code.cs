using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer.FileExecution;
using DuetControlServer.IPC;
using DuetControlServer.IPC.Processors;
using DuetControlServer.Model;
using DuetControlServer.SPI;
using Nito.AsyncEx;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.Code"/> command
    /// </summary>
    public sealed class Code : DuetAPI.Commands.Code, IConnectionCommand
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// List of cancellation tokens to cancel pending codes while they are waiting for their execution
        /// </summary>
        /// <remarks>
        /// While it may appear nicer to move the cancellation functionality to the code pipeline itself,
        /// this coule lead to performance issues or unexpected behaviour due to intercepted codes. So leave it here for now
        /// </remarks>
        private static readonly CancellationTokenSource[] _cancellationTokenSources = new CancellationTokenSource[Inputs.Total];

        /// <summary>
        /// Static constructor of this class
        /// </summary>
        static Code()
        {
            for (int i = 0; i < Inputs.Total; i++)
            {
                _cancellationTokenSources[i] = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);

                FileLocks[i] = new AsyncLock();
            }
        }

        /// <summary>
        /// Cancel pending codes of the given channel
        /// </summary>
        /// <param name="channel">Channel to cancel codes from</param>
        public static void CancelPending(CodeChannel channel)
        {
            lock (_cancellationTokenSources)
            {
                // Cancel and dispose the existing CTS
                CancellationTokenSource oldTcs = _cancellationTokenSources[(int)channel];
                oldTcs.Cancel();
                oldTcs.Dispose();

                // Create a new one
                _cancellationTokenSources[(int)channel] = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);
            }
        }

        /// <summary>
        /// Cancellation token that may be used to cancel this code
        /// </summary>
        internal CancellationToken CancellationToken { get; set; }

        /// <summary>
        /// Used to reset the cancellation token of this code
        /// </summary>
        internal void ResetCancellationToken()
        {
            lock (_cancellationTokenSources)
            {
                CancellationToken = _cancellationTokenSources[(int)Channel].Token;
            }
        }

        /// <summary>
        /// Lock around the files being written
        /// </summary>
        public static readonly AsyncLock[] FileLocks = new AsyncLock[Inputs.Total];

        /// <summary>
        /// Current stream writer of the files being written to (M28/M29)
        /// </summary>
        public static readonly StreamWriter[] FilesBeingWritten = new StreamWriter[Inputs.Total];

        /// <summary>
        /// Constructor of a new code
        /// </summary>
        public Code() : base() { }

        /// <summary>
        /// Constructor of a new code which also parses the given text-based G/M/T-code
        /// </summary>
        public Code(string code) : base(code) { }

        /// <summary>
        /// Source connection of this command
        /// </summary>
        public Connection Connection
        {
            get => _connection;
            set
            {
                SourceConnection = value?.Id ?? 0;
                _connection = value;
            }
        }
        private Connection _connection;

        /// <summary>
        /// Check if Marlin is being emulated
        /// </summary>
        /// <returns>True if Marlin is being emulated</returns>
        public async Task<bool> EmulatingMarlin()
        {
            using (await Provider.AccessReadOnlyAsync(CancellationToken))
            {
                Compatibility compatibility = Provider.Get.Inputs[Channel].Compatibility;
                return compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP;
            }
        }

        /// <summary>
        /// Run an arbitrary G/M/T-code and wait for it to finish
        /// </summary>
        /// <returns>Result of the code</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        public override async Task<Message> Execute()
        {
            // Assign a cancellation token when the execution starts
            if (CancellationToken == default)
            {
                CancellationToken = _cancellationTokenSources[(int)Channel].Token;
            }

            // Send it to the code pipeline
            await Codes.Processor.StartCodeAsync(this);

            // Wait for the result unless it has the asynchronous flag
            if (!Flags.HasFlag(CodeFlags.Asynchronous))
            {
                await Task;
                return Result;
            }
            return null;
        }

        /// <summary>
        /// Current stage of this code on the code pipeline
        /// </summary>
        internal Codes.PipelineStage Stage { get; set; }

        /// <summary>
        /// File that started this code
        /// </summary>
        internal Files.CodeFile File { get; set; }

        /// <summary>
        /// Macro that started this code or null
        /// </summary>
        internal Macro Macro { get; set; }

        /// <summary>
        /// Attempt to process this code internally
        /// </summary>
        /// <returns>Whether the code could be processed internally</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        internal async Task<bool> ProcessInternally()
        {
            if (Keyword != KeywordType.None &&
                Keyword != KeywordType.Echo &&
                Keyword != KeywordType.Abort &&
                Keyword != KeywordType.Global &&
                Keyword != KeywordType.Var &&
                Keyword != KeywordType.Set)
            {
                // Other meta keywords will be handled later
                throw new InvalidOperationException("Conditional codes must not be executed");
            }

            // Check if this code is supposed to be written to a file
            int numChannel = (int)Channel;
            using (await FileLocks[numChannel].LockAsync(Program.CancellationToken))
            {
                if (FilesBeingWritten[numChannel] != null && (Type != CodeType.MCode || MajorNumber != 29))
                {
                    _logger.Debug("Writing {0}", this);
                    FilesBeingWritten[numChannel].WriteLine(this);
                    Result = new();
                    return true;
                }
            }

            // Try to process this code internally
            _logger.Debug("Processing {0}", this);

            // Flush the code channel and populate SBC fields where applicable
            if (Keyword == KeywordType.None && Expressions.ContainsSbcFields(this) && !await Interface.FlushAsync(this, true, false))
            {
                throw new OperationCanceledException();
            }

            // Attempt to process the code internally
            switch (Type)
            {
                case CodeType.GCode:
                    Result = await Codes.Handlers.GCodes.Process(this);
                    break;
                case CodeType.MCode:
                    Result = await Codes.Handlers.MCodes.Process(this);
                    break;
                case CodeType.TCode:
                    Result = await Codes.Handlers.TCodes.Process(this);
                    break;
                case CodeType.Keyword:
                    Result = await Codes.Handlers.Keywords.Process(this);
                    break;
            }

            if (Result != null && Keyword == KeywordType.None)
            {
                return true;
            }

            // If the code could not be interpreted internally, post-process it
            if (!Flags.HasFlag(CodeFlags.IsPostProcessed))
            {
                bool resolved = await CodeInterception.Intercept(this, InterceptionMode.Post);

                Flags |= CodeFlags.IsPostProcessed;
                if (resolved)
                {
                    return true;
                }
            }

            // Do not send comments that may not be interpreted by RRF
            if ((Type == CodeType.None) ||
                (Type == CodeType.Comment && (string.IsNullOrWhiteSpace(Comment) || !Settings.FirmwareComments.Any(chunk => Comment.Contains(chunk)))))
            {
                Result = new Message();
                return true;
            }

            // Code has not been interpreted yet - let RRF deal with it
            return false;
        }

        /// <summary>
        /// Size of this code in binary representation
        /// </summary>
        internal int BinarySize { get; set; }

        /// <summary>
        /// Task to complete when this code is complete
        /// </summary>
        internal Task Task => _tcs.Task;

        /// <summary>
        /// Set this code as complete
        /// </summary>
        public void SetResult() => _tcs.TrySetResult();

        /// <summary>
        /// Set this code as cancelled
        /// </summary>
        public void SetCancelled() => _tcs.TrySetCanceled();

        /// <summary>
        /// Set an exception for this code
        /// </summary>
        /// <param name="e">Exception to set</param>
        public void SetException(Exception e) => _tcs.TrySetException(e);

        /// <summary>
        /// Internal TCS representing the lifecycle of a code
        /// </summary>
        private TaskCompletionSource _tcs = new();

        /// <summary>
        /// Resets more <see cref="Code"/> fields
        /// </summary>
        public override void Reset()
        {
            base.Reset();
            Connection = null;
            Stage = Codes.PipelineStage.Start;
            File = null;
            Macro = null;
            _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            BinarySize = 0;
        }
    }
}