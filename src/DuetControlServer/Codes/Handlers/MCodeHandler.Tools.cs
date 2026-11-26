using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Link;
using DuetControlServer.Motion;
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
    /// The M563 parameters that define a tool, any of which makes the code a definition rather than a report
    /// </summary>
    /// <remarks>RepRapFirmware's <c>seen</c> in <c>GCodes::ManageTool</c>, which L alone does not set</remarks>
    internal const string ToolDefinitionLetters = "SDHXYZFR";

    /// <summary>
    /// M207: set or report the firmware retraction settings of the tool P names, or of every tool
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// RepRapFirmware's <c>Tool::SetFirmwareRetraction</c>. Every parameter is read before any tool changes, so a refused
    /// M207 changes nothing. None of them follows G20
    /// </remarks>
    private async ValueTask<Message> HandleFirmwareRetractionAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            List<Tool> tools;
            if (code.TryGetUInt('P', out uint? toolNumber))
            {
                if (toolManager.Find((int)Math.Min(toolNumber.Value, int.MaxValue)) is not Tool tool)
                {
                    return new Message(MessageType.Error, $"Tool {toolNumber} does not exist");
                }
                tools = [tool];
            }
            else
            {
                tools = [.. model.Tools.OfType<Tool>()];
            }

            bool seen = code.TryGetFloat('S', out float? length);
            seen |= code.TryGetFloat('R', out float? extraRestart);
            seen |= code.TryGetFloat('F', out float? speed);
            seen |= code.TryGetFloat('T', out float? unretractSpeed);
            seen |= code.TryGetFloat('Z', out float? zHop, min: 0.0f);
            if (!seen)
            {
                return ReportFirmwareRetraction(tools);
            }

            foreach (Tool tool in tools)
            {
                ToolRetraction retraction = tool.Retraction;
                if (length is float newLength)
                {
                    retraction.Length = Math.Max(newLength, 0.0f);
                }
                if (extraRestart is float newExtraRestart)
                {
                    // Against the length as it stands after S
                    retraction.ExtraRestart = Math.Max(newExtraRestart, -retraction.Length);
                }
                if (speed is float newSpeed)
                {
                    retraction.Speed = retraction.UnretractSpeed = Math.Max(newSpeed / SecondsPerMinute, ToolManager.MinRetractSpeed);
                }
                if (unretractSpeed is float newUnretractSpeed)
                {
                    retraction.UnretractSpeed = Math.Max(newUnretractSpeed / SecondsPerMinute, ToolManager.MinRetractSpeed);
                }
                if (zHop is float newZHop)
                {
                    retraction.ZHop = newZHop;
                }
            }
        }
        return new Message();
    }

    /// <summary>
    /// Report the firmware retraction settings of some tools, one line each, as M207 with nothing to set does
    /// </summary>
    private static Message ReportFirmwareRetraction(IEnumerable<Tool> tools)
    {
        StringBuilder builder = new();
        foreach (Tool tool in tools)
        {
            ToolRetraction retraction = tool.Retraction;
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }
            builder.Append(CultureInfo.InvariantCulture,
                $"Tool {tool.Number} retract/reprime: length {retraction.Length:F2}/{retraction.Length + retraction.ExtraRestart:F2}mm, speed {retraction.Speed:F1}/{retraction.UnretractSpeed:F1}mm/sec, Z hop {retraction.ZHop:F2}mm");
        }
        return new Message(MessageType.Success, builder.ToString());
    }

    /// <summary>
    /// M563: define, delete or report a tool
    /// </summary>
    /// <param name="code">The code</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result</returns>
    /// <remarks>
    /// RepRapFirmware's <c>GCodes::ManageTool</c>. A definition deletes the tool with that number first and builds the
    /// new one from exactly the parameters given. A refusal while reading the parameters keeps the old tool, while one
    /// from <c>Tool::Create</c>'s checks comes after the old tool is gone
    /// </remarks>
    private async ValueTask<Message> HandleDefineToolAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int toolNumber = code.GetInt('P', min: 0, max: ToolManager.MaxTools - 1);
        if (!code.Parameters.Any(parameter => ToolDefinitionLetters.Contains(parameter.Letter)))
        {
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                return (toolManager.Find(toolNumber) is Tool tool)
                    ? new Message(MessageType.Success, DescribeTool(tool))
                    : new Message(MessageType.Error, "Attempt to print details of non-existent tool");
            }
        }

        ToolDefinition definition;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            definition = ReadToolDefinition(code, toolNumber);
        }
        if (Overlaps(definition.XMap, definition.YMap) || Overlaps(definition.XMap, definition.ZMap) || Overlaps(definition.YMap, definition.ZMap))
        {
            return new Message(MessageType.Error, "Cannot map two or more of X,Y,Z to the same axis");
        }

        // Deleted before CreateAsync validates the definition, so a definition it refuses leaves no tool
        await toolManager.DeleteAsync(code.Channel, toolNumber, cancellationToken);
        if (!IsToolDeletion(definition) && await toolManager.CreateAsync(definition, cancellationToken) is string refusal)
        {
            return new Message(MessageType.Error, refusal);
        }
        return new Message();

        static bool Overlaps(IReadOnlyList<int> first, IReadOnlyList<int> second) => first.Any(second.Contains);
    }

    /// <summary>
    /// Read what an M563 definition asks for
    /// </summary>
    /// <remarks>
    /// As RepRapFirmware's <c>GCodes::ManageTool</c> reads them: X, Y and Z map onto visible axes and F onto fan numbers
    /// below <see cref="Fans.FanManager.MaxFans"/>, dropping any other number, and F-1 maps no fan. The caller must hold
    /// the object model lock
    /// </remarks>
    private ToolDefinition ReadToolDefinition(Commands.Code code, int toolNumber)
    {
        int numVisibleAxes = model.Move.Axes.Count(axis => axis.Visible);
        int[] ReadAxisMap(char letter)
        {
            if (code.TryGetUIntArray(letter, numVisibleAxes, out uint[]? axes))
            {
                return [.. axes.Where(axis => axis < numVisibleAxes).Select(axis => (int)axis).Distinct().Order()];
            }

            // By default each of X, Y and Z drives the axis carrying that letter, wherever M584 put it, and a letter the
            // machine does not have drives nothing
            int axisIndex = AxisIndices.IndexOf(model.Move, letter);
            return (axisIndex >= 0) ? [axisIndex] : [];
        }

        int[] extruders = code.GetIntArray('D', ToolManager.MaxExtrudersPerTool, defaultValue: []);
        return new ToolDefinition
        {
            Number = toolNumber,
            Name = code.GetString('S', defaultValue: string.Empty),
            Extruders = extruders,
            Heaters = code.GetIntArray('H', Heat.HeatManager.MaxHeatersPerTool, defaultValue: []),
            Fans = code.TryGetIntArray('F', Fans.FanManager.MaxFans, out int[]? fans)
                ? [.. fans.Where(fan => fan >= 0 && fan < Fans.FanManager.MaxFans).Distinct().Order()]
                : [0],
            XMap = ReadAxisMap('X'),
            YMap = ReadAxisMap('Y'),
            ZMap = ReadAxisMap('Z'),
            FilamentExtruder = code.GetInt('L', defaultValue: (extruders.Length == 1) ? extruders[0] : -1),
            Spindle = code.GetInt('R', defaultValue: -1, min: -1, max: Spindles.SpindleManager.MaxSpindles),
        };
    }

    /// <summary>
    /// Whether an M563 definition is <c>M563 P# D-1 H-1 [R-1]</c>, which deletes the tool and creates none
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>GCodes::ManageTool</c> tests for exactly one drive and exactly one heater, both of them -1,
    /// with no spindle. A -1 anywhere else is a bad drive or heater number
    /// </remarks>
    private static bool IsToolDeletion(ToolDefinition definition)
        => definition.Extruders is [-1] && definition.Heaters is [-1] && definition.Spindle == -1;

    /// <summary>
    /// Describe a tool, as M563 with only P does
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Tool::PrintTool</c>. The caller must hold the object model lock</remarks>
    private string DescribeTool(Tool tool)
    {
        StringBuilder builder = new();
        builder.Append(CultureInfo.InvariantCulture, $"Tool {tool.Number} - ");
        if (!string.IsNullOrEmpty(tool.Name))
        {
            builder.Append(CultureInfo.InvariantCulture, $"name: {tool.Name}; ");
        }

        builder.Append((tool.Extruders.Count == 0) ? "no drives" : "drives: " + string.Join(',', tool.Extruders));

        if (tool.Heaters.Count == 0)
        {
            builder.Append("; no heaters");
        }
        else
        {
            builder.Append("; heaters (active/standby temps): ");
            builder.AppendJoin(',', tool.Heaters.Select((heaterNumber, index) => string.Create(CultureInfo.InvariantCulture,
                $"{heaterNumber} ({TemperatureAt(tool.Active, index):F1}/{TemperatureAt(tool.Standby, index):F1})")));
        }

        string[] mapNames = ["xmap", "ymap", "zmap"];
        for (int map = 0; map < mapNames.Length; map++)
        {
            IEnumerable<int> axes = (map < tool.Axes.Count) ? tool.Axes[map] : [];
            builder.Append(CultureInfo.InvariantCulture, $"; {mapNames[map]}: ");
            builder.AppendJoin(',', axes.Where(axis => axis < model.Move.Axes.Count).Select(axis => model.Move.Axes[axis].Letter));
        }

        builder.Append("; fans: ").AppendJoin(',', tool.Fans);
        builder.Append((tool.Spindle == -1) ? "; no spindle" : string.Create(CultureInfo.InvariantCulture, $"; spindle: {tool.Spindle}@{tool.SpindleRpm}RPM"));
        builder.Append("; status: ").Append(tool.State switch
        {
            ToolState.Active => "selected",
            ToolState.Standby => "standby",
            _ => "off"
        });
        return builder.ToString();

        static float TemperatureAt(IList<float> temperatures, int index) => (index < temperatures.Count) ? temperatures[index] : 0.0f;
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
            Tool tool = toolManager.GetSpecifiedOrCurrent(code);

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
    /// M568: set a tool's active and standby temperatures, its spindle speed, and whether it is on
    /// </summary>
    /// <remarks>
    /// The temperatures belong to the tool rather than to the heaters, which is the distinction M104
    /// cannot make: a tool carries what it should be at when it is in use and what it should idle at,
    /// and selecting or deselecting it is what applies one or the other
    /// </remarks>
    private async ValueTask<Message> HandleToolSettingsAsync(Commands.Code code, CancellationToken cancellationToken)
    {
        int toolNumber;
        float[]? active = null, standby = null;
        uint? spindleRpm = null;
        ToolHeaterState? heaterState = null;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            Tool tool = toolManager.GetSpecifiedOrCurrent(code);
            toolNumber = tool.Number;

            bool seen = false;
            int hCount = tool.Heaters.Count;
            if (hCount > 0)
            {
                // One temperature per heater of the tool, and a single value stands for all of them
                // (RRF GCodes.cpp SetOrReportOffsets, hCount = tool->HeaterCount())
                seen |= code.TryGetFloatArray('R', hCount, out standby, pad: true);
                seen |= code.TryGetFloatArray('S', hCount, out active, pad: true);
            }
            if (tool.Spindle > -1 && code.TryGetUInt('F', out uint rpm))
            {
                spindleRpm = rpm;
                seen = true;
            }
            if (code.TryGetEnum('A', out ToolHeaterState requested))
            {
                heaterState = requested;
                seen = true;
            }

            if (!seen)
            {
                return ReportToolSettings(tool);
            }
        }

        for (int index = 0; standby is not null && index < standby.Length; index++)
        {
            Message reply = await toolManager.SetToolHeaterTemperatureAsync(toolNumber, index, standby[index], false, cancellationToken);
            if (!reply.Succeeded())
            {
                return reply;
            }
        }
        for (int index = 0; active is not null && index < active.Length; index++)
        {
            Message reply = await toolManager.SetToolHeaterTemperatureAsync(toolNumber, index, active[index], true, cancellationToken);
            if (!reply.Succeeded())
            {
                return reply;
            }
        }

        if (spindleRpm is uint rpmToSet)
        {
            Message reply = await toolManager.SetSpindleRpmAsync(toolNumber, (int)Math.Min(rpmToSet, int.MaxValue), cancellationToken);
            if (!reply.Succeeded())
            {
                return reply;
            }
        }

        // A sets the heaters, not the tool's state (RRF GCodes.cpp SetOrReportOffsets)
        switch (heaterState)
        {
            case ToolHeaterState.Off:
                await toolManager.HeatersToOffAsync(toolNumber, cancellationToken);
                break;
            case ToolHeaterState.Standby:
                await toolManager.HeatersToActiveOrStandbyAsync(toolNumber, false, cancellationToken);
                break;
            case ToolHeaterState.Active:
                await toolManager.HeatersToActiveOrStandbyAsync(toolNumber, true, cancellationToken);
                break;
        }
        return new Message();
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
        if (tool.Spindle > -1)
        {
            builder.Append(CultureInfo.InvariantCulture, $", spindle {tool.Spindle}@{tool.SpindleRpm}rpm");
        }
        return new Message(MessageType.Success, builder.ToString());
    }
}
