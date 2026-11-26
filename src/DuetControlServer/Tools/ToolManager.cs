using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Model;
using DuetControlServer.Motion.Native;
using DuetAPI;
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
/// <param name="spindleManager">Drives a tool's spindle</param>
/// <param name="eventLogger">Writes out what a heater said when a tool changed its state</param>
/// <param name="logger">Logger</param>
public sealed class ToolManager(Model.ObjectModel model, MacroRunner macroRunner, Heat.HeatManager heatManager,
                                Spindles.SpindleManager spindleManager, Utility.EventLogger eventLogger, ILogger<ToolManager> logger)
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
    /// Firmware retraction length a new tool starts with, in mm
    /// </summary>
    /// <remarks>RepRapFirmware's <c>DefaultRetractLength</c></remarks>
    public const float DefaultRetractLength = 2.0f;

    /// <summary>
    /// Firmware retraction and unretraction speed a new tool starts with, in mm/s
    /// </summary>
    /// <remarks>RepRapFirmware's <c>DefaultRetractSpeed</c>, which is 1000 mm/min</remarks>
    public const float DefaultRetractSpeed = 1000.0f / 60.0f;

    /// <summary>
    /// Lowest firmware retraction and unretraction speed, in mm/s
    /// </summary>
    /// <remarks>RepRapFirmware's <c>MinRetractSpeed</c>, which is 60 mm/min</remarks>
    public const float MinRetractSpeed = 1.0f;

    /// <summary>
    /// Find a tool by number
    /// </summary>
    /// <param name="toolNumber">The number</param>
    /// <returns>The tool, or null if there is none with that number</returns>
    /// <remarks>
    /// <c>tools[]</c> is indexed by tool number, with null where no tool has the number, as RepRapFirmware reports it.
    /// The caller must hold the object model lock
    /// </remarks>
    public Tool? Find(int toolNumber) => model.Tools.GetAt(toolNumber);

    /// <summary>
    /// The tool a code addresses: the one P names, or else the current tool
    /// </summary>
    /// <param name="code">The code</param>
    /// <returns>The tool</returns>
    /// <exception cref="GCodeException">There is no tool to address</exception>
    /// <remarks>RepRapFirmware's <c>GCodes::GetSpecifiedOrCurrentTool</c>. The caller must hold the object model lock</remarks>
    public Tool GetSpecifiedOrCurrent(Commands.Code code)
    {
        int toolNumber;
        if (code.TryGetUInt('P', out uint given))
        {
            toolNumber = (int)Math.Min(given, int.MaxValue);
        }
        else
        {
            toolNumber = model.State.CurrentTool;
            if (toolNumber < 0)
            {
                throw new GCodeException("No tool number given and no current tool");
            }
        }
        return Find(toolNumber) ?? throw new GCodeException("Invalid tool number");
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
        => Current ?? model.Tools.FirstOrDefault(tool => tool is not null);

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
    /// RepRapFirmware's <c>Tool::Activate</c>, which also stops the tool's spindle and gives it the tool's RPM to start
    /// at. The tool must already be the selected tool. The caller must not hold the object model lock
    /// </remarks>
    public async ValueTask ActivateAsync(int toolNumber, CancellationToken cancellationToken)
    {
        await HeatersToActiveOrStandbyAsync(toolNumber, true, cancellationToken);
        await StopSpindleAsync(toolNumber, restoreToolRpm: true, cancellationToken);
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
    /// <remarks>
    /// RepRapFirmware's <c>Tool::Standby</c>, which also stops the tool's spindle. The caller must not hold the object
    /// model lock
    /// </remarks>
    public async ValueTask StandbyAsync(int toolNumber, CancellationToken cancellationToken)
    {
        await HeatersToActiveOrStandbyAsync(toolNumber, false, cancellationToken);
        await StopSpindleAsync(toolNumber, restoreToolRpm: false, cancellationToken);
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(toolNumber) is Tool tool)
            {
                tool.State = ToolState.Standby;
            }
        }
    }

    /// <summary>
    /// Stop a tool's spindle, if it has one, as a tool change does
    /// </summary>
    /// <param name="toolNumber">The tool</param>
    /// <param name="restoreToolRpm">Whether the spindle then takes the tool's RPM as the speed it starts at</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// <c>Tool::Activate</c> and <c>Tool::Standby</c>, following the NIST M6 rule that the spindle is stopped once a
    /// tool change is complete. What the spindle's boards say is reported rather than failing the tool change, as a
    /// heater's is. The caller must not hold the object model lock
    /// </remarks>
    private async ValueTask StopSpindleAsync(int toolNumber, bool restoreToolRpm, CancellationToken cancellationToken)
    {
        int spindleNumber, toolRpm;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (Find(toolNumber) is not Tool { Spindle: >= 0 } tool)
            {
                return;
            }
            spindleNumber = tool.Spindle;
            toolRpm = tool.SpindleRpm;
        }

        Message reply = await spindleManager.StopAsync(spindleNumber, cancellationToken);
        if (reply.Succeeded() && restoreToolRpm)
        {
            reply = await spindleManager.SetConfiguredRpmAsync(spindleNumber, toolRpm, updateCurrentRpm: false, cancellationToken);
        }
        if (reply.Type != MessageType.Success)
        {
            await eventLogger.LogOutputAsync(reply, cancellationToken);
        }
    }

    /// <summary>
    /// Check a tool definition, as RepRapFirmware's <c>Tool::Create</c> does before it builds the tool
    /// </summary>
    /// <param name="definition">What M563 asked for</param>
    /// <returns>Why the tool cannot be created, or null if it can</returns>
    /// <remarks>
    /// Heaters and fans are only range-checked, so a tool may name ones config.g has not created yet. The caller must
    /// hold the object model lock
    /// </remarks>
    public string? Validate(ToolDefinition definition)
    {
        foreach (int extruder in definition.Extruders)
        {
            if (extruder < 0 || extruder >= model.Move.Extruders.Count)
            {
                return "bad drive number";
            }
        }

        foreach (int heaterNumber in definition.Heaters)
        {
            if (heaterNumber < 0 || heaterNumber >= Heat.HeatManager.MaxHeaters)
            {
                return "bad heater number";
            }
            HeaterFunction function = heatManager.GetHeaterFunction(heaterNumber);
            if (function is HeaterFunction.Bed or HeaterFunction.Chamber)
            {
                return $"heater {heaterNumber} is already assigned as a {((function == HeaterFunction.Bed) ? "bed" : "chamber")} heater";
            }
        }

        if (definition.Spindle > -1)
        {
            if (definition.Spindle >= Spindles.SpindleManager.MaxSpindles)
            {
                return "bad spindle number";
            }
            if (spindleManager.Find(definition.Spindle) is null)
            {
                return "unconfigured spindle";
            }
        }
        return null;
    }

    /// <summary>
    /// Create a tool and add it to <c>tools[]</c>, giving its heaters the tool function
    /// </summary>
    /// <param name="definition">What M563 asked for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Why the tool was refused, or null if it was created</returns>
    /// <remarks>
    /// RepRapFirmware's <c>Tool::Create</c> and <c>Tool::AddTool</c>. Any tool with the same number must already have
    /// been deleted. The caller must not hold the object model lock, and must have brought the machine to standstill
    /// </remarks>
    public async ValueTask<string?> CreateAsync(ToolDefinition definition, CancellationToken cancellationToken)
    {
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Validate(definition) is string refusal)
            {
                return refusal;
            }

            Tool tool = new()
            {
                Number = definition.Number,
                Name = definition.Name ?? string.Empty,
                FilamentExtruder = (definition.FilamentExtruder >= 0 && definition.FilamentExtruder < MotionLimits.MaxExtruders)
                                   ? definition.FilamentExtruder : -1,
                Spindle = definition.Spindle,
                SpindleRpm = 0,
                State = ToolState.Off
            };
            tool.Retraction.Length = DefaultRetractLength;
            tool.Retraction.Speed = tool.Retraction.UnretractSpeed = DefaultRetractSpeed;

            // The initial mix ratio is 1:0:0
            for (int index = 0; index < definition.Extruders.Count; index++)
            {
                tool.Extruders.Add(definition.Extruders[index]);
                tool.Mix.Add((index == 0) ? 1.0f : 0.0f);
            }
            foreach (int heaterNumber in definition.Heaters)
            {
                tool.Heaters.Add(heaterNumber);
                tool.Active.Add(Heat.HeatManager.AbsoluteZero);
                tool.Standby.Add(Heat.HeatManager.AbsoluteZero);
                tool.FeedForwardPwm.Add(0.0f);
                tool.FeedForwardTemp.Add(0.0f);
            }
            foreach (int fan in definition.Fans)
            {
                tool.Fans.Add(fan);
            }

            // The axis maps in the order the object model documents: X, then Y, then Z
            tool.Axes.Add([.. definition.XMap]);
            tool.Axes.Add([.. definition.YMap]);
            tool.Axes.Add([.. definition.ZMap]);

            // One offset per axis, so that the transform can index it by axis without a bounds check on every move
            for (int axis = 0; axis < model.Move.Axes.Count; axis++)
            {
                tool.Offsets.Add(0.0f);
            }

            // TODO RRF's Tool::Create also gives the filament extruder the filament loaded in it, from the Filament
            // instance shared by every tool on that extruder (filaments.csv); that needs the filament subsystem (M701-M703)
            model.Tools.SetAt(definition.Number, tool);
        }

        // Heat::SetAsToolHeater
        foreach (int heaterNumber in definition.Heaters)
        {
            await heatManager.SetFunctionAsync(heaterNumber, HeaterFunction.Tool, cancellationToken);
        }
        return null;
    }

    /// <summary>
    /// Delete a tool, deselecting it first if it is the current tool and then switching its heaters off
    /// </summary>
    /// <param name="channel">Channel of the code deleting the tool</param>
    /// <param name="toolNumber">The tool, which need not exist</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>GCodes::ManageTool</c>, which deselects the tool without running any tool change macro
    /// (<c>MovementState::SelectTool(-1)</c>, which puts it on standby), and <c>Tool::DeleteTool</c>, which switches
    /// off every heater of the tool. <c>tools[]</c> then ends at the highest-numbered tool left.
    /// </para>
    /// <para>
    /// The caller must not hold the object model lock, and must have brought the machine to standstill: the offsets a
    /// tool carries are part of the transform every queued move was planned against
    /// </para>
    /// </remarks>
    public async ValueTask DeleteAsync(DuetAPI.CodeChannel channel, int toolNumber, CancellationToken cancellationToken)
    {
        bool selected;
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            selected = model.State.CurrentTool == toolNumber;
        }
        if (selected)
        {
            await SelectAsync(channel, NoTool, ToolChangeParameters.None, null, cancellationToken);
        }

        List<int> heaters = [];
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(toolNumber) is not Tool tool)
            {
                return;
            }
            heaters.AddRange(tool.Heaters);
            model.Tools.ClearAt(toolNumber, trim: true);
        }

        foreach (int heaterNumber in heaters)
        {
            await heatManager.SwitchOffAsync(heaterNumber, cancellationToken);
        }
    }

    /// <summary>
    /// Set the speed a tool's spindle turns at, and the running spindle's speed if the tool is the current one
    /// </summary>
    /// <param name="toolNumber">The tool, which need not exist</param>
    /// <param name="rpm">The speed, where 0 stops the spindle</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>An error if the speed is outside the spindle's range or a board refused it</returns>
    /// <remarks>
    /// RepRapFirmware's <c>Tool::SetSpindleRpm</c>, which does nothing for a tool without a spindle. The caller must not
    /// hold the object model lock
    /// </remarks>
    public async ValueTask<Message> SetSpindleRpmAsync(int toolNumber, int rpm, CancellationToken cancellationToken)
    {
        int spindleNumber;
        using (await model.AccessReadWriteAsync(cancellationToken))
        {
            if (Find(toolNumber) is not Tool { Spindle: > -1 } tool)
            {
                return new Message();
            }
            spindleNumber = tool.Spindle;

            if (rpm != 0)
            {
                if (spindleManager.Find(spindleNumber) is not Spindle spindle || !Spindles.SpindleManager.IsValidRpm(spindle, rpm))
                {
                    return new Message(MessageType.Error, "Requested spindle RPM out of range");
                }
                tool.SpindleRpm = rpm;
                if (model.State.CurrentTool != toolNumber)
                {
                    return new Message();
                }
            }
            else
            {
                tool.SpindleRpm = 0;
            }
        }

        if (rpm != 0)
        {
            return await spindleManager.SetConfiguredRpmAsync(spindleNumber, rpm, updateCurrentRpm: true, cancellationToken);
        }

        // Stopping applies to the spindle whichever tool is current, and clears the speed it would start at
        Message reply = await spindleManager.StopAsync(spindleNumber, cancellationToken);
        return reply.Succeeded()
            ? await spindleManager.SetConfiguredRpmAsync(spindleNumber, 0, updateCurrentRpm: false, cancellationToken)
            : reply;
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
}
