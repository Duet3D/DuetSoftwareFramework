using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Machine;
using DuetControlServer.FileExecution;
using DuetControlServer.Files;
using DuetControlServer.SPI.Communication.Shared;
using DuetControlServer.Utility;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.SPI.Channel
{
    /// <summary>
    /// Class used to process data on a single code channel
    /// </summary>
    public class Processor
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private readonly NLog.Logger _logger;

        /// <summary>
        /// What code channel this class is about
        /// </summary>
        public CodeChannel Channel { get; }

        /// <summary>
        /// Constructor of a code channel processor
        /// </summary>
        /// <param name="channel">Code channel of this instance</param>
        public Processor(CodeChannel channel)
        {
            _logger = NLog.LogManager.GetLogger(channel.ToString());
            Channel = channel;

            CurrentState = new State();
            Stack.Push(CurrentState);
        }

        /// <summary>
        /// Lock used when accessing this instance
        /// </summary>
        private readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Lock access to this code channel
        /// </summary>
        /// <returns>Disposable lock</returns>
        public IDisposable Lock() => _lock.Lock(Program.CancellationToken);

        /// <summary>
        /// Lock access to this code channel asynchronously
        /// </summary>
        /// <returns>Disposable lock</returns>
        public AwaitableDisposable<IDisposable> LockAsync() => _lock.LockAsync(Program.CancellationToken);

        /// <summary>
        /// Indicates if this channel is blocked until the next full transfer
        /// </summary>
        public bool IsBlocked { get; set; }

        /// <summary>
        /// Prioritised codes that override every other code
        /// </summary>
        public Queue<PendingCode> PriorityCodes { get; } = new Queue<PendingCode>();

        /// <summary>
        /// Stack of the different channel states
        /// </summary>
        public Stack<State> Stack { get; } = new Stack<State>();

        /// <summary>
        /// Get the current state from the stack
        /// </summary>
        public State CurrentState { get; private set; }

        /// <summary>
        /// Push a new state on the stack
        /// </summary>
        /// <returns>New state</returns>
        public State Push()
        {
            State state = new State();

            // Suspend the remaining buffered codes
            foreach (PendingCode bufferedCode in BufferedCodes)
            {
                _logger.Debug("Suspending code {0}", bufferedCode);
                CurrentState.SuspendedCodes.Enqueue(bufferedCode);
            }
            BytesBuffered = 0;
            BufferedCodes.Clear();

            // Do not send codes to RRF until it has cleared its internal buffer
            IsBlocked = true;

            // Done
            Stack.Push(state);
            CurrentState = state;
            return state;
        }

        /// <summary>
        /// Pop the last state from the stack
        /// </summary>
        public void Pop()
        {
            // There must be at least one item on the stack...
            if (Stack.Count == 1)
            {
                throw new InvalidOperationException("Stack underrun");
            }

            // Pop the stack
            State oldState = Stack.Pop();
            CurrentState = Stack.Peek();

            // Remove potential event listeners
            if (oldState.WaitingForAcknowledgement)
            {
                Model.Provider.Get.Inputs[Channel].PropertyChanged -= InputPropertyChanged;
                _propertyChangedRegistered = false;
            }

            // Invalidate obsolete lock requests and supended codes
            while (oldState.LockRequests.TryDequeue(out LockRequest lockRequest))
            {
                lockRequest.Resolve(false);
            }

            while (oldState.SuspendedCodes.TryDequeue(out PendingCode suspendedCode))
            {
                suspendedCode.SetCancelled();
            }

            // Make sure macro files are properly disposed
            if (oldState.Macro != null)
            {
                using (oldState.Macro.Lock())
                {
                    if (oldState.Macro.IsExecuting)
                    {
                        oldState.Macro.Abort();
                    }
                    else if (Channel != CodeChannel.Daemon)
                    {
                        _logger.Info("Finished macro file {0}", Path.GetFileName(oldState.Macro.FileName));
                    }
                    else
                    {
                        _logger.Trace("Finished macro file {0}", Path.GetFileName(oldState.Macro.FileName));
                    }

                    if (oldState.MacroStartCode != null)
                    {
                        oldState.MacroStartCode.AppendReply(oldState.Macro.Result);
                        _logger.Debug("==> Unfinished starting code: {0}", oldState.MacroStartCode);

                        BufferedCodes.Insert(0, oldState.MacroStartCode);
                        BytesBuffered += oldState.MacroStartCode.BinarySize;
                    }
                    oldState.Macro.Dispose();
                }
            }

            // Invalidate pending codes and flush requests
            while (oldState.PendingCodes.TryDequeue(out PendingCode pendingCode))
            {
                pendingCode.SetCancelled();
            }

            while (oldState.FlushRequests.TryDequeue(out TaskCompletionSource<bool> source))
            {
                source.SetResult(false);
            }
        }

        /// <summary>
        /// List of buffered G/M/T-codes that are being processed by the firmware
        /// </summary>
        public List<PendingCode> BufferedCodes { get; } = new List<PendingCode>();

        /// <summary>
        /// Occupied space for buffered codes in bytes
        /// </summary>
        public int BytesBuffered { get; private set; }

        /// <summary>
        /// Write channel diagnostics to the given string builder
        /// </summary>
        /// <param name="builder">Target to write to</param>
        public void Diagnostics(StringBuilder builder)
        {
            StringBuilder channelDiagostics = new StringBuilder();

            foreach (PendingCode bufferedCode in BufferedCodes)
            {
                channelDiagostics.AppendLine($"Buffered code: {bufferedCode.Code}");
            }
            if (BytesBuffered != 0)
            {
                channelDiagostics.AppendLine($"==> {BytesBuffered} bytes");
            }

            bool topStackItem = true;
            foreach (State state in Stack)
            {
                if (topStackItem)
                {
                    topStackItem = false;
                }
                else
                {
                    channelDiagostics.AppendLine("> Next stack level");
                }

                if (state.WaitingForAcknowledgement)
                {
                    channelDiagostics.AppendLine("Waiting for acknowledgement");
                }
                if (state.LockRequests.Count > 0)
                {
                    channelDiagostics.AppendLine($"Number of lock/unlock requests: {state.LockRequests.Count(item => item.IsLockRequest)}/{state.LockRequests.Count(item => !item.IsLockRequest)}");
                }
                if (state.Macro != null)
                {
                    channelDiagostics.AppendLine($"{(state.Macro.IsExecuting ? "Executing" : "Finishing")} macro {state.Macro.FileName}, started by {((state.MacroStartCode == null) ? "system" : state.MacroStartCode.ToString())}");
                }
                foreach (PendingCode suspendedCode in state.SuspendedCodes)
                {
                    channelDiagostics.AppendLine($"Suspended code: {suspendedCode.Code}");
                }
                foreach (PendingCode pendingCode in state.PendingCodes)
                {
                    channelDiagostics.AppendLine($"Pending code: {pendingCode.Code}");
                }
                if (state.FlushRequests.Count > 0)
                {
                    channelDiagostics.AppendLine($"Number of flush requests: {state.FlushRequests.Count}");
                }
            }

            if (channelDiagostics.Length != 0)
            {
                builder.AppendLine($"{Channel}:");
                builder.Append(channelDiagostics);
            }
        }

        /// <summary>
        /// Checks if this channel is waiting for acknowledgement
        /// </summary>
        public bool IsWaitingForAcknowledgement
        {
            get => CurrentState.WaitingForAcknowledgement;
        }

        /// <summary>
        /// Process another code
        /// </summary>
        /// <param name="code">Code to process</param>
        public Task<CodeResult> ProcessCode(Code code)
        {
            if (code.CancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<CodeResult>(code.CancellationToken);
            }

            PendingCode item = new PendingCode(code);
            if (code.Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                // This code is supposed to override every other queued code
                PriorityCodes.Enqueue(item);
            }
            else if (code.Flags.HasFlag(CodeFlags.IsFromMacro))
            {
                // This code belongs to a macro file. Try to find it
                bool found = false;
                foreach (State state in Stack)
                {
                    if (code.Macro == null || state.Macro == code.Macro)
                    {
                        state.PendingCodes.Enqueue(item);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return Task.FromException<CodeResult>(new ArgumentException("Invalid macro code"));
                }
            }
            else if (code.IsForAcknowledgement)
            {
                // Regular code for a message acknowledgement
                bool found = false;
                foreach (State state in Stack)
                {
                    if (state.Macro == null && state.WaitingForAcknowledgement)
                    {
                        state.PendingCodes.Enqueue(item);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    foreach (State state in Stack)
                    {
                        if (state.Macro == null)
                        {
                            state.PendingCodes.Enqueue(item);
                            break;
                        }
                    }
                }
            }
            else
            {
                // Regular code
                foreach (State state in Stack)
                {
                    if (state.Macro == null && !state.WaitingForAcknowledgement)
                    {
                        state.PendingCodes.Enqueue(item);
                        break;
                    }
                }
            }
            return item.Task;
        }

        /// <summary>
        /// Flush pending codes and return true on success or false on failure
        /// </summary>
        /// <param name="code">Optional code for the flush target</param>
        /// <returns>Whether the codes could be flushed</returns>
        public Task<bool> Flush(Code code = null)
        {
            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (code != null)
            {
                foreach (State state in Stack)
                {
                    if (state.WaitingForAcknowledgement && code.IsForAcknowledgement)
                    {
                        state.FlushRequests.Enqueue(tcs);
                        return tcs.Task;
                    }
                    if (code.Macro != null)
                    {
                        if (code.Macro == state.Macro)
                        {
                            state.FlushRequests.Enqueue(tcs);
                            return tcs.Task;
                        }
                    }
                    if (!state.WaitingForAcknowledgement && state.Macro == null)
                    {
                        state.FlushRequests.Enqueue(tcs);
                        return tcs.Task;
                    }
                }
            }

            CurrentState.FlushRequests.Enqueue(tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Lock the move module and wait for standstill
        /// </summary>
        /// <returns>Whether the resource could be locked</returns>
        public Task<bool> LockMovementAndWaitForStandstill()
        {
            LockRequest request = new LockRequest(true);
            CurrentState.LockRequests.Enqueue(request);
            return request.Task;
        }

        /// <summary>
        /// Unlock all resources occupied by the given channel
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public Task UnlockAll()
        {
            LockRequest request = new LockRequest(false);
            CurrentState.LockRequests.Enqueue(request);
            return request.Task;
        }

        /// <summary>
        /// Abort the last or all files
        /// </summary>
        /// <param name="abortAll">Whether to abort all files</param>
        /// <param name="printStopped">Whether the print has been stopped</param>
        public void AbortFile(bool abortAll, bool printStopped)
        {
            // Kill the pending message(s)
            while (CurrentState.WaitingForAcknowledgement)
            {
                MessageAcknowledged();
            }

            // Stop the file print if necessary
            if (Channel == CodeChannel.File && !printStopped && (abortAll || CurrentState.Macro == null))
            {
                using (FileExecution.Job.Lock())
                {
                    FileExecution.Job.Abort();
                }
            }

            if (abortAll)
            {
                // Invalidate stack levels running macro files and resolve their start codes
                while (CurrentState.WaitingForAcknowledgement || CurrentState.Macro != null)
                {
                    if (CurrentState.MacroStartCode != null)
                    {
                        CurrentState.MacroStartCode.AppendReply(CurrentState.Macro.Result);
                        CurrentState.MacroStartCode.SetFinished();
                        CurrentState.MacroStartCode = null;
                    }

                    Pop();
                }

                // Cancel all other buffered and regular codes
                InvalidateRegular();
            }
            else
            {
                // Invalidate the last stack level if a macro file is running
                PendingCode lastMacroStartCode = CurrentState.MacroStartCode;
                if (CurrentState.Macro != null)
                {
                    Pop();
                }

                // Invalidate all the buffered codes except for the one that invoked the last macro file
                for (int i = BufferedCodes.Count - 1; i > 0; i--)
                {
                    if (BufferedCodes[i] != lastMacroStartCode)
                    {
                        BufferedCodes[i].SetCancelled();
                        BytesBuffered -= BufferedCodes[i].BinarySize;
                        BufferedCodes.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        /// Called when a resource has been locked
        /// </summary>
        public void ResourceLocked()
        {
            if (CurrentState.LockRequests.TryDequeue(out LockRequest item))
            {
                item.Resolve(true);
            }
        }

        /// <summary>
        /// Process pending requests on this channel
        /// </summary>
        /// <returns>If anything more can be done on this channel</returns>
        public bool Run()
        {
            // 1. Lock/Unlock requests
            if (CurrentState.LockRequests.TryPeek(out LockRequest lockRequest))
            {
                if (lockRequest.IsLockRequest)
                {
                    if (!lockRequest.IsLockRequested)
                    {
                        lockRequest.IsLockRequested = DataTransfer.WriteLockMovementAndWaitForStandstill(Channel);
                    }
                }
                else if (DataTransfer.WriteUnlock(Channel))
                {
                    lockRequest.Resolve(true);
                    CurrentState.LockRequests.Dequeue();
                }
                return false;
            }

            // 2. Suspended codes being resumed (may include priority and macro codes)
            if (CurrentState.SuspendedCodes.TryPeek(out PendingCode suspendedCode))
            {
                if (BufferCode(suspendedCode))
                {
                    _logger.Debug("-> Resumed suspended code");
                    CurrentState.SuspendedCodes.Dequeue();
                    return true;
                }
                return false;
            }

            // 3. Priority codes
            if (PriorityCodes.TryPeek(out PendingCode queuedCode))
            {
                if (BufferCode(queuedCode))
                {
                    PriorityCodes.Dequeue();
                    return true;
                }
                return false;
            }

            // 4. Macro files
            if (CurrentState.Macro != null)
            {
                using (CurrentState.Macro.Lock())
                {
                    if (!CurrentState.Macro.IsExecuting)
                    {
                        if (!CurrentState.MacroCompleted)
                        {
                            CurrentState.MacroCompleted = DataTransfer.WriteMacroCompleted(Channel, CurrentState.Macro.HadError);
                        }
                        return false;
                    }
                }
            }

            // 5. Pending codes
            if (CurrentState.PendingCodes.TryPeek(out queuedCode))
            {
                if (BufferCode(queuedCode))
                {
                    CurrentState.PendingCodes.Dequeue();
                    return true;
                }
                return false;
            }

            // 6. Flush requests
            if (BufferedCodes.Count == 0 && CurrentState.FlushRequests.TryDequeue(out TaskCompletionSource<bool> flushRequest))
            {
                flushRequest.SetResult(true);
                return false;
            }

            // End
            return false;
        }

        /// <summary>
        /// Perform a regular code that was requested from the firmware
        /// </summary>
        /// <param name="code">Code to perform</param>
        public void DoFirmwareCode(string code)
        {
            try
            {
                _logger.Info("Running code from firmware '{0}' on channel {1}", code, Channel);
                Code codeObj = new Code(code) { Channel = Channel, Flags = CodeFlags.IsFromFirmware };
                _ = codeObj.Execute().ContinueWith(async task =>
                {
                    try
                    {
                        CodeResult result = await task;
                        foreach (Message message in result)
                        {
                            // Check what kind of message this is
                            MessageTypeFlags flags = (MessageTypeFlags)(1 << (int)Channel);
                            if (message.Type != MessageType.Success)
                            {
                                flags |= (message.Type == MessageType.Error) ? MessageTypeFlags.ErrorMessageFlag : MessageTypeFlags.WarningMessageFlag;
                            }

                            // Split the message into multiple chunks so RRF can output it
                            Memory<byte> encodedMessage = Encoding.UTF8.GetBytes(result.ToString());
                            for (int i = 0; i < encodedMessage.Length; i += Communication.Consts.MaxMessageLength)
                            {
                                if (i + Communication.Consts.MaxMessageLength >= encodedMessage.Length)
                                {
                                    Memory<byte> partialMessage = encodedMessage.Slice(i);
                                    Interface.SendMessage(flags, Encoding.UTF8.GetString(partialMessage.ToArray()));
                                }
                                else
                                {
                                    Memory<byte> partialMessage = encodedMessage.Slice(i, Math.Min(encodedMessage.Length - i, Communication.Consts.MaxMessageLength));
                                    Interface.SendMessage(flags | MessageTypeFlags.PushFlag, Encoding.UTF8.GetString(partialMessage.ToArray()));
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Code has been cancelled. Don't log this
                    }
                    catch (AggregateException ae)
                    {
                        await Logger.LogOutput(MessageType.Error, $"Failed to execute {code} from firmware: [{ae.InnerException.GetType().Name}] {ae.InnerException.Message}");
                    }
                    catch (Exception e)
                    {
                        await Logger.LogOutput(MessageType.Error, $"Failed to execute {code} from firmware: [{e.GetType().Name}] {e.Message}");
                    }
                }, TaskContinuationOptions.RunContinuationsAsynchronously);
            }
            catch (CodeParserException cpe)
            {
                MessageTypeFlags flags = (MessageTypeFlags)(1 << (int)Channel) | MessageTypeFlags.ErrorMessageFlag;
                Interface.SendMessage(flags, "Failed to parse firmware code: " + cpe.Message);
            }
        }

        /// <summary>
        /// Store a pending code for transmission to RepRapFirmware
        /// </summary>
        /// <param name="pendingCode">Code to transfer</param>
        /// <returns>True if the code could be buffered</returns>
        private bool BufferCode(PendingCode pendingCode)
        {
            if (pendingCode.Code.CancellationToken.IsCancellationRequested)
            {
                // Don't send cancelled codes to the firmware...
                pendingCode.SetCancelled();
                return true;
            }

            try
            {
                if ((BytesBuffered == 0 || BytesBuffered + pendingCode.BinarySize <= Settings.MaxBufferSpacePerChannel) &&
                    Interface.SendCode(pendingCode.Code, pendingCode.BinarySize))
                {
                    BytesBuffered += pendingCode.BinarySize;
                    BufferedCodes.Add(pendingCode);
                    _logger.Debug("Sent {0}, remaining space {1}, needed {2}", pendingCode.Code, Settings.MaxBufferSpacePerChannel - BytesBuffered, pendingCode.BinarySize);
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to buffer code {0}", pendingCode.Code);
                pendingCode.SetException(e);
                return true;
            }
        }

        /// <summary>
        /// Indicates if the next empty response is supposed to be suppressed (e.g. because a print event just occurred)
        /// </summary>
        private bool _suppressEmptyReply;

        /// <summary>
        /// Partial log message that has not been printed yet
        /// </summary>
        /// <summary>
        /// Handle a G-code reply
        /// </summary>
        /// <param name="flags">Message flags</param>
        /// <param name="reply">Code reply</param>
        /// <returns>Whether the reply could be processed</returns>
        public bool HandleReply(MessageTypeFlags flags, string reply)
        {
            // Deal with codes being executed
            if (BufferedCodes.Count > 0)
            {
                BufferedCodes[0].HandleReply(flags, reply);
                if (BufferedCodes[0].IsFinished)
                {
                    BytesBuffered -= BufferedCodes[0].BinarySize;
                    BufferedCodes.RemoveAt(0);
                }
                return true;
            }

            // Check for final empty replies to macro files being closed
            if (CurrentState.MacroCompleted)
            {
                PendingCode startCode = CurrentState.MacroStartCode;
                if (startCode != null)
                {
                    startCode.AppendReply(CurrentState.Macro.Result);
                    startCode.HandleReply(flags, reply);
                    if (startCode.IsFinished)
                    {
                        CurrentState.MacroStartCode = null;
                    }
                    Pop();
                    if (startCode.IsFinished)
                    {
                        _logger.Debug("==> Finished starting code: {0}", startCode);
                    }
                    return true;
                }

                Pop();
                return string.IsNullOrEmpty(reply);
            }

            // Check for message boxes being closed
            if (CurrentState.WaitingForAcknowledgement && string.IsNullOrEmpty(reply))
            {
                MessageAcknowledged();
                return true;
            }

            // Unless this message comes from the file or code queue it is out-of-order...
            if (Channel != CodeChannel.Queue)
            {
                if (!string.IsNullOrEmpty(reply) || !_suppressEmptyReply)
                {
                    _logger.Warn("Out-of-order reply: '{0}'", reply);
                }
                else
                {
                    _suppressEmptyReply = false;
                }
            }
            return false;
        }

        /// <summary>
        /// Event that is called when a property of this input channel in the OM has changed while waiting for acknowledgement
        /// </summary>
        /// <param name="sender">Sender</param>
        /// <param name="e">Event args</param>
        private void InputPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName.Equals(nameof(InputChannel.State)) &&
                (InputChannelState)sender.GetType().GetProperty(e.PropertyName).GetValue(sender) != InputChannelState.AwaitingAcknowledgement)
            {
                using (_lock.Lock())
                {
                    // Make sure the G-code flow is resumed even if the message box is closed from RRF
                    MessageAcknowledged();
                }
            }
        }

        /// <summary>
        /// Indicates if the event handler <see cref="InputPropertyChanged(object, PropertyChangedEventArgs)"/> is registered
        /// </summary>
        private bool _propertyChangedRegistered;

        /// <summary>
        /// Wait for a message to be acknowledged
        /// </summary>
        public void WaitForAcknowledgement()
        {
            if (!CurrentState.WaitingForAcknowledgement)
            {
                _logger.Debug("Waiting for acknowledgement");

                State newState = Push();
                newState.WaitingForAcknowledgement = true;

                if (!_propertyChangedRegistered)
                {
                    Model.Provider.Get.Inputs[Channel].PropertyChanged += InputPropertyChanged;
                    _propertyChangedRegistered = true;
                }
            }
        }

        /// <summary>
        /// Called when a message has been acknowledged
        /// </summary>
        public void MessageAcknowledged()
        {
            if (CurrentState.WaitingForAcknowledgement)
            {
                _logger.Debug("Message acknowledged");
                Pop();
            }
        }

        /// <summary>
        /// Attempt to start a file macro
        /// </summary>
        /// <param name="fileName">Name of the macro file</param>
        /// <param name="reportMissing">Report an error if the file could not be found</param>
        /// <param name="fromCode">Request comes from a real G/M/T-code</param>
        /// <returns>Asynchronous task</returns>
        public async Task DoMacroFile(string fileName, bool reportMissing, bool fromCode)
        {
            // Figure out which code started the macro file
            PendingCode startCode = null;
            if (fromCode)
            {
                if (CurrentState.MacroCompleted)
                {
                    _logger.Info("Finished intermediate macro file {0}", Path.GetFileName(CurrentState.Macro.FileName));
                    startCode = CurrentState.MacroStartCode;
                    if (startCode != null)
                    {
                        startCode.AppendReply(CurrentState.Macro.Result);
                    }
                    CurrentState.MacroStartCode = null;     // don't add it back to the buffered codes because it's about to be pushed on the stack again
                    Pop();
                }
                else if (BufferedCodes.Count > 0)
                {
                    startCode = BufferedCodes[0];
                    BytesBuffered -= startCode.BinarySize;
                    BufferedCodes.RemoveAt(0);
                }
            }

            // FIXME Check if the actual macro filename has to be adjusted - will become obsolete when RRF gets its own task for the Linux interface
            if (startCode != null)
            {
                if ((fileName == "stop.g" || fileName == "sleep.g") && startCode.Code.CancellingPrint)
                {
                    string cancelFile = await FilePath.ToPhysicalAsync("cancel.g", FileDirectory.System);
                    if (File.Exists(cancelFile))
                    {
                        // Execute cancel.g instead of stop.g if it exists
                        fileName = "cancel.g";
                    }
                }
            }

            // Try to locate the macro file
            string physicalFile = await FilePath.ToPhysicalAsync(fileName, FileDirectory.System);
            if (!File.Exists(physicalFile))
            {
                if (fileName == FilePath.ConfigFile)
                {
                    physicalFile = await FilePath.ToPhysicalAsync(FilePath.ConfigFileFallback, FileDirectory.System);
                    if (File.Exists(physicalFile))
                    {
                        // Use config.b.bak if config.g cannot be found
                        _logger.Warn("Using fallback file {0} because {1} could not be found", FilePath.ConfigFileFallback, FilePath.ConfigFile);
                    }
                    else
                    {
                        // No configuration file found
                        await Logger.LogOutput(MessageType.Error, $"Macro files {FilePath.ConfigFile} and {FilePath.ConfigFileFallback} not found");
                    }
                }
                else if (reportMissing)
                {
                    if (!fromCode || BufferedCodes.Count == 0 || BufferedCodes[0].Code.Type != CodeType.MCode || BufferedCodes[0].Code.MajorNumber != 98)
                    {
                        // M98 outputs its own warning message via RRF
                        await Logger.LogOutput(MessageType.Error, $"Macro file {fileName} not found");
                    }
                }
                else if (FilePath.DeployProbePattern.IsMatch(fileName))
                {
                    physicalFile = await FilePath.ToPhysicalAsync(FilePath.DeployProbeFallbackFile, FileDirectory.System);
                    if (File.Exists(physicalFile))
                    {
                        _logger.Info($"Using fallback file {FilePath.DeployProbeFallbackFile} because {fileName} could not be found");
                    }
                    else
                    {
                        // No deployprobe file found
                        _logger.Info($"Optional macro files {fileName} and {FilePath.DeployProbeFallbackFile} not found");
                    }
                }
                else if (FilePath.RetractProbePattern.IsMatch(fileName))
                {
                    physicalFile = await FilePath.ToPhysicalAsync(FilePath.RetractProbeFallbackFile, FileDirectory.System);
                    if (File.Exists(physicalFile))
                    {
                        _logger.Info($"Using fallback file {FilePath.RetractProbeFallbackFile} because {fileName} could not be found");
                    }
                    else
                    {
                        // No retractprobe file found
                        _logger.Info($"Optional macro files {fileName} and {FilePath.RetractProbeFallbackFile} not found");
                    }
                }
                else if (fileName != FilePath.DaemonFile)
                {
                    _logger.Info("Optional macro file {0} not found", fileName);
                }
                else
                {
                    _logger.Trace("Optional macro file {0} not found", fileName);
                }
            }

            // Push the stack and try to start the macro file
            State newState = Push();
            newState.MacroStartCode = startCode;
            newState.Macro = new Macro(physicalFile, Channel, startCode != null, (startCode != null) ? startCode.Code.SourceConnection : 0);
        }

        /// <summary>
        /// Invalidate buffered and regular codes + requests
        /// </summary>
        public void InvalidateRegular()
        {
            foreach (PendingCode bufferedCode in BufferedCodes)
            {
                bufferedCode.SetCancelled();
            }
            BytesBuffered = 0;
            BufferedCodes.Clear();

            foreach (State state in Stack)
            {
                if (!state.WaitingForAcknowledgement && state.Macro == null)
                {
                    while (state.LockRequests.TryDequeue(out LockRequest lockRequest))
                    {
                        lockRequest.Resolve(false);
                    }

                    while (state.SuspendedCodes.TryDequeue(out PendingCode suspendedCode))
                    {
                        suspendedCode.SetCancelled();
                    }

                    while (state.PendingCodes.TryDequeue(out PendingCode pendingCode))
                    {
                        pendingCode.SetCancelled();
                    }

                    while (state.FlushRequests.TryDequeue(out TaskCompletionSource<bool> source))
                    {
                        source.SetResult(false);
                    }
                }
            }

            _suppressEmptyReply = true;
        }

        /// <summary>
        /// Invalidate every request and buffered code on this channel
        /// </summary>
        /// <returns>If any resource has been invalidated</returns>
        public bool Invalidate()
        {
            bool resourceInvalidated = false;

            // Invalidate the stack
            do
            {
                while (CurrentState.LockRequests.TryDequeue(out LockRequest lockRequest))
                {
                    lockRequest.Resolve(false);
                    resourceInvalidated = true;
                }

                while (CurrentState.SuspendedCodes.TryDequeue(out PendingCode suspendedCode))
                {
                    suspendedCode.SetCancelled();
                    resourceInvalidated = true;
                }

                // Macro files and their start codes are disposed by Pop()
                resourceInvalidated |= (CurrentState.Macro != null);

                while (CurrentState.PendingCodes.TryDequeue(out PendingCode pendingCode))
                {
                    pendingCode.SetCancelled();
                    resourceInvalidated = true;
                }

                while (CurrentState.FlushRequests.TryDequeue(out TaskCompletionSource<bool> source))
                {
                    source.SetResult(false);
                    resourceInvalidated = true;
                }

                if (Stack.Count == 1)
                {
                    break;
                }
                Pop();
            }
            while (true);

            // Clear codes being processed
            foreach (PendingCode bufferedCode in BufferedCodes)
            {
                bufferedCode.SetCancelled();
                resourceInvalidated = true;
            }
            BufferedCodes.Clear();
            BytesBuffered = 0;

            // Clear codes that are still pending but have not been fed into the SPI interface yet
            Code.CancelPending(Channel);

            // Done
            IsBlocked = true;
            return resourceInvalidated;
        }
    }
}
