using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Tools;

namespace DuetControlServer.Codes.Handlers;

/// <summary>
/// The M-codes that define and configure a tool
/// </summary>
/// <remarks>
/// A tool is what a coordinate is measured to, so the codes here change the transform every later
/// move goes through. That is why they wait for standstill: an offset changing under a queued move
/// would mean the move was planned against one nozzle and executed with another
/// </remarks>
internal partial class MCodeHandler
{
    /// <summary>
    /// M563: define or delete a tool
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// RepRapFirmware's <c>ManageTool</c>. Redefining a tool replaces it, so a config.g that is re-run
    /// does not accumulate duplicates
    /// </remarks>
    private async ValueTask<Message> HandleDefineToolAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        if (!code.TryGetInt('P', out int toolNumber))
        {
            return await ReportToolsAsync(cancellationToken);
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            int axesCount = model.Move.Axes.Count;
            int eCount = model.Move.Extruders.Count;

            int[] extruders = code.GetIntArray('D', ToolManager.MaxExtrudersPerTool, defaultValue: [], min: 0, max: eCount - 1);
            ToolDefinition definition = new()
            {
                Number = toolNumber,
                Name = code.GetString('S', defaultValue: String.Empty),
                Extruders = extruders,
                Heaters = code.GetIntArray('H', Heat.HeatManager.MaxHeatersPerTool, defaultValue: [], min: 0, max: model.Heat.Heaters.Count - 1),
                Fans = code.GetIntArray('F', Fans.FanManager.MaxFans, defaultValue: [0], min: 0, max: model.Fans.Count - 1),
                XMap = code.GetIntArray('X', axesCount, defaultValue: [0], min: 0, max: axesCount - 1),
                YMap = code.GetIntArray('Y', axesCount, defaultValue: [1], min: 0, max: axesCount - 1),
                ZMap = code.GetIntArray('Z', axesCount, defaultValue: [2], min: 0, max: axesCount - 1),

                // RepRapFirmware defaults this to the tool's only drive when it has exactly one:
                // with several drives there is no single filament to speak of
                FilamentExtruder = code.GetInt('L', defaultValue: extruders.Length == 1 ? extruders[0] : -1, min: -1, max: eCount - 1),
                Spindle = code.GetInt('R', defaultValue: -1, min: -1, max: model.Spindles.Count - 1),
            };

            if (ShouldDeleteTool(definition))
            {
                toolManager.Remove(toolNumber);
            }
            else
            {
                string? error = toolManager.Define(definition);
                if (error is not null)
                {
                    return new Message(MessageType.Error, error);
                }
            }
        }

        return new Message();
    }

    private static bool ShouldDeleteTool(ToolDefinition definition)
    {
        return definition.Extruders.All(e => e == -1)
            && definition.Heaters.All(h => h == -1)
            && definition.Spindle == -1;
    }

    /// <summary>
    /// M567: set the mixing ratios of a tool
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// The ratios say how one E value is divided between a tool's drives, so a slicer commands the
    /// filament the nozzle consumes and the machine decides where it comes from. RepRapFirmware
    /// normalises them, so <c>E0.5:0.5</c> and <c>E1:1</c> mean the same thing
    /// </remarks>
    private async ValueTask<Message> HandleMixRatiosAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            Tool? tool = code.TryGetInt('P', out int toolNumber) ? toolManager.Find(toolNumber) : toolManager.Current;
            if (tool is null)
            {
                return new Message(MessageType.Error, "No tool selected");
            }

            // One ratio per drive of the tool, as RepRapFirmware reads it (GCodes2.cpp case 567,
            // eCount = tool->DriveCount())
            if (!code.TryGetFloatArray('E', tool.Extruders.Count, out float[]? ratios) || ratios.Length == 0)
            {
                StringBuilder report = new();
                report.Append(CultureInfo.InvariantCulture, $"Tool {tool.Number} mix ratios:");
                foreach (float ratio in tool.Mix)
                {
                    report.Append(CultureInfo.InvariantCulture, $" {ratio:F3}");
                }
                return new Message(MessageType.Success, report.ToString());
            }

            if (ratios.Length != tool.Extruders.Count)
            {
                return new Message(MessageType.Error,
                    $"Tool {tool.Number} has {tool.Extruders.Count} drives, so it needs that many mix ratios");
            }

            float total = 0.0f;
            foreach (float ratio in ratios)
            {
                if (ratio < 0.0f)
                {
                    return new Message(MessageType.Error, "Mix ratios cannot be negative");
                }
                total += ratio;
            }
            if (total <= 0.0f)
            {
                return new Message(MessageType.Error, "Mix ratios cannot all be zero");
            }

            tool.Mix.Clear();
            foreach (float ratio in ratios)
            {
                tool.Mix.Add(ratio / total);
            }
        }
        return new Message();
    }

    /// <summary>
    /// Report the tools the machine has, as M563 with no parameters does
    /// </summary>
    private async ValueTask<Message> ReportToolsAsync(CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            foreach (Tool? tool in model.Tools)
            {
                if (tool is null)
                {
                    continue;
                }

                builder.Append(CultureInfo.InvariantCulture, $"Tool {tool.Number}");
                if (!string.IsNullOrEmpty(tool.Name))
                {
                    builder.Append(CultureInfo.InvariantCulture, $" \"{tool.Name}\"");
                }
                AppendList(builder, " drives", tool.Extruders);
                AppendList(builder, " heaters", tool.Heaters);
                AppendOffsets(builder, tool, model);
                builder.AppendLine();
            }

            if (builder.Length == 0)
            {
                return new Message(MessageType.Success, "No tools are defined");
            }
        }
        return new Message(MessageType.Success, builder.ToString().TrimEnd());
    }

    private static void AppendList(StringBuilder builder, string label, IReadOnlyList<int> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        builder.Append(label).Append(':');
        foreach (int value in values)
        {
            builder.Append(CultureInfo.InvariantCulture, $" {value}");
        }
    }

    private static void AppendOffsets(StringBuilder builder, Tool tool, Model.ObjectModel model)
    {
        bool any = false;
        for (int axis = 0; axis < tool.Offsets.Count && axis < model.Move.Axes.Count; axis++)
        {
            if (tool.Offsets[axis] != 0.0f)
            {
                if (!any)
                {
                    builder.Append(" offsets:");
                    any = true;
                }
                builder.Append(CultureInfo.InvariantCulture,
                               $" {model.Move.Axes[axis].Letter}{tool.Offsets[axis]:F2}");
            }
        }
    }

    /// <summary>
    /// M568: set a tool's active and standby temperatures, its spindle speed, and whether it is on
    /// </summary>
    /// <remarks>
    /// The temperatures belong to the tool rather than to the heaters, which is the distinction M104
    /// cannot make: a tool carries what it should be at when it is in use and what it should idle at,
    /// and selecting or deselecting it is what applies one or the other
    /// </remarks>
    private async ValueTask<Message> HandleToolSettingsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        Tool? tool;
        ToolState state;
        bool apply = false;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            tool = code.TryGetInt('P', out int toolNumber) ? toolManager.Find(toolNumber) : toolManager.Current;
            if (tool is null)
            {
                return new Message(MessageType.Error, "No tool selected");
            }

            bool seen = false;
            int hCount = tool.Heaters.Count;
            // One temperature per heater of the tool, and a single value stands for all of them
            // (RRF GCodes.cpp SetOrReportToolTemperatures, hCount = tool->HeaterCount())
            if (code.TryGetFloatArray('S', hCount, out float[]? active, pad: true))
            {
                Assign(tool.Active, active);
                seen = true;
            }
            if (code.TryGetFloatArray('R', hCount, out float[]? standby, pad: true))
            {
                Assign(tool.Standby, standby);
                seen = true;
            }
            if (code.TryGetInt('F', out int spindleRpm))
            {
                tool.SpindleRpm = spindleRpm;
                seen = true;
            }
            if (code.TryGetEnum('A', out ToolState requested))
            {
                tool.State = requested;
                seen = true;
            }

            if (!seen)
            {
                return ReportToolSettings(tool);
            }

            state = tool.State;
            apply = tool.Heaters.Count > 0;
        }

        if (apply)
        {
            await heatManager.ApplyToolStateAsync(tool, state, cancellationToken);
        }

        // The spindle speed is applied when the spindle is started rather than now, which is what
        // lets M568 F set it up before an M3 that carries no S
        return new Message();
    }

    /// <summary>
    /// Copy values into a tool's per-heater list, leaving entries the code did not give
    /// </summary>
    private static void Assign(System.Collections.ObjectModel.ObservableCollection<float> target, float[] values)
    {
        for (int index = 0; index < values.Length; index++)
        {
            if (index < target.Count)
            {
                target[index] = values[index];
            }
            else
            {
                target.Add(values[index]);
            }
        }
    }

    /// <summary>
    /// Report a tool's settings, as M568 with only P does
    /// </summary>
    private static Message ReportToolSettings(Tool tool)
    {
        StringBuilder builder = new();
        builder.Append(CultureInfo.InvariantCulture, $"Tool {tool.Number} is {tool.State}");
        if (tool.Active.Count > 0)
        {
            builder.Append(", active temperatures:");
            foreach (float temperature in tool.Active)
            {
                builder.Append(CultureInfo.InvariantCulture, $" {temperature:F1}");
            }
        }
        if (tool.Standby.Count > 0)
        {
            builder.Append(", standby temperatures:");
            foreach (float temperature in tool.Standby)
            {
                builder.Append(CultureInfo.InvariantCulture, $" {temperature:F1}");
            }
        }
        return new Message(MessageType.Success, builder.ToString());
    }
}
