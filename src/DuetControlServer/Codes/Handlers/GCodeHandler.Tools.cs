using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The G-codes that belong to a tool
/// </summary>
internal sealed partial class GCodeHandler
{
    /// <summary>
    /// G10: set tool offsets, or retract
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// <para>
    /// G10 is two codes wearing one number, and which one it is depends on whether P or L is given.
    /// With P it sets a tool's offsets; without, it is firmware retraction, which is the older
    /// meaning and the one a slicer emits.
    /// </para>
    /// <para>
    /// L is RepRapFirmware's discriminator between setting the offsets directly (L1, or absent) and
    /// setting them so that the current position becomes a given coordinate (L2 and L20 are the
    /// workplace forms, which belong to G10's other job)
    /// </para>
    /// </remarks>
    private async ValueTask<Message> HandleToolOffsetsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.HasParameter('P'))
        {
            // TODO firmware retraction (G10 with no P, and G11) needs tools[].retraction acting on a
            // move rather than only being stored - M207 sets it and nothing reads it yet
            return new Message(MessageType.Warning,
                               "Firmware retraction is not supported yet; G10 without P does nothing");
        }

        if (!code.TryGetInt('P', out int toolNumber))
        {
            return new Message(MessageType.Error, "Missing tool number");
        }

        // The offsets are part of the transform every queued move was planned against, so they must
        // not change while one is in flight
        if (!await planner.StandstillAsync(cancellationToken))
        {
            throw new System.OperationCanceledException();
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Tool? tool = toolManager.Find(toolNumber);
            if (tool is null)
            {
                return new Message(MessageType.Error, $"Tool {toolNumber} not found");
            }

            InputChannel? input = model.Inputs[code.Channel];
            float unitScale = input?.DistanceUnit == DistanceUnit.Inch ? MmPerInch : 1.0f;

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
                    }
                }

                if (seen && model.State.CurrentTool == toolNumber)
                {
                    // The offsets just moved, so where the interpreter thinks the nozzle is has moved
                    // with them. The machine has not moved, so it is the user position that has to
                    // follow - which is the direction the inverse transform exists for
                    SyncInterpreterToMachine();
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
