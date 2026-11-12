using System;
using System.Globalization;

namespace DuetEmulation.Boards;

/// <summary>A pin of a Microchip MCU: its port (A, B, ...) and its number in that port</summary>
/// <param name="Port">Port index: 0 for A, 1 for B, ...</param>
/// <param name="Number">Pin number within the port, 0 to 31</param>
public readonly record struct McuPin(int Port, int Number)
{
    /// <summary>The pin's index across all ports: port * 32 + number</summary>
    public int Index => Port * 32 + Number;

    /// <summary>Its name in the form the datasheets use, such as PA06</summary>
    public override string ToString() => $"P{(char)('A' + Port)}{Number:D2}";

    /// <summary>Parse a name such as PA06 or PC2</summary>
    /// <exception cref="FormatException">It is not a pin name</exception>
    public static McuPin Parse(string name)
    {
        string trimmed = name.Trim().ToUpperInvariant();
        if (trimmed.Length < 3 || trimmed[0] != 'P' || trimmed[1] is < 'A' or > 'H' ||
            !int.TryParse(trimmed.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number > 31)
        {
            throw new FormatException($"\"{name}\" is not a pin name such as PA06");
        }
        return new McuPin(trimmed[1] - 'A', number);
    }
}
