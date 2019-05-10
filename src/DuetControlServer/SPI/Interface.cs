using System;
using System.Collections.Generic;
using System.IO;
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
        // Requests for each code channel
        // TODO Replace CodeChannel with a pseudo-enum (via consts) and replace the following with simple arrays
        private static readonly Dictionary<CodeChannel, Queue<QueuedCode>> _pendingCodes = new Dictionary<CodeChannel, Queue<QueuedCode>>();
        private static readonly Dictionary<CodeChannel, Stack<QueuedCode>> _pendingSystemCodes = new Dictionary<CodeChannel, Stack<QueuedCode>>();
        private static readonly Dictionary<CodeChannel, Stack<MacroFile>> _pendingMacros = new Dictionary<CodeChannel, Stack<MacroFile>>();
        private static readonly Dictionary<CodeChannel, Queue<QueuedLockRequest>> _pendingLockRequests = new Dictionary<CodeChannel, Queue<QueuedLockRequest>>();

        // Code channels that are currently blocked because of an executing G/M/T-code
        private static int _busyChannels = 0;

        // Number of the module of the object model being queried (TODO fully implement this)
        private static byte _moduleToQuery = 2;

        // Time when the object model was queried the last time
        private static DateTime _lastQueryTime = DateTime.Now;

        // Heightmap requests
        private static AsyncLock _heightmapLock = new AsyncLock();
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
            foreach (CodeChannel channel in Enum.GetValues(typeof(CodeChannel)))
            {
                _pendingCodes.Add(channel, new Queue<QueuedCode>());
                _pendingSystemCodes.Add(channel, new Stack<QueuedCode>());
                _pendingMacros.Add(channel, new Stack<MacroFile>());
                _pendingLockRequests.Add(channel, new Queue<QueuedLockRequest>());
            }

            // Request buffer states immediately
            DataTransfer.WriteGetState();
        }

        /// <summary>
        /// Retrieve the current heightmap from the firmware
        /// </summary>
        /// <returns>Heightmap in use</returns>
        /// <exception cref="OperationCanceledException">Operation could not finish</exception>
        public static async Task<Heightmap> GetHeightmap()
        {
            lock (await _heightmapLock.LockAsync())
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
            lock (await _heightmapLock.LockAsync())
            {
                if (_setHeightmapRequest == null)
                {
                    _setHeightmapRequest = new TaskCompletionSource<object>();
                }
                _heightmapToSet = map;
            }
            await _setHeightmapRequest.Task;
        }

        /// <summary>
        /// Execute a G/M/T-code and wait asynchronously for its completion
        /// </summary>
        /// <param name="code">Code to execute</param>
        /// <returns>Asynchronous task</returns>
        public static Task<CodeResult> ProcessCode(Code code)
        {
            QueuedCode item = new QueuedCode(code, false);
            lock (_pendingCodes[code.Channel])
            {
                _pendingCodes[code.Channel].Enqueue(item);
            }
            return item.Task;
        }

        /// <summary>
        /// Execute a system G/M/T-code and wait asynchronously for its completion.
        /// This is only used for macro files requested from the firmware
        /// </summary>
        /// <param name="code">Code to execute</param>
        /// <returns>Task that completes when the code has finished</returns>
        public static Task<CodeResult> ProcessSystemCode(Code code)
        {
            QueuedCode item = new QueuedCode(code, true);
            lock (_pendingSystemCodes[code.Channel])
            {
                _pendingSystemCodes[code.Channel].Push(item);
            }
            return item.Task;
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
        public static Task<bool> LockMovementAndWaitForStandstill(CodeChannel channel)
        {
            lock (_pendingLockRequests[channel])
            {
                QueuedLockRequest request = new QueuedLockRequest(true, channel);
                _pendingLockRequests[channel].Enqueue(request);
                return request.Task;
            }
        }

        /// <summary>
        /// Unlock all resources occupied by the given channel
        /// </summary>
        /// <param name="channel">Channel holding the resources</param>
        /// <returns>Asynchronous task</returns>
        public static Task UnlockAll(CodeChannel channel)
        {
            lock (_pendingLockRequests[channel])
            {
                QueuedLockRequest request = new QueuedLockRequest(false, channel);
                _pendingLockRequests[channel].Enqueue(request);
                return request.Task;
            }
        }

        /// <summary>
        /// Initialize physical transfer and perform initial data transfer.
        /// This is only called once on initialization
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static Task<bool> Connect() => DataTransfer.PerformFullTransfer();

        private static async Task TransferData()
        {
            bool result;
            do
            {
                // Keep on trying until it succeeds
                result = await DataTransfer.PerformFullTransfer();
            } while (!result);
        }

        /// <summary>
        /// Perform communication with the RepRapFirmware controller
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Run()
        {
            do
            {
                //Console.WriteLine($"- Transfer {DataTransfer.TransferNumber} -");
                
                // Check if an emergency stop has been requested
                if (_emergencyStopRequested && DataTransfer.WriteEmergencyStop())
                {
                    _emergencyStopRequested = false;
                    Console.WriteLine("[info] Emergency stop");
                    await TransferData();
                }

                // Check if a firmware reset has been requested
                if (_resetRequested && DataTransfer.WriteReset())
                {
                    _resetRequested = false;
                    Console.WriteLine("[info] Resetting controller");
                    await TransferData();
                }

                // Invalidate data if a controller reset has been performed
                if (DataTransfer.HadReset())
                {
                    await InvalidateData("Controller has been reset");
                }

                // Check for changes of the print status.
                // The packet providing file info has be sent first because it includes a time_t value that must reside on a 64-bit boundary!
                if (_printStarted)
                {
                    using (await Model.Provider.AccessReadOnly())
                    {
                        _printStarted = !DataTransfer.WritePrintStarted(Model.Provider.Get.Job.File);
                    }
                }
                else if (_printStoppedReason.HasValue && DataTransfer.WritePrintStopped(_printStoppedReason.Value))
                {
                    _printStoppedReason = null;
                }

                using (await _heightmapLock.LockAsync())
                {
                    // Check if the heightmap is supposed to be set
                    if (_heightmapToSet != null)
                    {
                        if (DataTransfer.WriteHeightMap(_heightmapToSet))
                        {
                            _heightmapToSet = null;
                            _setHeightmapRequest.SetResult(null);
                            _setHeightmapRequest = null;
                        }
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
                    Communication.PacketHeader? packet = DataTransfer.ReadPacket();
                    if (!packet.HasValue)
                    {
                        Console.WriteLine("[err] Read invalid packet");
                        break;
                    }

                    try
                    {
                        //Console.WriteLine($"-> Packet #{packet.Value.Id} (request {(Communication.FirmwareRequests.Request)packet.Value.Request}) length {packet.Value.Length}");
                        await ProcessPacket(packet.Value);
                    }
                    catch (ArgumentOutOfRangeException e)
                    {
                        DataTransfer.DumpMalformedPacket();
                        Console.Write("[perr] ");
                        Console.WriteLine(e);
                        break;
                    }
                }

                // Process pending codes, macro files and requests for resource locks/unlocks
                foreach (CodeChannel channel in Enum.GetValues(typeof(CodeChannel)))
                {
                    if ((_busyChannels & (1 << (int)channel)) == 0)
                    {
                        QueuedCode item = null;

                        // Codes may request macro files from RepRapFirmware which contain more codes that override the currently-executing code
                        lock (_pendingSystemCodes[channel])
                        {
                            _pendingSystemCodes[channel].TryPeek(out item);
                        }

                        // If there is no code from a macro file being executed, see if another one can be read
                        if (item == null || item.DoingNestedMacro)
                        {
                            MacroFile macro = null;
                            lock (_pendingMacros[channel])
                            {
                                _pendingMacros[channel].TryPeek(out macro);
                            }

                            if (macro == null)
                            {
                                // If there is no pending code from a requested macro file and no macro file in general, deal with regular codes
                                lock (_pendingCodes[channel])
                                {
                                    _pendingCodes[channel].TryPeek(out item);
                                }
                            }
                            else
                            {
                                // Start executing the next valid G/M/T-code from the requested macro file.
                                // Note that code.Execute() does not wait for the code's completion; this will happen in the background as via _pendingSystemCodes
                                Code code;
                                do
                                {
                                    code = await macro.ReadCode();
                                    if (code == null)
                                    {
                                        break;
                                    }
                                    await code.Execute();
                                } while (code.Type == CodeType.Comment);

                                if (code == null)
                                {
                                    // Macro file is complete
                                    if (DataTransfer.WriteMacroCompleted(channel, false))
                                    {
                                        lock (_pendingSystemCodes[channel])
                                        {
                                            _pendingSystemCodes[channel].TryPeek(out item);
                                        }
                                        if (item != null)
                                        {
                                            item.DoingNestedMacro = false;
                                        }

                                        lock (_pendingMacros[channel])
                                        {
                                            if (!_pendingMacros[channel].Pop().IsAborted)
                                            {
                                                Console.WriteLine($"[info] Finished execution of macro file {macro.FileName}");
                                                continue;
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    // Retrieve the next code. It should have been queued for further processing
                                    lock (_pendingSystemCodes[channel])
                                    {
                                        _pendingSystemCodes[channel].TryPeek(out item);
                                    }
                                }
                            }
                        }

                        // Deal with the next code to execute
                        if (item != null)
                        {
                            // FIXME From a code start to its result 4 transfers are needed:
                            // 1) DCS writes code packet
                            // 2) RRF reads and starts it (during this state the channel is busy under all circumstances, hence we don't get here at that time)
                            // 3) RRF writes code response
                            // 4) DCS reads code response
                            // The latency for the next code can be reduced here by assigning the executing code to an "expecting response" dictionary
                            // This way the effective number of required transfers can be reduced from 4 to 3 by skipping step 3 (in theory)
                            if (item.IsExecuting)
                            {
                                // The reply for this code may not have been received yet. Expect one to come even if it is empty - better than missing output
                                if (item.CanFinish)
                                {
                                    // This code has finished...
                                    Console.WriteLine($"Finished {item.Code}");
                                    if (item.IsRequestedFromFirmware)
                                    {
                                        // ... and it was requested from the firmware
                                        lock (_pendingSystemCodes[channel])
                                        {
                                            _pendingSystemCodes[channel].Pop();
                                        }
                                    }
                                    else
                                    {
                                        // ... and it is a regular code
                                        lock (_pendingCodes[channel])
                                        {
                                            _pendingCodes[channel].Dequeue();
                                        }
                                    }
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
                                        Console.WriteLine($"Executing {item.Code}");
                                        item.IsExecuting = true;
                                        _busyChannels |= (1 << (int)channel);
                                    }
                                }
                                catch (Exception e)
                                {
                                    // This code could not be written...
                                    if (item.IsRequestedFromFirmware)
                                    {
                                        // ... and it was requested from the firmware
                                        lock (_pendingSystemCodes[channel])
                                        {
                                            _pendingSystemCodes[channel].Pop();
                                        }
                                    }
                                    else
                                    {
                                        // ... and it is a regular code
                                        lock (_pendingCodes[channel])
                                        {
                                            _pendingCodes[channel].Dequeue();
                                        }
                                    }
                                    item.SetException(e);
                                }
                            }
                        }
                    }
                    else
                    {
                        // Deal with lock/unlock requests. They are only permitted if no code is being executed
                        lock (_pendingLockRequests[channel])
                        {
                            if (_pendingLockRequests[channel].TryPeek(out QueuedLockRequest item) && !item.IsLockRequested)
                            {
                                if (item.IsLockRequest)
                                {
                                    item.IsLockRequested = DataTransfer.WriteLockMovementAndWaitForStandstill(item.Channel);
                                }
                                else if (DataTransfer.WriteUnlock(item.Channel))
                                {
                                    _pendingLockRequests[channel].Dequeue();
                                }
                            }
                        }
                    }
                }

                // Request the state of the GCodeBuffers and the object model after the codes have been processed
                DataTransfer.WriteGetState();
                if (IsIdle() || DateTime.Now - _lastQueryTime > TimeSpan.FromMilliseconds(Settings.MaxUpdateDelay))
                {
                    DataTransfer.WriteGetObjectModel(_moduleToQuery);
                    _lastQueryTime = DateTime.Now;
                }

                // Do another full SPI transfer
                await TransferData();

                // Wait a moment
                if (IsIdle())
                {
                    await Task.Delay(Settings.SpiPollDelay, Program.CancelSource.Token);
                }
            } while (!Program.CancelSource.IsCancellationRequested);
        }

        private static bool IsIdle()
        {
            foreach (CodeChannel channel in Enum.GetValues(typeof(CodeChannel)))
            {
                lock (_pendingCodes[channel])
                {
                    if (_pendingCodes[channel].Count != 0)
                    {
                        return false;
                    }
                }

                lock (_pendingSystemCodes[channel])
                {
                    if (_pendingSystemCodes[channel].Count != 0)
                    {
                        return false;
                    }
                }

                lock (_pendingMacros[channel])
                {
                    if (_pendingMacros[channel].Count != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static async Task ProcessPacket(Communication.PacketHeader packet)
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
                    await HandleObjectModel();
                    break;

                case Communication.FirmwareRequests.Request.CodeReply:
                    HandleCodeReply();
                    break;

                case Communication.FirmwareRequests.Request.ExecuteMacro:
                    await HandleMacroRequest();
                    break;

                case Communication.FirmwareRequests.Request.AbortFile:
                    await HandleAbortFileRequest();
                    break;

                case Communication.FirmwareRequests.Request.StackEvent:
                    await HandleStackEvent();
                    break;

                case Communication.FirmwareRequests.Request.PrintPaused:
                    await HandlePrintPaused();
                    break;

                case Communication.FirmwareRequests.Request.HeightMap:
                    DataTransfer.ReadHeightMap(out Heightmap map);
                    lock (_getHeightmapRequest)
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
                    break;

                case Communication.FirmwareRequests.Request.Locked:
                    HandleResourceLocked();
                    break;
            }
        }

        private static async Task HandleObjectModel()
        {
            DataTransfer.ReadObjectModel(out byte module, out string json);

            // Merge the data into our own object model
            await Model.Updater.MergeData(module, json);

            // Reset everything if the controller is halted
            using (await Model.Provider.AccessReadOnly())
            {
                if (Model.Provider.Get.State.Status == MachineStatus.Halted)
                {
                    await InvalidateData("Code has been cancelled because the firmware is halted");
                }
                else if (module == 2 && Model.Provider.Get.State.Status == MachineStatus.Processing)
                {
                    _moduleToQuery = 3;
                }
                else
                {
                    _moduleToQuery = 2;
                }
            }
        }

        private static void HandleCodeReply()
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
                foreach (CodeChannel channel in Enum.GetValues(typeof(CodeChannel)))
                {
                    Communication.MessageTypeFlags channelFlag = (Communication.MessageTypeFlags)(1 << (int)channel);
                    if (flags.HasFlag(channelFlag))
                    {
                        QueuedCode code = null;

                        // Is this reply for a system code (requested from a macro file)?
                        lock (_pendingSystemCodes[channel])
                        {
                            _pendingSystemCodes[channel].TryPeek(out code);
                        }

                        if (code != null)
                        {
                            code.HandleReply(flags, reply);
                            continue;
                        }

                        // Is this reply for a regular code?
                        lock (_pendingCodes[channel])
                        {
                            _pendingCodes[channel].TryPeek(out code);
                        }

                        if (code != null && code.IsExecuting)
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
                Console.WriteLine($"[info] Executing requested macro file '{filename}' on channel {channel}");

                // Enqueue the macro file
                MacroFile macro = new MacroFile(path, channel, true, 0);
                lock (_pendingMacros[channel])
                {
                    _pendingMacros[channel].Push(macro);
                }

                // Deal with nested macros
                QueuedCode item;
                lock (_pendingSystemCodes[channel])
                {
                    _pendingSystemCodes[channel].TryPeek(out item);
                }
                if (item != null)
                {
                    item.DoingNestedMacro = true;
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
                    Console.WriteLine($"[info] Requested macro file '{filename}' not found");
                }

                _busyChannels |= (1 << (int)channel);
                DataTransfer.WriteMacroCompleted(channel, true);
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

            using (await Model.Provider.AccessReadWrite())
            {
                Channel item = Model.Provider.Get.Channels[channel];
                if (stackDepth > item.StackDepth)
                {
                    Console.WriteLine($"Push on {channel} level = {stackDepth}");
                }
                else if (stackDepth < item.StackDepth)
                {
                    Console.WriteLine($"Pop on {channel} level = {stackDepth}");
                }
                item.StackDepth = stackDepth;
                item.RelativeExtrusion = stackFlags.HasFlag(Communication.FirmwareRequests.StackFlags.DrivesRelative);
                item.RelativePositioning = stackFlags.HasFlag(Communication.FirmwareRequests.StackFlags.AxesRelative);
                item.UsingInches = stackFlags.HasFlag(Communication.FirmwareRequests.StackFlags.UsingInches);
                item.Feedrate = feedrate;
            }
        }

        private static async Task HandlePrintPaused()
        {
            DataTransfer.ReadPrintPaused(out uint filePosition, out Communication.PrintPausedReason pauseReason);
            Console.WriteLine($"[info] Print has been paused at file position {filePosition}. Reason: {pauseReason}");

            // Make the print stop and rewind back to the given file position
            await Print.OnPause(filePosition);

            // Update the object model
            using (await Model.Provider.AccessReadWrite())
            {
                Model.Provider.Get.State.Status = MachineStatus.Paused;
            }

            // Resolve pending codes on the file channel
            lock (_pendingCodes[CodeChannel.File])
            {
                while (_pendingCodes[CodeChannel.File].TryDequeue(out QueuedCode code))
                {
                    code.HandleReply(Communication.MessageTypeFlags.FileMessage, $"Print has been paused at byte {filePosition}");
                    code.SetFinished();
                }
            }
        }

        private static void HandleResourceLocked()
        {
            DataTransfer.ReadResourceLocked(out CodeChannel channel);
            lock (_pendingLockRequests[channel])
            {
                if (_pendingLockRequests[channel].TryDequeue(out QueuedLockRequest item))
                {
                    item.Resolve(true);
                }
            }
        }

        private static async Task InvalidateData(string codeErrorMessage)
        {
            // Close every open file. This closes the internal macro files as well
            await Print.Cancel();
            foreach (CodeChannel channel in Enum.GetValues(typeof(CodeChannel)))
            {
                MacroFile.AbortAllFiles(channel);
            }

            // Resolve pending (system) codes
            foreach (CodeChannel channel in Enum.GetValues(typeof(CodeChannel)))
            {
                lock (_pendingCodes[channel])
                {
                    Queue<QueuedCode> validCodes = new Queue<QueuedCode>();

                    while (_pendingCodes[channel].TryDequeue(out QueuedCode item))
                    {
                        if (item.Code.Type == CodeType.MCode && (item.Code.MajorNumber != 112 || item.Code.MajorNumber != 999))
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
                }

                lock (_pendingSystemCodes[channel])
                {
                    while (_pendingSystemCodes[channel].TryPop(out QueuedCode item))
                    {
                        item.HandleReply(Communication.MessageTypeFlags.ErrorMessageFlag, codeErrorMessage);
                        item.SetFinished();
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

            // Resolve pending lock/unlock requests
            foreach (CodeChannel channel in Enum.GetValues(typeof(CodeChannel)))
            {
                lock (_pendingLockRequests[channel])
                {
                    while (_pendingLockRequests[channel].TryDequeue(out QueuedLockRequest item))
                    {
                        item.Resolve(false);
                    }
                }
            }
        }
    }
}
