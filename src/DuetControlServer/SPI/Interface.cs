using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Machine;
using DuetAPI.Utility;
using DuetControlServer.FileExecution;
using DuetControlServer.SPI.Communication;
using Nito.AsyncEx;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// This class accesses RepRapFirmware via SPI and deals with general communication
    /// </summary>
    public static class Interface
    {
        private static readonly CodeChannel[] CodeChannels = (CodeChannel[])Enum.GetValues(typeof(CodeChannel));

        // Information about the code channels
        private static readonly ChannelStore _channels = new ChannelStore();
        private static readonly List<CodeChannel> _busyChannels = new List<CodeChannel>();
        private static int _bytesReserved = 0, _bufferSpace = 0;

        // Object model query
        private static byte _moduleToQuery = 2;
        private static DateTime _lastQueryTime = DateTime.Now;

        // Heightmap requests
        private static readonly AsyncLock _heightmapLock = new AsyncLock();
        private static TaskCompletionSource<Heightmap> _getHeightmapRequest;
        private static bool _heightmapRequested;
        private static TaskCompletionSource<object> _setHeightmapRequest;
        private static Heightmap _heightmapToSet;

        // Special requests
        private static bool _emergencyStopRequested, _resetRequested, _printStarted;
        private static PrintStoppedReason? _printStoppedReason;
        private static Stream _iapStream, _firmwareStream;
        private static readonly Queue<Tuple<int, string>> _extruderFilamentUpdates = new Queue<Tuple<int, string>>();

        // Partial messages (if any)
        private static string _partialGenericMessage;

        /// <summary>
        /// Initialize the SPI interface but do not connect yet
        /// </summary>
        public static void Init()
        {
            // Initialize SPI and GPIO pin
            DataTransfer.Initialize();
        }

        /// <summary>
        /// Print diagnostics of this class
        /// </summary>
        /// <param name="builder">String builder</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Diagnostics(StringBuilder builder)
        {
            foreach (CodeChannel channel in CodeChannels)
            {
                using (await _channels[channel].LockAsync())
                {
                    _channels[channel].Diagnostics(builder);
                }
            }
            builder.AppendLine($"Code buffer space: {_bufferSpace}");
        }

        /// <summary>
        /// Retrieve the current heightmap from the firmware
        /// </summary>
        /// <returns>Heightmap in use</returns>
        /// <exception cref="OperationCanceledException">Operation could not finish</exception>
        public static async Task<Heightmap> GetHeightmap()
        {
            using (await _heightmapLock.LockAsync())
            {
                if (_getHeightmapRequest == null)
                {
                    _getHeightmapRequest = new TaskCompletionSource<Heightmap>();
                }
            }
            return await _getHeightmapRequest.Task;
        }

        /// <summary>
        /// Set the current heightmap to use
        /// </summary>
        /// <param name="map">Heightmap to set</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="OperationCanceledException">Operation could not finish</exception>
        public static async Task SetHeightmap(Heightmap map)
        {
            Task task;
            using (await _heightmapLock.LockAsync())
            {
                if (_setHeightmapRequest == null)
                {
                    _setHeightmapRequest = new TaskCompletionSource<object>();
                }
                _heightmapToSet = map;
                task = _setHeightmapRequest.Task;
            }
            await task;
        }

        /// <summary>
        /// Enqueue a G/M/T-code synchronously and obtain a task that completes when the code has finished
        /// </summary>
        /// <param name="code">Code to execute</param>
        /// <returns>Asynchronous task</returns>
        public static Task<CodeResult> ProcessCode(Code code)
        {
            QueuedCode item = null;
            using (_channels[code.Channel].Lock())
            {
                if (code.Flags.HasFlag(CodeFlags.IsPrioritized))
                {
                    // This code is supposed to override every other queued code
                    item = new QueuedCode(code);
                    _channels[code.Channel].PriorityCodes.Enqueue(item);
                }
                else if (code.Flags.HasFlag(CodeFlags.IsFromMacro))
                {
                    // Macro codes are already enqueued at the time this is called
                    foreach (QueuedCode queuedCode in _channels[code.Channel].NestedMacroCodes)
                    {
                        if (queuedCode.Code == code)
                        {
                            item = queuedCode;
                            break;
                        }
                    }

                    // Users may want to enqueue custom codes as well when dealing with macro files
                    if (item == null)
                    {
                        item = new QueuedCode(code);
                        _channels[code.Channel].NestedMacroCodes.Enqueue(item);
                    }
                }
                else
                {
                    // Enqueue this code for regular execution
                    item = new QueuedCode(code);
                    _channels[code.Channel].PendingCodes.Enqueue(item);
                }
            }
            item.IsReadyToSend = true;
            return item.Task;
        }

        /// <summary>
        /// Wait for all pending codes to finish
        /// </summary>
        /// <param name="channel">Code channel to wait for</param>
        /// <returns>Asynchronous task</returns>
        public static Task Flush(CodeChannel channel)
        {
            TaskCompletionSource<object> source = new TaskCompletionSource<object>();
            using (_channels[channel].Lock())
            {
                _channels[channel].PendingFlushRequests.Enqueue(source);
            }
            return source.Task;
        }

        /// <summary>
        /// Request an immediate emergency stop
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task RequestEmergencyStop()
        {
            _emergencyStopRequested = true;
            await InvalidateData("Firmware halted");
        }

        /// <summary>
        /// Request a firmware reset
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task RequestReset()
        {
            _resetRequested = true;
            await InvalidateData("Firmware reset imminent");
        }

        /// <summary>
        /// Notify the firmware that a file print has started
        /// </summary>
        public static void SetPrintStarted()
        {
            _printStarted = true;
        }

        /// <summary>
        /// Notify the firmware that the file print has been stopped
        /// </summary>
        /// <param name="stopReason">Reason why the print has stopped</param>
        public static void SetPrintStopped(PrintStoppedReason stopReason)
        {
            _printStoppedReason = stopReason;
        }

        /// <summary>
        /// Lock the move module and wait for standstill
        /// </summary>
        /// <param name="channel">Code channel acquiring the lock</param>
        /// <returns>Whether the resource could be locked</returns>
        public static async Task<bool> LockMovementAndWaitForStandstill(CodeChannel channel)
        {
            QueuedLockRequest request = new QueuedLockRequest(true);
            using (await _channels[channel].LockAsync())
            {
                _channels[channel].PendingLockRequests.Enqueue(request);
            }
            return await request.Task;
        }

        /// <summary>
        /// Unlock all resources occupied by the given channel
        /// </summary>
        /// <param name="channel">Channel holding the resources</param>
        /// <returns>Asynchronous task</returns>
        public static async Task UnlockAll(CodeChannel channel)
        {
            QueuedLockRequest request = new QueuedLockRequest(false);
            using (await _channels[channel].LockAsync())
            {
                _channels[channel].PendingLockRequests.Enqueue(request);
            }
            await request.Task;
        }

        /// <summary>
        /// Perform an update of the main firmware via IAP
        /// </summary>
        /// <param name="iapStream">IAP binary</param>
        /// <param name="firmwareStream">Firmware binary</param>
        public static void UpdateFirmware(Stream iapStream, Stream firmwareStream)
        {
            _iapStream = iapStream;
            _firmwareStream = firmwareStream;
        }

        private static async Task PerformFirmwareUpdate()
        {
            // Notify clients that we are now installing a firmware update
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.State.Status = MachineStatus.Updating;
            }

            // Get the CRC16 checksum of the firmware binary
            byte[] firmwareBlob = /*stackalloc*/ new byte[_firmwareStream.Length];
            _firmwareStream.Read(firmwareBlob);
            ushort crc16 = CRC16.Calculate(firmwareBlob);

            // Send the IAP binary to the firmware
            Console.Write("[info] Flashing IAP binary");
            bool dataSent;
            do
            {
                dataSent = DataTransfer.WriteIapSegment(_iapStream);
                DataTransfer.PerformFullTransfer();
                Console.Write('.');
            } while (dataSent);
            Console.WriteLine();

            _iapStream.Close();
            _iapStream = null;

            // Start the IAP binary
            DataTransfer.StartIap();

            // Send the firmware binary to the IAP program
            int numRetries = 0;
            do
            {
                if (numRetries != 0)
                {
                    Console.WriteLine("Error");
                }

                Console.Write("[info] Flashing RepRapFirmware");
                _firmwareStream.Seek(0, SeekOrigin.Begin);
                while (DataTransfer.FlashFirmwareSegment(_firmwareStream))
                {
                    Console.Write('.');
                }
                Console.WriteLine();

                Console.Write("[info] Verifying checksum... ");
            } while (++numRetries < 3 && !DataTransfer.VerifyFirmwareChecksum(_firmwareStream.Length, crc16));

            if (numRetries == 3)
            {
                Console.WriteLine("Error");

                // Failed to flash the firmware
                await Utility.Logger.LogOutput(MessageType.Error, "Could not flash the firmware binary after 3 attempts. Please install it manually via bossac.");
                Program.CancelSource.Cancel();
            }
            else
            {
                Console.WriteLine("OK");

                // Wait for the IAP binary to restart the controller
                DataTransfer.WaitForIapReset();
                Console.WriteLine("[info] Firmware update successful!");
            }

            _firmwareStream.Close();
            _firmwareStream = null;
        }

        /// <summary>
        /// Assign the filament to an extruder drive
        /// </summary>
        /// <param name="extruder">Extruder drive</param>
        /// <param name="filament">Loaded filament</param>
        public static void AssignFilament(int extruder, string filament)
        {
            lock (_extruderFilamentUpdates)
            {
                _extruderFilamentUpdates.Enqueue(new Tuple<int, string>(extruder, filament));
            }
        }

        /// <summary>
        /// Initialize physical transfer and perform initial data transfer.
        /// This is only called once on initialization
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static bool Connect() => DataTransfer.PerformFullTransfer(false);

        /// <summary>
        /// Perform communication with the RepRapFirmware controller
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Run()
        {
            do
            {
                // Check if an emergency stop has been requested
                if (_emergencyStopRequested && DataTransfer.WriteEmergencyStop())
                {
                    _emergencyStopRequested = false;
                    Console.WriteLine("[info] Emergency stop");
                    DataTransfer.PerformFullTransfer();
                }

                // Check if a firmware reset has been requested
                if (_resetRequested && DataTransfer.WriteReset())
                {
                    _resetRequested = false;
                    Console.WriteLine("[info] Resetting controller");
                    DataTransfer.PerformFullTransfer();
                }

                // Check if a firmware update is supposed to be performed
                if (_iapStream != null && _firmwareStream != null)
                {
                    await InvalidateData("Firmware update imminent");
                    await PerformFirmwareUpdate();
                }

                // Invalidate data if a controller reset has been performed
                if (DataTransfer.HadReset())
                {
                    _emergencyStopRequested = _resetRequested = false;
                    Console.WriteLine("[info] Controller has been reset");
                    await InvalidateData("Controller reset");
                }

                // Check for changes of the print status.
                // The packet providing file info has be sent first because it includes a time_t value that must reside on a 64-bit boundary!
                if (_printStarted)
                {
                    using (Model.Provider.AccessReadOnly())
                    {
                        _printStarted = !DataTransfer.WritePrintStarted(Model.Provider.Get.Job.File);
                    }
                }
                else if (_printStoppedReason.HasValue && DataTransfer.WritePrintStopped(_printStoppedReason.Value))
                {
                    _printStoppedReason = null;
                }

                // Deal with heightmap requests
                using (_heightmapLock.Lock())
                {
                    // Check if the heightmap is supposed to be set
                    if (_heightmapToSet != null && DataTransfer.WriteHeightMap(_heightmapToSet))
                    {
                        _heightmapToSet = null;
                        _setHeightmapRequest.SetResult(null);
                        _setHeightmapRequest = null;
                    }

                    // Check if the heightmap is requested
                    if (_getHeightmapRequest != null && !_heightmapRequested)
                    {
                        _heightmapRequested = DataTransfer.WriteGetHeightMap();
                    }
                }

                // Process incoming packets
                for (int i = 0; i < DataTransfer.PacketsToRead; i++)
                {
                    PacketHeader? packet;

                    try
                    {
                        packet = DataTransfer.ReadPacket();
                        if (!packet.HasValue)
                        {
                            Console.WriteLine("[err] Read invalid packet");
                            break;
                        }
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        DataTransfer.DumpMalformedPacket();
                        throw;
                    }

                    await ProcessPacket(packet.Value);
                }
                _bytesReserved = 0;

                // Process pending codes, macro files and requests for resource locks/unlocks as well as flush requests
                bool dataProcessed;
                do
                {
                    dataProcessed = false;
                    foreach (CodeChannel channel in CodeChannels)
                    {
                        if (!_busyChannels.Contains(channel))
                        {
                            using (_channels[channel].Lock())
                            {
                                if (_channels[channel].ProcessRequests())
                                {
                                    // Something could be processed
                                    dataProcessed = true;
                                }
                                else
                                {
                                    // Cannot do any more on this channel
                                    _busyChannels.Add(channel);
                                }
                            }
                        }
                    }
                } while (dataProcessed);

                // Request object model updates
                if (Model.Updater.IsReady && DateTime.Now - _lastQueryTime > TimeSpan.FromMilliseconds(Settings.ModelUpdateInterval))
                {
                    DataTransfer.WriteGetObjectModel(_moduleToQuery);
                    _lastQueryTime = DateTime.Now;
                }

                // Update filament assignment per extruder drive
                lock (_extruderFilamentUpdates)
                {
                    if (_extruderFilamentUpdates.TryPeek(out Tuple<int, string> filamentMapping) &&
                        DataTransfer.WriteAssignFilament(filamentMapping.Item1, filamentMapping.Item2))
                    {
                        _extruderFilamentUpdates.Dequeue();
                    }
                }

                // Do another full SPI transfer
                DataTransfer.PerformFullTransfer();
                _busyChannels.Clear();

                // Wait a moment
                if (IsIdle)
                {
                    await Task.Delay(Settings.SpiPollDelay, Program.CancelSource.Token);
                }
            } while (!Program.CancelSource.IsCancellationRequested);
        }

        /// <summary>
        /// Send a queued code to the firmware
        /// </summary>
        /// <param name="queuedCode">Code to send</param>
        /// <param name="codeLength">Length of the binary code in bytes</param>
        /// <returns>Whether the code could be processed</returns>
        /// <remarks>The corresponding Channel is locked when this is called</remarks>
        public static bool BufferCode(QueuedCode queuedCode, out int codeLength)
        {
            codeLength = Consts.BufferedCodeHeaderSize + DataTransfer.GetCodeSize(queuedCode.Code);
            if (_bufferSpace > codeLength && _channels[queuedCode.Code.Channel].BytesBuffered + codeLength <= Settings.MaxBufferSpacePerChannel &&
                DataTransfer.WriteCode(queuedCode.Code))
            {
                _bytesReserved += codeLength;
                _bufferSpace -= codeLength;
                queuedCode.BinarySize = codeLength;
                Console.WriteLine($"[info] Sent {queuedCode.Code}, remaining space {Settings.MaxBufferSpacePerChannel - _channels[queuedCode.Code.Channel].BytesBuffered - codeLength} ({_bufferSpace} total), needed {codeLength}");
                return true;
            }
            return false;
        }

        private static bool IsIdle
        {
            get
            {
                foreach (CodeChannel channel in CodeChannels)
                {
                    using (_channels[channel].Lock())
                    {
                        if (!_channels[channel].IsIdle)
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
        }

        private static Task ProcessPacket(PacketHeader packet)
        {
            Communication.FirmwareRequests.Request request = (Communication.FirmwareRequests.Request)packet.Request;
            switch (request)
            {
                case Communication.FirmwareRequests.Request.ResendPacket:
                    DataTransfer.ResendPacket(packet);
                    break;

                case Communication.FirmwareRequests.Request.ObjectModel:
                    return HandleObjectModel();

                case Communication.FirmwareRequests.Request.CodeBufferUpdate:
                    HandleCodeBufferUpdate();
                    break;

                case Communication.FirmwareRequests.Request.CodeReply:
                    HandleCodeReply();
                    break;

                case Communication.FirmwareRequests.Request.ExecuteMacro:
                    return HandleMacroRequest();

                case Communication.FirmwareRequests.Request.AbortFile:
                    return HandleAbortFileRequest();

                case Communication.FirmwareRequests.Request.StackEvent:
                    return HandleStackEvent();

                case Communication.FirmwareRequests.Request.PrintPaused:
                    return HandlePrintPaused();

                case Communication.FirmwareRequests.Request.HeightMap:
                    return HandleHeightMap();

                case Communication.FirmwareRequests.Request.Locked:
                    return HandleResourceLocked();
            }
            return Task.CompletedTask;
        }

        private static async Task HandleObjectModel()
        {
            DataTransfer.ReadObjectModel(out byte module, out string json);

            // Are we printing? Need to know for the next status update
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                if (module == 2 && Model.Provider.Get.State.Status == MachineStatus.Processing)
                {
                    _moduleToQuery = 3;
                }
                else
                {
                    _moduleToQuery = 2;
                }
            }

            // Merge the data into our own object model
            Model.Updater.MergeData(module, json);
        }

        private static void HandleCodeBufferUpdate()
        {
            DataTransfer.ReadCodeBufferUpdate(out ushort bufferSpace);
            _bufferSpace = bufferSpace - _bytesReserved;
            //Console.WriteLine($"[info] Buffer space available: {_bufferSpace}");
        }

        private static void HandleCodeReply()
        {
            DataTransfer.ReadCodeReply(out MessageTypeFlags flags, out string reply);

            // TODO check for "File %s will print in %" PRIu32 "h %" PRIu32 "m plus heating time" and modify simulation time

            // Deal with generic replies
            if ((flags & MessageTypeFlags.GenericMessage) == MessageTypeFlags.GenericMessage ||
                flags == MessageTypeFlags.LogMessage || flags == (MessageTypeFlags.LogMessage | MessageTypeFlags.PushFlag))
            {
                OutputGenericMessage(flags, reply);
                return;
            }

            // Check if this is a targeted message. If yes, send it to the corresponding code being executed
            bool replyHandled = false;
            if (!replyHandled && flags.HasFlag(MessageTypeFlags.BinaryCodeReplyFlag))
            {
                foreach (CodeChannel channel in CodeChannels)
                {
                    MessageTypeFlags channelFlag = (MessageTypeFlags)(1 << (int)channel);
                    if (flags.HasFlag(channelFlag))
                    {
                        using (_channels[channel].Lock())
                        {
                            replyHandled = _channels[channel].HandleReply(flags, reply);
                        }
                        break;
                    }
                }
            }

            if (!replyHandled && !flags.HasFlag(MessageTypeFlags.CodeQueueMessage))
            {
                // If the message could not be processed, output a warning. Should never happen except for queued codes
                Console.WriteLine($"[warn] Received out-of-sync code reply ({flags}: {reply.TrimEnd()})");
            }
        }

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
                    Utility.Logger.LogOutput(type, _partialGenericMessage.TrimEnd());
                }
                _partialGenericMessage = null;
            }
        }

        private static async Task HandleMacroRequest()
        {
            DataTransfer.ReadMacroRequest(out CodeChannel channel, out bool reportMissing, out bool fromCode, out string filename);
            using (await _channels[channel].LockAsync())
            {
                await _channels[channel].HandleMacroRequest(filename, reportMissing, fromCode);
            }
        }

        private static async Task HandleAbortFileRequest()
        {
            DataTransfer.ReadAbortFile(out CodeChannel channel, out bool abortAll);
            Console.WriteLine($"[info] Received file abort request on channel {channel} for {(abortAll ? "all files" : "the last file")}");

            if (abortAll)
            {
                if (channel == CodeChannel.File)
                {
                    await Print.Cancel();
                }
                MacroFile.AbortAllFiles(channel);
                _channels[channel].InvalidateBuffer(false);
            }
            else
            {
                MacroFile.AbortLastFile(channel);
                _channels[channel].InvalidateBuffer(true);
                if (_channels[channel].NestedMacros.TryPop(out MacroFile macroFile))
                {
                    Console.WriteLine($"[info] Aborted last macro file '{macroFile.FileName}'");
                    macroFile.Dispose();
                }
            }
        }

        private static async Task HandleStackEvent()
        {
            DataTransfer.ReadStackEvent(out CodeChannel channel, out byte stackDepth, out Communication.FirmwareRequests.StackFlags stackFlags, out float feedrate);

            using (await Model.Provider.AccessReadWriteAsync())
            {
                Channel item = Model.Provider.Get.Channels[channel];
                item.StackDepth = stackDepth;
                item.RelativeExtrusion = stackFlags.HasFlag(Communication.FirmwareRequests.StackFlags.DrivesRelative);
                item.VolumetricExtrusion = stackFlags.HasFlag(Communication.FirmwareRequests.StackFlags.VolumetricExtrusion);
                item.RelativePositioning = stackFlags.HasFlag(Communication.FirmwareRequests.StackFlags.AxesRelative);
                item.UsingInches = stackFlags.HasFlag(Communication.FirmwareRequests.StackFlags.UsingInches);
                item.Feedrate = feedrate;
            }
        }

        private static async Task HandlePrintPaused()
        {
            DataTransfer.ReadPrintPaused(out uint filePosition, out PrintPausedReason pauseReason);
            if (filePosition == Consts.NoFilePosition)
            {
                // We get an invalid file position from RRF if the print is paused during a macro file.
                // In this case, RRF has no way to determine the file position so we have to care of that.
                filePosition = (uint)(Print.LastFilePosition ?? Print.Length);
            }
            Console.WriteLine($"[info] Print paused at file position {filePosition}. Reason: {pauseReason}");

            // Make the print stop and rewind back to the given file position
            await Print.OnPause(filePosition);

            // Update the object model
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.State.Status = MachineStatus.Paused;
            }

            // Resolve pending and buffered codes on the file channel
            using (await _channels[CodeChannel.File].LockAsync())
            {
                while (_channels[CodeChannel.File].PendingCodes.TryDequeue(out QueuedCode code))
                {
                    code.SetFinished();
                }
                _channels[CodeChannel.File].InvalidateBuffer(false);
            }
        }

        private static async Task HandleHeightMap()
        {
            DataTransfer.ReadHeightMap(out Heightmap map);
            using (await _heightmapLock.LockAsync())
            {
                _heightmapRequested = false;
                if (_getHeightmapRequest == null)
                {
                    Console.WriteLine("[err] Got heightmap response although it was not requested");
                }
                else
                {
                    _getHeightmapRequest.SetResult(map);
                    _getHeightmapRequest = null;
                }
            }
        }

        private static async Task HandleResourceLocked()
        {
            DataTransfer.ReadResourceLocked(out CodeChannel channel);
            using (await _channels[channel].LockAsync())
            {
                if (_channels[channel].PendingLockRequests.TryDequeue(out QueuedLockRequest item))
                {
                    item.Resolve(true);
                }
            }
        }

        /// <summary>
        /// Invalidate every resource due to a critical event
        /// </summary>
        /// <param name="codeErrorMessage">Message for cancelled codes</param>
        /// <returns>Asynchronous task</returns>
        private static async Task InvalidateData(string codeErrorMessage)
        {
            // Keep this event in the log...
            await Utility.Logger.Log(MessageType.Warning, codeErrorMessage);

            // Close every open file. This closes the internal macro files as well
            await Print.Cancel();

            // Resolve pending macros, unbuffered (system) codes and flush requests
            foreach (CodeChannel channel in CodeChannels)
            {
                MacroFile.AbortAllFiles(channel);

                using (await _channels[channel].LockAsync())
                {
                    _channels[channel].Invalidate(codeErrorMessage);
                }
            }
            _bytesReserved = _bufferSpace = 0;

            // Resolve pending heightmap requests
            using (await _heightmapLock.LockAsync())
            {
                _getHeightmapRequest?.SetException(new OperationCanceledException(codeErrorMessage));
                _heightmapRequested = false;

                _setHeightmapRequest?.SetException(new OperationCanceledException(codeErrorMessage));
                _heightmapToSet = null;
            }
        }
    }
}
