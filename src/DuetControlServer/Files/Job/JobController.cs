using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;
using DuetControlServer.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuetControlServer.Files.Job;

/// <summary>
/// The one task that owns the state of the job
/// </summary>
/// <remarks>
/// <para>
/// Every change of the job is a command dequeued here and performed by this task alone. No other
/// code writes job state and no caller holds a job lock: what the rest of DuetControlServer reads is
/// <see cref="State"/>, an immutable snapshot published as the last act of each transition. What is
/// accepted, what is refused and what is held is decided inside this loop from the phase it holds,
/// never by a handler reading the phase and then choosing which command to post, so no state is
/// reachable only by an interleaving.
/// </para>
/// <para>
/// The loop never waits for a macro. Anything that takes time is a <em>sequence</em>: a child task,
/// owned by this class and cancelled by it, which runs the macros and the motion steps and reports
/// what it did through <see cref="JobCommand.SequenceCompleted"/> for the loop to settle. That is
/// what keeps single ownership while a pause takes seconds, and what lets an abort be ordered
/// against a pause instead of racing it.
/// </para>
/// <para>
/// <c>docs/devel/JOB_CONTROL_CONCURRENCY.md</c> §7 is the design and the reasoning
/// </para>
/// </remarks>
[DiagnosticsPriority(-1)]
internal sealed class JobController : BackgroundService, IAsyncDiagnostics
{
    private readonly CodeFactory _codeFactory;
    private readonly CodeProcessor _codeProcessor;
    private readonly EventLogger _eventLogger;
    private readonly FileFactory _fileFactory;
    private readonly Parser.FileInfoParser _fileInfoParser;
    private readonly JobSequences _sequences;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<JobController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Settings _settings;

    /// <summary>
    /// Constructor of the job controller
    /// </summary>
    /// <param name="codeFactory">Code factory, for the readers' code pools</param>
    /// <param name="codeProcessor">Code processor</param>
    /// <param name="eventLogger">Event logger</param>
    /// <param name="fileFactory">File factory</param>
    /// <param name="fileInfoParser">File info parser</param>
    /// <param name="sequences">The macro and motion steps of each transition</param>
    /// <param name="lifetime">Host application lifetime</param>
    /// <param name="logger">Logger</param>
    /// <param name="loggerFactory">Logger factory, for the readers</param>
    /// <param name="settings">Settings</param>
    public JobController(CodeFactory codeFactory,
        CodeProcessor codeProcessor,
        EventLogger eventLogger,
        FileFactory fileFactory,
        Parser.FileInfoParser fileInfoParser,
        JobSequences sequences,
        IHostApplicationLifetime lifetime,
        ILogger<JobController> logger,
        ILoggerFactory loggerFactory,
        IOptions<Settings> settings)
    {
        _codeFactory = codeFactory;
        _codeProcessor = codeProcessor;
        _eventLogger = eventLogger;
        _fileFactory = fileFactory;
        _fileInfoParser = fileInfoParser;
        _sequences = sequences;
        _lifetime = lifetime;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _settings = settings.Value;
    }

    /// <summary>
    /// Commands waiting to be performed
    /// </summary>
    private readonly Channel<JobCommand> _commands = Channel.CreateUnbounded<JobCommand>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    /// <summary>
    /// What the job is doing, as one value
    /// </summary>
    /// <remarks>
    /// One field read, no lock. No combination is ever published that a single transition did not
    /// write, so a caller cannot see a job that half exists
    /// </remarks>
    public JobState State => _state;
    private volatile JobState _state = new();

    /// <summary>
    /// Token cancelled when the run that is going ends, after every stream has closed
    /// </summary>
    /// <remarks>
    /// One per run, cancelled once. Nothing has to re-read it, because nothing replaces it while the
    /// run is going, and no sequence runs under it
    /// </remarks>
    private CancellationTokenSource? _runTokenSource;

    // The sequence in flight, its token and who asked for it. Private to the loop: nothing outside
    // uses them, and a disposed source has no business in a record every reader keeps
    private Task? _sequence;
    private CancellationTokenSource? _sequenceTokenSource;
    private JobCommand? _sequenceRequest;
    private int _sequenceId;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (JobCommand command in _commands.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await PerformAsync(command);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to perform job command {Command}", command.GetType().Name);
                    command.Completion.TrySetException(e);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        finally
        {
            _commands.Writer.TryComplete();
        }
    }

    #region The surface the rest of DuetControlServer sees

    /// <summary>
    /// Where a stream has got to in the job file
    /// </summary>
    /// <param name="stream">Motion system</param>
    /// <returns>Byte offset, or zero if there is no such stream</returns>
    /// <remarks>The reader publishes this itself, so asking costs no command and no lock</remarks>
    public long GetFilePosition(int stream) => _state.Stream(stream)?.Reader.Position ?? 0;

    /// <summary>
    /// Select a file to print or simulate
    /// </summary>
    /// <param name="virtualFile">Virtual path of the file</param>
    /// <param name="physicalFile">Physical path of the file</param>
    /// <param name="simulating">Whether it is to be simulated rather than printed</param>
    /// <param name="updateSimulatedTime">Whether a completed simulation writes its time back</param>
    /// <param name="channel">Channel the selection was commanded from</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The message to report</returns>
    /// <remarks>The file is parsed and opened here, before the command is posted</remarks>
    public async ValueTask<Message> SelectFileAsync(string virtualFile, string physicalFile, bool simulating,
                                                    bool updateSimulatedTime, CodeChannel channel,
                                                    CancellationToken cancellationToken)
    {
        GCodeFileInfo info = await _fileInfoParser.ParseAsync(physicalFile, true);
        CodeFile file = _fileFactory.Create(virtualFile, physicalFile, CodeChannel.File);
        return await PostAsync(new JobCommand.SelectFile(new JobFile(file, info, simulating, updateSimulatedTime), channel),
                               cancellationToken);
    }

    /// <summary>
    /// Start a selected job or resume a paused one
    /// </summary>
    /// <param name="channel">Channel the request came from, which <c>resume.g</c> runs on</param>
    /// <param name="runMacro">Whether to run <c>resume.g</c>, cleared by <c>M24 P0</c></param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The message to report</returns>
    public ValueTask<Message> StartOrResumeAsync(CodeChannel channel, bool runMacro, CancellationToken cancellationToken)
        => PostAsync(new JobCommand.StartOrResume(channel, runMacro), cancellationToken);

    /// <summary>
    /// Pause the job
    /// </summary>
    /// <param name="request">What kind of pause, and why</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The message to report, or an error if the job cannot be paused</returns>
    public ValueTask<Message> PauseAsync(PauseRequest request, CancellationToken cancellationToken)
        => PostAsync(new JobCommand.Pause(request), cancellationToken);

    /// <summary>
    /// Stop the job, or put the machine down when there is none
    /// </summary>
    /// <param name="channel">Channel the stop was commanded from, which the macro runs on</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The message to report, or an error if the job cannot be stopped</returns>
    public ValueTask<Message> StopAsync(CodeChannel channel, CancellationToken cancellationToken)
        => PostAsync(new JobCommand.Stop(channel), cancellationToken);

    /// <summary>
    /// Tear the job down because it cannot carry on
    /// </summary>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// A code error, an <c>abort</c> keyword, the link going down, a shutdown. Awaited only as far as
    /// the transition: what the machine does about it is a sequence like any other
    /// </remarks>
    public async ValueTask AbortAsync()
    {
        try
        {
            await PostAsync(new JobCommand.Abort(), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The controller is shutting down, which is the same outcome
        }
        catch (ChannelClosedException)
        {
            // The same
        }
    }

    /// <summary>
    /// Tear the job down without waiting to hear that it has been
    /// </summary>
    /// <remarks>
    /// For the link dispatcher, which runs on a thread of its own and must not be held by whatever
    /// the job has in flight. The command is queued like any other, so it is still ordered against
    /// everything else the job is asked to do
    /// </remarks>
    public void Abort() => _commands.Writer.TryWrite(new JobCommand.Abort());

    /// <summary>
    /// Fork the job onto the second file channel
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The message to report</returns>
    public ValueTask<Message> ForkAsync(CancellationToken cancellationToken)
        => PostAsync(new JobCommand.Fork(), cancellationToken);

    /// <summary>
    /// Set where in the file a stream starts or carries on from
    /// </summary>
    /// <param name="stream">Motion system</param>
    /// <param name="position">Byte offset</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The message to report</returns>
    public ValueTask<Message> SetFilePositionAsync(int stream, long position, CancellationToken cancellationToken)
        => PostAsync(new JobCommand.SetFilePosition(stream, position), cancellationToken);

    /// <summary>
    /// Post a command and wait for the loop to answer it
    /// </summary>
    /// <param name="command">The command</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The reply</returns>
    /// <remarks>
    /// A caller that gives up does not affect what it asked for: the sequence runs under its own
    /// token, so a pause asked for from a dropped connection still finishes
    /// </remarks>
    private async ValueTask<Message> PostAsync(JobCommand command, CancellationToken cancellationToken)
    {
        await _commands.Writer.WriteAsync(command, cancellationToken);
        return await command.Completion.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask PrintDiagnosticsAsync(StringBuilder builder, CancellationToken cancellationToken)
    {
        JobState state = _state;
        if (state.File is JobFile file)
        {
            builder.Append($"File {file.File.FilePath.Virtual} is selected");
            builder.Append($", {char.ToLowerInvariant(state.Phase.ToString()[0])}{state.Phase.ToString()[1..]}");
            if (file.IsSimulating)
            {
                builder.Append(", simulating");
            }
            if (state.PendingPause is not null)
            {
                builder.Append(", pause pending");
            }
            builder.AppendLine();
        }
        return ValueTask.CompletedTask;
    }

    #endregion

    #region Transitions

    /// <summary>
    /// Perform one command
    /// </summary>
    /// <param name="command">The command</param>
    /// <returns>Asynchronous task</returns>
    private ValueTask PerformAsync(JobCommand command) => command switch
    {
        JobCommand.SelectFile c => OnSelectFileAsync(c),
        JobCommand.StartOrResume c => OnStartOrResumeAsync(c),
        JobCommand.Pause c => OnPauseAsync(c),
        JobCommand.Stop c => OnStopAsync(c),
        JobCommand.Abort c => OnAbortAsync(c),
        JobCommand.SetFilePosition c => OnSetFilePositionAsync(c),
        JobCommand.Fork c => OnForkAsync(c),
        JobCommand.ReaderStopped c => OnReaderStoppedAsync(c),
        JobCommand.ReaderFinished c => OnReaderFinishedAsync(c),
        JobCommand.ReaderFailed c => OnReaderFailedAsync(c),
        JobCommand.SequenceCompleted c => OnSequenceCompletedAsync(c),
        _ => ValueTask.CompletedTask
    };

    /// <summary>
    /// M23, M32 and M37 P: choose the file the next run reads
    /// </summary>
    private async ValueTask OnSelectFileAsync(JobCommand.SelectFile command)
    {
        JobState state = _state;
        string refusal = command.File.IsSimulating
                         ? "Cannot set file to simulate, because a file is already being printed"
                         : "Cannot set file to print, because a file is already being printed";

        if (command.Channel is CodeChannel.File or CodeChannel.File2)
        {
            // M32 from inside the job file, or from stop.g. The run it is part of has to be torn
            // down first, so the file is stored and started from Idle by the same pair of commands
            // every other caller uses - and the handler is answered at once, so nothing waits for
            // the run it is itself a code of
            if (state.Phase is JobPhase.Running or JobPhase.Finishing)
            {
                Publish(state with { NextFile = command.File });
                command.Reply(new Message());
                if (state.Phase == JobPhase.Running)
                {
                    await EndRunAsync(PrintStoppedReason.NormalCompletion);
                }
                return;
            }

            command.File.File.Dispose();
            command.Refuse(refusal);
            return;
        }

        if (state.IsJobInProgress)
        {
            command.File.File.Dispose();
            command.Refuse(refusal);
            return;
        }

        await AdoptAsync(command.File);
        command.Reply(new Message());
    }

    /// <summary>
    /// Make a file the one the next run reads
    /// </summary>
    /// <param name="file">The file</param>
    /// <returns>Asynchronous task</returns>
    private async ValueTask AdoptAsync(JobFile file)
    {
        // A file that was selected and never started is replaced outright
        JobState state = _state;
        await CloseStreamsAsync(state);

        Publish(new JobState
        {
            Phase = JobPhase.Selected,
            File = file,
            Streams = [NewStream(0, file.File)]
        });

        // A file is selected before M26 says where in it to start, so anything an earlier job left
        // behind belongs to that job rather than to this one
        _sequences.ForgetPreviousRun();
        await _sequences.PublishFileInfoAsync(file.Info, _lifetime.ApplicationStopping);

        _logger.LogInformation("Selected file {File}", file.File.FilePath.Virtual);
    }

    /// <summary>
    /// M24, M32 and M37: start what was selected, or resume what was paused
    /// </summary>
    private ValueTask OnStartOrResumeAsync(JobCommand.StartOrResume command)
    {
        JobState state = _state;
        switch (state.Phase)
        {
            case JobPhase.Selected:
                Publish(state with { Phase = JobPhase.Starting, StopReason = null });
                _runTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
                StartSequence(command, token => _sequences.StartAsync(_state, token));
                return ValueTask.CompletedTask;

            case JobPhase.Paused:
                Publish(state with { Phase = JobPhase.Resuming });
                StartSequence(command, token => _sequences.ResumeAsync(_state, command.RunMacro, token));
                return ValueTask.CompletedTask;

            case JobPhase.Starting:
            case JobPhase.Running:
            case JobPhase.Pausing:
            case JobPhase.Resuming:
                // RepRapFirmware ignores a resume of a job that is already going where it was asked
                command.Reply(new Message());
                return ValueTask.CompletedTask;

            default:
                // Idle, Cancelling, Finishing and Aborting: the file is closed in every one of them
                command.Refuse("Cannot print, because no file is selected!");
                return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// M25, M226, M600, M601 and the events that pause a job
    /// </summary>
    private ValueTask OnPauseAsync(JobCommand.Pause command)
    {
        JobState state = _state;
        PauseRequest request = command.Request;

        if (state.Phase is JobPhase.Pausing or JobPhase.Paused or JobPhase.Resuming)
        {
            command.Refuse("Printing is already paused!");
            return ValueTask.CompletedTask;
        }
        if (state.Phase != JobPhase.Running)
        {
            command.Refuse("Cannot pause print, because no file is being printed!");
            return ValueTask.CompletedTask;
        }

        // A job inside a macro that has not said it can be restarted must not be interrupted
        // part-way: the macro would be abandoned with no way to put back what it had already done.
        // Read here, inside the loop, so there is one decision point - a macro that ends before the
        // barrier arms leaves a boundary pause at the code that follows it, which is a pause either
        // way. RepRapFirmware's deferredPauseCommandPending
        if (_codeProcessor.IsDoingMacro(CodeChannel.File) && !_codeProcessor.CanRestartMacros(CodeChannel.File))
        {
            if (state.PendingPause is PauseRequest held &&
                (held.Macro == PauseMacro.FilamentChange || request.Macro != PauseMacro.FilamentChange))
            {
                // A filament change takes priority over an ordinary pause and replaces it; anything
                // else is refused rather than stacked
                command.Reply(new Message(MessageType.Warning, "Pausing is already pending"));
                return ValueTask.CompletedTask;
            }

            Publish(state with { PendingPause = request });
            state.Stream(0)?.Reader.FreezeAtBoundary();
            command.Reply(new Message());
            return ValueTask.CompletedTask;
        }

        BeginPause(command, request);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Enter <see cref="JobPhase.Pausing"/> and run the pause sequence
    /// </summary>
    /// <param name="command">The request, or null for a pause the reader reported a boundary for</param>
    /// <param name="request">What kind of pause</param>
    /// <param name="boundaryPosition">
    /// Where the reader stopped, for a pause that asked the engine for nothing
    /// </param>
    private void BeginPause(JobCommand? command, PauseRequest request, long? boundaryPosition = null)
    {
        // The finish a pause landed on top of is dropped: the reader reports Finished again once the
        // job resumes and runs out of codes a second time
        CancelSequence();
        Publish(_state with { Phase = JobPhase.Pausing, PendingPause = null });

        // A pause commanded from the job file itself is answered now rather than when the sequence
        // settles: the sequence has to wait for the code that asked to complete, and a reply held
        // until then would be a reply the code is waiting for. Where it stopped is reported through
        // the log, as it is for a pause nobody asked for
        if (request.Synchronous)
        {
            command?.Reply(new Message());
            command = null;
        }
        StartSequence(command, token => _sequences.PauseAsync(_state, request, boundaryPosition, token));
    }

    /// <summary>
    /// M0, M1 and M2
    /// </summary>
    private async ValueTask OnStopAsync(JobCommand.Stop command)
    {
        JobState state = _state;

        if (command.Channel is CodeChannel.File or CodeChannel.File2)
        {
            if (state.Phase == JobPhase.Running)
            {
                // The job reaching its end. Answered at once, because the code that asked is one of
                // the job's own and the teardown has to wait for it to complete
                command.Reply(new Message());
                await EndRunAsync(PrintStoppedReason.NormalCompletion);
                return;
            }
            command.Reply(new Message());
            return;
        }

        switch (state.Phase)
        {
            case JobPhase.Paused:
                Publish(state with { Phase = JobPhase.Cancelling, StopReason = PrintStoppedReason.UserCancelled });
                StopReading(_state);
                StartSequence(command, async token =>
                {
                    await CloseStreamsAsync(_state);
                    return await _sequences.StopAsync(command.Channel, PrintStoppedReason.UserCancelled, token);
                });
                return;

            case JobPhase.Idle:
                // No job to stop, so this is the operator putting the machine down for the night.
                // RepRapFirmware runs stop.g here too, and it must work every time it is given
                StartSequence(command, token => _sequences.StopAsync(command.Channel, PrintStoppedReason.NormalCompletion, token));
                return;

            case JobPhase.Cancelling:
            case JobPhase.Finishing:
            case JobPhase.Aborting:
                command.Reply(new Message());
                return;

            default:
                command.Refuse("Pause the print before attempting to cancel it");
                return;
        }
    }

    /// <summary>
    /// A code error, an <c>abort</c>, the link going down or a shutdown
    /// </summary>
    private async ValueTask OnAbortAsync(JobCommand.Abort command)
    {
        JobState state = _state;
        switch (state.Phase)
        {
            case JobPhase.Idle:
                command.Reply(new Message());
                return;

            case JobPhase.Selected:
                // Nothing was printing, so nothing is switched off. RepRapFirmware guards StopPrint's
                // heater switch-off with IsPrinting for the same reason
                await CloseStreamsAsync(state);
                Publish(new JobState());
                command.Reply(new Message());
                return;

            case JobPhase.Finishing:
                // The stop macro is cut short and never runs again; what is left is the teardown,
                // which the run cannot end without
                CancelSequence();
                StopReading(state);
                StartSequence(null, token => _sequences.TeardownAsync(_state, token));
                command.Reply(new Message());
                return;

            case JobPhase.Aborting:
                command.Reply(new Message());
                return;

            default:
                Publish(state with { Phase = JobPhase.Aborting, PendingPause = null });
                await DropPendingPauseAsync(state);
                StopReading(_state);
                if (_sequence is null)
                {
                    await EndRunAsync(PrintStoppedReason.Abort);
                }
                else
                {
                    // The sequence unwinds first, so an abort during a pause is ordered against it
                    // rather than racing it
                    CancelSequence(keepTask: true);
                }
                command.Reply(new Message());
                return;
        }
    }

    /// <summary>
    /// M26
    /// </summary>
    private async ValueTask OnSetFilePositionAsync(JobCommand.SetFilePosition command)
    {
        JobState state = _state;
        if (state.Phase is not (JobPhase.Selected or JobPhase.Paused))
        {
            command.Refuse("Not printing a file");
            return;
        }
        if (command.Position < 0 || command.Position > state.FileLength)
        {
            command.Refuse("Position is out of range");
            return;
        }

        if (state.Stream(command.Stream) is JobStream stream)
        {
            await stream.Reader.SetPositionAsync(command.Position, _lifetime.ApplicationStopping);
        }
        command.Reply(new Message());
    }

    /// <summary>
    /// M606 S1
    /// </summary>
    private async ValueTask OnForkAsync(JobCommand.Fork command)
    {
        JobState state = _state;
        if (state.Phase != JobPhase.Running || state.File is null)
        {
            command.Refuse("No file is selected");
            return;
        }
        if (state.Stream(1) is not null)
        {
            command.Reply(new Message());
            return;
        }

        JobStream first = state.Streams[0];
        CodeFile forked;
        using (await first.Reader.File.LockAsync(_lifetime.ApplicationStopping))
        {
            // The copy constructor reads NextFilePosition
            forked = _fileFactory.Create(first.Reader.File, CodeChannel.File2);
        }

        JobStream stream = NewStream(1, forked);
        Publish(state with { Streams = state.Streams.Add(stream) });
        await stream.Reader.RunAsync(null, restartMacro: false, RunToken, _lifetime.ApplicationStopping);
        command.Reply(new Message());
    }

    /// <summary>
    /// A stream has come to rest at the boundary a held pause was waiting for
    /// </summary>
    private ValueTask OnReaderStoppedAsync(JobCommand.ReaderStopped command)
    {
        JobState state = _state;
        if (state.Phase == JobPhase.Running && state.PendingPause is PauseRequest request)
        {
            // The macro was let finish, so the File channel is inside none by construction and
            // nothing was submitted past the last code's moves. The ring drains to rest at the
            // boundary, so the pause makes no stop and rewinds to where the reader says it is
            BeginPause(null, request with { Synchronous = true }, command.Position);
        }
        command.Reply(new Message());
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A stream has run out of codes
    /// </summary>
    private ValueTask OnReaderFinishedAsync(JobCommand.ReaderFinished command)
    {
        command.Reply(new Message());

        JobState state = _state;
        if (state.Phase != JobPhase.Running)
        {
            return ValueTask.CompletedTask;
        }

        Publish(state with { Streams = [.. MarkFinished(state.Streams, command.Stream)] });
        foreach (JobStream stream in _state.Streams)
        {
            if (!stream.Finished)
            {
                return ValueTask.CompletedTask;
            }
        }

        // The moves the file queued last have still to be made, and the job is not over until they
        // have. The phase stays Running while they run, so a pause may still land: RepRapFirmware
        // waits for standstill at this same point, before it closes the file
        StartSequence(null, _sequences.WaitForLastMovesAsync);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A stream could not carry on
    /// </summary>
    private async ValueTask OnReaderFailedAsync(JobCommand.ReaderFailed command)
    {
        command.Reply(new Message());
        _logger.LogError(command.Error, "Job stream {Stream} failed", command.Stream);
        if (_state.IsProcessing)
        {
            await OnAbortAsync(new JobCommand.Abort());
        }
    }

    /// <summary>
    /// Settle the phase the sequence was working towards
    /// </summary>
    private async ValueTask OnSequenceCompletedAsync(JobCommand.SequenceCompleted command)
    {
        command.Reply(new Message());
        if (command.Id != _sequenceId)
        {
            // A sequence that was cancelled and replaced; its replacement settles the phase
            return;
        }

        JobCommand? request = _sequenceRequest;
        _sequence = null;
        _sequenceRequest = null;
        _sequenceTokenSource = null;

        JobState state = _state;
        switch (state.Phase)
        {
            case JobPhase.Starting:
                if (command.Outcome.Failed)
                {
                    request?.Reply(command.Outcome.Reply);
                    await EndRunAsync(PrintStoppedReason.Abort);
                    return;
                }
                await BeginRunningAsync(state);
                request?.Reply(command.Outcome.Reply);
                return;

            case JobPhase.Pausing:
                // Every outcome settles to Paused, as RepRapFirmware's PauseSequenceAborted does:
                // the steps that cannot be cancelled have already run, so the machine is stopped and
                // must say so however the rest of the sequence ended
                Publish(_state with
                {
                    Phase = JobPhase.Paused,
                    Streams = [.. WithRewinds(_state.Streams, command.Outcome.Rewinds)]
                });
                request?.Reply(command.Outcome.Reply);
                return;

            case JobPhase.Resuming:
                if (command.Outcome.Failed)
                {
                    Publish(_state with { Phase = JobPhase.Paused });
                    request?.Reply(command.Outcome.Reply);
                    return;
                }
                await BeginRunningAsync(_state);
                request?.Reply(command.Outcome.Reply);
                return;

            case JobPhase.Running:
                // The wait for the last moves. A pause that landed while they ran replaced this
                // sequence, so reaching here means the job really is over
                request?.Reply(command.Outcome.Reply);
                await EndRunAsync(PrintStoppedReason.NormalCompletion);
                return;

            case JobPhase.Cancelling:
                // cancel.g has replaced stop.g, so the finish has only the teardown left
                request?.Reply(command.Outcome.Reply);
                Publish(_state with { Phase = JobPhase.Finishing });
                StartSequence(null, token => _sequences.TeardownAsync(_state, token));
                return;

            case JobPhase.Aborting:
                request?.Reply(command.Outcome.Reply);
                await EndRunAsync(PrintStoppedReason.Abort);
                return;

            case JobPhase.Finishing:
                request?.Reply(command.Outcome.Reply);
                await FinishAsync();
                return;

            case JobPhase.Idle:
                // The stop sequence of an M0 with no job
                request?.Reply(command.Outcome.Reply);
                return;

            default:
                request?.Reply(command.Outcome.Reply);
                return;
        }
    }

    #endregion

    #region The steps a transition takes

    /// <summary>
    /// Tell every stream to read, and publish <see cref="JobPhase.Running"/>
    /// </summary>
    /// <param name="state">State to move on from</param>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// The readers are told last, so there is no window in which the job has been started or resumed
    /// and a reader has not been told, and none in which a reader reads before the head is back
    /// </remarks>
    private async ValueTask BeginRunningAsync(JobState state)
    {
        Publish(state with { Phase = JobPhase.Running });
        foreach (JobStream stream in state.Streams)
        {
            await stream.Reader.RunAsync(stream.RewindPoint, stream.AbandonedMacros, RunToken,
                                         _lifetime.ApplicationStopping);
        }
        Publish(_state with
        {
            Streams = [.. ClearRewindPoints(_state.Streams)]
        });
    }

    /// <summary>
    /// End the run: close the streams and run whatever macro the reason calls for
    /// </summary>
    /// <param name="reason">Why the run ended</param>
    /// <returns>Asynchronous task</returns>
    private async ValueTask EndRunAsync(PrintStoppedReason reason)
    {
        JobState state = _state;
        await DropPendingPauseAsync(state);
        Publish(state with { Phase = JobPhase.Finishing, StopReason = reason, PendingPause = null });
        StopReading(_state);

        _logger.LogInformation(reason switch
        {
            PrintStoppedReason.UserCancelled => "Cancelled job file",
            PrintStoppedReason.Abort => "Aborted job file",
            _ => "Finished job file"
        });

        StartSequence(null, async token =>
        {
            // The streams are closed on the sequence rather than in the loop: closing waits for the
            // codes in flight, and one of them may be the very code that ended the run
            await CloseStreamsAsync(_state);
            SequenceOutcome outcome = await _sequences.StopAsync(CodeChannel.File, reason, token);
            return outcome.Failed ? outcome : await _sequences.TeardownAsync(_state, token);
        });
    }

    /// <summary>
    /// Publish <see cref="JobPhase.Idle"/> and start whatever M32 selected while the run was going
    /// </summary>
    /// <returns>Asynchronous task</returns>
    private async ValueTask FinishAsync()
    {
        JobState state = _state;
        await CloseStreamsAsync(state);

        _runTokenSource?.Cancel();
        _runTokenSource?.Dispose();
        _runTokenSource = null;

        JobFile? next = state.NextFile;
        Publish(new JobState());

        if (next is not null)
        {
            // The chained print starts from Idle, by the same pair of transitions every other caller
            // goes through
            await AdoptAsync(next);
            await OnStartOrResumeAsync(new JobCommand.StartOrResume(CodeChannel.File, RunMacro: true));
        }
    }

    /// <summary>
    /// Start a sequence and remember who is waiting for it
    /// </summary>
    /// <param name="request">The command to answer when it settles, or null if nobody is waiting</param>
    /// <param name="body">What the sequence does</param>
    /// <remarks>
    /// Its token is its own, linked to <c>ApplicationStopping</c> and to nothing else, so a caller
    /// that dies while awaiting its reply does not affect what it asked for
    /// </remarks>
    private void StartSequence(JobCommand? request, Func<CancellationToken, Task<SequenceOutcome>> body)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
        _sequenceTokenSource = source;
        _sequenceRequest = request;
        _sequence = RunSequenceAsync(++_sequenceId, body, source);
    }

    /// <summary>
    /// Run a sequence and report what it did
    /// </summary>
    /// <remarks>
    /// The source is disposed here rather than by whoever cancels it: a cancelled sequence is still
    /// unwinding, and a token whose source has gone is one it cannot ask about
    /// </remarks>
    private async Task RunSequenceAsync(int id, Func<CancellationToken, Task<SequenceOutcome>> body,
                                        CancellationTokenSource source)
    {
        SequenceOutcome outcome;
        try
        {
            outcome = await body(source.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = new SequenceOutcome(new Message(), Failed: true);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Job sequence failed");
            outcome = new SequenceOutcome(new Message(MessageType.Error, e.Message), Failed: true);
        }
        finally
        {
            source.Dispose();
        }

        try
        {
            await _commands.Writer.WriteAsync(new JobCommand.SequenceCompleted(id, outcome));
        }
        catch (ChannelClosedException)
        {
            // The controller has shut down
        }
    }

    /// <summary>
    /// Cancel the sequence in flight
    /// </summary>
    /// <param name="keepTask">
    /// Whether to leave the task in place so that its completion still settles a phase, which is
    /// what makes an abort wait for a pause to unwind
    /// </param>
    private void CancelSequence(bool keepTask = false)
    {
        try
        {
            _sequenceTokenSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The sequence finished as this was deciding to cancel it, which is the outcome asked for
        }
        if (!keepTask)
        {
            _sequenceRequest?.Reply(new Message());
            _sequence = null;
            _sequenceRequest = null;
            _sequenceId++;
        }
    }

    /// <summary>
    /// Say that a pause the job never got far enough to make will not happen
    /// </summary>
    /// <param name="state">State the run is ending from</param>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// The request itself was answered when it was taken, because a held pause is a promise rather
    /// than an operation. The run ending breaks that promise, and the operator asked for a pause
    /// rather than for the job to finish, so it is said out loud
    /// </remarks>
    private async ValueTask DropPendingPauseAsync(JobState state)
    {
        if (state.PendingPause is not null)
        {
            await _eventLogger.LogOutputAsync(MessageType.Warning,
                                              "Pause request dropped because the job ended before it could be actioned");
        }
    }

    /// <summary>
    /// Stop every stream reading, without waiting for what is in flight
    /// </summary>
    private static void StopReading(JobState state)
    {
        foreach (JobStream stream in state.Streams)
        {
            stream.Reader.StopReading();
        }
    }

    /// <summary>
    /// Build a reader for a stream of the job
    /// </summary>
    private JobStream NewStream(int index, CodeFile file)
    {
        JobReader reader = new(index, file, _commands.Writer, _codeFactory, _codeProcessor, _eventLogger,
                               _loggerFactory.CreateLogger<JobReader>(), _settings.BufferedPrintCodes);
        return new JobStream(index, file.Channel, reader, null, false, false);
    }

    /// <summary>
    /// Close every stream of a state and forget them
    /// </summary>
    private static async ValueTask CloseStreamsAsync(JobState state)
    {
        foreach (JobStream stream in state.Streams)
        {
            await stream.Reader.CloseAsync();
        }
    }

    /// <summary>
    /// The streams with the rewind points a resume has just spent taken back off
    /// </summary>
    private static IEnumerable<JobStream> ClearRewindPoints(ImmutableArray<JobStream> streams)
    {
        foreach (JobStream stream in streams)
        {
            yield return stream with { RewindPoint = null, AbandonedMacros = false, Finished = false };
        }
    }

    /// <summary>
    /// The streams with what the pause worked out for each of them
    /// </summary>
    private static IEnumerable<JobStream> WithRewinds(ImmutableArray<JobStream> streams,
                                                      IReadOnlyList<StreamRewind>? rewinds)
    {
        foreach (JobStream stream in streams)
        {
            JobStream result = stream;
            if (rewinds is not null)
            {
                foreach (StreamRewind rewind in rewinds)
                {
                    if (rewind.Stream == stream.Index)
                    {
                        result = stream with { RewindPoint = rewind.Point, AbandonedMacros = rewind.AbandonedMacros };
                        break;
                    }
                }
            }
            yield return result;
        }
    }

    /// <summary>
    /// The streams with the one that ran out of codes marked
    /// </summary>
    private static IEnumerable<JobStream> MarkFinished(ImmutableArray<JobStream> streams, int index)
    {
        foreach (JobStream stream in streams)
        {
            yield return stream.Index == index ? stream with { Finished = true } : stream;
        }
    }

    /// <summary>
    /// Token of the run that is going, or one already cancelled if none is
    /// </summary>
    private CancellationToken RunToken => _runTokenSource?.Token ?? _lifetime.ApplicationStopping;

    /// <summary>
    /// Make a new state visible
    /// </summary>
    private void Publish(JobState state) => _state = state;

    #endregion
}
