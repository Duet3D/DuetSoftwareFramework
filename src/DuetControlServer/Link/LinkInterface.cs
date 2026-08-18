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
/// <param name="nativeLink">Native SPI transfer loop</param>
/// <param name="logger">Logger instance</param>
/// <param name="settings">Settings</param>
[DiagnosticsPriority(-5)]
public sealed partial class LinkInterface(
    NativeLink nativeLink,
    ILogger<LinkInterface> logger,
    IOptions<Settings> settings) : IDiagnostics
{
    /// <summary>
    /// The native SPI transfer loop and motion engine
    /// </summary>
    public NativeLink Native => nativeLink;

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

    /// <summary>
    /// Print diagnostics of this class
    /// </summary>
    /// <param name="builder">String builder</param>
    /// <returns>Asynchronous task</returns>
    public void PrintDiagnostics(StringBuilder builder)
    {
        builder.AppendLine($"Code buffer space: {BufferSpace}");

        // Every move is scheduled by absolute start time in the controller's step clock, which this
        // side has no counter for and fits to the samples the controller sends. Whether that fit has
        // taken is not visible anywhere else, and an unfitted clock does not stop anything working
        // until an endstop fires and the position it reverts to has no relation to where it stopped
        NativeClockStats clock = Native.GetClockStats();
        builder.AppendLine(clock.Synced != 0
            ? $"Step clock: synchronised, {clock.NumSamples} samples, drift {clock.DriftPpm:F1}ppm, "
              + $"peak residual {clock.PeakResidualNs / 1000}us, {clock.NumBackwardClamps} clamps, "
              + $"{clock.NumRejectedSamples} rejected"
            : $"Step clock: NOT synchronised, {clock.NumSamples} samples, {clock.NumRejectedSamples} rejected");

        // Reported next to the clock because it is part of reading it: moves are timed in the
        // movement timebase and an endstop reports its trigger in the raw one, so this is the gap
        // between the two. It only ever grows, and it grows silently - every board slips by the same
        // amount, so nothing about the motion looks wrong while it does
        if (Native.GetMovementDelay() is uint movementDelay)
        {
            builder.AppendLine($"Movement delay: {movementDelay} ticks "
                               + $"({movementDelay * 1000.0 / Motion.Native.MotionLimits.StepClockRate:F1}ms)");
        }
    }

    /// <summary>
    /// Set the CAN address and bit timing of an expansion board, which it saves in non-volatile memory
    /// </summary>
    /// <param name="dstAddress">Address the board has now</param>
    /// <param name="newAddress">Address to give it, or null to leave the address alone</param>
    /// <param name="timing">Arbitration phase bit timing to give it</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>The board's reply</returns>
    public Task<CanResponse> ConfigCanAsync(byte dstAddress, byte? newAddress, CanTiming timing, CancellationToken cancellationToken = default)
    {
        CanMessageSetAddressAndNormalTiming message = new()
        {
            OldAddress = dstAddress,
            NewAddress = newAddress ?? dstAddress,
            NewAddressInverted = (byte)~(newAddress ?? dstAddress),
            DoSetTiming = CanMessageSetAddressAndNormalTiming.DoSetTimingYes,
            NormalTiming = timing
        };

        return SendCanMessageAsync(dstAddress, in message, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Ask an expansion board to report its CAN address and bit timing, changing neither
    /// </summary>
    /// <param name="dstAddress">CAN address of the board</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>The board's reply</returns>
    public Task<CanResponse> ReportCanConfigAsync(byte dstAddress, CancellationToken cancellationToken = default)
    {
        CanMessageSetAddressAndNormalTiming message = new()
        {
            OldAddress = dstAddress,
            DoSetTiming = CanMessageSetAddressAndNormalTiming.DoSetTimingNo
        };

        return SendCanMessageAsync(dstAddress, message, CanMessageType.StandardReply, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Turn the controller's CAN interface on or off
    /// </summary>
    /// <param name="enable">True to turn it on</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>An awaitable task that completes once the request has been written to the controller</returns>
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
    /// <param name="dstAddress">CAN destination: up to <see cref="CanId.MaxCanAddress" />, or <see cref="CanId.BroadcastAddress" /></param>
    /// <param name="message">CAN message body to send</param>
    /// <param name="replyType">Expected reply type (<see cref="CanMessageType.NoReply"/> if none)</param>
    /// <param name="isResponse">True if this message is a response to something an expansion board sent, rather than a request</param>
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
    /// Repackage a G-code as the generic CAN message its parameter table describes, and send it
    /// </summary>
    /// <typeparam name="TReq">Type of the CAN message body</typeparam>
    /// <param name="dstAddress">CAN address of the board that will act on it</param>
    /// <param name="code">The code whose parameters the message carries</param>
    /// <param name="replyType">Expected reply type</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Reassembled reply</returns>
    /// <remarks>
    /// <para>
    /// A generic message <em>is</em> its parameter table: the table says which G-code letters it
    /// carries and in what form, so turning a code into one is a repackaging rather than a
    /// translation. That makes it a property of the wire format, which is why it lives here - a
    /// handler that built its own would be reimplementing the format one code at a time.
    /// </para>
    /// <para>
    /// The reply comes back as a <see cref="CanResponse"/> rather than a message, because what the
    /// board said and how a code should report it are different questions and only the handler knows
    /// the second
    /// </para>
    /// </remarks>
    public Task<CanResponse> SendCodeAsync<TReq>(byte dstAddress, Code code,
                                                 CanMessageType replyType = CanMessageType.StandardReply,
                                                 CancellationToken cancellationToken = default)
        where TReq : struct, ICanGenericMessage<TReq>
    {
        TReq message = default;
        message.FromCode(code);
        return SendCanMessageAsync(dstAddress, in message, replyType, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Send a raw CAN message to an expansion board and wait for the (optional) reply
    /// </summary>
    /// <param name="messageType">Type of the CAN message</param>
    /// <param name="replyType">Expected reply type (<see cref="CanMessageType.NoReply"/> if none)</param>
    /// <param name="dstAddress">CAN destination: up to <see cref="CanId.MaxCanAddress" />, or <see cref="CanId.BroadcastAddress" /></param>
    /// <param name="payload">Serialized CAN message payload</param>
    /// <param name="isResponse">True if this message is a response to something an expansion board sent, rather than a request</param>
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
            uint sequenceNumber = nativeLink.QueueCanMessage(request.TxToken, (ushort)request.MessageType, (ushort)request.ReplyType,
                request.DstAddress, request.IsResponse, request.RequestPayload);
            request.Sent = true;

            // A request expecting no reply has no CanResponse event to resolve it, so what completes it
            // is the transfer that carried it reaching the controller. Taking the message out of the
            // ring is a memcpy, and a request resolved on that is one the caller believes was sent when
            // the link may drop before it ever is
            if (!request.ExpectsReply)
            {
                try
                {
                    await nativeLink.WaitForDeliveryAsync(sequenceNumber, cancellationToken);
                }
                finally
                {
                    lock (CanRequests)
                    {
                        CanRequests.Remove(request);
                    }
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
    /// Record what became of a CAN message the controller was asked to send
    /// </summary>
    /// <param name="txToken">Token the message was queued with</param>
    /// <param name="status">Outcome the controller reported</param>
    /// <remarks>
    /// A message expecting no reply is complete here: this is the furthest anything can say it got.
    /// One expecting a reply is only failed here - a reply it can no longer receive is one it would
    /// otherwise wait out the whole timeout for
    /// </remarks>
    internal void CompleteCanMessageSent(ushort txToken, Protocol.FirmwareRequests.CanStatus status)
    {
        CanRequest? request = null;
        lock (CanRequests)
        {
            foreach (CanRequest candidate in CanRequests)
            {
                if (candidate.TxToken == txToken)
                {
                    request = candidate;
                    break;
                }
            }

            if (request is null || (request.ExpectsReply && status == Protocol.FirmwareRequests.CanStatus.Ok))
            {
                return;
            }
            CanRequests.Remove(request);
        }

        if (status == Protocol.FirmwareRequests.CanStatus.Ok)
        {
            request.SetResult();
        }
        else
        {
            request.SetException(new IOException($"Controller could not send CAN message: {status}"));
        }
    }

    /// <summary>
    /// Invalidate pending codes and code-relevant requests due to an emergency stop
    /// </summary>
    internal void InvalidateCodes()
    {
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
        // Invalidate codes and code-relevant requests. Messages no longer need clearing here: they go
        // straight into the native outbound ring, which the transfer loop discards on a reset
        InvalidateCodes();
    }

}
