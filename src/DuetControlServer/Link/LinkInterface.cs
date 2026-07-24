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
using DuetControlServer.Link.Native;
using DuetControlServer.Link.Protocol.CanMessages;
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
/// <param name="nativeLink">Native SPI transfer loop</param>
/// <param name="logger">Logger instance</param>
/// <param name="settings">Settings</param>
[DiagnosticsPriority(-5)]
public sealed partial class LinkInterface(
    Channel.Manager channels,
    NativeLink nativeLink,
    ILogger<LinkInterface> logger,
    IOptions<Settings> settings) : IDiagnostics
{
    // Information about the code channels
    internal int BytesReserved, BufferSpace;

    // CAN bus requests

    /// <summary>
    /// Reserved transmission token used for CAN messages that are not a reply to one of our requests
    /// </summary>
    internal const ushort UnsolicitedTxToken = 0xFFFF;

    internal readonly List<CanRequest> CanRequests = [];
    private ushort _canTxToken;

    // Firmware updates. LinkService watches FirmwareUpdateRequested and performs the flash, because
    // only it owns the resource invalidation and object model updates an update implies
    internal readonly AsyncLock FirmwareUpdateLock = new();
    internal Stream? IapStream, FirmwareStream;
    internal TaskCompletionSource? FirmwareUpdateRequest;

    /// <summary>
    /// Raised when a firmware update has been staged for <see cref="LinkService"/> to perform
    /// </summary>
    internal readonly SemaphoreSlim FirmwareUpdateRequested = new(0);

    // Serialises firmware halt/restart requests against each other
    internal readonly AsyncLock FirmwareActionLock = new();

    /// <summary>
    /// Invalidate pending codes and code-relevant requests, set by <see cref="LinkService"/>
    /// </summary>
    /// <remarks>
    /// An emergency stop voids everything in flight, and that teardown spans the job processor and
    /// channel processors which this class does not own. Rather than reach across, it calls back into
    /// <see cref="LinkService"/>, which owns them
    /// </remarks>
    internal Action? InvalidateCodesCallback;

    /// <summary>
    /// Invalidate every resource, set by <see cref="LinkService"/>
    /// </summary>
    internal Action? InvalidateCallback;

    // Print handling
    internal readonly AsyncLock PrintStateLock = new();
    internal TaskCompletionSource? SetPrintInfoRequest;
    internal PrintStoppedReason StopPrintReason;
    internal TaskCompletionSource? StopPrintRequest;

    /// <summary>
    /// Print diagnostics of this class
    /// </summary>
    /// <param name="builder">String builder</param>
    /// <returns>Asynchronous task</returns>
    public void PrintDiagnostics(StringBuilder builder)
    {
        builder.AppendLine($"Code buffer space: {BufferSpace}");
    }

    public Task<CanResponse> ConfigCanAsync(byte dstAddress, byte? newAddress, CanTiming timing, CancellationToken cancellationToken = default)
    {
        CanMessageSetAddressAndNormalTiming message = new()
        {
            oldAddress = dstAddress,
            newAddress = newAddress ?? dstAddress,
            newAddressInverted = (byte)~(newAddress ?? dstAddress),
            doSetTiming = CanMessageSetAddressAndNormalTiming.DoSetTimingYes,
            normalTiming = timing
        };

        return SendCanMessageAsync(dstAddress, in message, cancellationToken: cancellationToken);
    }

    public Task<CanResponse> ReportCanConfigAsync(byte dstAddress, CancellationToken cancellationToken = default)
    {
        CanMessageSetAddressAndNormalTiming message = new()
        {
            oldAddress = dstAddress,
            doSetTiming = CanMessageSetAddressAndNormalTiming.DoSetTimingNo
        };

        return SendCanMessageAsync(dstAddress, message, CanMessageType.StandardReply, cancellationToken: cancellationToken);
    }

    public async Task EnableCanAsync(bool enable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The native loop stages this into the next transfer and reports back once it has been written
        await nativeLink.EnableCanAsync(enable, cancellationToken);
        logger.LogInformation("Sent CAN enable request: {Enable}", enable);
    }

    /// <summary>
    /// Send a typed CAN message to an expansion board and wait for the (optional) reply
    /// </summary>
    /// <typeparam name="TReq">Type of the CAN message body</typeparam>
    /// <param name="dstAddress">CAN destination: 0..126, or 127 for broadcast</param>
    /// <param name="message">CAN message body to send</param>
    /// <param name="replyType">Expected reply type (<see cref="CanMessageType.NoReply"/> if none)</param>
    /// <param name="flags">Flags for the CAN message</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Reassembled reply (empty if no reply was expected)</returns>
    public Task<CanResponse> SendCanMessageAsync<TReq>(byte dstAddress, in TReq message, CanMessageType replyType = CanMessageType.NoReply, bool isResponse = false, CancellationToken cancellationToken = default)
        where TReq : struct, ICanMessage<TReq>
    {
        // Only the leading GetActualDataLength() bytes are transmitted; variable-length messages report fewer
        // bytes than sizeof(TReq), so writing the whole struct here would overrun a shorter payload buffer.
        byte[] payload = new byte[message.GetActualDataLength()];
        CanMessageSerializer.Serialize(in message, payload);
        return SendCanMessageAsync(TReq.MessageType, replyType, dstAddress, payload, isResponse, cancellationToken);
    }

    /// <summary>
    /// Send a raw CAN message to an expansion board and wait for the (optional) reply
    /// </summary>
    /// <param name="messageType">Type of the CAN message</param>
    /// <param name="replyType">Expected reply type (<see cref="CanMessageType.NoReply"/> if none)</param>
    /// <param name="dstAddress">CAN destination: 0..126, or 127 for broadcast</param>
    /// <param name="payload">Serialized CAN message payload</param>
    /// <param name="flags">Flags for the CAN message</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Reassembled reply (empty if no reply was expected)</returns>
    private async Task<CanResponse> SendCanMessageAsync(CanMessageType messageType, CanMessageType replyType, byte dstAddress, byte[] payload, bool isResponse, CancellationToken cancellationToken)
    {
        CanRequest request;
        lock (CanRequests)
        {
            if (replyType != CanMessageType.NoReply)
            {
                // set the RID (first 12 bits) to 0x7FF to indicate to the HAT that the firmware should allocate the RID for us.
                payload[0] = 0xFF;
                payload[1] |= 0x07;
            }
            request = new(messageType, replyType, NextCanTxToken(), dstAddress, isResponse, payload);
            CanRequests.Add(request);
            logger.LogDebug("Queueing CAN message of type {MessageType} to address {DstAddress} expecting reply of type {ReplyType}", messageType, dstAddress, replyType);
        }

        try
        {
            // Hand the message to the native loop, which stages it into the next transfer. The reply
            // (if any) arrives as a CanResponse event and is matched back to this request by its token
            nativeLink.QueueCanMessage(request.TxToken, (ushort)request.MessageType, (ushort)request.ReplyType,
                request.DstAddress, request.IsResponse, request.RequestPayload);
            request.Sent = true;

            // A request expecting no reply is complete as soon as the native loop has taken it: there
            // is nothing further to wait for, and no CanResponse event will ever arrive to resolve it.
            // It must also be dropped from the list here, because only a matching response would
            // otherwise remove it -- leaving it to accumulate for every fire-and-forget message.
            if (!request.ExpectsReply)
            {
                lock (CanRequests)
                {
                    CanRequests.Remove(request);
                }
                request.SetResult();
            }
        }
        catch
        {
            lock (CanRequests)
            {
                CanRequests.Remove(request);
            }
            throw;
        }

        try
        {
            if (request.ExpectsReply)
            {
                // If no reply is received within the timeout, the request will be canceled and an exception will be thrown
                await request.Task.WaitAsync(TimeSpan.FromMilliseconds(settings.Value.CanRequestTimeout), cancellationToken);
            }
            else
            {
                await request.Task.WaitAsync(cancellationToken);
            }
        }
        catch
        {
            lock (CanRequests)
            {
                CanRequests.Remove(request);
            }
            throw;
        }
        return CanResponse.FromRequest(request);
    }

    /// <summary>
    /// Allocate the next CAN transmission token, skipping <see cref="UnsolicitedTxToken"/>.
    /// Must be called while holding the <see cref="CanRequests"/> lock.
    /// </summary>
    /// <returns>Token to use for the next request</returns>
    private ushort NextCanTxToken()
    {
        ushort token = _canTxToken++;
        if (_canTxToken == UnsolicitedTxToken)
        {
            _canTxToken = 0;
        }
        return token;
    }

    /// <summary>
    /// Wait for all pending codes of the first or last stack item to finish
    /// </summary>
    /// <param name="channel">Code channel to wait for</param>
    /// <param name="flushAll">Flush everything</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async ValueTask<bool> FlushAsync(CodeChannel channel, bool flushAll, CancellationToken cancellationToken = default)
    {
        ValueTask<bool> flushTask;
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
    public async ValueTask<bool> FlushAsync(CodeFile file, CancellationToken cancellationToken = default)
    {
        ValueTask<bool> flushTask;
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
    public async ValueTask<bool> FlushAsync(Code code, CancellationToken cancellationToken = default)
    {
        ValueTask<bool> flushTask;
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

        using (await FirmwareActionLock.LockAsync(cancellationToken))
        {
            // Everything pending is void the moment the controller halts, so tear it down before the
            // request goes out rather than racing the halt
            InvalidateCodesCallback?.Invoke();
            await nativeLink.EmergencyStopAsync(cancellationToken);
        }
        logger.LogWarning("Emergency stop");
    }

    /// <summary>
    /// Perform a firmware reset and wait for it to finish
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task ResetFirmwareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using (await FirmwareActionLock.LockAsync(cancellationToken))
        {
            // A reset invalidates every resource, not just the code-related ones
            InvalidateCallback?.Invoke();
            await nativeLink.ResetAsync(cancellationToken);
        }
        logger.LogWarning("Resetting controller");
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
    /// <param name="cancellationToken">Optional cancellation token</param>
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

        // Hand off to LinkService, which owns the invalidation and object model changes an update implies
        FirmwareUpdateRequested.Release();
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
        if (nativeLink.ProtocolVersion == 1)
        {
            throw new NotSupportedException("Incompatible firmware version");
        }
        if (message.Length > settings.Value.MaxMessageLength)
        {
            throw new ArgumentException($"{nameof(message)} too long");
        }

        nativeLink.QueueMessage(flags, message);
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
        BytesReserved = BufferSpace = MaxReportedBufferSpace = 0;

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
        // Invalidate codes and code-relevant requests. Messages no longer need clearing here: they go
        // straight into the native outbound ring, which the transfer loop discards on a reset
        InvalidateCodes();
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
        BytesReserved = BufferSpace = MaxReportedBufferSpace = 0;

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
        // Invalidate codes and code-relevant requests. See Invalidate() on why messages need no
        // clearing here
        await InvalidateCodesAsync(cancellationToken);
    }
}
