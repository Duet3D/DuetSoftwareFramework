using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Native;
using DuetControlServer.Motion.Kinematics;
using Microsoft.Extensions.Logging;

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
/// <param name="logger">Logger</param>
internal sealed partial class GCodeHandler(
    Model.ObjectModel model,
    MovePlanner planner,
    BedCompensation bedCompensation,
    Files.MacroRunner macroRunner,
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
    public async ValueTask<Message?> ProcessAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        switch (code.MajorNumber)
        {
            // Rapid and coordinated moves
            case 0:
            case 1:
                return await HandleMoveAsync(code, isCoordinated: code.MajorNumber == 1, cancellationToken);

            // Set units to inches / millimetres
            case 20:
            case 21:
                await UpdateInputAsync(code, input => input.DistanceUnit = code.MajorNumber == 20 ? DistanceUnit.Inch : DistanceUnit.MM, cancellationToken);
                return new Message();

            // Absolute / relative positioning.
            case 90:
            case 91:
                await UpdateInputAsync(code, input =>
                {
                    input.AxesRelative = code.MajorNumber == 91;
                }, cancellationToken);
                return new Message();

            // Home the machine
            case 28:
                return await HandleHomeAsync(code, cancellationToken);

            // Probe the grid and build a height map
            case 29:
                return await HandleProbeGridAsync(code, cancellationToken);

            // Probe the bed
            case 30:
                return await HandleProbeAsync(code, cancellationToken);

            // Set or report the Z probe trigger height, offsets and threshold
            case 31:
                return await HandleProbeParametersAsync(code, cancellationToken);

            // Set position without moving
            case 92:
                return await HandleSetPositionAsync(code, cancellationToken);

            // Inverse time / feed rate mode
            case 93:
            case 94:
                await UpdateInputAsync(code, input => input.InverseTimeMode = code.MajorNumber == 93, cancellationToken);
                return new Message();

            default:
                return null;
        }
    }

    /// <summary>
    /// React to an executed G-code before its result is returned
    /// </summary>
    /// <param name="code">Code processed by RepRapFirmware</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result to output</returns>
    public ValueTask CodeExecutedAsync(Commands.Code code, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// Turn a G0 or G1 into a queued move
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="isCoordinated">Whether the axes move together (G1) or independently (G0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message?> HandleMoveAsync(Commands.Code code, bool isCoordinated, CancellationToken cancellationToken)
    {
        // A special move is planned against the motor positions rather than the axis positions, so
        // the machine has to have settled before it is built - as in RepRapFirmware, which locks and
        // waits for standstill before reading them
        int moveType = code.GetInt('H', 0);
        if (moveType != 0)
        {
            await planner.WaitForStandstillAsync(cancellationToken);
        }

        // Retrying rather than failing when the ring is full is what applies back-pressure: it is the
        // normal state when moves are commanded faster than the machine can run them, and it is what
        // keeps the G-code stream in step with the machine
        while (!cancellationToken.IsCancellationRequested)
        {
            MoveSubmitResult result;
            List<int> homingAxes = [];

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

                // Held across building and queueing, because the move is a delta from the state the
                // planner holds: another channel building in between would measure from the wrong
                // place. Building also advances that state, which is what makes the rollback below
                // necessary - and why the retry path must not simply build the same code again
                using (planner.Lock())
                {
                    MovementState state = planner.State;
                    float[] positionBeforeMove = ArrayPool<float>.Shared.Rent(MotionLimits.MaxAxes);
                    try
                    {
                        state.CurrentUserPosition.CopyTo(positionBeforeMove, 0);

                        RawMove move = BuildRawMove(code, input, isCoordinated, out Message? error);
                        if (error is not null)
                        {
                            positionBeforeMove.AsSpan(0, MotionLimits.MaxAxes).CopyTo(state.CurrentUserPosition);
                            return error;
                        }

                        homingAxes = move.HomingAxes;
                        result = planner.QueueMove(move);

                        if (result is MoveSubmitResult.Queued or MoveSubmitResult.NoMovement)
                        {
                            // The move is committed, so the reported position is where it will leave
                            // the machine. Recording it now rather than on completion is what lets the
                            // next code be interpreted without waiting for the machine to catch up
                            CommitPositions(move);
                        }
                        else
                        {
                            // RepRapFirmware's abandonMove: the move is not going to happen, so the
                            // interpreter has to be put back where it was. Busy retries the same code,
                            // and a relative move applied twice would be a real movement error
                            positionBeforeMove.AsSpan(0, MotionLimits.MaxAxes).CopyTo(state.CurrentUserPosition);
                        }
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(positionBeforeMove);
                    }
                }
            }

            switch (result)
            {
                case MoveSubmitResult.Queued:
                case MoveSubmitResult.NoMovement:
                    if (moveType != 0)
                    {
                        // A special move is where the machine finds out where it is, so the code has
                        // to wait for it rather than queue it and move on. Every ordinary move is
                        // committed at its planned endpoint and the next code interpreted straight
                        // away, which is what keeps the queue full
                        await FinishSpecialMoveAsync(homingAxes, cancellationToken);
                    }
                    return new Message();

                case MoveSubmitResult.Rejected:
                    logger.LogError("Rejected {Code}", code);
                    return new Message(MessageType.Error, "Move could not be planned; see the log for details");

                default:
                    await Task.Delay(RingFullRetryDelay, cancellationToken);
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Read a movement code's parameters into a move
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="input">The channel's interpreter state</param>
    /// <param name="isCoordinated">Whether this is a G1</param>
    /// <param name="error">Receives why the move cannot be built, if it cannot</param>
    /// <returns>The move</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    private RawMove BuildRawMove(Commands.Code code, InputChannel input, bool isCoordinated, out Message? error)
    {
        error = null;
        MotionParameters parameters = planner.Parameters;
        int numAxes = Math.Min(parameters.NumAxes, model.Move.Axes.Count);
        float unitScale = input.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
        MovementState state = planner.State;

        RawMove raw = new()
        {
            IsCoordinated = isCoordinated,
            InverseTimeMode = input.InverseTimeMode,
            XAxes = AxisBitmap(model.Move, 'X'),
            YAxes = AxisBitmap(model.Move, 'Y')
        };

        // H selects what kind of move this is. H1, H3 and H4 stop on the endstops - that is homing -
        // and H2 is an individual motor move that ignores them
        raw.MoveType = code.GetInt('H', 0);
        raw.CheckEndstops = raw.MoveType is 1 or 3 or 4;

        // G53 asks for machine coordinates on this line only, so neither the workplace offset nor
        // (once tools exist) the tool offset applies to it
        bool machineCoordinates = code.Flags.HasFlag(CodeFlags.EnforceAbsolutePosition);

        if (raw.MoveType == 0)
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
                else
                {
                    state.CurrentUserPosition[axis] = machineCoordinates
                        ? moveArg
                        : moveArg + WorkplaceOffset(axisConfig, model.Move.WorkplaceNumber);
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

            // Every axis is transformed, not just the ones mentioned: an axis the user left out still
            // has to be commanded to where the interpreter thinks it is, and babystepping may have
            // moved that since the last move
            ApplyAxisTransform(state.CurrentUserPosition, raw.Coords, numAxes);
            ApplyBedCompensation(raw, numAxes);
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
                    error = new Message(MessageType.Error,
                        "Attempt to move individual motors of a delta machine to absolute positions");
                    return raw;
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
        error = LoadFeedRate(code, input, raw);
        if (error is not null)
        {
            return raw;
        }

        if (raw.CheckEndstops)
        {
            error = ApplyEndstops(code, raw, numAxes);
        }

        bool hasForwardExtrusion = ApplyExtrusion(code, input, raw, unitScale);

        // Pressure advance is for a printing move, so it needs forward extrusion and movement in
        // something other than Z. RepRapFirmware excludes Z because a move that only changes height
        // while extruding is not laying a line down
        if (hasForwardExtrusion)
        {
            raw.UsePressureAdvance = MentionsAxisOtherThanZ(code, numAxes);
        }

        return raw;
    }

    /// <summary>
    /// Work out how fast the move should go
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="input">The channel's interpreter state</param>
    /// <param name="move">The move being built, with its move type and mentioned axes already set</param>
    /// <returns>An error if the feed rate cannot be determined, else null</returns>
    /// <remarks>
    /// Ported from <c>GCodes::LoadFeedrateFromGCode</c>. F persists across codes, so the value the
    /// user typed is kept on the channel - unconverted, because whether inches apply depends on the
    /// axes of the move it is eventually used for, which is not known when it is read
    /// </remarks>
    private Message? LoadFeedRate(Commands.Code code, InputChannel input, RawMove move)
    {
        // The overrides belong to the print, so they apply to an ordinary move that names an axis and
        // to nothing else
        move.ApplyM220M221 = move.MoveType == 0
            && (move.LinearAxesMentioned || move.RotationalAxesMentioned)
            && !code.Flags.HasFlag(CodeFlags.IsFromSystemMacro);
        move.UsingStandardFeedrate = true;

        if (input.InverseTimeMode)
        {
            // G93: F is one over the time the move should take, in minutes, so it is a duration and
            // not a speed. It cannot carry over from a previous move because it describes this move's
            // length, and it is not a distance, so the inch scale does not apply to it
            if (!code.TryGetFloat('F', out float inverseTime) || inverseTime <= 0.0f)
            {
                return new Message(MessageType.Error,
                    "Feed rate must be specified with every move when using inverse time mode");
            }

            // A duration, so the speed factor divides it rather than multiplying: M220 S200 should
            // make the move take half as long, not twice as long
            float duration = SecondsPerMinute / inverseTime;
            move.DurationSec = move.ApplyM220M221 ? duration / model.Move.SpeedFactor : duration;
            return null;
        }

        if (code.TryGetFloat('F', out float feedRate))
        {
            // Kept raw, which is also what inputs[].feedRate reports
            input.FeedRate = feedRate;
        }

        // A move that names only rotational axes is measured in degrees, so G20 does not scale its
        // feed rate even though the same F would be inches per minute for a linear move
        bool convertInches = move.LinearAxesMentioned || !move.RotationalAxesMentioned;
        float unitScale = convertInches && input.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;
        float converted = input.FeedRate * unitScale / SecondsPerMinute;

        move.FeedRateMmPerSec = move.ApplyM220M221 ? converted * model.Move.SpeedFactor : converted;
        return null;
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
    /// Fill in where a special move starts from
    /// </summary>
    /// <param name="move">The move being built</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// Ported from the <c>moveType != 0</c> block of <c>GCodes::DoStraightMove</c>. A raw motor move
    /// is measured in motor positions, so it starts from the motor endpoints converted back to mm per
    /// drive; anything else is still an axis move and starts from the axis coordinates. Both come
    /// from the planner rather than the object model, because the planner's copy is where the last
    /// queued move left the machine and the object model's is where the machine has got to
    /// </remarks>
    private void SeedSpecialMoveCoordinates(RawMove move, int numAxes)
    {
        MotionParameters parameters = planner.Parameters;
        if (parameters.Geometry.IsRawMotorMove(move.MoveType))
        {
            ReadOnlySpan<int> endPoints = planner.Builder.EndPoints;
            for (int axis = 0; axis < numAxes; axis++)
            {
                float stepsPerMm = parameters.StepsPerMm[axis];
                move.Coords[axis] = stepsPerMm != 0.0f ? endPoints[axis] / stepsPerMm : 0.0f;
            }
        }
        else
        {
            ReadOnlySpan<float> startCoordinates = planner.Builder.StartCoordinates;
            for (int axis = 0; axis < numAxes; axis++)
            {
                move.Coords[axis] = startCoordinates[axis];
            }
        }
    }

    /// <summary>
    /// Convert user coordinates into the machine coordinates a move is planned in
    /// </summary>
    /// <param name="userPosition">User coordinates, workplace offset already included</param>
    /// <param name="coords">Receives the machine coordinates</param>
    /// <param name="numAxes">Number of axes to convert</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>ToolOffsetTransform</c>. Today it applies babystepping alone; tool offsets,
    /// X/Y/Z axis mapping, axis scale factors and Z hop are terms to be added here as they are ported.
    /// </para>
    /// <para>
    /// The direction matters. This is the only way user coordinates become machine coordinates, so
    /// every term added here applies everywhere at once, and nothing needs a matching inverse: the
    /// interpreter never reconstructs its position from a machine coordinate on the normal path. See
    /// <see cref="RedefineMachinePosition"/> for the cases where it has to
    /// </para>
    /// </remarks>
    private void ApplyAxisTransform(ReadOnlySpan<float> userPosition, float[] coords, int numAxes)
    {
        for (int axis = 0; axis < numAxes; axis++)
        {
            // Babystepping shifts where the machine goes without changing the coordinate the user
            // asked for. RepRapFirmware applies a change as a small move of its own; here it takes
            // effect on the next commanded move instead
            coords[axis] = userPosition[axis] + model.Move.Axes[axis].Babystep;
        }
    }

    /// <summary>
    /// Read the E parameter into a move
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="input">The channel's interpreter state</param>
    /// <param name="move">Move to fill in</param>
    /// <param name="unitScale">Millimetres per user unit</param>
    /// <returns>True if the move extrudes forwards, which is what pressure advance applies to</returns>
    /// <remarks>The caller must hold the object model lock</remarks>
    private bool ApplyExtrusion(Commands.Code code, InputChannel input, RawMove move, float unitScale)
    {
        if (!code.TryGetFloatArray('E', out float[]? extrusion) || extrusion.Length == 0)
        {
            return false;
        }

        MotionParameters parameters = planner.Parameters;
        int numExtruders = Math.Min(parameters.NumExtruders, model.Move.Extruders.Count);

        // One value per extruder for a mixing tool, or a single value for the first extruder. Tool
        // mixing ratios are not ported yet, so a lone E does not fan out
        int count = extrusion.Length == 1 ? Math.Min(1, numExtruders) : Math.Min(extrusion.Length, numExtruders);

        bool hasForwardExtrusion = false;
        for (int extruder = 0; extruder < count; extruder++)
        {
            Extruder extruderConfig = model.Move.Extruders[extruder];
            float requestedMm = extrusion[extruder] * unitScale;

            // Absolute extrusion is a running total, so the movement is the difference from where
            // the extruder was last told it had reached
            float movement = input.DrivesRelative ? requestedMm : requestedMm - extruderConfig.RawPosition;
            if (movement > 0.0f)
            {
                hasForwardExtrusion = true;
            }

            // M221 is the operator adjusting a print, so it applies to the same moves M220 does
            move.Coords[MotionParameters.ExtruderToDrive(extruder)] =
                move.ApplyM220M221 ? movement * extruderConfig.Factor : movement;
        }
        return hasForwardExtrusion;
    }

    /// <summary>
    /// Say which endstop stops which drive of a homing move
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="move">The move being built</param>
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
    private Message? ApplyEndstops(Commands.Code code, RawMove move, int numAxes)
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
            if (endstop is null ||
                !RemoteEndstops.TryGetStopInput(endstop, axis, model.Move.Axes[axis].Drivers.Count, move.StopOnInput[axis]))
            {
                continue;                       // no endstop, or one no move can stop on
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
                    return new Message(MessageType.Error,
                        $"Cannot home {model.Move.Axes[stopAllAxis].Letter} and {model.Move.Axes[axis].Letter} together: "
                        + "on this kinematics either endstop has to stop every drive");
                }
                stopAllAxis = axis;
                stopAllInput.CopyFrom(move.StopOnInput[axis]);
                move.HomingAxes.Add(axis);
            }
            else
            {
                perAxisCount++;
                move.HomingAxes.Add(axis);
            }
        }

        if (stopAllAxis >= 0)
        {
            if (perAxisCount > 0)
            {
                return new Message(MessageType.Error,
                    $"Cannot home {model.Move.Axes[stopAllAxis].Letter} with another axis: "
                    + "its endstop has to stop every drive, which would disarm the others");
            }

            // Every drive watches the one switch, so whichever driver sees the change first, they all
            // stop. That is what makes this stopAll rather than stopAxis. A per-driver endstop is
            // demoted to its first switch here for the same reason RepRapFirmware demotes it: the
            // drives are coupled, so letting each motor wait for its own switch would keep the
            // others running
            for (int drive = 0; drive < move.StopOnInput.Length; drive++)
            {
                move.StopOnInput[drive].SetShared(stopAllInput.Handle, stopAllInput.Boards[0]);
            }

            // On coupled kinematics the whole move stops on the one endstop, so an endstop that is
            // already closed holds every drive rather than only its own axis
            if (alreadyTriggered.Contains(stopAllAxis))
            {
                HoldAxes(move, numAxes);
                return null;
            }
        }

        foreach (int axis in alreadyTriggered)
        {
            HoldAxis(move, axis);
        }
        return null;
    }

    /// <summary>
    /// Command an axis to stay where it is
    /// </summary>
    /// <param name="move">The move being built</param>
    /// <param name="axis">Axis to hold</param>
    /// <remarks>The caller must hold the object model lock</remarks>
    private void HoldAxis(RawMove move, int axis)
        => move.Coords[axis] = model.Move.Axes[axis].MachinePosition ?? move.Coords[axis];

    /// <summary>
    /// Command every axis to stay where it is
    /// </summary>
    /// <param name="move">The move being built</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>The caller must hold the object model lock</remarks>
    private void HoldAxes(RawMove move, int numAxes)
    {
        for (int axis = 0; axis < numAxes; axis++)
        {
            HoldAxis(move, axis);
        }
    }

    /// <summary>
    /// Publish the positions a committed move will leave the machine at
    /// </summary>
    /// <param name="move">The move</param>
    /// <remarks>
    /// <para>
    /// A projection of the interpreter's state, not a derivation from the move. The move's
    /// coordinates have been through the axis transform and the bed compensation on the way out, and
    /// inverting all of that to recover what the user asked for is exactly what the interpreter's own
    /// position exists to avoid.
    /// </para>
    /// <para>
    /// <c>machinePosition</c> is deliberately not written here. It is the live position, published
    /// from the engine by <see cref="MotionService"/>, and a move that has only been queued has not
    /// moved the machine yet
    /// </para>
    /// <para>The caller must hold the object model write lock and the planner lock</para>
    /// </remarks>
    private void CommitPositions(RawMove move)
    {
        MotionParameters parameters = planner.Parameters;
        int numAxes = Math.Min(parameters.NumAxes, model.Move.Axes.Count);

        if (move.MoveType == 0)
        {
            PublishUserPositions(numAxes);
        }

        int numExtruders = Math.Min(parameters.NumExtruders, model.Move.Extruders.Count);
        for (int extruder = 0; extruder < numExtruders; extruder++)
        {
            float movement = move.Coords[MotionParameters.ExtruderToDrive(extruder)];
            if (movement != 0.0f)
            {
                Extruder extruderConfig = model.Move.Extruders[extruder];
                extruderConfig.Position += movement;
                extruderConfig.RawPosition += movement;
            }
        }
    }

    /// <summary>
    /// G92: redefine the current position without moving
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message?> HandleSetPositionAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            InputChannel? input = model.Inputs[code.Channel];
            float unitScale = input?.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;

            using (planner.Lock())
            {
                MovementState state = planner.State;
                int numAxes = Math.Min(planner.Parameters.NumAxes, model.Move.Axes.Count);
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
                    ApplyAxisTransform(state.CurrentUserPosition, coords, numAxes);
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
                - WorkplaceOffset(axisConfig, model.Move.WorkplaceNumber);
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
        int numAxes = Math.Min(planner.Parameters.NumAxes, model.Move.Axes.Count);
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
