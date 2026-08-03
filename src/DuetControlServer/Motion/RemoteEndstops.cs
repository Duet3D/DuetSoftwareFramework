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
    /// Separator between the ports of an axis that has one switch per driver
    /// </summary>
    public const char PortSeparator = '+';

    /// <summary>
    /// The input handle an axis' endstop switch is monitored under
    /// </summary>
    /// <param name="axis">Axis number</param>
    /// <param name="switchIndex">Which switch of that axis, which is also the driver it belongs to</param>
    /// <returns>The handle</returns>
    /// <remarks>
    /// Major is the axis and minor the switch within it. An axis with one switch uses minor zero for
    /// every driver; an axis with a switch per driver pairs port i with driver i, which is how
    /// RepRapFirmware pairs them too
    /// </remarks>
    public static RemoteInputHandle HandleFor(int axis, int switchIndex = 0)
    {
        RemoteInputHandle handle = default;
        handle.Type = (byte)RemoteInputHandle.TypeEndstop;
        handle.Major = (byte)axis;
        handle.Minor = (byte)switchIndex;
        return handle;
    }

    /// <summary>
    /// The ports of an endstop, in driver order
    /// </summary>
    /// <param name="endstop">The endstop</param>
    /// <returns>The ports, empty if it has none</returns>
    public static string[] PortsOf(Endstop endstop)
        => string.IsNullOrWhiteSpace(endstop.Port)
            ? []
            : endstop.Port.Split(PortSeparator, System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

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
    /// <param name="numDrivers">How many drivers the axis has</param>
    /// <param name="stopInput">Receives the packed board and handle</param>
    /// <returns>True if the axis has an endstop a move can stop on</returns>
    /// <remarks>
    /// <para>
    /// Only a switch on an input pin qualifies. A stall endstop is detected by the driver rather than
    /// by an input, and a Z probe standing in for an endstop needs M558, which is not ported.
    /// </para>
    /// <para>
    /// An axis with as many switches as drivers stops each driver on its own switch, which is
    /// RepRapFirmware's <c>stopDriver</c>. Any other count - one switch for a dual-motor axis, or
    /// more switches than drivers - stops the whole axis on the first trigger, which is what
    /// RepRapFirmware does when the two counts disagree
    /// </para>
    /// </remarks>
    public static bool TryGetStopInput(Endstop endstop, int axis, int numDrivers, out uint stopInput)
    {
        stopInput = MoveParams.NoStopInput;
        if (endstop.Type != EndstopType.InputPin)
        {
            return false;
        }

        string[] ports = PortsOf(endstop);
        if (ports.Length == 0 || !TrySplitPort(ports[0], out byte board, out _))
        {
            return false;
        }

        bool perDriver = numDrivers > 1 && ports.Length == numDrivers;
        if (perDriver)
        {
            // Every switch of the axis has to be on one board, because a move carries one CAN address
            // per drive and the driver index only selects the switch
            foreach (string port in ports)
            {
                if (!TrySplitPort(port, out byte portBoard, out _) || portBoard != board)
                {
                    return false;
                }
            }
        }

        stopInput = MoveParams.MakeStopInput(board, HandleFor(axis).All);
        if (perDriver)
        {
            stopInput |= MoveParams.StopInputPerDriver;
        }
        return true;
    }
}
