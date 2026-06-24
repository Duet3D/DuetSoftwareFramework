using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Link;

/// <summary>
/// Main firmware interface
/// </summary>
/// <param name="channels">Channel manager</param>
/// <param name="linkAdapter">Firmware link adapter</param>
/// <param name="logger">Logger instance</param>
/// <param name="settings">Settings</param>
[DiagnosticsPriority(-5)]
public sealed partial class LinkInterface(
    Channel.Manager channels,
    ILinkAdapter linkAdapter,
    ILogger<LinkInterface> logger,
    IOptions<Settings> settings) : IDiagnostics
{
    // Information about the code channels
    internal int BytesReserved, BufferSpace;

    // CAN bus requests
    internal readonly List<CanRequest> CanRequests = [];

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
    /// Set the last code result for a specific code channel
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="result">Code result</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public Task SendCanMessageAsync(CanMessageType messageType, CanMessageType replyType, CancellationToken cancellationToken = default)
    {
        CanRequest request;
        lock (CanRequests)
        {
            request = new(messageType, replyType);
            CanRequests.Add(request);
            logger.LogDebug("Sending CAN message of type {MessageType} expecting reply of type {ReplyType}", messageType, replyType);
        }
        return request.Task.WaitAsync(cancellationToken);
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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task CopyStateAsync(CodeChannel from, CodeChannel to, CancellationToken cancellationToken = default)
    {
        using (await channels[to].LockAsync(cancellationToken))
        {
            using (await channels[from].LockAsync(cancellationToken))
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
        await onFirmwareHalted.WaitAsync(cancellationToken);
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
            await channels[channel].SetMacroPausable(isPausable).WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Update the print file info in the firmware
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
    public async Task SetPrintFileInfo(CancellationToken cancellationToken = default)
    {
        Task task;
        using (await PrintStateLock.LockAsync(cancellationToken))
        {
            SetPrintInfoRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = SetPrintInfoRequest.Task;
        }
        await task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Notify the firmware that the file print has been stopped
    /// </summary>
    /// <param name="reason">Reason why the print has stopped</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
    /// <exception cref="OperationCanceledException">Connection lost while trying to notify RRF</exception>
    public async Task StopPrintAsync(PrintStoppedReason reason, CancellationToken cancellationToken = default)
    {
        Task onPrintStopped;
        using (await PrintStateLock.LockAsync(cancellationToken))
        {
            StopPrintReason = reason;
            StopPrintRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onPrintStopped = StopPrintRequest.Task;
        }
        await onPrintStopped.WaitAsync(cancellationToken);
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
    public async Task<IAsyncDisposable> LockAllMovementSystemsAndWaitForStandstill(CodeChannel channel, CancellationToken cancellationToken = default)
    {
        Task<bool> lockTask;
        using (await channels[channel].LockAsync(cancellationToken))
        {
            lockTask = channels[channel].LockAllMovementSystemsAndWaitForStandstill();
        }

        if (await lockTask.WaitAsync(cancellationToken))
        {
            return new MovementLock(channel, this);
        }
        throw new OperationCanceledException();
    }

    /// <summary>
    /// Unlock all resources occupied by the given channel
    /// </summary>
    /// <param name="channel">Channel holding the resources</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
    internal async Task UnlockAll(CodeChannel channel, CancellationToken cancellationToken = default)
    {
        Task unlockTask;
        using (await channels[channel].LockAsync(cancellationToken))
        {
            unlockTask = channels[channel].UnlockAll();
        }
        await unlockTask.WaitAsync(cancellationToken);
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
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task WaitForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using (await FirmwareUpdateLock.LockAsync(cancellationToken))
        {
            // This lock is acquired as long as a firmware update is in progress; no need to do anything else
        }
    }

    /// <summary>
    /// Perform an update of the main firmware via IAP
    /// </summary>
    /// <param name="iapStream">IAP binary</param>
    /// <param name="firmwareStream">Firmware binary</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <exception cref="InvalidOperationException">Firmware is already being updated or not connected over SPI</exception>
    /// <returns>Asynchronous task</returns>
    public async Task UpdateFirmware(Stream iapStream, Stream firmwareStream, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource tcs;
        using (await FirmwareUpdateLock.LockAsync(cancellationToken))
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
        await tcs.Task.WaitAsync(cancellationToken);
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
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="InvalidOperationException">Not connected over SPI</exception>
    public async Task AbortAllAsync(CodeChannel channel, CancellationToken cancellationToken = default)
    {
        using (await channels[channel].LockAsync(cancellationToken))
        {
            await channels[channel].AbortAllFilesAsync().WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Invalidate pending codes and code-relevant requests due to an emergency stop
    /// </summary>
    internal void InvalidateCodes()
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

        // Resolve pending CAN requests
        lock (CanRequests)
        {
            foreach (CanRequest request in CanRequests)
            {
                request.SetCanceled();
            }
            CanRequests.Clear();
        }
    }

    /// <summary>
    /// Invalidate every resource due to a disconnect or reset
    /// </summary>
    internal void Invalidate()
    {
        // Invalidate codes and code-relevant requests
        InvalidateCodes();

        // Clear messages to send to the firmware
        lock (MessagesToSend)
        {
            MessagesToSend.Clear();
        }
    }

    /// <summary>
    /// Invalidate pending codes and code-relevant requests due to an emergency stop asynchronously
    /// </summary>
    /// <returns>Asynchronous task</returns>
    internal async Task InvalidateCodesAsync(CancellationToken cancellationToken)
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

        // Resolve pending CAN requests
        lock (CanRequests)
        {
            foreach (CanRequest request in CanRequests)
            {
                request.SetCanceled();
            }
            CanRequests.Clear();
        }
    }

    /// <summary>
    /// Invalidate every resource due to a disconnect or reset asynchronously
    /// </summary>
    /// <returns>Asynchronous task</returns>
    internal async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        // Invalidate codes and code-relevant requests
        await InvalidateCodesAsync(cancellationToken);

        // Clear messages to send to the firmware
        lock (MessagesToSend)
        {
            MessagesToSend.Clear();
        }
    }
}
