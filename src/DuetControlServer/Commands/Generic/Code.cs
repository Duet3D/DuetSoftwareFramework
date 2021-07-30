using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.FileExecution;
using DuetControlServer.IPC;
using DuetControlServer.IPC.Processors;
using DuetControlServer.Model;
using DuetControlServer.SPI;
using Nito.AsyncEx;
using Job = DuetControlServer.FileExecution.Job;

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

        #region Code Scheduling
        /// <summary>
        /// Internal type of a code. This reflects the priority as well
        /// </summary>
        private enum InternalCodeType : int
        {
            /// <summary>
            /// Regular G/M/T-code
            /// </summary>
            Regular = 0,

            /// <summary>
            /// Regular G/M/T-code for a message prompt awaiting acknowledgement
            /// </summary>
            Acknowledgement = 1,

            /// <summary>
            /// Code from a macro file
            /// </summary>
            Macro = 2,

            /// <summary>
            /// Code with <see cref="CodeFlags.IsPrioritized"/> set
            /// </summary>
            Prioritized = 3
        }

        /// <summary>
        /// Array of AsyncLocks to guarantee the ordered start of incoming G/M/T-codes
        /// </summary>
        /// <remarks>
        /// AsyncLock implements an internal waiter queue, so it is safe to rely on it for
        /// maintaining the right order of codes being executed per code channel
        /// </remarks>
        private static readonly AsyncLock[,] _codeStartLocks = new AsyncLock[Inputs.Total, Enum.GetValues(typeof(InternalCodeType)).Length];

        /// <summary>
        /// Array of AsyncLocks to guarantee the ordered finishing of G/M/T-codes
        /// </summary>
        private static readonly AsyncLock[,] _codeFinishLocks = new AsyncLock[Inputs.Total, Enum.GetValues(typeof(InternalCodeType)).Length];

        /// <summary>
        /// List of cancellation tokens to cancel pending codes while they are waiting for their execution
        /// </summary>
        private static readonly CancellationTokenSource[] _cancellationTokenSources = new CancellationTokenSource[Inputs.Total];

        /// <summary>
        /// Static constructor of this class
        /// </summary>
        static Code()
        {
            for (int i = 0; i < Inputs.Total; i++)
            {
                foreach (InternalCodeType codeType in Enum.GetValues(typeof(InternalCodeType)))
                {
                    _codeStartLocks[i, (int)codeType] = new AsyncLock();
                    _codeFinishLocks[i, (int)codeType] = new AsyncLock();
                }
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
                CancellationTokenSource oldCTS = _cancellationTokenSources[(int)channel];
                oldCTS.Cancel();
                oldCTS.Dispose();

                // Create a new one
                _cancellationTokenSources[(int)channel] = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);
            }
        }

        /// <summary>
        /// Internal type assigned by the code scheduler
        /// </summary>
        private InternalCodeType _codeType;

        /// <summary>
        /// Lock that is maintained as long as this code prevents the next code from being started
        /// </summary>
        private IDisposable _codeStartLock;

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
        /// Create a task that waits until this code can be executed.
        /// It may be cancelled if this code is supposed to be cancelled before it is started
        /// </summary>
        /// <returns>Lock to maintain while the code is being executed internally</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        private Task<IDisposable> WaitForExecution()
        {
            // Assign a cancellation token if required
            if (CancellationToken == default)
            {
                lock (_cancellationTokenSources)
                {
                    CancellationToken = _cancellationTokenSources[(int)Channel].Token;
                }
            }

            // Codes from interceptors do not have any order control to avoid deadlocks
            Code codeBeingIntercepted = CodeInterception.GetCodeBeingIntercepted(Connection);
            if (codeBeingIntercepted != null)
            {
                if (codeBeingIntercepted.Flags.HasFlag(CodeFlags.IsFromMacro))
                {
                    Flags |= CodeFlags.IsFromMacro;
                    File = codeBeingIntercepted.File;
                    Macro = codeBeingIntercepted.Macro;
                }
                return Task.FromResult<IDisposable>(null);
            }

            // Wait for pending high priority codes
            if (Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                _codeType = InternalCodeType.Prioritized;
                _logger.Debug("Waiting for execution of {0} (prioritized)", this);
                return _codeStartLocks[(int)Channel, (int)InternalCodeType.Prioritized].LockAsync(CancellationToken);
            }

            // Wait for pending codes from the current macro
            if (Flags.HasFlag(CodeFlags.IsFromMacro))
            {
                _codeType = InternalCodeType.Macro;
                _logger.Debug("Waiting for execution of {0} (macro code)", this);
                return (Macro == null) ? _codeStartLocks[(int)Channel, (int)InternalCodeType.Macro].LockAsync(CancellationToken) : Macro.WaitForCodeStart();
            }

            // Wait for pending codes for message acknowledgements
            // FIXME M0/M1 are not meant to be used while a message box is open
            if (!Flags.HasFlag(CodeFlags.IsFromFirmware) && Interface.IsWaitingForAcknowledgement(Channel) &&
                (Type != CodeType.MCode || (MajorNumber != 0 && MajorNumber != 1)))
            {
                _codeType = InternalCodeType.Acknowledgement;
                _logger.Debug("Waiting for execution of {0} (acknowledgement)", this);
                return _codeStartLocks[(int)Channel, (int)InternalCodeType.Acknowledgement].LockAsync(CancellationToken);
            }

            // Wait for pending regular codes
            _codeType = InternalCodeType.Regular;
            _logger.Debug("Waiting for execution of {0}", this);
            return _codeStartLocks[(int)Channel, (int)InternalCodeType.Regular].LockAsync(CancellationToken);
        }

        /// <summary>
        /// Start the next available G/M/T-code unless this code has already started one
        /// </summary>
        private void StartNextCode()
        {
            if (_codeStartLock != null)
            {
                _codeStartLock.Dispose();
                _codeStartLock = null;
            }
        }

        /// <summary>
        /// Start the next available G/M/T-code and wait until this code may finish
        /// </summary>
        /// <returns>Awaitable disposable</returns>
        private AwaitableDisposable<IDisposable> WaitForFinish()
        {
            if (!Flags.HasFlag(CodeFlags.Unbuffered))
            {
                StartNextCode();
            }

            if (CodeInterception.IsInterceptingConnection(Connection))
            {
                return new AwaitableDisposable<IDisposable>(Task.FromResult<IDisposable>(null));
            }

            AwaitableDisposable<IDisposable> finishTask = (Macro == null) ? _codeFinishLocks[(int)Channel, (int)_codeType].LockAsync(CancellationToken) : Macro.WaitForCodeFinish();
            return finishTask;
        }
        #endregion

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
                SourceConnection = (value != null) ? value.Id : 0;
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
        /// Indicates if this code is supposed to deal with a message box awaiting acknowledgement
        /// </summary>
        internal bool IsForAcknowledgement { get => _codeType == InternalCodeType.Acknowledgement; }

        /// <summary>
        /// Run an arbitrary G/M/T-code and wait for it to finish
        /// </summary>
        /// <returns>Result of the code</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        public override Task<Message> Execute()
        {
            // Wait until this code can be executed and then start it
            Task<Message> executingTask = WaitForExecution()
                .ContinueWith(async task =>
                {
                    try
                    {
                        _codeStartLock = await task;
                        return await ExecuteInternally();
                    }
                    finally
                    {
                        IsExecuted = true;
                    }
                }, TaskContinuationOptions.RunContinuationsAsynchronously)
                .Unwrap();

            // Return either the task itself or null and let it finish in the background
            return Flags.HasFlag(CodeFlags.Asynchronous) ? Task.FromResult<Message>(null) : executingTask;
        }

        /// <summary>
        /// Indicates whether the code has been internally processed
        /// </summary>
        internal bool InternallyProcessed { get; set; }

        /// <summary>
        /// File that started this code
        /// </summary>
        internal Files.CodeFile File { get; set; }

        /// <summary>
        /// Macro that started this code or null
        /// </summary>
        internal Macro Macro { get; set; }

        /// <summary>
        /// Execute the given code internally
        /// </summary>
        /// <returns>Result of the code</returns>
        private async Task<Message> ExecuteInternally()
        {
            string logSuffix = Flags.HasFlag(CodeFlags.Asynchronous) ? " asynchronously" : string.Empty;

            try
            {
                try
                {
                    CancellationToken.ThrowIfCancellationRequested();

                    // Check if this code is supposed to be written to a file
                    int numChannel = (int)Channel;
                    using (await FileLocks[numChannel].LockAsync(Program.CancellationToken))
                    {
                        if (FilesBeingWritten[numChannel] != null && (Type != CodeType.MCode || MajorNumber != 29))
                        {
                            _logger.Debug("Writing {0}{1}", this, logSuffix);
                            FilesBeingWritten[numChannel].WriteLine(this);
                            return new Message();
                        }
                    }

                    // Execute this code
                    _logger.Debug("Processing {0}{1}", this, logSuffix);
                    await Process();
                    _logger.Debug("Completed {0}{1}", this, logSuffix);
                }
                catch (OperationCanceledException oce)
                {
                    // Code has been cancelled
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug(oce, "Cancelled {0}{1}", this, logSuffix);
                    }
                    else
                    {
                        _logger.Debug("Cancelled {0}{1}", this, logSuffix);
                    }
                    throw;
                }
                catch (InvalidOperationException ioe)
                {
                    // Some inputs may be disabled in a custom build
                    _logger.Debug(ioe, "{0} cannot be executed{1} because its code channel {2} is disabled", this, logSuffix, Channel);
                    Result = new Message(MessageType.Error, $"Code channel {Channel} is disabled");
                }
                catch (NotSupportedException nse)
                {
                    // Some codes may not be supported yet
                    _logger.Debug(nse, "{0} is not supported{1}", this, logSuffix);
                    Result = new Message(MessageType.Error, "Operation is not supported");
                }
                catch (Exception e)
                {
                    // This code is no longer processed if an exception has occurred
                    _logger.Error(e, "Code {0} has thrown an exception{1}", this, logSuffix);
                    throw;
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                // Cancel the running file and then start the next code if an exception has occurred
                if (Macro != null)
                {
                    using (await Macro.LockAsync())
                    {
                        await Macro.AbortAsync();
                    }
                }
                else if (Channel == CodeChannel.File)
                {
                    using (await Job.LockAsync())
                    {
                        await Job.AbortAsync();
                    }
                }
                StartNextCode();
                throw;
            }
            finally
            {
                // Start the next (unbuffered or unsupported) code if everything went well
                StartNextCode();
            }
            return Result;
        }

        /// <summary>
        /// Process the code
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task Process()
        {
            // Check if the corresponding code channel has been disabled
            using (await Provider.AccessReadOnlyAsync())
            {
                if (!Settings.NoSpi && Provider.Get.Inputs[Channel] == null)
                {
                    throw new InvalidOperationException("Requested code channel has been disabled");
                }
            }

            // Attempt to process the code internally first
            if (!InternallyProcessed && await ProcessInternally())
            {
                _logger.Debug("Waiting for finish of {0}", this);
                using (await WaitForFinish())
                {
                    await CodeExecuted();
                }
                return;
            }

            // Enqueue this code for further processing in RepRapFirmware
            FirmwareTCS = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Interface.ProcessCode(this);

            // Wait for the code to be processed by RepRapFirmware
            try
            {
                _logger.Debug("Waiting for finish of {0}", this);
                using (await WaitForFinish())
                {
                    await FirmwareTCS.Task;
                    await CodeExecuted();
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelling a code clears the result
                using (await WaitForFinish())
                {
                    Result = null;
                    await CodeExecuted();
                }
                throw;
            }
        }

        /// <summary>
        /// Attempt to process this code internally
        /// </summary>
        /// <returns>Whether the code could be processed internally</returns>
        private async Task<bool> ProcessInternally()
        {
            if (Keyword != KeywordType.None &&
                Keyword != KeywordType.Echo &&
                Keyword != KeywordType.Abort &&
#pragma warning disable CS0618 // Type or member is obsolete
                Keyword != KeywordType.Return &&
#pragma warning restore CS0618 // Type or member is obsolete
                Keyword != KeywordType.Global &&
                Keyword != KeywordType.Var &&
                Keyword != KeywordType.Set)
            {
                // Other meta keywords must be handled before we get here...
                throw new InvalidOperationException("Conditional codes must not be executed");
            }

            // Pre-process this code
            if (!Flags.HasFlag(CodeFlags.IsPreProcessed))
            {
                bool resolved = await CodeInterception.Intercept(this, InterceptionMode.Pre);

                Flags |= CodeFlags.IsPreProcessed;
                if (resolved)
                {
                    InternallyProcessed = true;
                    return true;
                }
            }

            // Flush the code channel and populate Linux fields where applicable
            if (Keyword == KeywordType.None && Expressions.ContainsLinuxFields(this) && !await Interface.Flush(this, true, false))
            {
                throw new OperationCanceledException();
            }

            // Attempt to process the code internally
            switch (Type)
            {
                case CodeType.GCode:
                    Result = await GCodes.Process(this);
                    break;

                case CodeType.MCode:
                    Result = await MCodes.Process(this);
                    break;

                case CodeType.TCode:
                    Result = await TCodes.Process(this);
                    break;
            }

            if (Result != null && Keyword == KeywordType.None)
            {
                InternallyProcessed = true;
                return true;
            }

            // If the code could not be interpreted internally, post-process it
            if (!Flags.HasFlag(CodeFlags.IsPostProcessed))
            {
                bool resolved = await CodeInterception.Intercept(this, InterceptionMode.Post);

                Flags |= CodeFlags.IsPostProcessed;
                if (resolved)
                {
                    InternallyProcessed = true;
                    return true;
                }
            }

            // Handle keywords
            if (Keyword != KeywordType.None)
            {
                Result = await Keywords.Process(this);
                InternallyProcessed = true;
                return true;
            }

            // Do not send comments that may not be interpreted by RRF
            if ((Type == CodeType.None) ||
                (Type == CodeType.Comment && (string.IsNullOrWhiteSpace(Comment) || !Settings.FirmwareComments.Any(chunk => Comment.Contains(chunk)))))
            {
                Result = new Message();
                InternallyProcessed = true;
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
        /// TCS used by the SPI subsystem to flag when the code has been cancelled/caused an error/finished
        /// </summary>
        internal TaskCompletionSource FirmwareTCS { get; private set; }

        /// <summary>
        /// Indicates if the code has been fully executed (including the Executed interceptor if applicable)
        /// </summary>
        internal bool IsExecuted
        {
            get => _isExecuted;
            private set => _isExecuted = value;
        }
        private volatile bool _isExecuted;

        /// <summary>
        /// Executed when the code has finished
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task CodeExecuted()
        {
            if (Result != null)
            {
                // Process the code result
                switch (Type)
                {
                    case CodeType.GCode:
                        await GCodes.CodeExecuted(this);
                        break;

                    case CodeType.MCode:
                        await MCodes.CodeExecuted(this);
                        break;

                    case CodeType.TCode:
                        await TCodes.CodeExecuted(this);
                        break;
                }

                if (!Flags.HasFlag(CodeFlags.IsPostProcessed))
                {
                    // RepRapFirmware generally prefixes error messages with the code itself, mimic this behavior
                    if (Result.Type == MessageType.Error)
                    {
                        Result.Content = ToShortString() + ": " + Result.Content;
                    }

                    // Messages from RRF and replies to file print codes are logged somewhere else,
                    // so we only need to log internal code replies that are not part of file prints
                    if (File == null || Channel != CodeChannel.File)
                    {
                        await Utility.Logger.LogAsync(Result);
                    }
                }

                // Deal with firmware emulation
                if (!Flags.HasFlag(CodeFlags.IsFromMacro))
                {
                    if (await EmulatingMarlin())
                    {
                        if (Flags.HasFlag(CodeFlags.IsLastCode))
                        {
                            if (Result == null || string.IsNullOrEmpty(Result.Content))
                            {
                                Result = new Message(MessageType.Success, "ok\n");
                            }
                            else if (Type == CodeType.MCode && MajorNumber == 105)
                            {
                                Result.Content = "ok " + Result.Content;
                            }
                            else
                            {
                                Result.AppendLine("ok\n");
                            }
                        }
                    }
                    else if (Result == null || string.IsNullOrEmpty(Result.Content))
                    {
                        Result = new Message(MessageType.Success, "\n");
                    }
                    else
                    {
                        Result.AppendLine(string.Empty);
                    }
                }

                // Update the last code result
                if (File != null)
                {
                    File.LastResult = (int)Result.Type;
                }
            }

            // Done
            await CodeInterception.Intercept(this, InterceptionMode.Executed);
        }

        /// <summary>
        /// Resets more <see cref="Code"/> fields
        /// </summary>
        public override void Reset()
        {
            base.Reset();
            Connection = null;
            InternallyProcessed = false;
            File = null;
            Macro = null;
            FirmwareTCS = null;
            BinarySize = 0;
            IsExecuted = false;
        }
    }
}