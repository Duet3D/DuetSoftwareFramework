using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
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
/// The object model is authoritative, as §1's first rule requires and as §14 step 4 concluded for
/// anything that is a flat list of values with no derived state. There is no second copy of a tool
/// here; this is the operations on <c>tools[]</c>, not a mirror of it
/// </para>
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="macroRunner">Runs the tfree/tpre/tpost macros a tool change is made of</param>
/// <param name="logger">Logger</param>
public sealed class ToolManager(Model.ObjectModel model, MacroRunner macroRunner, ILogger<ToolManager> logger)
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
    /// The tool that is currently selected, or null if none is
    /// </summary>
    /// <remarks>The caller must hold the object model lock</remarks>
    public Tool? Current => model.State.CurrentTool >= 0 ? Find(model.State.CurrentTool) : null;

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
            // Recorded but not driven: there is no Heat subsystem, so nothing sets a temperature on
            // these yet. Storing them is still right - §1's first rule - because a machine that
            // cannot be rebuilt from the object model has lost its configuration
            // TODO drive these once there is a Heat subsystem: M568 active/standby, M116, tool state
            tool.Heaters.Add(heater);
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
    public async ValueTask<string?> SelectAsync(DuetAPI.CodeChannel channel, int toolNumber,
                                                ToolChangeParameters parameters, Commands.Code? startCode,
                                                CancellationToken cancellationToken)
    {
        int previous;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            previous = model.State.CurrentTool;
            if (toolNumber != NoTool && Find(toolNumber) is null)
            {
                return $"Tool {toolNumber} not found";
            }
        }

        if (previous == toolNumber)
        {
            return null;                        // already selected, so there is nothing to change
        }

        if (previous != NoTool && parameters.HasFlag(ToolChangeParameters.RunFree))
        {
            await RunToolMacroAsync(channel, "tfree", previous, startCode, cancellationToken);
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(previous) is Tool old)
            {
                // TODO put the tool into standby rather than off once there is a Heat subsystem -
                // RepRapFirmware keeps its heaters at the standby temperature so it can be picked up
                // again without waiting for it to reheat
                old.State = ToolState.Off;
            }
        }

        if (toolNumber != NoTool && parameters.HasFlag(ToolChangeParameters.RunPre))
        {
            await RunToolMacroAsync(channel, "tpre", toolNumber, startCode, cancellationToken);
        }

        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            model.State.CurrentTool = toolNumber;
            if (Find(toolNumber) is Tool selected)
            {
                // TODO bring the heaters to the active temperature once there is a Heat subsystem
                selected.State = ToolState.Active;
            }
        }

        if (toolNumber != NoTool && parameters.HasFlag(ToolChangeParameters.RunPost))
        {
            await RunToolMacroAsync(channel, "tpost", toolNumber, startCode, cancellationToken);
        }
        return null;
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
