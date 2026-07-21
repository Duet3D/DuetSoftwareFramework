using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Files;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Link.Requests;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Link.Channel;

/// <summary>
/// Class used to process data on a single code channel
/// </summary>
/// <remarks>
/// This class should be merged with Codes.Pipelines.Firmware at some point
/// </remarks>
public sealed class Processor
{
    /// <summary>
    /// What code channel this class is about
    /// </summary>
    public CodeChannel Channel { get; }

    // Private variables
    private readonly Commands.CommandFactory _commandFactory;
    private readonly CodeProcessor _codeProcessor;
    private readonly FilePathResolver _filePathResolver;
    private readonly ILinkAdapter _linkAdapter;
    private readonly LinkInterface _linkInterface;
    private readonly JobProcessor _jobProcessor;

    private readonly Model.ObjectModel _model;
    private readonly FileFactory _macroFileFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger _logger;
    private readonly Settings _settings;

    /// <summary>
    /// Constructor of a code channel processor
    /// </summary>
    /// <param name="channel">Code channel of this instance</param>
    /// <param name="commandFactory">Command factory</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="filePathResolver">File path resolver</param>
    /// <param name="linkAdapter">Link adapter</param>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="jobProcessor">Job processor</param>
    /// <param name="macroFileFactory">Macro file factory</param>
    /// <param name="model">Object model</param>
    /// <param name="lifetime">Host application lifetime</param>
    /// <param name="loggerFactory">Logger factory</param>
    /// <param name="settings">Settings</param>
    public Processor(
        CodeChannel channel,
        Commands.CommandFactory commandFactory,
        CodeProcessor codeProcessor,
        FilePathResolver filePathResolver,
        ILinkAdapter linkAdapter,
        LinkInterface linkInterface,
        JobProcessor jobProcessor,
        FileFactory macroFileFactory,
        Model.ObjectModel model,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        IOptions<Settings> settings)
    {
        Channel = channel;
        _commandFactory = commandFactory;
        _codeProcessor = codeProcessor;
        _filePathResolver = filePathResolver;
        _jobProcessor = jobProcessor;
        _linkAdapter = linkAdapter;
        _linkInterface = linkInterface;
        _macroFileFactory = macroFileFactory;
        _model = model;
        _lifetime = lifetime;
        _logger = loggerFactory.CreateLogger(channel.ToString());
        _settings = settings.Value;

        BaseState = CurrentState = new StackState(codeProcessor.GetFirmwareState(channel));
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
    public IDisposable Lock(CancellationToken cancellationToken = default) => _lock.Lock(cancellationToken);

    /// <summary>
    /// Lock access to this code channel asynchronously
    /// </summary>
    /// <returns>Disposable lock</returns>
    public AwaitableDisposable<IDisposable> LockAsync(CancellationToken cancellationToken = default) => _lock.LockAsync(cancellationToken);

    /// <summary>
    /// This is set to true if all the files have been aborted and RRF has to be notified
    /// </summary>
    private bool _allFilesAborted;

    /// <summary>
    /// Stack of the different channel states
    /// </summary>
    public Stack<StackState> Stack { get; } = new();

    /// <summary>
    /// First item on the stack
    /// </summary>
    public StackState BaseState { get; }

    /// <summary>
    /// Get the current state from the stack
    /// </summary>
    public StackState CurrentState { get; private set; }

    /// <summary>
    /// Push a new state on the stack
    /// </summary>
    /// <param name="file">Optional file being executed</param>
    /// <returns>New state</returns>
    public StackState Push(CodeFile? file = null)
    {
        // Push a new element on the stack. Also record if the motion system was active in case it's changed
        bool msActive;
        using (_model.AccessReadOnly())
        {
            msActive = _model.Inputs[Channel]?.Active == true;
        }
        StackState state = new(_codeProcessor.Push(Channel, file), msActive);

        // Dequeue already suspended codes first so the correct order is maintained
        Queue<Code> alreadySuspendedCodes = new(CurrentState.SuspendedCodes.Count);
        while (CurrentState.SuspendedCodes.TryDequeue(out Code? suspendedCode))
        {
            alreadySuspendedCodes.Enqueue(suspendedCode);
        }

        // Suspend the already buffered codes
        foreach (Code bufferedCode in BufferedCodes)
        {
            _logger.LogDebug("Suspending code {Code}", bufferedCode);
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
        _codeProcessor.Pop(Channel);
        StackState oldState = Stack.Pop();
        CurrentState = Stack.Peek();

        // Restore message box and motion system states
        _isWaitingForAcknowledgment = CurrentState.WaitingForAcknowledgement;
        using (_model.AccessReadWrite())
        {
            InputChannel? input = _model.Inputs[Channel];
            if (input is not null)
            {
                input.Active = oldState.MotionSystemWasActive;
            }
        }

        // Invalidate obsolete lock requests and supended codes
        while (oldState.LockRequests.TryDequeue(out LockMovementRequest? lockRequest))
        {
            lockRequest.Resolve(false);
        }

        while (oldState.SuspendedCodes.TryDequeue(out Code? suspendedCode))
        {
            _codeProcessor.CancelCode(suspendedCode);
        }

        // Deal with macro files
        if (oldState.File is MacroFile macro)
        {
            using (macro.Lock(_lifetime.ApplicationStopped))
            {
                if (macro.IsExecuting)
                {
                    if (!macro.IsAborted)
                    {
                        if (!_lifetime.ApplicationStopping.IsCancellationRequested)
                        {
                            _logger.LogWarning("Aborting orphaned macro file {File}", macro.FilePath.Virtual);
                        }
                        macro.Abort();
                    }
                }
                else
                {
                    if (Channel != CodeChannel.Daemon)
                    {
                        _logger.LogDebug("Disposing macro file {File}", macro.FilePath.Virtual);
                    }
                    else
                    {
                        _logger.LogTrace("Disposing macro file {File}", macro.FilePath.Virtual);
                    }
                    macro.Dispose();
                }
            }
        }

        // Invalidate macro start codes, pending codes, and flush requests
        if (oldState.StartCode is not null)
        {
            _logger.LogWarning("==> Cancelling unfinished starting code: {Code}", oldState.StartCode);
            _codeProcessor.CancelCode(oldState.StartCode);
        }

        while (oldState.PendingCodes.Reader.TryRead(out Code? pendingCode))
        {
            pendingCode.Stage = PipelineStage.Firmware;
            _codeProcessor.CancelCode(pendingCode);
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
        _codeProcessor.Pop(Channel);
        StackState oldState = Stack.Pop();
        CurrentState = Stack.Peek();

        // Restore message box and motion system states
        _isWaitingForAcknowledgment = CurrentState.WaitingForAcknowledgement;
        using (await _model.AccessReadWriteAsync())
        {
            InputChannel? input = _model.Inputs[Channel];
            if (input is not null)
            {
                input.Active = oldState.MotionSystemWasActive;
            }
        }

        // Invalidate obsolete lock requests and supended codes
        while (oldState.LockRequests.TryDequeue(out LockMovementRequest? lockRequest))
        {
            lockRequest.Resolve(false);
        }

        while (oldState.SuspendedCodes.TryDequeue(out Code? suspendedCode))
        {
            _codeProcessor.CancelCode(suspendedCode);
        }

        // Deal with macro files
        if (oldState.File is MacroFile macro)
        {
            using (await macro.LockAsync(_lifetime.ApplicationStopping))
            {
                if (macro.IsExecuting)
                {
                    if (!macro.IsAborted)
                    {
                        if (!_lifetime.ApplicationStopping.IsCancellationRequested)
                        {
                            _logger.LogWarning("Aborting orphaned macro file {File}", macro.FilePath.Virtual);
                        }
                        macro.Abort();
                    }
                }
                else
                {
                    if (Channel != CodeChannel.Daemon)
                    {
                        _logger.LogDebug("Disposing macro file {File}", macro.FilePath.Virtual);
                    }
                    else
                    {
                        _logger.LogTrace("Disposing macro file {File}", macro.FilePath.Virtual);
                    }
                    macro.Dispose();
                }
            }
        }

        // Invalidate macro start codes, pending codes, and flush requests
        if (oldState.StartCode is not null)
        {
            _logger.LogWarning("==> Cancelling unfinished starting code: {Code}", oldState.StartCode);
            _codeProcessor.CancelCode(oldState.StartCode);
        }

        while (oldState.PendingCodes.Reader.TryRead(out Code? pendingCode))
        {
            pendingCode.Stage = PipelineStage.Firmware;
            _codeProcessor.CancelCode(pendingCode);
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
    private static readonly List<MacroFile> _macrosToStart = [];

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
        StackState baseItem = from.Stack.Last();
        foreach (StackState item in from.Stack.Reverse())
        {
            if (item != baseItem)
            {
                if (item.File is MacroFile macro)
                {
                    MacroFile copy = _macroFileFactory.CreateMacro(macro, Channel);
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
    public List<Code> BufferedCodes { get; } = [];

    /// <summary>
    /// Occupied space for buffered codes in bytes
    /// </summary>
    public int BytesBuffered { get; private set; }

    /// <summary>
    /// Queue of code replies for codes that pushed the stack (e.g. macro files or blocking messages).
    /// Replies must be consumed in arrival order, else multi-chunk replies are reassembled wrongly
    /// </summary>
    public Queue<Tuple<MessageTypeFlags, string>> PendingReplies { get; } = new();

    /// <summary>
    /// Print diagnostics of this class
    /// </summary>
    /// <param name="builder">String builder to print to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async ValueTask PrintDiagnosticsAsync(StringBuilder builder, CancellationToken cancellationToken)
    {
        StringBuilder channelDiagostics = new();

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
        foreach (StackState state in Stack.Reverse())
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
                channelDiagostics.AppendLine($"{prefix} {(macro.IsExecuting ? "Doing" : "Finishing")} macro {state.File.FilePath.Virtual}, started by {((state.StartCode is null) ? "system" : state.StartCode.ToString())}");
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
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private ValueTask<bool> GetFlushTask(StackState state, CancellationToken cancellationToken = default)
    {
        // Check if we can resolve the flush request immediately if nothing is being done
        if (state == CurrentState &&
            BufferedCodes.Count == 0 && state.LockRequests.Count == 0 && !_allFilesAborted &&
            (state.File is not MacroFile macro || (!macro.JustStarted && macro.IsExecuting)) && !state.MacroCompleted &&
            state.SuspendedCodes.Count == 0 && !state.PendingCodes.Reader.TryPeek(out _))
        {
            return ValueTask.FromResult(true);
        }

        // Need to wait for the SPI connector to finish other operations first
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.FlushRequests.Enqueue(tcs);
        return new ValueTask<bool>(tcs.Task.WaitAsync(cancellationToken));
    }

    /// <summary>
    /// Flush pending codes and return true on success or false on failure.
    /// This method may be deprecated; in theory it should suffice to flush the pipeline only (with stricter Busy conditions)
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes could be flushed</returns>
    public ValueTask<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        // Need to find the correct state for a flush request first.
        // Generic flush requests are not meant for temporary macro states
        foreach (StackState state in Stack)
        {
            if ((state.File is not MacroFile macro || !macro.WasStarted || macro.IsExecuting) && !state.MacroCompleted)
            {
                return GetFlushTask(state, cancellationToken);
            }
        }

        // Fallback, should not happen
        _logger.LogWarning("Failed to find suitable stack level for flush request, falling back to current one");
        return GetFlushTask(CurrentState, cancellationToken);
    }

    /// <summary>
    /// Flush pending codes and return true on success or false on failure.
    /// This method may be deprecated; in theory it should suffice to flush the pipeline only (with stricter Busy conditions)
    /// </summary>
    /// <param name="file">Optional code file for the flush target</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes could be flushed</returns>
    public ValueTask<bool> FlushAsync(CodeFile file, CancellationToken cancellationToken = default)
    {
        // Need to find the correct state for a flush request first.
        // Generic flush requests are not meant for temporary macro states
        foreach (StackState state in Stack)
        {
            if (state.File == file)
            {
                return GetFlushTask(state, cancellationToken);
            }
        }

        // Fallback, should not happen
        _logger.LogWarning("Failed to find suitable stack level for flush request, falling back to current one");
        return GetFlushTask(CurrentState, cancellationToken);
    }

    /// <summary>
    /// Flush all pending codes and return true on success or false on failure.
    /// This method may be deprecated; in theory it should suffice to flush the pipeline only (with stricter Busy conditions)
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes could be flushed</returns>
    public ValueTask<bool> FlushAllAsync(CancellationToken cancellationToken = default) => GetFlushTask(BaseState, cancellationToken);

    /// <summary>
    /// Lock all movement systems and wait for standstill
    /// </summary>
    /// <returns>Whether the movement systems could be locked</returns>
    public Task<bool> LockAllMovementSystemsAndWaitForStandstill()
    {
        LockMovementRequest request = new(true);
        CurrentState.LockRequests.Enqueue(request);
        return request.Task;
    }

    /// <summary>
    /// Unlock all resources occupied by the given channel
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public Task UnlockAll()
    {
        LockMovementRequest request = new(false);
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
            using (await macro.LockAsync(_lifetime.ApplicationStopping))
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
                using (macro.Lock(_lifetime.ApplicationStopping))
                {
                    if (startCode is not null && abortAll)
                    {
                        // Wait for the macro to be fully cancelled and then cancel the code that started it.
                        // Copy the code to a local first because startCode is shared across loop iterations
                        // and must be reset here so lower stack levels cannot complete it a second time
                        Code codeToComplete = startCode;
                        _ = macro.WaitForFinishAsync().ContinueWith(async task =>
                        {
                            try
                            {
                                await task;
                            }
                            finally
                            {
                                _codeProcessor.CodeCompleted(codeToComplete);
                            }
                        }, TaskContinuationOptions.RunContinuationsAsynchronously);
                        startCode = null;
                    }

                    // Abort the macro file
                    macro.Abort();
                }
                macroAborted = true;
            }
            else if (startCode is not null)
            {
                // This is a message prompt. Cancel the code that started it
                _codeProcessor.CancelCode(startCode);
                startCode = null;
            }

            // Pop the stack
            Pop();
            if (startCode is not null && abortAll)
            {
                _logger.LogDebug("==> Unfinished starting code: {Code}", startCode);
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
                _codeProcessor.CancelCode(bufferedCode);
            }
            BufferedCodes.Clear();
            BytesBuffered = 0;

            // If only the last file was closed (e.g. from M99), carry on with the execution of the code that started it
            if (startCode is not null)
            {
                BytesBuffered += startCode.BinarySize;
                BufferedCodes.Insert(0, startCode);
                _logger.LogDebug("==> Resuming unfinished starting code: {Code}", startCode);
            }
        }

        // Abort the file print if necessary
        if ((Channel is CodeChannel.File or CodeChannel.File2) && (abortAll || !macroAborted))
        {
            using (_jobProcessor.Lock())
            {
                _jobProcessor.Abort();
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
                using (await macro.LockAsync(_lifetime.ApplicationStopping))
                {
                    // Resolve potential start codes when the macro file finishes. Copy the code to a local
                    // first because startCode is shared across loop iterations and must be reset here so
                    // lower stack levels cannot complete it a second time
                    if (startCode is not null)
                    {
                        Code codeToComplete = startCode;
                        _ = macro.WaitForFinishAsync().ContinueWith(async task =>
                        {
                            try
                            {
                                await task;
                            }
                            finally
                            {
                                _codeProcessor.CodeCompleted(codeToComplete);
                            }
                        }, TaskContinuationOptions.RunContinuationsAsynchronously);
                        startCode = null;
                    }

                    // Abort the macro file
                    macro.Abort();
                }
            }
            else if (startCode is not null)
            {
                // Cancel the code that started the blocking message prompt
                _codeProcessor.CancelCode(startCode);
                startCode = null;
            }

            // Pop the stack
            await PopAsync();
            if (startCode is not null)
            {
                _logger.LogDebug("==> Unfinished starting code: {Code}", startCode);
            }
        }

        // Cancel pending codes and requests
        _allFilesAborted = _linkAdapter.ProtocolVersion >= 3;
        InvalidateRegular();

        // Abort the job files if necessary
        if (Channel is CodeChannel.File or CodeChannel.File2)
        {
            using (await _jobProcessor.LockAsync())
            {
                _jobProcessor.Abort();
            }
        }
    }

    /// <summary>
    /// Called when a resource has been locked
    /// </summary>
    public void ResourceLocked()
    {
        foreach (StackState state in Stack)
        {
            if (state.LockRequests.TryDequeue(out LockMovementRequest? item))
            {
                item.Resolve(true);
                return;
            }
        }
        _logger.LogError("Received a lock confirmation for a non-existent request!");
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
            _codeProcessor.CodeCompleted(code);
        }
    }

    /// <summary>
    /// Process code replies that could not be interpreted immediately
    /// </summary>
    private void ResolvePendingReplies()
    {
        while (BufferedCodes.Count > 0 && PendingReplies.TryDequeue(out Tuple<MessageTypeFlags, string>? reply))
        {
            HandleReply(reply.Item1, reply.Item2);
        }
    }

    /// <summary>
    /// Process pending requests on this channel
    /// </summary>
    public void Spin()
    {
        // 1. Whole line comments and pending replies
        ResolveCommentCodes();
        ResolvePendingReplies();

        // 2. Lock/Unlock requests
        if (CurrentState.LockRequests.TryPeek(out LockMovementRequest? lockRequest))
        {
            if (lockRequest.IsLockRequest)
            {
                if (!lockRequest.IsLockRequested && _linkAdapter.WriteLockAllMovementSystemsAndWaitForStandstill(Channel))
                {
                    lockRequest.IsLockRequested = true;
                }
                return;
            }

            if (_linkAdapter.WriteUnlock(Channel))
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
            _allFilesAborted = !_linkAdapter.WriteInvalidateChannel(Channel);
            return;
        }

        // 4. Macro files (must come before any other code unless the stack state is being cloned)
        if (CurrentState.File is MacroFile || CurrentState.MacroError)
        {
            // Tell RRF as quickly as possible about the new macro being started
            if (CurrentState.File is MacroFile macro && macro.JustStarted)
            {
                macro.JustStarted = (_linkAdapter.ProtocolVersion >= 3) && !_linkAdapter.WriteMacroStarted(Channel);
                return;
            }

            // Check if the macro file has finished
            if (CurrentState.File is MacroFile { WasStarted: true, IsExecuting: false } || CurrentState.MacroError)
            {
                if (!CurrentState.MacroCompleted && _linkAdapter.WriteMacroCompleted(Channel, CurrentState.MacroError))
                {
                    CurrentState.MacroCompleted = true;
                    if (_linkAdapter.ProtocolVersion >= 3)
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
                                _logger.LogDebug("==> Unfinished starting code: {Code}", startCode);
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
                _logger.LogDebug("-> Resumed suspended code");
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
        while (PendingReplies.TryDequeue(out Tuple<MessageTypeFlags, string>? reply))
        {
            _logger.LogWarning("Pending out-of-order reply: '{Reply}'", reply.Item2);
        }
    }

    /// <summary>
    /// Perform a regular code that was requested from the firmware
    /// </summary>
    /// <param name="code">Code to perform</param>
    public void DoFirmwareCode(string code)
    {
        _logger.LogDebug("Running code from firmware '{Code}'", code);

        Commands.SimpleCode simpleCode = _commandFactory.Create<Commands.SimpleCode>();
        simpleCode.Code = code;
        simpleCode.Channel = Channel;
        simpleCode.IsFromFirmware = true;
        simpleCode.ExecuteAsynchronously = true;
        _ = simpleCode.ExecuteAsync();
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
            if (pendingCode.Stage != PipelineStage.Firmware)
            {
                pendingCode.BinarySize = Consts.BufferedCodeHeaderSize + Protocol.Writer.GetCodeSize(pendingCode, _settings.MaxCodeBufferSize, _linkAdapter.ProtocolVersion);

                pendingCode.Stage = PipelineStage.Firmware;
            }

            // Don't send cancelled codes to the firmware
            if (pendingCode.CancellationToken.IsCancellationRequested)
            {
                _codeProcessor.CancelCode(pendingCode);
                return true;
            }

            // Try to send it to RepRapFirmware
            int maxBufferSpace = _linkInterface.GetMaxBufferSpacePerChannel(_jobProcessor.NumJobStreams);
            if ((BytesBuffered == 0 || BytesBuffered + pendingCode.BinarySize <= maxBufferSpace) &&
                _linkInterface.SendCode(pendingCode, pendingCode.BinarySize))
            {
                BytesBuffered += pendingCode.BinarySize;
                BufferedCodes.Add(pendingCode);
                _logger.LogDebug("Sent {Code}, remaining space {BytesRemaining}, needed {BytesNeeded}", pendingCode, maxBufferSpace - BytesBuffered, pendingCode.BinarySize);
                return true;
            }
            return false;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Failed to buffer code {Code}", pendingCode);
            _codeProcessor.CancelCode(pendingCode, e);
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
            if (_linkAdapter.ProtocolVersion < 3 && string.IsNullOrEmpty(reply))
            {
                MacroFileClosed();
                return true;
            }
            else if (_linkAdapter.ProtocolVersion >= 3)
            {
                PendingReplies.Enqueue(new Tuple<MessageTypeFlags, string>(flags, reply));
                return true;
            }
        }

        // Check for message boxes being closed
        if (CurrentState.WaitingForAcknowledgement)
        {
            if (_linkAdapter.ProtocolVersion < 3 && string.IsNullOrEmpty(reply))
            {
                MessageAcknowledged();
                return true;
            }
            else if (_linkAdapter.ProtocolVersion >= 3)
            {
                PendingReplies.Enqueue(new Tuple<MessageTypeFlags, string>(flags, reply));
                return true;
            }
        }

        // Unless this message comes from the file or code queue it is out-of-order...
        if (Channel != CodeChannel.Queue)
        {
            if (!_suppressEmptyReply)
            {
                _logger.LogWarning("Out-of-order reply: '{Reply}'", reply);
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

            // Preserve the log level from the firmware reply flags
            code.ReplyLogLevel = (flags & MessageTypeFlags.LogOff) == MessageTypeFlags.LogOff ? EventLogLevel.Off
                : flags.HasFlag(MessageTypeFlags.LogWarn) ? EventLogLevel.Warn
                : flags.HasFlag(MessageTypeFlags.LogInfo) ? EventLogLevel.Info
                : EventLogLevel.Debug;

            _codeProcessor.CodeCompleted(code);
        }
        else
        {
            // Final output from a system macro
            MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                        : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                        : MessageType.Success;
            _model.Output(type, reply);
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
            _logger.LogDebug("Waiting for acknowledgement");

            Code? startCode = null;
            if (BufferedCodes.Count > 0)
            {
                startCode = BufferedCodes[0];
                startCode.UpdateNextFilePosition();
                BytesBuffered -= startCode.BinarySize;
                BufferedCodes.RemoveAt(0);
            }

            StackState newState = Push();
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
            _logger.LogDebug("==> Unfinished starting code: {Code}", startCode);

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
            _logger.LogDebug("Message acknowledged");

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
                _logger.LogDebug("==> Unfinished starting code: {Code}", startCode);
            }
        }
        else
        {
            _logger.LogError("Tried to acknowledge a message, but no acknowledgement is requested!");
        }
    }

    /// <summary>
    /// Attempt to start a file macro
    /// </summary>
    /// <param name="virtualFile">Requested name of the macro file</param>
    /// <param name="fromCode">Request comes from a real G/M/T-code</param>
    public void DoMacroFile(string virtualFile, bool fromCode)
    {
        // Macro requests are not meant for comment codes, resolve them separately
        ResolveCommentCodes();

        // Cannot start system macro if something is still busy
        if (!fromCode && Stack.Count > 1)
        {
            _logger.LogWarning("System macro {File} is requested but the stack is not empty. Discarding request.", virtualFile);
            _linkAdapter.WriteMacroCompleted(Channel, true);
            return;
        }

        // Figure out which code started the macro file
        Code? startCode = null;
        if (fromCode)
        {
            if (CurrentState.MacroCompleted)
            {
                _logger.LogInformation("Finished intermediate macro file {File}", CurrentState.File!.FilePath.Virtual);
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
            _logger.LogWarning("System macro {File} is requested but the stack is not empty. Discarding request.", virtualFile);
            _linkAdapter.WriteMacroCompleted(Channel, true);
            return;
        }

        // Try to locate the macro file
        string physicalFile = _filePathResolver.ToPhysical(virtualFile, FileDirectory.System);
        MacroFile? macro = _macroFileFactory.CreateMacro(virtualFile, physicalFile, Channel, startCode, startCode?.SourceConnection ?? 0);

        StackState newState = Push(macro);
        newState.StartCode = startCode;
        if (macro is not null)
        {
            // Start it
            if (startCode is not null)
            {
                startCode.UpdateNextFilePosition();
                _logger.LogDebug("==> Starting code {Code}", startCode);
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
            _codeProcessor.CancelCode(bufferedCode);
        }
        BufferedCodes.Clear();
        BytesBuffered = 0;
        _suppressEmptyReply = true;

        foreach (StackState state in Stack)
        {
            if (!state.WaitingForAcknowledgement && (state.File is not MacroFile macro || macro.IsPausable))
            {
                while (state.LockRequests.TryDequeue(out LockMovementRequest? lockRequest))
                {
                    lockRequest.Resolve(false);
                }

                while (state.SuspendedCodes.TryDequeue(out Code? suspendedCode))
                {
                    _codeProcessor.CancelCode(suspendedCode);
                }

                while (state.PendingCodes.Reader.TryRead(out Code? pendingCode))
                {
                    pendingCode.Stage = PipelineStage.Firmware;
                    _codeProcessor.CancelCode(pendingCode);
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
            while (CurrentState.LockRequests.TryDequeue(out LockMovementRequest? lockRequest))
            {
                lockRequest.Resolve(false);
            }

            while (CurrentState.SuspendedCodes.TryDequeue(out Code? suspendedCode))
            {
                _codeProcessor.CancelCode(suspendedCode);
            }

            while (CurrentState.PendingCodes.Reader.TryRead(out Code? pendingCode))
            {
                pendingCode.Stage = PipelineStage.Firmware;
                _codeProcessor.CancelCode(pendingCode);
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
            _codeProcessor.CancelCode(bufferedCode);
        }
        BufferedCodes.Clear();
        BytesBuffered = 0;
        _suppressEmptyReply = true;

        // Clear codes that are still pending but have not been fed into the SPI interface yet
        _codeProcessor.CancelPending(Channel);
        _allFilesAborted = false;
    }
}
