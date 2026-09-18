using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Events;

/// <summary>
/// Something the machine reported that may need reporting on or acting on
/// </summary>
/// <param name="Type">What happened, which is also the name of the macro run in response</param>
/// <param name="Param">Detail whose meaning depends on the type: a heater fault subtype, a driver status, a filament sensor status</param>
/// <param name="BoardAddress">CAN address of the board it came from</param>
/// <param name="DeviceNumber">Device on that board: a heater, an extruder, a driver</param>
/// <param name="Text">Additional text the source sent, which may be empty</param>
/// <remarks>
/// Two events are the same occurrence reported twice if everything but the text matches - see
/// <see cref="EventQueue"/>. That is why this is a record: the comparison is the type's own
/// </remarks>
public sealed record class MachineEvent(EventType Type, ushort Param, byte BoardAddress, byte DeviceNumber, string Text)
{
    /// <summary>
    /// Whether this is the same occurrence as another event, whatever either says about it
    /// </summary>
    /// <param name="other">Event to compare against</param>
    /// <returns>True if the two describe the same occurrence</returns>
    /// <remarks>
    /// The text is deliberately not compared. A fault that reports itself ten times a second sends
    /// slightly different text each time - a temperature that has moved on, a status that has - and
    /// queueing ten of them would run the macro ten times for one fault
    /// </remarks>
    public bool IsSameOccurrenceAs(MachineEvent other) =>
        Type == other.Type && Param == other.Param && BoardAddress == other.BoardAddress && DeviceNumber == other.DeviceNumber;
}
