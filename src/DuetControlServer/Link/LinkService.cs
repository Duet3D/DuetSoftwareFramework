using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Events;
using DuetControlServer.Files;
using DuetControlServer.Link.Native;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuetControlServer.Link;

/// <summary>
/// Drives the native SPI transfer loop and dispatches everything it reports
/// </summary>
/// <remarks>
/// <para>
/// The SPI protocol itself lives in C++ (<c>src/DuetSbcInterface</c>): it owns the transfer state
/// machine and runs it on a pinned, real-time thread. This service starts that loop and then runs a
/// single normal-priority dispatcher thread which drains the native inbound ring and hands each event
/// to the same handlers DCS has always used.
/// </para>
/// <para>
/// The split matters: because the dispatcher is an ordinary managed thread, managed allocation, lock
/// acquisition and GC pauses all happen here rather than on the real-time thread, so none of them can
/// stall an SPI transfer in flight.
/// </para>
/// </remarks>
/// <param name="eventLogger">Event logger</param>
/// <param name="expansionBoardManager">Receiver for expansion board status reports</param>
/// <param name="macroRunner">Runs macro files</param>
/// <param name="jobController">Job controller</param>
/// <param name="events">Events waiting to be dealt with</param>
/// <param name="eventProcessor">Event processor, for the reconnect default action</param>
/// <param name="nativeLink">Native SPI transfer loop</param>
/// <param name="linkInterface">Link interface</param>
/// <param name="model">Object model</param>
/// <param name="filePathResolver">File path resolver</param>
/// <param name="motionTracker">Where what the native motion engine reports is recorded</param>
/// <param name="planner">Holds the index of which job code each queued move came from</param>
/// <param name="endstopCorrection">Applies the position an endstop actually fired at</param>
/// <param name="lifetime">Host application lifetime</param>
/// <param name="logger">Logger</param>
/// <param name="settings">Settings</param>
internal sealed class LinkService(
    EventLogger eventLogger,
    Expansion.ExpansionBoardManager expansionBoardManager,
    MacroRunner macroRunner,
    Files.Job.JobController jobController,
    Events.EventQueue events,
    Events.EventProcessor eventProcessor,
    NativeLink nativeLink,
    LinkInterface linkInterface,
    Model.ObjectModel model,
    FilePathResolver filePathResolver,
    Motion.MotionTracker motionTracker,
    Motion.MovePlanner planner,
    Motion.EndstopCorrection endstopCorrection,
    IHostApplicationLifetime lifetime,
    ILogger<LinkService> logger,
    IOptions<Settings> settings) : BackgroundService
{
    /// <summary>
    /// How long the dispatcher blocks waiting for a native event before looping to re-check shutdown
    /// </summary>
    private const int EventWaitTimeout = 250;

    /// <summary>
    /// Open files requested by the firmware
    /// </summary>
    private readonly Dictionary<uint, FileStream> _openFiles = [];

    /// <summary>
    /// Handle counter for open files
    /// </summary>
    public uint _openFileHandleCounter = Consts.NoFileHandle;

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // An emergency stop or reset raised through LinkInterface has to tear down resources this
        // service owns (job processor, channel processors, open files), so hand it the entry points
        linkInterface.InvalidateCodesCallback = InvalidateCodes;
        linkInterface.InvalidateCallback = Invalidate;

        // A controller that comes back has to be configured again. controller-reconnect.g replaces
        // this when a machine has one, which is what lets it home or resume instead
        eventProcessor.ReconnectDefaultAction = _ => RunStartupFilesAsync();

        // Create the native interface and complete the initial handshake. This throws if the
        // controller is absent or fundamentally incompatible, which is worth failing startup over
        nativeLink.Connect();

        // Run this service
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Start the native transfer loop and dispatch the events it produces
    /// </summary>
    /// <param name="stoppingToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Watch for firmware update requests alongside the dispatcher
        _ = WatchForFirmwareUpdatesAsync(stoppingToken);

        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread dispatcher = new(() =>
        {
            try
            {
                // The native loop places its own thread on an isolated core at real-time priority;
                // this dispatcher deliberately stays an ordinary thread
                nativeLink.Start();
                Dispatch(stoppingToken);
                tcs.SetResult();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                tcs.SetResult();
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        })
        {
            Name = "DuetControlServer LinkService",
            IsBackground = true
        };
        dispatcher.Start();
        return tcs.Task;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        // Cancel the file being printed
        jobController.Abort();

        // Shut down the link subsystem
        linkInterface.Invalidate();

        // Stop the native transfer loop, which releases the dispatcher from its wait
        nativeLink.Stop();

        // Shut down this service. This terminates the dispatcher, which may still be serving file
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
    /// Drain the native inbound ring and dispatch every event until shutdown
    /// </summary>
    /// <param name="stoppingToken">Cancellation token</param>
    private void Dispatch(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Block until the native loop reports something. The timeout only exists so shutdown is
            // noticed promptly; the native side wakes us as soon as an event is posted
            if (!nativeLink.WaitForEvent(EventWaitTimeout))
            {
                continue;
            }

            while (!stoppingToken.IsCancellationRequested && nativeLink.TryReadEvent(out ReadOnlySpan<byte> record))
            {
                try
                {
                    ProcessEvent(record);
                }
                catch (Exception e)
                {
                    // A handler failing must not wedge the dispatcher; the link itself is still fine
                    logger.LogError(e, "Failed to process native link event");
                }
                finally
                {
                    nativeLink.ConsumeEvent();
                }
            }
        }
    }

    /// <summary>
    /// Dispatch a single event record read from the native inbound ring
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void ProcessEvent(ReadOnlySpan<byte> record)
    {
        if (record.Length < Marshal.SizeOf<InboundEventHeader>())
        {
            logger.LogError("Discarding truncated native link event ({Length} bytes)", record.Length);
            return;
        }

        InboundEventHeader header = MemoryMarshal.Read<InboundEventHeader>(record);
        switch ((InboundEventType)header.Type)
        {
            case InboundEventType.Message:
                HandleMessage(record);
                break;
            case InboundEventType.CanResponse:
                HandleCanResponse(record);
                break;
            case InboundEventType.CodeBufferUpdate:
                HandleCodeBufferUpdate(record);
                break;
            case InboundEventType.ControllerReset:
                Invalidate();
                eventLogger.LogOutput(MessageType.Warning, "Connection to controller has been reset");

                // A reboot quick enough to fit inside one connection timeout is an outage the timeout
                // never saw, so this is the only signal there is for it
                RaiseControllerDisconnect(ControllerResetCause, "the controller reset");
                break;
            case InboundEventType.ConnectionLost:
                HandleConnectionLost(record);
                break;
            case InboundEventType.ConnectionEstablished:
                HandleConnectionEstablished(record);
                break;
            case InboundEventType.RequestCompleted:
                HandleRequestCompleted(record);
                break;
            case InboundEventType.Log:
                HandleLog(record);
                break;
            case InboundEventType.MalformedPacket:
                DumpMalformedPacket(record);
                break;
            case InboundEventType.MoveCompleted:
                HandleMoveCompleted(record);
                break;
            case InboundEventType.MoveFailed:
                HandleMoveFailed(record);
                break;
            case InboundEventType.MotionStopped:
                HandleMotionStopped(record);
                break;
            case InboundEventType.CanMessagesSent:
                HandleCanMessagesSent(record);
                break;
            case InboundEventType.OutboundDelivered:
            case InboundEventType.OutboundDropped:
                nativeLink.CompleteOutbound(MemoryMarshal.Read<OutboundSeqEvent>(record).SequenceNumber,
                                            (InboundEventType)header.Type == InboundEventType.OutboundDelivered);
                break;
            case InboundEventType.FatalError:
                HandleFatalError(record);
                break;
            default:
                logger.LogWarning("Received unknown native link event type {Type}", header.Type);
                break;
        }
    }

    /// <summary>
    /// Decode the UTF-8 tail that follows a fixed-size event header
    /// </summary>
    /// <typeparam name="T">Header type</typeparam>
    /// <param name="record">Raw event record</param>
    /// <returns>Decoded text</returns>
    private static string ReadTailString<T>(ReadOnlySpan<byte> record) where T : struct
    {
        int headerSize = Marshal.SizeOf<T>();
        return record.Length > headerSize ? Encoding.UTF8.GetString(record[headerSize..]) : string.Empty;
    }

    /// <summary>
    /// Handle the link coming up
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void HandleConnectionEstablished(ReadOnlySpan<byte> record)
    {
        ConnectionEstablishedEvent connectionEvent = MemoryMarshal.Read<ConnectionEstablishedEvent>(record);
        nativeLink.SetProtocolVersion(connectionEvent.ProtocolVersion);

        if (connectionEvent.ProtocolVersion != Consts.ProtocolVersion)
        {
            eventLogger.LogOutput(MessageType.Warning, "Incompatible firmware, please upgrade as soon as possible");
        }
        eventLogger.LogOutput(MessageType.Success, "Connection to Duet established");

        if (_controllerDown)
        {
            // Coming back rather than starting up. The macro decides what that means for this machine,
            // and running config.g is what happens when it has nothing to say - see §4.3 of
            // docs/devel/EVENTS_MIGRATION.md for why the recovery does not live in the macro alone
            _controllerDown = false;
            events.Raise(new MachineEvent(EventType.ControllerReconnect, connectionEvent.HadReset,
                                          CanId.MasterAddress, 0, string.Empty));
            return;
        }

        // The machine is only configured once config.g has run, and nothing else runs it
        _ = RunStartupFilesAsync();
    }

    /// <summary>
    /// Run the files that configure the machine, in the order RepRapFirmware runs them
    /// </summary>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// config.g is what turns an unconfigured process into a machine: until it has run there are no
    /// axes, no drivers and no way to move. It runs on the trigger channel, as it does in
    /// RepRapFirmware, so that it does not consume the job or user channels. runonce.g follows it and
    /// is deleted afterwards, which is the whole point of it
    /// </remarks>
    private async Task RunStartupFilesAsync()
    {
        try
        {
            // The link is up, so whatever the status was while it was not, it is not that now
            model.IsDisconnected = false;

            if (!await macroRunner.TryRunAsync(CodeChannel.Trigger, FilePathResolver.ConfigFile,
                                               cancellationToken: lifetime.ApplicationStopping) &&
                !await macroRunner.TryRunAsync(CodeChannel.Trigger, FilePathResolver.ConfigFileFallback,
                                               cancellationToken: lifetime.ApplicationStopping))
            {
                eventLogger.LogOutput(MessageType.Warning, "Configuration file not found, the machine is unconfigured");
                return;
            }

            if (await macroRunner.TryRunAsync(CodeChannel.Trigger, FilePathResolver.RunOnceFile,
                                              cancellationToken: lifetime.ApplicationStopping))
            {
                // runonce.g is meant to run exactly once, so it removes itself
                try
                {
                    System.IO.File.Delete(await filePathResolver.ToPhysicalAsync(FilePathResolver.RunOnceFile, FileDirectory.System,
                                                                                 lifetime.ApplicationStopping));
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Failed to delete {File} after running it", FilePathResolver.RunOnceFile);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to run the startup files");
        }
        finally
        {
            // Starting ends when the startup files have been run, whether or not they were found:
            // the machine is as configured as it is going to get, so reporting it as still starting
            // would leave a machine with no config.g looking permanently mid-boot
            model.IsStarting = false;
        }
    }

    /// <summary>
    /// Handle the link dropping
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void HandleConnectionLost(ReadOnlySpan<byte> record)
    {
        string reason = ReadTailString<InboundEventHeader>(record);
        logger.LogDebug("Lost connection to Duet: {Reason}", reason);

        Invalidate();
        RaiseControllerDisconnect(TimeoutCause, reason);
    }

    /// <summary>
    /// Cause of a disconnect that the link timed out
    /// </summary>
    private const ushort TimeoutCause = 0;

    /// <summary>
    /// Cause of a disconnect noticed only because the controller had reset
    /// </summary>
    private const ushort ControllerResetCause = 1;

    /// <summary>
    /// Whether the controller is currently away
    /// </summary>
    /// <remarks>
    /// Both signals that say so can arrive for one outage, and a slow one will have finished running
    /// the disconnect macro long before the second does - so the queue's own suppression, which only
    /// covers an event still waiting, is not enough to keep it to one
    /// </remarks>
    private bool _controllerDown;

    /// <summary>
    /// Raise the disconnect event, at most once for an outage
    /// </summary>
    /// <param name="cause">What noticed the outage</param>
    /// <param name="reason">What to tell the operator</param>
    private void RaiseControllerDisconnect(ushort cause, string reason)
    {
        if (_controllerDown)
        {
            return;
        }
        _controllerDown = true;
        events.Raise(new MachineEvent(EventType.ControllerDisconnect, cause, CanId.MasterAddress, 0, reason));
    }

    /// <summary>
    /// Resolve the CAN messages the controller has dealt with
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void HandleCanMessagesSent(ReadOnlySpan<byte> record)
    {
        CanMessagesSentEvent sent = MemoryMarshal.Read<CanMessagesSentEvent>(record);
        ReadOnlySpan<byte> entries = record[Marshal.SizeOf<CanMessagesSentEvent>()..];
        for (int i = 0; i < sent.Count; i++)
        {
            CanMessageSentEntry entry = MemoryMarshal.Read<CanMessageSentEntry>(entries[(i * Marshal.SizeOf<CanMessageSentEntry>())..]);
            linkInterface.CompleteCanMessageSent(entry.TxToken, (CanStatus)entry.Status);
        }
    }

    /// <summary>
    /// Resolve a request the native loop has finished serving
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void HandleRequestCompleted(ReadOnlySpan<byte> record)
    {
        RequestCompletedEvent completed = MemoryMarshal.Read<RequestCompletedEvent>(record);
        string? error = record.Length > Marshal.SizeOf<RequestCompletedEvent>()
            ? ReadTailString<RequestCompletedEvent>(record)
            : null;
        nativeLink.CompleteRequest(completed.RequestId, (RequestResult)completed.Result, error);
    }

    /// <summary>
    /// Log a diagnostic reported by the native transfer loop
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void HandleLog(ReadOnlySpan<byte> record)
    {
        LogEvent logEvent = MemoryMarshal.Read<LogEvent>(record);
        string message = ReadTailString<LogEvent>(record);

        switch ((NativeLogLevel)logEvent.Level)
        {
            case NativeLogLevel.Debug:
                logger.LogDebug("{Message}", message);
                break;
            case NativeLogLevel.Info:
                logger.LogInformation("{Message}", message);
                break;
            case NativeLogLevel.Warning:
                logger.LogWarning("{Message}", message);
                break;
            default:
                logger.LogError("{Message}", message);
                break;
        }
    }

    /// <summary>
    /// Handle an unrecoverable error from the native loop by terminating the application
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void HandleFatalError(ReadOnlySpan<byte> record)
    {
        string message = ReadTailString<InboundEventHeader>(record);
        logger.LogError("Fatal error in native SPI interface: {Message}", message);
        eventLogger.LogOutput(MessageType.Error, $"Fatal SPI error: {message}");
        lifetime.StopApplication();
    }

    /// <summary>
    /// Handle a queued move finishing execution
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void HandleMoveCompleted(ReadOnlySpan<byte> record)
    {
        if (record.Length < Marshal.SizeOf<MoveCompletedEvent>())
        {
            logger.LogError("Discarding truncated MoveCompleted event ({Length} bytes)", record.Length);
            return;
        }

        MoveCompletedEvent moveEvent = MemoryMarshal.Read<MoveCompletedEvent>(record);
        motionTracker.MoveCompleted(moveEvent.Ring, moveEvent.MoveId, moveEvent.CompletedMoves);
    }

    /// <summary>
    /// Handle a move that was rejected or could not be executed
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void HandleMoveFailed(ReadOnlySpan<byte> record)
    {
        if (record.Length < Marshal.SizeOf<MoveFailedEvent>())
        {
            logger.LogError("Discarding truncated MoveFailed event ({Length} bytes)", record.Length);
            return;
        }

        MoveFailedEvent moveEvent = MemoryMarshal.Read<MoveFailedEvent>(record);
        motionTracker.MoveFailed(moveEvent.Ring, moveEvent.MoveId, moveEvent.Error);
    }

    /// <summary>
    /// Undo the overshoot of a move an endstop cut short
    /// </summary>
    /// <param name="record">Raw event record</param>
    /// <remarks>
    /// The controller stopped the drives but cannot say where they should end up - it never generated
    /// the steps. This is its report, and <see cref="Motion.EndstopCorrection"/> is what turns it into
    /// a position and a message telling the boards to wind back
    /// </remarks>
    private void HandleMotionStopped(ReadOnlySpan<byte> record)
    {
        int headerSize = Marshal.SizeOf<MotionStoppedEvent>();
        if (record.Length < headerSize)
        {
            logger.LogError("Discarding truncated MotionStopped event ({Length} bytes)", record.Length);
            return;
        }

        MotionStoppedEvent stoppedEvent = MemoryMarshal.Read<MotionStoppedEvent>(record);
        ReadOnlySpan<byte> tail = record[headerSize..];
        int entrySize = Marshal.SizeOf<MotionStoppedDriverEntry>();
        if (tail.Length < stoppedEvent.NumDrivers * entrySize)
        {
            logger.LogError(
                "Discarding MotionStopped event claiming {NumDrivers} drivers but carrying {Length} bytes",
                stoppedEvent.NumDrivers, tail.Length);
            return;
        }

        ReadOnlySpan<MotionStoppedDriverEntry> drivers =
            MemoryMarshal.Cast<byte, MotionStoppedDriverEntry>(tail[..(stoppedEvent.NumDrivers * entrySize)]);
        endstopCorrection.Apply(stoppedEvent.WhenTriggered, stoppedEvent.MoveId, drivers);
    }

    /// <summary>
    /// Write a malformed packet to disk and log it for diagnostic purposes
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void DumpMalformedPacket(ReadOnlySpan<byte> record)
    {
        MalformedPacketEvent packet = MemoryMarshal.Read<MalformedPacketEvent>(record);
        ReadOnlySpan<byte> packetData = record[Marshal.SizeOf<MalformedPacketEvent>()..];

        try
        {
            using FileStream stream = new(Path.Combine(settings.Value.BaseDirectory, "sys/transferDump.bin"), FileMode.Create, FileAccess.Write);
            stream.Write(packetData);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to write transfer dump");
        }

        StringBuilder dump = new();
        dump.AppendLine($"=== Packet #{packet.PacketId} from offset {packet.Offset} request {packet.Request} (length {packet.Length}) ===");
        foreach (byte c in packetData)
        {
            dump.Append(c.ToString("x2"));
        }
        dump.AppendLine();
        foreach (char c in Encoding.UTF8.GetString(packetData))
        {
            dump.Append(char.IsLetterOrDigit(c) ? c : '.');
        }
        dump.AppendLine();
        dump.Append("====================");
        logger.LogError("Received malformed packet: {SpiDump}", dump.ToString());
    }

    /// <summary>
    /// Update the amount of buffer space
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void HandleCodeBufferUpdate(ReadOnlySpan<byte> record)
    {
        CodeBufferEvent bufferEvent = MemoryMarshal.Read<CodeBufferEvent>(record);
        linkInterface.BufferSpace = bufferEvent.BufferSpace - linkInterface.BytesReserved;
        logger.LogTrace("Buffer space available: {BufferSpace}", linkInterface.BufferSpace);
    }

    /// <summary>
    /// Buffer for truncated log messages
    /// </summary>
    private string? _partialLogMessage;

    /// <summary>
    /// Process an incoming message
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void HandleMessage(ReadOnlySpan<byte> record)
    {
        MessageEvent messageEvent = MemoryMarshal.Read<MessageEvent>(record);
        MessageTypeFlags flags = (MessageTypeFlags)messageEvent.Flags;
        string reply = ReadTailString<MessageEvent>(record);
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
            // Codes are resolved where they are executed now, so nothing is waiting to be matched
            // up with a reply arriving separately. Anything still flagged as one is an unsolicited
            // message from the link itself
            OutputGenericMessage(flags, reply);
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

    /// <summary>
    /// Process a forwarded CAN message and complete the matching request once fully reassembled
    /// </summary>
    /// <param name="record">Raw event record</param>
    private void HandleCanResponse(ReadOnlySpan<byte> record)
    {
        CanResponseEvent response = MemoryMarshal.Read<CanResponseEvent>(record);
        int headerSize = Marshal.SizeOf<CanResponseEvent>();
        byte[] payload = record.Length > headerSize ? record[headerSize..].ToArray() : [];

        ushort txToken = response.TxToken;
        CanMessageType msgType = (CanMessageType)response.MsgType;
        byte srcAddress = response.SrcAddress;
        CanStatus status = (CanStatus)response.Status;

        // Messages that are not a reply to one of our requests carry the reserved token
        if (txToken == LinkInterface.UnsolicitedTxToken)
        {
            HandleUnsolicitedCanMessage(msgType, srcAddress, response.Flags, status, payload);
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
            CanFragment fragment = CanFragmentation.Parse(request.ReplyType, payload);
            logger.LogDebug("Received CAN response fragment {FragmentNumber} of type {MsgType} from address {SrcAddress} ({Length} bytes, result {ResultCode}, more follows: {MoreFollows})", fragment.Number, msgType, srcAddress, fragment.Content.Length, fragment.ResultCode, fragment.MoreFollows);
            request.AddFragment(in fragment);
            if (!fragment.MoreFollows)
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
        logger.LogTrace("Received unsolicited CAN message of type {MsgType} from address {SrcAddress} ({Length} bytes)", msgType, srcAddress, payload.Length);

        // The status reports the expansion boards broadcast are decoded and applied to the object
        // model on the board manager's own task, so nothing is deserialized on this thread
        if (expansionBoardManager.TryEnqueue(msgType, srcAddress, payload))
        {
            return;
        }

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

    private async void HandleFirmwareBlockRequestAsync(CanMessageFirmwareUpdateRequest request, byte srcAddress, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Received firmware block request: FileOffset={FileOffset}, BootloaderVersion={BootloaderVersion}, Uf2Format={Uf2Format}, FileWanted={FileWanted}, LengthRequested={LengthRequested}, BoardType={BoardType}", request.FileOffset, request.BootloaderVersion, request.Uf2Format, request.FileWanted, request.LengthRequested, request.BoardType);

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
            filename += request.Uf2Format ? ".uf2" : ".bin";
            uint fileOffset = request.FileOffset;
            uint lengthRequested = request.LengthRequested;

            string filepath = await filePathResolver.ToPhysicalAsync(filename, FileDirectory.Firmware, cancellationToken);

            if (!File.Exists(filepath))
            {
                logger.LogError("Requested firmware file does not exist: {Filepath}", filepath);
                return;
            }

            using FileStream fs = new(filepath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fileOffset >= fs.Length)
            {
                logger.LogError("Requested file offset {FileOffset} is beyond the end of the file {Filepath} (length {Length})", fileOffset, filepath, fs.Length);
                await linkInterface.SendCanMessageAsync(srcAddress, new CanMessageFirmwareUpdateResponse
                {
                    FileOffset = fileOffset,
                    DataLength = 0,
                    Err = (byte)CanMessageFirmwareUpdateResponse.ErrBadOffset,
                    FileLength = (uint)fs.Length,
                    Data = new ByteArray56()
                },
                isResponse: true,
                cancellationToken: cancellationToken);
                return;
            }

            fs.Seek(fileOffset, SeekOrigin.Begin);
            if (fs.Length - fileOffset < lengthRequested)
            {
                lengthRequested = (uint)(fs.Length - fileOffset);
            }

            for (;;)
            {
                uint lengthToSend = Math.Min(lengthRequested, ByteArray56.Length);

                byte[] buffer = new byte[lengthToSend];
                int bytesRead = fs.Read(buffer, 0, buffer.Length);

                if (bytesRead != lengthToSend)
                {
                    logger.LogError("Read {BytesRead} bytes from firmware file {Filepath} but expected {LengthToSend} bytes", bytesRead, filepath, lengthToSend);
                    await linkInterface.SendCanMessageAsync(srcAddress, new CanMessageFirmwareUpdateResponse
                    {
                        DataLength = 0,
                        Err = (byte)CanMessageFirmwareUpdateResponse.ErrOther,
                        FileLength = (uint)fs.Length,
                        FileOffset = 0,
                        Data = new ByteArray56()
                    },
                    isResponse: true,
                    cancellationToken: cancellationToken);
                    // TODO update OM that the firmware update failed
                    return;
                }

                // Send the requested block back to the firmware
                CanMessageFirmwareUpdateResponse response = new()
                {
                    DataLength = (byte)bytesRead,
                    Err = (byte)CanMessageFirmwareUpdateResponse.ErrNone,
                    FileLength = (uint)fs.Length,
                    FileOffset = fileOffset,
                    Data = new ByteArray56()
                };
                buffer.AsSpan(0, bytesRead).CopyTo(response.Data);

                await linkInterface.SendCanMessageAsync(srcAddress, response, CanMessageType.NoReply, isResponse: true, cancellationToken: cancellationToken);

                fileOffset += (uint)bytesRead;
                lengthRequested -= (uint)bytesRead;
                if (lengthRequested == 0)
                {
                    break;
                }
            }
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

    #region Firmware update
    /// <summary>
    /// Serve firmware update requests raised through <see cref="LinkInterface.UpdateFirmware"/>
    /// </summary>
    /// <remarks>
    /// The flash itself runs inside the native loop, which suspends the regular transfer protocol for
    /// its duration. This only stages the binaries and reports the outcome
    /// </remarks>
    /// <param name="stoppingToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private async Task WatchForFirmwareUpdatesAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await linkInterface.FirmwareUpdateRequested.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            TaskCompletionSource? request;
            Stream? iapStream, firmwareStream;
            using (await linkInterface.FirmwareUpdateLock.LockAsync(stoppingToken))
            {
                request = linkInterface.FirmwareUpdateRequest;
                iapStream = linkInterface.IapStream;
                firmwareStream = linkInterface.FirmwareStream;
            }

            if (request is null || iapStream is null || firmwareStream is null)
            {
                continue;
            }

            try
            {
                await PerformFirmwareUpdateAsync(iapStream, firmwareStream, stoppingToken);
                request.TrySetResult();
            }
            catch (Exception e)
            {
                logger.LogError(e, "Firmware update failed");
                request.TrySetException(e);
            }
            finally
            {
                using (await linkInterface.FirmwareUpdateLock.LockAsync(CancellationToken.None))
                {
                    linkInterface.IapStream = linkInterface.FirmwareStream = null;
                    linkInterface.FirmwareUpdateRequest = null;
                }
            }
        }
    }

    /// <summary>
    /// Perform the firmware update
    /// </summary>
    /// <param name="iapStream">IAP binary</param>
    /// <param name="firmwareStream">Firmware binary</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private async Task PerformFirmwareUpdateAsync(Stream iapStream, Stream firmwareStream, CancellationToken cancellationToken)
    {
        using (model.AccessReadWrite(cancellationToken))
        {
            model.IsUpdating = true;
        }

        // Everything in flight is about to become invalid: the controller is going to reboot into IAP
        Invalidate();

        // Get the CRC16 checksum of the firmware binary before handing it to the native side
        ushort crc16 = CRC16.Calculate(firmwareStream);

        firmwareStream.Seek(0, SeekOrigin.Begin);
        iapStream.Seek(0, SeekOrigin.Begin);

        byte[] iap = new byte[iapStream.Length];
        await iapStream.ReadExactlyAsync(iap, cancellationToken);
        byte[] firmware = new byte[firmwareStream.Length];
        await firmwareStream.ReadExactlyAsync(firmware, cancellationToken);

        logger.LogInformation("Starting firmware update ({IapLength} byte IAP, {FirmwareLength} byte firmware)", iap.Length, firmware.Length);

        try
        {
            await nativeLink.UpdateFirmwareAsync(iap, firmware, crc16, cancellationToken);
        }
        catch (Exception e)
        {
            eventLogger.LogOutput(MessageType.Error, "Failed to update firmware. Please install it manually.");
            logger.LogError(e, "Failed to update firmware");
            throw;
        }

        logger.LogInformation("Firmware update successful");
    }
    #endregion

    /// <summary>
    /// Invalidate pending codes and code-relevant requests due to an emergency stop
    /// </summary>
    private void InvalidateCodes()
    {
        // Invalidate pending codes and code-relevant requests
        linkInterface.InvalidateCodes();

        // Cancel the file being printed (if any)
        jobController.Abort();
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

        // Fail anything still waiting on the native loop
        nativeLink.CancelPendingRequests();

        // Forget what the motion engine reported. The moves it refers to are gone with the link, and
        // a stale endpoint reading applied to a move planned after the reconnect would be a jump
        motionTracker.Invalidate();

        // The same for what each of those moves was going to tell the job file: a move id from
        // before the link went down describes a queue the engine no longer has
        using (planner.Lock())
        {
            planner.JobMoves.Clear();
        }

        // Forget when each board was last heard from, so that the first sweep after the link returns
        // does not time out every board for a silence they had no way to break
        expansionBoardManager.Invalidate();

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
