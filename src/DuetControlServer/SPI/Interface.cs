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
        // Information about the code channels
        private static readonly ChannelStore _channels = new ChannelStore();
        private static int _bytesReserved = 0, _bufferSpace = 0;

        // Object model query
        private static byte _moduleToQuery = 5;
        private static DateTime _lastConfigQuery = DateTime.Now;
        private static DateTime _lastQueryTime = DateTime.Now;

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

        // Special requests
        private static bool _emergencyStopRequested, _resetRequested, _printStarted;
        private static PrintStoppedReason? _printStoppedReason;
        private static readonly Queue<Tuple<int, string>> _extruderFilamentUpdates = new Queue<Tuple<int, string>>();

        // Partial messages (if any)
        private static string _partialGenericMessage;

        /// <summary>
        /// Initialize the SPI interface but do not connect yet
        /// </summary>
        public static void Init() => DataTransfer.Init();

        /// <summary>
        /// Print diagnostics of this class
        /// </summary>
        /// <param name="builder">String builder</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Diagnostics(StringBuilder builder)
        {
            foreach (ChannelInformation channel in _channels)
            {
                using (await channel.LockAsync())
                {
                    channel.Diagnostics(builder);
                }
            }
            builder.AppendLine($"Code buffer space: {_bufferSpace}");
        }

        /// <summary>
        /// Retrieve the current heightmap from the firmware
        /// </summary>
        /// <returns>Heightmap in use</returns>
        /// <exception cref="TaskCanceledException">Operation could not finish</exception>
        public static Task<Heightmap> GetHeightmap()
        {
            using (_heightmapLock.Lock())
            {
                if (_getHeightmapRequest == null)
                {
                    _isHeightmapRequested = false;
                    _getHeightmapRequest = new TaskCompletionSource<Heightmap>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                return _getHeightmapRequest.Task;
            }
        }

        /// <summary>
        /// Set the current heightmap to use
        /// </summary>
        /// <param name="map">Heightmap to set</param>
        /// <returns>Asynchronous task</returns>
        /// <exception cref="TaskCanceledException">Operation could not finish</exception>
        /// <exception cref="InvalidOperationException">Heightmap is already being set</exception>
        public static Task SetHeightmap(Heightmap map)
        {
            using (_heightmapLock.Lock())
            {
                if (_setHeightmapRequest != null)
                {
                    throw new InvalidProgramException("Heightmap is already being set");
                }

                _heightmapToSet = map;
                _setHeightmapRequest = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _setHeightmapRequest.Task;
            }
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
        /// <returns>Whether the codes have been flushed successfully</returns>
        public static Task<bool> Flush(CodeChannel channel)
        {
            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (_channels[channel].Lock())
            {
                _channels[channel].PendingFlushRequests.Enqueue(tcs);
            }
            return tcs.Task;
        }

        /// <summary>
        /// Request an immediate emergency stop
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task RequestEmergencyStop()
        {
            _emergencyStopRequested = true;
            await Invalidate("Firmware halted");
        }

        /// <summary>
        /// Request a firmware reset
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task RequestReset()
        {
            _resetRequested = true;
            await Invalidate("Firmware reset imminent");
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
        public static Task<bool> LockMovementAndWaitForStandstill(CodeChannel channel)
        {
            QueuedLockRequest request = new QueuedLockRequest(true);
            using (_channels[channel].Lock())
            {
                _channels[channel].PendingLockRequests.Enqueue(request);
            }
            return request.Task;
        }

        /// <summary>
        /// Unlock all resources occupied by the given channel
        /// </summary>
        /// <param name="channel">Channel holding the resources</param>
        /// <returns>Asynchronous task</returns>
        public static Task UnlockAll(CodeChannel channel)
        {
            QueuedLockRequest request = new QueuedLockRequest(false);
            using (_channels[channel].Lock())
            {
                _channels[channel].PendingLockRequests.Enqueue(request);
            }
            return request.Task;
        }

        /// <summary>
        /// Perform an update of the main firmware via IAP
        /// </summary>
        /// <param name="iapStream">IAP binary</param>
        /// <param name="firmwareStream">Firmware binary</param>
        /// <exception cref="InvalidOperationException">Firmware is already being updated</exception>
        public static Task UpdateFirmware(Stream iapStream, Stream firmwareStream)
        {
            using (_firmwareUpdateLock.Lock())
            {
                if (_firmwareUpdateRequest != null)
                {
                    throw new InvalidOperationException("Firmware is already being updated");
                }

                _iapStream = iapStream;
                _firmwareStream = firmwareStream;
                _firmwareUpdateRequest = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _firmwareUpdateRequest.Task;
            }
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
            }
            while (dataSent);
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
            }
            while (++numRetries < 3 && !DataTransfer.VerifyFirmwareChecksum(_firmwareStream.Length, crc16));

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

            using (_firmwareUpdateLock.Lock())
            {
                _firmwareUpdateRequest.SetResult(null);
                _firmwareUpdateRequest = null;
            }
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
                    await Invalidate("Firmware update imminent");
                    await PerformFirmwareUpdate();
                    if (Program.UpdateOnly)
                    {
                        // Stop after installing the firmware update
                        break;
                    }
                }

                // Invalidate data if a controller reset has been performed
                if (DataTransfer.HadReset())
                {
                    _emergencyStopRequested = _resetRequested = false;
                    await Invalidate("Controller has been reset");
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
                using (await _heightmapLock.LockAsync())
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
                    foreach (ChannelInformation channel in _channels)
                    {
                        using (channel.Lock())
                        {
                            if (!channel.IsBusy)
                            {
                                if (channel.ProcessRequests())
                                {
                                    // Something could be processed
                                    dataProcessed = true;
                                }
                                else
                                {
                                    // Cannot do any more on this channel
                                    channel.IsBusy = true;
                                }
                            }
                        }
                    }
                }
                while (dataProcessed);

                // Request object model updates
                if (DateTime.Now - _lastQueryTime > TimeSpan.FromMilliseconds(Settings.ModelUpdateInterval))
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
                _channels.ResetBusyChannels();

                // Wait a moment
                if (IsIdle)
                {
                    await Task.Delay(Settings.SpiPollDelay, Program.CancelSource.Token);
                }
            }
            while (!Program.CancelSource.IsCancellationRequested);
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
                foreach (ChannelInformation channel in _channels)
                {
                    using (channel.Lock())
                    {
                        if (!channel.IsIdle)
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

            if (Program.UpdateOnly && request != Communication.FirmwareRequests.Request.ObjectModel)
            {
                // Don't process any requests except for object model responses if only the firmware is supposed to be updated
                return Task.CompletedTask;
            }

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

                case Communication.FirmwareRequests.Request.RequestFileChunk:
                    return HandleFileChunkRequest();
            }
            return Task.CompletedTask;
        }

        private static async Task HandleObjectModel()
        {
            DataTransfer.ReadObjectModel(out byte module, out string json);

            // Check which module to query next
            using (await Model.Provider.AccessReadOnlyAsync())
            {
                switch (module)
                {
                    // Advanced status response
                    case 2:
                        if (DateTime.Now - _lastConfigQuery > TimeSpan.FromMilliseconds(Settings.ConfigUpdateInterval))
                        {
                            _moduleToQuery = 5;
                        }
                        else if (Model.Provider.Get.State.Status == MachineStatus.Processing)
                        {
                            _moduleToQuery = 3;
                        }
                        break;

                    // Print response
                    case 3:
                        _moduleToQuery = 2;
                        break;

                    // Config response
                    case 5:
                        _moduleToQuery = 2;
                        _lastConfigQuery = DateTime.Now;
                        break;
                }
            }

            // Merge the data into our own object model
            await Model.Updater.MergeData(module, json);
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
                foreach (ChannelInformation channel in _channels)
                {
                    MessageTypeFlags channelFlag = (MessageTypeFlags)(1 << (int)channel.Channel);
                    if (flags.HasFlag(channelFlag))
                    {
                        using (channel.Lock())
                        {
                            replyHandled = channel.HandleReply(flags, reply);
                        }
                        break;
                    }
                }
            }

            if (!replyHandled)
            {
                // Must be a left-over error message...
                OutputGenericMessage(flags, reply);
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

            if (abortAll && channel == CodeChannel.File)
            {
                await Print.Cancel();
            }

            _channels[channel].InvalidateBuffer(!abortAll);
            while (_channels[channel].NestedMacros.TryPop(out MacroFile macroFile))
            {
                macroFile.StartCode?.HandleReply(new CodeResult());
                macroFile.Abort();
                macroFile.Dispose();
                if (!abortAll)
                {
                    break;
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

        private static async Task HandleFileChunkRequest()
        {
            DataTransfer.ReadFileChunkRequest(out string filename, out uint offset, out uint maxLength);
            Console.WriteLine($"[trace] Received file chunk request for {filename}, offset {offset}, maxLength {maxLength}");
            try
            {
                string filePath = await FilePath.ToPhysicalAsync(filename, "sys");
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    fs.Position = offset;

                    byte[] buffer = /*stackalloc*/ new byte[maxLength];
                    int bytesRead = await fs.ReadAsync(buffer, 0, (int)maxLength);
                    DataTransfer.WriteFileChunk(buffer.AsSpan(0, bytesRead), fs.Length);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[err] Failed to send requested file chunk of {filename}: {e.Message}");
                DataTransfer.WriteFileChunk(null, 0);
            }
        }

        /// <summary>
        /// Invalidate every resource due to a critical event
        /// </summary>
        /// <param name="message">Reason why everything is being invalidated</param>
        /// <returns>Asynchronous task</returns>
        private static async Task Invalidate(string message)
        {
            bool outputMessage = Print.IsPrinting;

            // Cancel the file being printed
            await Print.Cancel();

            // Resolve pending macros, unbuffered (system) codes and flush requests
            foreach (ChannelInformation channel in _channels)
            {
                using (await channel.LockAsync())
                {
                    outputMessage |= channel.Invalidate();
                }
            }
            _bytesReserved = _bufferSpace = 0;

            // Resolve pending heightmap requests
            using (await _heightmapLock.LockAsync())
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

            // Keep this event in the log...
            if (outputMessage)
            {
                await Utility.Logger.LogOutput(MessageType.Warning, message);
            }
            else
            {
                await Utility.Logger.Log(MessageType.Warning, message);
            }
        }
    }
}
