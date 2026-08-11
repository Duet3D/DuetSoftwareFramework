using System;
using System.Diagnostics.CodeAnalysis;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
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
            : endstop.Port.Split(PortSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Split a port name into the board that carries it and the port on that board
    /// </summary>
    /// <param name="port">Port name, such as "1.io1.in"</param>
    /// <param name="description">What is being addressed, for the message - "Endstop port", say</param>
    /// <param name="board">Receives the CAN address</param>
    /// <param name="localPort">Receives the port name as that board knows it</param>
    /// <param name="error">Receives why the port cannot be used, or null if it can</param>
    /// <returns>True if the name is a port this architecture can watch</returns>
    /// <remarks>
    /// <para>
    /// This answers "can this port be used", not merely "does this parse", and the difference is
    /// deliberate. A port on board 0 cannot be used - that board runs DuetCANMaster and has no ports
    /// of its own - and a name with no board prefix means board 0, as in RepRapFirmware. Both are
    /// refused here rather than by the caller, because a caller that has to remember a second check
    /// is a caller that will one day forget it. Four of the six call sites had.
    /// </para>
    /// <para>
    /// The reason comes back with the refusal for the same reason. A caller composing its own message
    /// would have to know which of the two refusals it was looking at, which is the check again in
    /// another form; and "invalid port" for a port that is merely on the wrong board sends the
    /// operator looking for a typo that is not there
    /// </para>
    /// </remarks>
    public static bool TrySplitPort(string port, string description, out byte board, out string localPort,
                                    [NotNullWhen(false)] out string? error)
    {
        board = CanId.MasterAddress;
        localPort = port;
        error = null;

        int dot = port.IndexOf('.');
        if (dot <= 0 || !byte.TryParse(port[..dot], out board))
        {
            // No board prefix at all, or a first segment that is part of the port name rather than a
            // number. Either way RepRapFirmware's grammar makes this the main board's own port
            board = CanId.MasterAddress;
            error = CanAddresses.NoHardwareMessage($"{description} '{port}'");
            return false;
        }

        if (CanAddresses.HasNoHardware(board))
        {
            error = CanAddresses.NoHardwareMessage($"{description} '{port}'");
            return false;
        }

        localPort = port[(dot + 1)..];
        if (localPort.Length == 0)
        {
            error = $"{description} '{port}' names a board but no pin on it";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The input handle a board reports its stalled drivers under
    /// </summary>
    /// <remarks>
    /// One handle for the whole board, not one per driver or per axis: a board reports a bitmap of
    /// the drivers that stalled under <c>RemoteInputHandle(typeStallEndstop, 0, 0)</c>. So the board
    /// address is what distinguishes one stall endstop from another, which is why
    /// <see cref="TryGetStallStopInput"/> fills in a board per driver but leaves the handle alone
    /// </remarks>
    public static RemoteInputHandle StallHandle()
    {
        RemoteInputHandle handle = default;
        handle.Type = (byte)RemoteInputHandle.TypeStallEndstop;
        handle.Major = 0;
        handle.Minor = 0;
        return handle;
    }

    /// <summary>
    /// Fill in the switches a homing move should stop an axis on
    /// </summary>
    /// <param name="endstop">The axis' endstop</param>
    /// <param name="axis">Axis number</param>
    /// <param name="numDrivers">How many drivers the axis has</param>
    /// <param name="stopInput">Entry to fill in; left watching nothing if the axis cannot be stopped</param>
    /// <returns>True if the axis has a switch a move can stop on</returns>
    /// <remarks>
    /// <para>
    /// Only a switch on an input pin qualifies. A Z probe standing in for an endstop is registered
    /// under a probe handle, so it goes through <see cref="RemoteProbes"/>, and a stall is detected by
    /// the driver rather than by an input, so it goes through <see cref="TryGetStallStopInput"/>.
    /// </para>
    /// <para>
    /// An axis with as many switches as drivers stops each driver on its own switch, which is what
    /// squares a gantry. Any other count - one switch for a dual-motor axis, or more switches than
    /// drivers - stops the whole axis on the first trigger, which is what RepRapFirmware does when
    /// the two counts disagree and what keeps a driver with no switch of its own from running on
    /// </para>
    /// </remarks>
    public static bool TryGetStopInput(Endstop endstop, int axis, int numDrivers, MoveStopInput stopInput)
    {
        stopInput.Clear();
        if (endstop.Type != EndstopType.InputPin)
        {
            return false;
        }

        string[] ports = PortsOf(endstop);
        if (ports.Length == 0)
        {
            return false;
        }

        Span<byte> boards = stackalloc byte[ports.Length];
        for (int i = 0; i < ports.Length; i++)
        {
            if (!TrySplitPort(ports[i], "Endstop port", out boards[i], out _, out _))
            {
                return false;
            }
        }

        if (numDrivers > 1 && ports.Length == numDrivers)
        {
            stopInput.SetPerDriver(HandleFor(axis).All, boards);
        }
        else
        {
            stopInput.SetShared(HandleFor(axis).All, boards[0]);
        }
        return true;
    }

    /// <summary>
    /// Fill in the stall reports a homing move should stop an axis on
    /// </summary>
    /// <param name="drivers">Drivers to watch, in driver order</param>
    /// <param name="stopInput">Entry to fill in; left watching nothing if there is nothing to watch</param>
    /// <returns>True if there is at least one driver to watch</returns>
    /// <remarks>
    /// <para>
    /// A stall is detected by the driver, so what stops the move is a report from the board carrying
    /// it rather than an input on a pin. Every board reports under the one <see cref="StallHandle"/>,
    /// so unlike a switch per driver this is one handle and a board per driver - which is why the
    /// native side only derives a per-driver minor field for an endstop handle.
    /// </para>
    /// <para>
    /// A single driver is written as shared rather than per-driver so that every driver of the drive
    /// watches it. That is what makes <c>MotorStallAny</c> stop a dual-motor axis on either motor
    /// stalling, and it is also the right thing for the coupled case
    /// </para>
    /// </remarks>
    public static bool TryGetStallStopInput(ReadOnlySpan<DuetAPI.Utility.DriverId> drivers, MoveStopInput stopInput)
    {
        stopInput.Clear();
        if (drivers.Length == 0)
        {
            return false;
        }

        if (drivers.Length == 1)
        {
            stopInput.SetShared(StallHandle().All, (byte)drivers[0].Board);
            return true;
        }

        Span<byte> boards = stackalloc byte[drivers.Length];
        for (int i = 0; i < drivers.Length; i++)
        {
            boards[i] = (byte)drivers[i].Board;
        }
        stopInput.SetPerDriver(StallHandle().All, boards);
        return true;
    }
}
