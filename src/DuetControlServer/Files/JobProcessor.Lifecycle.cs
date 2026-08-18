using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Motion;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Files;

/// <summary>
/// Which macro a pause runs, if any
/// </summary>
/// <remarks>
/// RepRapFirmware encodes this in the state it enters - <c>pausing1</c> runs <c>pause.g</c>,
/// <c>pausing2</c> runs nothing, <c>filamentChangePause1</c> prefers <c>filament-change.g</c> - so
/// this is that choice named rather than left implicit in a state number
/// </remarks>
public enum PauseMacro
{
    /// <summary>
    /// Run no macro, as <c>M226 P0</c> and a driver error ask for
    /// </summary>
    None,

    /// <summary>
    /// Run <c>pause.g</c>
    /// </summary>
    Pause,

    /// <summary>
    /// Run <c>filament-change.g</c>, falling back to <c>pause.g</c> if there is none
    /// </summary>
    FilamentChange
}

internal partial class JobProcessor
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
    /// Pause the job
    /// </summary>
    /// <param name="channel">Channel the pause was commanded from, which the macro runs on</param>
    /// <param name="reason">Why the job is pausing</param>
    /// <param name="macro">Which macro to run once the machine has stopped</param>
    /// <param name="synchronous">Whether the pause came from a command in the job file itself</param>
    /// <param name="reportPosition">Whether to announce where the job paused</param>
    /// <param name="pausingCode">The code asking for the pause, if it is one of the job's own</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The message to report, or an error if the job cannot be paused</returns>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>DoSynchronousPause</c> and <c>DoAsynchronousPause</c> followed by the
    /// <c>pausing1</c> / <c>pausing2</c> states. They are one method here because the only difference
    /// between them is where the machine is allowed to stop: a synchronous pause is a command in the
    /// job file, so the file has already reached the pause point and everything before it must run,
    /// while an asynchronous one interrupts whatever the job was doing.
    /// </para>
    /// <para>
    /// The states exist in RepRapFirmware because <c>GCodes::Spin</c> cannot block. This can await, so
    /// the sequence is written as one - but the state it publishes is the same, and the
    /// <c>finally</c> is what <c>PauseSequenceAborted</c> is for: if <c>pause.g</c> is aborted
    /// part-way, the machine must settle to paused rather than report pausing for ever
    /// </para>
    /// </remarks>
    public async ValueTask<Message> PauseAsync(CodeChannel channel, PrintPausedReason reason, PauseMacro macro,
                                               bool synchronous, bool reportPosition,
                                               Commands.Code? pausingCode, CancellationToken cancellationToken)
    {
        using (await LockAsync(cancellationToken))
        {
            if (PauseState != PauseState.NotPaused)
            {
                return new Message(MessageType.Error, "Printing is already paused!");
            }
            if (!IsProcessing)
            {
                return new Message(MessageType.Error, "Cannot pause print, because no file is being printed!");
            }
            PauseState = PauseState.Pausing;
        }

        try
        {
            using (await LockAsync(cancellationToken))
            {
                // Cancel what the job has read ahead. This comes first because it is what lets the
                // flush below finish: a job code waiting on a temperature would otherwise hold it up
                // for as long as the heater takes.
                //
                // The file position is deliberately not supplied. DoFilePrint rewinds to the end of
                // the last code that actually completed, and the read-ahead codes cancelled here do
                // not complete, so that is the pause point without anything having to compute it. For
                // a synchronous pause it is the code after the M226 - supplying the position of the
                // M226 itself would re-run it on resume and never make progress.
                // TODO the feedhold of JOB_LIFECYCLE.md §3.5 is what supplies a real position, taken
                // from the first move it purges
                StopReadingForPause(filePosition: null, filePosition2: null, reason);

                // A pause commanded from within the job cancelled its own code along with the rest of
                // the job's, and its token is the one every step below is waiting on. Re-arm it so the
                // sequence can finish and the code can report what it did
                pausingCode?.ResetCancellationToken();
            }

            // Not the caller's token: for a synchronous pause that is the token just cancelled above.
            // The rest of the sequence stops for a shutdown and nothing else
            cancellationToken = _lifetime.ApplicationStopping;

            if (!synchronous)
            {
                // An asynchronous pause interrupts a job channel it is not running on, so what the
                // channel still holds has to be drained before the machine can be called stopped. A
                // synchronous pause is a code in that job, and its handler has already flushed
                // everything ahead of it - flushing the whole channel from inside it would wait for
                // the pausing code itself
                await _codeProcessor.FlushAsync(CodeChannel.File, flushAll: true, cancellationToken);
            }
            await _planner.WaitForStandstillAsync(cancellationToken);

            // Where the machine came to rest, so the resume can put it back there
            await SaveRestorePointAsync(channel, cancellationToken);

            await RunPauseMacroAsync(channel, macro, cancellationToken);
            return reportPosition ? await PausedAtMessageAsync(macro, cancellationToken) : new Message();
        }
        finally
        {
            // However the sequence ended - the macro was aborted, a code threw, the job was cancelled
            // underneath it - the machine is stopped and must say so. RepRapFirmware needs
            // PauseSequenceAborted for this because its state lives on a stack that unwinds; here the
            // state is settled on the way out
            using (await LockAsync(CancellationToken.None))
            {
                if (PauseState == PauseState.Pausing)
                {
                    PauseState = PauseState.Paused;
                }
            }
        }
    }

    /// <summary>
    /// Resume a paused job, or start a selected one
    /// </summary>
    /// <param name="channel">Channel the resume was commanded from, which the macro runs on</param>
    /// <param name="runMacro">Whether to run <c>resume.g</c>, cleared by <c>M24 P0</c></param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The message to report, or an error if the job cannot be resumed</returns>
    /// <remarks>RepRapFirmware's M24, and the <c>resuming1</c> to <c>resuming3</c> states</remarks>
    public async ValueTask<Message> ResumeAsync(CodeChannel channel, bool runMacro, CancellationToken cancellationToken)
    {
        bool resuming;
        using (await LockAsync(cancellationToken))
        {
            if (PauseState is PauseState.Pausing or PauseState.Resuming)
            {
                // RepRapFirmware ignores the request rather than refusing it: the machine is already
                // going where the operator asked, just not there yet
                return new Message();
            }
            if (!IsFileSelected)
            {
                return new Message(MessageType.Error, "Cannot print, because no file is selected!");
            }
            if (PauseState == PauseState.NotPaused && IsProcessing)
            {
                // Already running, so there is nothing to start or resume
                return new Message();
            }

            resuming = PauseState == PauseState.Paused;
            if (resuming)
            {
                PauseState = PauseState.Resuming;
            }
        }

        if (!resuming)
        {
            // Starting a job that was only selected. start.g runs on the file channel so that any
            // M82/M83 in it applies to the job that is about to read from it, and it is awaited so
            // that it has finished before the first code of the job does
            await _macroRunner.TryRunAsync(CodeChannel.File, "start.g", cancellationToken: cancellationToken);

            using (await LockAsync(cancellationToken))
            {
                Resume();
            }
            return new Message();
        }

        try
        {
            if (runMacro && await AllAxesHomedAsync(cancellationToken))
            {
                await _macroRunner.TryRunAsync(channel, "resume.g", cancellationToken: cancellationToken);
            }

            await MoveBackToRestorePointAsync(cancellationToken);
            await RestoreFeedRateAsync(cancellationToken);
            _logger.LogInformation("Printing resumed");
            await _eventLogger.LogOutputAsync(MessageType.Warning, "Printing resumed");
            return new Message();
        }
        finally
        {
            using (await LockAsync(CancellationToken.None))
            {
                if (PauseState == PauseState.Resuming)
                {
                    PauseState = PauseState.NotPaused;
                    if (IsFileSelected && !IsProcessing)
                    {
                        _resume.NotifyAll();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Save where the machine came to rest, so a resume can put it back
    /// </summary>
    /// <param name="channel">Channel the pause was commanded from, whose feed rate is saved</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async ValueTask SaveRestorePointAsync(CodeChannel channel, CancellationToken cancellationToken)
    {
        using (await _model.AccessReadWriteAsync(cancellationToken))
        {
            InputChannel? input = _model.Inputs[channel];
            float unitScale = input?.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
            float feedRateMmPerSec = (input?.FeedRate ?? 0.0f) * unitScale / SecondsPerMinute;

            using (_planner.Lock())
            {
                _planner.State.SavePosition(Motion.RestorePoint.PauseNumber,
                                            _planner.Parameters.SharedAxisCount(_model.Move),
                                            feedRateMmPerSec, _model.State.CurrentTool, filePosition: null);
                _planner.PublishRestorePoints();
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
            await _macroRunner.TryRunAsync(channel, "filament-change.g", cancellationToken: cancellationToken))
        {
            return;
        }
        await _macroRunner.TryRunAsync(channel, "pause.g", cancellationToken: cancellationToken);
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
        using (await _model.AccessReadOnlyAsync(cancellationToken))
        {
            using (_planner.Lock())
            {
                Motion.RestorePoint rp = _planner.State.RestorePoints[Motion.RestorePoint.PauseNumber];
                int numAxes = _planner.Parameters.SharedAxisCount(_model.Move);
                for (int axis = 0; axis < numAxes; axis++)
                {
                    builder.Append(System.Globalization.CultureInfo.InvariantCulture,
                                   $" {_model.Move.Axes[axis].Letter}{rp.Coords[axis]:F1}");
                }
            }
        }

        string text = builder.ToString();
        _logger.LogInformation("{Message}", text);
        await _eventLogger.LogOutputAsync(MessageType.Warning, text);
        return new Message(MessageType.Success, text);
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
        using (await _model.AccessReadOnlyAsync(cancellationToken))
        {
            using (_planner.Lock())
            {
                Motion.RestorePoint rp = _planner.State.RestorePoints[Motion.RestorePoint.PauseNumber];
                int zAxis = AxisIndices.ZAxisIndex(_model.Move);

                // Both sides are user coordinates. RepRapFirmware compares its machine Z against the
                // restore point's user Z, which differ by the tool Z offset and the babystep; the
                // comparison means "is the head above where it paused", and this is that question
                // asked in one coordinate system
                headIsAbovePausePoint = zAxis >= 0
                                        && _planner.State.CurrentUserPosition[zAxis] > rp.Coords[zAxis];
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
            using (await _model.AccessReadWriteAsync(cancellationToken))
            {
                using (_planner.Lock())
                {
                    Motion.RestorePoint rp = _planner.State.RestorePoints[Motion.RestorePoint.PauseNumber];
                    int numAxes = _planner.Parameters.SharedAxisCount(_model.Move);
                    int zAxis = AxisIndices.ZAxisIndex(_model.Move);

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
                        if (_planner.State.CurrentUserPosition[axis] == rp.Coords[axis])
                        {
                            continue;
                        }

                        _planner.State.CurrentUserPosition[axis] = rp.Coords[axis];
                        anythingToDo = true;
                        if (_model.Move.Axes[axis].Rotational)
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

                    ToolTransform.Apply(_toolManager.Current, _model.Move, _planner.State, move.Coords, numAxes);
                    result = _planner.QueueMove(move);
                    if (result is MoveSubmitResult.Queued or MoveSubmitResult.NoMovement or MoveSubmitResult.Rejected)
                    {
                        _planner.PublishCommittedPosition();
                    }
                }
            }

            if (result is MoveSubmitResult.Queued or MoveSubmitResult.NoMovement or MoveSubmitResult.Rejected)
            {
                await _planner.WaitForStandstillAsync(cancellationToken);
                return;
            }
            await Task.Delay(RingFullRetryDelay, cancellationToken);
        }
    }

    /// <summary>
    /// Put the job channel's feed rate back to what it was when the job paused
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// RepRapFirmware's <c>resuming3</c>. The fan speeds are deliberately <em>not</em> restored: a
    /// tool change during the pause would have set them for the new tool, and putting the old ones
    /// back would undo it. A machine that wants them back does it in <c>resume.g</c>
    /// </remarks>
    private async ValueTask RestoreFeedRateAsync(CancellationToken cancellationToken)
    {
        using (await _model.AccessReadWriteAsync(cancellationToken))
        {
            if (_model.Inputs[CodeChannel.File] is InputChannel input)
            {
                float feedRateMmPerSec;
                using (_planner.Lock())
                {
                    feedRateMmPerSec = _planner.State.RestorePoints[Motion.RestorePoint.PauseNumber].FeedRate;
                }

                float unitScale = input.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
                if (unitScale != 0.0f)
                {
                    input.FeedRate = feedRateMmPerSec * SecondsPerMinute / unitScale;
                }
            }
        }
    }

    /// <summary>
    /// Whether every axis knows where it is
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if no axis is unhomed</returns>
    private async ValueTask<bool> AllAxesHomedAsync(CancellationToken cancellationToken)
    {
        using (await _model.AccessReadOnlyAsync(cancellationToken))
        {
            foreach (Axis axis in _model.Move.Axes)
            {
                if (axis.Visible && !axis.Homed)
                {
                    return false;
                }
            }
            return true;
        }
    }

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
}
