using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using DuetControlServer.SPI.Communication.Shared;
using DuetControlServer.Utility;
using Nito.AsyncEx;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// This class accesses RepRapFirmware via SPI and deals with general communication
    /// </summary>
    public static class Interface
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        // Information about the code channels
        private static readonly Channel.Manager _channels = new();
        private static int _bytesReserved, _bufferSpace;

        // Object model queries
        private sealed class PendingModelQuery
        {
            /// <summary>
            /// Key to query
            /// </summary>
            public string Key { get; init; }

            /// <summary>
            /// Flags to query
            /// </summary>
            public string Flags { get; init; }

            /// <summary>
            /// Whether the model query has been sent
            /// </summary>
            public bool QuerySent { get; set; }

            /// <summary>
            /// Task to complete when the query has finished
            /// </summary>
            public TaskCompletionSource<byte[]> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private static readonly Queue<PendingModelQuery> _pendingModelQueries = new();
        private static DateTime _lastQueryTime = DateTime.Now;

        // Expression evaluation and variable requests
        private static readonly List<EvaluateExpressionRequest> _evaluateExpressionRequests = new();
        private static readonly List<VariableRequest> _variableRequests = new();

        // Firmware updates
        private static readonly AsyncLock _firmwareUpdateLock = new();
        private static Stream _iapStream, _firmwareStream;
        private static TaskCompletionSource _firmwareUpdateRequest;

        // Firmware halt/restart requests
        private static readonly AsyncLock _firmwareActionLock = new();
        private static TaskCompletionSource _firmwareHaltRequest;
        private static TaskCompletionSource _firmwareResetRequest;

        // Print handling
        private static readonly AsyncLock _printStateLock = new();
        private static TaskCompletionSource _setPrintInfoRequest;
        private static PrintStoppedReason _stopPrintReason;
        private static TaskCompletionSource _stopPrintRequest;

        // Miscellaneous requests
        private static readonly Queue<Tuple<MessageTypeFlags, string>> _messagesToSend = new();
        private static readonly Dictionary<uint, FileStream> _openFiles = new();
        private static uint _openFileHandle = Consts.NoFileHandle;

        // Partial incoming message (if any)
        private static string _partialGenericMessage;

        /// <summary>
        /// Print diagnostics of this class
        /// </summary>
        /// <param name="builder">String builder</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Diagnostics(StringBuilder builder)
        {
            await _channels.Diagnostics(builder);
            builder.AppendLine($"Code buffer space: {_bufferSpace}");
            DataTransfer.Diagnostics(builder);
        }

        /// <summary>
        /// Request a specific update of the object model
        /// </summary>
        /// <param name="key">Key to request</param>
        /// <param name="flags">Object model flags</param>
        /// <returns>Deserialized JSON document</returns>
        public static Task<byte[]> RequestObjectModel(string key, string flags)
        {
            if (Program.CancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<byte[]>(Program.CancellationToken);
            }
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            PendingModelQuery query = new() { Key = key, Flags = flags };
            lock (_pendingModelQueries)
            {
                _pendingModelQueries.Enqueue(query);
            }
            return query.Tcs.Task;
        }

        /// <summary>
        /// Evaluate an arbitrary expression
        /// </summary>
        /// <param name="channel">Where to evaluate the expression</param>
        /// <param name="expression">Expression to evaluate</param>
        /// <returns>Result of the evaluated expression</returns>
        /// <exception cref="CodeParserException">Failed to evaluate expression</exception>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        /// <exception cref="NotSupportedException">Incompatible firmware version</exception>
        /// <exception cref="ArgumentException">Invalid parameter</exception>
        public static Task<object> EvaluateExpression(CodeChannel channel, string expression)
        {
            if (Program.CancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<object>(Program.CancellationToken);
            }
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }
            if (DataTransfer.ProtocolVersion == 1)
            {
                throw new NotSupportedException("Incompatible firmware version");
            }
            if (Encoding.UTF8.GetByteCount(expression) >= Consts.MaxExpressionLength)
            {
                throw new ArgumentException($"Expression too long (max {Consts.MaxExpressionLength} chars)", nameof(expression));
            }

            lock (_evaluateExpressionRequests)
            {
                foreach (EvaluateExpressionRequest item in _evaluateExpressionRequests)
                {
                    if (item.Expression == expression)
                    {
                        // There is no reason to evaluate the same expression twice...
                        return item.Task;
                    }
                }

                EvaluateExpressionRequest request = new(channel, expression);
                _evaluateExpressionRequests.Add(request);
                _logger.Debug("Evaluating {0} on channel {1}", expression, channel);
                return request.Task;
            }
        }

        /// <summary>
        /// Set or delete a global or local variable
        /// </summary>
        /// <param name="channel">Where to evaluate the expression</param>
        /// <param name="createVariable">Whether the variable shall be created</param>
        /// <param name="varName">Name of the variable</param>
        /// <param name="expression">Expression to evaluate</param>
        /// <returns>Result of the evaluated expression</returns>
        /// <exception cref="CodeParserException">Failed to assign or delete variable</exception>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        /// <exception cref="NotSupportedException">Incompatible firmware version</exception>
        /// <exception cref="ArgumentException">Invalid parameter</exception>
        public static Task<object> SetVariable(CodeChannel channel, bool createVariable, string varName, string expression)
        {
            if (Program.CancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<object>(Program.CancellationToken);
            }
            if (DataTransfer.ProtocolVersion < 5)
            {
                throw new NotSupportedException("Incompatible firmware version");
            }
            if (Encoding.UTF8.GetByteCount(varName) >= Consts.MaxVariableLength)
            {
                throw new ArgumentException($"Variable too long (max {Consts.MaxVariableLength} chars)");
            }
            if (expression != null && Encoding.UTF8.GetByteCount(expression) >= Consts.MaxExpressionLength)
            {
                throw new ArgumentException($"Expression too long (max {Consts.MaxExpressionLength} chars)");
            }

            VariableRequest request;
            lock (_variableRequests)
            {
                request = new(channel, createVariable, varName, expression);
                _variableRequests.Add(request);
                if (expression != null)
                {
                    _logger.Debug("Setting variable {0} to {1} on channel {2}", varName, expression, channel);
                }
                else
                {
                    _logger.Debug("Deleting local variable {0} on channel {1}", varName, channel);
                }
            }
            return request.Task;
        }

        /// <summary>
        /// Get a channel that is currently idle in order to process a priority code
        /// </summary>
        /// <returns>Idle channel</returns>
        public static Task<CodeChannel> GetIdleChannel() => _channels.GetIdleChannel();

        /// <summary>
        /// Check if a code channel is waiting for acknowledgement
        /// </summary>
        /// <param name="channel">Channel to query</param>
        /// <returns>Whether the channel is awaiting acknowledgement</returns>
        public static bool IsWaitingForAcknowledgment(CodeChannel channel) => _channels[channel].IsWaitingForAcknowledgment;

        /// <summary>
        /// Enqueue a G/M/T-code for execution by RepRapFirmware
        /// </summary>
        /// <param name="code">Code to execute</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        public static async Task ProcessCode(Code code)
        {
            Program.CancellationToken.ThrowIfCancellationRequested();
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            using (await _channels[code.Channel].LockAsync())
            {
                _channels[code.Channel].ProcessCode(code);
            }
        }

        /// <summary>
        /// Wait for all pending codes to finish
        /// </summary>
        /// <param name="channel">Code channel to wait for</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public static async Task<bool> Flush(CodeChannel channel)
        {
            Program.CancellationToken.ThrowIfCancellationRequested();
            if (Settings.NoSpi)
            {
                return true;
            }

            Task<bool> flushTask;
            using (await _channels[channel].LockAsync())
            {
                flushTask = _channels[channel].FlushAsync();
            }
            return await flushTask;
        }

        /// <summary>
        /// Wait for all pending codes on the same stack level as the given code to finish.
        /// By default this replaces all expressions as well for convenient parsing by the code processors.
        /// </summary>
        /// <param name="code">Code waiting for the flush</param>
        /// <param name="evaluateExpressions">Evaluate all expressions when pending codes have been flushed</param>
        /// <param name="evaluateAll">Evaluate the expressions or only SBC fields if evaluateExpressions is set to true</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public static async Task<bool> Flush(Code code, bool evaluateExpressions = true, bool evaluateAll = true)
        {
            Program.CancellationToken.ThrowIfCancellationRequested();
            if (Settings.NoSpi)
            {
                return true;
            }

            Task<bool> flushTask;
            using (await _channels[code.Channel].LockAsync())
            {
                flushTask = _channels[code.Channel].FlushAsync(code);
            }

            if (await flushTask)
            {
                if (evaluateExpressions)
                {
                    // Code is about to be processed internally, evaluate potential expressions
                    await Model.Expressions.Evaluate(code, evaluateAll);
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Request an immediate emergency stop
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task EmergencyStop()
        {
            Program.CancellationToken.ThrowIfCancellationRequested();
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            Task onFirmwareHalted;
            using (await _firmwareActionLock.LockAsync(Program.CancellationToken))
            {
                _firmwareHaltRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                onFirmwareHalted = _firmwareHaltRequest.Task;
            }
            await onFirmwareHalted;
        }

        /// <summary>
        /// Perform a firmware reset and wait for it to finish
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task ResetFirmware()
        {
            Program.CancellationToken.ThrowIfCancellationRequested();
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            Task onFirmwareReset;
            using (await _firmwareActionLock.LockAsync(Program.CancellationToken))
            {
                _firmwareResetRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                onFirmwareReset = _firmwareResetRequest.Task;
            }
            await onFirmwareReset;
        }

        /// <summary>
        /// Attempt to flag the currently executing macro file as (not) pausable
        /// </summary>
        /// <param name="channel">Code channel where the macro is being executed</param>
        /// <param name="isPausable">Whether or not the macro file is pausable</param>
        /// <returns>Asynchronous task</returns>
        public static async Task SetMacroPausable(CodeChannel channel, bool isPausable)
        {
            using (await _channels[channel].LockAsync())
            {
                await _channels[channel].SetMacroPausable(isPausable);
            }
        }

        /// <summary>
        /// Update the print file info in the firmware
        /// </summary>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        public static async Task SetPrintFileInfo()
        {
            Program.CancellationToken.ThrowIfCancellationRequested();
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            Task task;
            using (await _printStateLock.LockAsync(Program.CancellationToken))
            {
                _setPrintInfoRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                task = _setPrintInfoRequest.Task;
            }
            await task;
        }

        /// <summary>
        /// Notify the firmware that the file print has been stopped
        /// </summary>
        /// <param name="reason">Reason why the print has stopped</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        public static async Task StopPrint(PrintStoppedReason reason)
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            if (!Program.CancellationToken.IsCancellationRequested)
            {
                Task onPrintStopped;
                using (await _printStateLock.LockAsync(Program.CancellationToken))
                {
                    _stopPrintReason = reason;
                    _stopPrintRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    onPrintStopped = _stopPrintRequest.Task;
                }
                await onPrintStopped;
            }
        }

        /// <summary>
        /// Class representing an acquired movement lock
        /// </summary>
        public class MovementLock : IAsyncDisposable
        {
            /// <summary>
            /// Constructor of this class
            /// </summary>
            /// <param name="channel">Locked code channel</param>
            public MovementLock(CodeChannel channel) => _channel = channel;

            /// <summary>
            /// Locked code channel
            /// </summary>
            private readonly CodeChannel _channel;

            /// <summary>
            /// Called when this instance is being disposed
            /// </summary>
            /// <returns>Asynchronous task</returns>
            public async ValueTask DisposeAsync()
            {
                GC.SuppressFinalize(this);
                await UnlockAll(_channel);
            }
        }

        /// <summary>
        /// Lock the move module and wait for standstill
        /// </summary>
        /// <param name="channel">Code channel acquiring the lock</param>
        /// <returns>Disposable lock object that releases the lock when disposed</returns>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        /// <exception cref="OperationCanceledException">Failed to get movement lock</exception>
        public static async Task<IAsyncDisposable> LockMovementAndWaitForStandstill(CodeChannel channel)
        {
            Program.CancellationToken.ThrowIfCancellationRequested();
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            Task<bool> lockTask;
            using (await _channels[channel].LockAsync())
            {
                lockTask = _channels[channel].LockMovementAndWaitForStandstill();
            }

            if (await lockTask)
            {
                return new MovementLock(channel);
            }
            throw new OperationCanceledException();
        }

        /// <summary>
        /// Unlock all resources occupied by the given channel
        /// </summary>
        /// <param name="channel">Channel holding the resources</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        private static async Task UnlockAll(CodeChannel channel)
        {
            Program.CancellationToken.ThrowIfCancellationRequested();
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            Task unlockTask;
            using (await _channels[channel].LockAsync())
            {
                unlockTask = _channels[channel].UnlockAll();
            }
            await unlockTask;
        }

        /// <summary>
        /// Wait for potential firmware update to finish
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task WaitForUpdate()
        {
            using (await _firmwareUpdateLock.LockAsync(Program.CancellationToken))
            {
                // This lock is acquired as long as a firmware update is in progress; no need to do anything else
            }
        }

        /// <summary>
        /// Perform an update of the main firmware via IAP
        /// </summary>
        /// <param name="iapStream">IAP binary</param>
        /// <param name="firmwareStream">Firmware binary</param>
        /// <exception cref="InvalidOperationException">Firmware is already being updated or not connected over SPI</exception>
        /// <returns>Asynchronous task</returns>
        public static async Task UpdateFirmware(Stream iapStream, Stream firmwareStream)
        {
            Program.CancellationToken.ThrowIfCancellationRequested();
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            TaskCompletionSource tcs;
            using (await _firmwareUpdateLock.LockAsync(Program.CancellationToken))
            {
                if (_firmwareUpdateRequest != null)
                {
                    throw new InvalidOperationException("Firmware is already being updated");
                }

                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _iapStream = iapStream;
                _firmwareStream = firmwareStream;
                _firmwareUpdateRequest = tcs;
            }
            await tcs.Task;
        }

        /// <summary>
        /// Perform the firmware update internally
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static void PerformFirmwareUpdate()
        {
            using (Model.Provider.AccessReadWrite())
            {
                Model.Provider.Get.State.Status = MachineStatus.Updating;
            }

            // Get the CRC16 checksum of the firmware binary
            ushort crc16 = CRC16.Calculate(_firmwareStream);

            // Send the IAP binary to the firmware
            _logger.Info("Flashing IAP binary");
            bool dataSent;
            do
            {
                dataSent = DataTransfer.WriteIapSegment(_iapStream);
                if (_logger.IsDebugEnabled)
                {
                    Console.Write('.');
                }
            }
            while (dataSent);
            if (_logger.IsDebugEnabled)
            {
                Console.WriteLine();
            }

            // Start the IAP binary
            DataTransfer.StartIap();

            // Send the firmware binary to the IAP program
            int numRetries = 0;
            do
            {
                if (numRetries != 0)
                {
                    _logger.Error("Firmware checksum verification failed");
                }

                _logger.Info("Flashing RepRapFirmware");
                _firmwareStream.Seek(0, SeekOrigin.Begin);

                try
                {
                    while (DataTransfer.FlashFirmwareSegment(_firmwareStream))
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            Console.Write('.');
                        }
                    }
                    if (_logger.IsDebugEnabled)
                    {
                        Console.WriteLine();
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e);
                    Logger.LogOutput(MessageType.Error, "Failed to flash flash firmware. Please install it manually.");
                    throw;
                }

                _logger.Info("Verifying checksum");
            }
            while (!DataTransfer.VerifyFirmwareChecksum(_firmwareStream.Length, crc16) && ++numRetries < 3);

            if (numRetries == 3)
            {
                // Failed to flash the firmware
                Logger.LogOutput(MessageType.Error, "Could not flash firmware after 3 attempts. Please install it manually.");
                throw new OperationCanceledException("Failed to flash firmware after 3 attempts");
            }

            // Wait for the IAP binary to restart the controller
            DataTransfer.WaitForIapReset();
            _logger.Info("Firmware update successful");
        }

        /// <summary>
        /// Send a message to the firmware
        /// </summary>
        /// <param name="flags">Message flags</param>
        /// <param name="message">Message content</param>
        /// <exception cref="InvalidOperationException">Incompatible firmware or not connected over SPI</exception>
        /// <exception cref="NotSupportedException">Incompatible firmware version</exception>
        /// <exception cref="ArgumentException">Invalid parameter</exception>
        public static void SendMessage(MessageTypeFlags flags, string message)
        {
            if (Program.CancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }
            if (DataTransfer.ProtocolVersion == 1)
            {
                throw new NotSupportedException("Incompatible firmware version");
            }
            if (message.Length > Settings.MaxMessageLength)
            {
                throw new ArgumentException($"{nameof(message)} too long");
            }

            lock (_messagesToSend)
            {
                _messagesToSend.Enqueue(new Tuple<MessageTypeFlags, string>(flags, message));
            }
        }

        /// <summary>
        /// Abort all files in RRF on the given channel asynchronously
        /// </summary>
        /// <param name="channel">Channel where all the files have been aborted</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        public static async Task AbortAll(CodeChannel channel)
        {
            Program.CancellationToken.ThrowIfCancellationRequested();
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            using (await _channels[channel].LockAsync())
            {
                await _channels[channel].AbortFilesAsync(true, false);
            }
        }

        /// <summary>
        /// Perform communication with the RepRapFirmware controller over SPI
        /// </summary>
        public static void Run()
        {
            do
            {
                bool blockTask = false, skipChannels = false;
                using (_firmwareActionLock.Lock(Program.CancellationToken))
                {
                    // Check if an emergency stop has been requested
                    if (_firmwareHaltRequest != null)
                    {
                        Invalidate();
                        if (DataTransfer.WriteEmergencyStop())
                        {
                            _logger.Warn("Emergency stop");
                            _firmwareHaltRequest.SetResult();
                            _firmwareHaltRequest = null;
                        }
                        skipChannels = true;
                    }

                    // Check if a firmware reset has been requested
                    if (_firmwareResetRequest != null)
                    {
                        Invalidate();
                        if (DataTransfer.WriteReset())
                        {
                            _logger.Warn("Resetting controller");
                            DataTransfer.PerformFullTransfer();
                            _firmwareResetRequest.SetResult();
                            _firmwareResetRequest = null;

                            blockTask = !Settings.NoTerminateOnReset;
                        }
                        skipChannels = true;
                    }
                }

                // Check if a firmware update is supposed to be performed
                using (_firmwareUpdateLock.Lock(Program.CancellationToken))
                {
                    if (_iapStream != null && _firmwareStream != null)
                    {
                        Invalidate();

                        try
                        {
                            PerformFirmwareUpdate();
                            _firmwareUpdateRequest?.SetResult();
                            _firmwareUpdateRequest = null;
                        }
                        catch (Exception e)
                        {
                            _firmwareUpdateRequest?.SetException(e);
                            _firmwareUpdateRequest = null;

                            if (!Settings.UpdateOnly && Settings.NoTerminateOnReset && e is OperationCanceledException)
                            {
                                _logger.Debug(e, "Firmware update cancelled");
                            }
                            throw;
                        }

                        _iapStream = _firmwareStream = null;
                        blockTask = Settings.UpdateOnly || !Settings.NoTerminateOnReset;
                    }
                }
                if (blockTask)
                {
                    // Wait for the requesting task to complete, it will terminate DCS next
                    Task.Delay(-1, Program.CancellationToken).Wait();
                }

                // Invalidate data if a controller reset has been performed
                if (DataTransfer.HadReset())
                {
                    Invalidate();
                    Logger.LogOutput(MessageType.Warning, "SPI connection has been reset");
                }

                // Check for changes of the print status.
                using (_printStateLock.Lock(Program.CancellationToken))
                {
                    if (_setPrintInfoRequest != null && DataTransfer.WritePrintFileInfo(Model.Provider.Get.Job.File))
                    {
                        // The packet providing file info has be sent first because it includes a time_t value that must reside on a 64-bit boundary!
                        _setPrintInfoRequest.SetResult();
                        _setPrintInfoRequest = null;
                    }
                    else
                    {
                        if (_stopPrintRequest != null && DataTransfer.WritePrintStopped(_stopPrintReason))
                        {
                            _stopPrintRequest.SetResult();
                            _stopPrintRequest = null;
                        }
                    }
                }

                // Process incoming packets
                for (int i = 0; i < DataTransfer.PacketsToRead; i++)
                {
                    try
                    {
                        PacketHeader? packet = DataTransfer.ReadNextPacket();
                        if (packet == null)
                        {
                            _logger.Error("Read invalid packet");
                            break;
                        }
                        ProcessPacket(packet.Value);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        DataTransfer.DumpMalformedPacket();
                        throw;
                    }
                }
                _bytesReserved = 0;

                // Process pending codes, macro files and requests for resource locks/unlocks as well as flush requests
                if (!skipChannels)
                {
                    _channels.Run();
                }

                // Request object model updates
                if (DataTransfer.ProtocolVersion == 1)
                {
                    if (DateTime.Now - _lastQueryTime > TimeSpan.FromMilliseconds(Settings.ModelUpdateInterval))
                    {
                        using (Model.Provider.AccessReadOnly())
                        {
                            if (Model.Provider.Get.Boards.Count == 0 && DataTransfer.WriteGetLegacyConfigResponse())
                            {
                                // We no longer support regular status responses except to obtain the board name for updating the firmware
                                _lastQueryTime = DateTime.Now;
                            }
                        }
                    }
                }
                else
                {
                    lock (_pendingModelQueries)
                    {
                        if (_pendingModelQueries.TryPeek(out PendingModelQuery query) &&
                            !query.QuerySent && DataTransfer.WriteGetObjectModel(query.Key, query.Flags))
                        {
                            query.QuerySent = true;
                        }
                    }
                }

                {
                    int numEvaluationsSent = 0;

                    // Ask for expressions to be evaluated
                    lock (_evaluateExpressionRequests)
                    {
                        foreach (EvaluateExpressionRequest request in _evaluateExpressionRequests)
                        {
                            if (!request.Written && DataTransfer.WriteEvaluateExpression(request.Channel, request.Expression))
                            {
                                request.Written = true;

                                numEvaluationsSent++;
                                if (numEvaluationsSent >= Consts.MaxEvaluationRequestsPerTransfer)
                                {
                                    break;
                                }
                            }
                        }
                    }

                    // Perform variable updates
                    lock (_variableRequests)
                    {
                        foreach (VariableRequest request in _variableRequests.ToList())
                        {
                            if (!request.Written)
                            {
                                if ((request.Expression != null && DataTransfer.WriteSetVariable(request.Channel, request.CreateVariable, request.VariableName, request.Expression)) ||
                                    (request.Expression == null && DataTransfer.WriteDeleteLocalVariable(request.Channel, request.VariableName)))
                                {
                                    if (request.Expression == null)
                                    {
                                        request.SetResult(null);
                                        _variableRequests.Remove(request);
                                    }
                                    else
                                    {
                                        request.Written = true;
                                    }

                                    numEvaluationsSent++;
                                    if (numEvaluationsSent >= Consts.MaxEvaluationRequestsPerTransfer)
                                    {
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                // Send pending messages
                lock (_messagesToSend)
                {
                    while (_messagesToSend.TryPeek(out Tuple<MessageTypeFlags, string> message))
                    {
                        if (DataTransfer.WriteMessage(message.Item1, message.Item2))
                        {
                            _messagesToSend.Dequeue();
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                // Do another full SPI transfer
                DataTransfer.PerformFullTransfer();
            }
            while (!Program.CancellationToken.IsCancellationRequested);
        }

        /// <summary>
        /// Send a pending code to the firmware
        /// </summary>
        /// <param name="code">Code to send</param>
        /// <param name="codeLength">Length of the binary code in bytes</param>
        /// <returns>Whether the code could be sent</returns>
        internal static bool SendCode(Code code, int codeLength)
        {
            if (_bufferSpace > codeLength && DataTransfer.WriteCode(code))
            {
                _bytesReserved += codeLength;
                _bufferSpace -= codeLength;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Process a packet from RepRapFirmware
        /// </summary>
        /// <param name="packet">Received packet</param>
        /// <returns>Asynchronous task</returns>
        private static void ProcessPacket(PacketHeader packet)
        {
            switch ((Request)packet.Request)
            {
                case Request.ResendPacket:
                    DataTransfer.ResendPacket(packet, out Communication.SbcRequests.Request sbcRequest);
                    if (sbcRequest != Communication.SbcRequests.Request.LockMovementAndWaitForStandstill)
                    {
                        // It's expected that RRF will need a moment to lock the movement but report other resend requests
                        _logger.Warn("Resending packet #{0} (request {1})", packet.Id, sbcRequest);
                    }
                    break;
                case Request.ObjectModel:
                    HandleObjectModel();
                    break;
                case Request.CodeBufferUpdate:
                    HandleCodeBufferUpdate();
                    break;
                case Request.Message:
                    HandleMessage();
                    break;
                case Request.ExecuteMacro:
                    HandleMacroRequest();
                    break;
                case Request.AbortFile:
                    HandleAbortFileRequest();
                    break;
                case Request.PrintPaused:
                    HandlePrintPaused();
                    break;
                case Request.Locked:
                    HandleResourceLocked();
                    break;
                case Request.FileChunk:
                    HandleFileChunkRequest();
                    break;
                case Request.EvaluationResult:
                    HandleEvaluationResult();
                    break;
                case Request.DoCode:
                    HandleDoCode();
                    break;
                case Request.WaitForAcknowledgement:
                    HandleWaitForAcknowledgement();
                    break;
                case Request.MacroFileClosed:
                    HandleMacroFileClosed();
                    break;
                case Request.MessageAcknowledged:
                    HandleMessageAcknowledgement();
                    break;
                case Request.VariableResult:
                    HandleVariableResult();
                    break;
                case Request.CheckFileExists:
                    HandleCheckFileExists();
                    break;
                case Request.DeleteFileOrDirectory:
                    HandleDeleteFileOrDirectory();
                    break;
                case Request.OpenFile:
                    HandleOpenFile();
                    break;
                case Request.ReadFile:
                    HandleReadFile();
                    break;
                case Request.WriteFile:
                    HandleWriteFile();
                    break;
                case Request.SeekFile:
                    HandleSeekFile();
                    break;
                case Request.TruncateFile:
                    HandleTruncateFile();
                    break;
                case Request.CloseFile:
                    HandleCloseFile();
                    break;
            }
        }

        /// <summary>
        /// Process an object model response
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static void HandleObjectModel()
        {
            _logger.Trace("Received object model");
            if (DataTransfer.ProtocolVersion > 1)
            {
                DataTransfer.ReadObjectModel(out ReadOnlySpan<byte> json);
                lock (_pendingModelQueries)
                {
                    if (_pendingModelQueries.TryDequeue(out PendingModelQuery query))
                    {
                        query.Tcs.SetResult(json.ToArray());
                    }
                    else if (!Program.CancellationToken.IsCancellationRequested)
                    {
                        _logger.Warn("Failed to find query for object model response");
                    }
                }
            }
            else
            {
                DataTransfer.ReadLegacyConfigResponse(out ReadOnlySpan<byte> json);
                Model.Updater.ProcessLegacyConfigResponse(json.ToArray());
            }
        }

        /// <summary>
        /// Update the amount of buffer space
        /// </summary>
        private static void HandleCodeBufferUpdate()
        {
            DataTransfer.ReadCodeBufferUpdate(out ushort bufferSpace);
            _bufferSpace = bufferSpace - _bytesReserved;
            _logger.Trace("Buffer space available: {0}", _bufferSpace);
        }

        /// <summary>
        /// Buffer for truncated log messages
        /// </summary>
        private static string _partialLogMessage;

        /// <summary>
        /// Process an incoming message
        /// </summary>
        private static void HandleMessage()
        {
            DataTransfer.ReadMessage(out MessageTypeFlags flags, out string reply);
            _logger.Trace("Received message [{0}] {1}", flags, reply);

            // Deal with log messages
            if ((flags & MessageTypeFlags.LogOff) != MessageTypeFlags.LogOff)
            {
                _partialLogMessage += reply;
                if (!flags.HasFlag(MessageTypeFlags.PushFlag))
                {
                    if (!string.IsNullOrWhiteSpace(_partialLogMessage))
                    {
                        MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                                            : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                                                : MessageType.Success;
                        LogLevel level = flags.HasFlag(MessageTypeFlags.LogOff) ? LogLevel.Off
                                            : flags.HasFlag(MessageTypeFlags.LogWarn) ? LogLevel.Warn
                                                : flags.HasFlag(MessageTypeFlags.LogInfo) ? LogLevel.Info
                                                    : LogLevel.Debug;
                        Logger.Log(level, type, _partialLogMessage.TrimEnd());
                    }
                    _partialLogMessage = null;
                }
            }

            // Check if this is a code reply
            if (flags.HasFlag(MessageTypeFlags.BinaryCodeReplyFlag))
            {
                if (!_channels.HandleReply(flags, reply))
                {
                    // Must be a left-over error message...
                    OutputGenericMessage(flags, reply);
                }
            }
            else if ((flags & MessageTypeFlags.GenericMessage) == MessageTypeFlags.GenericMessage)
            {
                // Generic messages to the main object model
                OutputGenericMessage(flags, reply);
            }
            else
            {
                // Targeted messages are handled by the IPC processors
                MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                    : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                        : MessageType.Success;
                IPC.Processors.CodeStream.RecordMessage(flags, new Message(type, reply));
                IPC.Processors.ModelSubscription.RecordMessage(flags, new Message(type, reply));
            }
        }

        /// <summary>
        /// Output a generic message
        /// </summary>
        /// <param name="flags">Message flags</param>
        /// <param name="reply">Message content</param>
        private static void OutputGenericMessage(MessageTypeFlags flags, string reply)
        {
            _partialGenericMessage += reply;
            if (!flags.HasFlag(MessageTypeFlags.PushFlag))
            {
                if (!string.IsNullOrWhiteSpace(_partialGenericMessage))
                {
                    MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                                        : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                                            : MessageType.Success;
                    Model.Provider.Output(type, _partialGenericMessage.TrimEnd());
                }
                _partialGenericMessage = null;
            }
        }

        /// <summary>
        /// Handle a macro request
        /// </summary>
        private static void HandleMacroRequest()
        {
            DataTransfer.ReadMacroRequest(out CodeChannel channel, out bool fromCode, out string filename);
            _logger.Trace("Received macro request for file {0} on channel {1}", filename, channel);

            using (_channels[channel].Lock())
            {
                _channels[channel].DoMacroFile(filename, fromCode);
            }
        }

        /// <summary>
        /// Handle a file abort request
        /// </summary>
        private static void HandleAbortFileRequest()
        {
            DataTransfer.ReadAbortFile(out CodeChannel channel, out bool abortAll);
            _logger.Info("Received file abort request on channel {0} for {1}", channel, abortAll ? "all files" : "the last file");

            using (_channels[channel].Lock())
            {
                _channels[channel].AbortFiles(abortAll, true);
            }
        }

        /// <summary>
        /// Deal with paused print events
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static void HandlePrintPaused()
        {
            DataTransfer.ReadPrintPaused(out uint filePosition, out PrintPausedReason pauseReason);
            _logger.Debug("Received print pause notification for file position {0}, reason {1}", filePosition, pauseReason);

            // Update the object model
            using (Model.Provider.AccessReadWrite())
            {
                Model.Provider.Get.State.Status = MachineStatus.Paused;
            }

            // Pause the print
            using (FileExecution.Job.Lock())
            {
                // Do NOT supply a file position if this is a pause request initiated from G-code because that would lead to an endless loop
                bool filePositionValid = (filePosition != Consts.NoFilePosition) && (pauseReason != PrintPausedReason.GCode) && (pauseReason != PrintPausedReason.FilamentChange);
                FileExecution.Job.Pause(filePositionValid ? filePosition : null, pauseReason);
            }

            // Resolve pending and buffered codes on the file channel
            using (_channels[CodeChannel.File].Lock())
            {
                _channels[CodeChannel.File].PrintPaused();
            }
        }

        /// <summary>
        /// Deal with the confirmation that a resource has been locked
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static void HandleResourceLocked()
        {
            DataTransfer.ReadCodeChannel(out CodeChannel channel);
            _logger.Trace("Received resource locked notification for channel {0}", channel);

            using (_channels[channel].Lock())
            {
                _channels[channel].ResourceLocked();
            }
        }

        /// <summary>
        /// Process a request for a chunk of a given file
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static void HandleFileChunkRequest()
        {
            DataTransfer.ReadFileChunkRequest(out string filename, out uint offset, out int maxLength);
            _logger.Debug("Received file chunk request for {0}, offset {1}, maxLength {2}", filename, offset, maxLength);

            try
            {
                string filePath;
                if (filename.EndsWith(".bin") || filename.EndsWith(".uf2"))
                {
                    filePath = FilePath.ToPhysical(filename, FileDirectory.Firmware);
                    if (!File.Exists(filePath))
                    {
                        filePath = FilePath.ToPhysical(filename, FileDirectory.System);
                    }
                }
                else
                {
                    filePath = FilePath.ToPhysical(filename, FileDirectory.System);
                }

                using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read)
                {
                    Position = offset
                };
                Span<byte> buffer = stackalloc byte[maxLength];
                int bytesRead = fs.Read(buffer);

                DataTransfer.WriteFileChunk((bytesRead > 0) ? buffer[..bytesRead] : Span<byte>.Empty, fs.Length);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to send requested file chunk of {0}", filename);
                DataTransfer.WriteFileChunk(null, 0);
            }
        }

        /// <summary>
        /// Handle the result of an evaluated expression
        /// </summary>
        private static void HandleEvaluationResult()
        {
            DataTransfer.ReadEvaluationResult(out string expression, out object result);
            _logger.Trace("Received evaluation result for expression {0} = {1}", expression, result);

            lock (_evaluateExpressionRequests)
            {
                foreach (EvaluateExpressionRequest request in _evaluateExpressionRequests)
                {
                    if (request.Expression == expression)
                    {
                        if (result is Exception exception)
                        {
                            request.SetException(exception);
                        }
                        else
                        {
                            request.SetResult(result);
                        }
                        _evaluateExpressionRequests.Remove(request);
                        return;
                    }
                }
            }

            _logger.Warn("Unresolved evaluation result for expression {0} = {1}", expression, result);
        }

        /// <summary>
        /// Handle a firmware request to perform a G/M/T-code in DSF
        /// </summary>
        private static void HandleDoCode()
        {
            DataTransfer.ReadDoCode(out CodeChannel channel, out string code);
            _logger.Trace("Received firmware code request on channel {0} => {1}", channel, code);

            using (_channels[channel].Lock())
            {
                _channels[channel].DoFirmwareCode(code);
            }
        }

        /// <summary>
        /// Handle a firmware request to wait for a message to be acknowledged
        /// </summary>
        private static void HandleWaitForAcknowledgement()
        {
            DataTransfer.ReadCodeChannel(out CodeChannel channel);
            _logger.Trace("Received wait for message acknowledgement on channel {0}", channel);

            using (_channels[channel].Lock())
            {
                _channels[channel].WaitForAcknowledgement();
            }
        }

        /// <summary>
        /// Handle a firmware request that is sent when RRF has internally closed a macro file
        /// </summary>
        private static void HandleMacroFileClosed()
        {
            DataTransfer.ReadCodeChannel(out CodeChannel channel);
            _logger.Trace("Received file closal on channel {0}", channel);

            using (_channels[channel].Lock())
            {
                _channels[channel].MacroFileClosed();
            }
        }

        /// <summary>
        /// Handle a firmware request that is sent when RRF has successfully acknowledged a blocking message
        /// </summary>
        private static void HandleMessageAcknowledgement()
        {
            DataTransfer.ReadCodeChannel(out CodeChannel channel);
            _logger.Trace("Received message acknowledgement on channel {0}", channel);

            using (_channels[channel].Lock())
            {
                _channels[channel].MessageAcknowledged();
            }
        }

        /// <summary>
        /// Handle the result of a variable assignment
        /// </summary>
        private static void HandleVariableResult()
        {
            DataTransfer.ReadEvaluationResult(out string varName, out object result);
            _logger.Trace("Received variable assignment result for {0} = {1}", varName, result);

            lock (_evaluateExpressionRequests)
            {
                foreach (VariableRequest request in _variableRequests)
                {
                    if (request.VariableName == varName)
                    {
                        if (result is Exception exception)
                        {
                            request.SetException(exception);
                        }
                        else
                        {
                            request.SetResult(result);
                        }
                        _variableRequests.Remove(request);
                        return;
                    }
                }
            }

            _logger.Warn("Unresolved variable set result for variable {0} = {1}", varName, result);
        }

        /// <summary>
        /// Check if a file exists
        /// </summary>
        private static void HandleCheckFileExists()
        {
            DataTransfer.ReadCheckFileExists(out string filename);
            _logger.Debug("Checking if file {0} exists", filename);

            try
            {
                string physicalFile = FilePath.ToPhysical(filename);
                bool exists = File.Exists(physicalFile);
                DataTransfer.WriteCheckFileExistsResult(exists);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to check if file {0} exists", filename);
                DataTransfer.WriteCheckFileExistsResult(false);
            }
        }

        /// <summary>
        /// Delete a file or directory
        /// </summary>
        private static void HandleDeleteFileOrDirectory()
        {
            DataTransfer.ReadDeleteFileOrDirectory(out string filename);
            _logger.Debug("Attempting to delete {0}", filename);

            try
            {
                string physicalFile = FilePath.ToPhysical(filename);
                if (Directory.Exists(physicalFile))
                {
                    Directory.Delete(physicalFile);
                    DataTransfer.WriteFileDeleteResult(true);
                }
                else
                {
                    File.Delete(physicalFile);
                    DataTransfer.WriteFileDeleteResult(true);
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to delete file or directory {0}", filename);
                DataTransfer.WriteFileDeleteResult(false);
            }
        }

        /// <summary>
        /// Try to open a file
        /// </summary>
        private static void HandleOpenFile()
        {
            DataTransfer.ReadOpenFile(out string filename, out bool forWriting, out bool append, out long preAllocSize);
            _logger.Debug("Opening {0} for {1} ({2}appending), prealloc {3}", filename, forWriting ? "writing" : "reading", append ? string.Empty : "not ", preAllocSize);

            try
            {
                // Resolve the path and create the parent directory if necessary
                string physicalFile = FilePath.ToPhysical(filename), parentDirectory = Path.GetDirectoryName(physicalFile);
                if (!Directory.Exists(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                // Try to open the file as requested
                FileMode fsMode = forWriting ? (append ? FileMode.Append : FileMode.Create) : FileMode.Open;
                FileStream fs = new(physicalFile, fsMode);
                if (forWriting && !append && preAllocSize > 0)
                {
                    fs.SetLength(preAllocSize);
                }

                // Register a handle and send it back
                _openFileHandle++;
                if (_openFileHandle == Consts.NoFileHandle)
                {
                    _openFileHandle++;
                }
                _openFiles.Add(_openFileHandle, fs);

                _logger.Debug("File {0} opened with handle #{1}", filename, _openFileHandle);
                DataTransfer.WriteOpenFileResult(_openFileHandle, fs.Length);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to open {0} for {1}", filename, forWriting ? "writing" : "reading");
                DataTransfer.WriteOpenFileResult(Consts.NoFileHandle, 0);
            }
        }

        /// <summary>
        /// Read more from a given file
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static void HandleReadFile()
        {
            DataTransfer.ReadFileRequest(out uint handle, out int maxLength);
            _logger.Debug("Reading up to {0} bytes from file #{1}", maxLength, handle);

            try
            {
                // Read file content as requested
                FileStream fs = _openFiles[handle];
                Span<byte> data = stackalloc byte[maxLength];
                int bytesRead = fs.Read(data);

                // Send it back
                DataTransfer.WriteFileReadResult((bytesRead > 0) ? data[..bytesRead] : Span<byte>.Empty, bytesRead);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to read {0} bytes from file #{1}", maxLength, handle);
                DataTransfer.WriteFileReadResult(Span<byte>.Empty, -1);
            }
        }

        /// <summary>
        /// Write more to a given file
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static void HandleWriteFile()
        {
            DataTransfer.ReadWriteRequest(out uint handle, out ReadOnlySpan<byte> data);
            _logger.Debug("Writing {0} bytes to file #{1}", data.Length, handle);

            try
            {
                // Write file content as requested
                FileStream fs = _openFiles[handle];
                fs.Write(data);

                // Send it back
                DataTransfer.WriteFileWriteResult(true);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to write {0} bytes to file #{1}", data.Length, handle);
                DataTransfer.WriteFileWriteResult(false);
            }
        }

        /// <summary>
        /// Go to a specific position in a file
        /// </summary>
        private static void HandleSeekFile()
        {
            DataTransfer.ReadSeekFile(out uint handle, out long offset);
            _logger.Debug("Seeking to position {0} in file #{1}", offset, handle);

            try
            {
                // Go to the file position as requested
                FileStream fs = _openFiles[handle];
                fs.Seek(offset, SeekOrigin.Begin);

                // Send it back
                DataTransfer.WriteFileSeekResult(true);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to go to position {0} in file #{1}", offset, handle);
                DataTransfer.WriteFileSeekResult(false);
            }
        }

        /// <summary>
        /// Go to a specific position in a file
        /// </summary>
        private static void HandleTruncateFile()
        {
            DataTransfer.ReadTruncateFile(out uint handle);
            _logger.Debug("Truncating file #{0}", handle);

            try
            {
                // Go to the file position as requested
                FileStream fs = _openFiles[handle];
                fs.SetLength(fs.Position);
                _logger.Debug("Truncated file #{0} at byte {1}", handle, fs.Length);

                // Send it back
                DataTransfer.WriteFileTruncateResult(true);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to truncate file #{0}", handle);
                DataTransfer.WriteFileTruncateResult(false);
            }
        }

        /// <summary>
        /// Check if a file exists
        /// </summary>
        private static void HandleCloseFile()
        {
            DataTransfer.ReadCloseFile(out uint handle);
            _logger.Debug("Closing file #{0}", handle);

            try
            {
                // Close the file stream
                FileStream fs = _openFiles[handle];
                fs.Close();

                // Remove it again from the list of open files
                _openFiles.Remove(handle);

                // RRF doesn't expect a response for this...
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to close file #{0}", handle);
            }
        }

        /// <summary>
        /// Called to shut down the SPI subsystem
        /// </summary>
        public static void Shutdown() => Invalidate();

        /// <summary>
        /// Invalidate every resource due to a critical event
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static void Invalidate()
        {
            // No longer starting or stopping a print. Must do this before aborting the print
            using (_printStateLock.Lock(Program.CancellationToken))
            {
                if (_setPrintInfoRequest != null)
                {
                    _setPrintInfoRequest.SetCanceled();
                    _setPrintInfoRequest = null;
                }
                if (_stopPrintRequest != null)
                {
                    _stopPrintRequest.SetResult();      // called from the print task so this never throws an exception
                    _stopPrintRequest = null;
                }
            }

            // Cancel the file being printed
            using (FileExecution.Job.Lock())
            {
                FileExecution.Job.Abort();
            }

            // Resolve pending macros, unbuffered (system) codes and flush requests
            foreach (Channel.Processor channel in _channels)
            {
                using (channel.Lock())
                {
                    channel.Invalidate();
                }
            }
            _bytesReserved = _bufferSpace = 0;

            // Resolve pending object model requests
            lock (_pendingModelQueries)
            {
                foreach (PendingModelQuery query in _pendingModelQueries)
                {
                    query.Tcs.SetCanceled();
                }
                _pendingModelQueries.Clear();
            }

            // Resolve pending expression evaluation and variable requests
            lock (_evaluateExpressionRequests)
            {
                foreach (EvaluateExpressionRequest request in _evaluateExpressionRequests)
                {
                    request.SetCanceled();
                }
                _evaluateExpressionRequests.Clear();
            }

            lock (_variableRequests)
            {
                foreach (VariableRequest request in _variableRequests)
                {
                    request.SetCanceled();
                }
                _variableRequests.Clear();
            }

            // Clear messages to send to the firmware
            lock (_messagesToSend)
            {
                _messagesToSend.Clear();
            }

            // Close all the files
            foreach (var kv in _openFiles)
            {
                kv.Value.Close();
            }
            _openFiles.Clear();

            // Notify the updater task
            Model.Updater.ConnectionLost();
        }
    }
}
