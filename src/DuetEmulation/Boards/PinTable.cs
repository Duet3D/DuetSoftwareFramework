using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DuetEmulation.Boards;

/// <summary>
/// One row of a board's pin table: a pin, the peripheral functions the firmware uses it for, and the
/// port names a configuration addresses it by
/// </summary>
/// <param name="Pin">The pin</param>
/// <param name="Names">Its port names, such as <c>io0.in</c>, aliases included; empty if it has none</param>
/// <param name="AdcInput">Its ADC input as the firmware names it (<c>adc1_9</c>), or null</param>
/// <param name="TimerOutput">Its timer output as the firmware names it (<c>tc0_1</c>, <c>tioa6</c>), or null</param>
/// <param name="PwmOutput">Its PWM output as the firmware names it (<c>tcc1_2F</c>, <c>pwm0h3_b</c>), or null</param>
public sealed record PinTableEntry(McuPin Pin, IReadOnlyList<string> Names, string? AdcInput, string? TimerOutput, string? PwmOutput);

/// <summary>
/// A board's pin table, read from its firmware's configuration header, so that the bench addresses pins
/// by the names the firmware gives them and cannot drift from the firmware it runs
/// </summary>
/// <remarks>
/// Each row of the table is an initialiser ending in the port names (a string or nullptr) followed by
/// a comment naming the pin, as in <c>{ TcOutput::none, ..., "io0.in" }, // PA06</c>. Columns are told
/// apart by their enum type, so the RepRapFirmware, DuetCANMaster and Duet3Expansion layouts all read
/// </remarks>
public sealed partial class PinTable
{
    private readonly Dictionary<string, PinTableEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every row, in pin order</summary>
    public IReadOnlyList<PinTableEntry> Entries { get; }

    private PinTable(List<PinTableEntry> entries)
    {
        Entries = entries;
        foreach (PinTableEntry entry in entries)
        {
            foreach (string name in entry.Names)
            {
                _byName.TryAdd(name, entry);
            }
        }
    }

    /// <summary>Read a board's pin table from its configuration header</summary>
    /// <exception cref="InvalidDataException">The header has no pin table this can read</exception>
    public static PinTable Load(string headerPath)
    {
        List<PinTableEntry> entries = [];
        foreach (string line in File.ReadLines(headerPath))
        {
            Match row = RowPattern().Match(line);
            if (!row.Success)
            {
                continue;
            }
            McuPin pin = McuPin.Parse(row.Groups["pin"].Value);
            string[] columns = row.Groups["columns"].Value.Split(',').Select(c => c.Trim()).ToArray();
            entries.Add(new PinTableEntry(
                pin,
                ParseNames(row.Groups["columns"].Value),
                EnumValue(columns, "AdcInput"),
                EnumValue(columns, "TcOutput"),
                EnumValue(columns, "TccOutput") ?? EnumValue(columns, "PwmOutput")));
        }
        if (entries.Count == 0)
        {
            throw new InvalidDataException($"No pin table found in {headerPath}");
        }
        return new PinTable(entries);
    }

    /// <summary>The row of a port name</summary>
    /// <exception cref="KeyNotFoundException">The board has no port of that name</exception>
    public PinTableEntry this[string portName] => _byName.TryGetValue(portName, out PinTableEntry? entry)
        ? entry
        : throw new KeyNotFoundException($"The board has no port named {portName}");

    /// <summary>The row of a port name, if the board has it</summary>
    public bool TryGet(string portName, out PinTableEntry entry) => _byName.TryGetValue(portName, out entry!);

    /// <summary>The row of a pin, if it is in the table</summary>
    public PinTableEntry? Find(McuPin pin) => Entries.FirstOrDefault(e => e.Pin == pin);

    private static List<string> ParseNames(string columns)
    {
        Match names = NamesPattern().Match(columns);
        return names.Success
            ? [.. names.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];
    }

    private static string? EnumValue(string[] columns, string type)
    {
        string prefix = type + "::";
        string? column = columns.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal));
        string? value = column?[prefix.Length..];
        return value is null or "none" ? null : value;
    }

    [GeneratedRegex(@"^\s*\{(?<columns>[^{}]*)\}\s*,?\s*//\s*(?<pin>P[A-H]\d{1,2})\b")]
    private static partial Regex RowPattern();

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex NamesPattern();
}
