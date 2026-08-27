using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
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
    /// <param name="feedhold">Whether to stop by planned deceleration rather than by draining the queue</param>
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
                                               bool synchronous, bool feedhold, bool reportPosition,
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
            // An asynchronous pause stops the machine before the queue has run, so it happens first:
            // everything below is about what the machine does once it has stopped, and there is no
            // point recording where that is until it has.
            //
            // The engine plans a deceleration at the first move it has not committed and drops the
            // rest, rather than looking for a junction the toolpath is already slow enough to stop
            // at as RepRapFirmware does - during a fast print that search finds nothing and the whole
            // queue runs. A synchronous pause does neither: the job file has reached the pause point,
            // so everything queued ahead of it is what has to run and there is nothing to purge
            MovePlanner.FeedholdOutcome held = default;
            if (!synchronous)
            {
                held = await _planner.StopEarlyAsync(plannedDeceleration: feedhold, _moveInterpreter, cancellationToken);

                // Deferred codes anchored past the move the machine stops on are dropped: the moves
                // they were waiting for will never run, and the rewind re-reads their lines, so each
                // fires exactly once. Codes anchored at or before it are owed, because the moves the
                // stop left standing all run to completion.
                //
                // The boundary is the last surviving move and not the purge count, because a stop
                // that finds nothing to purge in the ring still discards what was on its way to it.
                // Waiting on those anchors is a wait that never ends, and the standstill below is
                // what would do the waiting - so a pause that dropped nothing from the ring would
                // hang for as long as the machine was left on
                if (held.Stopped)
                {
                    _codeProcessor.CancelDeferredCodesAfter(CodeChannel.File, held.LastSurvivingMoveId + 1);
                }
            }

            Motion.JobResumePoint? resume;
            using (await LockAsync(cancellationToken))
            {
                // Where the job carries on from, taken once and before the read-ahead is cancelled:
                // taking the record of the code that was going out is what fixes how much of it the
                // machine will have made, and the cancellation would otherwise end that submission
                // somewhere this has not looked
                resume = _planner.TakeJobResumePoint(held);

                // Cancel what the job has read ahead. This comes first because it is what lets the
                // flush below finish: a job code waiting on a temperature would otherwise hold it up
                // for as long as the heater takes.
                //
                // A pause with no code left part-way through supplies no file position. DoFilePrint
                // rewinds to the end of the last code that actually completed, and the read-ahead
                // codes cancelled here do not complete, so that is the pause point without anything
                // having to compute it. For a synchronous pause it is the code after the M226 -
                // supplying the position of the M226 itself would re-run it on resume and never make
                // progress. A stop that interrupted a code is the other case: the job had already
                // read past the point the machine will come to rest at, so the resume point says
                // where to go back to, and the fraction of that code not to make again goes with it
                StopReadingForPause(resume?.FilePosition, filePosition2: null, reason);

                // A pause commanded from within the job cancelled its own code along with the rest of
                // the job's, and its token is the one every step below is waiting on. Re-arm it so the
                // sequence can finish and the code can report what it did
                pausingCode?.ResetCancellationToken();
            }

            // The macros the job was inside are abandoned: the machine is stopping somewhere they did
            // not expect, so what they had left to do is no longer meaningful. The job file itself
            // stays, because the resume reads from it again. Whether anything was abandoned is
            // RepRapFirmware's pausedInMacro: the resume marks the replayed command as a restart
            _pausedInMacro = await _codeProcessor.AbandonMacrosForPauseAsync(CodeChannel.File, cancellationToken);

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

            // Through the code processor rather than the planner: the machine is not stopped while
            // owed deferred codes are still delivering their effects
            await _codeProcessor.WaitForStandstillAsync(cancellationToken);

            // Where the machine came to rest, so the resume can put it back there
            await SaveRestorePointAsync(channel, held, resume, cancellationToken);

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
    /// Whether the last pause abandoned macros the job was inside
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>MovementState::pausedInMacro</c>. The rewind point of such a pause is the
    /// command that started the outermost abandoned macro, so the resume re-runs the macro whole;
    /// marking the job file lets it read <c>state.macroRestarted</c> and skip what must not repeat
    /// </remarks>
    private bool _pausedInMacro;

    /// <summary>
    /// A pause that has been asked for but cannot happen yet, and the macro it will run
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>deferredPauseCommandPending</c>. A job inside a macro that has not said it
    /// is restartable must not be interrupted part-way - the macro would be abandoned with no way to
    /// put back what it had already done - so the request is held until the job is back out of it
    /// </remarks>
    private PauseMacro? _deferredPause;

    /// <summary>
    /// Whether a pause is waiting for the job to leave a macro it cannot be interrupted inside
    /// </summary>
    public bool IsPauseDeferred
    {
        get
        {
            using (Lock())
            {
                return _deferredPause is not null;
            }
        }
    }

    /// <summary>
    /// Ask for a pause once the job is out of the macro it is in
    /// </summary>
    /// <param name="macro">Which macro the pause will run</param>
    /// <returns>True if the request was taken, false if one was already pending</returns>
    /// <remarks>
    /// A filament change takes priority over an ordinary pause, which is RepRapFirmware's rule: it
    /// stashes the stronger request and refuses the weaker one rather than replacing it
    /// </remarks>
    public bool TryDeferPause(PauseMacro macro)
    {
        using (Lock())
        {
            if (_deferredPause is not null && _deferredPause != PauseMacro.FilamentChange)
            {
                if (macro != PauseMacro.FilamentChange)
                {
                    return false;
                }
            }
            else if (_deferredPause is not null)
            {
                return false;
            }
            _deferredPause = macro;
            return true;
        }
    }

    /// <summary>
    /// Action a pause that was deferred, if the job is now somewhere it can be paused
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <remarks>
    /// RepRapFirmware's <c>CheckForDeferredPause</c>, called as each code on the job channel
    /// finishes. It waits for the job to be out of macros, because that is what it was waiting for
    /// in the first place.
    ///
    /// TODO RepRapFirmware also waits for a tool change to finish (<c>!doingToolChange</c>). Nothing
    /// here says a tool change is in progress yet - the same gap MachineStatusService names for
    /// <c>ChangingTool</c> - so a pause deferred into one would act part-way through it
    /// </remarks>
    public async ValueTask CheckForDeferredPauseAsync(CancellationToken cancellationToken)
    {
        PauseMacro macro;
        using (await LockAsync(cancellationToken))
        {
            if (_deferredPause is null || PauseState != PauseState.NotPaused || !IsProcessing)
            {
                return;
            }
            if (_codeProcessor.IsDoingMacro(CodeChannel.File))
            {
                return;         // still inside the macro it was deferred out of
            }
            macro = _deferredPause.Value;
            _deferredPause = null;
        }

        await PauseAsync(CodeChannel.File,
                         macro == PauseMacro.FilamentChange ? PrintPausedReason.FilamentChange : PrintPausedReason.User,
                         macro, synchronous: true, feedhold: false, reportPosition: true,
                         pausingCode: null, cancellationToken);
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
                // What M26 said about the line the job starts on, applied here rather than there
                // because start.g comes between the two and its own moves must not spend it. This is
                // RepRapFirmware's M24, which copies restartMoveFractionDone into moveFractionToSkip
                // and puts the modal G command back before StartPrinting
                await ApplyRestartStateAsync(cancellationToken);

                // A job that does not begin at the top of the file is a restart - resurrect.g wrote
                // the M26 - and RepRapFirmware's StartPrinting marks its first command as one
                if (_file is not null)
                {
                    using (await _file.LockAsync(cancellationToken))
                    {
                        if (_file.Position > 0)
                        {
                            _file.FirstCommandAfterRestart = true;
                        }
                    }
                }
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
            await RestoreInterpreterStateAsync(cancellationToken);

            // The pause abandoned the macro the job was inside and rewound the file to the command
            // that started it, so that command is about to run again. RepRapFirmware's resuming3
            // marks it the same way through firstCommandAfterRestart
            if (_pausedInMacro && _file is not null)
            {
                _pausedInMacro = false;
                using (await _file.LockAsync(cancellationToken))
                {
                    _file.FirstCommandAfterRestart = true;
                }
            }

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
    /// Stop the job, running whatever macro the reason calls for
    /// </summary>
    /// <param name="channel">Channel the stop was commanded from, which the macro runs on</param>
    /// <param name="reason">Why the job stopped</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>GCodes::StopPrint</c> and the <c>stopping</c> / <c>cancelling</c> states.
    /// Which macro runs depends on how the job ended, and the order is not interchangeable: a
    /// cancelled job runs <c>cancel.g</c> <em>instead of</em> <c>stop.g</c>, and only when there is
    /// no macro at all do the heaters go off.
    /// </para>
    /// <para>
    /// An aborted job runs no macro. It stopped because something went wrong, so the machine is put
    /// in a safe state directly rather than through a file that may itself depend on what broke
    /// </para>
    /// </remarks>
    public async ValueTask StopAsync(CodeChannel channel, PrintStoppedReason reason, CancellationToken cancellationToken)
    {
        using (await LockAsync(cancellationToken))
        {
            if (_stopped)
            {
                return;
            }

            // The guard is only about a job finishing twice - once because M0 stopped it and again
            // because its file then ran out of codes. With no job selected, M0 is the operator
            // putting the machine down and must work every time it is given
            _stopped = IsFileSelected;

            if (reason == PrintStoppedReason.UserCancelled)
            {
                // Observable while cancel.g runs, which is after the job file has already closed
                PauseState = PauseState.Cancelling;
            }
        }

        try
        {
            await UnwindZHopAsync(cancellationToken);

            switch (reason)
            {
                case PrintStoppedReason.UserCancelled:
                    // cancel.g replaces stop.g entirely, and only when neither exists do the heaters
                    // go off. RepRapFirmware writes this as two nested DoFileMacro calls
                    if (!await _macroRunner.TryRunAsync(channel, "cancel.g", cancellationToken: cancellationToken) &&
                        !await _macroRunner.TryRunAsync(channel, "stop.g", cancellationToken: cancellationToken))
                    {
                        await _heatManager.SwitchOffAllAsync(includingChamberAndBed: true, cancellationToken);
                    }
                    break;

                case PrintStoppedReason.NormalCompletion:
                    if (!await _macroRunner.TryRunAsync(channel, "stop.g", cancellationToken: cancellationToken))
                    {
                        await _heatManager.SwitchOffAllAsync(includingChamberAndBed: true, cancellationToken);
                    }
                    break;

                case PrintStoppedReason.Abort:
                    // No macro: the job stopped because something went wrong, and a file that depends
                    // on the machine working is the wrong thing to reach for
                    await _heatManager.SwitchOffAllAsync(includingChamberAndBed: true, cancellationToken);
                    await _spindleManager.StopAllAsync(cancellationToken);
                    // TODO the laser is switched off here too in RepRapFirmware, once M452 exists
                    break;
            }
        }
        finally
        {
            using (await LockAsync(CancellationToken.None))
            {
                if (PauseState == PauseState.Cancelling)
                {
                    PauseState = PauseState.NotPaused;
                }
            }
        }
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
        using (await _model.AccessReadWriteAsync(cancellationToken))
        {
            if (_toolManager.Current is not Tool tool || !tool.IsRetracted)
            {
                return;
            }

            int zAxis = AxisIndices.ZAxisIndex(_model.Move);
            if (zAxis >= 0)
            {
                using (_planner.Lock())
                {
                    _planner.State.CurrentUserPosition[zAxis] += ToolTransform.ActualZHop(tool);
                }
            }
            tool.IsRetracted = false;
        }
    }

    /// <summary>
    /// Start the job reading in the state M26 said the line it starts on was written in
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// Only a job started from a file position has any of this to do: <c>resurrect.g</c> is the file
    /// that writes it, and a job started from the beginning has nothing to put back. The fraction is
    /// spent by the first move the job reads, so both are cleared here rather than left to leak into
    /// whatever runs next. This class must be locked
    /// </remarks>
    private async ValueTask ApplyRestartStateAsync(CancellationToken cancellationToken)
    {
        int modalGCommand;
        using (_planner.Lock())
        {
            _planner.State.MoveFractionToSkip = _planner.State.RestartMoveFractionDone;
            modalGCommand = _planner.State.RestartGCommandNumber;
            _planner.State.RestartMoveFractionDone = 0.0f;
            _planner.State.RestartGCommandNumber = -1;
        }

        if (modalGCommand >= 0 && _file is not null)
        {
            using (await _file.LockAsync(cancellationToken))
            {
                _file.ModalGCommand = modalGCommand;
            }
        }
    }

    /// <summary>
    /// Save where the machine came to rest, so a resume can put it back
    /// </summary>
    /// <param name="channel">Channel the pause was commanded from, whose feed rate is saved</param>
    /// <param name="held">What the stop did, if the pause made one</param>
    /// <param name="resume">Where the job carries on from, as the pause took it</param>
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
    /// The resume point is the same value the file was rewound to, so the fraction written here and
    /// the position the job reads from cannot describe different lines
    /// </para>
    /// </remarks>
    private async ValueTask SaveRestorePointAsync(CodeChannel channel, MovePlanner.FeedholdOutcome held,
                                                  Motion.JobResumePoint? resume, CancellationToken cancellationToken)
    {
        using (await _model.AccessReadWriteAsync(cancellationToken))
        {
            InputChannel? input = _model.Inputs[channel];
            float unitScale = input?.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
            float feedRateMmPerSec = (input?.FeedRate ?? 0.0f) * unitScale / SecondsPerMinute;

            using (_planner.Lock())
            {
                // The stop put the interpreter's position right under the lock it purged in, and the
                // submission it interrupted did the same as it unwound, so there is nothing to
                // correct here - only to report, because a client's idea of where the machine is came
                // from the queue that was dropped
                if (held.Stopped || resume is not null)
                {
                    _planner.PublishCommittedPosition();
                }

                _planner.State.SavePosition(Motion.RestorePoint.PauseNumber,
                                            _planner.Parameters.SharedAxisCount(_model.Move),
                                            feedRateMmPerSec, _model.State.CurrentTool, filePosition: null);

                // What the file position alone cannot say, from the same value that supplied it: the
                // fraction of the code the machine has already made, the modal G command that code
                // was read under, and the feed rate it was read with. Without them a resume that
                // lands part-way through a line re-runs the part already made, and a line that names
                // neither G nor F is read with whatever happens to be modal after resume.g
                if (resume is Motion.JobResumePoint point)
                {
                    Motion.RestorePoint rp = _planner.State.RestorePoints[Motion.RestorePoint.PauseNumber];
                    rp.ProportionDone = point.ProportionDone;
                    rp.GCommandNumber = point.GCommandNumber;
                    rp.FeedRate = point.FeedRateMmPerSec;
                    rp.AxesRelative = point.AxesRelative;
                    rp.DrivesRelative = point.DrivesRelative;
                }
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
    /// Put back the interpreter state the interrupted line was read with
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>resuming3</c>, which restores the feed rate. The distance modes go with
    /// it here because this port reads ahead and RepRapFirmware does not: a G90, G91, M82 or M83
    /// further down the file may already have run by the time the stop lands, and rewinding the file
    /// does not undo it, so the re-read line would be interpreted in a mode it was never written
    /// for. Only what the stop actually named is put back - see <see cref="Motion.RestorePoint"/>.
    /// </para>
    /// <para>
    /// The fan speeds are deliberately <em>not</em> restored: a tool change during the pause would
    /// have set them for the new tool, and putting the old ones back would undo it. A machine that
    /// wants them back does it in <c>resume.g</c>
    /// </para>
    /// </remarks>
    private async ValueTask RestoreInterpreterStateAsync(CancellationToken cancellationToken)
    {
        using (await _model.AccessReadWriteAsync(cancellationToken))
        {
            if (_model.Inputs[CodeChannel.File] is InputChannel input)
            {
                float feedRateMmPerSec;
                bool? axesRelative, drivesRelative;
                using (_planner.Lock())
                {
                    Motion.RestorePoint rp = _planner.State.RestorePoints[Motion.RestorePoint.PauseNumber];
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
