using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Link;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Tools;

/// <summary>
/// The tools a machine has, and which one is selected
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>Tool</c> and the tool half of <c>GCodes</c>. A tool is the thing a
/// coordinate is measured to: it collects a set of extruders, heaters and fans under one number, and
/// carries the offsets between where the machine thinks it is and where <em>that</em> nozzle is. So
/// nothing else in the move pipeline needs to know what a tool is - it asks for the offsets and the
/// axis mapping and gets numbers.
/// </para>
/// <para>
/// The object model is authoritative, as it is for anything that is a flat list of values with no
/// derived state. There is no second copy of a tool here; this is the operations on <c>tools[]</c>,
/// not a mirror of it
/// </para>
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="macroRunner">Runs the tfree/tpre/tpost macros a tool change is made of</param>
/// <param name="heatManager">Brings a tool's heaters to its active or standby temperatures</param>
/// <param name="eventLogger">Writes out what a heater said when a tool changed its state</param>
/// <param name="logger">Logger</param>
public sealed class ToolManager(Model.ObjectModel model, MacroRunner macroRunner,
                                Heat.HeatManager heatManager, Utility.EventLogger eventLogger, ILogger<ToolManager> logger)
{
    /// <summary>
    /// Highest tool number a machine may have
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MaxTools</c>, which exists to bound the serialised object model</remarks>
    public const int MaxTools = 50;

    /// <summary>
    /// Extruders one tool may drive
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MaxExtrudersPerTool</c> for a Duet 3 MB6HC</remarks>
    public const int MaxExtrudersPerTool = 12;

    /// <summary>
    /// Meaning "no tool", as <c>state.currentTool</c> uses it
    /// </summary>
    public const int NoTool = -1;

    /// <summary>
    /// Find a tool by number
    /// </summary>
    /// <param name="toolNumber">The number</param>
    /// <returns>The tool, or null if there is none with that number</returns>
    /// <remarks>
    /// The collection is indexed by position rather than by tool number - a machine may define tools
    /// 0 and 5 and nothing between - so this is a search rather than an index. The caller must hold
    /// the object model lock
    /// </remarks>
    public Tool? Find(int toolNumber)
    {
        foreach (Tool? tool in model.Tools)
        {
            if (tool is not null && tool.Number == toolNumber)
            {
                return tool;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether any tool uses a heater
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Tool::IsHeaterAssignedToTool</c>. The caller must hold the object model lock</remarks>
    public bool IsHeaterAssignedToTool(int heaterNumber)
    {
        foreach (Tool? tool in model.Tools)
        {
            if (tool is not null && tool.Heaters.Contains(heaterNumber))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The tool that is currently selected, or null if none is
    /// </summary>
    /// <remarks>The caller must hold the object model lock</remarks>
    public Tool? Current => model.State.CurrentTool >= 0 ? Find(model.State.CurrentTool) : null;

    /// <summary>
    /// The tool that is currently selected, or else the lowest-numbered tool, or null if there are no tools
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>MovementState::GetLockedCurrentOrDefaultTool</c>, which M104 and M109 address without T. The
    /// caller must hold the object model lock
    /// </remarks>
    public Tool? CurrentOrDefault
        => Current ?? model.Tools.Where(tool => tool is not null).MinBy(tool => tool!.Number);

    /// <summary>
    /// Check if the specified heater is used by a current tool other than the specified one.
    /// </summary>
    /// <remarks>RepRapFirmware's <c>GCodes::IsHeaterUsedByDifferentCurrentTool</c>. The caller must hold the object model lock</remarks>
    public bool IsHeaterUsedByDifferentCurrentTool(int heaterNumber, int toolNumber)
        => Current is Tool current && current.Number != toolNumber && current.Heaters.Contains(heaterNumber);

    /// <summary>
    /// Set a tool's active or standby temperature for one of its heaters, and the heater's too unless another tool that
    /// is selected uses it
    /// </summary>
    /// <param name="toolNumber">The tool, which need not exist</param>
    /// <param name="heaterIndex">Index of the heater in the tool</param>
    /// <param name="temperature">The temperature, which at or below -273 switches the heater off</param>
    /// <param name="active">True for the active temperature, false for the standby temperature</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An error if the temperature is outside the heater's limits or the heater refused it</returns>
    /// <remarks>
    /// RepRapFirmware's <c>Tool::SetToolHeaterActiveOrStandbyTemperature</c>. The standby temperature also reaches a heater
    /// that another selected tool uses when this tool, or no tool, last put it on standby. The caller must not hold the
    /// object model lock
    /// </remarks>
    public async ValueTask<Message> SetToolHeaterTemperatureAsync(int toolNumber, int heaterIndex, float temperature, bool active,
                                                                  CancellationToken cancellationToken)
    {
        int heaterNumber;
        bool setHeater;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(toolNumber) is not Tool tool || heaterIndex >= tool.Heaters.Count)
            {
                return new Message();
            }
            heaterNumber = tool.Heaters[heaterIndex];
            int lastStandbyTool = heatManager.GetLastStandbyTool(heaterNumber);
            setHeater = !IsHeaterUsedByDifferentCurrentTool(heaterNumber, toolNumber)
                        || (!active && (lastStandbyTool == NoTool || lastStandbyTool == toolNumber));

            if (temperature > Heat.HeatManager.NearlyAbsoluteZero)
            {
                Heater? heater = heatManager.Find(heaterNumber);
                float lowest = (heater is null) ? Heat.HeatManager.AbsoluteZero : Heat.HeatManager.GetLowestTemperatureLimit(heater);
                float highest = (heater is null) ? Heat.HeatManager.BadErrorTemperature : Heat.HeatManager.GetHighestTemperatureLimit(heater);
                if (temperature <= lowest || temperature >= highest)
                {
                    return new Message(MessageType.Error, "Requested temperature out of range");
                }
            }
            SetToolTemperature(active ? tool.Active : tool.Standby, heaterIndex,
                               (temperature <= Heat.HeatManager.NearlyAbsoluteZero) ? 0.0f : temperature);
        }

        if (!setHeater)
        {
            return new Message();
        }
        if (temperature <= Heat.HeatManager.NearlyAbsoluteZero)
        {
            await heatManager.SwitchOffAsync(heaterNumber, cancellationToken);
            return new Message();
        }
        return await heatManager.SetActiveOrStandbyTemperatureAsync(heaterNumber, temperature, active, cancellationToken);
    }

    /// <summary>
    /// Store one of a tool's per-heater temperatures, growing the list to hold it
    /// </summary>
    private static void SetToolTemperature(IList<float> temperatures, int heaterIndex, float temperature)
    {
        while (temperatures.Count <= heaterIndex)
        {
            temperatures.Add(0.0f);
        }
        temperatures[heaterIndex] = temperature;
    }

    /// <summary>
    /// Switch a tool's heaters to its active or standby temperatures, except any that another selected tool uses
    /// </summary>
    /// <param name="toolNumber">The tool, which need not exist</param>
    /// <param name="active">True for the active temperatures, false for the standby temperatures</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// RepRapFirmware's <c>Tool::HeatersToActiveOrStandby</c>, which reports what a heater refused as a message of its own.
    /// The caller must not hold the object model lock
    /// </remarks>
    public async ValueTask HeatersToActiveOrStandbyAsync(int toolNumber, bool active, CancellationToken cancellationToken)
    {
        List<(int Heater, float Temperature)> heaters = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (Find(toolNumber) is not Tool tool)
            {
                return;
            }
            IList<float> temperatures = active ? tool.Active : tool.Standby;
            for (int index = 0; index < tool.Heaters.Count; index++)
            {
                int heaterNumber = tool.Heaters[index];
                if (!IsHeaterUsedByDifferentCurrentTool(heaterNumber, toolNumber))
                {
                    heaters.Add((heaterNumber, (index < temperatures.Count) ? temperatures[index] : 0.0f));
                }
            }
        }

        foreach ((int heaterNumber, float temperature) in heaters)
        {
            Message reply = await heatManager.SetActiveOrStandbyTemperatureAsync(heaterNumber, temperature, active, cancellationToken);
            if (reply.Succeeded())
            {
                reply = await heatManager.SetActiveOrStandbyAsync(heaterNumber, active, cancellationToken, toolNumber);
            }
            if (reply.Type != MessageType.Success)
            {
                await eventLogger.LogOutputAsync(reply, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Switch a tool's heaters off, except any that another selected tool uses
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Tool::HeatersToOff</c>. The caller must not hold the object model lock</remarks>
    public async ValueTask HeatersToOffAsync(int toolNumber, CancellationToken cancellationToken)
    {
        List<int> heaters = [];
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (Find(toolNumber) is Tool tool)
            {
                heaters.AddRange(tool.Heaters.Where(heaterNumber => !IsHeaterUsedByDifferentCurrentTool(heaterNumber, toolNumber)));
            }
        }
        foreach (int heaterNumber in heaters)
        {
            await heatManager.SwitchOffAsync(heaterNumber, cancellationToken);
        }
    }

    /// <summary>
    /// Switch a tool's heaters to their active temperatures and make the tool active
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>Tool::Activate</c>. The tool must already be the selected tool. The caller must not hold the
    /// object model lock
    /// </remarks>
    public async ValueTask ActivateAsync(int toolNumber, CancellationToken cancellationToken)
    {
        // TODO RRF's Tool::Activate also stops the tool's spindle and restores its configured RPM
        await HeatersToActiveOrStandbyAsync(toolNumber, true, cancellationToken);
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(toolNumber) is Tool tool)
            {
                tool.State = ToolState.Active;
            }
        }
    }

    /// <summary>
    /// Switch a tool's heaters to their standby temperatures and put the tool on standby
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Tool::Standby</c>. The caller must not hold the object model lock</remarks>
    public async ValueTask StandbyAsync(int toolNumber, CancellationToken cancellationToken)
    {
        // TODO RRF's Tool::Standby also stops the tool's spindle
        await HeatersToActiveOrStandbyAsync(toolNumber, false, cancellationToken);
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(toolNumber) is Tool tool)
            {
                tool.State = ToolState.Standby;
            }
        }
    }

    /// <summary>
    /// Define or redefine a tool (M563)
    /// </summary>
    /// <param name="definition">What the code asked for</param>
    /// <returns>An error if the tool could not be defined, else null</returns>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>ManageTool</c>. Redefining a tool deletes the old one first, so a config.g
    /// that is re-run does not accumulate duplicates, and a definition naming no drives, heaters or
    /// mappings deletes the tool outright - which is how <c>M563 P0</c> with nothing else removes it.
    /// </para>
    /// <para>
    /// The caller must hold the object model write lock and must have brought the machine to
    /// standstill: the offsets a tool carries are part of the transform every queued move was planned
    /// against, so changing them under a queued move moves the machine
    /// </para>
    /// </remarks>
    public string? Define(ToolDefinition definition)
    {
        if (definition.Number < 0 || definition.Number >= MaxTools)
        {
            return $"Tool number must be between 0 and {MaxTools - 1}";
        }

        // Two of X, Y and Z mapped to the same axis would make the transform ambiguous: a coordinate
        // in one would have to be two different machine positions at once
        if (Overlaps(definition.XMap, definition.YMap) || Overlaps(definition.XMap, definition.ZMap)
            || Overlaps(definition.YMap, definition.ZMap))
        {
            return "Cannot map two or more of X,Y,Z to the same axis";
        }

        foreach (int extruder in definition.Extruders)
        {
            if (extruder < 0 || extruder >= model.Move.Extruders.Count)
            {
                return $"Extruder {extruder} does not exist";
            }
        }
        if (definition.Extruders.Count > MaxExtrudersPerTool)
        {
            return $"A tool may drive at most {MaxExtrudersPerTool} extruders";
        }

        foreach (int heater in definition.Heaters)
        {
            if (heater < 0 || heater >= model.Heat.Heaters.Count)
            {
                return $"Heater {heater} does not exist";
            }
        }

        Remove(definition.Number);

        Tool tool = new()
        {
            Number = definition.Number,
            Name = definition.Name ?? string.Empty,
            FilamentExtruder = definition.FilamentExtruder,
            Spindle = definition.Spindle,
            State = ToolState.Off
        };

        foreach (int extruder in definition.Extruders)
        {
            tool.Extruders.Add(extruder);
        }
        foreach (int heater in definition.Heaters)
        {
            tool.Heaters.Add(heater);

            // One active and one standby temperature per heater, starting at absolute zero (RRF Tool::Create)
            tool.Active.Add(Heat.HeatManager.AbsoluteZero);
            tool.Standby.Add(Heat.HeatManager.AbsoluteZero);
        }
        foreach (int fan in definition.Fans)
        {
            // TODO drive these once there is a Fan subsystem: M106 addresses a tool's fans
            tool.Fans.Add(fan);
        }

        // The axis maps in the order the object model documents, which is the order of the visible
        // axes: X, then Y, then Z
        tool.Axes.Add([.. definition.XMap]);
        tool.Axes.Add([.. definition.YMap]);
        tool.Axes.Add([.. definition.ZMap]);

        // One offset per axis, so that the transform can index it by axis without a bounds check on
        // every move
        for (int axis = 0; axis < model.Move.Axes.Count; axis++)
        {
            tool.Offsets.Add(0.0f);
        }

        // An even mix by default, which is what a single-extruder tool needs and what RepRapFirmware
        // gives a multi-extruder one until M567 says otherwise
        float share = definition.Extruders.Count > 0 ? 1.0f / definition.Extruders.Count : 0.0f;
        foreach (int _ in definition.Extruders)
        {
            tool.Mix.Add(share);
        }

        // Kept in tool-number order so that a client listing them gets them in the order the operator
        // thinks of them, rather than in definition order
        int position = 0;
        while (position < model.Tools.Count && model.Tools[position] is Tool existing && existing.Number < tool.Number)
        {
            position++;
        }
        model.Tools.Insert(position, tool);
        return null;
    }

    /// <summary>
    /// Delete a tool, deselecting it first if it is the one in use
    /// </summary>
    /// <param name="toolNumber">The number</param>
    /// <returns>True if there was such a tool</returns>
    /// <remarks>The caller must hold the object model write lock</remarks>
    public bool Remove(int toolNumber)
    {
        Tool? tool = Find(toolNumber);
        if (tool is null)
        {
            return false;
        }

        if (model.State.CurrentTool == toolNumber)
        {
            // Deselected without running tfree.g: the tool is being removed rather than put down, and
            // a macro that addressed it would be addressing something that no longer exists
            model.State.CurrentTool = NoTool;
        }
        model.Tools.Remove(tool);
        return true;
    }

    /// <summary>
    /// Select a tool, running the macros a tool change is made of
    /// </summary>
    /// <param name="channel">Channel the T-code came from</param>
    /// <param name="toolNumber">Tool to select, or <see cref="NoTool"/> to select none</param>
    /// <param name="parameters">Which of the three macros to run</param>
    /// <param name="startCode">The T-code, so the macros inherit its state</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An error if the change could not be made, else null</returns>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's tool change sequence: <c>tfree&lt;n&gt;.g</c> for the tool being put down,
    /// then <c>tpre&lt;n&gt;.g</c> and <c>tpost&lt;n&gt;.g</c> for the one being picked up. The
    /// machine's own macros are what know how to change a tool - park the old one, pick up the new
    /// one, purge it - and this only sequences them.
    /// </para>
    /// <para>
    /// The selection itself happens between tpre and tpost, as in RepRapFirmware, so that tpre runs
    /// with the old tool's offsets and tpost with the new one's. A tpost that primes the nozzle has
    /// to be able to move to a coordinate measured against the tool it is priming
    /// </para>
    /// </remarks>
    public async ValueTask<Message> SelectAsync(DuetAPI.CodeChannel channel, int toolNumber,
                                                ToolChangeParameters parameters, Commands.Code? startCode,
                                                CancellationToken cancellationToken)
    {
        int previous;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            previous = model.State.CurrentTool;
            if (toolNumber != NoTool && Find(toolNumber) is null)
            {
                return new Message(MessageType.Error, $"Tool {toolNumber} not found");
            }
        }

        if (previous == toolNumber)
        {
            return new Message();                        // already selected, so there is nothing to change
        }

        if (previous != NoTool && parameters.HasFlag(ToolChangeParameters.RunFree))
        {
            await RunToolMacroAsync(channel, "tfree", previous, startCode, cancellationToken);
        }

        if (previous != NoTool)
        {
            // Standby rather than off, so the tool can be picked up again without waiting for it to
            // reheat. That is the whole reason a tool has two temperatures
            await StandbyAsync(previous, cancellationToken);
        }

        if (toolNumber != NoTool && parameters.HasFlag(ToolChangeParameters.RunPre))
        {
            await RunToolMacroAsync(channel, "tpre", toolNumber, startCode, cancellationToken);
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            model.State.CurrentTool = toolNumber;
        }

        if (toolNumber != NoTool)
        {
            await ActivateAsync(toolNumber, cancellationToken);
        }

        if (toolNumber != NoTool && parameters.HasFlag(ToolChangeParameters.RunPost))
        {
            await RunToolMacroAsync(channel, "tpost", toolNumber, startCode, cancellationToken);
        }
        return new Message();
    }

    /// <summary>
    /// Run one of the tool change macros, if the machine has it
    /// </summary>
    /// <remarks>
    /// A missing tool macro is not an error, as it is not in RepRapFirmware: a machine with one tool
    /// and nothing to park has no use for tfree.g, and requiring an empty file would be a trap
    /// </remarks>
    private async ValueTask RunToolMacroAsync(DuetAPI.CodeChannel channel, string prefix, int toolNumber,
                                              Commands.Code? startCode, CancellationToken cancellationToken)
    {
        string fileName = $"{prefix}{toolNumber}.g";
        if (!await macroRunner.TryRunAsync(channel, fileName, startCode, cancellationToken: cancellationToken))
        {
            logger.LogDebug("No {File} for tool {Tool}", fileName, toolNumber);
        }
    }

    private static bool Overlaps(IReadOnlyList<int> first, IReadOnlyList<int> second)
        => first.Any(second.Contains);
}
