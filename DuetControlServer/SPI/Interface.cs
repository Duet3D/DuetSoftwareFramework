using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.FileExecution;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// This class accesses RepRapFirmware via SPI and deals with general communication
    /// </summary>
    public static class Interface
    {
        // Requests for each code channel
        private static readonly Dictionary<CodeChannel, Queue<QueuedCode>> _pendingCodes = new Dictionary<CodeChannel, Queue<QueuedCode>>();
        private static readonly Dictionary<CodeChannel, Queue<QueuedLockRequest>> _pendingLockRequests = new Dictionary<CodeChannel, Queue<QueuedLockRequest>>();
        private static readonly Dictionary<CodeChannel, Queue<MacroFile>> _pendingMacros = new Dictionary<CodeChannel, Queue<MacroFile>>();

        // Code channels that are currently blocked because of an executing G/M/T-code
        private static int _busyChannels = 0;

        // Number of the module of the object model being queried (TODO fully implement this)
        private static byte _moduleToQuery = 2;

        // Special requests
        private static bool _emergencyStopRequested, _resetRequested, _printStarted;
        private static Communication.PrintStoppedReason? _printStoppedReason;

        // Partial message (if any)
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
                _pendingMacros.Add(channel, new Queue<MacroFile>());
                _pendingLockRequests.Add(channel, new Queue<QueuedLockRequest>());
            }

            // Request buffer states immediately
            DataTransfer.WriteGetState();
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
        /// Enqueue this code to be executed but don't wait for it.
        /// This is used for macro files requested from the firmware
        /// </summary>
        /// <param name="code">Code to execute</param>
        private static void ProcessSystemCode(Code code)
        {
            QueuedCode item = new QueuedCode(code, true);
            lock (_pendingCodes[code.Channel])
            {
                _pendingCodes[code.Channel].Enqueue(item);
            }
        }

        /// <summary>
        /// Request an immediate emergency stop
        /// </summary>
        public static void RequestEmergencyStop()
        {
            _emergencyStopRequested = true;
        }

        /// <summary>
        /// Request a firmware reset
        /// </summary>
        public static void RequestReset()
        {
            _resetRequested = true;
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
        /// <param name="stopReason"></param>
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
        /// This is only called 
        /// </summary>
        public static Task Connect()
        {
            // Do one transfer to ensure both sides are using compatible versions of the data protocol
            return DataTransfer.PerformFullTransfer();
        }

        /// <summary>
        /// Perform communication with the RepRapFirmware controller
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public static async Task Run()
        {
            do
            {
                // Process incoming packets
                for (Communication.PacketHeader? packet = DataTransfer.ReadPacket(); packet != null; packet = DataTransfer.ReadPacket())
                {
                    try
                    {
                        //Console.WriteLine($"-> Packet #{packet.Value.Id} (request {(Communication.FirmwareRequests.Request)packet.Value.Request}) length {packet.Value.Length}");
                        await ProcessPacket(packet.Value);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        DataTransfer.DumpMalformedPacket();
                    }
                }

                // Check if an emergency stop has been requested. Perform another transfer immediately because this is time-critical
                if (_emergencyStopRequested && DataTransfer.WriteEmergencyStop())
                {
                    _emergencyStopRequested = false;
                    await DataTransfer.PerformFullTransfer();
                    InvalidateData(false);
                }

                // Check if a firmware reset has been performed or requested
                if (DataTransfer.HadReset() || (_resetRequested && DataTransfer.WriteReset()))
                {
                    _resetRequested = false;
                    InvalidateData(true);
                }

                // Process pending codes, macro files and requests for resource locks/unlocks
                foreach (CodeChannel channel in Enum.GetValues(typeof(CodeChannel)))
                {
                    if ((_busyChannels & (1 << (int)channel)) == 0)
                    {
                        // Attempt to get another queued code
                        QueuedCode item = null;
                        lock (_pendingCodes[channel])
                        {
                            _pendingCodes[channel].TryPeek(out item);
                        }

                        // If there is no queued code, check if another code from a macro file can be started
                        if (item == null)
                        {
                            MacroFile macro = null;
                            lock (_pendingMacros[channel])
                            {
                                _pendingMacros[channel].TryPeek(out macro);
                            }

                            if (macro != null)
                            {
                                Code code = await macro.ReadCode();
                                if (code == null)
                                {
                                    // Macro file is complete
                                    if (DataTransfer.WriteMacroCompleted(channel, false))
                                    {
                                        lock (_pendingMacros[channel])
                                        {
                                            _pendingMacros[channel].Dequeue();
                                        }
                                    }
                                }
                                else
                                {
                                    // Run the next code from the macro file
                                    ProcessSystemCode(code);
                                    _pendingCodes[channel].TryPeek(out item);
                                }
                            }
                        }

                        // See if there is any queued code to execute
                        if (item != null)
                        {
                            if (item.IsExecuting)
                            {
                                // The reply for this code may not have been received yet. Expect one to come even if it is empty - better than missing output
                                if (item.CanFinish)
                                {
                                    // This code has finished
                                    lock (_pendingCodes[channel])
                                    {
                                        _pendingCodes[channel].Dequeue();
                                    }
                                    item.SetFinished();
                                }
                            }
                            else
                            {
                                // Send it to the firmware and request. This may fail if the code is too big
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
                                    lock (_pendingCodes[channel])
                                    {
                                        _pendingCodes[channel].Dequeue();
                                    }
                                    item.SetException(e);
                                }
                            }
                        }
                    }

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

                // Request the state of the GCodeBuffers and the object model after the codes have been processed
                DataTransfer.WriteGetState();
                DataTransfer.WriteGetObjectModel(_moduleToQuery);

                // Check for changes of the print status
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

                // Do another full SPI transfer
                await DataTransfer.PerformFullTransfer();

                // Wait a moment
                await Task.Delay(Settings.SpiPollDelay, Program.CancelSource.Token);
            } while (!Program.CancelSource.IsCancellationRequested);
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
                    HandleAbortFileRequest();
                    break;

                case Communication.FirmwareRequests.Request.StackEvent:
                    await HandleStackEvent();
                    break;

                case Communication.FirmwareRequests.Request.PrintPaused:
                    await HandlePrintPaused();
                    break;

                case Communication.FirmwareRequests.Request.HeightMap:
                    DataTransfer.ReadHeightMap(out Communication.FirmwareRequests.HeightMap header, out float[] zCoordinates);
                    // TODO implement handling via own HeightMap class
                    Console.WriteLine("Got heightmap");
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
                if (Model.Provider.Get.State.Status == DuetAPI.Machine.State.Status.Halted)
                {
                    InvalidateData(false);
                }
                else if (module == 2 && Model.Provider.Get.State.Status == DuetAPI.Machine.State.Status.Processing)
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
            //Console.WriteLine($"Got code reply [{flags}] {reply}");

            // Check if this reply was incomplete
            if (_partialCodeReply != null)
            {
                reply = _partialCodeReply + reply;
                _partialCodeReply = null;
            }

            // Try to feed the message to every destination channel
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
                        lock (_pendingCodes)
                        {
                            _pendingCodes[channel].TryPeek(out code);
                        }

                        if (code != null)
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

            // If at least one channel destination could not be reached, cache it or put it into the machine model
            if (!replyHandled)
            {
                if (flags.HasFlag(Communication.MessageTypeFlags.PushFlag))
                {
                    _partialCodeReply = reply;
                }
                else if (flags.HasFlag(Communication.MessageTypeFlags.ErrorMessageFlag))
                {
                    Log.LogError(reply);
                }
                else if (flags.HasFlag(Communication.MessageTypeFlags.WarningMessageFlag))
                {
                    Log.LogWarning(reply);
                }
                else
                {
                    Log.LogInfo(reply);
                }
            }
        }

        private static async Task HandleMacroRequest()
        {
            DataTransfer.ReadMacroRequest(out CodeChannel channel, out bool reportMissing, out string filename);

            string path = await FilePath.ToPhysical(filename, "sys");
            if (filename == MacroFile.ConfigFile && !File.Exists(filename))
            {
                string fallback = await FilePath.ToPhysical(MacroFile.ConfigFileFallback, "sys");
                if (File.Exists(fallback))
                {
                    // Use config.b.bak if config.g cannot be found
                    filename = fallback;
                }
                else
                {
                    Log.LogError($"Could not find macro files {MacroFile.ConfigFile} and {MacroFile.ConfigFileFallback}");
                    DataTransfer.WriteMacroCompleted(channel, true);
                    return;
                }
            }

            if (File.Exists(path))
            {
                Console.WriteLine($"[info] Executing requested macro file '{filename}'");

                MacroFile macro = new MacroFile(path, channel, 0);
                lock (_pendingMacros[channel])
                {
                    _pendingMacros[channel].Enqueue(macro);
                }
            }
            else
            {
                if (reportMissing)
                {
                    Log.LogError($"Could not find macro file {filename}");
                }
                DataTransfer.WriteMacroCompleted(channel, true);
            }
        }

        private static void HandleAbortFileRequest()
        {
            DataTransfer.ReadAbortFile(out CodeChannel channel);
            Console.WriteLine($"Abort file on channel {channel}");

            MacroFile.AbortAllFiles(channel);
            if (channel == CodeChannel.File)
            {
                Print.Cancel();
            }
        }

        private static async Task HandleStackEvent()
        {
            DataTransfer.ReadStackEvent(out CodeChannel channel, out byte stackDepth, out Communication.FirmwareRequests.StackFlags stackFlags, out float feedrate);
            Console.WriteLine("Stack event");

            using (await Model.Provider.AccessReadWrite())
            {
                DuetAPI.Machine.Channels.Channel item = Model.Provider.Get.Channels[channel];
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
            Console.WriteLine("Print paused");
            // pauseReason is still unused, integrate this into the object model at some point

            // Make the print stop and rewind back to the given file position
            Print.Paused(filePosition);

            // Update the object model
            using (await Model.Provider.AccessReadWrite())
            {
                Model.Provider.Get.State.Status = DuetAPI.Machine.State.Status.Paused;
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
            Console.WriteLine($"Resource locked on channel {channel}");

            lock (_pendingLockRequests[channel])
            {
                if (_pendingLockRequests[channel].TryDequeue(out QueuedLockRequest item))
                {
                    item.Resolve(true);
                }
            }
        }

        private static void InvalidateData(bool dueToReset)
        {
            // Close every open file. This closes the internal macro files as well
            Print.Cancel();
            foreach (CodeChannel channel in Enum.GetValues(typeof(CodeChannel)))
            {
                MacroFile.AbortAllFiles(channel);
            }

            // Resolve pending codes
            foreach (CodeChannel channel in Enum.GetValues(typeof(CodeChannel)))
            {
                lock (_pendingCodes[channel])
                {
                    while (_pendingCodes[channel].TryDequeue(out QueuedCode item))
                    {
                        item.HandleReply(Communication.MessageTypeFlags.ErrorMessageFlag, dueToReset ? "Code has been cancelled due to a controller reset" : "Code has been cancelled ddue to an emergency stop");
                        item.SetFinished();
                    }
                }
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

            // TODO invalidate object model?
        }
    }
}
