using System;
using System.Diagnostics.CodeAnalysis;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link;

/// <summary>
/// The syntax of a port name, and the board address in front of it
/// </summary>
/// <remarks>
/// Ported from RepRapFirmware's <c>IoPort</c>, which likewise has one. A port name is a grammar, and
/// two readers of one grammar diverge silently: each stays correct on the inputs it happens to see,
/// so the disagreement only shows up as a port that one part of the system accepts and another
/// rejects
/// </remarks>
public static class IoPorts
{
    /// <summary>
    /// Take the board address off a port name, and say which board it named
    /// </summary>
    /// <param name="portName">Port name as the operator wrote it</param>
    /// <param name="localPort">Receives the name as the board that carries it knows it</param>
    /// <returns>The CAN address of the board carrying the port</returns>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>IoPort::RemoveBoardAddress</c>, and the grammar is its: any number of
    /// <c>!</c>, <c>^</c> and <c>*</c> modifiers, then the digits of a board address, then a dot.
    /// Anything that does not match that - no digits, no dot after them, or an address past
    /// <see cref="CanId.MaxCanAddress"/> - is not an address at all, and the name belongs to the
    /// local board unchanged. That is how <c>e0heat</c> stays <c>e0heat</c> rather than being read as
    /// board "e0".
    /// </para>
    /// <para>
    /// The modifiers stay on the name that goes to the board. They say the pin is inverted or wants a
    /// pull-up, which is the board's business, not the address's - so <c>!1.io1.in</c> is board 1's
    /// <c>!io1.in</c>, not its <c>io1.in</c>.
    /// </para>
    /// <para>
    /// Where RepRapFirmware answers with <c>CanInterface::GetCanAddress()</c> for a name with no
    /// address, this answers <see cref="CanId.MasterAddress"/>: the local board is always the main
    /// board here, because this is the only thing that runs on one. That address carries no ports, so
    /// a caller that needs a usable port wants <see cref="TrySplitPort"/> rather than this
    /// </para>
    /// </remarks>
    public static byte RemoveBoardAddress(string portName, out string localPort)
    {
        localPort = portName;

        int prefix = 0;
        while (prefix < portName.Length && portName[prefix] is '!' or '^' or '*')
        {
            prefix++;
        }

        int afterDigits = prefix;
        int boardAddress = 0;
        while (afterDigits < portName.Length && char.IsAsciiDigit(portName[afterDigits]))
        {
            if (boardAddress <= CanId.MaxCanAddress)
            {
                // Stops accumulating once it cannot be an address any more, so that a silly long run
                // of digits cannot overflow into a small number that looks like one
                boardAddress = (boardAddress * 10) + (portName[afterDigits] - '0');
            }
            afterDigits++;
        }

        if (afterDigits == prefix                       // no digits, so no address
            || afterDigits >= portName.Length
            || portName[afterDigits] != '.'             // digits that are part of the port name
            || boardAddress > CanId.MaxCanAddress)
        {
            return CanId.MasterAddress;
        }

        localPort = string.Concat(portName.AsSpan(0, prefix), portName.AsSpan(afterDigits + 1));
        return (byte)boardAddress;
    }

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
    /// The name is read by <see cref="RemoveBoardAddress"/>, which is the grammar. What this adds is
    /// the policy on top of it: a port on board 0 cannot be used, because that board runs
    /// DuetCANMaster and has no ports of its own, and a name with no address means board 0 as it does
    /// in RepRapFirmware.
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
        board = RemoveBoardAddress(port, out localPort);
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
}
