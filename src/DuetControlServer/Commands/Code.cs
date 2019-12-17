using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Connection;
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
        /// Queue of semaphores to guarantee the ordered execution of incoming G/M/T-codes
        /// </summary>
        /// <remarks>
        /// AsyncLock implements an internal waiter queue, so it is safe to rely on it for
        /// maintaining the right order of codes being executed per code channel
        /// </remarks>
        private static readonly AsyncLock[] _codeChannelLocks = new AsyncLock[DuetAPI.Machine.Channels.Total];

        /// <summary>
        /// List of cancellation tokens to cancel pending codes while they are waiting for their execution
        /// </summary>
        private static readonly CancellationTokenSource[] _cancellationTokenSources = new CancellationTokenSource[DuetAPI.Machine.Channels.Total];

        /// <summary>
        /// Initialize the code scheduler
        /// </summary>
        public static void Init()
        {
            for (int i = 0; i < DuetAPI.Machine.Channels.Total; i++)
            {
                _codeChannelLocks[i] = new AsyncLock();
                _cancellationTokenSources[i] = new CancellationTokenSource();
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
        /// Lock that is maintained as long as this code blocks the execution of the next code
        /// </summary>
        private IDisposable _codeChannelLock;

        /// <summary>
        /// Copy of the cancellation token at the time this code is created
        /// </summary>
        private CancellationToken _cancellationToken;

        /// <summary>
        /// Wait until this code can be executed
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        private async Task WaitForExecution()
        {
            CancellationToken myToken;
            lock (_cancellationTokenSources)
            {
                myToken = _cancellationToken;
            }

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, Program.CancelSource.Token);
            IDisposable myLock = null;
            try
            {
                myLock = await _codeChannelLocks[(int)Channel].LockAsync(myToken);
                cts.Token.ThrowIfCancellationRequested();
                _codeChannelLock = myLock;
            }
            catch
            {
                myLock?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Start the next available G/M/T-code unless this code has already started one
        /// </summary>
        private void StartNextCode()
        {
            _codeChannelLock?.Dispose();
            _codeChannelLock = null;
        }

        /// <summary>
        /// Create an empty Code instance
        /// </summary>
        public Code() : base()
        {
            lock (_cancellationTokenSources)
            {
                CancellationTokenSource cts = _cancellationTokenSources[(int)Channel];
                _cancellationToken = (cts != null) ? cts.Token : default;
            }
        }

        /// <summary>
        /// Create a new Code instance and attempt to parse the given code string
        /// </summary>
        /// <param name="code">G/M/T-Code</param>
        public Code(string code) : base(code)
        {
            lock (_cancellationTokenSources)
            {
                CancellationTokenSource cts = _cancellationTokenSources[(int)Channel];
                _cancellationToken = (cts != null) ? cts.Token : default;
            }
        }

        /// <summary>
        /// Overridden code channel property to upate the cancellation token when necessary
        /// </summary>
        public override CodeChannel Channel
        {
            get => base.Channel;
            set
            {
                base.Channel = value;
                lock (_cancellationTokenSources)
                {
                    CancellationTokenSource cts = _cancellationTokenSources[(int)Channel];
                    _cancellationToken = (cts != null) ? cts.Token : default;
                }
            }
        }
        #endregion

        /// <summary>
        /// Indicates whether the code has been internally processed
        /// </summary>
        [JsonIgnore]
        public bool InternallyProcessed { get; set; }

        /// <summary>
        /// Run an arbitrary G/M/T-code and wait for it to finish
        /// </summary>
        /// <returns>Result of the code</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        public override async Task<CodeResult> Execute()
        {
            // Wait for this code to be executed in the right order
            if (!Flags.HasFlag(CodeFlags.IsPrioritized) && !Flags.HasFlag(CodeFlags.IsFromMacro))
            {
                await WaitForExecution();
            }

            try
            {
                // Process this code
                _logger.Debug("Processing {0}", this);
                await Process();
            }
            catch (Exception e)
            {
                // Cancelling a code clears the result
                Result = null;
                if (e is OperationCanceledException)
                {
                    if (_logger.IsTraceEnabled)
                    {
                        _logger.Debug(e, "Cancelled {0}", this);
                    }
                    else
                    {
                        _logger.Debug("Cancelled {0}", this);
                    }
                }
                else
                {
                    _logger.Error(e, "Code {0} has caused an exception", this);
                }
                await CodeExecuted();
                throw;
            }
            finally
            {
                // Always interpret the result of this code
                await CodeExecuted();
                _logger.Debug("Completed {0}", this);
            }
            return Result;
        }

        /// <summary>
        /// Process the code
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private async Task Process()
        {
            // Starting this code. Make sure to set the file position pointing to the next code if applicable
            if (Channel == CodeChannel.File && FilePosition != null && Length != null)
            {
                using (await FileExecution.Print.LockAsync())
                {
                    FileExecution.Print.NextFilePosition = FilePosition + Length;
                }
            }

            // Attempt to process the code internally first
            if (!InternallyProcessed && await ProcessInternally())
            {
                return;
            }

            // Comments are resolved in DCS but they may be interpreted by third-party plugins
            if (Type == CodeType.Comment)
            {
                Result = new CodeResult();
                return;
            }

            // Send the code to RepRapFirmware
            if (Flags.HasFlag(CodeFlags.Asynchronous))
            {
                // Enqueue the code for execution by RRF and return no result
                Task<CodeResult> codeTask = Interface.ProcessCode(this);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Process this code via RRF asynchronously
                        Result = await codeTask;
                        await Model.Provider.Output(Result);
                    }
                    catch (OperationCanceledException)
                    {
                        // Deal with cancelled codes
                        await CodeExecuted();
                        _logger.Debug("Cancelled {0}", this);
                        throw;
                    }
                    catch (Exception e)
                    {
                        // Deal with exeptions of asynchronous codes
                        if (e is AggregateException ae)
                        {
                            e = ae.InnerException;
                        }
                        _logger.Error(e, "Failed to execute {0} asynchronously", this);
                        throw;
                    }
                    finally
                    {
                        // Always interpret the result of this code
                        await CodeExecuted();
                        _logger.Debug("Completed {0} asynchronously", this);
                    }
                });

                // Start the next code
                StartNextCode();
            }
            else
            {
                // RepRapFirmware buffers a number of codes so a new code can be started before the last one has finished
                StartNextCode();

                // Wait for the code to complete
                Result = await Interface.ProcessCode(this);
            }
        }

        private async Task<bool> ProcessInternally()
        {
            // Pre-process this code
            if (!Flags.HasFlag(CodeFlags.IsPreProcessed))
            {
                bool resolved = await Interception.Intercept(this, InterceptionMode.Pre);
                Flags |= CodeFlags.IsPreProcessed;

                if (resolved)
                {
                    InternallyProcessed = true;
                    return true;
                }
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
                Flags |= CodeFlags.IsPostProcessed;

                if (resolved)
                {
                    InternallyProcessed = true;
                    return true;
                }
            }

            // Code has not been interpreted yet - let RRF deal with it
            return false;
        }

        private async Task CodeExecuted()
        {
            // Start the next code if that hasn't happened yet
            StartNextCode();

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

                // RepRapFirmware generally prefixes error messages with the code itself.
                // Do this only for error messages that originate either from a print or from a macro file
                if (Flags.HasFlag(CodeFlags.IsFromMacro) || Channel == CodeChannel.File)
                {
                    foreach (Message msg in Result)
                    {
                        if (msg.Type == MessageType.Error)
                        {
                            msg.Content = ToShortString() + ": " + msg.Content;
                        }
                    }
                }

                // Log warning and error replies after the code has been processed internally
                if (InternallyProcessed)
                {
                    foreach (Message msg in Result)
                    {
                        if (msg.Type != MessageType.Success && Channel != CodeChannel.File)
                        {
                            await Utility.Logger.Log(msg);
                        }
                    }
                }
            }

            // Done
            await Interception.Intercept(this, InterceptionMode.Executed);
        }
    }
}