using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Codes;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;
using DuetControlServer.Utility;
using Microsoft.Extensions.Logging;
using RestorePoint = DuetControlServer.Motion.RestorePoint;

namespace DuetControlServer.Files.Job;

/// <summary>
/// The macros and motion steps each transition of the job takes
/// </summary>
/// <remarks>
/// <para>
/// Each of these runs as a child task of <see cref="JobController"/>, under a token of its own, and
/// writes no job state: it writes the planner, the object model and the readers as its steps
/// require, and reports what it did for the controller to settle. That is what keeps one owner for
/// the job while a pause takes seconds.
/// </para>
/// <para>
/// The bodies are ported step for step from RepRapFirmware, which
/// <c>docs/devel/JOB_LIFECYCLE.md</c> records: which macros run and on which channel, the feedhold,
/// the two-move return to the restore point, the modal state a resumed line is read with, and every
/// refusal message
/// </para>
/// </remarks>
/// <param name="codeProcessor">Code processor</param>
/// <param name="eventLogger">Event logger</param>
/// <param name="fileInfoParser">File info parser, which writes a simulation's time back</param>
/// <param name="heatManager">The heaters, which a stop switches off when no macro does</param>
/// <param name="jobMonitor">Keeps the job timings, told when a job starts and ends</param>
/// <param name="macroRunner">Runs the lifecycle macros</param>
/// <param name="model">Object model</param>
/// <param name="moveInterpreter">
/// The interpreter position, which a stop that dropped queued moves has to bring back into step with
/// the machine before the restore point is taken from it
/// </param>
/// <param name="planner">Where the restore point is saved from and the resume move is queued</param>
/// <param name="spindleManager">The spindles, which an aborted job stops</param>
/// <param name="toolManager">The selected tool, whose offsets the resume move goes through</param>
/// <param name="logger">Logger</param>
internal sealed class JobSequences(
    CodeProcessor codeProcessor,
    EventLogger eventLogger,
    Parser.FileInfoParser fileInfoParser,
    Heat.HeatManager heatManager,
    JobMonitor jobMonitor,
    MacroRunner macroRunner,
    Model.ObjectModel model,
    MoveInterpreter moveInterpreter,
    MovePlanner planner,
    Spindles.SpindleManager spindleManager,
    Tools.ToolManager toolManager,
    ILogger<JobSequences> logger)
{
    /// <summary>
    /// Feed rate the head is moved back at when a job resumes, in mm/s
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>DefaultFeedRate</c> of 3000 mm/min. The job may have paused during a very
    /// slow move and the way back is not a printing move, so the paused feed rate is the wrong one to
    /// travel at - it is restored afterwards, once the head is back
    /// </remarks>
    private const float ResumeFeedRateMmPerSec = 3000.0f / 60.0f;

    /// <summary>
    /// Millimetres per inch, for the channels working in G20
    /// </summary>
    private const float MmPerInch = 25.4f;

    /// <summary>
    /// Feed rates are given per minute and used per second
    /// </summary>
    private const float SecondsPerMinute = 60.0f;

    /// <summary>
    /// How long to wait before retrying a move the engine had no room for
    /// </summary>
    private static readonly TimeSpan RingFullRetryDelay = TimeSpan.FromMilliseconds(5);

    #region Selecting a file

    /// <summary>
    /// Forget what the previous run left behind
    /// </summary>
    /// <remarks>
    /// A file is selected before M26 says where in it to start, so a restart fraction or a modal
    /// command left over belongs to the job that is gone. The move index goes with them: a move id
    /// from the previous run says nothing about this file
    /// </remarks>
    public void ForgetPreviousRun()
    {
        using (planner.Lock())
        {
            planner.State.RestartMoveFractionDone = 0.0f;
            planner.State.RestartGCommandNumber = -1;
            planner.JobMoves.Clear();
        }
    }

    /// <summary>
    /// Say in the object model what the file info parser made of the selected file
    /// </summary>
    /// <param name="info">The parsed file info</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async ValueTask PublishFileInfoAsync(GCodeFileInfo info, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            model.Job.File.Assign(info);
        }
    }

    #endregion

    #region Starting

    /// <summary>
    /// Start a job that was only selected
    /// </summary>
    /// <param name="state">What the controller holds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the sequence did</returns>
    /// <remarks>
    /// <c>start.g</c> runs on the file channel so that any M82 or M83 in it applies to the job that
    /// is about to read from it, and it is awaited so that it has finished before the first code of
    /// the job does
    /// </remarks>
    public async Task<SequenceOutcome> StartAsync(JobState state, CancellationToken cancellationToken)
    {
        jobMonitor.Start();
        await macroRunner.TryRunAsync(CodeChannel.File, "start.g", cancellationToken: cancellationToken);
        await ApplyRestartStateAsync(state, cancellationToken);
        logger.LogInformation("Starting file print");
        return new SequenceOutcome(new Message(), Failed: false);
    }

    /// <summary>
    /// Start the job reading in the state M26 said the line it starts on was written in
    /// </summary>
    /// <param name="state">What the controller holds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// Only a job started from a file position has any of this to do: <c>resurrect.g</c> is the file
    /// that writes it, and a job started from the beginning has nothing to put back. Applied here
    /// rather than when M26 ran because <c>start.g</c> comes between the two and its own moves must
    /// not spend the fraction. This is RepRapFirmware's M24, which copies
    /// <c>restartMoveFractionDone</c> into <c>moveFractionToSkip</c> and puts the modal G command
    /// back before <c>StartPrinting</c>
    /// </remarks>
    private async ValueTask ApplyRestartStateAsync(JobState state, CancellationToken cancellationToken)
    {
        int modalGCommand;
        using (planner.Lock())
        {
            planner.State.MoveFractionToSkip = planner.State.RestartMoveFractionDone;
            modalGCommand = planner.State.RestartGCommandNumber;
            planner.State.RestartMoveFractionDone = 0.0f;
            planner.State.RestartGCommandNumber = -1;
        }

        if (modalGCommand >= 0 && state.Stream(0) is JobStream stream)
        {
            using (await stream.Reader.File.LockAsync(cancellationToken))
            {
                stream.Reader.File.ModalGCommand = modalGCommand;
            }
        }
    }

    #endregion

    #region Pausing

    /// <summary>
    /// Bring the machine to a stop, record where it stopped, and run the macro the pause asks for
    /// </summary>
    /// <param name="state">What the controller holds</param>
    /// <param name="request">What kind of pause, and why</param>
    /// <param name="boundaryPosition">
    /// Where the reader stopped, for a pause that asked the engine for nothing
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the sequence did, including where each stream carries on from</returns>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>DoSynchronousPause</c> and <c>DoAsynchronousPause</c> followed by the
    /// <c>pausing1</c> and <c>pausing2</c> states. They are one sequence here because the only
    /// difference between them is where the machine is allowed to stop: a synchronous pause is a
    /// command in the job file, so the file has already reached the pause point and everything
    /// before it must run, while an asynchronous one interrupts whatever the job was doing.
    /// </para>
    /// <para>
    /// The first steps cannot be cancelled by the caller, which is what lets the controller settle to
    /// paused on every outcome: by the time anything that can fail is reached, the readers are at a
    /// known point and the machine has been told to stop
    /// </para>
    /// </remarks>
    public async Task<SequenceOutcome> PauseAsync(JobState state, PauseRequest request, long? boundaryPosition,
                                                  CancellationToken cancellationToken)
    {
        // Nothing more is read and nothing more is dispatched. This comes first because it is what
        // lets the steps below finish: a job code waiting on a temperature would otherwise hold the
        // pause up for as long as the heater takes. Waiting for the codes already in flight comes
        // after the stop, not here: one of them may be a deferred code parked on a move this pause
        // is about to drop
        foreach (JobStream stream in state.Streams)
        {
            stream.Reader.Freeze();
        }

        // The engine plans a deceleration at the first move it has not committed and drops the rest.
        // A synchronous pause asks for none: the job file has reached the pause point, so everything
        // queued ahead of it is what has to run and there is nothing to purge
        MovePlanner.FeedholdOutcome held = default;
        if (!request.Synchronous)
        {
            held = await planner.StopEarlyAsync(plannedDeceleration: true, moveInterpreter, cancellationToken);

            // Deferred codes anchored past the move the machine stops on are dropped: the moves they
            // were waiting for will never run, and the rewind re-reads their lines, so each fires
            // exactly once. The boundary is the last surviving move and not the purge count, because
            // a stop that finds nothing to purge in the ring still discards what was on its way to it
            if (held.Stopped)
            {
                codeProcessor.CancelDeferredCodesAfter(CodeChannel.File, held.LastSurvivingMoveId + 1);
            }
        }

        // The machine is stopping somewhere the macros the job was inside did not expect, so what
        // they had left to do is no longer meaningful. The job file itself stays, because the resume
        // reads from it again. Whether anything was abandoned is RepRapFirmware's pausedInMacro
        bool abandonedMacros = await codeProcessor.AbandonMacrosForPauseAsync(CodeChannel.File, cancellationToken);

        // Every code the streams started has now either completed or been cancelled, so where each
        // reader says it is describes a boundary the machine has really reached
        foreach (JobStream stream in state.Streams)
        {
            await stream.Reader.DrainAsync();
        }

        // Where each stream carries on from. Only a pause that asked the engine to stop has anything
        // to work out: one that did not stops where the reader says it is, which is the end of the
        // last job code that completed
        List<StreamRewind> rewinds = [];
        foreach (JobStream stream in state.Streams)
        {
            JobResumePoint? point = null;
            bool restartMacro = abandonedMacros;
            if (stream.Index == 0 && !request.Synchronous)
            {
                MovePlanner.JobRewindPoint rewind = planner.JobRewindPointFor(held);
                point = rewind.Point;
                restartMacro = abandonedMacros || rewind.RestartMacro;
            }

            long position = point?.FilePosition
                            ?? (stream.Index == 0 ? boundaryPosition : null)
                            ?? stream.Reader.Position;
            await stream.Reader.RewindAsync(position, cancellationToken);
            rewinds.Add(new StreamRewind(stream.Index, point, restartMacro));

            logger.LogInformation("Job on {Channel} has been paused at byte {Offset}, reason {PauseReason}",
                                  stream.Channel, position, request.Reason);
        }

        // Through the code processor rather than the planner: the machine is not stopped while owed
        // deferred codes are still delivering their effects
        await codeProcessor.WaitForStandstillAsync(cancellationToken);

        // Where the machine came to rest, so the resume can put it back there
        await SaveRestorePointAsync(held, rewinds.Count > 0 ? rewinds[0].Point : null, cancellationToken);

        await RunPauseMacroAsync(request.Channel, request.Macro, cancellationToken);

        Message reply = await PausedAtMessageAsync(request.Macro, cancellationToken);
        return new SequenceOutcome(request.ReportPosition ? reply : new Message(), Failed: false, Rewinds: rewinds);
    }

    /// <summary>
    /// Save where the machine came to rest, so a resume can put it back
    /// </summary>
    /// <param name="held">What the stop did, if the pause made one</param>
    /// <param name="resume">Where the job carries on from</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// <para>
    /// A stop that dropped queued moves leaves the interpreter's position describing the end of the
    /// queue rather than where the machine stopped, so it is put back into step with the machine
    /// first: everything below reads it, and the whole point of the restore point is that it says
    /// where the head actually is. RepRapFirmware takes the same value from the same place - the end
    /// coordinates of the last move it kept, inverse-transformed - in <c>DDARing::PausePrint</c>.
    /// </para>
    /// <para>
    /// The feed rate saved is the <c>File</c> channel's, which is the file buffer's in
    /// RepRapFirmware's <c>DoAsynchronousPause</c>: it is the job's own feed rate that the resumed
    /// line has to be read with, not that of whichever channel happened to command the pause
    /// </para>
    /// </remarks>
    private async ValueTask SaveRestorePointAsync(MovePlanner.FeedholdOutcome held, JobResumePoint? resume,
                                                  CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            InputChannel? input = model.Inputs[CodeChannel.File];
            float unitScale = input?.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
            float feedRateMmPerSec = (input?.FeedRate ?? 0.0f) * unitScale / SecondsPerMinute;

            using (planner.Lock())
            {
                // The stop put the interpreter's position right under the lock it purged in, and the
                // submission it interrupted did the same as it unwound, so there is nothing to
                // correct here - only to report, because a client's idea of where the machine is came
                // from the queue that was dropped
                if (held.Stopped || resume is not null)
                {
                    planner.PublishCommittedPosition();
                }

                planner.State.SavePosition(RestorePoint.PauseNumber,
                                           planner.Parameters.SharedAxisCount(model.Move),
                                           feedRateMmPerSec, model.State.CurrentTool, filePosition: null);

                // What the file position alone cannot say, from the same value that supplied it: the
                // fraction of the code the machine has already made, the modal G command that code
                // was read under, and the feed rate it was read with. Without them a resume that
                // lands part-way through a line re-runs the part already made, and a line that names
                // neither G nor F is read with whatever happens to be modal after resume.g
                if (resume is JobResumePoint point)
                {
                    RestorePoint rp = planner.State.RestorePoints[RestorePoint.PauseNumber];
                    rp.ProportionDone = point.ProportionDone;
                    rp.GCommandNumber = point.GCommandNumber;
                    rp.FeedRate = point.FeedRateMmPerSec;
                    rp.AxesRelative = point.AxesRelative;
                    rp.DrivesRelative = point.DrivesRelative;
                }
                planner.PublishRestorePoints();
            }
        }
    }

    /// <summary>
    /// Run the macro a pause asks for
    /// </summary>
    /// <param name="channel">Channel to run it on</param>
    /// <param name="macro">Which macro</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// RepRapFirmware runs these only when every axis is homed, because <c>pause.g</c> is written to
    /// lift and park the head and neither is meaningful on a machine that does not know where it is
    /// </remarks>
    private async ValueTask RunPauseMacroAsync(CodeChannel channel, PauseMacro macro, CancellationToken cancellationToken)
    {
        if (macro == PauseMacro.None || !await AllAxesHomedAsync(cancellationToken))
        {
            return;
        }

        if (macro == PauseMacro.FilamentChange &&
            await macroRunner.TryRunAsync(channel, "filament-change.g", cancellationToken: cancellationToken))
        {
            return;
        }
        await macroRunner.TryRunAsync(channel, "pause.g", cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Announce where the job paused
    /// </summary>
    /// <param name="macro">Which macro ran, which is what names the message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The message</returns>
    /// <remarks>RepRapFirmware's <c>pausing2</c> and <c>filamentChangePause2</c> reply</remarks>
    private async ValueTask<Message> PausedAtMessageAsync(PauseMacro macro, CancellationToken cancellationToken)
    {
        System.Text.StringBuilder builder = new(macro == PauseMacro.FilamentChange
                                                ? "Printing paused for filament change at"
                                                : "Printing paused at");
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            using (planner.Lock())
            {
                RestorePoint rp = planner.State.RestorePoints[RestorePoint.PauseNumber];
                int numAxes = planner.Parameters.SharedAxisCount(model.Move);
                for (int axis = 0; axis < numAxes; axis++)
                {
                    builder.Append(System.Globalization.CultureInfo.InvariantCulture,
                                   $" {model.Move.Axes[axis].Letter}{rp.Coords[axis]:F1}");
                }
            }
        }

        string text = builder.ToString();
        logger.LogInformation("{Message}", text);
        await eventLogger.LogOutputAsync(MessageType.Warning, text);
        return new Message(MessageType.Success, text);
    }

    #endregion

    #region Resuming

    /// <summary>
    /// Put the machine back where the pause left it and read on
    /// </summary>
    /// <param name="state">What the controller holds</param>
    /// <param name="runMacro">Whether to run <c>resume.g</c>, cleared by <c>M24 P0</c></param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the sequence did</returns>
    /// <remarks>RepRapFirmware's <c>resuming1</c> to <c>resuming3</c></remarks>
    public async Task<SequenceOutcome> ResumeAsync(JobState state, bool runMacro, CancellationToken cancellationToken)
    {
        if (runMacro && await AllAxesHomedAsync(cancellationToken))
        {
            await macroRunner.TryRunAsync(CodeChannel.File, "resume.g", cancellationToken: cancellationToken);
        }

        await MoveBackToRestorePointAsync(cancellationToken);
        await RestoreInterpreterStateAsync(cancellationToken);

        // What a line the machine is already part-way through is owed, spent by the first move the
        // resumed job reads. RepRapFirmware's ResumeAfterPause
        using (planner.Lock())
        {
            planner.State.MoveFractionToSkip = planner.State.RestorePoints[RestorePoint.PauseNumber].ProportionDone;
        }

        logger.LogInformation("Printing resumed");
        await eventLogger.LogOutputAsync(MessageType.Warning, "Printing resumed");
        return new SequenceOutcome(new Message(), Failed: false);
    }

    /// <summary>
    /// Move the head back to where the job paused
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>resuming1</c> and <c>resuming2</c>. Whether it takes one move or two is
    /// decided by where the head is now: if it sits <em>above</em> where the job paused - which is
    /// what <c>pause.g</c> normally leaves it, having lifted and parked - then it travels across
    /// first and comes down last, so the nozzle never drags over the print. If it is at or below the
    /// pause height there is nothing to drag over, and everything moves together in one move.
    /// </para>
    /// <para>
    /// TODO only the single-motion-system branch is ported. RepRapFirmware's <c>SUPPORT_ASYNC_MOVES</c>
    /// branch allocates each axis to whichever motion system owns it before restoring it, which needs
    /// the axis ownership M596 brings. When it lands the restore becomes per-system and this ordering
    /// has to hold across both
    /// </para>
    /// </remarks>
    private async ValueTask MoveBackToRestorePointAsync(CancellationToken cancellationToken)
    {
        bool headIsAbovePausePoint;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            using (planner.Lock())
            {
                RestorePoint rp = planner.State.RestorePoints[RestorePoint.PauseNumber];
                int zAxis = AxisIndices.ZAxisIndex(model.Move);

                // Both sides are user coordinates. RepRapFirmware compares its machine Z against the
                // restore point's user Z, which differ by the tool Z offset and the babystep; the
                // comparison means "is the head above where it paused", and this is that question
                // asked in one coordinate system
                headIsAbovePausePoint = zAxis >= 0
                                        && planner.State.CurrentUserPosition[zAxis] > rp.Coords[zAxis];
            }
        }

        if (headIsAbovePausePoint)
        {
            await RestoreAxesAsync(AxisSelection.ExceptZ, cancellationToken);
            await RestoreAxesAsync(AxisSelection.ZOnly, cancellationToken);
        }
        else
        {
            await RestoreAxesAsync(AxisSelection.All, cancellationToken);
        }
    }

    /// <summary>
    /// Which axes one leg of the resume move restores
    /// </summary>
    private enum AxisSelection
    {
        /// <summary>Every axis, including Z</summary>
        All,

        /// <summary>Everything except Z, so the head travels at its current height</summary>
        ExceptZ,

        /// <summary>Z by itself</summary>
        ZOnly
    }

    /// <summary>
    /// Move some of the axes back to the restore point and wait for them to get there
    /// </summary>
    /// <param name="selection">Which axes to move</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async ValueTask RestoreAxesAsync(AxisSelection selection, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            MoveSubmitResult result;
            using (await model.AccessReadWriteAsync(cancellationToken))
            {
                using (planner.Lock())
                {
                    RestorePoint rp = planner.State.RestorePoints[RestorePoint.PauseNumber];
                    int numAxes = planner.Parameters.SharedAxisCount(model.Move);
                    int zAxis = AxisIndices.ZAxisIndex(model.Move);

                    RawMove move = new()
                    {
                        IsCoordinated = true,
                        FeedRateMmPerSec = ResumeFeedRateMmPerSec
                    };

                    bool anythingToDo = false;
                    for (int axis = 0; axis < numAxes; axis++)
                    {
                        bool isZ = axis == zAxis;
                        if ((selection == AxisSelection.ZOnly && !isZ) ||
                            (selection == AxisSelection.ExceptZ && isZ))
                        {
                            continue;
                        }
                        if (planner.State.CurrentUserPosition[axis] == rp.Coords[axis])
                        {
                            continue;
                        }

                        planner.State.CurrentUserPosition[axis] = rp.Coords[axis];
                        anythingToDo = true;
                        if (model.Move.Axes[axis].Rotational)
                        {
                            move.RotationalAxesMentioned = true;
                        }
                        else
                        {
                            move.LinearAxesMentioned = true;
                        }
                    }

                    if (!anythingToDo)
                    {
                        return;
                    }

                    ToolTransform.Apply(toolManager.Current, model.Move, planner.State, move.Coords, numAxes);
                    result = planner.QueueMove(move);
                    if (result is MoveSubmitResult.Queued or MoveSubmitResult.NoMovement or MoveSubmitResult.Rejected)
                    {
                        planner.PublishCommittedPosition();
                    }
                }
            }

            if (result is MoveSubmitResult.Queued or MoveSubmitResult.NoMovement or MoveSubmitResult.Rejected)
            {
                await planner.StandstillAsync(cancellationToken);
                return;
            }
            await Task.Delay(RingFullRetryDelay, cancellationToken);
        }
    }

    /// <summary>
    /// Put back the interpreter state the interrupted line was read with
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>resuming3</c>, which restores the feed rate. The distance modes go with
    /// it here because this port reads ahead and RepRapFirmware does not: a G90, G91, M82 or M83
    /// further down the file may already have run by the time the stop lands, and rewinding the file
    /// does not undo it, so the re-read line would be interpreted in a mode it was never written
    /// for. Only what the stop actually named is put back - see <see cref="RestorePoint"/>.
    /// </para>
    /// <para>
    /// The fan speeds are deliberately <em>not</em> restored: a tool change during the pause would
    /// have set them for the new tool, and putting the old ones back would undo it. A machine that
    /// wants them back does it in <c>resume.g</c>
    /// </para>
    /// </remarks>
    private async ValueTask RestoreInterpreterStateAsync(CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (model.Inputs[CodeChannel.File] is InputChannel input)
            {
                float feedRateMmPerSec;
                bool? axesRelative, drivesRelative;
                using (planner.Lock())
                {
                    RestorePoint rp = planner.State.RestorePoints[RestorePoint.PauseNumber];
                    feedRateMmPerSec = rp.FeedRate;
                    axesRelative = rp.AxesRelative;
                    drivesRelative = rp.DrivesRelative;
                }

                if (axesRelative is bool axesWereRelative)
                {
                    input.AxesRelative = axesWereRelative;
                }
                if (drivesRelative is bool drivesWereRelative)
                {
                    input.DrivesRelative = drivesWereRelative;
                }

                float unitScale = input.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
                if (unitScale != 0.0f)
                {
                    input.FeedRate = feedRateMmPerSec * SecondsPerMinute / unitScale;
                }
            }
        }
    }

    #endregion

    #region Finishing

    /// <summary>
    /// Wait for the moves the file queued last to be made
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the sequence did</returns>
    /// <remarks>
    /// The file running out of codes is not the end of the job: the moves it queued last have still
    /// to run, an asynchronous pause may still arrive while they do, and the machine has to keep
    /// reporting the job rather than an idle machine until it has actually stopped. RepRapFirmware
    /// waits for standstill at this same point, before it closes the file and stops the print
    /// </remarks>
    public async Task<SequenceOutcome> WaitForLastMovesAsync(CancellationToken cancellationToken)
    {
        await codeProcessor.WaitForStandstillAsync(cancellationToken);
        return new SequenceOutcome(new Message(), Failed: false);
    }

    /// <summary>
    /// Put the machine down, running whatever macro the reason calls for
    /// </summary>
    /// <param name="channel">Channel the macro runs on</param>
    /// <param name="reason">Why the job stopped</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the sequence did</returns>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>GCodes::StopPrint</c> and the <c>stopping</c> and <c>cancelling</c>
    /// states. Which macro runs depends on how the job ended, and the order is not interchangeable: a
    /// cancelled job runs <c>cancel.g</c> <em>instead of</em> <c>stop.g</c>, and only when there is
    /// no macro at all do the heaters go off.
    /// </para>
    /// <para>
    /// An aborted job runs no macro. It stopped because something went wrong, so the machine is put
    /// in a safe state directly rather than through a file that may itself depend on what broke
    /// </para>
    /// </remarks>
    public async Task<SequenceOutcome> StopAsync(CodeChannel channel, PrintStoppedReason reason,
                                                 CancellationToken cancellationToken)
    {
        await UnwindZHopAsync(cancellationToken);

        switch (reason)
        {
            case PrintStoppedReason.UserCancelled:
                // cancel.g replaces stop.g entirely, and only when neither exists do the heaters go
                // off. RepRapFirmware writes this as two nested DoFileMacro calls
                if (!await macroRunner.TryRunAsync(channel, "cancel.g", cancellationToken: cancellationToken) &&
                    !await macroRunner.TryRunAsync(channel, "stop.g", cancellationToken: cancellationToken))
                {
                    await heatManager.SwitchOffAllAsync(includingChamberAndBed: true, cancellationToken);
                }
                break;

            case PrintStoppedReason.NormalCompletion:
                if (!await macroRunner.TryRunAsync(channel, "stop.g", cancellationToken: cancellationToken))
                {
                    await heatManager.SwitchOffAllAsync(includingChamberAndBed: true, cancellationToken);
                }
                break;

            case PrintStoppedReason.Abort:
                // No macro: the job stopped because something went wrong, and a file that depends on
                // the machine working is the wrong thing to reach for
                await heatManager.SwitchOffAllAsync(includingChamberAndBed: true, cancellationToken);
                await spindleManager.StopAllAsync(cancellationToken);
                // TODO the laser is switched off here too in RepRapFirmware, once M452 exists
                break;
        }
        return new SequenceOutcome(new Message(), Failed: false);
    }

    /// <summary>
    /// Record what the job that has ended did
    /// </summary>
    /// <param name="state">What the controller holds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>What the sequence did</returns>
    public async Task<SequenceOutcome> TeardownAsync(JobState state, CancellationToken cancellationToken)
    {
        bool cancelled = state.StopReason == PrintStoppedReason.UserCancelled;
        bool aborted = state.StopReason == PrintStoppedReason.Abort;
        bool simulating = state.IsSimulating;

        // The monitor is told rather than left to notice, and it answers with what the job took, so
        // nothing has to wait for a figure to appear in the object model
        int duration = await jobMonitor.FinishAsync(cancellationToken);

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            model.Job.File.CustomInfo.Clear();
            model.Job.LastFileAborted = aborted;
            model.Job.LastFileCancelled = cancelled;
            model.Job.LastFileSimulated = simulating;
        }

        if (simulating && state.File?.UpdateSimulatedTime == true && !aborted && !cancelled)
        {
            if (duration > 0)
            {
                await fileInfoParser.UpdateSimulatedTimeAsync(state.File.File.FilePath.Physical, duration, cancellationToken);
            }
            else
            {
                logger.LogWarning("Failed to update simulation time because the simulation reported no duration");
            }
        }
        return new SequenceOutcome(new Message(), Failed: false);
    }

    /// <summary>
    /// Put back the Z hop of a retraction the job never undid
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// RepRapFirmware's <c>StopPrint</c> does this to every motion system: a job stopped between a
    /// G10 and its G11 leaves the tool retracted, and the hop is part of the transform, so the user
    /// position has to gain it back or the machine reports a height it is not at
    /// </remarks>
    private async ValueTask UnwindZHopAsync(CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (toolManager.Current is not Tool tool || !tool.IsRetracted)
            {
                return;
            }

            int zAxis = AxisIndices.ZAxisIndex(model.Move);
            if (zAxis >= 0)
            {
                using (planner.Lock())
                {
                    planner.State.CurrentUserPosition[zAxis] += ToolTransform.ActualZHop(tool);
                }
            }
            tool.IsRetracted = false;
        }
    }

    #endregion

    /// <summary>
    /// Whether every axis knows where it is
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if no axis is unhomed</returns>
    private async ValueTask<bool> AllAxesHomedAsync(CancellationToken cancellationToken)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            foreach (Axis axis in model.Move.Axes)
            {
                if (axis.Visible && !axis.Homed)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
