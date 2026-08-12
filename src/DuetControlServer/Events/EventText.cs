using DuetAPI.ObjectModel;
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
    /// CANlib carries these strings as well, but nothing on a board renders them: a board sends the
    /// fault type and whoever reports the fault to the operator turns it into words. The wording is
    /// RepRapFirmware's, and the trailing spaces are load-bearing - the detail the board sent is
    /// appended straight after
    /// </remarks>
    private static readonly string[] HeaterFaultText =
    [
        "failed to read sensor: ",                      // the sensor error follows
        "temperature rising too slowly: ",              // "expected ... measured ..." follows
        "exceeded allowed temperature excursion: ",     // "target ... actual ..." follows
        "",                                             // "monitor ... was triggered" follows
        "high PWM: ",                                   // "expected ... actual ..." follows
        "unknown error: "                               // the fault type was not one of the above
    ];

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
    /// <returns>What to say and how loudly to say it</returns>
    public static (string Text, MessageType Type) Describe(MachineEvent machineEvent)
    {
        switch (machineEvent.Type)
        {
            case EventType.HeaterFault:
                {
                    int index = (machineEvent.Param < HeaterFaultText.Length) ? machineEvent.Param : HeaterFaultText.Length - 1;
                    return ($"Heater {machineEvent.DeviceNumber} fault: {HeaterFaultText[index]}{machineEvent.Text}", MessageType.Error);
                }

            case EventType.FilamentError:
                return ($"Filament error on extruder {machineEvent.DeviceNumber}: {(FilamentMonitorStatus)machineEvent.Param}", MessageType.Error);

            case EventType.DriverError:
                // TODO: append the decoded driver status, which needs StandardDriverStatus in the schema:
                // the board renders the same bits for its own replies, so the two must not drift
                return ($"Driver {machineEvent.BoardAddress}.{machineEvent.DeviceNumber} error: {machineEvent.Text}", MessageType.Error);

            case EventType.DriverWarning:
                // TODO: append the decoded driver status, as above
                return ($"Driver {machineEvent.BoardAddress}.{machineEvent.DeviceNumber} warning: {machineEvent.Text}", MessageType.Warning);

            case EventType.DriverStall:
                return ($"Driver {machineEvent.BoardAddress}.{machineEvent.DeviceNumber} stall", MessageType.Warning);

            case EventType.McuTemperatureWarning:
                return ($"MCU temperature warning from board {machineEvent.BoardAddress}: temperature {machineEvent.Param / 10.0:F1}C", MessageType.Warning);

            case EventType.Overvoltage:
                return ($"overvoltage on board {machineEvent.BoardAddress}: voltage {machineEvent.Param / 10.0:F1}V", MessageType.Warning);

            case EventType.Undervoltage:
                return ($"undervoltage on board {machineEvent.BoardAddress}: voltage {machineEvent.Param / 10.0:F1}V", MessageType.Warning);

            case EventType.ExpansionTimeout:
                return ($"Expansion board {machineEvent.BoardAddress} stopped sending status", MessageType.Error);

            case EventType.ExpansionReconnect:
                return ($"Expansion board {machineEvent.BoardAddress} reconnected", MessageType.Error);

            case EventType.ControllerDisconnect:
                return ($"Lost connection to the controller: {machineEvent.Text}", MessageType.Error);

            case EventType.ControllerReconnect:
                return ("Connection to the controller re-established" + (machineEvent.Param != 0 ? ", it had reset" : string.Empty),
                        MessageType.Warning);

            case EventType.MainBoardPowerFail:
                // Never raised, here and in RepRapFirmware alike, which is why it has no text there either
                return (string.Empty, MessageType.Error);

            default:
                return ($"Unknown event type {(byte)machineEvent.Type}", MessageType.Error);
        }
    }
}
