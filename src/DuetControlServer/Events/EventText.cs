using DuetAPI.ObjectModel;
using System;
using DuetControlServer.Link.Protocol.Shared;
using System.Collections.Generic;

namespace DuetControlServer.Events;

/// <summary>
/// What an event is called and what it says
/// </summary>
/// <remarks>
/// A port of RepRapFirmware's <c>Event::GetTextDescription</c> and <c>GetMacroFileName</c>. The strings
/// are kept exactly as RepRapFirmware writes them, because Duet Web Control, PanelDue and a decade of
/// macros read them
/// </remarks>
public static class EventText
{
    /// <summary>
    /// Name of each event as the wire and the macro file spell it
    /// </summary>
    /// <remarks>
    /// The schema spells these in snake case and C# in PascalCase, so the two are related by a rule
    /// rather than by a transformation - and a rule with an exception in it is what a transformation
    /// cannot express. Written out so that adding an event type without naming its macro fails the
    /// test that walks every value, rather than producing a plausible file name nobody wrote
    /// </remarks>
    private static readonly Dictionary<EventType, string> Names = new()
    {
        [EventType.MainBoardPowerFail] = "main_board_power_fail",
        [EventType.ExpansionReconnect] = "expansion_reconnect",
        [EventType.ExpansionTimeout] = "expansion_timeout",
        [EventType.HeaterFault] = "heater_fault",
        [EventType.DriverError] = "driver_error",
        [EventType.FilamentError] = "filament_error",
        [EventType.DriverStall] = "driver_stall",
        [EventType.DriverWarning] = "driver_warning",
        [EventType.McuTemperatureWarning] = "mcu_temperature_warning",
        [EventType.Overvoltage] = "overvoltage",
        [EventType.Undervoltage] = "undervoltage",
        [EventType.ControllerDisconnect] = "controller_disconnect",
        [EventType.ControllerReconnect] = "controller_reconnect"
    };

    /// <summary>
    /// Why a heater was shut down, by fault type
    /// </summary>
    /// <remarks>
    /// <para>
    /// CANlib carries these strings as well, but nothing on a board renders them: a board sends the
    /// fault type and whoever reports the fault to the operator turns it into words. The wording is
    /// RepRapFirmware's, and the trailing spaces are load-bearing - the detail the board sent is
    /// appended straight after.
    /// </para>
    /// <para>
    /// One entry per <see cref="HeaterFaultType"/> plus one for a value that is none of them, which is
    /// what CANlib asserts of its own copy. C# cannot assert an array's length at compile time, so
    /// <c>HeaterFaultTextCoversEveryFaultType</c> asserts it where this repository keeps its other
    /// cross-file invariants
    /// </para>
    /// </remarks>
    internal static readonly string[] HeaterFaultText =
    [
        "failed to read sensor: ",                      // the sensor error follows
        "temperature rising too slowly: ",              // "expected ... measured ..." follows
        "exceeded allowed temperature excursion: ",     // "target ... actual ..." follows
        "",                                             // "monitor ... was triggered" follows
        "high PWM: ",                                   // "expected ... actual ..." follows
        "inductive heater load error: ",                // the message the board sent follows
        "unknown error: "                               // the fault type was not one of the above
    ];

    /// <summary>
    /// Find the event type a name refers to
    /// </summary>
    /// <param name="name">Name as the schema spells it, underscores or hyphens alike</param>
    /// <param name="type">Event type it names</param>
    /// <returns>True if it names one</returns>
    /// <remarks>
    /// The same table the macro name comes from, so a type that can be raised is a type that has a
    /// macro to raise it into
    /// </remarks>
    public static bool TryParse(string name, out EventType type)
    {
        string wanted = name.Trim().Replace('-', '_');
        foreach (var kv in Names)
        {
            if (string.Equals(kv.Value, wanted, StringComparison.OrdinalIgnoreCase))
            {
                type = kv.Key;
                return true;
            }
        }
        type = default;
        return false;
    }

    /// <summary>
    /// Get the name of the macro file run in response to an event
    /// </summary>
    /// <param name="type">Event type</param>
    /// <returns>File name within the system directory</returns>
    public static string GetMacroFileName(EventType type) =>
        (Names.TryGetValue(type, out string? name) ? name.Replace('_', '-') : $"event-{(byte)type}") + ".g";

    /// <summary>
    /// Describe an event to the operator
    /// </summary>
    /// <param name="machineEvent">Event to describe</param>
    /// <returns>What to say, and how loudly to say it</returns>
    /// <remarks>
    /// RepRapFirmware returns the severity and fills in a string reference, because that is what its
    /// string handling allows. Here the two belong to one another and there is already a type that
    /// says so, which is also what the logger takes
    /// </remarks>
    public static Message Describe(MachineEvent machineEvent)
    {
        switch (machineEvent.Type)
        {
            case EventType.HeaterFault:
                {
                    int index = (machineEvent.Param < HeaterFaultText.Length) ? machineEvent.Param : HeaterFaultText.Length - 1;
                    return new Message(MessageType.Error, $"Heater {machineEvent.DeviceNumber} fault: {HeaterFaultText[index]}{machineEvent.Text}");
                }

            case EventType.FilamentError:
                return new Message(MessageType.Error, $"Filament error on extruder {machineEvent.DeviceNumber}: {(FilamentMonitorStatus)machineEvent.Param}");

            case EventType.DriverError:
                return new Message(MessageType.Error,
                                   $"Driver {machineEvent.BoardAddress}.{machineEvent.DeviceNumber} error: " +
                                   $"{DriverStatusText.Describe(machineEvent.Param, DriverStatusText.Severity.ErrorsOnly)}{machineEvent.Text}");

            case EventType.DriverWarning:
                return new Message(MessageType.Warning,
                                   $"Driver {machineEvent.BoardAddress}.{machineEvent.DeviceNumber} warning: " +
                                   $"{DriverStatusText.Describe(machineEvent.Param, DriverStatusText.Severity.WarningsAndErrors)}{machineEvent.Text}");

            case EventType.DriverStall:
                return new Message(MessageType.Warning, $"Driver {machineEvent.BoardAddress}.{machineEvent.DeviceNumber} stall");

            case EventType.McuTemperatureWarning:
                return new Message(MessageType.Warning, $"MCU temperature warning from board {machineEvent.BoardAddress}: temperature {machineEvent.Param / 10.0:F1}C");

            case EventType.Overvoltage:
                return new Message(MessageType.Warning, $"overvoltage on board {machineEvent.BoardAddress}: voltage {machineEvent.Param / 10.0:F1}V");

            case EventType.Undervoltage:
                return new Message(MessageType.Warning, $"undervoltage on board {machineEvent.BoardAddress}: voltage {machineEvent.Param / 10.0:F1}V");

            case EventType.ExpansionTimeout:
                return new Message(MessageType.Error, $"Expansion board {machineEvent.BoardAddress} stopped sending status");

            case EventType.ExpansionReconnect:
                return new Message(MessageType.Error, $"Expansion board {machineEvent.BoardAddress} reconnected");

            case EventType.ControllerDisconnect:
                return new Message(MessageType.Error, $"Lost connection to the controller: {machineEvent.Text}");

            case EventType.ControllerReconnect:
                return new Message(MessageType.Warning,
                                   "Connection to the controller re-established" + (machineEvent.Param != 0 ? ", it had reset" : string.Empty));

            case EventType.MainBoardPowerFail:
                // Never raised, here and in RepRapFirmware alike, which is why it has no text there either
                return new Message(MessageType.Error, string.Empty);

            default:
                return new Message(MessageType.Error, $"Unknown event type {(byte)machineEvent.Type}");
        }
    }
}
