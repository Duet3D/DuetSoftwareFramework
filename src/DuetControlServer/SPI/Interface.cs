using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Machine;
using DuetAPI.Utility;
using DuetControlServer.FileExecution;
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
        private static readonly ChannelStore Channels = new ChannelStore();
        private static readonly CodeChannel[] CodeChannels = (CodeChannel[])Enum.GetValues(typeof(CodeChannel));
        private static int _bytesReserved = 0, _bufferSpace = 0;

        // Number of the module of the object model being queried (TODO fully implement this)
        private static byte _moduleToQuery = 2;

        // Time when the object model was queried the last time
        private static DateTime _lastQueryTime = DateTime.Now;

        // Heightmap requests
        private static readonly AsyncLock _heightmapLock = new AsyncLock();
        private static TaskCompletionSource<Heightmap> _getHeightmapRequest;
        private static bool _heightmapRequested;
        private static TaskCompletionSource<object> _setHeightmapRequest;
        private static Heightmap _heightmapToSet;

        // Special requests
        private static bool _emergencyStopRequested, _resetRequested, _printStarted;
        private static Communication.PrintStoppedReason? _printStoppedReason;

        // Partial generic message (if any)
        private static string _partialCodeReply;

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
                using (await Channels[channel].LockAsync())
                {
                    Channels[channel].Diagnostics(builder);
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
            using (Channels[code.Channel].Lock())
            {
                if (code.Flags.HasFlag(CodeFlags.IsFromMacro))
                {
                    // Macro codes are already enqueued at the time this is called
                    foreach (QueuedCode queuedCode in Channels[code.Channel].NestedMacroCodes)
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
                        Channels[code.Channel].NestedMacroCodes.Enqueue(item);
                    }
                }
                else
                {
                    // Enqueue this code for regular execution
                    item = new QueuedCode(code);
                    Channels[code.Channel].PendingCodes.Enqueue(item);
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
            using (Channels[channel].Lock())
            {
                Channels[channel].PendingFlushRequests.Enqueue(source);
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
            await InvalidateData("Code has been cancelled due to an emergency stop");
        }

        /// <summary>
        /// Request a firmware reset
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task RequestReset()
        {
            _resetRequested = true;
            await InvalidateData("Code has been cancelled because a firmware reset is imminent");
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
        public static void SetPrintStopped(Communication.PrintStoppedReason stopReason)
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
            using (await Channels[channel].LockAsync())
            {
                Channels[channel].PendingLockRequests.Enqueue(request);
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
            using (await Channels[channel].LockAsync())
            {
                Channels[channel].PendingLockRequests.Enqueue(request);
            }
            await request.Task;
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

                // Invalidate data if a controller reset has been performed
                if (DataTransfer.HadReset())
                {
                    Console.WriteLine("[info] Controller has been reset");
                    InvalidateData("Controller has been reset").Wait();
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
                    Communication.PacketHeader? packet;

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
                List<CodeChannel> blockedChannels = new List<CodeChannel>();
                do
                {
                    dataProcessed = false;
                    foreach (CodeChannel channel in CodeChannels)
                    {
                        if (!blockedChannels.Contains(channel))
                        {
                            using (Channels[channel].Lock())
                            {
                                if (Channels[channel].ProcessRequests())
                                {
                                    // Something could be done on this channel
                                    dataProcessed = true;
                                }
                                else
                                {
                                    // Don't call Process() again for this channel if it returned false before
                                    blockedChannels.Add(channel);
                                }
                            }
                        }
                    }
                } while (dataProcessed);

                // Request object model updates
                if (IsIdle || DateTime.Now - _lastQueryTime > TimeSpan.FromMilliseconds(Settings.MaxUpdateDelay))
                {
                    DataTransfer.WriteGetObjectModel(_moduleToQuery);
                    _lastQueryTime = DateTime.Now;
                }

                // Do another full SPI transfer
                DataTransfer.PerformFullTransfer();

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
        /// <returns>Whether the code could be processed</returns>
        public static bool BufferCode(QueuedCode queuedCode)
        {
            try
            {
                int codeLength = Communication.Consts.BufferedCodeHeaderSize + DataTransfer.GetCodeSize(queuedCode.Code);
                if (_bufferSpace > codeLength && DataTransfer.WriteCode(queuedCode.Code))
                {
                    Console.WriteLine($"[info] Sent {queuedCode.Code}, remaining space {_bufferSpace}, need {codeLength}");
                    _bytesReserved += codeLength;
                    _bufferSpace -= codeLength;
                    Channels[queuedCode.Code.Channel].BufferedCodes.Add(queuedCode);
                    return true;
                }
            }
            catch (Exception e)
            {
                queuedCode.SetException(e);
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
                    using (Channels[channel].Lock())
                    {
                        if (!Channels[channel].IsIdle)
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
        }

        private static Task ProcessPacket(Communication.PacketHeader packet)
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
            DataTransfer.ReadCodeReply(out Communication.MessageTypeFlags flags, out string reply);

            // TODO implement logging here
            // TODO check for "File %s will print in %" PRIu32 "h %" PRIu32 "m plus heating time" and modify simulation time

            // Deal with generic replies. Keep this check in sync with RepRapFirmware
            if (flags.HasFlag(Communication.MessageTypeFlags.UsbMessage) && flags.HasFlag(Communication.MessageTypeFlags.AuxMessage) &&
                flags.HasFlag(Communication.MessageTypeFlags.HttpMessage) && flags.HasFlag(Communication.MessageTypeFlags.TelnetMessage))
            {
                OutputGenericMessage(flags, reply.TrimEnd());
                return;
            }

            // Check if this is a targeted message. If yes, send it to the corresponding code being executed
            bool replyHandled = false;
            if (flags.HasFlag(Communication.MessageTypeFlags.BinaryCodeReplyFlag))
            {
                foreach (CodeChannel channel in CodeChannels)
                {
                    Communication.MessageTypeFlags channelFlag = (Communication.MessageTypeFlags)(1 << (int)channel);
                    if (flags.HasFlag(channelFlag))
                    {
                        using (Channels[channel].Lock())
                        {
                            replyHandled = Channels[channel].HandleReply(flags, reply);
                        }
                        break;
                    }
                }
            }

            if (!replyHandled)
            {
                // If the message could not be processed, output a warning. Should never happen
                Console.WriteLine($"[warn] Received out-of-sync code reply ({flags}: {reply.TrimEnd()})");
            }
        }

        private static void OutputGenericMessage(Communication.MessageTypeFlags flags, string reply)
        {
            if (_partialCodeReply != null)
            {
                reply = _partialCodeReply + reply;
                _partialCodeReply = null;
            }

            if (flags.HasFlag(Communication.MessageTypeFlags.PushFlag))
            {
                _partialCodeReply = reply;
            }
            else if (reply != "")
            {
                MessageType type = flags.HasFlag(Communication.MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                    : flags.HasFlag(Communication.MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                    : MessageType.Success;
                Model.Provider.Output(type, reply);
            }
        }

        private static async Task HandleMacroRequest()
        {
            DataTransfer.ReadMacroRequest(out CodeChannel channel, out bool reportMissing, out bool fromCode, out string filename);
            using (await Channels[channel].LockAsync())
            {
                await Channels[channel].HandleMacroRequest(filename, reportMissing, fromCode);
            }
        }

        private static async Task HandleAbortFileRequest()
        {
            DataTransfer.ReadAbortFile(out CodeChannel channel);
            Console.WriteLine($"[info] Received file abort request for channel {channel}");

            if (channel == CodeChannel.File)
            {
                await Print.Cancel();
            }
            MacroFile.AbortAllFiles(channel);
            Channels[channel].InvalidateBuffer();
        }

        private static async Task HandleStackEvent()
        {
            DataTransfer.ReadStackEvent(out CodeChannel channel, out byte stackDepth, out Communication.FirmwareRequests.StackFlags stackFlags, out float feedrate);

            using (await Model.Provider.AccessReadWriteAsync())
            {
                DuetAPI.Machine.Channel item = Model.Provider.Get.Channels[channel];
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
            DataTransfer.ReadPrintPaused(out uint filePosition, out Communication.PrintPausedReason pauseReason);
            if (filePosition == SPI.Communication.Consts.NoFilePosition)
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
            using (await Channels[CodeChannel.File].LockAsync())
            {
                while (Channels[CodeChannel.File].PendingCodes.TryDequeue(out QueuedCode code))
                {
                    code.SetFinished();
                }
                Channels[CodeChannel.File].InvalidateBuffer();
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
            using (await Channels[channel].LockAsync())
            {
                if (Channels[channel].PendingLockRequests.TryDequeue(out QueuedLockRequest item))
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
        public static async Task InvalidateData(string codeErrorMessage)
        {
            // Close every open file. This closes the internal macro files as well
            await Print.Cancel();

            // Resolve pending macros, unbuffered (system) codes and flush requests
            foreach (CodeChannel channel in CodeChannels)
            {
                MacroFile.AbortAllFiles(channel);

                using (await Channels[channel].LockAsync())
                {
                    Channels[channel].Invalidate(codeErrorMessage);
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
