using System;
using System.Buffers;
using System.Collections.Generic;
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
    public async ValueTask<Message> ProcessAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        Message rslt = new();
        switch (code.MajorNumber)
        {
            // Rapid and coordinated moves
            case 0:
            case 1:
                rslt = await HandleMoveAsync(code, isCoordinated: code.MajorNumber == 1, cancellationToken);
                break;

            // Set tool offsets, or retract
            case 10:
                rslt = await HandleToolOffsetsAsync(code, cancellationToken);
                break;

            // Set units to inches / millimetres
            case 20:
            case 21:
                await UpdateInputAsync(code, input => input.DistanceUnit = code.MajorNumber == 20 ? DistanceUnit.Inch : DistanceUnit.MM, cancellationToken);
                break;

            // Absolute / relative positioning.
            case 90:
            case 91:
                await UpdateInputAsync(code, input =>
                {
                    input.AxesRelative = code.MajorNumber == 91;
                }, cancellationToken);
                break;

            // Home the machine
            case 28:
                rslt = await HandleHomeAsync(code, cancellationToken);
                break;

            // Probe the grid and build a height map
            case 29:
                rslt = await HandleProbeGridAsync(code, cancellationToken);
                break;

            // Probe the bed
            case 30:
                rslt = await HandleProbeAsync(code, cancellationToken);
                break;

            // Set or report the Z probe trigger height, offsets and threshold
            case 31:
                rslt = await HandleProbeParametersAsync(code, cancellationToken);
                break;

            // Save the current position to a restore point
            case 60:
                rslt = await HandleSavePositionAsync(code, cancellationToken);
                break;

            // Set position without moving
            case 92:
                rslt = await HandleSetPositionAsync(code, cancellationToken);
                break;

            // Inverse time / feed rate mode
            case 93:
            case 94:
                await UpdateInputAsync(code, input => input.InverseTimeMode = code.MajorNumber == 93, cancellationToken);
                break;

            default:
                throw new NotSupportedException($"Unsupported code '{code}'");
        }
        return rslt;
    }

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
                        }

                        // As many segments as the engine will take. Stopping when it is full and picking
                        // up from the same place is what keeps a long segmented move from blocking
                        while (raw is not null && submitted < segments.Count)
                        {
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

                            // Where this move came from, so that a feedhold dropping it can say where
                            // to resume. Every segment of a move carries the same file position,
                            // because they all came from the one code
                            if (code.IsFromFileChannel)
                            {
                                planner.JobMoves.Note(raw.MoveId, new JobMoveOrigin
                                {
                                    FilePosition = code.FilePosition,
                                    GCommandNumber = code.MajorNumber ?? -1,
                                    FeedRateMmPerSec = raw.FeedRateMmPerSec
                                });
                            }
                            submitted++;
                            state.SegmentsLeft = segments.Count - submitted;
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
                // longer part-way through, and a channel waiting on it must not be left waiting
                using (planner.Lock())
                {
                    planner.State.SegmentsLeft = 0;
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
    /// Bring the interpreter's position back into step with where the machine actually is
    /// </summary>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>ToolOffsetInverseTransform</c> after a homing or probing move, and the
    /// only place the transform is inverted. Everywhere else the interpreter is authoritative and the
    /// machine follows it; here the machine is somewhere the interpreter did not put it. The caller
    /// must hold the object model write lock and the planner lock.
    /// </para>
    /// <para>
    /// The bed transform is undone first, as RepRapFirmware's <c>InverseBedTransform</c> is before
    /// <c>ToolOffsetInverseTransform</c>. The builder's position is where the machine was
    /// <em>commanded</em>, correction included, so leaving the correction in would hand the
    /// interpreter a Z that is already compensated - and it would then be compensated a second time
    /// on the next move
    /// </para>
    /// </remarks>
    private void SyncInterpreterToMachine()
    {
        MovementState state = planner.State;
        int numAxes = planner.Parameters.SharedAxisCount(model.Move);
        ReadOnlySpan<float> machinePosition = planner.Builder.StartCoordinates;

        machinePosition[..numAxes].CopyTo(state.CurrentUserPosition);

        // The bed transform first and the axis transform second, which is the order RepRapFirmware's
        // InverseAxisAndBedTransform uses - the mirror of applying the axis transform before the bed
        // one, because the map is indexed by coordinates the skew has already moved
        bedCompensation.Remove(state.CurrentUserPosition, numAxes);
        AxisSkew.Remove(toolManager.Current, model.Move, state.CurrentUserPosition, numAxes);
        ToolTransform.Remove(toolManager.Current, model.Move, state.CurrentUserPosition, numAxes);
        planner.PublishCommittedPosition();
    }
}
