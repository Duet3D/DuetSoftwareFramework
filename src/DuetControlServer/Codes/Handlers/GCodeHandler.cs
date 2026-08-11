using System;
using System.Buffers;
using System.Collections.Generic;
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
/// <param name="logger">Logger</param>
internal sealed partial class GCodeHandler(
    Model.ObjectModel model,
    MovePlanner planner,
    BedCompensation bedCompensation,
    Files.MacroRunner macroRunner,
    Link.LinkInterface linkInterface,
    EndstopCorrection endstopCorrection,
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

        // A stall-homed axis has to have its drivers told what speed to expect before the move runs,
        // which is a CAN round trip and so cannot happen with the object model lock held. Nothing is
        // sent for a move whose axes all home on switches
        HashSet<byte> armedBoards = [];
        Message? armReply = null;

        try
        {
            if (moveType.ChecksEndstops())
            {
                (armedBoards, armReply) = await ArmStallEndstopsAsync(code, cancellationToken);
            }
            // A board that armed the driver but had something to say about it is reported alongside
            // whatever the move itself came back with, rather than being dropped for not being an
            // error. A move that never completed still returns null, which is what says so
            Message result = await SubmitMoveAsync(code, isCoordinated, moveType, cancellationToken);
            return new[] { armReply, result }.ToMessage();
        }
        finally
        {
            // However the move ended. A driver left armed would report a stall during an ordinary
            // move, and the next move naming the stall handle would stop on it
            if (armedBoards.Count > 0)
            {
                await DisarmStallEndstopsAsync(armedBoards, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Build a move and get it into the queue, retrying while the queue is full
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="isCoordinated">Whether the axes move together (G1) or independently (G0)</param>
    /// <param name="moveType">What kind of move the H parameter asked for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> SubmitMoveAsync(Commands.Code code, bool isCoordinated, MoveType moveType,
                                                     CancellationToken cancellationToken)
    {
        RawMove? raw = null;
        SegmentedMove segments = default;
        List<int> armedAxes = [];
        int submitted = 0;

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

                    if (raw is null)
                    {
                        // Built once, however many segments it turns into and however many times the
                        // ring is too full to take the next one. Rebuilding would apply a relative
                        // move a second time, and cannot be done at all once a segment has gone out
                        float[] positionBeforeMove = ArrayPool<float>.Shared.Rent(MotionLimits.MaxAxes);
                        try
                        {
                            state.CurrentUserPosition.CopyTo(positionBeforeMove, 0);

                            raw = BuildRawMove(code, input, isCoordinated, moveType);
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
                        segments = SegmentedMove.From(raw, planner.Builder.StartCoordinates,
                                                      planner.Parameters.SharedAxisCount(model.Move),
                                                      planner.Parameters.FirstExtruderDrive);
                    }

                    // As many segments as the engine will take. Stopping when it is full and picking
                    // up from the same place is what keeps a long segmented move from blocking
                    while (submitted < segments.Count)
                    {
                        PrepareSegment(raw, segments, submitted + 1);

                        result = planner.QueueMove(raw);
                        if (result is MoveSubmitResult.Busy or MoveSubmitResult.Rejected)
                        {
                            break;
                        }
                        submitted++;
                    }
                }
            }

            if (result == MoveSubmitResult.Rejected)
            {
                logger.LogError("Rejected {Code}", code);
                return new Message(MessageType.Error, "Move could not be planned; see the log for details");
            }

            if (submitted >= segments.Count)
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

    /// <summary>
    /// What a move is broken into, and what each piece has to be worked out from
    /// </summary>
    /// <remarks>
    /// The move's own coordinates are overwritten segment by segment as it is submitted, so where it
    /// started and where it is going have to be kept somewhere else
    /// </remarks>
    private readonly struct SegmentedMove
    {
        /// <summary>How many pieces the move is in</summary>
        public int Count { get; private init; }

        /// <summary>Number of axes the move touches</summary>
        public int NumAxes { get; private init; }

        /// <summary>Where the move began, in machine coordinates</summary>
        public float[] Start { get; private init; }

        /// <summary>Where it ends, in machine coordinates</summary>
        public float[] Target { get; private init; }

        /// <summary>Extrusion for one segment, by logical drive</summary>
        public float[] ExtrusionPerSegment { get; private init; }

        /// <summary>First logical drive that is an extruder</summary>
        public int FirstExtruderDrive { get; private init; }

        /// <summary>
        /// Take a built move apart into what its segments need
        /// </summary>
        /// <param name="raw">The move</param>
        /// <param name="start">Where the machine is, which is where the move begins</param>
        /// <param name="numAxes">Number of axes to consider</param>
        /// <param name="firstExtruderDrive">First logical drive that is an extruder</param>
        /// <returns>The pieces</returns>
        public static SegmentedMove From(RawMove raw, ReadOnlySpan<float> start, int numAxes, int firstExtruderDrive)
        {
            SegmentedMove segmented = new()
            {
                Count = Math.Max(1, raw.SegmentCount),
                NumAxes = numAxes,
                FirstExtruderDrive = firstExtruderDrive,
                Start = new float[MotionLimits.MaxAxes],
                Target = new float[MotionLimits.MaxAxes],
                ExtrusionPerSegment = new float[MotionLimits.MaxAxesPlusExtruders]
            };

            start[..numAxes].CopyTo(segmented.Start);
            raw.Coords.AsSpan(0, numAxes).CopyTo(segmented.Target);

            // Divided rather than repeated: the extrusion belongs to the whole move, so each segment
            // gets its share. RepRapFirmware does the same in FinaliseMove
            for (int drive = firstExtruderDrive; drive < MotionLimits.MaxAxesPlusExtruders; drive++)
            {
                segmented.ExtrusionPerSegment[drive] = raw.Coords[drive] / segmented.Count;
            }
            return segmented;
        }
    }

    /// <summary>
    /// Point a move at the end of one of its segments
    /// </summary>
    /// <param name="raw">The move, whose coordinates are overwritten</param>
    /// <param name="segments">What the move was broken into</param>
    /// <param name="segment">Which segment to prepare, counting from one</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>GCodes::ReadMove</c>. Each segment ends a fraction of the way along the
    /// line, and the last one ends exactly at the target rather than at the sum of the fractions -
    /// otherwise a long move would accumulate rounding and stop short of where it was asked to go.
    /// </para>
    /// <para>
    /// The bed compensation is applied here rather than to the move as a whole, which is the point of
    /// the mesh segment count: the correction depends on where the nozzle is, so following the bed
    /// means sampling it along the way. Applied once at the end it is a chord across the bed's shape
    /// </para>
    /// </remarks>
    private void PrepareSegment(RawMove raw, in SegmentedMove segments, int segment)
    {
        if (segment >= segments.Count)
        {
            segments.Target.AsSpan(0, segments.NumAxes).CopyTo(raw.Coords);
        }
        else
        {
            float fraction = (float)segment / segments.Count;
            for (int axis = 0; axis < segments.NumAxes; axis++)
            {
                raw.Coords[axis] = segments.Start[axis]
                    + ((segments.Target[axis] - segments.Start[axis]) * fraction);
                // CHECK RRF updates the MovementState.initialCoords (ie the start of the next segment). This is probably for pausing mid-segment because of the way RRF has multiple threads for the motion pipeline. Might not be necessary here
            }
        }

        // Limit the end position at each segment. This is needed for arc moves on any printer, and for [segmented] straight moves on SCARA printers.
        // TODO check the segment end position to see if it is valid for the current kinematics

        for (int drive = segments.FirstExtruderDrive; drive < MotionLimits.MaxAxesPlusExtruders; drive++)
        {
            raw.Coords[drive] = segments.ExtrusionPerSegment[drive];
        }

        if (raw.MoveType == MoveType.Normal)
        {
            AxisAndBedTransform(raw, segments.NumAxes);
        }

        // Each segment is its own move to the engine, so it needs its own correlation id
        raw.MoveId = 0;
    }

    /// <summary>
    /// Read a movement code's parameters into a move
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="input">The channel's interpreter state</param>
    /// <param name="isCoordinated">Whether this is a G1</param>
    /// <param name="moveType">What kind of move the H parameter asked for</param>
    /// <param name="error">Receives why the move cannot be built, if it cannot</param>
    /// <returns>The move</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    private RawMove BuildRawMove(Commands.Code code, InputChannel input, bool isCoordinated, MoveType moveType)
    {
        MotionParameters parameters = planner.Parameters;
        int numAxes = parameters.SharedAxisCount(model.Move);
        float unitScale = input.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
        MovementState state = planner.State;

        RawMove raw = new()
        {
            IsCoordinated = isCoordinated,
            InverseTimeMode = input.InverseTimeMode,
            XAxes = AxisBitmap(model.Move, 'X'),
            YAxes = AxisBitmap(model.Move, 'Y'),

            // H selected what kind of move this is. H1, H3 and H4 stop on the endstops - that is
            // homing, measuring an axis' length, and probing - and H2 is an individual motor move
            // that ignores them
            MoveType = moveType,
            CheckEndstops = moveType.ChecksEndstops()
        };

        // G53 asks for machine coordinates on this line only, so neither the workplace offset nor
        // (once tools exist) the tool offset applies to it
        bool machineCoordinates = code.Flags.HasFlag(CodeFlags.EnforceAbsolutePosition);
        bool runningSystemMacro = code.Flags.HasFlag(CodeFlags.IsFromSystemMacro);
        uint axesMentioned = AxesMentioned(code, numAxes);

        if (moveType == MoveType.Normal)
        {
            for (int axis = 0; axis < numAxes; axis++)
            {
                Axis axisConfig = model.Move.Axes[axis];
                if (!code.TryGetFloat(axisConfig.Letter, out float value))
                {
                    continue;
                }

                float moveArg = axisConfig.Rotational ? value : value * unitScale;

                // The interpreter's own position is what a move is measured from and written back
                // to. It runs ahead of the machine by however many moves are still queued, which is
                // exactly why it cannot be read back out of the object model's reported positions
                if (input.AxesRelative)
                {
                    state.CurrentUserPosition[axis] += moveArg;
                }
                else if (machineCoordinates)
                {
                    // g53 ignores tool offsets as well as workplace coordinates
                    // TODO add the current tool offset / axisScaleFactor
                    state.CurrentUserPosition[axis] = moveArg;
                }
                else if (runningSystemMacro)
                {
                    // don't apply workplace offsets to commands in system macros
                    state.CurrentUserPosition[axis] = moveArg;
                }
                else
                {
                    state.CurrentUserPosition[axis] = moveArg + WorkplaceOffset(axisConfig, WorkplaceNumber);
                }

                if (axisConfig.Rotational)
                {
                    raw.RotationalAxesMentioned = true;
                }
                else
                {
                    raw.LinearAxesMentioned = true;
                }
            }

            // An axis the machine does not know the position of cannot be moved to a coordinate,
            // because there is no coordinate system to move it in. M564 decides whether that is
            // refused outright, and the geometry widens the set where its axes are coupled
            // This might throw a GCodeException
            // TODO use tool axis mapping to get the actual axes to move
            CheckEnoughAxesHomed(axesMentioned, numAxes); // TODO if doingManualBedProbe then skip this check
        }
        else
        {
            // A special move bypasses the user coordinate system entirely: no workplace offset, no
            // babystepping and no bed compensation, and the interpreter's position is left alone
            // because a motor position is not an axis position. RepRapFirmware does the same, which
            // is why it never writes currentUserPosition on this path
            SeedSpecialMoveCoordinates(raw, numAxes);

            for (int axis = 0; axis < numAxes; axis++)
            {
                Axis axisConfig = model.Move.Axes[axis];
                if (!code.TryGetFloat(axisConfig.Letter, out float value))
                {
                    continue;
                }

                if (!input.AxesRelative && parameters.Geometry is LinearDeltaKinematicsEngine)
                {
                    // A delta's motor positions are carriage heights, and where a carriage has to be
                    // for the head to reach a point depends on the other two. So there is no absolute
                    // position to give one motor, only an amount to move it by
                    throw new GCodeException("Attempt to move individual motors of a delta machine to absolute positions");
                }

                float moveArg = axisConfig.Rotational ? value : value * unitScale;
                if (input.AxesRelative)
                {
                    raw.Coords[axis] += moveArg;
                }
                else
                {
                    raw.Coords[axis] = moveArg;
                }

                if (axisConfig.Rotational)
                {
                    raw.RotationalAxesMentioned = true;
                }
                else
                {
                    raw.LinearAxesMentioned = true;
                }
            }
        }

        // Pausing during an endstop move is not safe: it may stop short, so where it would resume
        // from is not known until it has finished
        raw.CanPauseAfter = !raw.CheckEndstops;

        // Before the endstops, because arming a stall endstop needs the speeds this works out
        LoadFeedRate(code, input, raw); // Can throw GCodeException

        if (raw.CheckEndstops)
        {
            // TODO support extruder homing. This check should use `axesMentioned != 0 && code.HasParameter('E')` but `ApplyEndstops()` doesn't support extruders currently
            if (code.HasParameter('E'))
            {
                // The extruder speeds an extruder endstop is validated against are worked out from
                // the move's total extrusion, which an axis moving at the same time invalidates.
                // RepRapFirmware refuses the combination rather than arming both badly
                throw new GCodeException("Cannot enable both axis and extruder endstops in the same move");
            }

            // TODO calculate speeds for stall detect homing

            ApplyEndstops(code, raw, numAxes); // can throw GCodeException
        }

        bool hasExtrusion = ApplyExtrusion(code, input, raw, unitScale);
        if (hasExtrusion || axesMentioned != 0)
        {
            // TODO check if first move since skipping an object

            if (raw.HasPositiveExtrusion && axesMentioned != 0)
            {
                // TODO update the object coordinates list
            }

            if (moveType != MoveType.Normal)
            {
                // It is a raw motor move, so do it in a single semgnet and wait for it to complete
                // TODO set the total segments to 1
            }
            else if (axesMentioned == 0)
            {
                // it is an extruder only move
                // TODO set the total segments to 1
            }
            else
            {
                // TODO support coordinate rotation
                // TODO apply tool offset, baby stepping, z hop, and axis scaling
                // TODO supoort keepout zones, keep if move enters keepout zone
                // TODO collision checker for multiple motion systems

                // Only limit the positions of axes that have been mentioned explicitly.
                // This avoids at least two problems:
                // 1. When supporting multiple motion systems, if a M208 axis limit was changed and an axis coordinate was outside that limit,
                //    but we don't own the axis, then if we move that axis there will be a problem when SaveOwnAxisCoordinates is called
                //    because the new coordinate won't be saved.
                // 2. If a linear axis is being limited, but the move is for a rotational axis that is already in the correct position,
                //    then the code in DDA::InitStandardMove will throw it away because neither linearAxesMoving nor rotationalAxesMoving will be set.
                //    This was an actual problem on my tool changer.
                // After the extrusion, because whether the move prints decides what may be done about a
                // target that cannot be reached in a straight line, and before the bed compensation,
                // because the height map is a correction to a position the machine can already reach
                // TODO Update the above comment with DSF specific details (instead of current RRF details) when the code is written.
                LimitPosition(raw, state, input.AxesRelative, axesMentioned, raw.HasPositiveExtrusion, numAxes); // can throw GCodeException

                // Flag whether we should use pressure advance, if there is any extrusion in this move.
                // We assume it is a normal printing move needing pressure advance if there is forward extrusion and XYU... movement (we don't count Z).
                // The movement code will only apply pressure advance if there is forward extrusion, so we only need to check for XYU... movement here.
                // TODO with multi axis machines, the Z axis exclusion may be harmful. Some print tests are likely needed to see if this is the case
                if (raw.HasPositiveExtrusion)
                {
                    raw.UsePressureAdvance = MentionsAxisOtherThanZ(code, numAxes);
                }

                // The bed compensation is deliberately not applied here. It is a correction that depends
                // on where the nozzle is, so it belongs to each segment rather than to the move
                raw.SegmentCount = SegmentCountFor(raw, numAxes);
            }

            // TODO `FinaliseMove()` in RRF does the following things:
            // - adjust the move parameters to account for segmentation and/or part of the move having been done already
            // - set `canPauseAfter`
            // - set file position
            // - change the extrusion to extrusion per segment - done in `SegmentedMove::From()` after this function returns
            // - use `moveFractionToSkip` to skip some of the move if it has already been done (e.g. after a pause)
        }

        return raw;
    }

    /// <summary>
    /// Work out how fast the move should go
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="input">The channel's interpreter state</param>
    /// <param name="raw">The move being built, with its move type and mentioned axes already set</param>
    /// <returns>An error if the feed rate cannot be determined, else null</returns>
    /// <remarks>
    /// Ported from <c>GCodes::LoadFeedrateFromGCode</c>. F persists across codes, so the value the
    /// user typed is kept on the channel - unconverted, because whether inches apply depends on the
    /// axes of the move it is eventually used for, which is not known when it is read
    /// </remarks>
    private void LoadFeedRate(Commands.Code code, InputChannel input, RawMove raw)
    {
        // TODO handle G0 moves in CNC mode
        // The overrides belong to the print, so they apply to an ordinary move that names an axis and
        // to nothing else
        raw.ApplyM220M221 = raw.MoveType == MoveType.Normal
            && (raw.LinearAxesMentioned || raw.RotationalAxesMentioned)
            && !code.Flags.HasFlag(CodeFlags.IsFromSystemMacro);
        raw.UsingStandardFeedrate = true;

        if (input.InverseTimeMode)
        {
            // G93: F is one over the time the move should take, in minutes, so it is a duration and
            // not a speed. It cannot carry over from a previous move because it describes this move's
            // length, and it is not a distance, so the inch scale does not apply to it
            if (!code.TryGetFloat('F', out float inverseTime) || inverseTime <= 0.0f)
            {
                throw new GCodeException(
                    "Feed rate must be specified with every move when using inverse time mode");
            }

            // A duration, so the speed factor divides it rather than multiplying: M220 S200 should
            // make the move take half as long, not twice as long
            float duration = SecondsPerMinute / inverseTime;
            raw.DurationSec = raw.ApplyM220M221 ? duration / model.Move.SpeedFactor : duration;
            return;
        }

        if (code.TryGetFloat('F', out float feedRate))
        {
            // Kept raw, which is also what inputs[].feedRate reports
            input.FeedRate = feedRate;
        }

        // A move that names only rotational axes is measured in degrees, so G20 does not scale its
        // feed rate even though the same F would be inches per minute for a linear move
        bool convertInches = raw.LinearAxesMentioned || !raw.RotationalAxesMentioned;
        float unitScale = convertInches && input.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
        float converted = input.FeedRate * unitScale / SecondsPerMinute;

        raw.FeedRateMmPerSec = raw.ApplyM220M221 ? converted * model.Move.SpeedFactor : converted;
        return;
    }

    /// <summary>
    /// Whether the code moves anything other than Z
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>True if some axis other than Z was named</returns>
    private bool MentionsAxisOtherThanZ(Commands.Code code, int numAxes)
    {
        for (int axis = 0; axis < numAxes; axis++)
        {
            Axis axisConfig = model.Move.Axes[axis];
            if (char.ToUpperInvariant(axisConfig.Letter) != 'Z' && code.HasParameter(axisConfig.Letter))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// How many pieces this move has to be broken into
    /// </summary>
    /// <param name="raw">The move, with its target already limited</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>The segment count, at least one</returns>
    /// <remarks>
    /// <para>
    /// Ported from the segmentation block of <c>GCodes::DoStraightMove</c>. Three separate reasons a
    /// move may need splitting, and the answer is the largest of them, because each is a lower bound
    /// on what makes the move come out right:
    /// </para>
    /// <list type="bullet">
    /// <item>the geometry bows a straight line, so it has to be approximated by short ones;</item>
    /// <item>the height map has to be followed across the bed rather than applied at the ends;</item>
    /// <item>the move takes so long that the step clock would wrap during it.</item>
    /// </list>
    /// <para>
    /// RepRapFirmware makes the first of these optional and skips it while simulating. Here it is not
    /// optional: there is no local step generation to fall back on, so a move that is not segmented is
    /// simply executed as the wrong shape
    /// </para>
    /// </remarks>
    private int SegmentCountFor(RawMove raw, int numAxes)
    {
        KinematicsEngine geometry = planner.Parameters.Geometry;
        ReadOnlySpan<float> start = planner.Builder.StartCoordinates;

        // How far the move goes, counting the axes this geometry's error depends on
        float lengthSquared = 0.0f;
        for (int axis = 0; axis < numAxes && axis < 3; axis++)
        {
            bool counts = axis < 2 || geometry.Segmentation.HasFlag(SegmentationType.IncludeZ);
            if (counts)
            {
                float delta = raw.Coords[axis] - start[axis];
                lengthSquared += delta * delta;
            }
        }
        float length = MathF.Sqrt(lengthSquared);

        // TODO if machine type is a laser then we must use one segment per pixel

        int segments = 1;
        if (geometry.Segmentation.HasFlag(SegmentationType.Segment)
            && (raw.HasPositiveExtrusion || raw.IsCoordinated || geometry.Segmentation.HasFlag(SegmentationType.IncludeG0)))
        {
            // Short enough that the bow is below a step, but not so short that the move is chopped
            // into more pieces than the error justifies
            float speed = raw.InverseTimeMode
                ? (raw.DurationSec > 0.0f ? length / raw.DurationSec : 0.0f)
                : raw.FeedRateMmPerSec;
            float seconds = speed > 0.0f ? length / speed : 0.0f;

            float byLength = geometry.MinSegmentLength > 0.0f ? length / geometry.MinSegmentLength : 0.0f;
            float byTime = seconds * geometry.SegmentsPerSecond;
            segments = Math.Max(1, (int)MathF.Round(MathF.Min(byLength, byTime)));
        }

        if (IsUsingMeshCompensation(raw, numAxes))
        {
            (float axis0, float axis1) = GridCoordinates(raw, numAxes);
            (float startAxis0, float startAxis1) = GridCoordinatesOf(start, numAxes);
            segments = Math.Max(segments, MeshSegments(axis0 - startAxis0, axis1 - startAxis1));
        }

        // The step clock wraps roughly every 45 minutes, so a move that would take a large fraction
        // of that has to be split whatever the geometry says
        {
            float speed = raw.InverseTimeMode ? 0.0f : raw.FeedRateMmPerSec;
            float seconds = raw.InverseTimeMode
                ? raw.DurationSec
                : (speed > 0.0f ? length / speed : 0.0f);
            segments = Math.Max(segments, (int)(seconds / MaxSegmentSeconds));
        }

        return Math.Max(1, segments);
    }

    /// <summary>
    /// Longest a single segment may take, seconds
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>MaxSegmentTime</c>. The step clock is 32 bits at 750kHz, so it wraps in
    /// under an hour; a move that occupies a large part of that cannot be timed against it
    /// </remarks>
    private const float MaxSegmentSeconds = 5.0f * 60.0f;

    /// <summary>
    /// Axes the code names, as a bitmap
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>The bitmap</returns>
    private uint AxesMentioned(Commands.Code code, int numAxes)
    {
        uint mentioned = 0;
        for (int axis = 0; axis < numAxes && axis < 32; axis++)
        {
            if (code.HasParameter(model.Move.Axes[axis].Letter))
            {
                mentioned |= 1u << axis;
            }
        }
        return mentioned;
    }

    /// <summary>
    /// Refuse a move that would touch an axis whose position is not known
    /// </summary>
    /// <param name="axesMentioned">Axes the code names, as a bitmap</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>An error if the move must not run, else null</returns>
    /// <remarks>
    /// <para>
    /// Ported from <c>GCodes::CheckEnoughAxesHomed</c>. M564 S0 allows moving an unhomed axis, which
    /// is what makes a homing macro's own moves possible; the geometry gets to add to the set anyway,
    /// because on a delta or a SCARA the axes are not independently positioned and a coordinate in one
    /// of them means nothing until all of them are known.
    /// </para>
    /// <para>
    /// An extruder-only move is deliberately allowed either way - it names no axis, so there is
    /// nothing to be unsure of
    /// </para>
    /// </remarks>
    private void CheckEnoughAxesHomed(uint axesMentioned, int numAxes)
    {
        uint mustBeHomed = planner.Parameters.Geometry.MustBeHomedAxes(axesMentioned, model.Move.NoMovesBeforeHoming);

        uint unhomed = 0;
        for (int axis = 0; axis < numAxes && axis < 32; axis++)
        {
            if ((mustBeHomed & (1u << axis)) != 0 && !model.Move.Axes[axis].Homed)
            {
                unhomed |= 1u << axis;
            }
        }

        if (unhomed == 0)
        {
            return;
        }

        StringBuilder letters = new();
        for (int axis = 0; axis < numAxes && axis < 32; axis++)
        {
            if ((unhomed & (1u << axis)) != 0)
            {
                letters.Append(model.Move.Axes[axis].Letter);
            }
        }
        throw new GCodeException($"Insufficient axes homed ({letters})");
    }

    /// <summary>
    /// Bring a move within what the machine can reach, or refuse it
    /// </summary>
    /// <param name="raw">The move being built, whose coordinates may be adjusted</param>
    /// <param name="state">Interpreter state, brought back into step if the target was adjusted</param>
    /// <param name="axesRelative">Whether the move was commanded relative to where the machine is</param>
    /// <param name="axesMentioned">Axes the code names, as a bitmap</param>
    /// <param name="hasForwardExtrusion">Whether the move extrudes, so its path is being printed</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>An error if the move cannot be made possible, else null</returns>
    /// <remarks>
    /// <para>
    /// Ported from the <c>LimitPosition</c> block of <c>GCodes::DoStraightMove</c>. Only axes that are
    /// both homed and actually moving are limited. RepRapFirmware gives two reasons and both apply
    /// here: an axis whose position is not known has nothing to limit against, and limiting an axis
    /// the move does not touch could turn a rotational-only move into one that moves a linear axis
    /// too, which the planner would then throw away as having no movement.
    /// </para>
    /// <para>
    /// Whether an unreachable target is an error or is quietly clamped depends on how it was asked
    /// for. An absolute move names a place, and moving somewhere else instead would be wrong; a
    /// relative move names a direction, so going as far as the machine can is the sensible reading.
    /// </para>
    /// <para>
    /// The last resort is for a target that is reachable by a path other than a straight line, which
    /// is a delta near the top of its travel or a SCARA passing its inner radius. A travel move can
    /// simply be uncoordinated - the axes each go their own way and the head takes some curve - but a
    /// printing move cannot, because the curve would be extruded
    /// </para>
    /// </remarks>
    private void LimitPosition(RawMove raw, MovementState state, bool axesRelative, uint axesMentioned,
                                   bool hasForwardExtrusion, int numAxes)
    {
        // CHECK this logic is comparable to RRF in `GCodes::DoStraightMove()`
        uint axesToLimit = 0;
        for (int axis = 0; axis < numAxes && axis < 32; axis++)
        {
            if ((axesMentioned & (1u << axis)) != 0 && model.Move.Axes[axis].Homed)
            {
                axesToLimit |= 1u << axis;
            }
        }

        KinematicsEngine geometry = planner.Parameters.Geometry;
        ReadOnlySpan<float> initialCoords = planner.Builder.StartCoordinates[..numAxes];

        LimitPositionResult result = geometry.LimitPosition(
            raw.Coords.AsSpan(0, numAxes), initialCoords, numAxes, axesToLimit,
            raw.IsCoordinated, model.Move.LimitAxes);

        if (result is LimitPositionResult.Adjusted or LimitPositionResult.AdjustedAndIntermediateUnreachable)
        {
            if (!axesRelative)
            {
                throw new GCodeException("Target position not reachable");
            }

            // The move was clamped, so the interpreter has to be told where it is really going or the
            // next relative move would be measured from a position the machine never reached
            SyncInterpreterToTarget(raw, state, numAxes);

            if (result == LimitPositionResult.Adjusted)
            {
                return;
            }
        }

        if (result is LimitPositionResult.IntermediateUnreachable or LimitPositionResult.AdjustedAndIntermediateUnreachable)
        {
            bool canGoRoundTheHouses = raw.IsCoordinated && !hasForwardExtrusion;
            if (canGoRoundTheHouses)
            {
                LimitPositionResult uncoordinated = geometry.LimitPosition(
                    raw.Coords.AsSpan(0, numAxes), initialCoords, numAxes, axesToLimit,
                    isCoordinated: false, model.Move.LimitAxes);
                if (uncoordinated == LimitPositionResult.Ok)
                {
                    raw.IsCoordinated = false;
                    return;
                }
            }
            throw new GCodeException("Target position not reachable from current position");
        }
        return;
    }

    /// <summary>
    /// Bring the interpreter's position into step with a target that was clamped
    /// </summary>
    /// <param name="raw">The move, whose coordinates are now what the machine will do</param>
    /// <param name="state">Interpreter state</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// RepRapFirmware's <c>ToolOffsetInverseTransform</c> after a limit was applied, and one of the
    /// few places the transform is inverted. Bed compensation has not been applied yet, so the only
    /// term to undo is the one <see cref="ApplyAxisTransform"/> added
    /// </remarks>
    private void SyncInterpreterToTarget(RawMove raw, MovementState state, int numAxes)
    {
        for (int axis = 0; axis < numAxes; axis++)
        {
            state.CurrentUserPosition[axis] = raw.Coords[axis] - model.Move.Axes[axis].Babystep;
        }
    }

    /// <summary>
    /// Fill in where a special move starts from
    /// </summary>
    /// <param name="raw">The move being built</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// Ported from the <c>moveType != 0</c> block of <c>GCodes::DoStraightMove</c>. A raw motor move
    /// is measured in motor positions, so it starts from the motor endpoints converted back to mm per
    /// drive; anything else is still an axis move and starts from the axis coordinates. Both come
    /// from the planner rather than the object model, because the planner's copy is where the last
    /// queued move left the machine and the object model's is where the machine has got to
    /// </remarks>
    private void SeedSpecialMoveCoordinates(RawMove raw, int numAxes)
    {
        MotionParameters parameters = planner.Parameters;
        if (parameters.Geometry.IsRawMotorMove(raw.MoveType))
        {
            ReadOnlySpan<int> endPoints = planner.Builder.EndPoints;
            for (int axis = 0; axis < numAxes; axis++)
            {
                float stepsPerMm = parameters.StepsPerMm[axis];
                raw.Coords[axis] = stepsPerMm != 0.0f ? endPoints[axis] / stepsPerMm : 0.0f;
            }
        }
        else
        {
            ReadOnlySpan<float> startCoordinates = planner.Builder.StartCoordinates;
            for (int axis = 0; axis < numAxes; axis++)
            {
                raw.Coords[axis] = startCoordinates[axis];
            }
        }
    }

    /// <summary>
    /// Apply M556 axis skew compensation to a move's coordinates
    /// </summary>
    /// <param name="userPosition">User coordinates, workplace offset already included</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>Move::AxisTransform()</c>.
    /// </para>
    /// </remarks>
    private void ApplyAxisSkewTransform(Span<float> userPosition)
    {
        // TODO actually apply skew transform
    }

    /// <summary>
    /// Read the E parameter into a move
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="input">The channel's interpreter state</param>
    /// <param name="raw">Move to fill in</param>
    /// <param name="unitScale">Millimetres per user unit</param>
    /// <returns>True if the move extrudes forwards, which is what pressure advance applies to</returns>
    /// <remarks>The caller must hold the object model lock</remarks>
    private bool ApplyExtrusion(Commands.Code code, InputChannel input, RawMove raw, float unitScale)
    {
        bool hasExtrusion = false;
        raw.HasPositiveExtrusion = false;

        if (!code.TryGetFloatArray('E', out float[]? extrusion) || extrusion.Length == 0)
        {
            return false;
        }

        // TODO check that we have a tool to extrude with
        // TODO get tool extruders

        MotionParameters parameters = planner.Parameters;
        int numExtruders = parameters.SharedExtruderCount(model.Move); // TODO use the tool extruders not all extruders

        // One value per extruder for a mixing tool, or a single value for the first extruder. Tool
        // mixing ratios are not ported yet, so a lone E does not fan out
        int count = extrusion.Length == 1 ? Math.Min(1, numExtruders) : Math.Min(extrusion.Length, numExtruders);

        // TODO extend this with mixing extruders
        for (int extruder = 0; extruder < count; extruder++)
        {
            Extruder extruderConfig = model.Move.Extruders[extruder];
            float requestedMm = extrusion[extruder] * unitScale;

            // Absolute extrusion is a running total, so the movement is the difference from where
            // the extruder was last told it had reached
            float movement = input.DrivesRelative ? requestedMm : requestedMm - extruderConfig.RawPosition;
            if (movement != 0.0f)
            {
                hasExtrusion = true;
                if (movement > 0.0f)
                {
                    raw.HasPositiveExtrusion = true;
                }

                // TODO handle volumetric extrusion
            }

            // M221 is the operator adjusting a print, so it applies to the same moves M220 does
            raw.Coords[MotionParameters.ExtruderToDrive(extruder)] =
                raw.ApplyM220M221 ? movement * extruderConfig.Factor : movement;

            // TODO store which extruders are moving
        }

        // TODO handle endstop moves
        return hasExtrusion;
    }

    /// <summary>
    /// Say which endstop stops which drive of a homing move
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="raw">The move being built</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>An error if the move cannot be armed, else null</returns>
    /// <remarks>
    /// <para>
    /// RepRapFirmware picks one of three actions when an endstop fires, and which one depends on the
    /// geometry rather than on the endstop. If moving an axis needs drives other than its own - X on
    /// a CoreXY needs both motors - then stopping only that axis' drivers would leave the others
    /// running and drag the head diagonally into the switch, so the whole move has to stop. That is
    /// RRF's <c>stopAll</c>, and it is decided by exactly the test below. Otherwise the axis is
    /// independent and stopping its own drivers is enough, which is <c>stopAxis</c>.
    /// </para>
    /// <para>
    /// Only the axes the code actually moves are armed. A homing move naming X and Y must not be
    /// stopped by Z's switch happening to be closed already
    /// </para>
    /// </remarks>
    private void ApplyEndstops(Commands.Code code, RawMove raw, int numAxes)
    {
        KinematicsEngine geometry = planner.Parameters.Geometry;

        int stopAllAxis = -1;
        MoveStopInput stopAllInput = new();
        int perAxisCount = 0;
        List<int> alreadyTriggered = [];

        for (int axis = 0; axis < numAxes; axis++)
        {
            if (!code.HasParameter(model.Move.Axes[axis].Letter))
            {
                continue;
            }

            Endstop? endstop = axis < model.Sensors.Endstops.Count ? model.Sensors.Endstops[axis] : null;
            if (endstop is null)
            {
                throw new GCodeException($"No endstop configured for axis {model.Move.Axes[axis].Letter}");
            }

            // TODO if simulating continue to next axis

            if (!TryArmAxis(endstop, axis, raw))
            {
                // Refusing is the point. Leaving the axis unarmed and carrying on would run the move
                // to its full commanded length with nothing to stop it, which for a homing move means
                // driving into the end of the axis. RepRapFirmware's EnableAxisEndstops throws here
                // for the same reason
                throw new GCodeException($"Cannot home {model.Move.Axes[axis].Letter}: {DescribeUnusableEndstop(endstop)}");
            }

            if (endstop.Type is EndstopType.MotorStallAny or EndstopType.MotorStallIndividual)
            {
                // The driver has to be turning slowly enough to tell a stall from normal load, which
                // is what M201.1 configures
                raw.ReduceAcceleration = true;
            }

            if (endstop.Triggered)
            {
                // Already at the switch. The controller only stops a move when an input *changes*,
                // so a switch that is closed before the move starts would never report anything and
                // the axis would drive into it until the user opened and closed the switch by hand.
                // RepRapFirmware ends up in the same place from the other direction: its step
                // interrupt tests the endstop before the first step, so the move ends on the step it
                // began. Recorded here and applied below, once it is known whether this axis can be
                // held on its own
                alreadyTriggered.Add(axis);
            }

            // The axis needs a drive that is not its own, so it cannot be stopped by itself
            if ((geometry.GetControllingDrives(axis) & ~(1u << axis)) != 0)
            {
                if (stopAllAxis >= 0)
                {
                    throw new GCodeException(
                        $"Cannot home {model.Move.Axes[stopAllAxis].Letter} and {model.Move.Axes[axis].Letter} together: "
                        + "on this kinematics either endstop has to stop every drive");
                }
                stopAllAxis = axis;
                stopAllInput.CopyFrom(raw.StopOnInput[axis]);
                raw.ArmedAxes.Add(axis);
            }
            else
            {
                perAxisCount++;
                raw.ArmedAxes.Add(axis);
            }
        }

        if (stopAllAxis >= 0)
        {
            if (perAxisCount > 0)
            {
                throw new GCodeException(
                    $"Cannot home {model.Move.Axes[stopAllAxis].Letter} with another axis: "
                    + "its endstop has to stop every drive, which would disarm the others");
            }

            // Every drive watches the one switch, so whichever driver sees the change first, they all
            // stop. That is what makes this stopAll rather than stopAxis. A per-driver endstop is
            // demoted to its first switch here for the same reason RepRapFirmware demotes it: the
            // drives are coupled, so letting each motor wait for its own switch would keep the
            // others running
            for (int drive = 0; drive < raw.StopOnInput.Length; drive++)
            {
                raw.StopOnInput[drive].SetShared(stopAllInput.Handle, stopAllInput.Boards[0]);
            }

            // On coupled kinematics the whole move stops on the one endstop, so an endstop that is
            // already closed holds every drive rather than only its own axis
            if (alreadyTriggered.Contains(stopAllAxis))
            {
                HoldAxes(raw, numAxes);
                return;
            }
        }

        foreach (int axis in alreadyTriggered)
        {
            HoldAxis(raw, axis);
        }
        return;
    }

    /// <summary>
    /// Fill in what stops one axis of an endstop move, whatever kind of endstop it has
    /// </summary>
    /// <param name="endstop">The axis' endstop</param>
    /// <param name="axis">Axis number</param>
    /// <param name="raw">The move being built</param>
    /// <returns>True if the axis can be stopped, false if its endstop cannot arm a move</returns>
    /// <remarks>
    /// The four kinds are RepRapFirmware's four endstop classes. A switch and a Z probe are inputs a
    /// board watches, so they name a handle the board already knows; a stall is detected by the driver
    /// itself, so what the move watches is the board's stall report and the drivers were armed before
    /// the move was built - see <see cref="ArmStallEndstopsAsync"/>
    /// </remarks>
    private bool TryArmAxis(Endstop endstop, int axis, RawMove raw)
    {
        switch (endstop.Type)
        {
            case EndstopType.InputPin:
                // CHECK should we send a CanMessageChangeInputMonitorV1 message to actually enable the endstops like in `SwitchEndstop::PrimeAxis()`?
                return RemoteEndstops.TryGetStopInput(endstop, axis, model.Move.Axes[axis].Drivers.Count,
                                                      raw.StopOnInput[axis]);

            case EndstopType.ZProbeAsEndstop:
                {
                    int probeNumber = endstop.Probe ?? 0;
                    Probe? probe = probeNumber < model.Sensors.Probes.Count ? model.Sensors.Probes[probeNumber] : null;
                    return probe is not null && RemoteProbes.TryGetStopInput(probe, probeNumber, raw.StopOnInput[axis]);
                }

            case EndstopType.MotorStallAny:
            case EndstopType.MotorStallIndividual:
                {
                    // Which drivers to watch is the geometry's answer, not the axis': stopping on a
                    // CoreXY's X stall means watching both motors, because moving X turns both
                    uint drives = planner.Parameters.Geometry.GetControllingDrives(axis);
                    List<DuetAPI.Utility.DriverId> drivers = [];
                    for (int drive = 0; drive < model.Move.Axes.Count && drive < 32; drive++)
                    {
                        if ((drives & (1u << drive)) != 0)
                        {
                            drivers.AddRange(model.Move.Axes[drive].Drivers);
                        }
                    }
                    return RemoteEndstops.TryGetStallStopInput(CollectionsMarshal.AsSpan(drivers), raw.StopOnInput[axis]);
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// Why an endstop cannot stop a move, for the error the user sees
    /// </summary>
    /// <param name="endstop">The endstop</param>
    /// <returns>The reason</returns>
    private static string DescribeUnusableEndstop(Endstop endstop) => endstop.Type switch
    {
        EndstopType.InputPin => "its endstop has no port assigned",
        EndstopType.ZProbeAsEndstop => "its endstop is a Z probe that cannot stop a move; check M558",
        EndstopType.MotorStallAny or EndstopType.MotorStallIndividual => "no driver is assigned to it",
        _ => "its endstop type is not one a move can be stopped by"
    };

    /// <summary>
    /// Command an axis to stay where it is
    /// </summary>
    /// <param name="raw">The move being built</param>
    /// <param name="axis">Axis to hold</param>
    /// <remarks>The caller must hold the object model lock</remarks>
    private void HoldAxis(RawMove raw, int axis)
        => raw.Coords[axis] = model.Move.Axes[axis].MachinePosition ?? raw.Coords[axis];

    /// <summary>
    /// Command every axis to stay where it is
    /// </summary>
    /// <param name="raw">The move being built</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>The caller must hold the object model lock</remarks>
    private void HoldAxes(RawMove raw, int numAxes)
    {
        for (int axis = 0; axis < numAxes; axis++)
        {
            HoldAxis(raw, axis);
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
                    float[] coords = new float[MotionLimits.MaxAxes];
                    // TODO apply tool offsets?
                    foreach (int axis in axesIncluded)
                    {
                        planner.Builder.SetAxisPosition(axis, coords[axis]);
                    }

                    PublishUserPositions(numAxes);
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
    /// Publish the interpreter's position into the object model
    /// </summary>
    /// <param name="numAxes">Number of axes to publish</param>
    /// <remarks>
    /// RepRapFirmware's <c>GetUserCoordinate</c>: the workplace offset is included in the interpreter's
    /// position and taken back off for reporting, so the number the user sees is the one they typed.
    /// The caller must hold the object model write lock and the planner lock
    /// </remarks>
    private void PublishUserPositions(int numAxes)
    {
        MovementState state = planner.State;
        for (int axis = 0; axis < numAxes; axis++)
        {
            Axis axisConfig = model.Move.Axes[axis];
            axisConfig.UserPosition = state.CurrentUserPosition[axis]
                - WorkplaceOffset(axisConfig, WorkplaceNumber);
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
        SyncInterpreterToMachine();
    }

    /// <summary>
    /// Bring the interpreter's position back into step with where the machine actually is
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>ToolOffsetInverseTransform</c> after a homing or probing move, and the
    /// only place the transform is inverted. Everywhere else the interpreter is authoritative and the
    /// machine follows it; here the machine is somewhere the interpreter did not put it. The caller
    /// must hold the object model write lock and the planner lock
    /// </remarks>
    private void SyncInterpreterToMachine()
    {
        MovementState state = planner.State;
        int numAxes = planner.Parameters.SharedAxisCount(model.Move);
        ReadOnlySpan<float> machinePosition = planner.Builder.StartCoordinates;

        for (int axis = 0; axis < numAxes; axis++)
        {
            state.CurrentUserPosition[axis] = machinePosition[axis] - model.Move.Axes[axis].Babystep;
        }
        PublishUserPositions(numAxes);
    }

    /// <summary>
    /// The workplace offset in effect for an axis
    /// </summary>
    /// <param name="axis">The axis</param>
    /// <param name="workplace">Selected workplace number</param>
    /// <returns>The offset in mm</returns>
    private static float WorkplaceOffset(Axis axis, int workplace)
        => workplace >= 0 && workplace < axis.WorkplaceOffsets.Count ? axis.WorkplaceOffsets[workplace] : 0.0f;

    /// <summary>
    /// The selected workplace, which is a property of the motion system rather than of the machine
    /// </summary>
    /// <remarks>
    /// Only the first motion system is read, as everywhere else here: several of them is a
    /// RepRapFirmware feature that has not been ported, so there is never more than one
    /// </remarks>
    private int WorkplaceNumber
        => model.Move.MotionSystems.Count > 0 ? model.Move.MotionSystems[0].WorkplaceNumber : 0;

    /// <summary>
    /// Bitmap of the axes carrying the given letter
    /// </summary>
    /// <param name="move">The move subsystem</param>
    /// <param name="letter">Axis letter</param>
    /// <returns>The bitmap</returns>
    /// <remarks>
    /// The tool axis mapping is not ported yet, so this is the axes literally named X or Y. When it
    /// is, this becomes the tool's mapping, which is what decides whether a move counts as XY
    /// movement in user space and therefore whether the printing jerk limits apply
    /// </remarks>
    private static uint AxisBitmap(Move move, char letter)
    {
        uint bitmap = 0;
        for (int axis = 0; axis < move.Axes.Count && axis < 32; axis++)
        {
            if (char.ToUpperInvariant(move.Axes[axis].Letter) == letter)
            {
                bitmap |= 1u << axis;
            }
        }
        return bitmap;
    }
}
