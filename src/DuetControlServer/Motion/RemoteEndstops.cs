using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
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
    /// The name is read by <see cref="IoPorts.RemoveBoardAddress"/>, which is the one place that
    /// knows the grammar. What this adds is the policy: a port on board 0 cannot be used, because
    /// that board runs DuetCANMaster and has no ports of its own, and a name with no address means
    /// board 0 as it does in RepRapFirmware.
    /// </para>
    /// <para>
    /// The policy is applied here rather than by the caller because a caller that has to remember a
    /// second check is a caller that will one day forget it. The reason comes back with the refusal
    /// for the same reason: a caller composing its own message would have to know which refusal it
    /// was looking at, and "invalid port" for a port that is merely on the wrong board sends the
    /// operator looking for a typo that is not there
    /// </para>
    /// </remarks>
    public static bool TrySplitPort(string port, string description, out byte board, out string localPort,
                                    [NotNullWhen(false)] out string? error)
    {
        board = IoPorts.RemoveBoardAddress(port, out localPort);
        error = null;

        if (CanAddresses.HasNoHardware(board))
        {
            error = CanAddresses.NoHardwareMessage($"{description} '{port}'");
            return false;
        }

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
            stopInput.SetPerDriver(HandleFor(axis), boards);
        }
        else
        {
            stopInput.SetShared(HandleFor(axis), boards[0]);
        }
        return true;
    }

    /// <summary>
    /// Fill in the stall reports a homing move should stop an axis on
    /// </summary>
    /// <param name="drivers">Drivers the axis watches, which is only read to know whether there are any</param>
    /// <param name="stopInput">Entry to fill in; left watching nothing if there is nothing to watch</param>
    /// <returns>True if there is at least one driver to watch</returns>
    /// <remarks>
    /// <para>
    /// A stall is detected by the driver, so what stops the move is a report from the board carrying
    /// it rather than an input on a pin, and every board reports under the one
    /// <see cref="StallHandle"/>. So there is no board list to write: a driver can only be stopped by
    /// its own stall, and the board that reports it is the one carrying it, which the native side
    /// takes from the driver it is emitting.
    /// </para>
    /// <para>
    /// Writing a board list here was wrong in a way nothing could catch. Which drive an entry ends up
    /// on is decided after this runs - a coupled move rewrites every drive's entry to the one axis' -
    /// and the boards were then handed out round-robin across the move's drivers, so a driver could
    /// be given another driver's board to watch its own stall on
    /// </para>
    /// </remarks>
    public static bool TryGetStallStopInput(IReadOnlyList<WatchedDriver> drivers, MoveStopInput stopInput)
    {
        stopInput.Clear();
        if (drivers.Count == 0)
        {
            return false;
        }

        stopInput.SetStall(StallHandle());
        return true;
    }
}
