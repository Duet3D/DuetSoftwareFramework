using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// How an endstop configured by M574 is named on the CAN bus
/// </summary>
/// <remarks>
/// Three places have to agree on this: M574 when it asks a board to watch an input, a homing move
/// when it says which input stops which drive, and the receiver that turns an incoming change back
/// into an endstop. They agree because the handle is derived from the axis rather than allocated, so
/// nothing has to remember an allocation or look one up.
/// </remarks>
internal static class RemoteEndstops
{
    /// <summary>
    /// The input handle an axis' endstop is monitored under
    /// </summary>
    /// <param name="axis">Axis number</param>
    /// <returns>The handle</returns>
    /// <remarks>
    /// Minor is the switch within the axis. Only one switch per axis is supported so far, so it is
    /// always zero; RepRapFirmware uses it for the second switch of a dual-motor axis
    /// </remarks>
    public static RemoteInputHandle HandleFor(int axis)
    {
        RemoteInputHandle handle = default;
        handle.Type = (byte)RemoteInputHandle.TypeEndstop;
        handle.Major = (byte)axis;
        handle.Minor = 0;
        return handle;
    }

    /// <summary>
    /// Split a port name into the board that carries it and the port on that board
    /// </summary>
    /// <param name="port">Port name, such as "0.io1.in" or "io1.in"</param>
    /// <param name="board">Receives the CAN address</param>
    /// <param name="localPort">Receives the port name as that board knows it</param>
    /// <returns>True if the name could be split</returns>
    /// <remarks>A name with no board prefix belongs to board 0, as in RepRapFirmware</remarks>
    public static bool TrySplitPort(string port, out byte board, out string localPort)
    {
        board = 0;
        localPort = port;

        int dot = port.IndexOf('.');
        if (dot <= 0)
        {
            return true;                        // no prefix, so it is board 0's own port
        }

        if (!byte.TryParse(port[..dot], out board))
        {
            board = 0;
            return true;                        // the first segment is part of the port name
        }

        localPort = port[(dot + 1)..];
        return localPort.Length > 0;
    }

    /// <summary>
    /// The stop input entry a homing move should carry for an axis
    /// </summary>
    /// <param name="endstop">The axis' endstop</param>
    /// <param name="axis">Axis number</param>
    /// <param name="stopInput">Receives the packed board and handle</param>
    /// <returns>True if the axis has an endstop a move can stop on</returns>
    /// <remarks>
    /// Only a switch on an input pin qualifies. A stall endstop is detected by the driver rather than
    /// by an input, and a Z probe standing in for an endstop needs M558, which is not ported
    /// </remarks>
    public static bool TryGetStopInput(Endstop endstop, int axis, out uint stopInput)
    {
        stopInput = MoveParams.NoStopInput;
        if (endstop.Type != EndstopType.InputPin || string.IsNullOrWhiteSpace(endstop.Port) ||
            !TrySplitPort(endstop.Port, out byte board, out _))
        {
            return false;
        }

        stopInput = MoveParams.MakeStopInput(board, HandleFor(axis).All);
        return true;
    }
}
