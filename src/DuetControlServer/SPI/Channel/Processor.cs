using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
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
    /// <remarks>
    /// This class should be merged with Codes.Pipelines.Firmware at some point
    /// </remarks>
    public sealed class Processor
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

            BaseState = CurrentState = new State(Codes.Processor.GetFirmwareState(channel));
            Stack.Push(CurrentState);
        }

        /// <summary>
        /// Lock used when accessing this instance
        /// </summary>
        private readonly AsyncLock _lock = new();

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
        private bool _allFilesAborted;

        /// <summary>
        /// Stack of the different channel states
        /// </summary>
        public Stack<State> Stack { get; } = new();

        /// <summary>
        /// First item on the stack
        /// </summary>
        public State BaseState { get; }

        /// <summary>
        /// Get the current state from the stack
        /// </summary>
        public State CurrentState { get; private set; }

        /// <summary>
        /// Push a new state on the stack
        /// </summary>
        /// <param name="macro">Optional macro</param>
        /// <returns>New state</returns>
        public State Push(CodeFile? file = null)
        {
            // Push a new element on the stack. Also record if the motion system was active in case it's changed
            bool msActive;
            using (Model.Provider.AccessReadOnly())
            {
                msActive = Model.Provider.Get.Inputs[Channel]?.Active == true;
            }
            State state = new(Codes.Processor.Push(Channel, file), msActive);

            // Dequeue already suspended codes first so the correct order is maintained
            Queue<Code> alreadySuspendedCodes = new(CurrentState.SuspendedCodes.Count);
            while (CurrentState.SuspendedCodes.TryDequeue(out Code? suspendedCode))
            {
                alreadySuspendedCodes.Enqueue(suspendedCode);
            }

            // Suspend the already buffered codes
            foreach (Code bufferedCode in BufferedCodes)
            {
                _logger.Debug("Suspending code {0}", bufferedCode);
                CurrentState.SuspendedCodes.Enqueue(bufferedCode);
            }
            BytesBuffered = 0;
            BufferedCodes.Clear();

            // Add back any codes that were previously suspended
            while (alreadySuspendedCodes.TryDequeue(out Code? suspendedCode))
            {
                CurrentState.SuspendedCodes.Enqueue(suspendedCode);
            }

            // Done
            Stack.Push(state);
            CurrentState = state;
            return state;
        }

        /// <summary>
        /// Pop the last state from the stack
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public void Pop()
        {
            // There must be at least one item on the stack...
            if (Stack.Count == 1)
            {
                throw new InvalidOperationException($"Stack underrun on channel {Channel}");
            }

            // Pop the stack
            Codes.Processor.Pop(Channel);
            State oldState = Stack.Pop();
            CurrentState = Stack.Peek();

            // Restore message box and motion system states
            _isWaitingForAcknowledgment = CurrentState.WaitingForAcknowledgement;
            using (Model.Provider.AccessReadOnly())
            {
                if (Model.Provider.Get.Inputs[Channel] is not null)
                {
                    Model.Provider.Get.Inputs[Channel]!.Active = oldState.MotionSystemWasActive;
                }
            }

            // Invalidate obsolete lock requests and supended codes
            while (oldState.LockRequests.TryDequeue(out LockRequest? lockRequest))
            {
                lockRequest.Resolve(false);
            }

            while (oldState.SuspendedCodes.TryDequeue(out Code? suspendedCode))
            {
                Codes.Processor.CancelCode(suspendedCode);
            }

            // Deal with macro files
            if (oldState.File is MacroFile macro)
            {
                using (macro.Lock())
                {
                    if (macro.IsExecuting)
                    {
                        if (!macro.IsAborted)
                        {
                            _logger.Warn("Aborting orphaned macro file {0}", macro.FileName);
                            macro.Abort();
                        }
                    }
                    else
                    {
                        if (Channel != CodeChannel.Daemon)
                        {
                            _logger.Debug("Disposing macro file {0}", macro.FileName);
                        }
                        else
                        {
                            _logger.Trace("Disposing macro file {0}", macro.FileName);
                        }
                        macro.Dispose();
                    }
                }
            }

            // Invalidate macro start codes, pending codes, and flush requests
            if (oldState.StartCode is not null)
            {
                _logger.Warn("==> Cancelling unfinished starting code: {0}", oldState.StartCode);
                Codes.Processor.CancelCode(oldState.StartCode);
            }

            while (oldState.PendingCodes.Reader.TryRead(out Code? pendingCode))
            {
                pendingCode.Stage = Codes.PipelineStage.Firmware;
                Codes.Processor.CancelCode(pendingCode);
            }

            while (oldState.FlushRequests.TryDequeue(out TaskCompletionSource<bool>? source))
            {
                source.SetResult(false);
            }
            oldState.SetBusy(false);
        }

        /// <summary>
        /// Pop the last state from the stack
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public async Task PopAsync()
        {
            // There must be at least one item on the stack...
            if (Stack.Count == 1)
            {
                throw new InvalidOperationException($"Stack underrun on channel {Channel}");
            }

            // Pop the stack
            Codes.Processor.Pop(Channel);
            State oldState = Stack.Pop();
            CurrentState = Stack.Peek();
            _isWaitingForAcknowledgment = CurrentState.WaitingForAcknowledgement;

            // Invalidate obsolete lock requests and supended codes
            while (oldState.LockRequests.TryDequeue(out LockRequest? lockRequest))
            {
                lockRequest.Resolve(false);
            }

            while (oldState.SuspendedCodes.TryDequeue(out Code? suspendedCode))
            {
                Codes.Processor.CancelCode(suspendedCode);
            }

            // Deal with macro files
            if (oldState.File is MacroFile macro)
            {
                using (await macro.LockAsync())
                {
                    if (macro.IsExecuting)
                    {
                        if (!macro.IsAborted)
                        {
                            _logger.Warn("Aborting orphaned macro file {0}", macro.FileName);
                            macro.Abort();
                        }
                    }
                    else
                    {
                        if (Channel != CodeChannel.Daemon)
                        {
                            _logger.Debug("Disposing macro file {0}", macro.FileName);
                        }
                        else
                        {
                            _logger.Trace("Disposing macro file {0}", macro.FileName);
                        }
                        macro.Dispose();
                    }
                }
            }

            // Invalidate macro start codes, pending codes, and flush requests
            if (oldState.StartCode is not null)
            {
                _logger.Warn("==> Cancelling unfinished starting code: {0}", oldState.StartCode);
                Codes.Processor.CancelCode(oldState.StartCode);
            }

            while (oldState.PendingCodes.Reader.TryRead(out Code? pendingCode))
            {
                pendingCode.Stage = Codes.PipelineStage.Firmware;
                Codes.Processor.CancelCode(pendingCode);
            }

            while (oldState.FlushRequests.TryDequeue(out TaskCompletionSource<bool>? source))
            {
                source.SetResult(false);
            }
            oldState.SetBusy(false);
        }

        /// <summary>
        /// Block file macro calls if the state is being copied
        /// </summary>
        private static readonly List<MacroFile> _macrosToStart = new();

        /// <summary>
        /// Copy the state from another channel processor
        /// </summary>
        /// <param name="from">Source</param>
        /// <returns>Asynchronous task</returns>
        public void CopyState(Processor from)
        {
            if (Stack.Count != 1)
            {
                throw new ArgumentException("Cannot copy state because the stack is not empty");
            }

            // Create macro/state copies but don't start the macros yet. Some may need to wait before they can start execution
            State baseItem = from.Stack.Last();
            foreach (State item in from.Stack.Reverse())
            {
                if (item != baseItem)
                {
                    if (item.File is MacroFile macro)
                    {
                        MacroFile copy = new(macro, Channel);
                        Push(copy);
                        lock (_macrosToStart)
                        {
                            _macrosToStart.Add(copy);
                        }
                    }
                    else
                    {
                        Push();
                        CurrentState.WaitingForAcknowledgement = item.WaitingForAcknowledgement;
                    }
                    CurrentState.MotionSystemWasActive = !item.MotionSystemWasActive;
                }
            }
        }

        /// <summary>
        /// Start copied macros. This must happen later to avoid race conditions
        /// </summary>
        public static void StartCopiedMacros()
        {
            lock (_macrosToStart)
            {
                foreach (MacroFile file in _macrosToStart)
                {
                    file.Start(false);
                }
                _macrosToStart.Clear();
            }
        }

        /// <summary>
        /// List of buffered G/M/T-codes that are being processed by the firmware
        /// </summary>
        public List<Code> BufferedCodes { get; } = new();

        /// <summary>
        /// Occupied space for buffered codes in bytes
        /// </summary>
        public int BytesBuffered { get; private set; }

        /// <summary>
        /// Stack of code replies for codes that pushed the stack (e.g. macro files or blocking messages)
        /// </summary>
        public Stack<Tuple<MessageTypeFlags, string>> PendingReplies { get; } = new();

        /// <summary>
        /// Write channel diagnostics to the given string builder
        /// </summary>
        /// <param name="builder">Target to write to</param>
        /// <returns>Asynchronous task</returns>
        public async Task Diagnostics(StringBuilder builder)
        {
            StringBuilder channelDiagostics = new();

            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);
            IDisposable? lockObject = null;
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
                channelDiagostics.AppendLine($"Buffered codes: {BytesBuffered} bytes total");
            }

            string prefix = ">";
            foreach (State state in Stack.Reverse())
            {
                if (state.WaitingForAcknowledgement)
                {
                    channelDiagostics.AppendLine($"{prefix} Waiting for acknowledgement, requested by {((state.StartCode is null) ? "system" : state.StartCode.ToString())}");
                }
                if (state.LockRequests.Count > 0)
                {
                    channelDiagostics.AppendLine($"{prefix} Number of lock/unlock requests: {state.LockRequests.Count(item => item.IsLockRequest)}/{state.LockRequests.Count(item => !item.IsLockRequest)}");
                }
                if (state.File is MacroFile macro)
                {
                    channelDiagostics.AppendLine($"{prefix} {(macro.IsExecuting ? "Doing" : "Finishing")} macro {state.File.FileName}, started by {((state.StartCode is null) ? "system" : state.StartCode.ToString())}");
                }
                foreach (Code suspendedCode in state.SuspendedCodes)
                {
                    channelDiagostics.AppendLine($"{prefix} Suspended code: {suspendedCode}");
                }
                if (state.FlushRequests.Count > 0)
                {
                    channelDiagostics.AppendLine($"{prefix} Number of flush requests: {state.FlushRequests.Count}");
                }
                prefix += '>';
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
        public bool IsWaitingForAcknowledgment => _isWaitingForAcknowledgment;
        private volatile bool _isWaitingForAcknowledgment;

        /// <summary>
        /// Get a flush task
        /// </summary>
        /// <param name="state">Stack item</param>
        /// <returns>Asynchronous task</returns>
        private Task<bool> GetFlushTask(State state)
        {
            // Check if we can resolve the flush request immediately if nothing is being done
            if (state == CurrentState &&
                BufferedCodes.Count == 0 && state.LockRequests.Count == 0 && !_allFilesAborted &&
                (state.File is not MacroFile macro || (!macro.JustStarted && macro.IsExecuting)) && !state.MacroCompleted &&
                state.SuspendedCodes.Count == 0 && !state.PendingCodes.Reader.TryPeek(out _))
            {
                return Task.FromResult(true);
            }

            // Need to wait for the SPI connector to finish other operations first
            TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            state.FlushRequests.Enqueue(tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Flush pending codes and return true on success or false on failure.
        /// This method may be deprecated; in theory it should suffice to flush the pipeline only (with stricter Busy conditions)
        /// </summary>
        /// <param name="file">Optional code file for the flush target</param>
        /// <returns>Whether the codes could be flushed</returns>
        public Task<bool> FlushAsync(CodeFile? file = null)
        {
            // Need to find the correct state for a flush request first.
            // Generic flush requests are not meant for temporary macro states
            foreach (State state in Stack)
            {
                if ((state.File == file) || (file is null && (state.File is not MacroFile macro || !macro.WasStarted || macro.IsExecuting) && !state.MacroCompleted))
                {
                    return GetFlushTask(state);
                }
            }

            // Fallback, should not happen
            _logger.Warn("Failed to find suitable stack level for flush request, falling back to current one");
            return GetFlushTask(CurrentState);
        }

        /// <summary>
        /// Flush all pending codes and return true on success or false on failure.
        /// This method may be deprecated; in theory it should suffice to flush the pipeline only (with stricter Busy conditions)
        /// </summary>
        /// <returns>Whether the codes could be flushed</returns>
        public Task<bool> FlushAllAsync() => GetFlushTask(BaseState);

        /// <summary>
        /// Lock all movement systems and wait for standstill
        /// </summary>
        /// <returns>Whether the movement systems could be locked</returns>
        public Task<bool> LockAllMovementSystemsAndWaitForStandstill()
        {
            LockRequest request = new(true);
            CurrentState.LockRequests.Enqueue(request);
            return request.Task;
        }

        /// <summary>
        /// Unlock all resources occupied by the given channel
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public Task UnlockAll()
        {
            LockRequest request = new(false);
            CurrentState.LockRequests.Enqueue(request);
            return request.Task;
        }

        /// <summary>
        /// Flag the currently executing macro file as (not) pausable
        /// </summary>
        /// <param name="isPausable">Whether the macro is pausable or not</param>
        /// <returns>Asynchronous task</returns>
        public async Task SetMacroPausable(bool isPausable)
        {
            if (CurrentState.File is MacroFile macro)
            {
                using (await macro.LockAsync())
                {
                    macro.IsPausable = isPausable;
                }
            }
        }

        /// <summary>
        /// Called when the last or all files have been aborted by the firmware
        /// </summary>
        /// <param name="abortAll">Whether to abort all files</param>
        public void FilesAborted(bool abortAll)
        {
            bool macroAborted = false;

            // If only the last macro is aborted, we may have a pending reply for e.g. M99
            if (!abortAll)
            {
                ResolvePendingReplies();
            }

            // Clean up the stack
            Code? startCode = null;
            while (CurrentState.WaitingForAcknowledgement || CurrentState.File is MacroFile)
            {
                if (CurrentState.StartCode is not null)
                {
                    startCode = CurrentState.StartCode;
                    CurrentState.StartCode = null;
                }

                if (CurrentState.File is MacroFile macro)
                {
                    using (macro.Lock())
                    {
                        if (startCode is not null && abortAll)
                        {
                            // Wait for the macro to be fully cancelled and then cancel the code that started it
                            _ = macro.WaitForFinishAsync().ContinueWith(async task =>
                            {
                                try
                                {
                                    await task;
                                }
                                finally
                                {
                                    Codes.Processor.CodeCompleted(startCode);
                                }
                            }, TaskContinuationOptions.RunContinuationsAsynchronously);
                        }

                        // Abort the macro file
                        macro.Abort();
                    }
                    macroAborted = true;
                }
                else if (startCode is not null)
                {
                    // This is a message prompt. Cancel the code that started it
                    Codes.Processor.CancelCode(startCode);
                    startCode = null;
                }

                // Pop the stack
                Pop();
                if (startCode is not null && abortAll)
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
                    if (bufferedCode != startCode)
                    {
                        Codes.Processor.CancelCode(bufferedCode);
                    }
                }
                BufferedCodes.Clear();
                BytesBuffered = 0;

                // If only the last file was closed (e.g. from M99), carry on with the execution of the code that started it
                if (startCode is not null)
                {
                    BytesBuffered += startCode.BinarySize;
                    BufferedCodes.Insert(0, startCode);
                    _logger.Debug("==> Resuming unfinished starting code: {0}", startCode);
                }
            }

            // Abort the file print if necessary
            if ((Channel is CodeChannel.File or CodeChannel.File2) && (abortAll || !macroAborted))
            {
                using (JobProcessor.Lock())
                {
                    JobProcessor.Abort();
                }
            }
        }

        /// <summary>
        /// Abort all files asynchronously
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public async Task AbortAllFilesAsync()
        {
            // Clean up the stack
            Code? startCode = null;
            while (CurrentState.WaitingForAcknowledgement || CurrentState.File is MacroFile)
            {
                if (CurrentState.StartCode is not null)
                {
                    startCode = CurrentState.StartCode;
                    CurrentState.StartCode = null;
                }

                if (CurrentState.File is MacroFile macro)
                {
                    using (await macro.LockAsync())
                    {
                        // Resolve potential start codes when the macro file finishes
                        if (startCode is not null)
                        {
                            _ = macro.WaitForFinishAsync().ContinueWith(async task =>
                            {
                                try
                                {
                                    await task;
                                }
                                finally
                                {
                                    Codes.Processor.CodeCompleted(startCode);
                                }
                            }, TaskContinuationOptions.RunContinuationsAsynchronously);
                        }

                        // Abort the macro file
                        macro.Abort();
                    }
                }
                else if (startCode is not null)
                {
                    // Cancel the code that started the blocking message prompt
                    Codes.Processor.CancelCode(startCode);
                    startCode = null;
                }

                // Pop the stack
                await PopAsync();
                if (startCode is not null)
                {
                    _logger.Debug("==> Unfinished starting code: {0}", startCode);
                }
            }

            // Cancel pending codes and requests
            _allFilesAborted = (DataTransfer.ProtocolVersion >= 3);
            InvalidateRegular();

            // Abort the job files if necessary
            if (Channel is CodeChannel.File or CodeChannel.File2)
            {
                using (await JobProcessor.LockAsync())
                {
                    JobProcessor.Abort();
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
                if (state.LockRequests.TryDequeue(out LockRequest? item))
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
                Code code = BufferedCodes[0];
                BytesBuffered -= code.BinarySize;
                BufferedCodes.RemoveAt(0);

                code.Result = new Message();
                Codes.Processor.CodeCompleted(code);
            }
        }

        /// <summary>
        /// Process code replies that could not be interpreted immediately
        /// </summary>
        private void ResolvePendingReplies()
        {
            while (BufferedCodes.Count > 0 && PendingReplies.TryPop(out Tuple<MessageTypeFlags, string>? reply))
            {
                HandleReply(reply.Item1, reply.Item2);
            }
        }

        /// <summary>
        /// Process pending requests on this channel
        /// </summary>
        public void Run()
        {
            // 1. Whole line comments and pending replies
            ResolveCommentCodes();
            ResolvePendingReplies();

            // 2. Lock/Unlock requests
            if (CurrentState.LockRequests.TryPeek(out LockRequest? lockRequest))
            {
                if (lockRequest.IsLockRequest)
                {
                    if (!lockRequest.IsLockRequested && DataTransfer.WriteLockAllMovementSystemsAndWaitForStandstill(Channel))
                    {
                        lockRequest.IsLockRequested = true;
                    }
                    return;
                }

                if (DataTransfer.WriteUnlock(Channel))
                {
                    lockRequest.Resolve(true);
                    CurrentState.LockRequests.Dequeue();
                    // Resources unlocked; carry on
                }
                else
                {
                    return;
                }
            }

            // 3. Abort requests
            if (_allFilesAborted)
            {
                _allFilesAborted = !DataTransfer.WriteInvalidateChannel(Channel);
                return;
            }

            // 4. Macro files (must come before any other code unless the stack state is being cloned)
            if (CurrentState.File is MacroFile || CurrentState.MacroError)
            {
                // Tell RRF as quickly as possible about the new macro being started
                if (CurrentState.File is MacroFile macro && macro.JustStarted)
                {
                    macro.JustStarted = (DataTransfer.ProtocolVersion >= 3) && !DataTransfer.WriteMacroStarted(Channel);
                    return;
                }

                // Check if the macro file has finished
                if (CurrentState.File is MacroFile { WasStarted: true, IsExecuting: false } || CurrentState.MacroError)
                {
                    if (!CurrentState.MacroCompleted && DataTransfer.WriteMacroCompleted(Channel, CurrentState.MacroError))
                    {
                        CurrentState.MacroCompleted = true;
                        if (DataTransfer.ProtocolVersion >= 3)
                        {
                            if (CurrentState.MacroError)
                            {
                                // In newer protocol versions we don't expect a response because RRF will be waiting in a semaphore
                                Code? startCode = CurrentState.StartCode;
                                if (startCode is not null)
                                {
                                    BytesBuffered += startCode.BinarySize;
                                    BufferedCodes.Insert(0, startCode);
                                    CurrentState.StartCode = null;
                                    ResolvePendingReplies();
                                }

                                // Macro has finished, pop the stack
                                Pop();
                                if (startCode is not null)
                                {
                                    _logger.Debug("==> Unfinished starting code: {0}", startCode);
                                }
                            }
                        }
                        else
                        {
                            // Wait for a response first if an older firmware version is used, then pop the stack
                            return;
                        }
                    }
                    else
                    {
                        // Still waiting for acknowledgement or failed to write macro complete message, try again ASAP
                        return;
                    }
                }
            }

            // 5. Suspended codes being resumed (may include priority and macro codes)
            while (CurrentState.SuspendedCodes.TryPeek(out Code? suspendedCode))
            {
                if (BufferCode(suspendedCode))
                {
                    _logger.Debug("-> Resumed suspended code");
                    CurrentState.SuspendedCodes.Dequeue();
                }
                else
                {
                    return;
                }
            }

            // 6. Pending codes
            while (CurrentState.PendingCodes.Reader.TryPeek(out Code? pendingCode))
            {
                if (BufferCode(pendingCode))
                {
                    CurrentState.PendingCodes.Reader.TryRead(out _);
                }
                else
                {
                    return;
                }
            }

            // 7. Flush requests
            if (BufferedCodes.Count == 0)
            {
                if (CurrentState.FlushRequests.TryDequeue(out TaskCompletionSource<bool>? flushRequest))
                {
                    flushRequest.SetResult(true);
                    return;
                }
                CurrentState.SetBusy(false);
            }

            // Log untracked code replies
            while (PendingReplies.TryPop(out Tuple<MessageTypeFlags, string>? reply))
            {
                _logger.Warn("Pending out-of-order reply: '{0}'", reply.Item2);
            }
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
                Code codeObj = new(code) { Channel = Channel, Flags = CodeFlags.IsFromFirmware | CodeFlags.IsLastCode };
                _ = codeObj.Execute().ContinueWith(async task =>
                {
                    try
                    {
                        Message? result = await task;
                        if (result is null)
                        {
                            return;
                        }

                        // Check what kind of message this is
                        MessageTypeFlags flags = (MessageTypeFlags)(1 << (int)Channel);
                        if (result.Type != MessageType.Success)
                        {
                            flags |= (result.Type == MessageType.Error) ? MessageTypeFlags.ErrorMessageFlag : MessageTypeFlags.WarningMessageFlag;
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
                    catch (OperationCanceledException)
                    {
                        // Code has been cancelled. Don't log this
                    }
                    catch (AggregateException ae)
                    {
                        await Logger.LogOutputAsync(MessageType.Error, $"Failed to execute {code} from firmware: [{ae.InnerException!.GetType().Name}] {ae.InnerException.Message}");
                        _logger.Warn(ae);
                    }
                    catch (Exception e)
                    {
                        await Logger.LogOutputAsync(MessageType.Error, $"Failed to execute {code} from firmware: [{e.GetType().Name}] {e.Message}");
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
            try
            {
                // Figure out how much space this code needs
                if (pendingCode.Stage != Codes.PipelineStage.Firmware)
                {
                    pendingCode.BinarySize = Communication.Consts.BufferedCodeHeaderSize + DataTransfer.GetCodeSize(pendingCode);
                    pendingCode.Stage = Codes.PipelineStage.Firmware;
                }

                // Don't send cancelled codes to the firmware
                if (pendingCode.CancellationToken.IsCancellationRequested)
                {
                    Codes.Processor.CancelCode(pendingCode);
                    return true;
                }

                // Try to send it to RepRapFirmware
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
                Codes.Processor.CancelCode(pendingCode, e);
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
        public bool HandleReply(MessageTypeFlags flags, string reply)
        {
            // Replies are not meant for comment codes, resolve them separately
            ResolveCommentCodes();

            // Deal with codes being executed
            if (BufferedCodes.Count > 0)
            {
                int codeSize = BufferedCodes[0].BinarySize;
                if (HandleCodeReply(BufferedCodes[0], flags, reply))
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
                    MacroFileClosed();
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
                    MessageAcknowledged();
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
        private string? _lastPartialMessage;

        /// <summary>
        /// Process a firmware code reply
        /// </summary>
        /// <param name="code">Destination code</param>
        /// <param name="flags">Reply flags</param>
        /// <param name="reply">Reply</param>
        /// <returns>Whether the code has finished</returns>
        private bool HandleCodeReply(Code code, MessageTypeFlags flags, string reply)
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

            if (code is not null)
            {
                // Code reply is complete, resolve the code
                MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                            : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                            : MessageType.Success;
                if (code.Result is null)
                {
                    code.Result = new Message(type, reply);
                }
                else
                {
                    code.Result.Append(type, reply);
                }
                Codes.Processor.CodeCompleted(code);
            }
            else
            {
                // Final output from a system macro
                MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                            : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                            : MessageType.Success;
                Model.Provider.Output(type, reply);
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

                Code? startCode = null;
                if (BufferedCodes.Count > 0)
                {
                    startCode = BufferedCodes[0];
                    startCode.UpdateNextFilePosition();
                    BytesBuffered -= startCode.BinarySize;
                    BufferedCodes.RemoveAt(0);
                }

                State newState = Push();
                newState.StartCode = startCode;
                newState.WaitingForAcknowledgement = true;
                _isWaitingForAcknowledgment = true;
            }
        }

        /// <summary>
        /// Called when RepRapFirmware has closed the last macro file internally
        /// </summary>
        public void MacroFileClosed()
        {
            Code? startCode = CurrentState.StartCode;
            if (startCode is not null)
            {
                _logger.Debug("==> Unfinished starting code: {0}", startCode);

                // Code has not finished yet, need a separate response for it
                BytesBuffered += startCode.BinarySize;
                BufferedCodes.Insert(0, startCode);
                CurrentState.StartCode = null;
                ResolvePendingReplies();
            }

            Pop();
        }

        /// <summary>
        /// Called when a message has been acknowledged
        /// </summary>
        public void MessageAcknowledged()
        {
            if (CurrentState.WaitingForAcknowledgement)
            {
                _logger.Debug("Message acknowledged");

                Code? startCode = CurrentState.StartCode;
                if (startCode is not null)
                {
                    BytesBuffered += CurrentState.StartCode!.BinarySize;
                    BufferedCodes.Insert(0, CurrentState.StartCode);
                    CurrentState.StartCode = null;
                    ResolvePendingReplies();
                }

                Pop();
                if (startCode is not null)
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
        public void DoMacroFile(string fileName, bool fromCode)
        {
            // Macro requests are not meant for comment codes, resolve them separately
            ResolveCommentCodes();

            // Cannot start system macro if something is still busy
            if (!fromCode && Stack.Count > 1)
            {
                _logger.Warn("System macro {0} is requested but the stack is not empty. Discarding request.", fileName);
                DataTransfer.WriteMacroCompleted(Channel, true);
                return;
            }

            // Figure out which code started the macro file
            Code? startCode = null;
            if (fromCode)
            {
                if (CurrentState.MacroCompleted)
                {
                    _logger.Info("Finished intermediate macro file {0}", CurrentState.File!.FileName);
                    startCode = CurrentState.StartCode;
                    CurrentState.StartCode = null;     // don't add it back to the buffered codes because it's about to be pushed on the stack again
                    Pop();
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

            // Try to locate the macro file
            string physicalFile = FilePath.ToPhysical(fileName, FileDirectory.System);
            MacroFile? macro = MacroFile.Open(fileName, physicalFile, Channel, startCode, startCode?.SourceConnection ?? 0);

            State newState = Push(macro);
            newState.StartCode = startCode;
            if (macro is not null)
            {
                // Start it
                if (startCode is not null)
                {
                    startCode.UpdateNextFilePosition();
                    _logger.Debug("==> Starting code {0}", startCode);
                }
                macro.Start();
            }
            else
            {
                // Report back to RRF that the file could not be opened
                newState.MacroError = true;
            }
        }

        /// <summary>
        /// Called when the print has been paused on the file channel
        /// </summary>
        public void PrintPaused()
        {
            // Invalidate pending requests
            InvalidateRegular();

            // Clear macros. When we get here, RRF has done the same
            while (CurrentState.File is MacroFile)
            {
                Pop();
            }

            // Invalidate everything else
            InvalidateRegular();
        }

        /// <summary>
        /// Invalidate buffered and regular codes + requests
        /// </summary>
        public void InvalidateRegular()
        {
            foreach (Code bufferedCode in BufferedCodes)
            {
                Codes.Processor.CancelCode(bufferedCode);
            }
            BufferedCodes.Clear();
            BytesBuffered = 0;
            _suppressEmptyReply = true;

            foreach (State state in Stack)
            {
                if (!state.WaitingForAcknowledgement && (state.File is not MacroFile macro || macro.IsPausable))
                {
                    while (state.LockRequests.TryDequeue(out LockRequest? lockRequest))
                    {
                        lockRequest.Resolve(false);
                    }

                    while (state.SuspendedCodes.TryDequeue(out Code? suspendedCode))
                    {
                        Codes.Processor.CancelCode(suspendedCode);
                    }

                    while (state.PendingCodes.Reader.TryRead(out Code? pendingCode))
                    {
                        pendingCode.Stage = Codes.PipelineStage.Firmware;
                        Codes.Processor.CancelCode(pendingCode);
                    }

                    while (state.FlushRequests.TryDequeue(out TaskCompletionSource<bool>? source))
                    {
                        source.SetResult(false);
                    }
                    state.SetBusy(false);
                }
            }
        }

        /// <summary>
        /// Invalidate every request and buffered code on this channel
        /// </summary>
        /// <returns>If any resource has been invalidated</returns>
        public void Invalidate()
        {
            // Invalidate the stack
            do
            {
                while (CurrentState.LockRequests.TryDequeue(out LockRequest? lockRequest))
                {
                    lockRequest.Resolve(false);
                }

                while (CurrentState.SuspendedCodes.TryDequeue(out Code? suspendedCode))
                {
                    Codes.Processor.CancelCode(suspendedCode);
                }

                while (CurrentState.PendingCodes.Reader.TryRead(out Code? pendingCode))
                {
                    pendingCode.Stage = Codes.PipelineStage.Firmware;
                    Codes.Processor.CancelCode(pendingCode);
                }

                while (CurrentState.FlushRequests.TryDequeue(out TaskCompletionSource<bool>? source))
                {
                    source.SetResult(false);
                }
                CurrentState.SetBusy(false);

                if (Stack.Count == 1)
                {
                    break;
                }
                Pop();
            }
            while (true);

            // Clear codes being processed
            foreach (Code bufferedCode in BufferedCodes)
            {
                Codes.Processor.CancelCode(bufferedCode);
            }
            BufferedCodes.Clear();
            BytesBuffered = 0;
            _suppressEmptyReply = true;

            // Clear codes that are still pending but have not been fed into the SPI interface yet
            Code.CancelPending(Channel);
            _allFilesAborted = false;
        }
    }
}
