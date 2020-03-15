﻿using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.FileExecution;
using DuetControlServer.SPI.Communication;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// Class used to hold internal information about a single code channel
    /// </summary>
    public class ChannelInformation
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
        /// Constructor of a code channel descriptor
        /// </summary>
        /// <param name="channel">Code channel of this instance</param>
        public ChannelInformation(CodeChannel channel)
        {
            Channel = channel;

            _logger = NLog.LogManager.GetLogger(channel.ToString());
        }

        /// <summary>
        /// Indicates if this channel is blocked until the next full transfer
        /// </summary>
        public bool IsBlocked { get; set; }

        /// <summary>
        /// Prioritised codes that override every other code
        /// </summary>
        public Queue<QueuedCode> PriorityCodes { get; } = new Queue<QueuedCode>();

        /// <summary>
        /// Queue of pending G/M/T-codes that have not been buffered yet
        /// </summary>
        public Queue<QueuedCode> PendingCodes { get; } = new Queue<QueuedCode>();

        /// <summary>
        /// Queue of pending flush requests
        /// </summary>
        public Queue<TaskCompletionSource<bool>> PendingFlushRequests { get; } = new Queue<TaskCompletionSource<bool>>();

        /// <summary>
        /// Occupied space for buffered codes in bytes
        /// </summary>
        public int BytesBuffered { get; private set; }

        /// <summary>
        /// List of buffered G/M/T-codes that are being processed by the firmware
        /// </summary>
        public List<QueuedCode> BufferedCodes { get; } = new List<QueuedCode>();

        /// <summary>
        /// Stack of suspended G/M/T-codes to resend when the current macro file finishes
        /// </summary>
        public Stack<Queue<QueuedCode>> SuspendedCodes { get; } = new Stack<Queue<QueuedCode>>();

        /// <summary>
        /// Indicates whether the requested system macro file has finished
        /// </summary>
        public bool SystemMacroHasFinished { get; private set; }

        /// <summary>
        /// Indicates if the last requested sytem macro file could not be opened
        /// </summary>
        public bool SystemMacroHadError { get; private set; }

        /// <summary>
        /// Stack of nested macro files being executed
        /// </summary>
        public Stack<MacroFile> NestedMacros { get; } = new Stack<MacroFile>();

        /// <summary>
        /// Queue of pending lock/unlock requests
        /// </summary>
        public Queue<QueuedLockRequest> PendingLockRequests { get; } = new Queue<QueuedLockRequest>();

        /// <summary>
        /// Lock used when accessing this instance
        /// </summary>
        private readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Lock access to this code channel
        /// </summary>
        /// <returns>Disposable lock</returns>
        public IDisposable Lock() => _lock.Lock();

        /// <summary>
        /// Lock access to this code channel asynchronously
        /// </summary>
        /// <returns>Disposable lock</returns>
        public AwaitableDisposable<IDisposable> LockAsync() => _lock.LockAsync();

        /// <summary>
        /// Write channel diagnostics to the given string builder
        /// </summary>
        /// <param name="builder">Target to write to</param>
        public void Diagnostics(StringBuilder builder)
        {
            StringBuilder channelDiagostics = new StringBuilder();

            foreach (QueuedCode pendingCode in PendingCodes)
            {
                channelDiagostics.AppendLine($"Pending code: {pendingCode.Code}");
            }

            foreach (QueuedCode bufferedCode in BufferedCodes)
            {
                channelDiagostics.AppendLine($"Buffered code: {bufferedCode.Code}");
            }
            if (BytesBuffered != 0)
            {
                channelDiagostics.AppendLine($"==> {BytesBuffered} bytes");
            }

            foreach (Queue<QueuedCode> suspendedCodes in SuspendedCodes)
            {
                channelDiagostics.AppendLine("> Suspended code level");
                foreach (QueuedCode suspendedCode in suspendedCodes)
                {
                    channelDiagostics.AppendLine($"Suspended code: {suspendedCode.Code}");
                }
            }

            foreach (MacroFile macroFile in NestedMacros)
            {
                channelDiagostics.AppendLine($"Nested macro: {macroFile.FileName}, started by: {((macroFile.StartCode == null) ? "system" : macroFile.StartCode.ToString())}");
                foreach (QueuedCode nestedMacroCode in macroFile.PendingCodes)
                {
                    channelDiagostics.AppendLine($"==> Pending code: {nestedMacroCode.Code}");
                }
            }

            if (PendingLockRequests.Count > 0)
            {
                channelDiagostics.AppendLine($"Number of lock/unlock requests: {PendingLockRequests.Count(item => item.IsLockRequest)}/{PendingLockRequests.Count(item => !item.IsLockRequest)}");
            }

            if (PendingFlushRequests.Count > 0)
            {
                channelDiagostics.AppendLine($"Number of flush requests: {PendingFlushRequests.Count}");
            }

            if (channelDiagostics.Length != 0)
            {
                builder.AppendLine($"{Channel}:");
                builder.Append(channelDiagostics);
            }
        }

        /// <summary>
        /// Process pending requests on this channel
        /// </summary>
        /// <returns>If anything more can be done on this channel</returns>
        public bool ProcessRequests()
        {
            // 1. Lock/Unlock requests
            if (PendingLockRequests.TryPeek(out QueuedLockRequest lockRequest))
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
                    PendingLockRequests.Dequeue();
                }
                return false;
            }

            // 2. Suspended codes being resumed (may include priority and macro codes)
            if (_resumingBuffer)
            {
                ResumeBuffer();
                return false;
            }

            // 3. Priority codes
            if (PriorityCodes.TryPeek(out QueuedCode queuedCode))
            {
                if (BufferCode(queuedCode))
                {
                    PriorityCodes.Dequeue();
                    return true;
                }
                return false;
            }

            // 4. Macros
            if (NestedMacros.TryPeek(out MacroFile macroFile))
            {
                // Fill up the macro code buffer
                Commands.Code code = null;
                if (macroFile.PendingCodes.Count < Settings.BufferedMacroCodes)
                {
                    try
                    {
                        code = macroFile.ReadCode();
                    }
                    catch (Exception e)
                    {
                        _logger.Error(e, "Failed to read code from macro file {0}", Path.GetFileName(macroFile.FileName));
                    }
                }

                if (code != null)
                {
                    // Start the next code in the background. An interceptor may also generate extra codes
                    queuedCode = new QueuedCode(code);
                    macroFile.PendingCodes.Enqueue(queuedCode);
                    _ = code.Execute().ContinueWith(async task =>
                    {
                        try
                        {
                            CodeResult result = await task;
                            if (!queuedCode.IsReadyToSend)
                            {
                                // Macro codes need special treatment because they may complete before they are actually
                                // sent to RepRapFirmware. Remove them from the NestedMacroCodes again in this case
                                queuedCode.HandleReply(result);
                            }

                            if (macroFile.StartCode == null)
                            {
                                await Utility.Logger.LogOutput(result);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Something has gone wrong and the SPI connector has invalidated everything - don't deal with this (yet?)
                        }
                        catch (Exception e)
                        {
                            if (e is AggregateException ae)
                            {
                                e = ae.InnerException;
                            }
                            await Utility.Logger.LogOutput(MessageType.Error, $"Failed to execute {code.ToShortString()}: [{e.GetType().Name}] {e.Message}");
                        }
                    });
                    return true;
                }

                if (macroFile.PendingCodes.TryPeek(out queuedCode))
                {
                    // Check if another code has finished
                    if (queuedCode.IsFinished || (queuedCode.IsReadyToSend && BufferCode(queuedCode)))
                    {
                        macroFile.PendingCodes.Dequeue();
                        return true;
                    }

                    // Take care of macro flush requests
                    if (queuedCode.Code.WaitingForFlush && macroFile.PendingFlushRequests.TryDequeue(out TaskCompletionSource<bool> macroFlushRequest))
                    {
                        queuedCode.Code.WaitingForFlush = false;
                        macroFlushRequest.TrySetResult(true);
                        return false;
                    }
                }
                else if (macroFile.IsFinished && BufferedCodes.Count == 0)
                {
                    // Take care of remaining macro flush requests
                    if (macroFile.PendingFlushRequests.TryDequeue(out TaskCompletionSource<bool> macroFlushRequest))
                    {
                        macroFlushRequest.TrySetResult(true);
                        return false;
                    }

                    // When the last code from the macro has been processed, notify RRF about the completed file
                    if (((macroFile.StartCode != null && macroFile.StartCode.DoingNestedMacro) || (macroFile.StartCode == null && !SystemMacroHasFinished)) &&
                        MacroCompleted(macroFile.StartCode, macroFile.IsAborted))
                    {
                        if (macroFile.StartCode == null)
                        {
                            SystemMacroHasFinished = true;
                        }

                        if (macroFile.IsAborted)
                        {
                            _logger.Info("Aborted macro file {0}", Path.GetFileName(macroFile.FileName));
                        }
                        else
                        {
                            _logger.Debug("Finished macro file {0}", Path.GetFileName(macroFile.FileName));
                        }
                    }
                }

                // Don't execute regular requests until the last macro file has finished
                return false;
            }

            // 5. Regular codes
            if (PendingCodes.TryPeek(out queuedCode))
            {
                if (BufferCode(queuedCode))
                {
                    PendingCodes.Dequeue();
                    return true;
                }
                return false;
            }

            // 6. Flush requests
            if (BufferedCodes.Count == 0 && PendingFlushRequests.TryDequeue(out TaskCompletionSource<bool> flushRequest))
            {
                flushRequest.SetResult(true);
                return false;
            }

            // End
            return false;
        }

        /// <summary>
        /// Store an enqueued code for transmission to RepRapFirmware
        /// </summary>
        /// <param name="queuedCode">Code to transfer</param>
        /// <returns>True if the code could be buffered</returns>
        private bool BufferCode(QueuedCode queuedCode)
        {
            try
            {
                if (Interface.BufferCode(queuedCode, out int codeLength))
                {
                    BytesBuffered += codeLength;
                    BufferedCodes.Add(queuedCode);
                    _logger.Debug("Sent {0}, remaining space {1}, needed {2}", queuedCode.Code, Settings.MaxBufferSpacePerChannel - BytesBuffered, codeLength);
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Failed to buffer code {0}", queuedCode.Code);
                queuedCode.SetException(e);
                return true;
            }
        }

        /// <summary>
        /// Partial log message that has not been printed yet
        /// </summary>
        private string _partialLogMessage;

        /// <summary>
        /// Handle a G-code reply
        /// </summary>
        /// <param name="flags">Message flags</param>
        /// <param name="reply">Code reply</param>
        /// <returns>Whether the reply could be processed</returns>
        public bool HandleReply(MessageTypeFlags flags, string reply)
        {
            if (flags.HasFlag(MessageTypeFlags.LogMessage))
            {
                _partialLogMessage += reply;
                if (!flags.HasFlag(MessageTypeFlags.PushFlag))
                {
                    if (!string.IsNullOrWhiteSpace(_partialLogMessage))
                    {
                        MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                                            : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                                                : MessageType.Success;
                        Utility.Logger.Log(type, _partialLogMessage);
                    }
                    _partialLogMessage = null;
                }
            }

            if (SystemMacroHadError)
            {
                SystemMacroHadError = false;
                return true;
            }

            if (NestedMacros.TryPeek(out MacroFile macroFile))
            {
                if ((macroFile.StartCode != null && !macroFile.StartCode.DoingNestedMacro) || (macroFile.StartCode == null && SystemMacroHasFinished))
                {
                    if (macroFile.StartCode != null)
                    {
                        macroFile.StartCode.HandleReply(flags, reply);
                        if (macroFile.IsFinished)
                        {
                            NestedMacros.Pop().Dispose();
                            _logger.Info("Finished macro file {0}", Path.GetFileName(macroFile.FileName));
                            if (macroFile.StartCode != null)
                            {
                                _logger.Debug("==> Starting code: {0}", macroFile.StartCode);
                            }
                        }
                    }
                    else if (!flags.HasFlag(MessageTypeFlags.PushFlag))
                    {
                        NestedMacros.Pop().Dispose();
                        SystemMacroHasFinished = false;
                        _logger.Info("Finished system macro file {0}", Path.GetFileName(macroFile.FileName));
                    }
                    return true;
                }

                if (macroFile.StartCode != null)
                {
                    macroFile.StartCode.HandleReply(flags, reply);
                }
            }

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

            // Replies from the code queue or a final empty response from the file are expected
            if (Channel != CodeChannel.CodeQueue && Channel != CodeChannel.File)
            {
                _logger.Warn("Out-of-order reply: '{0}'", reply);
            }
            return false;
        }

        /// <summary>
        /// Attempt to start a file macro
        /// </summary>
        /// <param name="filename">Name of the macro file</param>
        /// <param name="reportMissing">Report an error if the file could not be found</param>
        /// <param name="fromCode">Request comes from a real G/M/T-code</param>
        /// <returns>Asynchronous task</returns>
        public async Task HandleMacroRequest(string filename, bool reportMissing, bool fromCode)
        {
            // Get the code starting the macro file
            QueuedCode startingCode = null;
            if (fromCode)
            {
                if (NestedMacros.TryPeek(out MacroFile macroFile) && macroFile.StartCode != null && !macroFile.StartCode.DoingNestedMacro)
                {
                    // In case a G/M/T-code invokes more than one macro file...
                    startingCode = macroFile.StartCode;

                    // Check if the other macro file has been finished
                    if (macroFile.IsFinished)
                    {
                        NestedMacros.Pop().Dispose();
                        _logger.Info("Finished intermediate macro file {0}", Path.GetFileName(macroFile.FileName));
                    }
                }
                else if (BufferedCodes.Count > 0)
                {
                    // The top buffered code is the one that requested the macro file
                    startingCode = BufferedCodes[0];
                }

                if (startingCode != null)
                {
                    startingCode.DoingNestedMacro = true;

                    // FIXME This work-around will not be needed any more when the SBC interface has got its own task in RRF
                    if ((filename == "stop.g" || filename == "sleep.g") && startingCode.Code.CancellingPrint)
                    {
                        string cancelFile = await FilePath.ToPhysicalAsync("cancel.g", FileDirectory.System);
                        if (File.Exists(cancelFile))
                        {
                            // Execute cancel.g instead of stop.g if it exists
                            filename = "cancel.g";
                        }
                    }
                }
            }

            // Locate the macro file
            string physicalFile = await FilePath.ToPhysicalAsync(filename, FileDirectory.System);
            if (!File.Exists(physicalFile))
            {
                bool found = false;
                if (filename == FilePath.ConfigFile)
                {
                    physicalFile = await FilePath.ToPhysicalAsync(FilePath.ConfigFileFallback, FileDirectory.System);
                    if (File.Exists(physicalFile))
                    {
                        // Use config.b.bak if config.g cannot be found
                        found = true;
                        _logger.Warn("Using fallback file {0} because {1} could not be found", FilePath.ConfigFileFallback, FilePath.ConfigFile);
                    }
                    else
                    {
                        // No configuration file found
                        await Utility.Logger.LogOutput(MessageType.Error, $"Macro files {FilePath.ConfigFile} and {FilePath.ConfigFileFallback} not found");
                    }
                }
                else if (reportMissing)
                {
                    if (!fromCode || BufferedCodes.Count == 0 || BufferedCodes[0].Code.Type != CodeType.MCode || BufferedCodes[0].Code.MajorNumber != 98)
                    {
                        // M98 outputs its own warning message via RRF
                        await Utility.Logger.LogOutput(MessageType.Error, $"Macro file {filename} not found");
                    }
                }
                else if (FilePath.DeployProbePattern.IsMatch(filename))
                {
                    physicalFile = await FilePath.ToPhysicalAsync(FilePath.DeployProbeFallbackFile, FileDirectory.System);
                    if (File.Exists(physicalFile))
                    {
                        found = true;
                        _logger.Info($"Using fallback file {FilePath.DeployProbeFallbackFile} because {filename} could not be found");
                    }
                    else
                    {
                        // No deployprobe file found
                        _logger.Info($"Optional macro files {filename} and {FilePath.DeployProbeFallbackFile} not found");
                    }
                }
                else if (FilePath.RetractProbePattern.IsMatch(filename))
                {
                    physicalFile = await FilePath.ToPhysicalAsync(FilePath.RetractProbeFallbackFile, FileDirectory.System);
                    if (File.Exists(physicalFile))
                    {
                        found = true;
                        _logger.Info($"Using fallback file {FilePath.RetractProbeFallbackFile} because {filename} could not be found");
                    }
                    else
                    {
                        // No retractprobe file found
                        _logger.Info($"Optional macro files {filename} and {FilePath.RetractProbeFallbackFile} not found");
                    }
                }
                else if (filename != FilePath.DaemonFile)
                {
                    _logger.Info("Optional macro file {0} not found", filename);
                }
                else
                {
                    _logger.Trace("Optional macro file {0} not found", filename);
                }

                if (!found)
                {
                    SystemMacroHadError = startingCode == null;
                    SuspendBuffer(startingCode);
                    MacroCompleted(startingCode, true);
                    return;
                }
            }

            // Open the file
            try
            {
                MacroFile macro = new MacroFile(physicalFile, Channel, startingCode);
                NestedMacros.Push(macro);
            }
            catch (Exception e)
            {
                await Utility.Logger.LogOutput(MessageType.Error, $"Failed to open macro file '{filename}': {e.Message}");

                SystemMacroHadError = startingCode == null;
                SuspendBuffer(startingCode);
                MacroCompleted(startingCode, true);
                return;
            }

            // Macro file is now running. At this point, the buffered codes have been thrown away by RRF
            SuspendBuffer();
        }

        /// <summary>
        /// Insert a new code for execution before pending macro codes
        /// </summary>
        /// <param name="code">Queued code to insert</param>
        /// <exception cref="ArgumentException">No macro file is being executed</exception>
        public void InsertMacroCode(QueuedCode code)
        {
            if (NestedMacros.TryPeek(out MacroFile macroFile))
            {
                Queue<QueuedCode> pendingMacroCodes = new Queue<QueuedCode>();

                // Keep the order of already processed codes
                while (macroFile.PendingCodes.TryPeek(out QueuedCode queuedCode) && queuedCode.IsReadyToSend)
                {
                    pendingMacroCodes.Enqueue(macroFile.PendingCodes.Dequeue());
                }

                // Then add the new code
                pendingMacroCodes.Enqueue(code);

                // And finally all others
                while (macroFile.PendingCodes.TryDequeue(out QueuedCode queuedCode))
                {
                    pendingMacroCodes.Enqueue(queuedCode);
                }

                // Apply the new queue
                while (pendingMacroCodes.TryDequeue(out QueuedCode queuedCode))
                {
                    macroFile.PendingCodes.Enqueue(queuedCode);
                }
            }
            else
            {
                throw new ArgumentException("No macro file being executed");
            }

        }

        /// <summary>
        /// Indicates if the suspended codes are being resumed
        /// </summary>
        private bool _resumingBuffer;

        /// <summary>
        /// Notify RepRapFirmware about a completed macro file
        /// </summary>
        /// <param name="startingCode">Code starting the macro</param>
        /// <param name="error">True if there was any error processing the file</param>
        /// <returns>Whether the notification could be sent</returns>
        private bool MacroCompleted(QueuedCode startingCode, bool error)
        {
            if (DataTransfer.WriteMacroCompleted(Channel, error))
            {
                _resumingBuffer = true;
                if (startingCode != null)
                {
                    startingCode.DoingNestedMacro = false;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Invalidate all the buffered G/M/T-codes
        /// </summary>
        /// <param name="invalidateLastFileCodes)">Invalidate only codes of the last stack level</param>
        public void InvalidateBuffer(bool invalidateLastFileCodes)
        {
            if (invalidateLastFileCodes)
            {
                // Only a G/M-code can ask for last file aborts. It is still being executed, so take care of it 
                for (int i = BufferedCodes.Count - 1; i > 0; i--)
                {
                    BytesBuffered -= BufferedCodes[i].BinarySize;
                    BufferedCodes.RemoveAt(i);
                }

                if (SuspendedCodes.TryPop(out Queue<QueuedCode> suspendedCodes))
                {
                    while (suspendedCodes.TryDequeue(out QueuedCode suspendedCode))
                    {
                        suspendedCode.SetCancelled();
                    }
                }
            }
            else
            {
                // Remove every buffered code of this channel if every code is being invalidated
                foreach (QueuedCode queuedCode in BufferedCodes)
                {
                    queuedCode.SetCancelled();
                }

                BytesBuffered = 0;
                BufferedCodes.Clear();

                while (SuspendedCodes.TryPop(out Queue<QueuedCode> suspendedCodes))
                {
                    while (suspendedCodes.TryDequeue(out QueuedCode suspendedCode))
                    {
                        suspendedCode.SetCancelled();
                    }
                }
            }

            _resumingBuffer = invalidateLastFileCodes;
            SystemMacroHasFinished = SystemMacroHadError = false;

            // Resolve pending flush requests. At this point, codes waiting for a flush must stop executing
            while (PendingFlushRequests.TryDequeue(out TaskCompletionSource<bool> source))
            {
                source.SetResult(false);
            }

            // Do not send codes to RRF until it has cleared its internal buffer
            IsBlocked = true;
        }

        /// <summary>
        /// Suspend all the buffered G/M/T-codes for future execution
        /// </summary>
        /// <param name="codeBeingExecuted">Current code being executed to leave in the buffer</param>
        public void SuspendBuffer(QueuedCode codeBeingExecuted = null)
        {
            Queue<QueuedCode> suspendedItems = new Queue<QueuedCode>();

            // Suspend the remaining buffered codes except for the one that is already being executed
            foreach (QueuedCode bufferedCode in BufferedCodes.ToList())
            {
                if (bufferedCode != codeBeingExecuted)
                {
                    if (!bufferedCode.DoingNestedMacro)
                    {
                        _logger.Debug("Suspending code {0}", bufferedCode);
                        bufferedCode.IsSuspended = true;
                        suspendedItems.Enqueue(bufferedCode);
                    }

                    BytesBuffered -= bufferedCode.BinarySize;
                    BufferedCodes.Remove(bufferedCode);
                }
            }

            // Deal with case of a nested macro being started while suspended codes are still being restored
            if (_resumingBuffer)
            {
                if (SuspendedCodes.TryPop(out Queue<QueuedCode> remainingSuspendedCodes))
                {
                    while (remainingSuspendedCodes.TryDequeue(out QueuedCode remainingCode))
                    {
                        suspendedItems.Enqueue(remainingCode);
                    }
                }
                _resumingBuffer = false;
            }

            // Enequeue the suspended codes so they can continue execution later on
            SuspendedCodes.Push(suspendedItems);

            // Do not send codes to RRF until it has cleared its internal buffer
            IsBlocked = true;
        }

        /// <summary>
        /// Resume suspended codes when a nested macro file has finished
        /// </summary>
        /// <returns>True when finished</returns>
        public void ResumeBuffer()
        {
            if (SuspendedCodes.TryPeek(out Queue<QueuedCode> suspendedCodes))
            {
                while (suspendedCodes.TryPeek(out QueuedCode suspendedCode))
                {
                    if (BufferCode(suspendedCode))
                    {
                        _logger.Debug("-> Resumed suspended code");
                        suspendedCode.IsSuspended = false;
                        suspendedCodes.Dequeue();
                    }
                    else
                    {
                        return;
                    }
                }
                SuspendedCodes.Pop();
            }
            _resumingBuffer = false;
        }

        /// <summary>
        /// Invalidate every request and buffered code on this channel
        /// </summary>
        /// <returns>If any resource has been invalidated</returns>
        public bool Invalidate()
        {
            bool resourceInvalidated = false;

            Commands.Code.CancelPending(Channel);

            while (SuspendedCodes.TryPop(out Queue<QueuedCode> suspendedCodes))
            {
                while (suspendedCodes.TryDequeue(out QueuedCode queuedCode))
                {
                    queuedCode.SetCancelled();
                    resourceInvalidated = true;
                }
            }
            _resumingBuffer = false;

            SystemMacroHasFinished = SystemMacroHadError = false;

            while (NestedMacros.TryPop(out MacroFile macroFile))
            {
                macroFile.StartCode?.SetCancelled();
                macroFile.Abort();
                macroFile.Dispose();
                resourceInvalidated = true;
            }

            while (PendingCodes.TryDequeue(out QueuedCode queuedCode))
            {
                queuedCode.SetCancelled();
                resourceInvalidated = true;
            }

            while (PendingLockRequests.TryDequeue(out QueuedLockRequest item))
            {
                item.Resolve(false);
                resourceInvalidated = true;
            }

            while (PendingFlushRequests.TryDequeue(out TaskCompletionSource<bool> source))
            {
                source.SetResult(false);
                resourceInvalidated = true;
            }

            foreach (QueuedCode bufferedCode in BufferedCodes)
            {
                bufferedCode.SetCancelled();
                resourceInvalidated = true;
            }
            BufferedCodes.Clear();

            IsBlocked = true;
            BytesBuffered = 0;

            return resourceInvalidated;
        }
    }
}
