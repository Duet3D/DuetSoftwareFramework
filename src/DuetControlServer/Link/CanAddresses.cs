using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link;

/// <summary>
/// What the main board's CAN address means in this architecture
/// </summary>
/// <remarks>
/// The addresses themselves are <see cref="CanId"/>, generated from CANlib. What is not in CANlib is
/// what sits at <see cref="CanId.MasterAddress"/> here: on a Duet 3 running RepRapFirmware that is the
/// main board, with drivers and IO of its own, but here it runs DuetCANMaster, which bridges SPI to
/// CAN and nothing else. The drivers and ports that board physically has are not driven, because the
/// split this architecture makes is that motion is planned on the SBC and executed by expansion boards
/// </remarks>
internal static class CanAddresses
{
    /// <summary>
    /// Whether an address names a board that has no drivers or ports
    /// </summary>
    /// <param name="address">CAN address</param>
    /// <returns>True if nothing can be attached there</returns>
    public static bool HasNoHardware(int address) => address == CanId.MasterAddress;

    /// <summary>
    /// Why something cannot be on the main board
    /// </summary>
    /// <param name="what">What was addressed, as it appeared in the code</param>
    /// <returns>The message</returns>
    /// <remarks>
    /// Worth spelling out rather than saying "invalid": a configuration written for RepRapFirmware
    /// will name board 0 all over it, and a port written without a board prefix means board 0 too, so
    /// the reason has to say what to do instead
    /// </remarks>
    public static string NoHardwareMessage(string what)
        => $"{what} is on board {CanId.MasterAddress}, which runs DuetCANMaster and has no drivers or "
           + "ports of its own; every driver and port belongs to an expansion board, so it needs a board address";
}
