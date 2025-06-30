using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Files;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nito.AsyncEx;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.Link;

/// <summary>
/// This class accesses RepRapFirmware via SPI and deals with general communication
/// </summary>
/// <param name="channels">Channel manager</param>
/// <param name="expressions">Expressions parser</param>
/// <param name="dsfLogger">Internal logger</param>
/// <param name="filePathResolver">File path resolver</param>
/// <param name="jobProcessor">Job processor</param>
/// <param name="linkAdapter">Firmware link adapter</param>
/// <param name="model">Object model</param>
/// <param name="updater">Object model updater</param>
/// <param name="settings">Settings</param>
[DiagnosticsPriority(-6)]
public sealed partial class Interface(
    Channel.Manager channels,
    Expressions expressions,
    Logger dsfLogger,
    FilePathResolver filePathResolver,
    JobProcessor jobProcessor,
    ILinkAdapter linkAdapter,
    Model.ObjectModel model,
    Model.Updater updater,
    IOptions<Settings> settings) : BackgroundService, IAsyncDiagnostics
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    // Information about the code channels
    private int _bytesReserved, _bufferSpace;
    private readonly Queue<ModelQueryRequest> _modelQueryRequests = new();
    private DateTime _lastQueryTime = DateTime.Now;

    // Expression evaluation and variable requests
    private readonly List<EvaluateExpressionRequest> _evaluateExpressionRequests = [];
    private readonly List<VariableRequest> _variableRequests = [];

    // Firmware updates
    private readonly AsyncLock _firmwareUpdateLock = new();
    private Stream? _iapStream, _firmwareStream;
    private TaskCompletionSource? _firmwareUpdateRequest;

    // Firmware halt/restart requests
    private readonly AsyncLock _firmwareActionLock = new();
    private TaskCompletionSource? _firmwareHaltRequest;
    private TaskCompletionSource? _firmwareResetRequest;

    // Print handling
    private readonly AsyncLock _printStateLock = new();
    private TaskCompletionSource? _setPrintInfoRequest;
    private Protocol.Shared.PrintStoppedReason _stopPrintReason;
    private TaskCompletionSource? _stopPrintRequest;

    // Miscellaneous requests
    private readonly Queue<Tuple<MessageTypeFlags, string>> _messagesToSend = new();
    private readonly Dictionary<uint, FileStream> _openFiles = [];
    private uint _openFileHandle = Consts.NoFileHandle;

    /// <summary>
    /// Print diagnostics of this class
    /// </summary>
    /// <param name="builder">String builder</param>
    /// <returns>Asynchronous task</returns>
    public async ValueTask PrintDiagnosticsAsync(StringBuilder builder, CancellationToken cancellationToken)
    {
        #warning fixme
        await channels.PrintDiagnosticsAsync(builder, cancellationToken);
        builder.AppendLine($"Code buffer space: {_bufferSpace}");
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
    /// Request a specific update of the object model
    /// </summary>
    /// <param name="key">Key to request</param>
    /// <param name="flags">Object model flags</param>
    /// <returns>Deserialized JSON document</returns>
    public Task<byte[]> RequestObjectModel(string key, string flags, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<byte[]>(cancellationToken);
        }
        if (settings.Value.NoSpi)
        {
            throw new InvalidOperationException("Not connected over SPI");
        }

        ModelQueryRequest request = new(key, flags);
        lock (_modelQueryRequests)
        {
            _modelQueryRequests.Enqueue(request);
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
        if (settings.Value.NoSpi)
        {
            throw new InvalidOperationException("Not connected over SPI");
        }
        if (linkAdapter.ProtocolVersion == 1)
        {
            throw new NotSupportedException("Incompatible firmware version");
        }
        if (Encoding.UTF8.GetByteCount(expression) >= Consts.MaxExpressionLength)
        {
            throw new ArgumentException($"Expression too long (max {Consts.MaxExpressionLength} chars)", nameof(expression));
        }

        lock (_evaluateExpressionRequests)
        {
            foreach (EvaluateExpressionRequest item in _evaluateExpressionRequests)
            {
                if (item.Channel == channel && item.Expression == expression)
                {
                    // There is no reason to evaluate the same expression twice...
                    return item.Task;
                }
            }

            EvaluateExpressionRequest request = new(channel, expression);
            _evaluateExpressionRequests.Add(request);
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
        lock (_variableRequests)
        {
            request = new(channel, createVariable, varName, expression);
            _variableRequests.Add(request);
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
    /// Check if a code channel is waiting for acknowledgement
    /// </summary>
    /// <param name="channel">Channel to query</param>
    /// <returns>Whether the channel is awaiting acknowledgement</returns>
    public bool IsWaitingForAcknowledgment(CodeChannel channel) => channels[channel].IsWaitingForAcknowledgment;

    /// <summary>
    /// Wait for all pending codes of the first or last stack item to finish
    /// </summary>
    /// <param name="channel">Code channel to wait for</param>
    /// <param name="flushAll">Flush everything</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async Task<bool> FlushAsync(CodeChannel channel, bool flushAll, CancellationToken cancellationToken = default)
    {
        if (settings.Value.NoSpi)
        {
            return true;
        }

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
        if (settings.Value.NoSpi)
        {
            return true;
        }

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
    /// <param name="evaluateExpressions">Evaluate all expressions when pending codes have been flushed</param>
    /// <param name="evaluateAll">Evaluate the expressions or only SBC fields if evaluateExpressions is set to true</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the codes have been flushed successfully</returns>
    public async Task<bool> FlushAsync(Code code, bool evaluateExpressions = true, bool evaluateAll = true, CancellationToken cancellationToken = default)
    {
        if (settings.Value.NoSpi)
        {
            return true;
        }

        Task<bool> flushTask;
        using (await channels[code.Channel].LockAsync(cancellationToken))
        {
            flushTask = (code.File == null) ? channels[code.Channel].FlushAsync(cancellationToken) : channels[code.Channel].FlushAsync(code.File, cancellationToken);
        }

        if (await flushTask)
        {
            if (evaluateExpressions)
            {
                // Code is about to be processed internally, evaluate potential expressions
                await expressions.EvaluateAsync(code, evaluateAll, cancellationToken);
            }
            return true;
        }
        return false;
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
        if (settings.Value.NoSpi)
        {
            throw new InvalidOperationException("Not connected over SPI");
        }

        Task onFirmwareHalted;
        using (await _firmwareActionLock.LockAsync(cancellationToken))
        {
            _firmwareHaltRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onFirmwareHalted = _firmwareHaltRequest.Task;
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
        if (settings.Value.NoSpi)
        {
            throw new InvalidOperationException("Not connected over SPI");
        }

        Task onFirmwareReset;
        using (await _firmwareActionLock.LockAsync(cancellationToken))
        {
            _firmwareResetRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onFirmwareReset = _firmwareResetRequest.Task;
        }
        await onFirmwareReset;
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
        if (settings.Value.NoSpi)
        {
            throw new InvalidOperationException("Not connected over SPI");
        }

        Task task;
        using (await _printStateLock.LockAsync())
        {
            _setPrintInfoRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = _setPrintInfoRequest.Task;
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
    public async Task StopPrint(PrintStoppedReason reason)
    {
        if (settings.Value.NoSpi)
        {
            throw new InvalidOperationException("Not connected over SPI");
        }

        Task onPrintStopped;
        using (await _printStateLock.LockAsync())
        {
            _stopPrintReason = reason;
            _stopPrintRequest ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onPrintStopped = _stopPrintRequest.Task;
        }
        await onPrintStopped;
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
        if (settings.Value.NoSpi)
        {
            throw new InvalidOperationException("Not connected over SPI");
        }

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
        if (settings.Value.NoSpi)
        {
            throw new InvalidOperationException("Not connected over SPI");
        }

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
        using (_firmwareUpdateLock.Lock())
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
        using (await _firmwareUpdateLock.LockAsync())
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
        if (settings.Value.NoSpi)
        {
            throw new InvalidOperationException("Not connected over SPI");
        }

        TaskCompletionSource tcs;
        using (await _firmwareUpdateLock.LockAsync())
        {
            if (_firmwareUpdateRequest is not null)
            {
                throw new InvalidOperationException("Firmware is already being updated");
            }

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _iapStream = iapStream;
            _firmwareStream = firmwareStream;
            _firmwareUpdateRequest = tcs;
        }
        await tcs.Task;
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
        ushort crc16 = CRC16.Calculate(_firmwareStream!);

        // Send the IAP binary to the firmware
        _logger.Info("Flashing IAP binary");
        bool dataSent;
        do
        {
            dataSent = linkAdapter.WriteIapSegment(_iapStream!);
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
            _firmwareStream!.Seek(0, SeekOrigin.Begin);

            try
            {
                while (linkAdapter.FlashFirmwareSegment(_firmwareStream))
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
        while (!linkAdapter.VerifyFirmwareChecksum(_firmwareStream.Length, crc16) && ++numRetries < 3);

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
    /// Send a message to the firmware
    /// </summary>
    /// <param name="flags">Message flags</param>
    /// <param name="message">Message content</param>
    /// <exception cref="InvalidOperationException">Incompatible firmware or not connected over SPI</exception>
    /// <exception cref="NotSupportedException">Incompatible firmware version</exception>
    /// <exception cref="ArgumentException">Invalid parameter</exception>
    public void SendMessage(MessageTypeFlags flags, string message)
    {
        if (settings.Value.NoSpi)
        {
            throw new InvalidOperationException("Not connected over SPI");
        }
        if (linkAdapter.ProtocolVersion == 1)
        {
            throw new NotSupportedException("Incompatible firmware version");
        }
        if (message.Length > settings.Value.MaxMessageLength)
        {
            throw new ArgumentException($"{nameof(message)} too long");
        }

        lock (_messagesToSend)
        {
            _messagesToSend.Enqueue(new Tuple<MessageTypeFlags, string>(flags, message));
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
        if (settings.Value.NoSpi)
        {
            throw new InvalidOperationException("Not connected over SPI");
        }

        using (await channels[channel].LockAsync())
        {
            await channels[channel].AbortAllFilesAsync();
        }
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
    /// Perform communication with the RepRapFirmware controller over SPI
    /// </summary>
    /// <param name="stoppingToken">Cancellation token</param>
    private void Execute(CancellationToken stoppingToken)
    {
        do
        {
            bool blockTask = false, skipChannels = false;
            using (_firmwareActionLock.Lock(stoppingToken))
            {
                // Check if an emergency stop has been requested
                if (_firmwareHaltRequest is not null)
                {
                    Invalidate();
                    if (linkAdapter.WriteEmergencyStop())
                    {
                        _logger.Warn("Emergency stop");
                        _firmwareHaltRequest.SetResult();
                        _firmwareHaltRequest = null;
                    }
                    skipChannels = true;
                }

                // Check if a firmware reset has been requested
                if (_firmwareResetRequest is not null)
                {
                    Invalidate();
                    if (linkAdapter.WriteReset())
                    {
                        _logger.Warn("Resetting controller");
                        linkAdapter.PerformFullTransfer(cancellationToken: stoppingToken);
                        _firmwareResetRequest.SetResult();
                        _firmwareResetRequest = null;

                        blockTask = !settings.Value.NoTerminateOnReset;
                    }
                    skipChannels = true;
                }
            }

            // Check if a firmware update is supposed to be performed
            using (_firmwareUpdateLock.Lock(stoppingToken))
            {
                if (_iapStream is not null && _firmwareStream is not null)
                {
                    Invalidate();

                    try
                    {
                        PerformFirmwareUpdate();
                        _firmwareUpdateRequest?.SetResult();
                        _firmwareUpdateRequest = null;
                    }
                    catch (Exception e)
                    {
                        _firmwareUpdateRequest?.SetException(e);
                        _firmwareUpdateRequest = null;

                        if (!settings.Value.UpdateOnly && settings.Value.NoTerminateOnReset && e is OperationCanceledException)
                        {
                            _logger.Debug(e, "Firmware update cancelled");
                        }
                        throw;
                    }

                    _iapStream = _firmwareStream = null;
                    blockTask = settings.Value.UpdateOnly || !settings.Value.NoTerminateOnReset;
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
            using (_printStateLock.Lock(stoppingToken))
            {
                if (_setPrintInfoRequest is not null && linkAdapter.WritePrintFileInfo(model.Job.File))
                {
                    // The packet providing file info has be sent first because it includes a time_t value that must reside on a 64-bit boundary!
                    _setPrintInfoRequest.SetResult();
                    _setPrintInfoRequest = null;
                }
                else
                {
                    if (_stopPrintRequest is not null && linkAdapter.WritePrintStopped(_stopPrintReason))
                    {
                        _stopPrintRequest.SetResult();
                        _stopPrintRequest = null;
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
            _bytesReserved = 0;

            // Process pending codes, macro files and requests for resource locks/unlocks as well as flush requests
            if (!skipChannels)
            {
                channels.Spin();
            }

            // Request object model updates
            if (linkAdapter.ProtocolVersion == 1)
            {
                if (DateTime.Now - _lastQueryTime > TimeSpan.FromMilliseconds(settings.Value.ModelUpdateInterval))
                {
                    using (model.AccessReadOnly(stoppingToken))
                    {
                        if (model.Boards.Count == 0 && linkAdapter.WriteGetLegacyConfigResponse())
                        {
                            // We no longer support regular status responses except to obtain the board name for updating the firmware
                            _lastQueryTime = DateTime.Now;
                        }
                    }
                }
            }
            else
            {
                lock (_modelQueryRequests)
                {
                    if (_modelQueryRequests.TryPeek(out ModelQueryRequest? request) &&
                        !request.QuerySent && linkAdapter.WriteGetObjectModel(request.Key, request.Flags))
                    {
                        request.QuerySent = true;
                    }
                }
            }

            {
                int numEvaluationsSent = 0;

                // Ask for expressions to be evaluated
                lock (_evaluateExpressionRequests)
                {
                    foreach (EvaluateExpressionRequest request in _evaluateExpressionRequests)
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
                lock (_variableRequests)
                {
                    foreach (VariableRequest request in _variableRequests.ToList())
                    {
                        if (!request.Written)
                        {
                            if ((request.Expression is not null && linkAdapter.WriteSetVariable(request.Channel, request.CreateVariable, request.VariableName, request.Expression)) ||
                                (request.Expression is null && linkAdapter.WriteDeleteLocalVariable(request.Channel, request.VariableName)))
                            {
                                if (request.Expression is null)
                                {
                                    request.SetResult(null);
                                    _variableRequests.Remove(request);
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
            lock (_messagesToSend)
            {
                while (_messagesToSend.TryPeek(out Tuple<MessageTypeFlags, string>? message))
                {
                    if (linkAdapter.WriteMessage(message.Item1, message.Item2))
                    {
                        _messagesToSend.Dequeue();
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
    /// Send a pending code to the firmware
    /// </summary>
    /// <param name="code">Code to send</param>
    /// <param name="codeLength">Length of the binary code in bytes</param>
    /// <returns>Whether the code could be sent</returns>
    internal bool SendCode(Code code, int codeLength)
    {
        if (_bufferSpace > codeLength && linkAdapter.WriteCode(code))
        {
            _bytesReserved += codeLength;
            _bufferSpace -= codeLength;
            return true;
        }
        return false;
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
        if (linkAdapter.ProtocolVersion > 1)
        {
            linkAdapter.ReadObjectModel(out ReadOnlySpan<byte> json);
            lock (_modelQueryRequests)
            {
                if (_modelQueryRequests.TryDequeue(out ModelQueryRequest? query))
                {
                    query.Tcs.SetResult(json.ToArray());
                }
                else
                {
                    _logger.Warn("Failed to find query for object model response");
                }
            }
        }
        else
        {
            linkAdapter.ReadLegacyConfigResponse(out ReadOnlySpan<byte> json);
            updater.ProcessLegacyConfigResponse(json.ToArray());
        }
    }

    /// <summary>
    /// Update the amount of buffer space
    /// </summary>
    private void HandleCodeBufferUpdate()
    {
        linkAdapter.ReadCodeBufferUpdate(out ushort bufferSpace);
        _bufferSpace = bufferSpace - _bytesReserved;
        _logger.Trace("Buffer space available: {0}", _bufferSpace);
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

        lock (_evaluateExpressionRequests)
        {
            foreach (EvaluateExpressionRequest request in _evaluateExpressionRequests)
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
                    _evaluateExpressionRequests.Remove(request);
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

        lock (_variableRequests)
        {
            foreach (VariableRequest request in _variableRequests)
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
                    _variableRequests.Remove(request);
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
            _openFileHandle++;
            if (_openFileHandle == Consts.NoFileHandle)
            {
                _openFileHandle++;
            }
            _openFiles.Add(_openFileHandle, fs);

            _logger.Debug("File {0} opened with handle #{1}", filename, _openFileHandle);
            linkAdapter.WriteOpenFileResult(_openFileHandle, fs.Length);
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
        // No longer starting or stopping a print. Must do this before aborting the print
        using (_printStateLock.Lock())
        {
            if (_setPrintInfoRequest is not null)
            {
                _setPrintInfoRequest.SetCanceled();
                _setPrintInfoRequest = null;
            }
            if (_stopPrintRequest is not null)
            {
                _stopPrintRequest.SetCanceled();
                _stopPrintRequest = null;
            }
        }

        // Cancel the file being printed
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
        _bytesReserved = _bufferSpace = 0;

        // Resolve pending object model requests
        lock (_modelQueryRequests)
        {
            foreach (ModelQueryRequest request in _modelQueryRequests)
            {
                request.Tcs.SetCanceled();
            }
            _modelQueryRequests.Clear();
        }

        // Resolve pending expression evaluation and variable requests
        lock (_evaluateExpressionRequests)
        {
            foreach (EvaluateExpressionRequest request in _evaluateExpressionRequests)
            {
                request.SetCanceled();
            }
            _evaluateExpressionRequests.Clear();
        }

        lock (_variableRequests)
        {
            foreach (VariableRequest request in _variableRequests)
            {
                request.SetCanceled();
            }
            _variableRequests.Clear();
        }

        // Clear messages to send to the firmware
        lock (_messagesToSend)
        {
            _messagesToSend.Clear();
        }

        // Close all the files
        foreach (var kv in _openFiles)
        {
            kv.Value.Close();
        }
        _openFiles.Clear();

        // Notify the updater task
        updater.ConnectionLost();
    }

    /// <summary>
    /// Called to shut down the SPI subsystem asynchronously
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public async Task ShutdownAsync()
    {
        // No longer starting or stopping a print. Must do this before aborting the print
        using (await _printStateLock.LockAsync())
        {
            if (_setPrintInfoRequest is not null)
            {
                _setPrintInfoRequest.SetCanceled();
                _setPrintInfoRequest = null;
            }
            if (_stopPrintRequest is not null)
            {
                _stopPrintRequest.SetCanceled();
                _stopPrintRequest = null;
            }
        }

        // Cancel the file being printed
        using (await jobProcessor.LockAsync())
        {
            jobProcessor.Abort();
        }

        // Resolve pending macros, unbuffered (system) codes and flush requests
        foreach (Channel.Processor channel in channels)
        {
            using (await channel.LockAsync())
            {
                channel.Invalidate();
            }
        }
        _bytesReserved = _bufferSpace = 0;

        // Resolve pending object model requests
        lock (_modelQueryRequests)
        {
            foreach (ModelQueryRequest request in _modelQueryRequests)
            {
                request.Tcs.SetCanceled();
            }
            _modelQueryRequests.Clear();
        }

        // Resolve pending expression evaluation and variable requests
        lock (_evaluateExpressionRequests)
        {
            foreach (EvaluateExpressionRequest request in _evaluateExpressionRequests)
            {
                request.SetCanceled();
            }
            _evaluateExpressionRequests.Clear();
        }

        lock (_variableRequests)
        {
            foreach (VariableRequest request in _variableRequests)
            {
                request.SetCanceled();
            }
            _variableRequests.Clear();
        }

        // Clear messages to send to the firmware
        lock (_messagesToSend)
        {
            _messagesToSend.Clear();
        }

        // Close all the files
        foreach (var kv in _openFiles)
        {
            await kv.Value.DisposeAsync();
        }
        _openFiles.Clear();
    }
}
