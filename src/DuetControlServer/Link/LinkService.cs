using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace DuetControlServer.Link;

/// <summary>
/// This class accesses RepRapFirmware via SPI and deals with general communication
/// </summary>
/// <param name="channels">Channel manager</param>
/// <param name="eventLogger">Event logger</param>
/// <param name="jobProcessor">Job processor</param>
/// <param name="linkAdapter">Firmware link adapter</param>
/// <param name="linkInterface">Link interface</param>
/// <param name="model">Object model</param>
/// <param name="lifetime">Host application lifetime</param>
/// <param name="logger">Logger</param>
/// <param name="settings">Settings</param>
public sealed class LinkService(
    Channel.Manager channels,
    EventLogger eventLogger,
    JobProcessor jobProcessor,
    ILinkAdapter linkAdapter,
    LinkInterface linkInterface,
    Model.ObjectModel model,
    FilePathResolver filePathResolver,
    IHostApplicationLifetime lifetime,
    ILogger<LinkService> logger,
    IOptions<Settings> settings) : BackgroundService
{
    /// <summary>
    /// Open files requested by the firmware
    /// </summary>
    private readonly Dictionary<uint, FileStream> _openFiles = [];

    /// <summary>
    /// Handle counter for open files
    /// </summary>
    public uint _openFileHandleCounter = Consts.NoFileHandle;

    /// <summary>
    /// Perform the firmware update internally
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private void PerformFirmwareUpdate(CancellationToken cancellationToken = default)
    {
        using (model.AccessReadWrite(cancellationToken))
        {
            model.State.Status = MachineStatus.Updating;
        }

        // Get the CRC16 checksum of the firmware binary
        ushort crc16 = CRC16.Calculate(linkInterface.FirmwareStream!);

        // Send the IAP binary to the firmware. Cancellation is safe at this stage
        logger.LogInformation("Sending IAP binary");
        bool dataSent;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            dataSent = linkAdapter.WriteIapSegment(linkInterface.IapStream!, cancellationToken);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                Console.Write('.');
            }
        }
        while (dataSent);
        if (logger.IsEnabled(LogLevel.Debug))
        {
            Console.WriteLine();
        }

        // Start the IAP binary. This is the point of no return -- after this,
        // the board is running IAP and we must complete the firmware transfer
        // or the board will need manual recovery
        // The firmware length is sent as part of the USB handshake so IAP knows
        // exactly how many bytes to expect (SPI ignores this)
        uint firmwareLength = (uint)linkInterface.FirmwareStream!.Length;
        linkAdapter.StartIap(firmwareLength, cancellationToken);

        // From here on, do not honor the cancellation token for data transfer
        // Interrupting a flash-in-progress would brick the board
        // Only check cancellation between CRC retries as a last resort
        int numRetries = 0;
        do
        {
            if (numRetries != 0)
            {
                // Check cancellation between retries -- if DSF is being shut down
                // and the board is unresponsive, there's no point in retrying
                if (cancellationToken.IsCancellationRequested)
                {
                    eventLogger.LogOutput(MessageType.Error, "Firmware update cancelled during retry. The board may need manual recovery.");
                    logger.LogError("Firmware update cancelled during CRC retry");
                    throw new OperationCanceledException("Firmware update cancelled during retry");
                }
                logger.LogError("Firmware checksum verification failed");
            }

            logger.LogInformation("Updating RepRapFirmware");
            linkInterface.FirmwareStream!.Seek(0, SeekOrigin.Begin);

            try
            {
                while (linkAdapter.FlashFirmwareSegment(linkInterface.FirmwareStream))
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        Console.Write('.');
                    }
                }
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    Console.WriteLine();
                }
            }
            catch (Exception e)
            {
                eventLogger.LogOutput(MessageType.Error, "Failed to update firmware. Please install it manually.");
                logger.LogError(e, "Failed to update firmware");
                throw;
            }

            logger.LogInformation("Verifying checksum");
        }
        while (!linkAdapter.VerifyFirmwareChecksum(linkInterface.FirmwareStream.Length, crc16) && ++numRetries < 3);

        if (numRetries == 3)
        {
            // Failed to flash the firmware
            eventLogger.LogOutput(MessageType.Error, "Could not update firmware after 3 attempts. Please install it manually.");
            throw new OperationCanceledException("Failed to update firmware after 3 attempts");
        }

        // Wait for the IAP binary to restart the controller
        linkAdapter.WaitForIapReset();
        logger.LogInformation("Firmware update successful");
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Initialize the link interface
        linkAdapter.Connect(cancellationToken);

        // Run this service
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Start a thread that performs the communication with the firmware
    /// </summary>
    /// <remarks>
    /// This effectively starts a thread with higher priority in order to ensure
    /// that the communication with the controller is not blocked by other tasks
    /// </remarks>
    /// <param name="stoppingToken">Cancellation token</param>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread wrapper = new(() =>
        {
            try
            {
                if (settings.Value.IsolateInterfaceThread && DuetSharedLibrary.ProcessHelpers.IsRaspberryPi())
                {
                    if (DuetSharedLibrary.ProcessHelpers.PinCurrentThreadToCore(settings.Value.IsolatedCoreId))
                    {
                        logger.LogInformation("SPI thread pinned to CPU core {CoreId}", settings.Value.IsolatedCoreId);
                    }
                    else
                    {
                        logger.LogWarning("Failed to pin SPI thread to CPU core {CoreId}", settings.Value.IsolatedCoreId);
                    }
                }
                Execute(stoppingToken);
                tcs.SetResult();
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    if (ae.InnerException is OperationCanceledException)
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            tcs.SetResult();
                        }
                        else
                        {
                            tcs.SetCanceled();
                        }
                    }
                    else
                    {
                        tcs.SetException(ae.InnerException!);
                    }
                }
                else if (e is OperationCanceledException)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        tcs.SetResult();
                    }
                    else
                    {
                        tcs.SetCanceled();
                    }
                }
                else
                {
                    tcs.SetException(e);
                }
            }
        })
        {
            Name = "DuetControlServer LinkService",
            Priority = ThreadPriority.Highest,
            IsBackground = true
        };
        wrapper.Start();
        return tcs.Task;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        // Cancel the file being printed
        using (await jobProcessor.LockAsync(stoppingToken))
        {
            jobProcessor.Abort();
        }

        // Shut down the link subsystem
        await linkInterface.InvalidateAsync(stoppingToken);

        // Shut down this service. This terminates the transfer thread, which may still be serving file
        // requests, so the open files must not be closed before this call
        await base.StopAsync(stoppingToken);

        // Close all the files
        foreach (var kv in _openFiles)
        {
            await kv.Value.DisposeAsync();
        }
        _openFiles.Clear();
    }

    /// <summary>
    /// Perform communication with the RepRapFirmware controller over SPI
    /// </summary>
    /// <param name="stoppingToken">Cancellation token</param>
    private void Execute(CancellationToken stoppingToken)
    {
        do
        {
            bool skipChannels = false;
            using (linkInterface.FirmwareActionLock.Lock(stoppingToken))
            {
                // Check if an emergency stop has been requested
                if (linkInterface.FirmwareHaltRequest is not null)
                {
                    InvalidateCodes();
                    if (linkAdapter.WriteEmergencyStop())
                    {
                        logger.LogWarning("Emergency stop");
                        linkInterface.FirmwareHaltRequest.SetResult();
                        linkInterface.FirmwareHaltRequest = null;
                    }
                    skipChannels = true;
                }

                // Check if a firmware reset has been requested
                if (linkInterface.FirmwareResetRequest is not null)
                {
                    Invalidate();
                    if (linkAdapter.WriteReset())
                    {
                        logger.LogWarning("Resetting controller");
                        linkAdapter.PerformFullTransfer(cancellationToken: lifetime.ApplicationStopped);
                        linkInterface.FirmwareResetRequest.SetResult();
                        linkInterface.FirmwareResetRequest = null;
                        break;
                    }
                    skipChannels = true;
                }

                // Check if a CAN enable request has been made
                if (linkInterface.CanEnableRequest is not null && linkInterface.PendingCanEnable is not null)
                {
                    if (linkAdapter.WriteEnableCan(linkInterface.PendingCanEnable.Value))
                    {
                        logger.LogInformation("Sent CAN enable request: {Enable}", linkInterface.PendingCanEnable);
                        linkInterface.CanEnableRequest.SetResult();
                        linkInterface.CanEnableRequest = null;
                        linkInterface.PendingCanEnable = null;
                    }
                    else
                    {
                        logger.LogWarning("Failed to send CAN enable request: {Enable}", linkInterface.PendingCanEnable);
                    }
                }
            }

            // Check if a firmware update is supposed to be performed
            using (linkInterface.FirmwareUpdateLock.Lock(stoppingToken))
            {
                if (linkInterface.IapStream is not null && linkInterface.FirmwareStream is not null)
                {
                    Invalidate();

                    try
                    {
                        PerformFirmwareUpdate(lifetime.ApplicationStopped);
                        linkInterface.FirmwareUpdateRequest?.SetResult();
                        linkInterface.FirmwareUpdateRequest = null;
                    }
                    catch (Exception e)
                    {
                        linkInterface.FirmwareUpdateRequest?.SetException(e);
                        linkInterface.FirmwareUpdateRequest = null;
                        throw;
                    }

                    linkInterface.IapStream = linkInterface.FirmwareStream = null;
                    break;
                }
            }

            // Invalidate data if a controller reset has been performed
            if (linkAdapter.HadReset())
            {
                Invalidate();
                eventLogger.LogOutput(MessageType.Warning, "Connection to controller has been reset");
            }

            // Process incoming packets
            for (int i = 0; i < linkAdapter.PacketsToRead; i++)
            {
                try
                {
                    PacketHeader? packet = linkAdapter.ReadNextPacket();
                    if (packet is null)
                    {
                        logger.LogError("Read invalid packet");
                        break;
                    }
                    ProcessPacket(packet.Value);
                }
                catch (ArgumentOutOfRangeException)
                {
                    linkAdapter.DumpMalformedPacket();
                    throw;
                }
            }
            linkInterface.BytesReserved = 0;

#if false
            // Process pending codes, macro files and requests for resource locks/unlocks as well as flush requests
            if (!skipChannels)
            {
                channels.Spin();
            }
#endif

            // Request object model updates
            if (linkAdapter.ProtocolVersion == 1)
            {
                throw new Exception("Unsupported firmware version. Upgrade your firmware manually");
            }

            // Stage outgoing data and wait until there is a reason to perform another transfer. Data is
            // (re-)staged before every decision so that data queued while idle is sent in the next transfer
            // without triggering an empty one either before or after it
            do
            {
                // Send pending messages
                lock (linkInterface.MessagesToSend)
                {
                    while (linkInterface.MessagesToSend.TryPeek(out Tuple<MessageTypeFlags, string>? message))
                    {
                        if (linkAdapter.WriteMessage(message.Item1, message.Item2))
                        {
                            linkInterface.MessagesToSend.Dequeue();
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                // Send pending CAN messages
                if (!skipChannels)
                {
                    SendCanMessages();
                }
            }
            while (!linkAdapter.WaitForTransferReason(lifetime.ApplicationStopped));

            // Do another full SPI transfer
            linkAdapter.PerformFullTransfer(cancellationToken: lifetime.ApplicationStopped);
        }
        while (!stoppingToken.IsCancellationRequested);
    }

    /// <summary>
    /// Process a packet from RepRapFirmware
    /// </summary>
    /// <param name="packet">Received packet</param>
    /// <returns>Asynchronous task</returns>
    private void ProcessPacket(PacketHeader packet)
    {
        switch ((Request)packet.Request)
        {
            case Request.ResendPacket:
                linkAdapter.ResendPacket(packet, out Protocol.SbcRequests.Request sbcRequest);
                logger.LogWarning("Resending packet #{Id} (request {Request})", packet.Id, sbcRequest);
                break;
            case Request.CodeBufferUpdate:
                HandleCodeBufferUpdate();
                break;
            case Request.Message:
                HandleMessage();
                break;
            case Request.CANResponse:
                HandleCanResponse();
                break;
#if false
// TODO: re-enable these if we need them. Delete if not.
            case Request.WaitForAcknowledgement:
                HandleWaitForAcknowledgement();
                break;
            case Request.MessageAcknowledged:
                HandleMessageAcknowledgement();
                break;
#endif
        }
    }

    /// <summary>
    /// Send pending CAN requests to the firmware and complete those that expect no reply
    /// </summary>
    private void SendCanMessages()
    {
        lock (linkInterface.CanRequests)
        {
            // Write each unsent request once, in order, until the transfer buffer is full
            foreach (CanRequest request in linkInterface.CanRequests)
            {
                if (request.Sent)
                {
                    continue;
                }

                if (!linkAdapter.WriteCanMessage(request.TxToken, (ushort)request.MessageType, (ushort)request.ReplyType, request.DstAddress, request.Flags, request.RequestPayload))
                {
                    // Buffer full -- retry on the next transfer
                    logger.LogWarning("Could not send CAN request {TxToken} to address {DstAddress} (type {MessageType}) -- buffer full, will retry", request.TxToken, request.DstAddress, request.MessageType);
                    break;
                }
                logger.LogDebug("Sent CAN request {TxToken} to address {DstAddress} (type {MessageType})", request.TxToken, request.DstAddress, request.MessageType);
                request.Sent = true;
            }

            // Requests that expect no reply are complete as soon as they have been written
            linkInterface.CanRequests.RemoveAll(request =>
            {
                if (request.Sent && !request.ExpectsReply)
                {
                    request.SetResult();
                    return true;
                }
                return false;
            });
        }
    }

    /// <summary>
    /// Process a forwarded CAN message and complete the matching request once fully reassembled
    /// </summary>
    private void HandleCanResponse()
    {
        linkAdapter.ReadCanResponse(out ushort txToken, out CanMessageType msgType, out byte srcAddress, out byte flags, out CanStatus status, out byte[] payload);

        // Messages that are not a reply to one of our requests carry the reserved token
        if (txToken == LinkInterface.UnsolicitedTxToken)
        {
            HandleUnsolicitedCanMessage(msgType, srcAddress, flags, status, payload);
            return;
        }

        lock (linkInterface.CanRequests)
        {
            CanRequest? request = linkInterface.CanRequests.FirstOrDefault(r => r.TxToken == txToken);
            if (request is null)
            {
                // A non-reserved token with no matching request is unexpected (late/duplicate/timed-out reply)
                logger.LogWarning("Received CAN response with unknown token {TxToken}", txToken);
                return;
            }

            // The reply type must match what we expected when sending the request
            if (msgType != request.ReplyType)
            {
                logger.LogError("Received CAN response of type {MsgType} but expected {ReplyType}", msgType, request.ReplyType);
                request.SetException(new InvalidOperationException($"Expected CAN reply of type {request.ReplyType} but received {msgType}"));
                linkInterface.CanRequests.Remove(request);
                return;
            }

            // Propagate transport-level failures immediately
            if (status != CanStatus.Ok)
            {
                request.SetResult(status, msgType, srcAddress);
                linkInterface.CanRequests.Remove(request);
                return;
            }

            // Reassemble the (possibly fragmented) reply
            CanFragmentation.GetFragmentInfo(request.ReplyType, payload, out int fragmentNumber, out bool moreFollows, out ReadOnlySpan<byte> content);
            logger.LogDebug("Received CAN response fragment {FragmentNumber} of type {MsgType} from address {SrcAddress} ({Length} bytes, more follows: {MoreFollows})", fragmentNumber, msgType, srcAddress, content.Length, moreFollows);
            request.AddFragment(fragmentNumber, content);
            if (!moreFollows)
            {
                request.SetResult(status, msgType, srcAddress);
                linkInterface.CanRequests.Remove(request);
            }
        }
    }

    /// <summary>
    /// Process a CAN message that is not a reply to any outstanding request (reserved token 0xFFFF)
    /// </summary>
    /// <param name="msgType">Type of the received CAN message</param>
    /// <param name="srcAddress">Source address of the sending board</param>
    /// <param name="flags">Flags of the CAN message</param>
    /// <param name="status">Status of the CAN message</param>
    /// <param name="payload">CAN payload</param>
    private void HandleUnsolicitedCanMessage(CanMessageType msgType, byte srcAddress, byte flags, CanStatus status, byte[] payload)
    {
        // TODO: route unsolicited CAN messages (e.g. events, status reports, announcements) to their consumers
        logger.LogDebug("Received unsolicited CAN message of type {MsgType} from address {SrcAddress} ({Length} bytes)", msgType, srcAddress, payload.Length);

        // Route on the message type and deserialize straight into the concrete struct. Switching here rather than
        // on the runtime type keeps this allocation-free (no boxing) on a path that runs in the kHz range.
        switch (msgType)
        {
            case CanMessageType.FirmwareBlockRequest:
                HandleFirmwareBlockRequestAsync(CanMessageSerializer.Deserialize<CanMessageFirmwareUpdateRequest>(payload), srcAddress);
                break;
            default:
                logger.LogWarning("No unsolicited CAN handler implemented for message type {MsgType}", msgType);
                break;
        }
    }

    /// <summary>
    /// Update the amount of buffer space
    /// </summary>
    private void HandleCodeBufferUpdate()
    {
        linkAdapter.ReadCodeBufferUpdate(out ushort bufferSpace);
        linkInterface.BufferSpace = bufferSpace - linkInterface.BytesReserved;
        logger.LogTrace("Buffer space available: {BufferSpace}", linkInterface.BufferSpace);
    }

    /// <summary>
    /// Buffer for truncated log messages
    /// </summary>
    private string? _partialLogMessage;

    /// <summary>
    /// Process an incoming message
    /// </summary>
    private void HandleMessage()
    {
        linkAdapter.ReadMessage(out MessageTypeFlags flags, out string reply);
        logger.LogTrace("Received message [{Flags}] {Message}", flags, reply);

        // Deal with log messages
        if ((flags & MessageTypeFlags.LogOff) != MessageTypeFlags.LogOff)
        {
            _partialLogMessage += reply;
            if (!flags.HasFlag(MessageTypeFlags.PushFlag))
            {
                if (!string.IsNullOrWhiteSpace(_partialLogMessage))
                {
                    MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                                        : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                                            : MessageType.Success;
                    EventLogLevel level = flags.HasFlag(MessageTypeFlags.LogOff) ? EventLogLevel.Off
                                        : flags.HasFlag(MessageTypeFlags.LogWarn) ? EventLogLevel.Warn
                                            : flags.HasFlag(MessageTypeFlags.LogInfo) ? EventLogLevel.Info
                                                : EventLogLevel.Debug;
                    eventLogger.Log(level, type, _partialLogMessage.TrimEnd());
                }
                _partialLogMessage = null;
            }
        }

        // Check if this is a code reply
        if (flags.HasFlag(MessageTypeFlags.BinaryCodeReplyFlag))
        {
            if (!channels.HandleReply(flags, reply))
            {
                // Must be a left-over error message...
                OutputGenericMessage(flags, reply);
            }
        }
        else if ((flags & MessageTypeFlags.GenericMessage) == MessageTypeFlags.GenericMessage)
        {
            // Generic messages to the main object model
            OutputGenericMessage(flags, reply);
        }
        else
        {
            // Targeted messages are handled by the IPC processors
            MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                    : MessageType.Success;
            IPC.Processors.CodeStream.RecordMessage(flags, new Message(type, reply));
            IPC.Processors.ModelSubscription.RecordMessage(flags, new Message(type, reply));
        }
    }

    private async void HandleFirmwareBlockRequestAsync(CanMessageFirmwareUpdateRequest request, byte srcAddress, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Received firmware block request: FileOffset={FileOffset}, BootloaderVersion={BootloaderVersion}, UsesUf2Binary={UsesUf2Binary}, FileWanted={FileWanted}, LengthRequested={LengthRequested}, BoardType={BoardType}", request.FileOffset, request.BootloaderVersion, request.UsesUf2Binary, request.FileWanted, request.LengthRequested, request.BoardType);

        if (request.BootloaderVersion == CanMessageFirmwareUpdateRequest.BootloaderVersion0 && (request.FileWanted == 0 || request.FileWanted == 3))
        {
            // Firmware or bootloader requested
            string filename = request.FileWanted switch
            {
                0 => settings.Value.FirmwareFilePrefix,
                3 => settings.Value.BootloaderFilePrefix,
                _ => throw new InvalidOperationException($"Invalid FileWanted value: {request.FileWanted}")
            };

            // Add board type suffix
            filename += request.BoardTypeString;

            // Add file extension
            filename += request.UsesUf2Binary ? ".uf2" : ".bin";

            string filepath = await filePathResolver.ToPhysicalAsync(filename, FileDirectory.Firmware, cancellationToken);

            if (!File.Exists(filepath))
            {
                logger.LogError("Requested firmware file does not exist: {Filepath}", filepath);
                return;
            }

            using FileStream fs = new(filepath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (request.FileOffset >= fs.Length)
            {
                logger.LogError("Requested file offset {FileOffset} is beyond the end of the file {Filepath} (length {Length})", request.FileOffset, filepath, fs.Length);
                await linkInterface.SendCanMessageAsync(srcAddress, new CanMessageFirmwareUpdateResponse
                {
                    FileOffset = request.FileOffset,
                    DataLength = 0,
                    Err = CanMessageFirmwareUpdateResponse.ErrBadOffset,
                    FileLength = (uint)fs.Length,
                    Data = new CanMessageFirmwareUpdateResponseDataBuffer()
                });
                return;
            }

            fs.Seek(request.FileOffset, SeekOrigin.Begin);
            byte[] buffer = new byte[request.LengthRequested];
            int bytesRead = fs.Read(buffer, 0, buffer.Length);

            // Send the requested block back to the firmware
            CanMessageFirmwareUpdateResponse response = new()
            {
                FileOffset = request.FileOffset,
                DataLength = (uint)bytesRead,
                Err = CanMessageFirmwareUpdateResponse.ErrNone,
                FileLength = (uint)fs.Length,
                Data = new CanMessageFirmwareUpdateResponseDataBuffer()
            };
            // Array.Copy(buffer, 0, response.Data.AsSpan().ToArray(), 0, bytesRead);

            // await linkInterface.SendCanMessageAsync(srcAddress, response, CanMessageType.NoReply, cancellationToken: cancellationToken);
        }
        else
        {
            logger.LogWarning("Unsupported firmware update request: BootloaderVersion={BootloaderVersion}, FileWanted={FileWanted}", request.BootloaderVersion, request.FileWanted);
        }
    }


    /// <summary>
    /// Partial incoming message (if any)
    /// </summary>
    private static string? _partialGenericMessage;

    /// <summary>
    /// Output a generic message
    /// </summary>
    /// <param name="flags">Message flags</param>
    /// <param name="reply">Message content</param>
    private void OutputGenericMessage(MessageTypeFlags flags, string reply)
    {
        _partialGenericMessage += reply;
        if (!flags.HasFlag(MessageTypeFlags.PushFlag))
        {
            if (!string.IsNullOrWhiteSpace(_partialGenericMessage))
            {
                MessageType type = flags.HasFlag(MessageTypeFlags.ErrorMessageFlag) ? MessageType.Error
                                    : flags.HasFlag(MessageTypeFlags.WarningMessageFlag) ? MessageType.Warning
                                        : MessageType.Success;
                model.Output(type, _partialGenericMessage.TrimEnd());
            }
            _partialGenericMessage = null;
        }
    }

    /// <summary>
    /// Handle a firmware request to wait for a message to be acknowledged
    /// </summary>
    private void HandleWaitForAcknowledgement()
    {
        linkAdapter.ReadCodeChannel(out CodeChannel channel);
        logger.LogTrace("Received wait for message acknowledgement on channel {Channel}", channel);

        if (channel < CodeChannel.Unknown)
        {
            using (channels[channel].Lock())
            {
                channels[channel].WaitForAcknowledgement();
            }
        }
        else if (!settings.Value.UpdateOnly)
        {
            logger.LogError("Received wait for message acknowledgement on invalid channel {Channel}", channel);
        }
    }

    /// <summary>
    /// Handle a firmware request that is sent when RRF has successfully acknowledged a blocking message
    /// </summary>
    private void HandleMessageAcknowledgement()
    {
        linkAdapter.ReadCodeChannel(out CodeChannel channel);
        logger.LogTrace("Received message acknowledgement on channel {Channel}", channel);

        if (channel < CodeChannel.Unknown)
        {
            using (channels[channel].Lock())
            {
                channels[channel].MessageAcknowledged();
            }
        }
        else if (!settings.Value.UpdateOnly)
        {
            logger.LogError("Received message acknowledgement on invalid channel {Channel}", channel);
        }
    }

    /// <summary>
    /// Invalidate pending codes and code-relevant requests due to an emergency stop
    /// </summary>
    private void InvalidateCodes()
    {
        // Invalidate pending codes and code-relevant requests
        linkInterface.InvalidateCodes();

        // Cancel the file being printed (if any)
        using (jobProcessor.Lock())
        {
            jobProcessor.Abort();
        }

        // Resolve pending macros, unbuffered (system) codes and flush requests
        foreach (Channel.Processor channel in channels)
        {
            using (channel.Lock())
            {
                channel.Invalidate();
            }
        }
    }

    /// <summary>
    /// Invalidate every resource due to a disconnect or reset
    /// </summary>
    private void Invalidate()
    {
        // Invalidate codes and code-relevant requests
        InvalidateCodes();

        // Invalidate remaining link interface requests
        linkInterface.Invalidate();

        // Close all the files
        foreach (var kv in _openFiles)
        {
            kv.Value.Dispose();
        }
        _openFiles.Clear();

        // Notify the updater task about the lost connection
        model.ConnectionLost();
    }
}
