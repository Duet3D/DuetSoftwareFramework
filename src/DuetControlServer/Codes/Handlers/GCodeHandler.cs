using System;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
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
internal sealed class GCodeHandler(
    Model.ObjectModel model,
    MovePlanner planner,
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

            // Absolute / relative positioning. These set the extrusion mode too unless M82 or M83
            // has overridden it, which is what RepRapFirmware does in its native mode
            case 90:
            case 91:
                await UpdateInputAsync(code, input =>
                {
                    input.AxesRelative = code.MajorNumber == 91;
                    input.DrivesRelative = code.MajorNumber == 91;
                }, cancellationToken);
                return new Message();

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
        // Retrying rather than failing when the ring is full is what applies back-pressure: it is the
        // normal state when moves are commanded faster than the machine can run them, and it is what
        // keeps the G-code stream in step with the machine
        while (!cancellationToken.IsCancellationRequested)
        {
            MoveSubmitResult result;

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

                RawMove move = BuildRawMove(code, input, isCoordinated);
                result = planner.QueueMove(move);

                if (result is MoveSubmitResult.Queued or MoveSubmitResult.NoMovement)
                {
                    // The move is committed, so the reported position is where it will leave the
                    // machine. Recording it now rather than on completion is what lets the next code
                    // be interpreted without waiting for the machine to catch up
                    CommitPositions(move);
                }
            }

            switch (result)
            {
                case MoveSubmitResult.Queued:
                case MoveSubmitResult.NoMovement:
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
    /// <returns>The move</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    private RawMove BuildRawMove(Commands.Code code, InputChannel input, bool isCoordinated)
    {
        MotionParameters parameters = planner.Parameters;
        int numAxes = Math.Min(parameters.NumAxes, model.Move.Axes.Count);
        float unitScale = input.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;

        RawMove move = new()
        {
            IsCoordinated = isCoordinated,
            InverseTimeMode = input.InverseTimeMode,
            XAxes = AxisBitmap(model.Move, 'X'),
            YAxes = AxisBitmap(model.Move, 'Y')
        };

        // Start from where the machine already is, so an axis the user did not mention keeps its
        // position rather than being commanded to zero
        for (int axis = 0; axis < numAxes; axis++)
        {
            move.Coords[axis] = model.Move.Axes[axis].MachinePosition ?? 0.0f;
        }

        for (int axis = 0; axis < numAxes; axis++)
        {
            Axis axisConfig = model.Move.Axes[axis];
            if (!code.TryGetFloat(axisConfig.Letter, out float value))
            {
                continue;
            }

            float requested = input.AxesRelative
                ? (axisConfig.UserPosition ?? 0.0f) + (value * unitScale)
                : value * unitScale;

            // The workplace offset is what separates the coordinate the user typed from the machine
            // coordinate the kinematics work in
            move.Coords[axis] = requested + WorkplaceOffset(axisConfig, model.Move.WorkplaceNumber);

            if (axisConfig.Rotational)
            {
                move.RotationalAxesMentioned = true;
            }
            else
            {
                move.LinearAxesMentioned = true;
            }
        }

        // H selects what kind of move this is. H1, H3 and H4 stop on the endstops - that is homing -
        // and H2 is an individual motor move that ignores them
        move.MoveType = code.GetInt('H', 0);
        move.CheckEndstops = move.MoveType is 1 or 3 or 4;
        if (move.CheckEndstops)
        {
            ApplyEndstops(code, move, numAxes);
        }

        // Babystepping shifts where the machine goes without changing the coordinate the user asked
        // for, so it is added to the target here and taken back off in CommitPositions. RRF applies a
        // change as a small move of its own; here it takes effect on the next commanded move instead
        for (int axis = 0; axis < numAxes; axis++)
        {
            move.Coords[axis] += model.Move.Axes[axis].Babystep;
        }

        ApplyExtrusion(code, input, move, unitScale);

        // F persists across codes, which is why it is stored on the channel rather than the move
        if (code.TryGetFloat('F', out float feedRate))
        {
            // G-code feed rates are per minute
            input.FeedRate = feedRate * unitScale / 60.0f;
        }
        move.FeedRateMmPerSec = input.FeedRate * model.Move.SpeedFactor;

        return move;
    }

    /// <summary>
    /// Read the E parameter into a move
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="input">The channel's interpreter state</param>
    /// <param name="move">Move to fill in</param>
    /// <param name="unitScale">Millimetres per user unit</param>
    /// <remarks>The caller must hold the object model lock</remarks>
    private void ApplyExtrusion(Commands.Code code, InputChannel input, RawMove move, float unitScale)
    {
        if (!code.TryGetFloatArray('E', out float[]? extrusion) || extrusion.Length == 0)
        {
            return;
        }

        MotionParameters parameters = planner.Parameters;
        int numExtruders = Math.Min(parameters.NumExtruders, model.Move.Extruders.Count);

        // One value per extruder for a mixing tool, or a single value for the first extruder. Tool
        // mixing ratios are not ported yet, so a lone E does not fan out
        int count = extrusion.Length == 1 ? Math.Min(1, numExtruders) : Math.Min(extrusion.Length, numExtruders);

        for (int extruder = 0; extruder < count; extruder++)
        {
            Extruder extruderConfig = model.Move.Extruders[extruder];
            float requestedMm = extrusion[extruder] * unitScale;

            // Absolute extrusion is a running total, so the movement is the difference from where
            // the extruder was last told it had reached
            float movement = input.DrivesRelative ? requestedMm : requestedMm - extruderConfig.RawPosition;

            move.Coords[MotionParameters.ExtruderToDrive(extruder)] = movement * extruderConfig.Factor;
            if (movement != 0.0f)
            {
                move.UsePressureAdvance = true;
            }
        }
    }

    /// <summary>
    /// Say which endstop stops each drive of a homing move
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="move">The move being built</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// <para>
    /// Only the axes the code actually moves are given an endstop. A homing move that mentions X and
    /// Y must not be stopped by Z's switch happening to be closed already.
    /// </para>
    /// <para>
    /// The identity travels with the move all the way to the controller, which is what watches for
    /// the input change and stops the drives; nothing on this side is fast enough. See section 10 of
    /// docs/devel/MCODE_MIGRATION.md
    /// </para>
    /// </remarks>
    private void ApplyEndstops(Commands.Code code, RawMove move, int numAxes)
    {
        for (int axis = 0; axis < numAxes; axis++)
        {
            if (!code.HasParameter(model.Move.Axes[axis].Letter))
            {
                continue;
            }

            Endstop? endstop = axis < model.Sensors.Endstops.Count ? model.Sensors.Endstops[axis] : null;
            if (endstop is not null && RemoteEndstops.TryGetStopInput(endstop, axis, out uint stopInput))
            {
                move.StopOnInput[axis] = stopInput;
            }
        }
    }

    /// <summary>
    /// Record the positions a committed move will leave the machine at
    /// </summary>
    /// <param name="move">The move</param>
    /// <remarks>The caller must hold the object model write lock</remarks>
    private void CommitPositions(RawMove move)
    {
        MotionParameters parameters = planner.Parameters;
        int numAxes = Math.Min(parameters.NumAxes, model.Move.Axes.Count);

        for (int axis = 0; axis < numAxes; axis++)
        {
            Axis axisConfig = model.Move.Axes[axis];

            // The babystep offset is invisible to the reported coordinates, so it comes back off
            // whatever was actually commanded
            float commanded = move.Coords[axis] - axisConfig.Babystep;
            axisConfig.MachinePosition = commanded;
            axisConfig.UserPosition = commanded - WorkplaceOffset(axisConfig, model.Move.WorkplaceNumber);
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
                for (int axis = 0; axis < model.Move.Axes.Count; axis++)
                {
                    Axis axisConfig = model.Move.Axes[axis];
                    if (!code.TryGetFloat(axisConfig.Letter, out float value))
                    {
                        continue;
                    }

                    float userPosition = value * unitScale;
                    float machinePosition = userPosition + WorkplaceOffset(axisConfig, model.Move.WorkplaceNumber);

                    axisConfig.UserPosition = userPosition;
                    axisConfig.MachinePosition = machinePosition;

                    // The planner keeps its own machine position, and this changes what that
                    // position is called without moving anything
                    planner.Builder.SetAxisPosition(axis, machinePosition);
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
