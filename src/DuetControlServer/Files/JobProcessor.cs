using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Utility;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using DuetControlServer.Codes;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Codes.Meta;

namespace DuetControlServer.Files;

/// <summary>
/// Main class dealing with job files
/// </summary>
/// <remarks>
/// Lock this class whenever it is accessed (except for <see cref="Diagnostics(StringBuilder)"/>)
/// </remarks>
[DiagnosticsPriority(-9)]
public class JobProcessor : IAsyncDiagnostics
{
    // Private fields
    private readonly CodeProcessor _codeProcessor;
    private readonly CodeFactory _codeFactory;
    private readonly Logger _dsfLogger;
    private readonly Expressions _expressions;
    private readonly Parser.FileInfoParser _fileInfoParser;
    private readonly Link.Interface _linkInterface;
    private readonly Model.ObjectModel _model;
    private readonly IOptions<Settings> _settings;
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="codeFactory">Code factory</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="expressions">Expressions</param>
    /// <param name="fileInfoParser">File info parser</param>
    /// <param name="infoParser">Info parser</param>
    /// <param name="linkInterface">Link interface</param>
    /// <param name="model">Object Model</param>
    /// <param name="settings">Settings</param>
    /// <param name="lifetime">Host application lifetime</param>
    public JobProcessor(CodeFactory codeFactory,
        CodeProcessor codeProcessor,
        Logger dsfLogger,
        Expressions expressions,
        Parser.FileInfoParser fileInfoParser,
        Link.Interface linkInterface,
        Model.ObjectModel model,
        IOptions<Settings> settings,
        IHostApplicationLifetime lifetime)
    {
        _codeFactory = codeFactory;
        _codeProcessor = codeProcessor;
        _dsfLogger = dsfLogger;
        _expressions = expressions;
        _fileInfoParser = fileInfoParser;
        _linkInterface = linkInterface;
        _model = model;
        _settings = settings;
        _lifetime = lifetime;

        _resume = new(_lock);
        _finished = new(_lock);
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
    }

    /// <summary>
    /// Logger instance
    /// </summary>
    private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Lock around the print class
    /// </summary>
    private readonly AsyncLock _lock = new();

    /// <summary>
    /// Lock this class
    /// </summary>
    /// <returns>Disposable lock</returns>
    public IDisposable Lock() => _lock.Lock(_lifetime.ApplicationStopping);

    /// <summary>
    /// Lock this class
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Disposable lock</returns>
    public IDisposable Lock(CancellationToken cancellationToken) => _lock.Lock(cancellationToken);

    /// <summary>
    /// Lock this class asynchronously
    /// </summary>
    /// <returns>Disposable lock</returns>
    public AwaitableDisposable<IDisposable> LockAsync() => _lock.LockAsync(_lifetime.ApplicationStopping);

    /// <summary>
    /// Lock this class asynchronously
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Disposable lock</returns>
    public AwaitableDisposable<IDisposable> LockAsync(CancellationToken cancellationToken) => _lock.LockAsync(cancellationToken);

    /// <summary>
    /// Condition to trigger when the print is supposed to resume
    /// </summary>
    private readonly AsyncConditionVariable _resume;

    /// <summary>
    /// Condition to trigger when the print has finished
    /// </summary>
    private readonly AsyncConditionVariable _finished;

    /// <summary>
    /// First job file being read from
    /// </summary>
    private CodeFile? _file;

    /// <summary>
    /// Second job file being read from
    /// </summary>
    private CodeFile? _file2;

    /// <summary>
    /// Second job task (if any)
    /// </summary>
    private Task? _secondFileTask;

    /// <summary>
    /// Internal cancellation token source used to cancel pending codes when necessary
    /// </summary>
    private CancellationTokenSource _cancellationTokenSource;

    /// <summary>
    /// Indicates if a file has been selected for printing
    /// </summary>
    public bool IsFileSelected => _file is not null;

    /// <summary>
    /// Indicates if a print is live
    /// </summary>
    public bool IsProcessing { get; private set; }

    /// <summary>
    /// Indicates if a file is being simulated
    /// </summary>
    /// <remarks>
    /// This is volatile to allow fast access without locking the class first
    /// </remarks>
    public bool IsSimulating
    {
        get => _isSimulating;
        private set => _isSimulating = value;
    }
    private volatile bool _isSimulating;

    /// <summary>
    /// Indicates if the file print has been paused
    /// </summary>
    public bool IsPaused { get; private set; }

    /// <summary>
    /// Indicates if the file print has been cancelled
    /// </summary>
    public bool IsCancelled { get; private set; }

    /// <summary>
    /// Indicates if the file print has been aborted
    /// </summary>
    public bool IsAborted { get; private set; }

    /// <summary>
    /// Defines if the file position is supposed to be set by the Print task
    /// </summary>
    private long? _pausePosition;

    /// <summary>
    /// Reason why the print has been paused
    /// </summary>
    private PrintPausedReason _pauseReason;

    /// <summary>
    /// Get the current file position
    /// </summary>
    /// <param name="motionSystem">Motion system</param>
    /// <returns>File position</returns>
    public async Task<long> GetFilePositionAsync(int motionSystem)
    {
        if (_file is not null && motionSystem == 0)
        {
            using (await _file.LockAsync())
            {
                return _file.Position;
            }
        }

        if (_file2 is not null && motionSystem == 1)
        {
            using (await _file2.LockAsync())
            {
                return _file2.Position;
            }
        }

        return 0;
    }

    /// <summary>
    /// Set the current file position
    /// </summary>
    /// <param name="motionSystem">Motion system</param>
    /// <param name="filePosition">New file position</param>
    /// <returns>File position</returns>
    public async Task SetFilePositionAsync(int motionSystem, long filePosition)
    {
        if (_file is not null && motionSystem == 0)
        {
            using (await _file.LockAsync())
            {
                _file.Position = filePosition;
            }
        }

        if (_file2 is not null && motionSystem == 1)
        {
            using (await _file2.LockAsync())
            {
                _file2.Position = filePosition;
            }
        }
        _codeProcessor.ResolveSyncRequestsAfter(filePosition);
    }

    /// <summary>
    /// Returns the length of the file being printed in bytes
    /// </summary>
    public long FileLength => _file is not null ? _file.Length : 0;

    /// <summary>
    /// Start a new file print
    /// </summary>
    /// <param name="fileName">File to print</param>
    /// <param name="physicalFile">Physical file to print</param>
    /// <param name="simulating">Whether the file is being simulated</param>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// This class has to be locked when this method is called
    /// </remarks>
    public async Task SelectFile(string fileName, string physicalFile, bool simulating = false)
    {
        // Analyze and open the file
        GCodeFileInfo info = await _fileInfoParser.ParseAsync(physicalFile, true);
        CodeFile file = new(fileName, physicalFile, CodeChannel.File, _codeFactory, _codeProcessor, _expressions, _linkInterface, _model, _lifetime, _settings);

        // A file being printed may start another file print
        if (IsFileSelected)
        {
            Cancel();
            await _finished.WaitAsync(_lifetime.ApplicationStopping);
        }

        // Update the state
        IsCancelled = IsAborted = false;
        IsSimulating = simulating;
        _file = file;
        _pausePosition = null;

        // Update the object model
        using (await _model.AccessReadWriteAsync())
        {
            _model.Job.File.Assign(info);
        }

        // Notify RepRapFirmware and start processing the file in the background
        await _linkInterface.SetPrintFileInfo();
        _logger.Info("Selected file {0}", fileName);
    }

    /// <summary>
    /// Fork the file being processed to execute concurrently
    /// </summary>
    /// <returns>Message result</returns>
    public async Task<Message> ForkAsync()
    {
        if (_file is null)
        {
            return new Message(MessageType.Error, "No file is selected");
        }

        // Ignore the command if already forked
        if (_file2 is null)
        {
            // Copy the stack in case this is invoked from a macro file.
            // We need to pass the macro file position as well if applicable to resume the second macro file from the right position
            await _linkInterface.CopyStateAsync(CodeChannel.File, CodeChannel.File2);

            // Start printing using the second file channel if applicable.
            // Lock the file here because the copy constructor accesses file.NextFilePosition
            using (await _file.LockAsync())
            {
                _file2 = new(_file, CodeChannel.File2, _codeFactory, _codeProcessor, _expressions, _linkInterface, _model, _lifetime, _settings);
            }
        }
        return new Message();
    }

    /// <summary>
    /// Start the second file job if applicable
    /// </summary>
    public void StartSecondJob()
    {
        if (IsProcessing && _file2 is not null && _secondFileTask is null)
        {
            _secondFileTask = DoFilePrint(_file2);
        }
    }

    /// <summary>
    /// Print from the given file and send resulting codes to the specified channel
    /// </summary>
    /// <param name="file">File to read from</param>
    /// <returns>Asynchronous task</returns>
    private async Task DoFilePrint(CodeFile file)
    {
        // Get the cancellation token
        CancellationToken cancellationToken;
        using (await LockAsync())
        {
            cancellationToken = _cancellationTokenSource.Token;
        }

        // Use a code pool for print files
        Queue<Code> codePool = new();
        for (int i = 0; i < Math.Max(_settings.Value.BufferedPrintCodes, 1); i++)
        {
            codePool.Enqueue(_codeFactory.Create());
        }

        // Copy the full stack and assign the job file so flush requests are properly handled
        _codeProcessor.SetJobFile(file.Channel, file);

        // Process the file being printed
        Queue<Code> codes = new();
        long currentFilePosition = 0L;
        do
        {
            // Fill up the code buffer
            while (codePool.TryDequeue(out Code? sharedCode))
            {
                sharedCode.Reset();

                // Stop reading codes if the print has been paused or aborted
                using (await LockAsync())
                {
                    if (IsPaused || IsAborted)
                    {
                        cancellationToken = _cancellationTokenSource.Token;
                        codePool.Enqueue(sharedCode);
                        break;
                    }
                }

                // Try to read the next code
                Code? readCode = null;
                try
                {
                    try
                    {
                        readCode = await file.ReadCodeAsync(sharedCode);
                        if (readCode is null)
                        {
                            codePool.Enqueue(sharedCode);
                            break;
                        }
                        readCode.CancellationToken = cancellationToken;
                    }
                    catch
                    {
                        codePool.Enqueue(sharedCode);
                        throw;
                    }

                    readCode.Flags |= CodeFlags.Asynchronous;
                    codes.Enqueue(readCode);
                    await readCode.ExecuteAsync();
                }
                catch (Exception e)
                {
                    using (await LockAsync())
                    {
                        if (!IsAborted)
                        {
                            if (e is not OperationCanceledException)
                            {
                                if (e is AggregateException ae)
                                {
                                    e = ae.InnerException!;
                                }
                                await _dsfLogger.LogOutputAsync(MessageType.Error, $"in job file (channel {file.Channel}) line {readCode?.LineNumber ?? file.LineNumber}: {e.Message}");
                                _logger.Error(e);
                            }
                            Abort();
                        }
                    }
                }
            }

            // Is there anything more to do?
            if (codes.TryDequeue(out Code? code))
            {
                try
                {
                    try
                    {
                        // Logging of regular messages is done by the code itself, no need to take care of it here
                        await code.Task;

                        // Keep track of the file position
                        currentFilePosition = (code.FilePosition ?? 0L) + (code.Length ?? 0L);
                    }
                    catch (OperationCanceledException)
                    {
                        // Code has been cancelled, don't log this. This can happen when the file being printed is exchanged, when a
                        // pausable macro is interrupted, or when a code interceptor attempted to intercept a code on an inactive channel
                    }
                    catch (Exception e)
                    {
                        if (e is AggregateException ae)
                        {
                            e = ae.InnerException!;
                        }
                        await _dsfLogger.LogOutputAsync(MessageType.Error, $"in job file (channel {file.Channel}) line {code.LineNumber ?? 0}: {e.Message}");
                        _logger.Warn(e);
                    }
                }
                finally
                {
                    // Code has finished, add it back to the code pool
                    codePool.Enqueue(code);
                }
            }
            else
            {
                // Resolve pending sync requests waiting for this particular file channel
                _codeProcessor.PurgeSyncRequestsFor(file);

                // Flush one last time in case plugins inserted codes at the end of a print file.
                // Do this only if the job finished successfully, else we may get stuck in a deadlock
                try
                {
                    await _codeProcessor.FlushAsync(file);
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }

                using (await LockAsync())
                {
                    if (IsPaused)
                    {
                        // Adjust the file position
                        long newFilePosition = _pausePosition ?? currentFilePosition;
                        await SetFilePositionAsync(file.Channel == CodeChannel.File ? 0 : 1, newFilePosition);
                        _logger.Info("Job on {0} has been paused at byte {1}, reason {2}", file.Channel, (_pausePosition == null) ? $"{newFilePosition} (no fpos from firmware)" : newFilePosition.ToString(), _pauseReason);

                        // Wait for the print to be resumed
                        IsProcessing = false;
                        await _resume.WaitAsync(_lifetime.ApplicationStopping);
                        IsProcessing = !IsAborted && !IsCancelled;
                    }
                    else
                    {
                        // No more codes available - print must have finished
                        break;
                    }
                }
            }
        }
        while (!_lifetime.ApplicationStopping.IsCancellationRequested);

        // No longer printing
        _codeProcessor.SetJobFile(file.Channel, null);
    }

    /// <summary>
    /// Perform actual print jobs
    /// </summary>
    public async Task RunAsync()
    {
        do
        {
            // Wait for the next print to start
            bool startingNewPrint;
            using (await LockAsync())
            {
                await _resume.WaitAsync(_lifetime.ApplicationStopping);
                startingNewPrint = !_file!.IsClosed;
                IsProcessing = startingNewPrint;
            }

            // Deal with the file print
            if (startingNewPrint)
            {
                _logger.Info("Starting file print");

                // Start the main job
                Task fileTask = DoFilePrint(_file);

                // In case a forked print is supposed to start, start it here
                using (await LockAsync())
                {
                    if (_file2 is not null && _secondFileTask is null)
                    {
                        _secondFileTask = DoFilePrint(_file2);
                    }
                }

                // Run the main job
                await fileTask;

                // Wait for the forked job to complete (if any)
                Task? secondFileTask;
                using (await LockAsync())
                {
                    secondFileTask = _secondFileTask;
                    _secondFileTask = null;
                }

                if (secondFileTask is not null)
                {
                    await secondFileTask;
                }

                // Get the last print result
                bool isCancelled, isAborted, isSimulating;
                string physicalFileName;
                using (await LockAsync())
                {
                    isCancelled = IsCancelled;
                    isAborted = IsAborted;
                    isSimulating = IsSimulating;
                    physicalFileName = _file.PhysicalFileName;
                }

                // Notify RRF
                try
                {
                    if (isCancelled)
                    {
                        // Prints are cancelled by M0/M1/M2 which is processed by RRF
                        _logger.Info("Cancelled job file");
                    }
                    else if (isAborted)
                    {
                        await _linkInterface.StopPrint(PrintStoppedReason.Abort);
                        _logger.Info("Aborted job file");
                    }
                    else
                    {
                        await _linkInterface.StopPrint(PrintStoppedReason.NormalCompletion);
                        _logger.Info("Finished job file");
                    }
                }
                catch (OperationCanceledException)
                {
                    // SPI link lost while attempting to notify RRF, don't attempt anything else next
                    isAborted = true;
                }

                // Update special fields that are not available in RRF
                using (await _model.AccessReadWriteAsync())
                {
                    _model.Job.File.CustomInfo.Clear();
                    _model.Job.LastFileAborted = isAborted;
                    _model.Job.LastFileCancelled = isCancelled;
                    _model.Job.LastFileSimulated = isSimulating;
                }

                // Update the last simulated time
                if (isSimulating && !isAborted && !isCancelled)
                {
                    // Wait for the simulation time to be available
                    int? lastDuration = null;
                    int upTime = 0;
                    while (!_lifetime.ApplicationStopping.IsCancellationRequested)
                    {
                        await Model.Updater.WaitForFullUpdateAsync();
                        using (await _model.AccessReadOnlyAsync())
                        {
                            if (_model.State.UpTime < upTime || _model.Job.LastDuration is not null)
                            {
                                lastDuration = _model.Job.LastDuration;
                                break;
                            }
                            upTime = _model.State.UpTime;
                        }
                    }

                    // Try to update the last simulated time
                    if (lastDuration > 0)
                    {
                        await _fileInfoParser.UpdateSimulatedTime(physicalFileName, lastDuration.Value);
                    }
                    else
                    {
                        _logger.Warn("Failed to update simulation time because it was not set in the object model");
                    }
                }
            }

            using (await LockAsync())
            {
                // We are no longer printing a file...
                _finished.NotifyAll();

                // Dispose of the files
                _file!.Dispose();
                _file2?.Dispose();
                _file = _file2 = null;

                // End
                IsProcessing = IsSimulating = IsPaused = false;
            }
        }
        while (!_lifetime.ApplicationStopping.IsCancellationRequested);
    }

    /// <summary>
    /// Called when the print is being paused
    /// </summary>
    /// <param name="filePosition">File position where the print was paused</param>
    /// <param name="pauseReason">Reason why the print has been paused</param>
    public void Pause(long? filePosition, PrintPausedReason pauseReason)
    {
        if (IsFileSelected)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);

            IsPaused = true;
            _pausePosition = filePosition;
            _pauseReason = pauseReason;
        }
    }

    /// <summary>
    /// Resume a file print
    /// </summary>
    public void Resume()
    {
        if (IsFileSelected && !IsProcessing)
        {
            IsPaused = false;
            _resume.NotifyAll();
        }
    }

    /// <summary>
    /// Cancel the current print (e.g. when M0/M1 is called)
    /// </summary>
    public void Cancel()
    {
        if (IsFileSelected)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);

            _file!.Close();
            _file2?.Close();

            IsCancelled = IsPaused;
            Resume();
        }
    }

    /// <summary>
    /// Abort the current print asynchronously. This is called when the print could not complete as expected
    /// </summary>
    /// <returns>Asynchronous task</returns>
    public void Abort()
    {
        if (IsFileSelected)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);

            _file!.Close();
            _file2?.Close();

            IsAborted = true;
            Resume();
        }
    }

    /// <summary>
    /// Print diagnostics of this class
    /// </summary>
    /// <param name="builder">String builder</param>
    /// <returns>Asynchronous task</returns>
    public async ValueTask PrintDiagnosticsAsync(StringBuilder builder, CancellationToken cancellationToken)
    {
        using (await _lock.LockAsync(cancellationToken))
        {
            if (IsFileSelected)
            {
                builder.Append($"File {_file!.FileName} is selected");
                if (IsProcessing)
                {
                    builder.Append(", processing");
                }
                if (IsSimulating)
                {
                    builder.Append(", simulating");
                }
                if (IsPaused)
                {
                    builder.Append(", paused");
                }
                if (IsCancelled)
                {
                    builder.Append(", cancelled");
                }
                if (IsAborted)
                {
                    builder.Append(", aborted");
                }
                builder.AppendLine();
            }
        }
    }
}
