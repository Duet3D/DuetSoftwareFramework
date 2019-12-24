using System;
using System.IO;
using System.Text.Json.Serialization;
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
        /// Lock around the files being written
        /// </summary>
        public static AsyncLock[] FileLocks = new AsyncLock[DuetAPI.Machine.Channels.Total];

        /// <summary>
        /// Current stream writer of the files being written to (M28/M29)
        /// </summary>
        public static StreamWriter[] FilesBeingWritten = new StreamWriter[DuetAPI.Machine.Channels.Total];

        /// <summary>
        /// Check if Marlin is being emulated
        /// </summary>
        /// <returns>True if Marlin is being emulated</returns>
        public async Task<bool> EmulatingMarlin()
        {
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                Compatibility compatibility = Model.Provider.Get.Channels[Channel].Compatibility;
                return compatibility == Compatibility.Marlin || compatibility == Compatibility.NanoDLP;
            }
        }

        /// <summary>
        /// Indicates whether the code has been internally processed
        /// </summary>
        public bool InternallyProcessed;

        /// <summary>
        /// Run an arbitrary G/M/T-code and wait for it to finish
        /// </summary>
        /// <returns>Result of the code</returns>
        /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
        public override async Task<CodeResult> Execute()
        {
            if (Flags.HasFlag(CodeFlags.Asynchronous))
            {
                // Execute this code in the background
                _ = Task.Run(ExecuteInternally);
                return null;
            }

            // Execute this code and wait for the result
            return await ExecuteInternally();
        }

        /// <summary>
        /// Execute the given code internally
        /// </summary>
        /// <returns>Result of the code</returns>
        private async Task<CodeResult> ExecuteInternally()
        {
            string logSuffix = Flags.HasFlag(CodeFlags.Asynchronous) ? " asynchronously" : string.Empty;

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

            // Execute it
            try
            {
                try
                {
                    // Wait for this code to be executed in the right order
                    if (!Flags.HasFlag(CodeFlags.IsPrioritized) && !Flags.HasFlag(CodeFlags.IsFromMacro))
                    {
                        await WaitForExecution();
                    }

                    // Process this code
                    _logger.Debug("Processing {0}{1}", this, logSuffix);
                    await Process();
                    _logger.Debug("Completed {0}{1}", this, logSuffix);
                }
                catch (OperationCanceledException oce)
                {
                    // Cancelling a code clears the result
                    Result = null;
                    if (_logger.IsTraceEnabled)
                    {
                        _logger.Debug(oce, "Cancelled {0}{1}", this, logSuffix);
                    }
                    else
                    {
                        _logger.Debug("Cancelled {0}{1}", this, logSuffix);
                    }
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
                    _logger.Error(e, "Code {0} has caused an exception{1}", this, logSuffix);
                    throw;
                }
                // Code has finished
                await CodeExecuted();
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
            // Starting this code. Make sure to update the file positions if applicable
            if (Channel == CodeChannel.File && FilePosition != null)
            {
                using (await FileExecution.Print.LockAsync())
                {
                    FileExecution.Print.NextFilePosition = (Length != null) ? FilePosition + Length : FilePosition;
                }
                using (await Model.Provider.AccessReadWriteAsync())
                {
                    Model.Provider.Get.Job.FilePosition = FilePosition;
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

            // RepRapFirmware buffers a number of codes so a new code can be started before the last one has finished
            StartNextCode();

            // Wait for the code to complete
            Result = await Interface.ProcessCode(this);
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

                // Deal with firmware emulation
                if (!Flags.HasFlag(CodeFlags.IsFromMacro) && await EmulatingMarlin())
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