using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
using DuetAPI.Machine;
using DuetControlServer.Codes;
using DuetControlServer.IPC.Processors;
using DuetControlServer.SPI;
using Nito.AsyncEx;

namespace DuetControlServer.Commands
{
    /// <summary>
    /// Implementation of the <see cref="DuetAPI.Commands.Code"/> command
    /// </summary>
    public class Code : DuetAPI.Commands.Code
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
            /// Inserted code from an intercepting connection
            /// </summary>
            Inserted = 1,

            /// <summary>
            /// Code from a macro file
            /// </summary>
            Macro = 2,

            /// <summary>
            /// Inserted macro code from an intercepting connection
            /// </summary>
            InsertedFromMacro = 3,

            /// <summary>
            /// Code being executed while an acknowledgemenet is awaited
            /// </summary>
            BlockingRegular = 4,

            /// <summary>
            /// Code with <see cref="CodeFlags.IsPrioritized"/> set
            /// </summary>
            Prioritized = 5
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
        /// Initialize the code scheduler
        /// </summary>
        public static void Init()
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
                _cancellationTokenSources[(int)channel] = new CancellationTokenSource();
            }
        }

        /// <summary>
        /// Internal type assigned by the code scheduler
        /// </summary>
        private InternalCodeType _codeType;

        /// <summary>
        /// Indicates if this code originates from an interceptor while in a macro file
        /// </summary>
        public bool IsInsertedFromMacro { get => _codeType == InternalCodeType.InsertedFromMacro; }

        /// <summary>
        /// Lock that is maintained as long as this code blocks the execution of the next code
        /// </summary>
        private IDisposable _codeStartLock;

        /// <summary>
        /// Cancellation token that may be used to cancel this code
        /// </summary>
        public CancellationToken CancellationToken { get; private set; }

        /// <summary>
        /// Create a task that waits until this code can be executed.
        /// It may be cancelled if this code is supposed to be cancelled before it is started
        /// </summary>
        /// <returns>Lock to maintain while the code is being executed internally</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        private Task<IDisposable> WaitForExecution()
        {
            // Get a cancellation token
            lock (_cancellationTokenSources)
            {
                CancellationToken = _cancellationTokenSources[(int)Channel].Token;
            }

            // Assign a priority to this code and create a task that completes when it can be started
            if (Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                _codeType = InternalCodeType.Prioritized;
                _logger.Debug("Waiting for execution of {0} (prioritized)", this);
            }
            else if (Interception.IsInterceptingConnection(SourceConnection))
            {
                if (Flags.HasFlag(CodeFlags.IsFromMacro))
                {
                    _codeType = InternalCodeType.InsertedFromMacro;
                    _logger.Debug("Waiting for execution of {0} (inserted from macro)", this);
                }
                else
                {
                    _codeType = InternalCodeType.Inserted;
                    _logger.Debug("Waiting for execution of {0} (inserted)", this);
                }
            }
            else if (Flags.HasFlag(CodeFlags.IsFromMacro))
            {
                _codeType = InternalCodeType.Macro;
                _logger.Debug("Waiting for execution of {0} (macro code)", this);
            }
            else if (!Flags.HasFlag(CodeFlags.IsFromFirmware) && SPI.Interface.IsWaitingForAcknowledgement(Channel))
            {
                _codeType = InternalCodeType.BlockingRegular;
                _logger.Debug("Waiting for execution of {0} (acknowledgement)", this);
            }
            else
            {
                _codeType = InternalCodeType.Regular;
                _logger.Debug("Waiting for execution of {0}", this);
            }
            return _codeStartLocks[(int)Channel, (int)_codeType].LockAsync(CancellationToken);
        }

        /// <summary>
        /// Start the next available G/M/T-code unless this code has already started one
        /// </summary>
        private void StartNextCode()
        {
            _codeStartLock?.Dispose();
            _codeStartLock = null;
        }

        /// <summary>
        /// Start the next available G/M/T-code and wait until this code may finish
        /// </summary>
        /// <returns></returns>
        private AwaitableDisposable<IDisposable> WaitForFinish()
        {
            AwaitableDisposable<IDisposable> finishingTask = _codeFinishLocks[(int)Channel, (int)_codeType].LockAsync();
            if (!Flags.HasFlag(CodeFlags.Unbuffered))
            {
                StartNextCode();
            }
            return finishingTask;
        }

        /// <summary>
        /// Wait for inserted codes to be internally processed
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task WaitForInsertedCodes()
        {
            if (_codeType == InternalCodeType.Regular || _codeType == InternalCodeType.Macro)
            {
                int type = (int)(_codeType == InternalCodeType.Macro ? InternalCodeType.InsertedFromMacro : InternalCodeType.Inserted);
                using (await _codeStartLocks[(int)Channel, type].LockAsync())
                {
                    using (await _codeFinishLocks[(int)Channel, type].LockAsync()) { }
                }
            }
        }

        /// <summary>
        /// Indicates if this code is waiting for a flush request
        /// </summary>
        public bool WaitingForFlush;
        #endregion

        /// <summary>
        /// Lock around the files being written
        /// </summary>
        public static AsyncLock[] FileLocks = new AsyncLock[Inputs.Total];

        /// <summary>
        /// Current stream writer of the files being written to (M28/M29)
        /// </summary>
        public static StreamWriter[] FilesBeingWritten = new StreamWriter[Inputs.Total];

        /// <summary>
        /// Constructor of a new code
        /// </summary>
        public Code() : base() { }

        /// <summary>
        /// Constructor of a new code which also parses the given text-based G/M/T-code
        /// </summary>
        public Code(string code) : base(code) { }

        /// <summary>
        /// Check if Marlin is being emulated
        /// </summary>
        /// <returns>True if Marlin is being emulated</returns>
        public async Task<bool> EmulatingMarlin()
        {
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                Compatibility compatibility = Model.Provider.Get.Inputs[Channel].Compatibility;
                return compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP;
            }
        }

        /// <summary>
        /// This indicates if this code is cancelling a print.
        /// FIXME Remove this again when the SBC interface has got its own task in RRF
        /// </summary>
        public bool CancellingPrint { get; set; }

        /// <summary>
        /// Run an arbitrary G/M/T-code and wait for it to finish
        /// </summary>
        /// <returns>Result of the code</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        public override Task<CodeResult> Execute()
        {
            // Wait until this code can be executed and then start it
            Task<CodeResult> executingTask = WaitForExecution()
                .ContinueWith(async task =>
                {
                    _codeStartLock = await task;
                    return await ExecuteInternally();
                })
                .Unwrap();

            // Return either the task itself or null and let it finish in the background
            return Flags.HasFlag(CodeFlags.Asynchronous) ? Task.FromResult<CodeResult>(null) : executingTask;
        }

        /// <summary>
        /// Indicates whether the code has been internally processed
        /// </summary>
        public bool InternallyProcessed;

        /// <summary>
        /// Indicates if this code has been resolved by an interceptor
        /// </summary>
        public bool ResolvedByInterceptor;

        /// <summary>
        /// Execute the given code internally
        /// </summary>
        /// <returns>Result of the code</returns>
        private async Task<CodeResult> ExecuteInternally()
        {
            string logSuffix = Flags.HasFlag(CodeFlags.Asynchronous) ? " asynchronously" : string.Empty;

            try
            {
                // Check if this code is supposed to be written to a file
                int numChannel = (int)Channel;
                using (await FileLocks[numChannel].LockAsync())
                {
                    if (FilesBeingWritten[numChannel] != null && Type != CodeType.MCode && MajorNumber != 29)
                    {
                        _logger.Debug("Writing {0}{1}", this, logSuffix);
                        FilesBeingWritten[numChannel].WriteLine(this);
                        return new CodeResult();
                    }
                }

                // Execute this code
                try
                {
                    _logger.Debug("Processing {0}{1}", this, logSuffix);
                    await Process();
                    _logger.Debug("Completed {0}{1}", this, logSuffix);
                }
                catch (OperationCanceledException oce)
                {
                    // Code has been cancelled
                    if (_logger.IsTraceEnabled)
                    {
                        _logger.Debug(oce, "Cancelled {0}{1}", this, logSuffix);
                    }
                    else
                    {
                        _logger.Debug("Cancelled {0}{1}", this, logSuffix);
                    }
                    throw;
                }
                catch (NotSupportedException)
                {
                    // Some codes may not be supported yet
                    Result = new CodeResult(MessageType.Error, "Code is not supported");
                    _logger.Debug("{0} is not supported{1}", this, logSuffix);
                }
                catch (Exception e)
                {
                    // This code is no longer processed if an exception has occurred
                    _logger.Error(e, "Code {0} has thrown an exception{1}", this, logSuffix);
                    throw;
                }
            }
            finally
            {
                // Make sure the next code is started no matter what happened before
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
            // Attempt to process the code internally first
            if (!InternallyProcessed && await ProcessInternally())
            {
                await CodeExecuted();
                return;
            }

            // Comments are resolved in DCS but they may be interpreted by third-party plugins
            if (Type == CodeType.Comment)
            {
                Result = new CodeResult();
                await CodeExecuted();
                return;
            }

            // Cancel this code if it came from a file and if it was processed while the print was being paused.
            // If that is not the case, let RepRapFirmware process this code
            Task<CodeResult> rrfTask;
            if (Channel == CodeChannel.File)
            {
                using (await FileExecution.Job.LockAsync())
                {
                    if (FileExecution.Job.IsPaused)
                    {
                        throw new OperationCanceledException();
                    }
                    rrfTask = Interface.ProcessCode(this);
                }
            }
            else
            {
                rrfTask = Interface.ProcessCode(this);
            }

            // Start the next available code and make sure to finish this code in the right order
            using (await WaitForFinish())
            {
                try
                {
                    // Wait for the code to be processed by RepRapFirmware
                    Result = await rrfTask;
                    await CodeExecuted();
                }
                catch (OperationCanceledException)
                {
                    // Cancelling a code clears the result
                    Result = null;
                    await CodeExecuted();
                    throw;
                }
            }
        }

        /// <summary>
        /// Attempt to process this code internally
        /// </summary>
        /// <returns>Whether the code could be processed internally</returns>
        private async Task<bool> ProcessInternally()
        {
            // Pre-process this code
            if (!Flags.HasFlag(CodeFlags.IsPreProcessed))
            {
                bool resolved = await Interception.Intercept(this, InterceptionMode.Pre);
                await WaitForInsertedCodes();

                Flags |= CodeFlags.IsPreProcessed;
                if (resolved)
                {
                    ResolvedByInterceptor = InternallyProcessed = true;
                    return true;
                }
            }

            // Evaluate echo commands
            if (Keyword == KeywordType.Echo)
            {
                if (!await Interface.Flush(this))
                {
                    throw new OperationCanceledException();
                }

                StringBuilder builder = new StringBuilder();
                foreach (string expression in KeywordArgument.Split(','))
                {
                    string trimmedExpression = expression.Trim();
                    try
                    {
                        // FIXME This should only replace Linux expressions after Pre and perform the final evaluation after Post
                        bool expressionFound;
                        object expressionResult;
                        using (await Model.Provider.AccessReadOnlyAsync())
                        {
                            expressionFound = Model.Filter.GetSpecific(trimmedExpression, true, out expressionResult);
                        }
                        if (!expressionFound)
                        {
                            expressionResult = await Interface.EvaluateExpression(Channel, trimmedExpression);
                        }

                        if (builder.Length != 0)
                        {
                            builder.Append(' ');
                        }
                        builder.Append(expressionResult);
                    }
                    catch (CodeParserException e)
                    {
                        InternallyProcessed = true;
                        Result = new CodeResult(MessageType.Error, $"Failed to evaluate \"{trimmedExpression}\": {e.Message}");
                        return true;
                    }
                }

                InternallyProcessed = true;
                Result = new CodeResult(MessageType.Success, builder.ToString());
                return true;
            }
            else if (Keyword != KeywordType.None)
            {
                throw new NotSupportedException();
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

            if (Result != null)
            {
                InternallyProcessed = true;
                return true;
            }

            // If the code could not be interpreted internally, post-process it
            if (!Flags.HasFlag(CodeFlags.IsPostProcessed))
            {
                bool resolved = await Interception.Intercept(this, InterceptionMode.Post);
                await WaitForInsertedCodes();

                Flags |= CodeFlags.IsPostProcessed;
                if (resolved)
                {
                    ResolvedByInterceptor = InternallyProcessed = true;
                    return true;
                }
            }

            // Code has not been interpreted yet - let RRF deal with it
            return false;
        }

        /// <summary>
        /// Executed when the code has finished
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task CodeExecuted()
        {
            if (Result != null)
            {
                if (!ResolvedByInterceptor)
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

                    // RepRapFirmware generally prefixes error messages with the code itself
                    if (!Flags.HasFlag(CodeFlags.IsPostProcessed))
                    {
                        foreach (Message msg in Result)
                        {
                            if (msg.Type == MessageType.Error)
                            {
                                msg.Content = ToShortString() + ": " + msg.Content;
                            }
                        }
                    }

                    // Deal with firmware emulation
                    if (!Flags.HasFlag(CodeFlags.IsFromMacro))
                    {
                        if (await EmulatingMarlin())
                        {
                            if (Result.Count != 0 && Type == CodeType.MCode && MajorNumber == 105)
                            {
                                Result[0].Content = "ok " + Result[0].Content;
                            }
                            else if (Result.IsEmpty)
                            {
                                Result.Add(MessageType.Success, "ok\n");
                            }
                            else
                            {
                                Result[^1].Content += "\nok\n";
                            }
                        }
                        else if (!Result.IsEmpty)
                        {
                            Result[^1].Content += "\n";
                        }
                        else
                        {
                            Result.Add(MessageType.Success, "\n");
                        }
                    }
                }

                // Log our own warnings and errors
                if (!Flags.HasFlag(CodeFlags.IsPostProcessed) && Channel != CodeChannel.File)
                {
                    foreach (Message msg in Result)
                    {
                        if (msg.Type != MessageType.Success)
                        {
                            // When a file is being printed, every message is automatically output and logged
                            await Utility.Logger.Log(msg);
                        }
                    }
                }
            }

            // Done
            await Interception.Intercept(this, InterceptionMode.Executed);
            await WaitForInsertedCodes();
        }
    }
}