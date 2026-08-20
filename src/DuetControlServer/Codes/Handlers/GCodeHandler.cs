using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Native;
using DuetControlServer.Motion.Kinematics;
using Microsoft.Extensions.Logging;
using DuetAPI;
using static DuetControlServer.Motion.AxisIndices;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// G-code handler
/// </summary>
/// <remarks>
/// <para>
/// This is where a movement command becomes a queued move. Everything it reads and writes lives in
/// the object model - the axis positions in <c>move.axes[]</c>, the extruder positions in
/// <c>move.extruders[]</c>, and the per-channel interpreter state in <c>inputs[]</c> - so the state
/// a move is planned against is the state every API reports.
/// </para>
/// <para>
/// The lock order is the same everywhere and matters: the planner lock is taken inside the object
/// model lock, never the other way round
/// </para>
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="planner">Where G-codes become queued moves</param>
/// <param name="bedCompensation">Height map correction</param>
/// <param name="macroRunner">Runs the machine's own macro files</param>
/// <param name="linkInterface">Link interface, for the endstops a move has to arm over CAN</param>
/// <param name="endstopCorrection">Undoes the overshoot of a move an endstop cut short</param>
/// <param name="toolManager">The selected tool, whose offsets and axis mapping the transform needs</param>
/// <param name="moveInterpreter">Turns a movement code into the move the engine is asked to run</param>
/// <param name="logger">Logger</param>
internal sealed partial class GCodeHandler(
    Model.ObjectModel model,
    MovePlanner planner,
    BedCompensation bedCompensation,
    Files.MacroRunner macroRunner,
    Link.LinkInterface linkInterface,
    EndstopCorrection endstopCorrection,
    Tools.ToolManager toolManager,
    MoveInterpreter moveInterpreter,
    ILogger<GCodeHandler> logger) : ICodeHandler
{
    /// <summary>
    /// How long to wait before retrying a move the engine had no room for
    /// </summary>
    private static readonly TimeSpan RingFullRetryDelay = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Millimetres per inch, for G20
    /// </summary>
    private const float MmPerInch = 25.4f;

    /// <summary>
    /// G-code feed rates are per minute; everything below the interpreter is per second
    /// </summary>
    private const float SecondsPerMinute = 60.0f;

    /// <summary>
    /// Process a G-code that should be interpreted by the control server
    /// </summary>
    /// <param name="code">Code to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the code if the code completed, else null</returns>
    public ValueTask<Message> ProcessAsync(Commands.Code code, CancellationToken cancellationToken)
        => Rows.Invoke(this, code, cancellationToken);

    /// <inheritdoc />
    public CodeClass? Classify(DuetAPI.Commands.Code code) => Rows.Classify(code);

    /// <summary>
    /// Every G-code this handler implements: its class, enforced by the pipeline before dispatch,
    /// and its handler. A G-code with no row takes the macro-then-unsupported path
    /// </summary>
    internal static readonly CodeTable<GCodeHandler> Rows = new(CodeType.GCode)
    {
        // Rapid and coordinated moves. Immediate: they are the motion; a special move waits for
        // standstill inside the handler where its type is known
        { [0, 1], CodeClass.Immediate, (h, c, ct) => h.HandleMoveAsync(c, isCoordinated: c.MajorNumber == 1, ct) },
        // Set tool offsets, or retract. The offsets are part of the transform every queued move was
        // planned against, so an axis letter is a barrier; without one the code sets tool
        // temperatures, which belong at the point in the path
        { 10, c => c.Parameters.Any(p => Axis.Letters.Contains(p.Letter)) ? CodeClass.Barrier : CodeClass.Deferred, (h, c, ct) => h.HandleToolOffsetsAsync(c, ct) },
        // Set units to inches / millimetres
        { [20, 21], CodeClass.Immediate, async (h, c, ct) =>
            {
                await h.UpdateInputAsync(c, input => input.DistanceUnit = c.MajorNumber == 20 ? DistanceUnit.Inch : DistanceUnit.MM, ct);
                return new Message();
            } },
        // Home the machine
        { 28, CodeClass.Barrier, (h, c, ct) => h.HandleHomeAsync(c, ct) },
        // Probe the grid and build a height map
        { 29, CodeClass.Barrier, (h, c, ct) => h.HandleProbeGridAsync(c, ct) },
        // Probe the bed
        { 30, CodeClass.Barrier, (h, c, ct) => h.HandleProbeAsync(c, ct) },
        // Set or report the Z probe trigger height, offsets and threshold
        { 31, CodeClass.Immediate, (h, c, ct) => h.HandleProbeParametersAsync(c, ct) },
        // Save the current position to a restore point
        { 60, CodeClass.Immediate, (h, c, ct) => h.HandleSavePositionAsync(c, ct) },
        // Absolute / relative positioning
        { [90, 91], CodeClass.Immediate, async (h, c, ct) =>
            {
                await h.UpdateInputAsync(c, input => input.AxesRelative = c.MajorNumber == 91, ct);
                return new Message();
            } },
        // Set position without moving
        { 92, CodeClass.Immediate, (h, c, ct) => h.HandleSetPositionAsync(c, ct) },
        // Inverse time / feed rate mode
        { [93, 94], CodeClass.Immediate, async (h, c, ct) =>
            {
                await h.UpdateInputAsync(c, input => input.InverseTimeMode = c.MajorNumber == 93, ct);
                return new Message();
            } },
    };

    /// <summary>
    /// G60: save the current position to a restore point
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// RepRapFirmware's <c>GCodes::SavePosition</c>. S names the point and defaults to 0, so a G60
    /// with no parameters writes the first of the general-purpose points rather than the pause point
    /// </remarks>
    private async ValueTask<Message> HandleSavePositionAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int restorePointNumber = code.GetInt('S', 0);
        if (restorePointNumber < 0 || restorePointNumber >= Motion.RestorePoint.NumVisible)
        {
            return new Message(MessageType.Error, $"S parameter must be between 0 and {Motion.RestorePoint.NumVisible - 1}");
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            InputChannel? input = model.Inputs[code.Channel];
            float unitScale = input?.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
            float feedRateMmPerSec = (input?.FeedRate ?? 0.0f) * unitScale / SecondsPerMinute;

            using (planner.Lock())
            {
                planner.State.SavePosition(restorePointNumber, planner.Parameters.SharedAxisCount(model.Move),
                                           feedRateMmPerSec, model.State.CurrentTool, filePosition: null);
                planner.PublishRestorePoints();
            }
        }
        return new Message();
    }

    /// <summary>
    /// React to an executed G-code before its result is returned
    /// </summary>
    /// <param name="code">Code processed by RepRapFirmware</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result to output</returns>
    public ValueTask CodeExecutedAsync(Commands.Code code, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Read what kind of move a G0 or G1 asked for
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="moveType">Receives the kind of move</param>
    /// <param name="error">Receives why the H parameter cannot be used, if it cannot</param>
    /// <returns>True if the move can be built</returns>
    /// <remarks>
    /// The value is checked rather than cast, because every later decision branches on it and an
    /// unrecognised one would fall through those branches as though it were something else - an H7
    /// would arm no endstop and yet still bypass the user coordinate system, which is not a
    /// combination anything below here is written for. RepRapFirmware refuses the same values, in
    /// <c>gb.TryGetLimitedUIValue('H', moveType, dummy, 5)</c>, and reports it the same way
    /// </remarks>
    private static bool TryGetMoveType(Commands.Code code, out MoveType moveType, out Message? error)
    {
        int value = code.GetInt('H', 0);
        if (!Enum.IsDefined(typeof(MoveType), value))
        {
            moveType = MoveType.Normal;
            error = new Message(MessageType.Error, value < 0 ? "parameter 'H' too low" : "parameter 'H' too high");
            return false;
        }

        moveType = (MoveType)value;
        error = null;
        return true;
    }

    /// <summary>
    /// Turn a G0 or G1 into a queued move
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="isCoordinated">Whether the axes move together (G1) or independently (G0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleMoveAsync(Commands.Code code, bool isCoordinated, CancellationToken cancellationToken)
    {
        if (!TryGetMoveType(code, out MoveType moveType, out Message? typeError))
        {
            return typeError!;
        }

        // A special move is planned against the motor positions rather than the axis positions, so
        // the machine has to have settled before it is built - as in RepRapFirmware, which locks and
        // waits for standstill before reading them
        if (moveType != MoveType.Normal)
        {
            // TODO when multiple motion systems are implemented this will likely need to change to only wait for standstill on the active MS
            await planner.WaitForStandstillAsync(cancellationToken);
        }

        // What each named axis watches, worked out once. A stall-homed axis also has to have its
        // drivers told what speed to expect before the move runs, which is a CAN round trip and so
        // cannot happen with the object model lock held; nothing is sent for a move whose axes all
        // home on switches
        List<EndstopPlan> plans = [];
        EndstopArmingState armingState = new();
        Message? armReply = null;

        try
        {
            if (moveType.ChecksEndstops())
            {
                // Planned before anything is sent, so that a board refusing to arm still leaves the
                // release below knowing what to undo
                plans = await PlanEndstopsAsync(code, cancellationToken);
                armReply = await PrepareEndstopsAsync(plans, armingState, cancellationToken);
            }
            // A board that armed the driver but had something to say about it is reported alongside
            // whatever the move itself came back with, rather than being dropped for not being an
            // error. A move that never completed still returns null, which is what says so
            Message result = await SubmitMoveAsync(code, isCoordinated, moveType, plans, cancellationToken);
            return new[] { armReply, result }.ToMessage();
        }
        finally
        {
            await ReleaseEndstopsAsync(plans, armingState, CancellationToken.None);
        }
    }

    /// <summary>
    /// Build a move and get it into the queue, retrying while the queue is full
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="isCoordinated">Whether the axes move together (G1) or independently (G0)</param>
    /// <param name="moveType">What kind of move the H parameter asked for</param>
    /// <param name="plans">What each named axis watches, empty for a move that watches nothing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> SubmitMoveAsync(Commands.Code code, bool isCoordinated, MoveType moveType,
                                                     IReadOnlyList<EndstopPlan> plans,
                                                     CancellationToken cancellationToken)
    {
        RawMove? raw = null;
        SegmentedMove segments = default;
        List<int> armedAxes = [];
        int submitted = 0;
        uint purgeGeneration = 0;
        // Where a stop dropping this move would send the job back to, for a move that came from the
        // job file itself. It is created with the move and cleared by whatever ends the code: this
        // method, or the pause, which takes it
        JobMoveOrigin? origin = null;
        bool isJobCode = JobMoveOrigin.IsJobFileCode(code);

        try
        {
            // Retrying rather than failing when the ring is full is what applies back-pressure: it is the
            // normal state when moves are commanded faster than the machine can run them, and it is what
            // keeps the G-code stream in step with the machine
            while (!cancellationToken.IsCancellationRequested)
            {
                MoveSubmitResult result = MoveSubmitResult.Busy;

                using (await model.AccessReadWriteAsync(cancellationToken))
                {
                    InputChannel? input = model.Inputs[code.Channel];
                    if (input is null)
                    {
                        return new Message(MessageType.Error, $"Unknown code channel {code.Channel}");
                    }
                    if (model.Move.Axes.Count == 0)
                    {
                        return new Message(MessageType.Error, "No axes have been configured");
                    }

                    // Refused rather than planned for whichever axes both sides happen to agree on. The
                    // snapshot is only out of step with the object model when a reconfiguration did not
                    // happen or did not succeed, and a move planned from a description of a machine that
                    // no longer exists would address the wrong drives
                    if (!planner.Parameters.MatchesObjectModel(model.Move))
                    {
                        return new Message(MessageType.Error,
                                           "The motion configuration was not applied; no moves can be planned until it is");
                    }

                    // Held across building and queueing, because the move is a delta from the state the
                    // planner holds: another channel building in between would measure from the wrong
                    // place. Building also advances that state, which is what makes the rollback below
                    // necessary
                    using (planner.Lock())
                    {
                        MovementState state = planner.State;

                        if (raw is null && state.SegmentsLeft != 0)
                        {
                            // Another channel is part-way through a segmented move. Building now would
                            // measure this move from a position half way along that one and interleave
                            // the two on the ring, so this waits instead - which is what RepRapFirmware's
                            // `if (segmentsLeft != 0) return false` amounts to. It cannot be a lock held
                            // across the wait, because giving the ring up is the point
                            result = MoveSubmitResult.Busy;
                        }
                        else if (raw is null)
                        {
                            // What the build is about to spend, which the record has to keep: it is
                            // the fraction of the code that was already made before this build, and
                            // this build covers only the rest of it
                            float fractionAtStart = isJobCode ? state.MoveFractionToSkip : 0.0f;

                            // Built once, however many segments it turns into and however many times the
                            // ring is too full to take the next one. Rebuilding would apply a relative
                            // move a second time, and cannot be done at all once a segment has gone out
                            float[] positionBeforeMove = ArrayPool<float>.Shared.Rent(MotionLimits.MaxAxes);
                            try
                            {
                                state.CurrentUserPosition.CopyTo(positionBeforeMove, 0);

                                raw = moveInterpreter.BuildRawMove(code, input, isCoordinated, moveType, plans);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Could not build move for {Code}", code);
                                positionBeforeMove.AsSpan(0, MotionLimits.MaxAxes).CopyTo(state.CurrentUserPosition);
                                throw;
                            }
                            finally
                            {
                                ArrayPool<float>.Shared.Return(positionBeforeMove);
                            }

                            armedAxes = raw.ArmedAxes;
                            segments = SegmentedMove.From(raw, raw.InitialCoords,
                                                          planner.Parameters.SharedAxisCount(model.Move),
                                                          planner.Parameters.FirstExtruderDrive);

                            // Claimed here rather than as each segment goes out, so that the claim covers
                            // the windows in between - which is exactly what the claim is for
                            state.SegmentsLeft = segments.Count;

                            // Where this code came from, so that a stop can say where to resume it.
                            // One record for the whole code however many segments it becomes, because
                            // every one of them describes the same line of the file, and the file
                            // position and the fraction already made are two halves of one fact.
                            //
                            // Only a code from the job file itself - see IsJobFileCode. A code read
                            // from a macro carries an offset into the *macro*, and a resume rewinds
                            // the *job* file, so recording one would send the job to an unrelated
                            // position. With no record the pause falls back to the last completed
                            // job-file code, which is the macro invocation, so the macro re-runs
                            // whole. That is RepRapFirmware's GetJobFilePosition, which returns
                            // noFilePosition for exactly this case
                            if (isJobCode)
                            {
                                origin = new JobMoveOrigin
                                {
                                    FilePosition = code.FilePosition,
                                    CodeLength = code.Length ?? 0,
                                    GCommandNumber = code.MajorNumber ?? -1,
                                    FeedRateMmPerSec = raw.OriginalFeedRateMmPerSec,
                                    FractionAtStart = fractionAtStart,
                                    SegmentCount = segments.Count
                                };
                                state.CurrentJobMove = origin;
                            }

                            // What the ring had been through when this move was measured. A stop
                            // that empties the ring in one of the windows below invalidates the rest
                            // of this move, and this is what the loop notices it by
                            purgeGeneration = state.PurgeGeneration;
                        }

                        // As many segments as the engine will take. Stopping when it is full and picking
                        // up from the same place is what keeps a long segmented move from blocking
                        while (raw is not null && submitted < segments.Count)
                        {
                            if (state.PurgeGeneration != purgeGeneration ||
                                (origin is not null && !ReferenceEquals(state.CurrentJobMove, origin)))
                            {
                                // Either a stop emptied the ring while this move was part-way out, or
                                // a pause took the record of this code. The segments still in hand
                                // describe a path the machine has been told not to travel, and
                                // queueing them now would start it moving again after it had come to
                                // rest - or, where the queue is draining rather than emptied, would
                                // move the boundary the pause has already recorded.
                                //
                                // Cancelled rather than finished, because that is what it is: the
                                // code did not do what it was asked to. DoFilePrint advances its own
                                // idea of the file position by every code that completes, and that
                                // position is the one a stop with nothing to say falls back to
                                logger.LogInformation(
                                    "Abandoning {Count} remaining segment(s) of {Code} because the machine stopped",
                                    segments.Count - submitted, code);
                                throw new OperationCanceledException($"{code} was interrupted by a stop");
                            }

                            moveInterpreter.PrepareSegment(raw, segments, submitted + 1);

                            result = planner.QueueMove(raw);
                            if (result is MoveSubmitResult.Busy or MoveSubmitResult.Rejected)
                            {
                                break;
                            }

                            // The id the move went out under, which a stop report quotes back. It is
                            // assigned as the move is queued, so it cannot be known when the move was
                            // armed - and this is inside the planner lock, which is the lock a report
                            // takes, so no report can find the move armed but unnamed
                            if (submitted == 0 && plans.Count > 0)
                            {
                                endstopCorrection.NoteMoveId(raw.MoveId);
                            }

                            // Which code this move belongs to, and where in that code it comes. A
                            // stop that drops it therefore knows both where in the file to resume and
                            // how much of that line is already behind the machine, and both come from
                            // the one record
                            if (origin is not null)
                            {
                                planner.JobMoves.Note(raw.MoveId, origin, submitted);
                            }
                            submitted++;
                            state.SegmentsLeft = segments.Count - submitted;
                            if (origin is not null)
                            {
                                origin.SegmentsQueued = submitted;
                            }
                        }
                    }
                }

                if (result == MoveSubmitResult.Rejected)
                {
                    logger.LogError("Rejected {Code}", code);
                    return new Message(MessageType.Error, "Move could not be planned; see the log for details");
                }

                // `raw` being null means the move has not been built at all - another channel was
                // part-way through one - so an empty segment list is "not started", not "finished"
                if (raw is not null && submitted >= segments.Count)
                {
                    if (moveType != MoveType.Normal)
                    {
                        // A special move is where the machine finds out where it is, so the code has to
                        // wait for it rather than queue it and move on. Every ordinary move is committed
                        // at its planned endpoint and the next code interpreted straight away, which is
                        // what keeps the queue full
                        await FinishSpecialMoveAsync(moveType, armedAxes, cancellationToken);
                    }
                    return new Message();
                }

                await Task.Delay(RingFullRetryDelay, cancellationToken);
            }

            return new Message();
        }
        finally
        {
            if (raw is not null)
            {
                // However this ended - submitted, rejected, thrown or cancelled - the move is no
                // longer part-way through, and a channel waiting on it must not be left waiting.
                // The object model read lock because putting the interpreter's position back reads
                // the transform out of it; it is not published here, which the pause does once the
                // machine has settled
                using (await model.AccessReadOnlyAsync(CancellationToken.None))
                using (planner.Lock())
                {
                    MovementState state = planner.State;
                    state.SegmentsLeft = 0;

                    // The record belongs to this submission until something else takes it. A pause
                    // that did take it has put another value there, or none, and must not have this
                    // one written over the top of it
                    if (origin is not null && ReferenceEquals(state.CurrentJobMove, origin))
                    {
                        state.CurrentJobMove = null;
                    }

                    if (submitted < segments.Count)
                    {
                        // The interpreter accounted for the whole move when it built it, so a
                        // submission that ended early leaves it describing segments the machine will
                        // never make - and the next move built anywhere would start from them
                        moveInterpreter.SyncInterpreterToMachine();
                    }
                }
            }
        }
    }


    /// <summary>
    /// G92: redefine the current position without moving
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> HandleSetPositionAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // TODO validate this against RRF
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            InputChannel? input = model.Inputs[code.Channel];
            float unitScale = input?.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;

            using (planner.Lock())
            {
                MovementState state = planner.State;
                int numAxes = planner.Parameters.SharedAxisCount(model.Move);
                List<int> axesIncluded = [];

                for (int axis = 0; axis < numAxes; axis++)
                {
                    Axis axisConfig = model.Move.Axes[axis];
                    if (!code.TryGetFloat(axisConfig.Letter, out float value))
                    {
                        continue;
                    }

                    // RepRapFirmware assigns the raw value rather than adding the workplace offset,
                    // so G92 names a machine coordinate and the reported user position moves by the
                    // offset. Keeping that convention is what makes G92 and G1 agree about where the
                    // machine is
                    state.CurrentUserPosition[axis] = value * unitScale;
                    axesIncluded.Add(axis);
                }

                if (axesIncluded.Count > 0)
                {
                    // The planner keeps its own machine position, and this changes what that
                    // position is called without moving anything
                    // TODO apply tool offsets?
                    foreach (int axis in axesIncluded)
                    {
                        planner.Builder.SetAxisPosition(axis, state.CurrentUserPosition[axis]);
                    }

                    // The engine holds its own idea of where the motors are and plans the next move
                    // as the difference from it, so a position redefined only here would be undone by
                    // the next move. RepRapFirmware pushes both together in
                    // MovementState::SetNewPositionOfOwnedAxes
                    planner.PushPositionsToEngine();
                    planner.PublishCommittedPosition();
                }

                if (code.TryGetFloat('E', out float extruderPosition))
                {
                    foreach (Extruder extruder in model.Move.Extruders)
                    {
                        extruder.RawPosition = extruderPosition * unitScale;
                    }
                }
            }
        }
        return new Message();
    }

    /// <summary>
    /// Apply a change to the channel's interpreter state
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="update">What to change</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async ValueTask UpdateInputAsync(Commands.Code code, Action<InputChannel> update, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            InputChannel? input = model.Inputs[code.Channel];
            if (input is not null)
            {
                update(input);
            }
        }
    }


    /// <summary>
    /// Redefine where the machine is, from outside the interpreter
    /// </summary>
    /// <param name="axis">Axis to redefine</param>
    /// <param name="machinePosition">Its machine position in mm</param>
    /// <remarks>
    /// For homing and probing, where the machine turns out to be somewhere other than the interpreter
    /// commanded it to. This is the one direction the inverse transform is for: the position is known
    /// in machine coordinates and the interpreter's own position has to be brought back into step with
    /// it. The caller must hold the object model write lock and the planner lock
    /// </remarks>
    private void RedefineMachinePosition(int axis, float machinePosition)
    {
        planner.Builder.SetAxisPosition(axis, machinePosition);

        // The engine measures the next move from the position it holds, so redefining one here and
        // not there would have the machine travel the difference
        planner.PushPositionsToEngine();
        SyncInterpreterToMachine();

        // Nothing was queued, so nothing else will say where the machine ended up: what a client
        // reads would still be the coordinate the probing move was sent to
        planner.PublishCommittedPosition();
    }

    /// <summary>
    /// Bring the interpreter's position back into step with where the machine actually is, and say
    /// where that is
    /// </summary>
    /// <remarks>
    /// The inversion itself is <see cref="MoveInterpreter.SyncInterpreterToMachine"/>, which a
    /// feedhold needs as well; this is that plus telling a client about it. The caller must hold the
    /// object model write lock and the planner lock
    /// </remarks>
    private void SyncInterpreterToMachine()
    {
        moveInterpreter.SyncInterpreterToMachine();
        planner.PublishCommittedPosition();
    }
}
