using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.FileExecution;
using DuetControlServer.Files;
using DuetControlServer.SPI.Communication.Shared;
using DuetControlServer.Utility;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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
        /// This is set to true if all the files have been aborted and RRF has to be notified
        /// </summary>
        public bool AllFilesAborted { get; set; }

        /// <summary>
        /// Prioritised codes that override every other code
        /// </summary>
        public Queue<Code> PriorityCodes { get; } = new Queue<Code>();

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
            foreach (Code bufferedCode in BufferedCodes)
            {
                _logger.Debug("Suspending code {0}", bufferedCode);
                CurrentState.SuspendedCodes.Enqueue(bufferedCode);
            }
            BytesBuffered = 0;
            BufferedCodes.Clear();

            // Done
            Stack.Push(state);
            CurrentState = state;
            return state;
        }

        /// <summary>
        /// Pop the last state from the stack
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public async Task Pop()
        {
            // There must be at least one item on the stack...
            if (Stack.Count == 1)
            {
                throw new InvalidOperationException($"Stack underrun on channel {Channel}");
            }

            // Pop the stack
            State oldState = Stack.Pop();
            CurrentState = Stack.Peek();
            _isWaitingForAcknowledgement = CurrentState.WaitingForAcknowledgement;

            // Invalidate obsolete lock requests and supended codes
            while (oldState.LockRequests.TryDequeue(out LockRequest lockRequest))
            {
                lockRequest.Resolve(false);
            }

            while (oldState.SuspendedCodes.TryDequeue(out Code suspendedCode))
            {
                suspendedCode.FirmwareTCS.SetCanceled();
            }

            // Deal with macro files
            if (oldState.Macro != null)
            {
                using (await oldState.Macro.LockAsync())
                {
                    if (oldState.Macro.IsExecuting)
                    {
                        if (!oldState.Macro.IsAborted)
                        {
                            _logger.Warn("Aborting orphaned macro file {0}", oldState.Macro.FileName);
                            await oldState.Macro.Abort();
                        }
                    }
                    else
                    {
                        if (Channel != CodeChannel.Daemon)
                        {
                            _logger.Debug("Disposing macro file {0}", oldState.Macro.FileName);
                        }
                        else
                        {
                            _logger.Trace("Disposing macro file {0}", oldState.Macro.FileName);
                        }
                        oldState.Macro.Dispose();
                    }
                }
            }

            // Invalidate macro start codes, pending codes, and flush requests
            if (oldState.StartCode != null)
            {
                _logger.Warn("==> Cancelling unfinished starting code: {0}", oldState.StartCode);
                oldState.StartCode.FirmwareTCS.SetCanceled();
            }

            while (oldState.PendingCodes.TryDequeue(out Code pendingCode))
            {
                pendingCode.FirmwareTCS.SetCanceled();
            }

            while (oldState.FlushRequests.TryDequeue(out TaskCompletionSource<bool> source))
            {
                source.SetResult(false);
            }
        }

        /// <summary>
        /// List of buffered G/M/T-codes that are being processed by the firmware
        /// </summary>
        public List<Code> BufferedCodes { get; } = new List<Code>();

        /// <summary>
        /// Occupied space for buffered codes in bytes
        /// </summary>
        public int BytesBuffered { get; private set; }

        /// <summary>
        /// Stack of code replies for codes that pushed the stack (e.g. macro files or blocking messages)
        /// </summary>
        public Stack<Tuple<MessageTypeFlags, string>> PendingReplies { get; } = new Stack<Tuple<MessageTypeFlags, string>>();

        /// <summary>
        /// Write channel diagnostics to the given string builder
        /// </summary>
        /// <param name="builder">Target to write to</param>
        /// <returns>Asynchronous task</returns>
        public async Task Diagnostics(StringBuilder builder)
        {
            StringBuilder channelDiagostics = new StringBuilder();

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);
            IDisposable lockObject = null;
            try
            {
                cts.CancelAfter(2000);
                lockObject = await _lock.LockAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                channelDiagostics.AppendLine($"Failed to lock {Channel} processor within 2 seconds");
            }

            foreach (Code bufferedCode in BufferedCodes)
            {
                channelDiagostics.AppendLine($"Buffered code: {bufferedCode}");
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
                    channelDiagostics.AppendLine($"Waiting for acknowledgement, requested by {((state.StartCode == null) ? "system" : state.StartCode.ToString())}");
                }
                if (state.LockRequests.Count > 0)
                {
                    channelDiagostics.AppendLine($"Number of lock/unlock requests: {state.LockRequests.Count(item => item.IsLockRequest)}/{state.LockRequests.Count(item => !item.IsLockRequest)}");
                }
                if (state.Macro != null)
                {
                    channelDiagostics.AppendLine($"{(state.Macro.IsExecuting ? "Executing" : "Finishing")} macro {state.Macro.FileName}, started by {((state.StartCode == null) ? "system" : state.StartCode.ToString())}");
                }
                foreach (Code suspendedCode in state.SuspendedCodes)
                {
                    channelDiagostics.AppendLine($"Suspended code: {suspendedCode}");
                }
                foreach (Code pendingCode in state.PendingCodes)
                {
                    channelDiagostics.AppendLine($"Pending code: {pendingCode}");
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
            lockObject?.Dispose();
        }

        /// <summary>
        /// Checks if this channel is waiting for acknowledgement
        /// </summary>
        /// <remarks>
        /// This is volatile to allow fast access without locking this instance first
        /// </remarks>
        public bool IsWaitingForAcknowledgement
        {
            get => _isWaitingForAcknowledgement;
        }
        private volatile bool _isWaitingForAcknowledgement;

        /// <summary>
        /// Process another code
        /// </summary>
        /// <param name="code">Code to process</param>
        public void ProcessCode(Code code)
        {
            if (code.CancellationToken.IsCancellationRequested)
            {
                code.FirmwareTCS.SetCanceled();
                return;
            }

            code.BinarySize = Communication.Consts.BufferedCodeHeaderSize + DataTransfer.GetCodeSize(code);
            if (code.Flags.HasFlag(CodeFlags.IsPrioritized))
            {
                // This code is supposed to override every other queued code
                PriorityCodes.Enqueue(code);
            }
            else if (code.Flags.HasFlag(CodeFlags.IsFromMacro))
            {
                // This code belongs to a macro file. Try to find it
                bool found = false;
                foreach (State state in Stack)
                {
                    if (code.Macro == null || state.Macro == code.Macro)
                    {
                        state.PendingCodes.Enqueue(code);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    // Trying to execute a G/M/T-code on a macro that has been closed while the code was being processed internally
                    code.FirmwareTCS.SetCanceled();
                    return;
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
                        state.PendingCodes.Enqueue(code);
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
                            state.PendingCodes.Enqueue(code);
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
                        state.PendingCodes.Enqueue(code);
                        break;
                    }
                }
            }
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
                    if (code.Macro != null && code.Macro == state.Macro)
                    {
                        state.FlushRequests.Enqueue(tcs);
                        return tcs.Task;
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
        /// <returns>Asynchronous task</returns>
        public async Task AbortFile(bool abortAll, bool printStopped)
        {
            bool macroAborted = false;

            // Clean up the stack
            Code startCode = null;
            while (CurrentState.WaitingForAcknowledgement || CurrentState.Macro != null)
            {
                if (CurrentState.StartCode != null)
                {
                    startCode = CurrentState.StartCode;
                    CurrentState.StartCode = null;
                }

                if (CurrentState.Macro != null)
                {
                    using (await CurrentState.Macro.LockAsync())
                    {
                        // Propagate the macro result to the code that started it
                        if (startCode != null)
                        {
                            _ = CurrentState.Macro.FinishAsync().ContinueWith(async task =>
                            {
                                CodeResult macroResult = await task;
                                if (startCode.Result == null)
                                {
                                    startCode.Result = macroResult;
                                }
                                else if (macroResult != null && !macroResult.IsEmpty)
                                {
                                    startCode.Result.AddRange(macroResult);
                                }
                                startCode.FirmwareTCS.SetResult(null);
                            }, TaskContinuationOptions.RunContinuationsAsynchronously);
                        }

                        // Abort the macro file
                        await CurrentState.Macro.Abort();
                    }
                    macroAborted = true;
                }
                else if (startCode != null)
                {
                    // Cancel the code that started the blocking message prompt
                    startCode.FirmwareTCS.SetCanceled();
                    startCode = null;
                }

                // Pop the stack
                await Pop();
                if (startCode != null)
                {
                    _logger.Debug("==> Unfinished starting code: {0}", startCode);
                }

                // Stop if only a single file is supposed to be aborted
                if (!abortAll && macroAborted)
                {
                    break;
                }
            }

            if (abortAll)
            {
                // Cancel pending codes and requests
                InvalidateRegular();
            }
            else
            {
                // Invalidate remaining buffered codes from the last macro file
                foreach (Code bufferedCode in BufferedCodes)
                {
                    bufferedCode.FirmwareTCS.SetCanceled();
                }
                BufferedCodes.Clear();
                BytesBuffered = 0;
            }

            // Stop the file print if necessary
            if (Channel == CodeChannel.File && !printStopped && (abortAll || !macroAborted))
            {
                using (await FileExecution.Job.LockAsync())
                {
                    await FileExecution.Job.Abort();
                }
            }
        }

        /// <summary>
        /// Called when a resource has been locked
        /// </summary>
        public void ResourceLocked()
        {
            foreach (State state in Stack)
            {
                if (state.LockRequests.TryDequeue(out LockRequest item))
                {
                    item.Resolve(true);
                    return;
                }
            }
            _logger.Error("Received a lock confirmation for a non-existent request!");
        }

        /// <summary>
        /// Resolve pending comment codes
        /// </summary>
        private void ResolveCommentCodes()
        {
            while (BufferedCodes.Count > 0 && BufferedCodes[0].Type == CodeType.Comment)
            {
                BytesBuffered -= BufferedCodes[0].BinarySize;
                BufferedCodes[0].Result = new CodeResult();
                BufferedCodes[0].FirmwareTCS.SetResult(null);
                BufferedCodes.RemoveAt(0);
            }
        }

        /// <summary>
        /// Process pending requests on this channel
        /// </summary>
        /// <returns>Whether a response can be expected from RRF in the next transmission</returns>
        public async Task<bool> Run()
        {
            bool responseExpected = false;

            // Process code replies that could not be interpreted immediately
            while (BufferedCodes.Count > 0 && PendingReplies.TryPop(out Tuple<MessageTypeFlags, string> reply))
            {
                await HandleReply(reply.Item1, reply.Item2);
            }

            // 1. Whole line comments
            ResolveCommentCodes();

            // 2. Lock/Unlock requests
            if (CurrentState.LockRequests.TryPeek(out LockRequest lockRequest))
            {
                if (lockRequest.IsLockRequest)
                {
                    if (!lockRequest.IsLockRequested && DataTransfer.WriteLockMovementAndWaitForStandstill(Channel))
                    {
                        lockRequest.IsLockRequested = true;
                        return true;
                    }
                    return responseExpected;
                }

                if (DataTransfer.WriteUnlock(Channel))
                {
                    lockRequest.Resolve(true);
                    CurrentState.LockRequests.Dequeue();
                    // Resources unlocked; carry on
                }
                else
                {
                    return responseExpected;
                }
            }

            // 3. Abort requests
            if (AllFilesAborted)
            {
                if (DataTransfer.WriteFilesAborted(Channel))
                {
                    await AbortFile(true, Channel == CodeChannel.File);
                    AllFilesAborted = false;
                }
                return responseExpected;
            }

            // 4. Macro files (must come before any other code)
            if (CurrentState.Macro != null)
            {
                // Tell RRF as quickly as possible about the new macro being started
                if (CurrentState.Macro.JustStarted)
                {
                    CurrentState.Macro.JustStarted = (DataTransfer.ProtocolVersion >= 3) && !DataTransfer.WriteMacroStarted(Channel);
                    return true;
                }

                // Check if the macro file has finished
                if (!CurrentState.Macro.IsExecuting)
                {
                    if (!CurrentState.MacroCompleted && DataTransfer.WriteMacroCompleted(Channel, !CurrentState.Macro.FileOpened))
                    {
                        CurrentState.MacroCompleted = true;
                        if (DataTransfer.ProtocolVersion >= 3)
                        {
                            if (!CurrentState.Macro.FileOpened)
                            {
                                // In newer protocol versions we don't expect a response because RRF will be waiting in a semaphore
                                Code startCode = CurrentState.StartCode;
                                if (startCode != null)
                                {
                                    BytesBuffered += startCode.BinarySize;
                                    BufferedCodes.Insert(0, startCode);
                                    CurrentState.StartCode = null;
                                }

                                // Macro has finished, pop the stack
                                await Pop();
                                if (startCode != null)
                                {
                                    _logger.Debug("==> Unfinished starting code: {0}", startCode);
                                }
                            }
                        }
                        else
                        {
                            // Wait for a response first if an older firmware version is used, then pop the stack
                            return true;
                        }
                    }
                    else
                    {
                        // Still waiting for acknowledgement or failed to write macro complete message, try again ASAP
                        return true;
                    }
                }
            }

            // 5. Suspended codes being resumed (may include priority and macro codes)
            while (CurrentState.SuspendedCodes.TryPeek(out Code suspendedCode))
            {
                if (BufferCode(suspendedCode))
                {
                    responseExpected = true;
                    _logger.Debug("-> Resumed suspended code");
                    CurrentState.SuspendedCodes.Dequeue();
                }
                else
                {
                    return responseExpected;
                }
            }

            // 6. Priority codes
            while (PriorityCodes.TryPeek(out Code priorityCode))
            {
                if (BufferCode(priorityCode))
                {
                    responseExpected = true;
                    PriorityCodes.Dequeue();
                }
                else
                {
                    return responseExpected;
                }
            }

            // 7. Pending codes
            while (CurrentState.PendingCodes.TryPeek(out Code pendingCode))
            {
                if (BufferCode(pendingCode))
                {
                    responseExpected = true;
                    CurrentState.PendingCodes.Dequeue();
                }
                else
                {
                    return responseExpected;
                }
            }

            // 8. Flush requests
            if (BufferedCodes.Count == 0 && CurrentState.FlushRequests.TryDequeue(out TaskCompletionSource<bool> flushRequest))
            {
                flushRequest.SetResult(true);
                return responseExpected;
            }

            // Log untracked code replies
            while (PendingReplies.TryPop(out Tuple<MessageTypeFlags, string> reply))
            {
                _logger.Warn("Pending out-of-order reply: '{0}'", reply.Item2);
            }

            // End
            return responseExpected;
        }

        /// <summary>
        /// Perform a regular code that was requested from the firmware
        /// </summary>
        /// <param name="code">Code to perform</param>
        public void DoFirmwareCode(string code)
        {
            try
            {
                _logger.Debug("Running code from firmware '{0}' on channel {1}", code, Channel);
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
                            for (int i = 0; i < encodedMessage.Length; i += Settings.MaxMessageLength)
                            {
                                if (i + Settings.MaxMessageLength >= encodedMessage.Length)
                                {
                                    Memory<byte> partialMessage = encodedMessage[i..];
                                    Interface.SendMessage(flags, Encoding.UTF8.GetString(partialMessage.ToArray()));
                                }
                                else
                                {
                                    Memory<byte> partialMessage = encodedMessage.Slice(i, Math.Min(encodedMessage.Length - i, Settings.MaxMessageLength));
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
                        _logger.Warn(ae);
                    }
                    catch (Exception e)
                    {
                        await Logger.LogOutput(MessageType.Error, $"Failed to execute {code} from firmware: [{e.GetType().Name}] {e.Message}");
                        _logger.Warn(e);
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
        private bool BufferCode(Code pendingCode)
        {
            if (pendingCode.CancellationToken.IsCancellationRequested)
            {
                // Don't send cancelled codes to the firmware...
                pendingCode.FirmwareTCS.SetCanceled();
                return true;
            }

            try
            {
                if ((BytesBuffered == 0 || BytesBuffered + pendingCode.BinarySize <= Settings.MaxBufferSpacePerChannel) &&
                    Interface.SendCode(pendingCode, pendingCode.BinarySize))
                {
                    BytesBuffered += pendingCode.BinarySize;
                    BufferedCodes.Add(pendingCode);
                    _logger.Debug("Sent {0}, remaining space {1}, needed {2}", pendingCode, Settings.MaxBufferSpacePerChannel - BytesBuffered, pendingCode.BinarySize);
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to buffer code {0}", pendingCode);
                pendingCode.FirmwareTCS.SetException(e);
                return true;
            }
        }

        /// <summary>
        /// Indicates if the next empty response is supposed to be suppressed (e.g. because a print event just occurred)
        /// </summary>
        private bool _suppressEmptyReply;

        /// <summary>
        /// Handle a G-code reply
        /// </summary>
        /// <param name="flags">Message flags</param>
        /// <param name="reply">Code reply</param>
        /// <returns>Whether the reply could be processed</returns>
        public async Task<bool> HandleReply(MessageTypeFlags flags, string reply)
        {
            // Replies are not meant for comment codes, resolve them separately
            ResolveCommentCodes();

            // Deal with codes being executed
            if (BufferedCodes.Count > 0)
            {
                int codeSize = BufferedCodes[0].BinarySize;
                if (await HandleCodeReply(BufferedCodes[0], flags, reply))
                {
                    BytesBuffered -= codeSize;
                    BufferedCodes.RemoveAt(0);
                }
                return true;
            }

            // Check for a final empty reply for the current macro file being closed
            if (CurrentState.MacroCompleted)
            {
                if (DataTransfer.ProtocolVersion < 3 && string.IsNullOrEmpty(reply))
                {
                    await MacroFileClosed();
                    return true;
                }
                else if (DataTransfer.ProtocolVersion >= 3)
                {
                    PendingReplies.Push(new Tuple<MessageTypeFlags, string>(flags, reply));
                    return true;
                }
            }

            // Check for message boxes being closed
            if (CurrentState.WaitingForAcknowledgement)
            {
                if (DataTransfer.ProtocolVersion < 3 && string.IsNullOrEmpty(reply))
                {
                    await MessageAcknowledged();
                    return true;
                }
                else if (DataTransfer.ProtocolVersion >= 3)
                {
                    PendingReplies.Push(new Tuple<MessageTypeFlags, string>(flags, reply));
                    return true;
                }
            }

            // Unless this message comes from the file or code queue it is out-of-order...
            if (Channel != CodeChannel.Queue)
            {
                if (!_suppressEmptyReply)
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
        /// Hold the flags of the last incomplete code reply
        /// </summary>
        private MessageTypeFlags _lastPartialMessageType = MessageTypeFlags.NoDestinationMessage;

        /// <summary>
        /// Holds the last incomplete code reply
        /// </summary>
        private string _lastPartialMessage;

        /// <summary>
        /// Process a firmware code reply
        /// </summary>
        /// <param name="code">Destination code</param>
        /// <param name="flags">Reply flags</param>
        /// <param name="reply">Reply</param>
        /// <returns>Whether the code has finished</returns>
        private async Task<bool> HandleCodeReply(Code code, MessageTypeFlags flags, string reply)
        {
            if (!string.IsNullOrEmpty(_lastPartialMessage))
            {
                // Deal with incomplete replies
                reply = _lastPartialMessage + reply;
                flags |= _lastPartialMessageType & ~MessageTypeFlags.PushFlag;
                _lastPartialMessageType = MessageTypeFlags.NoDestinationMessage;
                _lastPartialMessage = null;
            }

            if (flags.HasFlag(MessageTypeFlags.PushFlag))
            {
                // Code reply is not complete yet
                _lastPartialMessageType |= flags;
                _lastPartialMessage = reply;
                return false;
            }

            if (code != null)
            {
                // Make sure the code has a code reply...
                if (code.Result == null)
                {
                    code.Result = new CodeResult();
                }

                // Code reply is complete, resolve the code
                MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                            : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                            : MessageType.Success;
                code.Result.Add(type, reply.TrimEnd());
                code.FirmwareTCS.SetResult(null);
            }
            else
            {
                // Final output from a system macro
                MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                            : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                            : MessageType.Success;
                await Model.Provider.Output(type, reply);
            }
            return true;
        }

        /// <summary>
        /// Wait for a message to be acknowledged
        /// </summary>
        public void WaitForAcknowledgement()
        {
            // Message box requests are not meant for comment codes, resolve them separately
            ResolveCommentCodes();

            // Figure out which code requested the message box
            if (!CurrentState.WaitingForAcknowledgement)
            {
                _logger.Debug("Waiting for acknowledgement");

                Code startCode = null;
                if (BufferedCodes.Count > 0)
                {
                    startCode = BufferedCodes[0];
                    BytesBuffered -= startCode.BinarySize;
                    BufferedCodes.RemoveAt(0);
                }

                State newState = Push();
                newState.StartCode = startCode;
                newState.WaitingForAcknowledgement = true;
                _isWaitingForAcknowledgement = true;
            }
        }

        /// <summary>
        /// Called when RepRapFirmware has closed the last macro file internally
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public async Task MacroFileClosed()
        {
            Code startCode = CurrentState.StartCode;
            if (startCode != null)
            {
                _logger.Debug("==> Unfinished starting code: {0}", startCode);

                // Concatenate output from nested macro codes
                using (await CurrentState.Macro.LockAsync())
                {
                    if (CurrentState.StartCode.Result == null)
                    {
                        CurrentState.StartCode.Result = CurrentState.Macro.Result;
                    }
                    else if (!CurrentState.Macro.Result.IsEmpty)
                    {
                        CurrentState.StartCode.Result.AddRange(CurrentState.Macro.Result);
                    }
                    CurrentState.Macro.Result = new CodeResult();
                }

                // Code has not finished yet, need a separate response for it
                BytesBuffered += startCode.BinarySize;
                BufferedCodes.Insert(0, startCode);
            }

            CurrentState.StartCode = null;
            await Pop();
        }

        /// <summary>
        /// Called when a message has been acknowledged
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public async Task MessageAcknowledged()
        {
            if (CurrentState.WaitingForAcknowledgement)
            {
                _logger.Debug("Message acknowledged");

                Code startCode = CurrentState.StartCode;
                if (startCode != null)
                {
                    BufferedCodes.Insert(0, CurrentState.StartCode);
                    BytesBuffered += CurrentState.StartCode.BinarySize;
                    CurrentState.StartCode = null;
                }

                await Pop();
                if (startCode != null)
                {
                    _logger.Debug("==> Unfinished starting code: {0}", startCode);
                }
            }
            else
            {
                _logger.Error("Tried to acknowledge a message, but no acknowledgement is requested!");
            }
        }

        /// <summary>
        /// Attempt to start a file macro
        /// </summary>
        /// <param name="fileName">Name of the macro file</param>
        /// <param name="fromCode">Request comes from a real G/M/T-code</param>
        /// <returns>Asynchronous task</returns>
        public async Task DoMacroFile(string fileName, bool fromCode)
        {
            // Macro requests are not meant for comment codes, resolve them separately
            ResolveCommentCodes();

            // Figure out which code started the macro file
            Code startCode = null;
            if (fromCode)
            {
                if (CurrentState.MacroCompleted)
                {
                    _logger.Info("Finished intermediate macro file {0}", CurrentState.Macro.FileName);
                    startCode = CurrentState.StartCode;
                    if (startCode != null)
                    {
                        if (startCode.Result == null)
                        {
                            startCode.Result = CurrentState.Macro.Result;
                        }
                        else if (!CurrentState.Macro.Result.IsEmpty)
                        {
                            startCode.Result.AddRange(CurrentState.Macro.Result);
                        }
                    }
                    CurrentState.StartCode = null;     // don't add it back to the buffered codes because it's about to be pushed on the stack again
                    await Pop();
                }
                else if (BufferedCodes.Count > 0)
                {
                    startCode = BufferedCodes[0];
                    BytesBuffered -= startCode.BinarySize;
                    BufferedCodes.RemoveAt(0);
                }
            }
            else if (Stack.Count > 1)
            {
                _logger.Warn("System macro {0} is requested but the stack is not empty. Discarding request.", fileName);
                DataTransfer.WriteMacroCompleted(Channel, true);
                return;
            }

            // Try to locate the macro file and start it
            string physicalFile = await FilePath.ToPhysicalAsync(fileName, FileDirectory.System);
            State newState = Push();
            newState.StartCode = startCode;
            newState.Macro = new Macro(fileName, physicalFile, Channel, startCode != null, (startCode != null) ? startCode.SourceConnection : 0);
            if (startCode != null)
            {
                _logger.Debug("==> Starting code {0}", startCode);
            }
        }

        /// <summary>
        /// Invalidate buffered and regular codes + requests
        /// </summary>
        public void InvalidateRegular()
        {
            foreach (Code bufferedCode in BufferedCodes)
            {
                bufferedCode.FirmwareTCS.SetCanceled();
            }
            BufferedCodes.Clear();
            BytesBuffered = 0;

            foreach (State state in Stack)
            {
                if (!state.WaitingForAcknowledgement && state.Macro == null)
                {
                    while (state.LockRequests.TryDequeue(out LockRequest lockRequest))
                    {
                        lockRequest.Resolve(false);
                    }

                    while (state.SuspendedCodes.TryDequeue(out Code suspendedCode))
                    {
                        suspendedCode.FirmwareTCS.SetCanceled();
                    }

                    while (state.PendingCodes.TryDequeue(out Code pendingCode))
                    {
                        pendingCode.FirmwareTCS.SetCanceled();
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
        public async Task<bool> Invalidate()
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

                while (CurrentState.SuspendedCodes.TryDequeue(out Code suspendedCode))
                {
                    suspendedCode.FirmwareTCS.SetCanceled();
                    resourceInvalidated = true;
                }

                // Macro files and their start codes are disposed by Pop()
                resourceInvalidated |= (CurrentState.Macro != null);

                while (CurrentState.PendingCodes.TryDequeue(out Code pendingCode))
                {
                    pendingCode.FirmwareTCS.SetCanceled();
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
                await Pop();
            }
            while (true);

            // Clear codes being processed
            foreach (Code bufferedCode in BufferedCodes)
            {
                bufferedCode.FirmwareTCS.SetCanceled();
                resourceInvalidated = true;
            }
            BufferedCodes.Clear();
            BytesBuffered = 0;

            // Clear codes that are still pending but have not been fed into the SPI interface yet
            Code.CancelPending(Channel);

            // Done
            return resourceInvalidated;
        }
    }
}
