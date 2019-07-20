using DuetAPI;
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
        /// Constructor of a code channel descriptor
        /// </summary>
        /// <param name="channel">Code channel of this instance</param>
        public ChannelInformation(CodeChannel channel)
        {
            Channel = channel;
        }

        /// <summary>
        /// What code channel this class is about
        /// </summary>
        public CodeChannel Channel { get; }

        /// <summary>
        /// Lock used when accessing this instance
        /// </summary>
        private readonly AsyncLock _lock = new AsyncLock();

        /// <summary>
        /// Queue of pending G/M/T-codes that have not been buffered yet
        /// </summary>
        public readonly Queue<QueuedCode> PendingCodes = new Queue<QueuedCode>();

        /// <summary>
        /// Occupied space for buffered codes in bytes
        /// </summary>
        public int BytesBuffered { get; set; }

        /// <summary>
        /// List of buffered G/M/T-codes that are being processed by the firmware
        /// </summary>
        public readonly List<QueuedCode> BufferedCodes = new List<QueuedCode>();

        /// <summary>
        /// Stack of suspended G/M/T-codes to resend when the current macro file finishes
        /// </summary>
        public readonly Stack<Queue<QueuedCode>> SuspendedCodes = new Stack<Queue<QueuedCode>>();

        /// <summary>
        /// Indicates whether the requested system macro file has finished
        /// </summary>
        public bool SystemMacroHasFinished = false;

        /// <summary>
        /// Stack of nested macro files being executed
        /// </summary>
        public readonly Stack<MacroFile> NestedMacros = new Stack<MacroFile>();

        /// <summary>
        /// Pending codes being started by a nested macro (and multiple codes may be started by an interceptor).
        /// This is required because it may take a moment until they are internally processed
        /// </summary>
        public readonly Queue<QueuedCode> NestedMacroCodes = new Queue<QueuedCode>();

        /// <summary>
        /// Queue of pending lock/unlock requests
        /// </summary>
        public readonly Queue<QueuedLockRequest> PendingLockRequests = new Queue<QueuedLockRequest>();

        /// <summary>
        /// Queue of pending flush requests
        /// </summary>
        public readonly Queue<TaskCompletionSource<object>> PendingFlushRequests = new Queue<TaskCompletionSource<object>>();

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
                channelDiagostics.AppendLine($"=> {BytesBuffered} bytes");
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
                channelDiagostics.AppendLine($"Nested macro: {macroFile.FileName}, started by: {((macroFile.StartCode == null) ? "n/a" : macroFile.StartCode.ToString())}");
            }

            foreach (QueuedCode nestedMacroCode in NestedMacroCodes)
            {
                channelDiagostics.AppendLine($"Nested macro code: {nestedMacroCode.Code}");
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
        /// Indicates if this code channel is currently idle
        /// </summary>
        public bool IsIdle
        {
            get => (PendingCodes.Count == 0) && (BufferedCodes.Count == 0) && (SuspendedCodes.Count == 0) &&
                (NestedMacros.Count == 0) &&
                (PendingLockRequests.Count == 0);
        }

        /// <summary>
        /// Process pending requests on this channel
        /// </summary>
        /// <returns>Whether another call to this method is permitted</returns>
        public bool ProcessRequests()
        {
            // The order is: Suspended codes > nested macro codes > next nested macro code > pending code > (un-)lock requests > flush requests
            if (_resumingBuffer)
            {
                // Resume codes that were temporarily suspended. This is the case when a nested macro returns
                ResumeBuffer();
                return _resumingBuffer;
            }
            else if (NestedMacroCodes.TryPeek(out QueuedCode queuedSystemCode) &&
                (queuedSystemCode.Code.Type == CodeType.Comment || queuedSystemCode.IsFinished || (queuedSystemCode.IsReadyToSend && Interface.BufferCode(queuedSystemCode))))
            {
                // Check if the current system code has finished internally or if it can be buffered for RRF
                NestedMacroCodes.Dequeue();
                return true;
            }
            else if (NestedMacros.TryPeek(out MacroFile macro))
            {
                // Try to read the next real code from the system macro being executed
                Commands.Code code = null;
                if (!macro.IsFinished && NestedMacroCodes.Count < Settings.BufferedMacroCodes)
                {
                    code = macro.ReadCode();
                }

                // If there is any, start executing it in the background. An interceptor may also generate extra codes
                if (code != null)
                {
                    // Note that Code.Enqueue is not used here to avoid potential deadlocks that would occur when
                    // firmware calls (e.g. load heightmap) are made while the code is being enqueued
                    NestedMacroCodes.Enqueue(new QueuedCode(code));
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
                            // FIXME: Should this terminate the macro being executed?
                            Console.WriteLine($"[err] {code} -> {ae.InnerException.Message}");
                        }
                    });

                    return true;
                }

                // Macro file is complete if no more codes can be read from the file and the buffered codes are completely gone
                if (macro.IsFinished  && !NestedMacroCodes.TryPeek(out _) && BufferedCodes.Count == 0 &&
                    ((macro.StartCode != null && macro.StartCode.DoingNestedMacro) || (macro.StartCode == null && !SystemMacroHasFinished)) &&
                    MacroCompleted(macro.StartCode, macro.IsAborted))
                {
                    if (macro.StartCode == null)
                    {
                        SystemMacroHasFinished = true;
                    }
                    Console.WriteLine($"[info] {(macro.IsAborted ? "Aborted" : "Finished")} macro file '{Path.GetFileName(macro.FileName)}'");
                }
            }
            else if (PendingCodes.TryPeek(out QueuedCode queuedCode) && Interface.BufferCode(queuedCode))
            {
                // Execute another regular code in RepRapFirmware. It is already pre- and postprocessed at this point
                PendingCodes.Dequeue();
                return true;
            }
            else if (BufferedCodes.Count == 0)
            {
                // No code is being executed on this channel
                if (PendingLockRequests.TryPeek(out QueuedLockRequest lockRequest))
                {
                    // Deal with pending lock/unlock requests
                    if (!lockRequest.IsLockRequested)
                    {
                        if (lockRequest.IsLockRequest)
                        {
                            lockRequest.IsLockRequested = DataTransfer.WriteLockMovementAndWaitForStandstill(Channel);
                        }
                        else if (DataTransfer.WriteUnlock(Channel))
                        {
                            PendingLockRequests.Dequeue();
                        }
                    }
                }
                else
                {
                    // If there is no pending lock/unlock request, this code channel is idle
                    if (PendingFlushRequests.TryDequeue(out TaskCompletionSource<object> source))
                    {
                        source.SetResult(null);
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Handle a G-code reply
        /// </summary>
        /// <param name="flags">Message flags</param>
        /// <param name="reply">Code reply</param>
        /// <returns>Whether the reply could be processed</returns>
        public bool HandleReply(MessageTypeFlags flags, string reply)
        {
            if (NestedMacros.TryPeek(out MacroFile macroFile) &&
                ((macroFile.StartCode != null && !macroFile.StartCode.DoingNestedMacro) || (macroFile.StartCode == null && SystemMacroHasFinished)))
            {
                if (macroFile.StartCode != null)
                {
                    macroFile.StartCode.HandleReply(flags, reply);
                    if (macroFile.IsFinished)
                    {
                        NestedMacros.Pop();
                        Console.WriteLine($"[info] Completed macro + start code {macroFile.StartCode}");
                    }
                }
                else if (!flags.HasFlag(MessageTypeFlags.PushFlag))
                {
                    NestedMacros.Pop();
                    SystemMacroHasFinished = false;
                    Console.WriteLine($"[info] Completed system macro");
                }
                return true;
            }

            if (BufferedCodes.Count > 0)
            {
                BufferedCodes[0].HandleReply(flags, reply);
                if (BufferedCodes[0].IsFinished)
                {
                    Console.WriteLine($"[info] Completed {BufferedCodes[0].Code}");
                    BytesBuffered -= BufferedCodes[0].BinarySize;
                    BufferedCodes.RemoveAt(0);
                }
                return true;
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
                }
                else if (BufferedCodes.Count > 0)
                {
                    // The top buffered code is the one that requested the macro file
                    startingCode = BufferedCodes[0];
                }

                if (startingCode != null)
                {
                    startingCode.DoingNestedMacro = true;
                }
            }

            // Locate the macro file
            string path = await FilePath.ToPhysical(filename, "sys");
            if (!File.Exists(path))
            {
                if (filename == MacroFile.ConfigFile)
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
                    }
                }
                else if (reportMissing)
                {
                    await Model.Provider.Output(MessageType.Error, $"Could not find macro file {filename}");
                }
                else
                {
                    Console.WriteLine($"[info] Optional macro file '{filename}' not found");
                }

                SuspendBuffer(startingCode);
                MacroCompleted(startingCode, true);
                return;
            }

            // Open the file
            try
            {
                MacroFile macro = new MacroFile(path, Channel, startingCode);
                NestedMacros.Push(macro);
            }
            catch (Exception e)
            {
                await Model.Provider.Output(MessageType.Error, $"Failed to open macro file '{filename}': {e.Message}");

                SuspendBuffer(startingCode);
                MacroCompleted(startingCode, true);
                return;
            }

            // Macro file is now running. At this point, the buffered codes have been thrown away by RRF
            SuspendBuffer();
        }

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
        public void InvalidateBuffer()
        {
            foreach (QueuedCode queuedCode in BufferedCodes)
            {
                if (!queuedCode.IsFinished)
                {
                    queuedCode.SetFinished();
                }
            }

            BytesBuffered = 0;
            BufferedCodes.Clear();

            while (SuspendedCodes.TryPop(out Queue<QueuedCode> suspendedCodes))
            {
                while (suspendedCodes.TryDequeue(out QueuedCode suspendedCode))
                {
                    suspendedCode.SetFinished();
                }
            }
            _resumingBuffer = SystemMacroHasFinished = false;
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
                        Console.WriteLine($"Suspending code {bufferedCode}");
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
        }

        /// <summary>
        /// Indicates if the suspended codes are being resumed
        /// </summary>
        private bool _resumingBuffer;

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
                    if (Interface.BufferCode(suspendedCode))
                    {
                        Console.WriteLine($"-> Resumed suspended code");
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
        /// <param name="codeErrorMessage">Error message to output</param>
        public void Invalidate(string codeErrorMessage)
        {
            while (SuspendedCodes.TryPop(out Queue<QueuedCode> suspendedCodes))
            {
                while (suspendedCodes.TryDequeue(out QueuedCode queuedCode))
                {
                    queuedCode.HandleReply(Communication.MessageTypeFlags.ErrorMessageFlag, codeErrorMessage);
                }
            }
            _resumingBuffer = false;

            while (NestedMacroCodes.TryDequeue(out QueuedCode item))
            {
                item.HandleReply(Communication.MessageTypeFlags.ErrorMessageFlag, codeErrorMessage);
            }

            while (NestedMacros.TryPop(out MacroFile macroFile))
            {
                if (macroFile.StartCode != null)
                {
                    macroFile.StartCode.HandleReply(Communication.MessageTypeFlags.ErrorMessageFlag, codeErrorMessage);
                }
            }

            while (PendingCodes.TryDequeue(out QueuedCode queuedCode))
            {
                queuedCode.HandleReply(Communication.MessageTypeFlags.ErrorMessageFlag, codeErrorMessage);
            }

            while (PendingLockRequests.TryDequeue(out QueuedLockRequest item))
            {
                item.Resolve(false);
            }

            while (PendingFlushRequests.TryDequeue(out TaskCompletionSource<object> source))
            {
                source.SetException(new OperationCanceledException(codeErrorMessage));
            }

            foreach (QueuedCode bufferedCode in BufferedCodes)
            {
                bufferedCode.HandleReply(Communication.MessageTypeFlags.ErrorMessageFlag, codeErrorMessage);
            }

            BytesBuffered = 0;
            BufferedCodes.Clear();
        }
    }

    /// <summary>
    /// Class used to hold internal information about all the code channels
    /// </summary>
    public class ChannelStore
    {
        private readonly ChannelInformation[] _channels;

        /// <summary>
        /// Constructor of the channel store
        /// </summary>
        public ChannelStore()
        {
            CodeChannel[] channels = (CodeChannel[])Enum.GetValues(typeof(CodeChannel));

            _channels = new ChannelInformation[channels.Length];
            foreach (CodeChannel channel in channels)
            {
                this[channel] = new ChannelInformation(channel);
            }
        }

        /// <summary>
        /// Index operator for easy access via a CodeChannel value
        /// </summary>
        /// <param name="channel">Channel to retrieve information abotu</param>
        /// <returns>Information about the code channel</returns>
        public ChannelInformation this[CodeChannel channel]
        {
            get => _channels[(int)channel];
            set => _channels[(int)channel] = value;
        }
    }
}
