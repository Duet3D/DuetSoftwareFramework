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
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Files;

/// <summary>
/// Main class dealing with job files
/// </summary>
[DiagnosticsPriority(-1)]
internal partial class JobProcessor : BackgroundService, IAsyncDiagnostics
{
    // Private fields
    private readonly CodeProcessor _codeProcessor;
    private readonly CodeFactory _codeFactory;
    private readonly EventLogger _eventLogger;
    private readonly Expressions _expressions;
    private readonly FileFactory _fileFactory;
    private readonly Parser.FileInfoParser _fileInfoParser;
    private readonly Heat.HeatManager _heatManager;
    private readonly MacroRunner _macroRunner;
    private readonly Spindles.SpindleManager _spindleManager;
    private readonly Motion.MovePlanner _planner;
    private readonly Motion.MoveInterpreter _moveInterpreter;
    private readonly Tools.ToolManager _toolManager;
    private readonly Model.ObjectModel _model;
    private readonly ILogger<JobProcessor> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IOptions<Settings> _settings;

    /// <summary>
    /// Constructor of this class
    /// </summary>
    /// <param name="codeFactory">Code factory</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="eventLogger">Event logger</param>
    /// <param name="expressions">Expressions</param>
    /// <param name="fileFactory">File factory</param>
    /// <param name="fileInfoParser">File info parser</param>
    /// <param name="heatManager">The heaters, which a stop switches off when no macro does</param>
    /// <param name="macroRunner">Runs the lifecycle macros</param>
    /// <param name="spindleManager">The spindles, which an aborted job stops</param>
    /// <param name="planner">Where the restore point is saved from and the resume move is queued</param>
    /// <param name="moveInterpreter">
    /// The interpreter position, which a stop that dropped queued moves has to bring back into step
    /// with the machine before the restore point is taken from it
    /// </param>
    /// <param name="toolManager">The selected tool, whose offsets the resume move goes through</param>
    /// <param name="model">Object Model</param>
    /// <param name="lifetime">Host application lifetime</param>
    /// <param name="logger">Logger</param>
    /// <param name="settings">Settings</param>
    public JobProcessor(CodeFactory codeFactory,
        CodeProcessor codeProcessor,
        EventLogger eventLogger,
        Expressions expressions,
        FileFactory fileFactory,
        Parser.FileInfoParser fileInfoParser,
        Heat.HeatManager heatManager,
        MacroRunner macroRunner,
        Spindles.SpindleManager spindleManager,
        Motion.MovePlanner planner,
        Motion.MoveInterpreter moveInterpreter,
        Tools.ToolManager toolManager,
        Model.ObjectModel model,
        IHostApplicationLifetime lifetime,
        ILogger<JobProcessor> logger,
        IOptions<Settings> settings)
    {
        _codeFactory = codeFactory;
        _codeProcessor = codeProcessor;
        _eventLogger = eventLogger;
        _expressions = expressions;
        _fileFactory = fileFactory;
        _fileInfoParser = fileInfoParser;
        _heatManager = heatManager;
        _macroRunner = macroRunner;
        _spindleManager = spindleManager;
        _planner = planner;
        _moveInterpreter = moveInterpreter;
        _toolManager = toolManager;
        _model = model;
        _lifetime = lifetime;
        _logger = logger;
        _settings = settings;

        _resume = new(_lock);
        _finished = new(_lock);
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
    }

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
    /// Number of active job file streams. Two while the job file is forked, else one
    /// </summary>
    public int NumJobStreams => (_file2 is not null) ? 2 : 1;

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
    /// Whether the simulated time is written back to the file when a simulation completes (cleared by M37 F0)
    /// </summary>
    public bool UpdateSimulatedTime { get; set; } = true;

    /// <summary>
    /// Where the job is between running and paused
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>pauseState</c>. Written only by the pause, resume and cancel paths, and by
    /// the teardown that ends a job
    /// </remarks>
    public PauseState PauseState { get; private set; }

    /// <summary>
    /// Indicates if the file print is paused and can be resumed or cancelled
    /// </summary>
    /// <remarks>
    /// Deliberately strict: a job that is still coming to a stop is not yet cancellable, which is
    /// what RepRapFirmware's <c>pauseState == PauseState::paused</c> tests mean
    /// </remarks>
    public bool IsPaused => PauseState == PauseState.Paused;

    /// <summary>
    /// Indicates if the job is anywhere other than running normally
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>pauseState != PauseState::notPaused</c>, which is what refuses a second
    /// pause and what tells a code that a job is still in the way
    /// </remarks>
    public bool IsPausedOrChanging => PauseState != PauseState.NotPaused;

    /// <summary>
    /// Indicates if a job is running and not pausing, paused or resuming
    /// </summary>
    /// <remarks>RepRapFirmware's <c>IsReallyPrinting()</c></remarks>
    public bool IsReallyPrinting => IsProcessing && PauseState == PauseState.NotPaused;

    /// <summary>
    /// Indicates if the file print has been cancelled
    /// </summary>
    public bool IsCancelled { get; private set; }

    /// <summary>
    /// Indicates if the file print has been aborted
    /// </summary>
    public bool IsAborted { get; private set; }

    /// <summary>
    /// Defines the file position to be set by the Print task on pause
    /// </summary>
    private long? _pausePosition;

    /// <summary>
    /// Defines the second file position to be set by the Print task on pause
    /// </summary>
    private long? _pausePosition2;

    /// <summary>
    /// Reason why the print has been paused
    /// </summary>
    private PrintPausedReason _pauseReason;

    /// <summary>
    /// Whether the stop sequence has already run for the job that is finishing
    /// </summary>
    /// <remarks>
    /// M0, M1 and M2 stop the job themselves, and the job file then runs out of codes, which is the
    /// same thing arriving from the other side. Without this the macro would run twice for a file
    /// that ends the way most files do
    /// </remarks>
    private bool _stopped;

    /// <summary>
    /// Get the current file position
    /// </summary>
    /// <param name="motionSystem">Motion system</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File position</returns>
    public async Task<long> GetFilePositionAsync(int motionSystem, CancellationToken cancellationToken)
    {
        if (_file is not null && motionSystem == 0)
        {
            using (await _file.LockAsync(cancellationToken))
            {
                return _file.Position;
            }
        }

        if (_file2 is not null && motionSystem == 1)
        {
            using (await _file2.LockAsync(cancellationToken))
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
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>File position</returns>
    public async Task SetFilePositionAsync(int motionSystem, long filePosition, CancellationToken cancellationToken = default)
    {
        if (_file is not null && motionSystem == 0)
        {
            using (await _file.LockAsync(cancellationToken))
            {
                _file.Position = filePosition;
            }
        }

        if (_file2 is not null && motionSystem == 1)
        {
            using (await _file2.LockAsync(cancellationToken))
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
    /// Select a new file to print asynchronously
    /// </summary>
    /// <param name="virtualFile">File to print</param>
    /// <param name="physicalFile">Physical file to print</param>
    /// <param name="simulating">Whether the file is being simulated</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// This class has to be locked when this method is called
    /// </remarks>
    public async Task SelectFileAsync(string virtualFile, string physicalFile, bool simulating = false, CancellationToken cancellationToken = default)
    {
        // Analyze and open the file
        GCodeFileInfo info = await _fileInfoParser.ParseAsync(physicalFile, true);
        CodeFile file = _fileFactory.Create(virtualFile, physicalFile, CodeChannel.File);

        // A file being printed may start another file print
        if (IsFileSelected)
        {
            Cancel();
            await _finished.WaitAsync(_lifetime.ApplicationStopping);
        }

        // Update the state
        IsCancelled = IsAborted = _stopped = false;
        PauseState = PauseState.NotPaused;
        _deferredPause = null;
        IsSimulating = simulating;
        _file = file;
        _pausePosition = _pausePosition2 = null;

        // A file is selected before M26 says where in it to start, so anything an earlier job left
        // behind belongs to that job rather than to this one
        using (_planner.Lock())
        {
            _planner.State.RestartMoveFractionDone = 0.0f;
            _planner.State.RestartGCommandNumber = -1;
            _planner.State.CurrentJobMove = null;
        }

        // Update the object model
        using (await _model.AccessReadWriteAsync())
        {
            _model.Job.File.Assign(info);
        }

        _logger.LogInformation("Selected file {File}", virtualFile);
    }

    /// <summary>
    /// Fork the file being processed to execute concurrently
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Message result</returns>
    public async Task<Message> ForkAsync(CancellationToken cancellationToken)
    {
        if (_file is null)
        {
            return new Message(MessageType.Error, "No file is selected");
        }

        // Ignore the command if already forked
        if (_file2 is null)
        {
            // The firmware used to keep its own copy of the channel's macro stack, which had to be
            // duplicated onto File2 before the fork could start. There is only one stack now and
            // File2 builds its own as it executes, so there is nothing left to copy across

            // Start printing using the second file channel if applicable.
            // Lock the file here because the copy constructor accesses file.NextFilePosition
            using (await _file.LockAsync(cancellationToken))
            {
                _file2 = _fileFactory.Create(_file, CodeChannel.File2);
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
                // Stop reading codes if the print has been paused or aborted. The comparison relies
                // on PauseState's order: anything past NotPaused means the job is not to run on
                using (await LockAsync())
                {
                    if (PauseState >= PauseState.Pausing || IsAborted)
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
                                await _eventLogger.LogOutputAsync(MessageType.Error, $"in job file (channel {file.Channel}) line {readCode?.LineNumber ?? file.LineNumber}: {e.Message}");
                                _logger.LogError(e, "Error in job file (channel {Channel}) line {LineNumber}: {Message}", file.Channel, readCode?.LineNumber ?? file.LineNumber, e.Message);
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

                        // Keep track of the file position. Comments are resolved internally and finish even when the
                        // print is paused, so they must not advance the position past the point RRF actually stopped at
                        if (!code.IsNonFirmwareComment)
                        {
                            currentFilePosition = (code.FilePosition ?? 0L) + (code.Length ?? 0L);
                        }
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
                        await _eventLogger.LogOutputAsync(MessageType.Error, $"in job file (channel {file.Channel}) line {code.LineNumber ?? 0}: {e.Message}");
                        _logger.LogError(e, "Error in job file (channel {Channel}) line {LineNumber}: {Message}", file.Channel, code.LineNumber ?? 0, e.Message);
                    }
                }
                finally
                {
                    // Code has finished, add it back to the code pool
                    codePool.Enqueue(code);
                }

                // A pause asked for while the job was inside a macro it could not be interrupted in
                // happens here, once it is back out. RepRapFirmware checks at the same point, after
                // each command on the job channel completes
                if (file.Channel == CodeChannel.File)
                {
                    await CheckForDeferredPauseAsync(cancellationToken);
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
                    if (PauseState >= PauseState.Pausing)
                    {
                        // Adjust the file position for this motion system. Each MS may have advanced its file
                        // independently between sync points, so rewind to the firmware-reported pause offset
                        // for this channel (falling back to the last code we executed if RRF didn't supply one)
                        long? msPausePosition = (file.Channel == CodeChannel.File) ? _pausePosition : _pausePosition2;
                        long newFilePosition = msPausePosition ?? currentFilePosition;
                        await SetFilePositionAsync(file.Channel == CodeChannel.File ? 0 : 1, newFilePosition);
                        _logger.LogInformation("Job on {Channel} has been paused at byte {Offset}, reason {PauseReason}", file.Channel, (msPausePosition == null) ? $"{newFilePosition} (no fpos from firmware)" : newFilePosition.ToString(), _pauseReason);

                        // Wait for the print to be resumed
                        IsProcessing = false;
                        await _resume.WaitAsync(_lifetime.ApplicationStopping);

                        // Reassign the file being printed unless the print is aborted
                        if (!IsAborted && !IsCancelled)
                        {
                            IsProcessing = true;
                            _codeProcessor.SetJobFile(file.Channel, file);
                            RestoreModalStateForResume(file);
                        }
                    }
                    else
                    {
                        // No more codes available - print must have finished
                        break;
                    }
                }
            }
        } while (!_lifetime.ApplicationStopping.IsCancellationRequested);

        // No longer printing
        _codeProcessor.SetJobFile(file.Channel, null);
    }

    /// <summary>
    /// Put back the state the job was reading with when it paused
    /// </summary>
    /// <param name="file">The job file, already rewound to where it is to carry on from</param>
    /// <remarks>
    /// <para>
    /// Done here rather than in <c>ResumeAsync</c> because this is the one point that is after both
    /// the rewind and the restore point being written, and before the next code is read. The two
    /// happen on different tasks, so anywhere earlier is a race with one of them.
    /// </para>
    /// <para>
    /// RepRapFirmware's resume does the same pair: <c>SetModalGCommand</c> so that a line naming no
    /// command letter still means what it did, and <c>ResumeAfterPause</c> so that a line the machine
    /// is already part-way through asks only for the rest of itself. The fraction goes to the shared
    /// interpreter state and is spent by the first move the job reads - see
    /// <see cref="Motion.MovementState.MoveFractionToSkip"/>
    /// </para>
    /// </remarks>
    private void RestoreModalStateForResume(CodeFile file)
    {
        // There is one interpreter state and one pause restore point, so both of these describe the
        // first file channel's job and neither means anything for a fork of it. TODO when M596 and
        // M598 give each motion system its own, this becomes per channel - the restore point that
        // recorded them as much as the file that reads them back
        if (file.Channel != CodeChannel.File)
        {
            return;
        }

        using (_planner.Lock())
        {
            Motion.RestorePoint rp = _planner.State.RestorePoints[Motion.RestorePoint.PauseNumber];
            if (rp.GCommandNumber >= 0)
            {
                file.ModalGCommand = rp.GCommandNumber;
            }
            _planner.State.MoveFractionToSkip = rp.ProportionDone;
        }
    }

    /// <summary>
    /// Perform actual print jobs
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            do
            {
                // Wait for the next print to start
                bool startingNewPrint;
                using (await LockAsync(stoppingToken))
                {
                    await _resume.WaitAsync(stoppingToken);
                    startingNewPrint = !_file!.IsClosed;
                    IsProcessing = startingNewPrint;
                }

                // Deal with the file print
                if (startingNewPrint)
                {
                    _logger.LogInformation("Starting file print");

                    // Start the main job
                    Task fileTask = DoFilePrint(_file);

                    // In case a forked print is supposed to start, start it here
                    using (await LockAsync(stoppingToken))
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
                    using (await LockAsync(stoppingToken))
                    {
                        secondFileTask = _secondFileTask;
                        _secondFileTask = null;
                    }

                    if (secondFileTask is not null)
                    {
                        await secondFileTask;
                    }

                    // Get the last print result
                    bool isCancelled, isAborted, isSimulating, updateSimulatedTime;
                    string physicalFileName;
                    using (await LockAsync(stoppingToken))
                    {
                        isCancelled = IsCancelled;
                        isAborted = IsAborted;
                        isSimulating = IsSimulating;
                        updateSimulatedTime = UpdateSimulatedTime;
                        physicalFileName = _file.FilePath.Physical;
                    }

                    // Say how the job ended. M0/M1/M2 is what cancels one, and it has already run
                    // by the time the file task returns
                    _logger.LogInformation(isCancelled ? "Cancelled job file"
                                            : isAborted ? "Aborted job file"
                                                : "Finished job file");

                    // Put the machine down. A job stopped by M0/M1/M2 has already been through this,
                    // and an abort has not - it is the path where nothing asked for the stop
                    bool alreadyStopped;
                    using (await LockAsync(stoppingToken))
                    {
                        alreadyStopped = _stopped;
                    }
                    if (!alreadyStopped)
                    {
                        await StopAsync(CodeChannel.File,
                                        isAborted ? PrintStoppedReason.Abort
                                        : isCancelled ? PrintStoppedReason.UserCancelled
                                        : PrintStoppedReason.NormalCompletion,
                                        stoppingToken);
                    }

                    // Update special fields that are not available in RRF
                    using (await _model.AccessReadWriteAsync(stoppingToken))
                    {
                        _model.Job.File.CustomInfo.Clear();
                        _model.Job.LastFileAborted = isAborted;
                        _model.Job.LastFileCancelled = isCancelled;
                        _model.Job.LastFileSimulated = isSimulating;
                    }

                    // Update the last simulated time
                    if (isSimulating && updateSimulatedTime && !isAborted && !isCancelled)
                    {
                        // Wait for the simulation time to be available
                        int? lastDuration = null;
                        int upTime = 0;
                        while (!_lifetime.ApplicationStopping.IsCancellationRequested)
                        {
                            await _model.WaitForFullUpdateAsync(stoppingToken);
                            using (await _model.AccessReadOnlyAsync(stoppingToken))
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
                            await _fileInfoParser.UpdateSimulatedTimeAsync(physicalFileName, lastDuration.Value, stoppingToken);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to update simulation time because it was not set in the object model");
                        }
                    }
                }

                using (await LockAsync(stoppingToken))
                {
                    // We are no longer printing a file...
                    _finished.NotifyAll();

                    // Dispose of the files
                    _file!.Dispose();
                    _file2?.Dispose();
                    _file = _file2 = null;

                    // End
                    IsProcessing = IsSimulating = false;
                    PauseState = PauseState.NotPaused;
                }
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    /// <summary>
    /// Stop the job reading and cancel what it has read ahead
    /// </summary>
    /// <param name="filePosition">File position to resume from, or null to use the last completed code</param>
    /// <param name="filePosition2">The same for the second motion system</param>
    /// <param name="pauseReason">Reason why the print is pausing</param>
    /// <remarks>
    /// One step of <see cref="PauseAsync"/> rather than the whole of a pause, and deliberately does
    /// not touch <see cref="PauseState"/>: the sequence owns that, because the machine is not paused
    /// until it has stopped moving and <c>pause.g</c> has run. This class has to be locked when this
    /// method is called
    /// </remarks>
    private void StopReadingForPause(long? filePosition, long? filePosition2, PrintPausedReason pauseReason)
    {
        if (IsFileSelected)
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);

            _pausePosition = filePosition;
            _pausePosition2 = filePosition2;
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
            PauseState = PauseState.NotPaused;
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
            PauseState = PauseState.NotPaused;
            _resume.NotifyAll();
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
            PauseState = PauseState.NotPaused;
            _resume.NotifyAll();
        }
    }

    /// <summary>
    /// Print diagnostics of this class
    /// </summary>
    /// <param name="builder">String builder</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async ValueTask PrintDiagnosticsAsync(StringBuilder builder, CancellationToken cancellationToken)
    {
        using (await _lock.LockAsync(cancellationToken))
        {
            if (IsFileSelected)
            {
                builder.Append($"File {_file!.FilePath.Virtual} is selected");
                if (IsProcessing)
                {
                    builder.Append(", processing");
                }
                if (IsSimulating)
                {
                    builder.Append(", simulating");
                }
                if (PauseState != PauseState.NotPaused)
                {
                    builder.Append($", {char.ToLowerInvariant(PauseState.ToString()[0])}{PauseState.ToString()[1..]}");
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
