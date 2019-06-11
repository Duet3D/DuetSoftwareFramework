using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
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
        private static readonly CodeChannel[] CodeChannels = (CodeChannel[])Enum.GetValues(typeof(CodeChannel));

        // Requests for each code channel
        private static readonly Dictionary<CodeChannel, AsyncLock> _channelLock = new Dictionary<CodeChannel, AsyncLock>();
        private static readonly Dictionary<CodeChannel, Queue<QueuedCode>> _pendingCodes = new Dictionary<CodeChannel, Queue<QueuedCode>>();
        private static readonly Dictionary<CodeChannel, Stack<QueuedCode>> _pendingSystemCodes = new Dictionary<CodeChannel, Stack<QueuedCode>>();
        private static readonly Dictionary<CodeChannel, Stack<MacroFile>> _pendingMacros = new Dictionary<CodeChannel, Stack<MacroFile>>();
        private static readonly Dictionary<CodeChannel, Queue<QueuedLockRequest>> _pendingLockRequests = new Dictionary<CodeChannel, Queue<QueuedLockRequest>>();
        private static readonly Dictionary<CodeChannel, Queue<TaskCompletionSource<object>>> _flushRequests = new Dictionary<CodeChannel, Queue<TaskCompletionSource<object>>>();

        // Code channels that are currently blocked because of an executing G/M/T-code
        private static int _busyChannels = 0;

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

            // Set up the code channel dictionaries
            foreach (CodeChannel channel in CodeChannels)
            {
                _channelLock.Add(channel, new AsyncLock());
                _pendingCodes.Add(channel, new Queue<QueuedCode>());
                _pendingSystemCodes.Add(channel, new Stack<QueuedCode>());
                _pendingMacros.Add(channel, new Stack<MacroFile>());
                _pendingLockRequests.Add(channel, new Queue<QueuedLockRequest>());
                _flushRequests.Add(channel, new Queue<TaskCompletionSource<object>>());
            }

            // Request buffer states immediately
            DataTransfer.WriteGetState();
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
                using (await _channelLock[channel].LockAsync())
                {
                    if (_pendingSystemCodes[channel].TryPeek(out QueuedCode code))
                    {
                        builder.AppendLine($"{channel} is {(code.IsExecuting ? "executing" : "waiting for")} system code {code.Code}");
                        continue;
                    }

                    if (_pendingMacros[channel].TryPeek(out MacroFile macro))
                    {
                        builder.AppendLine($"{channel} is doing system macro {macro.FileName}");
                        continue;
                    }

                    if (_pendingCodes[channel].TryPeek(out code))
                    {
                        builder.AppendLine($"{channel} is {(code.IsExecuting ? "executing" : "waiting for")} code {code.Code}");
                        continue;
                    }
                }
            }

            builder.AppendLine($"Busy channels: {_busyChannels}");
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
        /// Execute a G/M/T-code asynchronously
        /// </summary>
        /// <param name="code">Code to execute</param>
        /// <returns>Asynchronous task</returns>
        public static async Task<CodeResult> ProcessCode(Code code)
        {
            QueuedCode item = null;
            using (await _channelLock[code.Channel].LockAsync())
            {
                if (code.IsFromSystemMacro)
                {
                    // System codes from the internal execution are already enqueued at the time this is called
                    foreach (QueuedCode queuedCode in _pendingSystemCodes[code.Channel])
                    {
                        if (queuedCode.Code == code)
                        {
                            item = queuedCode;
                            break;
                        }
                    }

                    // Users may want to enqueue custom system codes as well. Use with care, only one can be executed simultaneously!
                    if (item == null)
                    {
                        if (_pendingSystemCodes[code.Channel].Count != 0)
                        {
                            throw new ArgumentException("Another system code is already being executed on this channel");
                        }
                        item = new QueuedCode(code);
                        _pendingSystemCodes[code.Channel].Push(item);
                    }
                }
                else
                {
                    // Enqueue this code for regular execution
                    item = new QueuedCode(code);
                    _pendingCodes[code.Channel].Enqueue(item);
                }
            }
            return await item.Task;
        }

        /// <summary>
        /// Wait for all pending codes to finish
        /// </summary>
        /// <param name="channel">Code channel to wait for</param>
        /// <returns>Asynchronous task</returns>
        public static async Task Flush(CodeChannel channel)
        {
            TaskCompletionSource<object> source = new TaskCompletionSource<object>();
            using (await _channelLock[channel].LockAsync())
            {
                _flushRequests[channel].Enqueue(source);
            }
            await source.Task;
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
            QueuedLockRequest request = new QueuedLockRequest(true, channel);
            using (await _channelLock[channel].LockAsync())
            {
                _pendingLockRequests[channel].Enqueue(request);
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
            QueuedLockRequest request = new QueuedLockRequest(false, channel);
            using (await _channelLock[channel].LockAsync())
            {
                _pendingLockRequests[channel].Enqueue(request);
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

                // Process pending codes, macro files and requests for resource locks/unlocks as well as flush requests
                foreach (CodeChannel channel in CodeChannels)
                {
                    using (_channelLock[channel].Lock())
                    {
                        if ((_busyChannels & (1 << (int)channel)) == 0 && RunSystemCode(channel) && RunCode(channel))
                        {
                            // No code is being executed or resolved on this channel. Deal with lock/unlock requests
                            if (_pendingLockRequests[channel].TryPeek(out QueuedLockRequest lockRequest) && !lockRequest.IsLockRequested)
                            {
                                if (lockRequest.IsLockRequest)
                                {
                                    lockRequest.IsLockRequested = DataTransfer.WriteLockMovementAndWaitForStandstill(lockRequest.Channel);
                                }
                                else if (DataTransfer.WriteUnlock(lockRequest.Channel))
                                {
                                    _pendingLockRequests[channel].Dequeue();
                                }
                            }

                            // If there is no pending lock/unlock request, this code channel is idle
                            if (lockRequest == null)
                            {
                                if (_flushRequests[channel].TryDequeue(out TaskCompletionSource<object> source))
                                {
                                    source.SetResult(null);
                                }
                            }
                        }
                    }
                }

                // Request the state of the GCodeBuffers and the object model after the codes have been processed
                DataTransfer.WriteGetState();
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

        // Returns true if no system code is being executed
        private static bool RunSystemCode(CodeChannel channel)
        {
            if (_pendingSystemCodes[channel].TryPeek(out QueuedCode item) && !item.DoingNestedMacro)
            {
                // Check if the current system code can be finished or started
                if (item.IsExecuting)
                {
                    // The reply for this code may not have been received yet.
                    // Expect one to come even if it is empty - better than missing output
                    if (item.CanFinish)
                    {
                        _pendingSystemCodes[channel].Pop();
                        item.SetFinished();
                    }
                }
                else if (item.Code.IsPostProcessed)
                {
                    // Send it to the firmware when it has been internally processed. This may fail if the code content is too big
                    try
                    {
                        if (DataTransfer.WriteCode(item.Code))
                        {
                            item.IsExecuting = true;
                            _busyChannels |= (1 << (int)channel);
                        }
                    }
                    catch (Exception e)
                    {
                        _pendingSystemCodes[channel].Pop();
                        item.SetException(e);
                    }
                }
            }
            else if (_pendingMacros[channel].TryPeek(out MacroFile macro))
            {
                // Read the next code from the requested macro file
                Code code;
                do
                {
                    code = macro.ReadCode();
                    if (code?.Type == CodeType.Comment)
                    {
                        // Execute comments like codes so interceptors can parse them
                        code.Execute().Wait();
                        continue;
                    }
                } while (code != null && code.Type == CodeType.Comment);

                if (code != null)
                {
                    // Keep track of it
                    item = new QueuedCode(code);
                    _pendingSystemCodes[code.Channel].Push(item);

                    // Execute it in the background
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            CodeResult result = await code.Execute();
                            if (!macro.IsAborted)
                            {
                                await Model.Provider.Output(result);
                            }
                        }
                        catch (AggregateException ae)
                        {
                            _pendingCodes[channel].Dequeue();
                            item.SetException(ae.InnerException);
                        }

                        if (!code.IsPostProcessed)
                        {
                            item.IsExecuting = true;
                            item.HandleReply(null);
                        }
                    });
                }
                else
                {
                    // Macro file is complete
                    if (DataTransfer.WriteMacroCompleted(channel, false))
                    {
                        if (item != null)
                        {
                            item.DoingNestedMacro = false;
                        }

                        bool aborted = _pendingMacros[channel].Pop().IsAborted;
                        Console.WriteLine($"[info] {(aborted ? "Aborted" : "Finished")} execution of macro file '{macro.FileName}'");
                        // Do not return true here and let the firmware process this event first
                    }
                }
            }
            else
            {
                // Nothing to do
                return true;
            }
            return false;
        }

        // Returns true if no code is being executed
        private static bool RunCode(CodeChannel channel)
        {
            // Check if there is any pending code
            if (_pendingCodes[channel].TryPeek(out QueuedCode item))
            {
                if (item.IsExecuting)
                {
                    // The reply for this code may not have been received yet.
                    // Expect one to come even if it is empty - better than missing output
                    if (item.CanFinish)
                    {
                        _pendingCodes[channel].Dequeue();
                        item.SetFinished();
                    }
                }
                else
                {
                    // Send it to the firmware. This may fail if the code content is too big
                    try
                    {
                        if (DataTransfer.WriteCode(item.Code))
                        {
                            item.IsExecuting = true;
                            _busyChannels |= (1 << (int)channel);
                        }
                    }
                    catch (Exception e)
                    {
                        _pendingCodes[channel].Dequeue();
                        item.SetException(e);
                    }
                }
                return false;
            }
            return true;
        }

        private static bool IsIdle
        {
            get
            {
                if (_busyChannels != 0)
                {
                    return false;
                }

                foreach (CodeChannel channel in Enum.GetValues(typeof(CodeChannel)))
                {
                    using (_channelLock[channel].Lock())
                    {
                        if (_pendingCodes[channel].Count != 0 ||
                            _pendingSystemCodes[channel].Count != 0 ||
                            _pendingMacros[channel].Count != 0 ||
                            _flushRequests[channel].Count != 0)
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

                case Communication.FirmwareRequests.Request.ReportState:
                    DataTransfer.ReadState(out _busyChannels);
                    break;

                case Communication.FirmwareRequests.Request.ObjectModel:
                    return HandleObjectModel();

                case Communication.FirmwareRequests.Request.CodeReply:
                    return HandleCodeReply();

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

        private static async Task HandleCodeReply()
        {
            DataTransfer.ReadCodeReply(out Communication.MessageTypeFlags flags, out string reply);
            if (!flags.HasFlag(Communication.MessageTypeFlags.PushFlag))
            {
                reply = reply.TrimEnd();
            }

            // TODO implement logging here
            // TODO check for "File %s will print in %" PRIu32 "h %" PRIu32 "m plus heating time" and modify simulation time

            // Deal with generic replies. Keep this check in sync with RepRapFirmware
            if (flags.HasFlag(Communication.MessageTypeFlags.UsbMessage) && flags.HasFlag(Communication.MessageTypeFlags.AuxMessage) &&
                flags.HasFlag(Communication.MessageTypeFlags.HttpMessage) && flags.HasFlag(Communication.MessageTypeFlags.TelnetMessage))
            {
                OutputGenericMessage(flags, reply);
                return;
            }

            // Check if this is a targeted message. If yes, send it to the code being executed
            bool replyHandled = false;
            if (flags.HasFlag(Communication.MessageTypeFlags.BinaryCodeReplyFlag))
            {
                replyHandled = true;
                foreach (CodeChannel channel in CodeChannels)
                {
                    using (await _channelLock[channel].LockAsync())
                    {
                        Communication.MessageTypeFlags channelFlag = (Communication.MessageTypeFlags)(1 << (int)channel);
                        if (flags.HasFlag(channelFlag))
                        {
                            // Is this reply for a system code or a regular code?
                            if ((_pendingSystemCodes[channel].TryPeek(out QueuedCode code) || _pendingCodes[channel].TryPeek(out code)) &&
                                code.IsExecuting)
                            {
                                code.HandleReply(flags, reply);
                            }
                            else
                            {
                                replyHandled = false;
                            }
                        }
                    }
                }
            }

            // If at least one channel destination could not be reached, treat it as a generic message
            if (!replyHandled)
            {
                OutputGenericMessage(flags, reply);
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
            DataTransfer.ReadMacroRequest(out CodeChannel channel, out bool reportMissing, out string filename);

            // Locate the macro file
            string path = await FilePath.ToPhysical(filename, "sys");
            if (filename == MacroFile.ConfigFile && !File.Exists(path))
            {
                path = await FilePath.ToPhysical(MacroFile.ConfigFileFallback, "sys");
                if (File.Exists(path))
                {
                    // Use config.b.bak if config.g cannot be found
                    Console.WriteLine($"[warn] Using fallback file {MacroFile.ConfigFileFallback} because {MacroFile.ConfigFile} could not be found");
                }
                else
                {
                    await Model.Provider.Output(MessageType.Error, $"Could not find macro files {MacroFile.ConfigFile} and {MacroFile.ConfigFileFallback}");
                    DataTransfer.WriteMacroCompleted(channel, true);
                    return;
                }
            }

            // Start it
            if (File.Exists(path))
            {
                MacroFile macro = new MacroFile(path, channel, true, 0);
                using (await _channelLock[channel].LockAsync())
                {
                    // Enqueue the macro file
                    _pendingMacros[channel].Push(macro);

                    // Deal with nested macros
                    if (_pendingSystemCodes[channel].TryPeek(out QueuedCode item))
                    {
                        item.DoingNestedMacro = true;
                    }
                }
            }
            else
            {
                if (reportMissing)
                {
                    await Model.Provider.Output(MessageType.Error, $"Could not find macro file {filename}");
                }
                else
                {
                    Console.WriteLine($"[info] Optional macro file '{filename}' not found");
                }

                _busyChannels |= (1 << (int)channel);
                DataTransfer.WriteMacroCompleted(channel, reportMissing);
            }
        }

        private static async Task HandleAbortFileRequest()
        {
            DataTransfer.ReadAbortFile(out CodeChannel channel);
            Console.WriteLine($"[info] Received file abort request for channel {channel}");

            MacroFile.AbortAllFiles(channel);
            if (channel == CodeChannel.File)
            {
                await Print.Cancel();
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
            DataTransfer.ReadPrintPaused(out uint filePosition, out Communication.PrintPausedReason pauseReason);
            Console.WriteLine($"[info] Print paused at file position {filePosition}. Reason: {pauseReason}");

            // Make the print stop and rewind back to the given file position
            await Print.OnPause(filePosition);

            // Update the object model
            using (await Model.Provider.AccessReadWriteAsync())
            {
                Model.Provider.Get.State.Status = MachineStatus.Paused;
            }

            // Resolve pending codes on the file channel
            using (await _channelLock[CodeChannel.File].LockAsync())
            {
                while (_pendingCodes[CodeChannel.File].TryDequeue(out QueuedCode code))
                {
                    code.HandleReply(Communication.MessageTypeFlags.FileMessage, $"Print has been paused at byte {filePosition}");
                    code.SetFinished();
                }
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
            using (await _channelLock[channel].LockAsync())
            {
                if (_pendingLockRequests[channel].TryDequeue(out QueuedLockRequest item))
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

            // Resolve pending macros, (system) codes and flush requests
            foreach (CodeChannel channel in CodeChannels)
            {
                MacroFile.AbortAllFiles(channel);

                using (await _channelLock[channel].LockAsync())
                {
                    Queue<QueuedCode> validCodes = new Queue<QueuedCode>();

                    while (_pendingSystemCodes[channel].TryPop(out QueuedCode item))
                    {
                        item.HandleReply(Communication.MessageTypeFlags.ErrorMessageFlag, codeErrorMessage);
                        item.SetFinished();
                    }

                    while (_pendingCodes[channel].TryDequeue(out QueuedCode item))
                    {
                        if (item.Code.Type == CodeType.MCode &&
                            (item.Code.MajorNumber == 112 || (item.Code.MajorNumber == 999 && item.Code.Parameter('P') == null)))
                        {
                            // Don't cancel codes from this channel if the emergency stop / reset came from here
                            validCodes.Enqueue(item);
                        }
                        else
                        {
                            // But do resolve every other code
                            item.HandleReply(Communication.MessageTypeFlags.ErrorMessageFlag, codeErrorMessage);
                            item.SetFinished();
                        }
                    }

                    while (validCodes.TryDequeue(out QueuedCode item))
                    {
                        _pendingCodes[channel].Enqueue(item);
                    }

                    while (_pendingLockRequests[channel].TryDequeue(out QueuedLockRequest item))
                    {
                        item.Resolve(false);
                    }

                    while (_flushRequests[channel].TryDequeue(out TaskCompletionSource<object> source))
                    {
                        source.SetException(new OperationCanceledException(codeErrorMessage));
                    }
                }
            }

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
