using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.FileExecution;
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
        private static readonly Channel.Manager _channels = new Channel.Manager();
        private static int _bytesReserved = 0, _bufferSpace = 0;

        // Object model queries
        private sealed class PendingModelQuery
        {
            /// <summary>
            /// Key to query
            /// </summary>
            public string Key { get; set; }

            /// <summary>
            /// Flags to query
            /// </summary>
            public string Flags { get; set; }

            /// <summary>
            /// Whether the model query has been sent
            /// </summary>
            public bool QuerySent { get; set; }

            /// <summary>
            /// Task to complete when the query has finished
            /// </summary>
            public TaskCompletionSource<byte[]> TCS { get; } = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private static readonly Queue<PendingModelQuery> _pendingModelQueries = new Queue<PendingModelQuery>();
        private static DateTime _lastQueryTime = DateTime.Now;

        // Expression evaluation requests
        private static readonly List<EvaluateExpressionRequest> _evaluateExpressionRequests = new List<EvaluateExpressionRequest>();

        // Heightmap requests
        private static readonly AsyncLock _heightmapLock = new AsyncLock();
        private static TaskCompletionSource<Heightmap> _getHeightmapRequest;
        private static bool _isHeightmapRequested;
        private static TaskCompletionSource<object> _setHeightmapRequest;
        private static Heightmap _heightmapToSet;

        // Firmware updates
        private static readonly AsyncLock _firmwareUpdateLock = new AsyncLock();
        private static Stream _iapStream, _firmwareStream;
        private static TaskCompletionSource<object> _firmwareUpdateRequest;

        // Firmware halt/restart requests
        private static readonly AsyncLock _firmwareActionLock = new AsyncLock();
        private static TaskCompletionSource<object> _firmwareHaltRequest;
        private static TaskCompletionSource<object> _firmwareResetRequest;

        // Miscellaneous requests
        private static readonly Queue<Tuple<int, string>> _extruderFilamentUpdates = new Queue<Tuple<int, string>>();
        private static readonly AsyncLock _printStopppedReasonLock = new AsyncLock();
        private static PrintStoppedReason? _printStoppedReason;
        private static volatile bool _printStarted, _assignFilaments;
        private static readonly Queue<Tuple<MessageTypeFlags, string>> _messagesToSend = new Queue<Tuple<MessageTypeFlags, string>>();

        // Partial incoming message (if any)
        private static string _partialGenericMessage;

        /// <summary>
        /// Initialize the SPI interface but do not connect yet
        /// </summary>
        public static void Init()
        {
            Program.CancellationToken.Register(() => _ = Invalidate(null));
        }

        /// <summary>
        /// Print diagnostics of this class
        /// </summary>
        /// <param name="builder">String builder</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Diagnostics(StringBuilder builder)
        {
            await _channels.Diagnostics(builder);
            builder.AppendLine($"Code buffer space: {_bufferSpace}");
        }

        /// <summary>
        /// Request a specific update of the object model
        /// </summary>
        /// <param name="key">Key to request</param>
        /// <param name="flags">Object model flags</param>
        /// <returns>Deserialized JSON document</returns>
        public static Task<byte[]> RequestObjectModel(string key, string flags)
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            PendingModelQuery query = new PendingModelQuery { Key = key, Flags = flags };
            lock (_pendingModelQueries)
            {
                _pendingModelQueries.Enqueue(query);
            }
            return query.TCS.Task;
        }

        /// <summary>
        /// Evaluate an arbitrary expression
        /// </summary>
        /// <param name="channel">Where to evaluate the expression</param>
        /// <param name="expression">Expression to evaluate</param>
        /// <returns>Result of the evaluated expression</returns>
        /// <exception cref="CodeParserException">Failed to evaluate expression</exception>
        /// <exception cref="InvalidOperationException">Incompatible firmware version or not connected over SPI</exception>
        public static Task<object> EvaluateExpression(CodeChannel channel, string expression)
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }
            if (DataTransfer.ProtocolVersion == 1)
            {
                throw new InvalidOperationException("Incompatible firmware version");
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

                EvaluateExpressionRequest newItem = new EvaluateExpressionRequest(channel, expression);
                _evaluateExpressionRequests.Add(newItem);
                _logger.Debug("Evaluating {0} on channel {1}", expression, channel);
                return newItem.Task;
            }
        }

        /// <summary>
        /// Retrieve the current heightmap from the firmware
        /// </summary>
        /// <returns>Heightmap in use</returns>
        /// <exception cref="OperationCanceledException">Operation could not finish</exception>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        public static Task<Heightmap> GetHeightmap()
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            TaskCompletionSource<Heightmap> tcs;
            using (_heightmapLock.Lock(Program.CancellationToken))
            {
                if (_getHeightmapRequest == null)
                {
                    tcs = new TaskCompletionSource<Heightmap>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _getHeightmapRequest = tcs;
                    _isHeightmapRequested = false;
                }
                else
                {
                    tcs = _getHeightmapRequest;
                }
            }
            return tcs.Task;
        }

        /// <summary>
        /// Set the current heightmap to use
        /// </summary>
        /// <param name="map">Heightmap to set</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation could not finish</exception>
        /// <exception cref="InvalidOperationException">Heightmap is already being set or not connected over SPI</exception>
        public static Task SetHeightmap(Heightmap map)
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            TaskCompletionSource<object> tcs;
            using (_heightmapLock.Lock(Program.CancellationToken))
            {
                if (_setHeightmapRequest != null)
                {
                    throw new InvalidOperationException("Heightmap is already being set");
                }

                tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                _heightmapToSet = map;
                _setHeightmapRequest = tcs;
            }
            return tcs.Task;
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
        public static bool IsWaitingForAcknowledgement(CodeChannel channel) => _channels[channel].IsWaitingForAcknowledgement;

        /// <summary>
        /// Enqueue a G/M/T-code synchronously and obtain a task that completes when the code has finished
        /// </summary>
        /// <param name="code">Code to execute</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        public static async Task ProcessCode(Code code)
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            if (code.Type == CodeType.MCode && code.MajorNumber == 703)
            {
                // It is safe to assume that the tools and extruders have been configured at this point.
                // Assign the filaments next so that M703 works as intended
                _assignFilaments = true;
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
        public static Task<bool> Flush(CodeChannel channel)
        {
            if (Settings.NoSpi)
            {
                return Task.FromResult(true);
            }

            using (_channels[channel].Lock())
            {
                return _channels[channel].Flush();
            }
        }

        /// <summary>
        /// Wait for all pending codes on the same stack level as the given code to finish
        /// </summary>
        /// <param name="code">Code waiting for the flush</param>
        /// <returns>Whether the codes have been flushed successfully</returns>
        public static Task<bool> Flush(Code code)
        {
            if (Settings.NoSpi)
            {
                return Task.FromResult(true);
            }

            using (_channels[code.Channel].Lock())
            {
                return _channels[code.Channel].Flush(code);
            }
        }

        /// <summary>
        /// Request an immediate emergency stop
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task EmergencyStop()
        {
            if (!Settings.NoSpi)
            {
                Task onFirmwareHalted;
                using (await _firmwareActionLock.LockAsync(Program.CancellationToken))
                {
                    if (_firmwareHaltRequest == null)
                    {
                        _firmwareHaltRequest = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    onFirmwareHalted = _firmwareHaltRequest.Task;
                }
                await onFirmwareHalted;
            }
        }

        /// <summary>
        /// Perform a firmware reset and wait for it to finish
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Reset()
        {
            if (!Settings.NoSpi)
            {
                Task onFirmwareReset;
                using (await _firmwareActionLock.LockAsync(Program.CancellationToken))
                {
                    if (_firmwareResetRequest == null)
                    {
                        _firmwareResetRequest = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    onFirmwareReset = _firmwareResetRequest.Task;
                }
                await onFirmwareReset;
            }
        }

        /// <summary>
        /// Notify the firmware that a file print has started
        /// </summary>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        public static void SetPrintStarted()
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            _printStarted = true;
        }

        /// <summary>
        /// Notify the firmware that the file print has been stopped
        /// </summary>
        /// <param name="stopReason">Reason why the print has stopped</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        public static async Task SetPrintStopped(PrintStoppedReason stopReason)
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            using (await _printStopppedReasonLock.LockAsync(Program.CancellationToken))
            {
                _printStoppedReason = stopReason;
            }

            using (await _channels[CodeChannel.File].LockAsync())
            {
                await _channels[CodeChannel.File].AbortFile(true, true);
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
            public async ValueTask DisposeAsync() => await UnlockAll(_channel);
        }

        /// <summary>
        /// Lock the move module and wait for standstill
        /// </summary>
        /// <param name="channel">Code channel acquiring the lock</param>
        /// <returns>Whether the resource could be locked</returns>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        /// <exception cref="OperationCanceledException">Failed to get movement lock</exception>
        public static Task<IAsyncDisposable> LockMovementAndWaitForStandstill(CodeChannel channel)
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            using (_channels[channel].Lock())
            {
                return _channels[channel]
                    .LockMovementAndWaitForStandstill()
                    .ContinueWith(async task =>
                    {
                        if (await task)
                        {
                            return (IAsyncDisposable)(new MovementLock(channel));
                        }
                        throw new OperationCanceledException();
                    }, TaskContinuationOptions.RunContinuationsAsynchronously)
                    .Unwrap();
            }
        }

        /// <summary>
        /// Unlock all resources occupied by the given channel
        /// </summary>
        /// <param name="channel">Channel holding the resources</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        private static Task UnlockAll(CodeChannel channel)
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            using (_channels[channel].Lock())
            {
                return _channels[channel].UnlockAll();
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
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            TaskCompletionSource<object> tcs;
            using (await _firmwareUpdateLock.LockAsync(Program.CancellationToken))
            {
                if (_firmwareUpdateRequest != null)
                {
                    throw new InvalidOperationException("Firmware is already being updated");
                }

                tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        private static async Task PerformFirmwareUpdate()
        {
            using (await Model.Provider.AccessReadWriteAsync())
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
                catch
                {
                    await Logger.LogOutput(MessageType.Error, "Failed to flash flash firmware. Please install it manually.");
                    Program.CancelSource.Cancel();
                    return;
                }

                _logger.Info("Verifying checksum");
            }
            while (++numRetries < 3 && !DataTransfer.VerifyFirmwareChecksum(_firmwareStream.Length, crc16));

            if (numRetries == 3)
            {
                // Failed to flash the firmware
                await Logger.LogOutput(MessageType.Error, "Could not flash firmware after 3 attempts. Please install it manually.");
                Program.CancelSource.Cancel();
            }
            else
            {
                // Wait for the IAP binary to restart the controller
                await DataTransfer.WaitForIapReset();
                _logger.Info("Firmware update successful");
            }
        }

        /// <summary>
        /// Assign the filament to an extruder drive
        /// </summary>
        /// <param name="extruder">Extruder drive</param>
        /// <param name="filament">Loaded filament</param>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        public static void AssignFilament(int extruder, string filament)
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            lock (_extruderFilamentUpdates)
            {
                _extruderFilamentUpdates.Enqueue(new Tuple<int, string>(extruder, filament));
            }
        }

        /// <summary>
        /// Send a message to the firmware
        /// </summary>
        /// <param name="flags">Message flags</param>
        /// <param name="message">Message content</param>
        /// <exception cref="InvalidOperationException">Incompatible firmware or not connected over SPI</exception>
        public static void SendMessage(MessageTypeFlags flags, string message)
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }
            if (DataTransfer.ProtocolVersion == 1)
            {
                throw new InvalidOperationException("Incompatible firmware version");
            }
            if (message.Length > Consts.MaxMessageLength)
            {
                throw new ArgumentException($"{nameof(message)} too long");
            }

            lock (_messagesToSend)
            {
                _messagesToSend.Enqueue(new Tuple<MessageTypeFlags, string>(flags, message));
            }
        }

        /// <summary>
        /// Abort all files in RRF on the given channel
        /// </summary>
        /// <param name="channel">Channel where all the files have been aborted</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
        public static async Task AbortAll(CodeChannel channel)
        {
            if (Settings.NoSpi)
            {
                throw new InvalidOperationException("Not connected over SPI");
            }

            if (DataTransfer.ProtocolVersion >= 3)
            {
                using (await _channels[channel].LockAsync())
                {
                    _channels[channel].AllFilesAborted = true;
                }
            }
        }

        /// <summary>
        /// Perform communication with the RepRapFirmware controller over SPI
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Run()
        {
            if (Settings.NoSpi)
            {
                await Task.Delay(-1, Program.CancellationToken);
            }

            do
            {
                bool skipChannels = false;
                using (await _firmwareActionLock.LockAsync(Program.CancellationToken))
                {
                    // Check if an emergency stop has been requested
                    if (_firmwareHaltRequest != null)
                    {
                        await Invalidate("Firmware halted");
                        if (DataTransfer.WriteEmergencyStop())
                        {
                            _logger.Warn("Emergency stop");
                            _firmwareHaltRequest.SetResult(null);
                            _firmwareHaltRequest = null;
                        }
                        skipChannels = true;
                    }

                    // Check if a firmware reset has been requested
                    if (_firmwareResetRequest != null)
                    {
                        await Invalidate("Firmware reset imminent");
                        if (DataTransfer.WriteReset())
                        {
                            _logger.Warn("Resetting controller");
                            DataTransfer.PerformFullTransfer();
                            _firmwareResetRequest.SetResult(null);
                            _firmwareResetRequest = null;

                            if (!Settings.NoTerminateOnReset)
                            {
                                // Wait for the program to terminate and don't perform any extra transfers
                                await Task.Delay(-1, Program.CancellationToken);
                            }
                        }
                        skipChannels = true;
                    }
                }

                // Check if a firmware update is supposed to be performed
                using (await _firmwareUpdateLock.LockAsync(Program.CancellationToken))
                {
                    if (_iapStream != null && _firmwareStream != null)
                    {
                        await Invalidate("Firmware update imminent");

                        try
                        {
                            await PerformFirmwareUpdate();

                            _firmwareUpdateRequest?.SetResult(null);
                            _firmwareUpdateRequest = null;
                        }
                        catch (Exception e)
                        {
                            _firmwareUpdateRequest?.SetException(e);
                            _firmwareUpdateRequest = null;

                            if (e is OperationCanceledException)
                            {
                                _logger.Debug(e, "Firmware update cancelled");
                            }
                            else
                            {
                                throw;
                            }
                        }

                        _iapStream = _firmwareStream = null;
                    }
                }

                // Invalidate data if a controller reset has been performed
                if (DataTransfer.HadReset())
                {
                    await Invalidate("Controller has been reset");
                }

                // Check for changes of the print status.
                // The packet providing file info has be sent first because it includes a time_t value that must reside on a 64-bit boundary!
                if (_printStarted)
                {
                    using (await Model.Provider.AccessReadOnlyAsync())
                    {
                        _printStarted = !DataTransfer.WritePrintStarted(Model.Provider.Get.Job.File);
                    }
                }
                else
                {
                    using (await _printStopppedReasonLock.LockAsync(Program.CancellationToken))
                    {
                        if (_printStoppedReason != null && DataTransfer.WritePrintStopped(_printStoppedReason.Value))
                        {
                            _printStoppedReason = null;
                        }
                    }
                }

                // Deal with heightmap requests
                using (await _heightmapLock.LockAsync(Program.CancellationToken))
                {
                    // Check if the heightmap is supposed to be set
                    if (_setHeightmapRequest != null && DataTransfer.WriteHeightMap(_heightmapToSet))
                    {
                        _setHeightmapRequest.SetResult(null);
                        _setHeightmapRequest = null;
                    }

                    // Check if the heightmap is requested
                    if (_getHeightmapRequest != null && !_isHeightmapRequested)
                    {
                        _isHeightmapRequested = DataTransfer.WriteGetHeightMap();
                    }
                }

                // Process incoming packets
                for (int i = 0; i < DataTransfer.PacketsToRead; i++)
                {
                    PacketHeader? packet;

                    try
                    {
                        packet = DataTransfer.ReadNextPacket();
                        if (packet == null)
                        {
                            _logger.Error("Read invalid packet");
                            break;
                        }
                        await ProcessPacket(packet.Value);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        DataTransfer.DumpMalformedPacket();
                        throw;
                    }
                }
                _bytesReserved = 0;

                // Process pending codes, macro files and requests for resource locks/unlocks as well as flush requests
                bool skipDelay = skipChannels || await _channels.Run();

                // Request object model updates
                if (DataTransfer.ProtocolVersion == 1)
                {
                    if (DateTime.Now - _lastQueryTime > TimeSpan.FromMilliseconds(Settings.ModelUpdateInterval))
                    {
                        using (await Model.Provider.AccessReadOnlyAsync())
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
                            skipDelay = query.QuerySent = true;
                        }
                    }
                }

                // Ask for expressions to be evaluated
                lock (_evaluateExpressionRequests)
                {
                    int numEvaluationsSent = 0;
                    foreach (EvaluateExpressionRequest request in _evaluateExpressionRequests)
                    {
                        if (!request.Written)
                        {
                            if (DataTransfer.WriteEvaluateExpression(request.Channel, request.Expression))
                            {
                                skipDelay = request.Written = true;

                                numEvaluationsSent++;
                                if (numEvaluationsSent >= Consts.MaxEvaluationRequestsPerTransfer)
                                {
                                    break;
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

                // Update filament assignment per extruder drive. This must happen when config.g has finished or M701 is requested
                if (!Macro.RunningConfig || _assignFilaments)
                {
                    lock (_extruderFilamentUpdates)
                    {
                        while (_extruderFilamentUpdates.TryPeek(out Tuple<int, string> filamentMapping))
                        {
                            if (DataTransfer.WriteAssignFilament(filamentMapping.Item1, filamentMapping.Item2))
                            {
                                _extruderFilamentUpdates.Dequeue();
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                    _assignFilaments = false;
                }

                // Do another full SPI transfe
                DataTransfer.PerformFullTransfer();

                // Wait a moment unless instructions are being sent rapidly to RRF
                if (!skipDelay)
                {
                    await Task.Delay(Settings.SpiPollDelay, Program.CancellationToken);
                }
            }
            while (true);
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
        private static Task ProcessPacket(PacketHeader packet)
        {
            Request request = (Request)packet.Request;

            if (Settings.UpdateOnly && request != Request.ObjectModel)
            {
                // Don't process any requests except for object model responses if only the firmware is supposed to be updated
                return Task.CompletedTask;
            }

            switch (request)
            {
                case Request.ResendPacket:
                    DataTransfer.ResendPacket(packet, out Communication.LinuxRequests.Request linuxRequest);
                    _logger.Warn("Resending packet type #{0} (request {1})", packet.Id, linuxRequest);
                    break;
                case Request.ObjectModel:
                    HandleObjectModel();
                    break;
                case Request.CodeBufferUpdate:
                    HandleCodeBufferUpdate();
                    break;
                case Request.Message:
                    return HandleMessage();
                case Request.ExecuteMacro:
                    return HandleMacroRequest();
                case Request.AbortFile:
                    return HandleAbortFileRequest();
                case Request.PrintPaused:
                    return HandlePrintPaused();
                case Request.HeightMap:
                    return HandleHeightMap();
                case Request.Locked:
                    return HandleResourceLocked();
                case Request.FileChunk:
                    return HandleFileChunkRequest();
                case Request.EvaluationResult:
                    HandleEvaluationResult();
                    break;
                case Request.DoCode:
                    return HandleDoCode();
                case Request.WaitForAcknowledgement:
                    return HandleWaitForAcknowledgement();
                case Request.MacroFileClosed:
                    return HandleMacroFileClosed();
                case Request.MessageAcknowledged:
                    return HandleMessageAcknowledgement();
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Process an object model response
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static void HandleObjectModel()
        {
            _logger.Trace("Received object model");
            if (DataTransfer.ProtocolVersion == 1)
            {
                DataTransfer.ReadLegacyConfigResponse(out ReadOnlySpan<byte> json);
                _ = Model.Updater.ProcessLegacyConfigResponse(json.ToArray());
            }
            else
            {
                DataTransfer.ReadObjectModel(out ReadOnlySpan<byte> json);
                lock (_pendingModelQueries)
                {
                    if (_pendingModelQueries.TryDequeue(out PendingModelQuery query))
                    {
                        query.TCS.SetResult(json.ToArray());
                    }
                    else
                    {
                        _logger.Error("Failed to find query for object model response");
                    }
                }
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
        /// <returns>Asynchronous task</returns>
        private static async Task HandleMessage()
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
                        LogLevel level = flags.HasFlag(MessageTypeFlags.LogWarn) ? LogLevel.Warn
                                            : flags.HasFlag(MessageTypeFlags.LogInfo) ? LogLevel.Info
                                                : LogLevel.Off;
                        await Logger.Log(level, type, _partialLogMessage);
                    }
                    _partialLogMessage = null;
                }
            }

            // Deal with generic replies
            if ((flags & MessageTypeFlags.GenericMessage) == MessageTypeFlags.GenericMessage)
            {
                await OutputGenericMessage(flags, reply);
                return;
            }

            // Check if this is a code reply
            bool replyHandled = false;
            if (!replyHandled && flags.HasFlag(MessageTypeFlags.BinaryCodeReplyFlag))
            {
                replyHandled = await _channels.HandleReply(flags, reply);
            }

            if (!replyHandled)
            {
                // Must be a left-over error message...
                await OutputGenericMessage(flags, reply);
            }
        }

        /// <summary>
        /// Output a generic message
        /// </summary>
        /// <param name="flags">Message flags</param>
        /// <param name="reply">Message content</param>
        /// <returns>Asynchronous task</returns>
        private static async Task OutputGenericMessage(MessageTypeFlags flags, string reply)
        {
            _partialGenericMessage += reply;
            if (!flags.HasFlag(MessageTypeFlags.PushFlag))
            {
                if (!string.IsNullOrWhiteSpace(_partialGenericMessage))
                {
                    MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                                        : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                                            : MessageType.Success;
                    await Model.Provider.Output(type, _partialGenericMessage.TrimEnd());
                }
                _partialGenericMessage = null;
            }
        }

        /// <summary>
        /// Handle a macro request
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task HandleMacroRequest()
        {
            DataTransfer.ReadMacroRequest(out CodeChannel channel, out bool fromCode, out string filename);
            _logger.Trace("Received macro request for file {0} on channel {1}", filename, channel);

            using (await _channels[channel].LockAsync())
            {
                await _channels[channel].DoMacroFile(filename, fromCode);
            }
        }

        /// <summary>
        /// Handle a file abort request
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task HandleAbortFileRequest()
        {
            DataTransfer.ReadAbortFile(out CodeChannel channel, out bool abortAll);
            _logger.Info("Received file abort request on channel {0} for {1}", channel, abortAll ? "all files" : "the last file");

            using (await _channels[channel].LockAsync())
            {
                await _channels[channel].AbortFile(abortAll, false);
            }
        }

        /// <summary>
        /// Deal with paused print events
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task HandlePrintPaused()
        {
            DataTransfer.ReadPrintPaused(out uint filePosition, out PrintPausedReason pauseReason);
            _logger.Trace("Received print pause notification for file position {0}, reason {1}", filePosition, pauseReason);

            // Update the object model
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.State.Status = MachineStatus.Paused;
            }

            // Pause the print
            using (await FileExecution.Job.LockAsync())
            {
                // Do NOT supply a file position if this is a pause request initiated from G-code because that would lead to an endless loop
                bool filePositionValid = (filePosition != Consts.NoFilePosition) && (pauseReason != PrintPausedReason.GCode) && (pauseReason != PrintPausedReason.FilamentChange);
                FileExecution.Job.Pause(filePositionValid ? (long?)filePosition : null, pauseReason);
            }

            // Resolve pending and buffered codes on the file channel
            using (await _channels[CodeChannel.File].LockAsync())
            {
                _channels[CodeChannel.File].InvalidateRegular();
            }
        }

        /// <summary>
        /// Process a received height map
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task HandleHeightMap()
        {
            DataTransfer.ReadHeightMap(out Heightmap map);
            _logger.Trace("Received heightmap");

            using (await _heightmapLock.LockAsync(Program.CancellationToken))
            {
                _getHeightmapRequest?.SetResult(map);
                _getHeightmapRequest = null;
            }
        }

        /// <summary>
        /// Deal with the confirmation that a resource has been locked
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task HandleResourceLocked()
        {
            DataTransfer.ReadCodeChannel(out CodeChannel channel);
            _logger.Trace("Received resource locked notification for channel {0}", channel);

            using (await _channels[channel].LockAsync())
            {
                _channels[channel].ResourceLocked();
            }
        }

        /// <summary>
        /// Process a request for a chunk of a given file
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task HandleFileChunkRequest()
        {
            DataTransfer.ReadFileChunkRequest(out string filename, out uint offset, out uint maxLength);
            _logger.Trace("Received file chunk request for {0}, offset {1}, maxLength {2}", filename, offset, maxLength);

            try
            {
                string filePath = await FilePath.ToPhysicalAsync(filename, filename.EndsWith(".bin") ? FileDirectory.Firmware : FileDirectory.System);
                using FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read)
                {
                    Position = offset
                };
                byte[] buffer = new byte[maxLength];
                int bytesRead = await fs.ReadAsync(buffer, 0, (int)maxLength);

                DataTransfer.WriteFileChunk(buffer.AsSpan(0, bytesRead), fs.Length);
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
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Handle a firmware request to perform a G/M/T-code in DSF
        /// </summary>
        private static async Task HandleDoCode()
        {
            DataTransfer.ReadDoCode(out CodeChannel channel, out string code);
            _logger.Trace("Received firmware code request on channel {0} => {1}", channel, code);

            using (await _channels[channel].LockAsync())
            {
                _channels[channel].DoFirmwareCode(code);
            }
        }

        /// <summary>
        /// Handle a firmware request to wait for a message to be acknowledged
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task HandleWaitForAcknowledgement()
        {
            DataTransfer.ReadCodeChannel(out CodeChannel channel);
            _logger.Trace("Received wait for message acknowledgement on channel {0}", channel);

            using (await _channels[channel].LockAsync())
            {
                _channels[channel].WaitForAcknowledgement();
            }
        }

        /// <summary>
        /// Handle a firmwre request that is sent when RRF has internally closed a macro file
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task HandleMacroFileClosed()
        {
            DataTransfer.ReadCodeChannel(out CodeChannel channel);
            _logger.Trace("Received file closal on channel {0}", channel);

            using (await _channels[channel].LockAsync())
            {
                await _channels[channel].MacroFileClosed();
            }
        }

        /// <summary>
        /// Handle a firmwre request that is sent when RRF has succesfully acknowledged a blocking message
        /// </summary>
        /// <returns>Asynchronous task</returns>
        private static async Task HandleMessageAcknowledgement()
        {
            DataTransfer.ReadCodeChannel(out CodeChannel channel);
            _logger.Trace("Received message acknowledgement on channel {0}", channel);

            using (await _channels[channel].LockAsync())
            {
                await _channels[channel].MessageAcknowledged();
            }
        }

        /// <summary>
        /// Invalidate every resource due to a critical event
        /// </summary>
        /// <param name="message">Reason why everything is being invalidated</param>
        /// <returns>Asynchronous task</returns>
        private static async Task Invalidate(string message)
        {
            // Cancel the file being printed
            bool outputMessage;
            using (await FileExecution.Job.LockAsync())
            {
                outputMessage = FileExecution.Job.IsProcessing;
                await FileExecution.Job.Abort();
            }

            // Resolve pending macros, unbuffered (system) codes and flush requests
            foreach (Channel.Processor channel in _channels)
            {
                using (await channel.LockAsync())
                {
                    outputMessage |= await channel.Invalidate();
                }
            }
            _bytesReserved = _bufferSpace = 0;

            // Resolve pending object model requests
            lock (_pendingModelQueries)
            {
                foreach (PendingModelQuery query in _pendingModelQueries)
                {
                    query.TCS.SetCanceled();
                }
                _pendingModelQueries.Clear();
            }

            // Resolve pending expression evaluation requests
            lock (_evaluateExpressionRequests)
            {
                foreach (EvaluateExpressionRequest request in _evaluateExpressionRequests)
                {
                    request.SetCanceled();
                }
                _evaluateExpressionRequests.Clear();
            }

            // Resolve pending heightmap requests
            using (await _heightmapLock.LockAsync(Program.CancellationToken))
            {
                if (_getHeightmapRequest != null)
                {
                    _getHeightmapRequest.SetCanceled();
                    _getHeightmapRequest = null;
                    outputMessage = true;
                }

                if (_setHeightmapRequest != null)
                {
                    _setHeightmapRequest.SetCanceled();
                    _setHeightmapRequest = null;
                    outputMessage = true;
                }
            }

            // Clear messages to send to the firmware
            lock (_messagesToSend)
            {
                _messagesToSend.Clear();
            }

            // Clear filament assign requests
            lock (_extruderFilamentUpdates)
            {
                while (_extruderFilamentUpdates.TryDequeue(out _)) { }
            }

            // Notify the updater task
            Model.Updater.ConnectionLost();

            // Keep this event in the log...
            if (!string.IsNullOrWhiteSpace(message))
            {
                if (outputMessage)
                {
                    await Logger.LogOutput(MessageType.Warning, message);
                }
                else
                {
                    await Logger.Log(MessageType.Warning, message);
                }
            }
        }
    }
}
