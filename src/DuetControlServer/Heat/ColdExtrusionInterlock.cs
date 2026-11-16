using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;

namespace DuetControlServer.Heat;

/// <summary>
/// The cold extrusion interlock: what M302 configures, whether an extruder may be driven, and the warning when it is not
/// </summary>
/// <remarks>
/// RepRapFirmware keeps the configuration in <c>Heat</c>, decides in <c>Tool::ExtruderMovementAllowed</c> and
/// <c>Tool::CanDriveExtruder</c>, and reports from <c>Tool::DisplayColdExtrusionWarnings</c>
/// </remarks>
/// <param name="model">Object model</param>
/// <param name="heatManager">The heaters a tool's temperatures are read from</param>
/// <param name="eventLogger">Writes out the warning that a tool was not driven</param>
public sealed class ColdExtrusionInterlock(Model.ObjectModel model, HeatManager heatManager, Utility.EventLogger eventLogger)
{
    /// <summary>
    /// Shortest time between two cold extrusion warnings
    /// </summary>
    /// <remarks>RRF3Common.h <c>MinimumWarningInterval</c></remarks>
    public static readonly TimeSpan WarningInterval = TimeSpan.FromMilliseconds(4000);

    /// <summary>
    /// Allow or forbid cold extrusion (M302 P)
    /// </summary>
    /// <remarks>The caller must hold the object model write lock</remarks>
    public void SetAllowed(bool allowed)
    {
        model.Heat.ColdExtrusionAllowed = allowed;
        UpdateReportedMinimums();
    }

    /// <summary>
    /// Set the minimum temperature for extrusion (M302 S)
    /// </summary>
    /// <remarks>The caller must hold the object model write lock</remarks>
    public void SetMinExtrusionTemperature(float temperature)
    {
        model.Heat.MinExtrusionTemperature = temperature;
        UpdateReportedMinimums();
    }

    /// <summary>
    /// Set the minimum temperature for retraction (M302 R)
    /// </summary>
    /// <remarks>The caller must hold the object model write lock</remarks>
    public void SetMinRetractionTemperature(float temperature)
    {
        model.Heat.MinRetractionTemperature = temperature;
        UpdateReportedMinimums();
    }

    /// <summary>
    /// Bring <c>heat.coldExtrudeTemperature</c> and <c>heat.coldRetractTemperature</c>, which report 0 while cold
    /// extrusion is allowed, into step with the configuration
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Heat::objectModelTable</c> entries for the two</remarks>
    private void UpdateReportedMinimums()
    {
        DuetAPI.ObjectModel.Heat heat = model.Heat;
        heat.ColdExtrudeTemperature = heat.ColdExtrusionAllowed ? 0.0f : heat.MinExtrusionTemperature;
        heat.ColdRetractTemperature = heat.ColdExtrusionAllowed ? 0.0f : heat.MinRetractionTemperature;
    }

    /// <summary>
    /// Whether an extruder may be driven in a move, flagging the tool for a warning when its heaters stop it
    /// </summary>
    /// <param name="tool">The tool the move was built for, or null for none</param>
    /// <param name="forwards">True for extrusion, false for retraction</param>
    /// <param name="extruder">The extruder number</param>
    /// <returns>True if the extruder may be driven</returns>
    /// <remarks>RepRapFirmware's <c>Tool::ExtruderMovementAllowed</c>. The caller must hold the object model lock</remarks>
    public bool ExtruderMovementAllowed(Tool? tool, bool forwards, int extruder)
    {
        if (model.Heat.ColdExtrusionAllowed)
        {
            return true;
        }

        // An extruder that is not the tool's is never driven, and neither is any extruder with no tool selected
        if (tool is null || !tool.Extruders.Contains(extruder))
        {
            return false;
        }

        if (CanDriveExtruder(tool, forwards))
        {
            return true;
        }
        FlagNotDriven(tool.Number);
        return false;
    }

    /// <summary>
    /// Whether every heater of a tool is free of faults and at least at the minimum temperature for the direction
    /// </summary>
    /// <param name="tool">The tool</param>
    /// <param name="forwards">True for extrusion, false for retraction</param>
    /// <returns>True if the tool's extruders may be driven</returns>
    /// <remarks>
    /// RepRapFirmware's <c>Tool::CanDriveExtruder</c> and <c>Tool::AllHeatersAtHighTemperature</c>. A heater that does
    /// not exist reads as absolute zero. The caller must hold the object model lock
    /// </remarks>
    public bool CanDriveExtruder(Tool tool, bool forwards)
    {
        float minimum = forwards ? model.Heat.MinExtrusionTemperature : model.Heat.MinRetractionTemperature;
        foreach (int heaterNumber in tool.Heaters)
        {
            Heater? heater = heatManager.Find(heaterNumber);
            if (heater?.State == HeaterState.Fault || (heater?.Current ?? HeatManager.AbsoluteZero) < minimum)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Guards the warning state below
    /// </summary>
    private readonly Lock _warningLock = new();

    /// <summary>
    /// Tools an extruder was not driven for since the last warning, RepRapFirmware's <c>prohibitedExtrusionTools</c>
    /// </summary>
    private readonly SortedSet<int> _notDrivenTools = [];

    /// <summary>
    /// When the last warning went out, in <see cref="Environment.TickCount64"/> milliseconds
    /// </summary>
    private long _lastWarningTicks = long.MinValue / 2;

    /// <summary>
    /// Whether a warning is waiting to go out
    /// </summary>
    private bool _warningPending;

    /// <summary>
    /// Flag a tool for the warning that it was not driven, sending the warning once <see cref="WarningInterval"/>
    /// has passed since the last one
    /// </summary>
    /// <param name="toolNumber">The tool</param>
    private void FlagNotDriven(int toolNumber)
    {
        lock (_warningLock)
        {
            _notDrivenTools.Add(toolNumber);
            if (_warningPending)
            {
                return;
            }
            _warningPending = true;
        }

        // Not awaited: the caller holds the object model lock, which writing the warning out takes
        _ = Task.Run(DisplayWarningsAsync);
    }

    /// <summary>
    /// Send the warning for every flagged tool once <see cref="WarningInterval"/> has passed since the last one
    /// </summary>
    /// <remarks>RepRapFirmware's <c>Tool::DisplayColdExtrusionWarnings</c>, called from <c>RepRap::Spin</c></remarks>
    private async Task DisplayWarningsAsync()
    {
        long wait;
        lock (_warningLock)
        {
            wait = _lastWarningTicks + (long)WarningInterval.TotalMilliseconds - Environment.TickCount64;
        }
        if (wait > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(wait));
        }

        int[] tools;
        lock (_warningLock)
        {
            tools = [.. _notDrivenTools];
            _notDrivenTools.Clear();
            _warningPending = false;
            _lastWarningTicks = Environment.TickCount64;
        }

        foreach (int tool in tools)
        {
            await eventLogger.LogOutputAsync(MessageType.Warning,
                $"Tool {tool} was not driven because its heater temperatures were not high enough or it has a heater fault");
        }
    }
}
