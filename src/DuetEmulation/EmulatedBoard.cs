using DuetEmulation.Boards;

namespace DuetEmulation;

/// <summary>One board of an emulated system: its machine in Renode, its type and its CAN address</summary>
/// <param name="Machine">The name of its machine in Renode</param>
/// <param name="Type">What kind of board it is</param>
/// <param name="Address">Its CAN address: 0 for the main board</param>
/// <param name="Firmware">The application it runs</param>
/// <param name="Bootloader">The bootloader it starts from, or null if it boots the application directly</param>
public sealed record EmulatedBoard(string Machine, BoardType Type, int Address, FirmwareImage Firmware, FirmwareImage? Bootloader)
{
    /// <summary>The machine name of the main board</summary>
    public const string MainMachine = "main";

    /// <summary>The machine name of an expansion board at an address</summary>
    public static string ExpansionMachine(int address) => $"exp{address}";

    /// <inheritdoc/>
    public override string ToString() => $"{Type.Name} at {Address} ({Machine})";
}
