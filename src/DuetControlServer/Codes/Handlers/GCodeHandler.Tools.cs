using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using DuetControlServer.Motion;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The G-codes that belong to a tool
/// </summary>
internal sealed partial class GCodeHandler
{
    /// <summary>The form of G10 its L parameter selects, numbered as RepRapFirmware's GCodes2.cpp case 10 reads it</summary>
    internal enum G10Form
    {
        /// <summary>Set a tool's offsets</summary>
        ToolOffsets = 1,

        /// <summary>Set a workplace's origin</summary>
        WorkplaceOrigin = 2,

        /// <summary>Set a workplace's origin such that the current position has the given coordinates</summary>
        WorkplaceOriginAtPosition = 20
    }

    /// <summary>
    /// G10: set tool offsets or workplace coordinates, or retract
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// G10 is several codes wearing one number. L chooses among them, as in RepRapFirmware's
    /// GCodes2.cpp case 10: L1 sets a tool's offsets, L2 a workplace's origin and L20 a workplace's
    /// origin such that the current position has the given coordinates. Without L, P names a tool
    /// whose offsets are set, and with neither it is firmware retraction, which is the older meaning
    /// and the one a slicer emits
    /// </remarks>
    private async ValueTask<Message> HandleToolOffsetsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (code.TryGetEnum('L', out G10Form form))
        {
            return form switch
            {
                G10Form.ToolOffsets => await SetToolOffsetsAsync(code, cancellationToken),
                G10Form.WorkplaceOrigin => await SetWorkplaceOffsetsAsync(code, relativeToPosition: false, cancellationToken),
                _ => await SetWorkplaceOffsetsAsync(code, relativeToPosition: true, cancellationToken)
            };
        }

        if (!code.HasParameter('P'))
        {
            // TODO firmware retraction (G10 with no P, and G11) needs tools[].retraction acting on a
            // move rather than only being stored - M207 sets it and nothing reads it yet
            return new Message(MessageType.Warning,
                               "Firmware retraction is not supported yet; G10 without P does nothing");
        }
        return await SetToolOffsetsAsync(code, cancellationToken);
    }

    /// <summary>
    /// G10 P or G10 L1: set or report a tool's offsets
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    private async ValueTask<Message> SetToolOffsetsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        // TODO RRF's SetOrReportOffsets addresses the current tool when P is not given
        if (!code.TryGetInt('P', out int toolNumber))
        {
            return new Message(MessageType.Error, "Missing tool number");
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Tool? tool = toolManager.Find(toolNumber);
            if (tool is null)
            {
                return new Message(MessageType.Error, $"Tool {toolNumber} not found");
            }

            InputChannel? input = model.Inputs[code.Channel];
            float unitScale = MoveInterpreter.UnitScale(input);

            bool seen = false;
            using (planner.Lock())
            {
                int numAxes = planner.Parameters.SharedAxisCount(model.Move);
                while (tool.Offsets.Count < numAxes)
                {
                    tool.Offsets.Add(0.0f);
                }

                for (int axis = 0; axis < numAxes; axis++)
                {
                    Axis axisConfig = model.Move.Axes[axis];
                    if (code.TryGetFloat(axisConfig.Letter, out float offset))
                    {
                        tool.Offsets[axis] = axisConfig.Rotational ? offset : offset * unitScale;
                        seen = true;

                        // An offset config-override.g sets is one M500 has to write back, as RRF's
                        // runningM501 marks it
                        if (code.Flags.HasFlag(CodeFlags.IsFromConfigOverride))
                        {
                            tool.OffsetsProbed |= 1 << axis;
                        }
                    }
                }

                if (seen && model.State.CurrentTool == toolNumber)
                {
                    // The offsets just moved, so where the interpreter thinks the nozzle is has moved
                    // with them. The machine has not moved, so it is the user position that has to
                    // follow - which is the direction the inverse transform exists for
                    moveInterpreter.SyncInterpreterToCoords();
                    planner.PublishCommittedPosition();
                }
            }

            if (!seen)
            {
                return new Message(MessageType.Success, DescribeOffsets(tool));
            }
        }
        return new Message();
    }

    /// <summary>
    /// G10 L2 or L20: set or report the origin of a workplace
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="relativeToPosition">Whether the axis values are what the current position is to
    /// be called in the workplace (L20) rather than its origin (L2)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// RepRapFirmware's <c>GCodes::GetSetWorkplaceCoordinates</c>. P numbers the workplaces from one
    /// as G54 to G59.3 do, and P0 or no P is the current one. Only the visible axes are addressed
    /// </remarks>
    private async ValueTask<Message> SetWorkplaceOffsetsAsync(Commands.Code code, bool relativeToPosition,
                                                              CancellationToken cancellationToken)
    {
        uint workplaceNumber = code.GetUInt('P', defaultValue: 0, max: MachineLimits.NumWorkplaces);
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            int workplace = workplaceNumber == 0 ? moveInterpreter.WorkplaceNumber : (int)workplaceNumber - 1;
            float unitScale = MoveInterpreter.UnitScale(model.Inputs[code.Channel]);

            bool seen = false;
            using (planner.Lock())
            {
                int numAxes = planner.Parameters.SharedAxisCount(model.Move);
                for (int axis = 0; axis < numAxes; axis++)
                {
                    Axis axisConfig = model.Move.Axes[axis];
                    if (!axisConfig.Visible || !code.TryGetFloat(axisConfig.Letter, out float value))
                    {
                        continue;
                    }

                    // A rotary axis is in degrees whatever G20 says, as it is for a move
                    float coordinate = axisConfig.Rotational ? value : value * unitScale;
                    while (axisConfig.WorkplaceOffsets.Count <= workplace)
                    {
                        axisConfig.WorkplaceOffsets.Add(0.0f);
                    }
                    axisConfig.WorkplaceOffsets[workplace] = relativeToPosition
                        ? planner.State.CurrentUserPosition[axis] - coordinate
                        : coordinate;
                    seen = true;
                }

                if (seen && workplace == moveInterpreter.WorkplaceNumber)
                {
                    // The machine has not moved, but what its position is called in this workplace has
                    planner.PublishCommittedPosition();
                }
            }

            if (!seen)
            {
                StringBuilder builder = new();
                builder.Append(CultureInfo.InvariantCulture, $"Origin of workplace {workplace + 1}:");
                foreach (Axis axis in model.Move.Axes)
                {
                    if (axis.Visible)
                    {
                        float offset = MoveInterpreter.WorkplaceOffset(axis, workplace);
                        builder.Append(CultureInfo.InvariantCulture,
                                       $" {axis.Letter}{(axis.Rotational ? offset : offset / unitScale):F2}");
                    }
                }
                return new Message(MessageType.Success, builder.ToString());
            }
        }
        return new Message();
    }

    /// <summary>
    /// Report a tool's offsets, as G10 with only a P does
    /// </summary>
    private string DescribeOffsets(Tool tool)
    {
        System.Text.StringBuilder builder = new();
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"Tool {tool.Number} offsets:");
        for (int axis = 0; axis < tool.Offsets.Count && axis < model.Move.Axes.Count; axis++)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture,
                           $" {model.Move.Axes[axis].Letter}{tool.Offsets[axis]:F2}");
        }
        return builder.ToString();
    }
}
