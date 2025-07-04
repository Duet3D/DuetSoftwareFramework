using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Files;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Link;

/// <summary>
/// Main firmware interface
/// </summary>
/// <param name="channels">Channel manager</param>
/// <param name="linkAdapter">Firmware link adapter</param>
/// <param name="settings">Settings</param>
[DiagnosticsPriority(-6)]
public sealed partial class LinkInterface(
    Channel.Manager channels,
    ILinkAdapter linkAdapter,
    IOptions<Settings> settings) : IDiagnostics
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    // Information about the code channels
    internal int BytesReserved, BufferSpace;
    internal readonly Queue<ModelQueryRequest> ModelQueryRequests = new();

    // Expression evaluation and variable requests
    internal readonly List<EvaluateExpressionRequest> EvaluateExpressionRequests = [];
    internal readonly List<VariableRequest> VariableRequests = [];

    // Firmware updates
    internal readonly AsyncLock FirmwareUpdateLock = new();
    internal Stream? IapStream, FirmwareStream;
    internal TaskCompletionSource? FirmwareUpdateRequest;

    // Firmware halt/restart requests
    internal readonly AsyncLock FirmwareActionLock = new();
    internal TaskCompletionSource? FirmwareHaltRequest;
    internal TaskCompletionSource? FirmwareResetRequest;

    // Print handling
    internal readonly AsyncLock PrintStateLock = new();
    internal TaskCompletionSource? SetPrintInfoRequest;
    internal PrintStoppedReason StopPrintReason;
    internal TaskCompletionSource? StopPrintRequest;

    // Miscellaneous requests
    internal readonly Queue<Tuple<MessageTypeFlags, string>> MessagesToSend = new();

    /// <summary>
    /// Print diagnostics of this class
    /// </summary>
    /// <param name="builder">String builder</param>
    /// <returns>Asynchronous task</returns>
    public void PrintDiagnostics(StringBuilder builder)
    {
        builder.AppendLine($"Code buffer space: {BufferSpace}");
    }

    /// <summary>
    /// Request a specific update of the object model
    /// </summary>
    /// <param name="key">Key to request</param>
    /// <param name="flags">Object model flags</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Deserialized JSON document</returns>
    public Task<byte[]> RequestObjectModel(string key, string flags, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<byte[]>(cancellationToken);
        }

        ModelQueryRequest request = new(key, flags);
        lock (ModelQueryRequests)
        {
            ModelQueryRequests.Enqueue(request);
        }
        return request.Tcs.Task;
    }

    /// <summary>
    /// Evaluate an arbitrary expression
    /// </summary>
    /// <param name="channel">Where to evaluate the expression</param>
    /// <param name="expression">Expression to evaluate</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Result of the evaluated expression</returns>
    /// <exception cref="CodeParserException">Failed to evaluate expression</exception>
    /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
    /// <exception cref="NotSupportedException">Incompatible firmware version</exception>
    /// <exception cref="ArgumentException">Invalid parameter</exception>
    public Task<object?> EvaluateExpressionAsync(CodeChannel channel, string expression, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<object?>(cancellationToken);
        }
        if (linkAdapter.ProtocolVersion == 1)
        {
            throw new NotSupportedException("Incompatible firmware version");
        }
        if (Encoding.UTF8.GetByteCount(expression) >= Consts.MaxExpressionLength)
        {
            throw new ArgumentException($"Expression too long (max {Consts.MaxExpressionLength} chars)", nameof(expression));
        }

        lock (EvaluateExpressionRequests)
        {
            foreach (EvaluateExpressionRequest item in EvaluateExpressionRequests)
            {
                if (item.Channel == channel && item.Expression == expression)
                {
                    // There is no reason to evaluate the same expression twice...
                    return item.Task;
                }
            }

            EvaluateExpressionRequest request = new(channel, expression);
            EvaluateExpressionRequests.Add(request);
            _logger.Debug("Evaluating {0} on channel {1}", expression, channel);
#warning add ct support
            return request.Task;
        }
    }

    /// <summary>
    /// Set or delete a global or local variable
    /// </summary>
    /// <param name="channel">Where to evaluate the expression</param>
    /// <param name="createVariable">Whether the variable shall be created</param>
    /// <param name="varName">Name of the variable</param>
    /// <param name="expression">Expression to evaluate</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Result of the evaluated expression</returns>
    /// <exception cref="CodeParserException">Failed to assign or delete variable</exception>
    /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
    /// <exception cref="NotSupportedException">Incompatible firmware version</exception>
    /// <exception cref="ArgumentException">Invalid parameter</exception>
    public Task<object?> SetVariableAsync(CodeChannel channel, bool createVariable, string varName, string? expression, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<object?>(cancellationToken);
        }
        if (linkAdapter.ProtocolVersion < 5)
        {
            throw new NotSupportedException("Incompatible firmware version");
        }
        if (Encoding.UTF8.GetByteCount(varName) >= Consts.MaxVariableLength)
        {
            throw new ArgumentException($"Variable too long (max {Consts.MaxVariableLength} chars)");
        }
        if (expression is not null && Encoding.UTF8.GetByteCount(expression) >= Consts.MaxExpressionLength)
        {
            throw new ArgumentException($"Expression too long (max {Consts.MaxExpressionLength} chars)");
        }

        VariableRequest request;
        lock (VariableRequests)
        {
            request = new(channel, createVariable, varName, expression);
            VariableRequests.Add(request);
            if (expression is not null)
            {
                _logger.Debug("Setting variable {0} to {1} on channel {2}", varName, expression, channel);
            }
            else
            {
                _logger.Debug("Deleting local variable {0} on channel {1}", varName, channel);
            }
        }
        return request.Task;
    }

    /// <summary>
    /// Wait for all pending codes of the first or last stack item to finish
    /// </summary>
    /// <param name="channel">Code channel to wait for</param>
    /// <param name="flushAll">Flush everything</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async Task<bool> FlushAsync(CodeChannel channel, bool flushAll, CancellationToken cancellationToken = default)
    {
        Task<bool> flushTask;
        using (await channels[channel].LockAsync(cancellationToken))
        {
            flushTask = flushAll ? channels[channel].FlushAllAsync(cancellationToken) : channels[channel].FlushAsync(cancellationToken);
        }
        return await flushTask;
    }

    /// <summary>
    /// Wait for all pending codes to finish
    /// </summary>
    /// <param name="file">Code file</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async Task<bool> FlushAsync(CodeFile file, CancellationToken cancellationToken = default)
    {
        Task<bool> flushTask;
        using (await channels[file.Channel].LockAsync(cancellationToken))
        {
            flushTask = channels[file.Channel].FlushAsync(file, cancellationToken);
        }
        return await flushTask;
    }

    /// <summary>
    /// Wait for all pending codes on the same stack level as the given code to finish.
    /// By default this replaces all expressions as well for convenient parsing by the code processors.
    /// </summary>
    /// <param name="code">Code waiting for the flush</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async Task<bool> FlushAsync(Code code, CancellationToken cancellationToken = default)
    {
        Task<bool> flushTask;
        using (await channels[code.Channel].LockAsync(cancellationToken))
        {
            flushTask = (code.File == null) ? channels[code.Channel].FlushAsync(cancellationToken) : channels[code.Channel].FlushAsync(code.File, cancellationToken);
        }
        return await flushTask;
    }

    /// <summary>
    /// Copy the state from one channel processor to another
    /// </summary>
    /// <param name="from">Source channel</param>
    /// <param name="to">Target channel</param>
    /// <exception cref="NotImplementedException"></exception>
    public async Task CopyStateAsync(CodeChannel from, CodeChannel to)
    {
        using (await channels[to].LockAsync())
        {
            using (await channels[from].LockAsync())
            {
                channels[to].CopyState(channels[from]);
            }
        }
    }

    /// <summary>
    /// Request an immediate emergency stop
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task onFirmwareHalted;
        using (await FirmwareActionLock.LockAsync(cancellationToken))
        {
            FirmwareHaltRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onFirmwareHalted = FirmwareHaltRequest.Task;
        }
        await onFirmwareHalted;
    }

    /// <summary>
    /// Perform a firmware reset and wait for it to finish
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task ResetFirmwareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task onFirmwareReset;
        using (await FirmwareActionLock.LockAsync(cancellationToken))
        {
            FirmwareResetRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onFirmwareReset = FirmwareResetRequest.Task;
        }
        await onFirmwareReset.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Attempt to flag the currently executing macro file as (not) pausable
    /// </summary>
    /// <param name="channel">Code channel where the macro is being executed</param>
    /// <param name="isPausable">Whether or not the macro file is pausable</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task SetMacroPausableAsync(CodeChannel channel, bool isPausable, CancellationToken cancellationToken = default)
    {
        using (await channels[channel].LockAsync(cancellationToken))
        {
            await channels[channel].SetMacroPausable(isPausable);
        }
    }

    /// <summary>
    /// Update the print file info in the firmware
    /// </summary>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
    public async Task SetPrintFileInfo()
    {
        Task task;
        using (await PrintStateLock.LockAsync())
        {
            SetPrintInfoRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = SetPrintInfoRequest.Task;
        }
        await task;
    }

    /// <summary>
    /// Notify the firmware that the file print has been stopped
    /// </summary>
    /// <param name="reason">Reason why the print has stopped</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
    /// <exception cref="OperationCanceledException">Connection lost while trying to notify RRF</exception>
    public async Task StopPrintAsync(PrintStoppedReason reason)
    {
        Task onPrintStopped;
        using (await PrintStateLock.LockAsync())
        {
            StopPrintReason = reason;
            StopPrintRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onPrintStopped = StopPrintRequest.Task;
        }
        await onPrintStopped;
    }

    /// <summary>
    /// Class representing an acquired movement lock
    /// </summary>
    /// <param name="channel">Locked code channel</param>
    /// <param name="linkInterface">Link interface</param>
    private class MovementLock(CodeChannel channel, LinkInterface linkInterface) : IAsyncDisposable
    {
        /// <summary>
        /// Called when this instance is being disposed
        /// </summary>
        /// <returns>Asynchronous task</returns>
        public async ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            await linkInterface.UnlockAll(channel);
        }
    }

    /// <summary>
    /// Lock all movement systems and wait for standstill
    /// </summary>
    /// <param name="channel">Code channel acquiring the lock</param>
    /// <returns>Disposable lock object that releases the lock when disposed</returns>
    /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
    /// <exception cref="OperationCanceledException">Failed to get movement lock</exception>
    public async Task<IAsyncDisposable> LockAllMovementSystemsAndWaitForStandstill(CodeChannel channel)
    {
        Task<bool> lockTask;
        using (await channels[channel].LockAsync())
        {
            lockTask = channels[channel].LockAllMovementSystemsAndWaitForStandstill();
        }

        if (await lockTask)
        {
            return new MovementLock(channel, this);
        }
        throw new OperationCanceledException();
    }

    /// <summary>
    /// Unlock all resources occupied by the given channel
    /// </summary>
    /// <param name="channel">Channel holding the resources</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
    internal async Task UnlockAll(CodeChannel channel)
    {
        Task unlockTask;
        using (await channels[channel].LockAsync())
        {
            unlockTask = channels[channel].UnlockAll();
        }
        await unlockTask;
    }

    /// <summary>
    /// Wait for potential firmware update to finish
    /// </summary>
    public void WaitForUpdate()
    {
        using (FirmwareUpdateLock.Lock())
        {
            // This lock is acquired as long as a firmware update is in progress; no need to do anything else
        }
    }

    /// <summary>
    /// Wait for potential firmware update to finish
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public async Task WaitForUpdateAsync()
    {
        using (await FirmwareUpdateLock.LockAsync())
        {
            // This lock is acquired as long as a firmware update is in progress; no need to do anything else
        }
    }

    /// <summary>
    /// Perform an update of the main firmware via IAP
    /// </summary>
    /// <param name="iapStream">IAP binary</param>
    /// <param name="firmwareStream">Firmware binary</param>
    /// <exception cref="InvalidOperationException">Firmware is already being updated or not connected over SPI</exception>
    /// <returns>Asynchronous task</returns>
    public async Task UpdateFirmware(Stream iapStream, Stream firmwareStream)
    {
        TaskCompletionSource tcs;
        using (await FirmwareUpdateLock.LockAsync())
        {
            if (FirmwareUpdateRequest is not null)
            {
                throw new InvalidOperationException("Firmware is already being updated");
            }

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IapStream = iapStream;
            FirmwareStream = firmwareStream;
            FirmwareUpdateRequest = tcs;
        }
        await tcs.Task;
    }

    /// <summary>
    /// Send a message to the firmware
    /// </summary>
    /// <param name="flags">Message flags</param>
    /// <param name="message">Message content</param>
    /// <exception cref="InvalidOperationException">Incompatible firmware or not connected over SPI</exception>
    /// <exception cref="NotSupportedException">Incompatible firmware version</exception>
    /// <exception cref="ArgumentException">Invalid parameter</exception>
    public void SendMessage(MessageTypeFlags flags, string message)
    {
        if (linkAdapter.ProtocolVersion == 1)
        {
            throw new NotSupportedException("Incompatible firmware version");
        }
        if (message.Length > settings.Value.MaxMessageLength)
        {
            throw new ArgumentException($"{nameof(message)} too long");
        }

        lock (MessagesToSend)
        {
            MessagesToSend.Enqueue(new Tuple<MessageTypeFlags, string>(flags, message));
        }
    }

    /// <summary>
    /// Abort all files in RRF on the given channel asynchronously
    /// </summary>
    /// <param name="channel">Channel where all the files have been aborted</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
    public async Task AbortAllAsync(CodeChannel channel)
    {
        using (await channels[channel].LockAsync())
        {
            await channels[channel].AbortAllFilesAsync();
        }
    }

    /// <summary>
    /// Send a pending code to the firmware
    /// </summary>
    /// <param name="code">Code to send</param>
    /// <param name="codeLength">Length of the binary code in bytes</param>
    /// <returns>Whether the code could be sent</returns>
    internal bool SendCode(Code code, int codeLength)
    {
        if (BufferSpace > codeLength && linkAdapter.WriteCode(code))
        {
            BytesReserved += codeLength;
            BufferSpace -= codeLength;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Invalidate every resource due to a critical event
    /// </summary>
    internal void Invalidate()
    {
        // No longer starting or stopping a print. Must do this before aborting the print
        using (PrintStateLock.Lock())
        {
            if (SetPrintInfoRequest is not null)
            {
                SetPrintInfoRequest.SetCanceled();
                SetPrintInfoRequest = null;
            }
            if (StopPrintRequest is not null)
            {
                StopPrintRequest.SetCanceled();
                StopPrintRequest = null;
            }
        }

        // Resolve pending macros, unbuffered (system) codes and flush requests
        foreach (Channel.Processor channel in channels)
        {
            using (channel.Lock())
            {
                channel.Invalidate();
            }
        }
        BytesReserved = BufferSpace = 0;

        // Resolve pending object model requests
        lock (ModelQueryRequests)
        {
            foreach (ModelQueryRequest request in ModelQueryRequests)
            {
                request.Tcs.SetCanceled();
            }
            ModelQueryRequests.Clear();
        }

        // Resolve pending expression evaluation and variable requests
        lock (EvaluateExpressionRequests)
        {
            foreach (EvaluateExpressionRequest request in EvaluateExpressionRequests)
            {
                request.SetCanceled();
            }
            EvaluateExpressionRequests.Clear();
        }

        lock (VariableRequests)
        {
            foreach (VariableRequest request in VariableRequests)
            {
                request.SetCanceled();
            }
            VariableRequests.Clear();
        }

        // Clear messages to send to the firmware
        lock (MessagesToSend)
        {
            MessagesToSend.Clear();
        }
    }

    /// <summary>
    /// Invalidate every resource due to a critical event asynchronously
    /// </summary>
    /// <returns>Asynchronous task</returns>
    internal async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        // No longer starting or stopping a print. Must do this before aborting the print
        using (await PrintStateLock.LockAsync(cancellationToken))
        {
            if (SetPrintInfoRequest is not null)
            {
                SetPrintInfoRequest.SetCanceled(cancellationToken);
                SetPrintInfoRequest = null;
            }
            if (StopPrintRequest is not null)
            {
                StopPrintRequest.SetCanceled(cancellationToken);
                StopPrintRequest = null;
            }
        }

        // Resolve pending macros, unbuffered (system) codes and flush requests
        foreach (Channel.Processor channel in channels)
        {
            using (await channel.LockAsync(cancellationToken))
            {
                channel.Invalidate();
            }
        }
        BytesReserved = BufferSpace = 0;

        // Resolve pending object model requests
        lock (ModelQueryRequests)
        {
            foreach (ModelQueryRequest request in ModelQueryRequests)
            {
                request.Tcs.SetCanceled(cancellationToken);
            }
            ModelQueryRequests.Clear();
        }

        // Resolve pending expression evaluation and variable requests
        lock (EvaluateExpressionRequests)
        {
            foreach (EvaluateExpressionRequest request in EvaluateExpressionRequests)
            {
                request.SetCanceled();
            }
            EvaluateExpressionRequests.Clear();
        }

        lock (VariableRequests)
        {
            foreach (VariableRequest request in VariableRequests)
            {
                request.SetCanceled();
            }
            VariableRequests.Clear();
        }

        // Clear messages to send to the firmware
        lock (MessagesToSend)
        {
            MessagesToSend.Clear();
        }
    }
}
