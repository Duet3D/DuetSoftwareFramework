using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DuetControlServer.Link;

/// <summary>
/// This class accesses RepRapFirmware via SPI and deals with general communication
/// </summary>
/// <param name="channels">Channel manager</param>
/// <param name="dsfLogger">Internal logger</param>
/// <param name="filePathResolver">File path resolver</param>
/// <param name="jobProcessor">Job processor</param>
/// <param name="linkAdapter">Firmware link adapter</param>
/// <param name="linkInterface">Link interface</param>
/// <param name="model">Object model</param>
/// <param name="settings">Settings</param>
[DiagnosticsPriority(-6)]
public sealed partial class LinkService(
    Channel.Manager channels,
    Logger dsfLogger,
    FilePathResolver filePathResolver,
    JobProcessor jobProcessor,
    ILinkAdapter linkAdapter,
    LinkInterface linkInterface,
    Model.ObjectModel model,
    IOptions<Settings> settings) : BackgroundService
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Last time the object model was queried
    /// </summary>
    private DateTime _lastQueryTime = DateTime.Now;

    /// <summary>
    /// Open files requested by the firmware
    /// </summary>
    private readonly Dictionary<uint, FileStream> _openFiles = [];

    /// <summary>
    /// Handle counter for open files
    /// </summary>
    public uint _openFileHandleCounter = Consts.NoFileHandle;

    /// <summary>
    /// Print diagnostics of this class asynchronously
    /// </summary>
    /// <param name="builder">String builder</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async ValueTask PrintDiagnosticsAsync(StringBuilder builder, CancellationToken cancellationToken)
    {
#warning fixme
        await channels.PrintDiagnosticsAsync(builder, cancellationToken);
        if (linkAdapter is IDiagnostics diagnostics)
        {
            diagnostics.PrintDiagnostics(builder);
        }
        if (linkAdapter is IAsyncDiagnostics asyncDiagnostics)
        {
            await asyncDiagnostics.PrintDiagnosticsAsync(builder, cancellationToken);
        }
    }

    /// <summary>
    /// Perform the firmware update internally
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void PerformFirmwareUpdate()
    {
        using (model.AccessReadWrite())
        {
            model.State.Status = MachineStatus.Updating;
        }

        // Get the CRC16 checksum of the firmware binary
        ushort crc16 = CRC16.Calculate(linkInterface.FirmwareStream!);

        // Send the IAP binary to the firmware
        _logger.Info("Flashing IAP binary");
        bool dataSent;
        do
        {
            dataSent = linkAdapter.WriteIapSegment(linkInterface.IapStream!);
            if (_logger.IsDebugEnabled)
            {
                Console.Write('.');
            }
        }
        while (dataSent);
        if (_logger.IsDebugEnabled)
        {
            Console.WriteLine();
        }

        // Start the IAP binary
        linkAdapter.StartIap();

        // Send the firmware binary to the IAP program
        int numRetries = 0;
        do
        {
            if (numRetries != 0)
            {
                _logger.Error("Firmware checksum verification failed");
            }

            _logger.Info("Flashing RepRapFirmware");
            linkInterface.FirmwareStream!.Seek(0, SeekOrigin.Begin);

            try
            {
                while (linkAdapter.FlashFirmwareSegment(linkInterface.FirmwareStream))
                {
                    if (_logger.IsDebugEnabled)
                    {
                        Console.Write('.');
                    }
                }
                if (_logger.IsDebugEnabled)
                {
                    Console.WriteLine();
                }
            }
            catch (Exception e)
            {
                _logger.Error(e);
                dsfLogger.LogOutput(MessageType.Error, "Failed to flash flash firmware. Please install it manually.");
                throw;
            }

            _logger.Info("Verifying checksum");
        }
        while (!linkAdapter.VerifyFirmwareChecksum(linkInterface.FirmwareStream.Length, crc16) && ++numRetries < 3);

        if (numRetries == 3)
        {
            // Failed to flash the firmware
            dsfLogger.LogOutput(MessageType.Error, "Could not flash firmware after 3 attempts. Please install it manually.");
            throw new OperationCanceledException("Failed to flash firmware after 3 attempts");
        }

        // Wait for the IAP binary to restart the controller
        linkAdapter.WaitForIapReset();
        _logger.Info("Firmware update successful");
    }

    /// <summary>
    /// Start this service asynchronously
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Initialize the link interface
        linkAdapter.Connect();

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
                Execute(stoppingToken);
                tcs.SetResult();
            }
            catch (Exception e)
            {
                if (e is AggregateException ae)
                {
                    if (ae.InnerException is OperationCanceledException)
                    {
                        tcs.SetCanceled();
                    }
                    else
                    {
                        tcs.SetException(ae.InnerException!);
                    }
                }
                else if (e is OperationCanceledException)
                {
                    tcs.SetCanceled();
                }
                else
                {
                    tcs.SetException(e);
                }
            }
        })
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true
        };
        wrapper.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Shut down this service
    /// </summary>
    /// <param name="stoppingToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        // Cancel the file being printed
        using (await jobProcessor.LockAsync(stoppingToken))
        {
            jobProcessor.Abort();
        }

        // Close all the files
        foreach (var kv in _openFiles)
        {
            await kv.Value.DisposeAsync();
        }
        _openFiles.Clear();

        // Shut down the link subsystem
        await linkInterface.InvalidateAsync(stoppingToken);

        // Shut down this service
        await base.StopAsync(stoppingToken);
    }

    /// <summary>
    /// Perform communication with the RepRapFirmware controller over SPI
    /// </summary>
    /// <param name="stoppingToken">Cancellation token</param>
    private void Execute(CancellationToken stoppingToken)
    {
        do
        {
            bool blockTask = false, skipChannels = false;
            using (linkInterface.FirmwareActionLock.Lock(stoppingToken))
            {
                // Check if an emergency stop has been requested
                if (linkInterface.FirmwareHaltRequest is not null)
                {
                    Invalidate();
                    if (linkAdapter.WriteEmergencyStop())
                    {
                        _logger.Warn("Emergency stop");
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
                        _logger.Warn("Resetting controller");
                        linkAdapter.PerformFullTransfer(cancellationToken: stoppingToken);
                        linkInterface.FirmwareResetRequest.SetResult();
                        linkInterface.FirmwareResetRequest = null;

                        blockTask = true;
                    }
                    skipChannels = true;
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
                        PerformFirmwareUpdate();
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
                    blockTask = true;
                }
            }
            if (blockTask)
            {
                // Wait for the requesting task to complete, it will terminate DCS next
                Task.Delay(-1, stoppingToken).Wait(stoppingToken);
            }

            // Invalidate data if a controller reset has been performed
            if (linkAdapter.HadReset())
            {
                Invalidate();
                dsfLogger.LogOutput(MessageType.Warning, "SPI connection has been reset");
            }

            // Check for changes of the print status
            using (linkInterface.PrintStateLock.Lock(stoppingToken))
            {
                if (linkInterface.SetPrintInfoRequest is not null && linkAdapter.WritePrintFileInfo(model.Job.File))
                {
                    // The packet providing file info has be sent first because it includes a time_t value that must reside on a 64-bit boundary!
                    linkInterface.SetPrintInfoRequest.SetResult();
                    linkInterface.SetPrintInfoRequest = null;
                }
                else
                {
                    if (linkInterface.StopPrintRequest is not null && linkAdapter.WritePrintStopped(linkInterface.StopPrintReason))
                    {
                        linkInterface.StopPrintRequest.SetResult();
                        linkInterface.StopPrintRequest = null;
                    }
                }
            }

            // Process incoming packets
            for (int i = 0; i < linkAdapter.PacketsToRead; i++)
            {
                try
                {
                    PacketHeader? packet = linkAdapter.ReadNextPacket();
                    if (packet is null)
                    {
                        _logger.Error("Read invalid packet");
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

            // Process pending codes, macro files and requests for resource locks/unlocks as well as flush requests
            if (!skipChannels)
            {
                channels.Spin();
            }

            // Request object model updates
            if (linkAdapter.ProtocolVersion == 1)
            {
                throw new Exception("Unsupported firmware version. Upgrade your firmware manually");
            }

            lock (linkInterface.ModelQueryRequests)
            {
                if (linkInterface.ModelQueryRequests.TryPeek(out ModelQueryRequest? request) &&
                    !request.QuerySent && linkAdapter.WriteGetObjectModel(request.Key, request.Flags))
                {
                    request.QuerySent = true;
                }
            }

            {
                int numEvaluationsSent = 0;

                // Ask for expressions to be evaluated
                lock (linkInterface.EvaluateExpressionRequests)
                {
                    foreach (EvaluateExpressionRequest request in linkInterface.EvaluateExpressionRequests)
                    {
                        if (!request.Written)
                        {
                            if (linkAdapter.WriteEvaluateExpression(request.Channel, request.Expression))
                            {
                                request.Written = true;

                                numEvaluationsSent++;
                                if (numEvaluationsSent >= Consts.MaxEvaluationRequestsPerTransfer)
                                {
                                    break;
                                }
                            }
                            else
                            {
                                // Don't attempt to write any more evaluation requests, else we risk getting out of order
                                break;
                            }
                        }
                    }
                }

                // Perform variable updates
                lock (linkInterface.VariableRequests)
                {
                    foreach (VariableRequest request in linkInterface.VariableRequests.ToList())
                    {
                        if (!request.Written)
                        {
                            if ((request.Expression is not null && linkAdapter.WriteSetVariable(request.Channel, request.CreateVariable, request.VariableName, request.Expression)) ||
                                (request.Expression is null && linkAdapter.WriteDeleteLocalVariable(request.Channel, request.VariableName)))
                            {
                                if (request.Expression is null)
                                {
                                    request.SetResult(null);
                                    linkInterface.VariableRequests.Remove(request);
                                }
                                else
                                {
                                    request.Written = true;
                                }

                                numEvaluationsSent++;
                                if (numEvaluationsSent >= Consts.MaxEvaluationRequestsPerTransfer)
                                {
                                    break;
                                }
                            }
                            else
                            {
                                // Don't attempt to write any more variable requests, else we risk getting out of order
                                break;
                            }
                        }
                    }
                }
            }

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

            // Do another full SPI transfer
            linkAdapter.PerformFullTransfer(cancellationToken: stoppingToken);
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
                if (sbcRequest != Protocol.SbcRequests.Request.LockAllMovementSystemsAndWaitForStandstill)
                {
                    // It's expected that RRF will need a moment to lock the movement but report other resend requests
                    _logger.Warn("Resending packet #{0} (request {1})", packet.Id, sbcRequest);
                }
                break;
            case Request.ObjectModel:
                HandleObjectModel();
                break;
            case Request.CodeBufferUpdate:
                HandleCodeBufferUpdate();
                break;
            case Request.Message:
                HandleMessage();
                break;
            case Request.ExecuteMacro:
                HandleMacroRequest();
                break;
            case Request.AbortFile:
                HandleAbortFileRequest();
                break;
            case Request.PrintPaused:
                HandlePrintPaused();
                break;
            case Request.Locked:
                HandleResourceLocked();
                break;
            case Request.FileChunk:
                HandleFileChunkRequest();
                break;
            case Request.EvaluationResult:
                HandleEvaluationResult();
                break;
            case Request.DoCode:
                HandleDoCode();
                break;
            case Request.WaitForAcknowledgement:
                HandleWaitForAcknowledgement();
                break;
            case Request.MacroFileClosed:
                HandleMacroFileClosed();
                break;
            case Request.MessageAcknowledged:
                HandleMessageAcknowledgement();
                break;
            case Request.VariableResult:
                HandleVariableResult();
                break;
            case Request.CheckFileExists:
                HandleCheckFileExists();
                break;
            case Request.DeleteFileOrDirectory:
            case Request.DeleteFileOrDirectoryRecursively:
                HandleDeleteFileOrDirectory((Request)packet.Request == Request.DeleteFileOrDirectoryRecursively);
                break;
            case Request.OpenFile:
                HandleOpenFile();
                break;
            case Request.ReadFile:
                HandleReadFile();
                break;
            case Request.WriteFile:
                HandleWriteFile();
                break;
            case Request.SeekFile:
                HandleSeekFile();
                break;
            case Request.TruncateFile:
                HandleTruncateFile();
                break;
            case Request.CloseFile:
                HandleCloseFile();
                break;
        }
    }

    /// <summary>
    /// Process an object model response
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void HandleObjectModel()
    {
        _logger.Trace("Received object model");
        linkAdapter.ReadObjectModel(out ReadOnlySpan<byte> json);
        lock (linkInterface.ModelQueryRequests)
        {
            if (linkInterface.ModelQueryRequests.TryDequeue(out ModelQueryRequest? query))
            {
                query.Tcs.SetResult(json.ToArray());
            }
            else
            {
                _logger.Warn("Failed to find query for object model response");
            }
        }
    }

    /// <summary>
    /// Update the amount of buffer space
    /// </summary>
    private void HandleCodeBufferUpdate()
    {
        linkAdapter.ReadCodeBufferUpdate(out ushort bufferSpace);
        linkInterface.BufferSpace = bufferSpace - linkInterface.BytesReserved;
        _logger.Trace("Buffer space available: {0}", linkInterface.BufferSpace);
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
        _logger.Trace("Received message [{0}] {1}", flags, reply);

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
                    LogLevel level = flags.HasFlag(MessageTypeFlags.LogOff) ? LogLevel.Off
                                        : flags.HasFlag(MessageTypeFlags.LogWarn) ? LogLevel.Warn
                                            : flags.HasFlag(MessageTypeFlags.LogInfo) ? LogLevel.Info
                                                : LogLevel.Debug;
                    dsfLogger.Log(level, type, _partialLogMessage.TrimEnd());
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
    /// Handle a macro request
    /// </summary>
    private void HandleMacroRequest()
    {
        linkAdapter.ReadMacroRequest(out CodeChannel channel, out bool fromCode, out string filename);
        _logger.Trace("Received macro request for file {0} on channel {1}", filename, channel);

        using (channels[channel].Lock())
        {
            channels[channel].DoMacroFile(filename, fromCode);
        }
    }

    /// <summary>
    /// Handle a file abort request
    /// </summary>
    private void HandleAbortFileRequest()
    {
        linkAdapter.ReadAbortFile(out CodeChannel channel, out bool abortAll);
        _logger.Info("Received file abort request on channel {0} for {1}", channel, abortAll ? "all files" : "the last file");

        using (channels[channel].Lock())
        {
            channels[channel].FilesAborted(abortAll);
        }
    }

    /// <summary>
    /// Deal with paused print events
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void HandlePrintPaused()
    {
        linkAdapter.ReadPrintPaused(out uint filePosition, out PrintPausedReason pauseReason);
        _logger.Debug("Received print pause notification for file position {0}, reason {1}", (filePosition == Consts.NoFilePosition) ? "(none)" : filePosition.ToString(), pauseReason);

        // Update the object model
        using (model.AccessReadWrite())
        {
            model.State.Status = MachineStatus.Paused;
        }

        // Pause the print
        using (jobProcessor.Lock())
        {
            // Do NOT supply a file position if this is a pause request initiated from G-code because that would lead to an endless loop
            bool filePositionValid = (filePosition != Consts.NoFilePosition) && (pauseReason != PrintPausedReason.GCode) && (pauseReason != PrintPausedReason.FilamentChange);
            jobProcessor.Pause(filePositionValid ? filePosition : null, pauseReason);
        }

        // Resolve pending and buffered codes on the file channels
        using (channels[CodeChannel.File].Lock())
        {
            channels[CodeChannel.File].PrintPaused();
        }

        using (channels[CodeChannel.File2].Lock())
        {
            channels[CodeChannel.File2].PrintPaused();
        }
    }

    /// <summary>
    /// Deal with the confirmation that a resource has been locked
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void HandleResourceLocked()
    {
        linkAdapter.ReadCodeChannel(out CodeChannel channel);
        _logger.Trace("Received resource locked notification for channel {0}", channel);

        using (channels[channel].Lock())
        {
            channels[channel].ResourceLocked();
        }
    }

    /// <summary>
    /// Process a request for a chunk of a given file
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void HandleFileChunkRequest()
    {
        linkAdapter.ReadFileChunkRequest(out string filename, out uint offset, out int maxLength);
        _logger.Debug("Received file chunk request for {0}, offset {1}, maxLength {2}", filename, offset, maxLength);

        try
        {
            string filePath;
            if (filename.EndsWith(".bin") || filename.EndsWith(".uf2"))
            {
                filePath = filePathResolver.ToPhysical(filename, FileDirectory.Firmware);
                if (!File.Exists(filePath))
                {
                    filePath = filePathResolver.ToPhysical(filename, FileDirectory.System);
                }
            }
            else
            {
                filePath = filePathResolver.ToPhysical(filename, FileDirectory.System);
            }

            using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, settings.Value.FileBufferSize)
            {
                Position = offset
            };
            Span<byte> buffer = stackalloc byte[maxLength];
            int bytesRead = fs.Read(buffer);

            linkAdapter.WriteFileChunk((bytesRead > 0) ? buffer[..bytesRead] : [], fs.Length);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to send requested file chunk of {0}", filename);
            linkAdapter.WriteFileChunk(null, 0);
        }
    }

    /// <summary>
    /// Handle the result of an evaluated expression
    /// </summary>
    private void HandleEvaluationResult()
    {
        linkAdapter.ReadEvaluationResult(out string expression, out object? result);
        _logger.Debug("Received evaluation result for expression {0} = {1}", expression, result);

        lock (linkInterface.EvaluateExpressionRequests)
        {
            foreach (EvaluateExpressionRequest request in linkInterface.EvaluateExpressionRequests)
            {
                // FIXME This should continue to work, but the next time the protocol is
                // updated, the evaluation response should include the channel as well
                if (request.Written && /*request.Channel == channel &&*/ request.Expression == expression)
                {
                    if (result is Exception exception)
                    {
                        request.SetException(exception);
                    }
                    else
                    {
                        request.SetResult(result);
                    }
                    linkInterface.EvaluateExpressionRequests.Remove(request);
                    return;
                }
            }
        }

        _logger.Warn("Unresolved evaluation result for expression {0} = {1}", expression, result);
    }

    /// <summary>
    /// Handle a firmware request to perform a G/M/T-code in DSF
    /// </summary>
    private void HandleDoCode()
    {
        linkAdapter.ReadDoCode(out CodeChannel channel, out string code);
        _logger.Trace("Received firmware code request on channel {0} => {1}", channel, code);

        using (channels[channel].Lock())
        {
            channels[channel].DoFirmwareCode(code);
        }
    }

    /// <summary>
    /// Handle a firmware request to wait for a message to be acknowledged
    /// </summary>
    private void HandleWaitForAcknowledgement()
    {
        linkAdapter.ReadCodeChannel(out CodeChannel channel);
        _logger.Trace("Received wait for message acknowledgement on channel {0}", channel);

        using (channels[channel].Lock())
        {
            channels[channel].WaitForAcknowledgement();
        }
    }

    /// <summary>
    /// Handle a firmware request that is sent when RRF has internally closed a macro file
    /// </summary>
    private void HandleMacroFileClosed()
    {
        linkAdapter.ReadCodeChannel(out CodeChannel channel);
        _logger.Trace("Received file closal on channel {0}", channel);

        using (channels[channel].Lock())
        {
            channels[channel].MacroFileClosed();
        }
    }

    /// <summary>
    /// Handle a firmware request that is sent when RRF has successfully acknowledged a blocking message
    /// </summary>
    private void HandleMessageAcknowledgement()
    {
        linkAdapter.ReadCodeChannel(out CodeChannel channel);
        _logger.Trace("Received message acknowledgement on channel {0}", channel);

        using (channels[channel].Lock())
        {
            channels[channel].MessageAcknowledged();
        }
    }

    /// <summary>
    /// Handle the result of a variable assignment
    /// </summary>
    private void HandleVariableResult()
    {
        linkAdapter.ReadEvaluationResult(out string varName, out object? result);
        _logger.Trace("Received variable assignment result for {0} = {1}", varName, result);

        lock (linkInterface.VariableRequests)
        {
            foreach (VariableRequest request in linkInterface.VariableRequests)
            {
                if (request.VariableName == varName)
                {
                    if (result is Exception exception)
                    {
                        request.SetException(exception);
                    }
                    else
                    {
                        request.SetResult(result);
                    }
                    linkInterface.VariableRequests.Remove(request);
                    return;
                }
            }
        }

        _logger.Warn("Unresolved variable set result for variable {0} = {1}", varName, result);
    }

    /// <summary>
    /// Check if a file exists
    /// </summary>
    private void HandleCheckFileExists()
    {
        linkAdapter.ReadCheckFileExists(out string filename);
        _logger.Debug("Checking if file {0} exists", filename);

        try
        {
            string physicalFile = filePathResolver.ToPhysical(filename);
            bool exists = File.Exists(physicalFile);
            linkAdapter.WriteCheckFileExistsResult(exists);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to check if file {0} exists", filename);
            linkAdapter.WriteCheckFileExistsResult(false);
        }
    }

    /// <summary>
    /// Delete a file or directory
    /// </summary>
    /// <param name="recursive">Delete file or directory recursively</param>
    private void HandleDeleteFileOrDirectory(bool recursive)
    {
        linkAdapter.ReadDeleteFileOrDirectory(out string filename);
        _logger.Debug("Attempting to delete {0}", filename);

        try
        {
            string physicalFile = filePathResolver.ToPhysical(filename);
            if (Directory.Exists(physicalFile))
            {
                Directory.Delete(physicalFile, recursive);
            }
            else
            {
                File.Delete(physicalFile);
            }
            linkAdapter.WriteFileDeleteResult(true);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to delete file or directory {0}", filename);
            linkAdapter.WriteFileDeleteResult(false);
        }
    }

    /// <summary>
    /// Try to open a file
    /// </summary>
    private void HandleOpenFile()
    {
        linkAdapter.ReadOpenFile(out string filename, out bool forWriting, out bool append, out long preAllocSize);
        _logger.Debug("Opening {0} for {1} ({2}appending), prealloc {3}", filename, forWriting ? "writing" : "reading", append ? string.Empty : "not ", preAllocSize);

        try
        {
            // Resolve the path and create the parent directory if necessary
            string physicalFile = filePathResolver.ToPhysical(filename), parentDirectory = Path.GetDirectoryName(physicalFile)!;
            if (!Directory.Exists(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            // Try to open the file as requested
            FileMode fsMode = forWriting ? (append ? FileMode.Append : FileMode.Create) : FileMode.Open;
            FileAccess faMode = forWriting ? FileAccess.Write : FileAccess.Read;
            FileStream fs = new(physicalFile, fsMode, faMode, FileShare.Read, settings.Value.FileBufferSize);
            if (forWriting && !append && preAllocSize > 0)
            {
                fs.SetLength(preAllocSize);
            }

            // Register a handle and send it back
            _openFileHandleCounter++;
            if (_openFileHandleCounter == Consts.NoFileHandle)
            {
                _openFileHandleCounter++;
            }
            _openFiles.Add(_openFileHandleCounter, fs);

            _logger.Debug("File {0} opened with handle #{1}", filename, _openFileHandleCounter);
            linkAdapter.WriteOpenFileResult(_openFileHandleCounter, fs.Length);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to open {0} for {1}", filename, forWriting ? "writing" : "reading");
            linkAdapter.WriteOpenFileResult(Consts.NoFileHandle, 0);
        }
    }

    /// <summary>
    /// Read more from a given file
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void HandleReadFile()
    {
        linkAdapter.ReadFileRequest(out uint handle, out int maxLength);
        _logger.Trace("Reading up to {0} bytes from file #{1}", maxLength, handle);

        try
        {
            // Read file content as requested
            FileStream fs = _openFiles[handle];
            Span<byte> data = stackalloc byte[maxLength];
            int bytesRead = fs.Read(data);

            // Send it back
            linkAdapter.WriteFileReadResult((bytesRead > 0) ? data[..bytesRead] : [], bytesRead);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to read {0} bytes from file #{1}", maxLength, handle);
            linkAdapter.WriteFileReadResult([], -1);
        }
    }

    /// <summary>
    /// Write more to a given file
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private void HandleWriteFile()
    {
        linkAdapter.ReadWriteRequest(out uint handle, out ReadOnlySpan<byte> data);
        _logger.Trace("Writing {0} bytes to file #{1}", data.Length, handle);

        try
        {
            // Write file content as requested
            FileStream fs = _openFiles[handle];
            fs.Write(data);

            // Send it back
            linkAdapter.WriteFileWriteResult(true);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to write {0} bytes to file #{1}", data.Length, handle);
            linkAdapter.WriteFileWriteResult(false);
        }
    }

    /// <summary>
    /// Go to a specific position in a file
    /// </summary>
    private void HandleSeekFile()
    {
        linkAdapter.ReadSeekFile(out uint handle, out long offset);
        _logger.Trace("Seeking to position {0} in file #{1}", offset, handle);

        try
        {
            // Go to the file position as requested
            FileStream fs = _openFiles[handle];
            fs.Seek(offset, SeekOrigin.Begin);

            // Send it back
            linkAdapter.WriteFileSeekResult(true);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to go to position {0} in file #{1}", offset, handle);
            linkAdapter.WriteFileSeekResult(false);
        }
    }

    /// <summary>
    /// Go to a specific position in a file
    /// </summary>
    private void HandleTruncateFile()
    {
        linkAdapter.ReadTruncateFile(out uint handle);
        _logger.Debug("Truncating file #{0}", handle);

        try
        {
            // Go to the file position as requested
            FileStream fs = _openFiles[handle];
            fs.SetLength(fs.Position);
            _logger.Debug("Truncated file #{0} at byte {1}", handle, fs.Length);

            // Send it back
            linkAdapter.WriteFileTruncateResult(true);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to truncate file #{0}", handle);
            linkAdapter.WriteFileTruncateResult(false);
        }
    }

    /// <summary>
    /// Check if a file exists
    /// </summary>
    private void HandleCloseFile()
    {
        linkAdapter.ReadCloseFile(out uint handle);
        _logger.Debug("Closing file #{0}", handle);

        try
        {
            // Close the file stream
            FileStream fs = _openFiles[handle];
            fs.Close();

            // Remove it again from the list of open files
            _openFiles.Remove(handle);

            // RRF doesn't expect a response for this...
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to close file #{0}", handle);
        }
    }
    
    /// <summary>
    /// Invalidate every resource due to a critical event
    /// </summary>
    private void Invalidate()
    {
        // Invalidate pending link interface requests
        linkInterface.Invalidate();

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
