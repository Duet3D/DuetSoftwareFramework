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
/// <param name="ExternalInterrupt">Its EXTINT line, for an MCU whose table gives one, or null</param>
public sealed record PinTableEntry(McuPin Pin, IReadOnlyList<string> Names, string? AdcInput, string? TimerOutput, string? PwmOutput,
                                   int? ExternalInterrupt);

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
    /// <exception cref="InvalidDataException">
    /// The header has no pin table this can read, or a row's comment names a pin other than the one
    /// at its position: the firmware indexes the table by pin, so the comment is only trusted where
    /// the two agree
    /// </exception>
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
            // The names are last and may hold commas, so the other columns are split without them
            string text = row.Groups["columns"].Value;
            Match names = NamesPattern().Match(text);
            string[] columns = (names.Success ? text[..names.Index] : text).Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
            entries.Add(new PinTableEntry(
                pin,
                names.Success ? [.. names.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : [],
                EnumValue(columns, "AdcInput"),
                EnumValue(columns, "TcOutput"),
                EnumValue(columns, "TccOutput") ?? EnumValue(columns, "PwmOutput"),
                ExternalInterrupt(columns, names.Success)));
        }
        if (entries.Count == 0)
        {
            throw new InvalidDataException($"No pin table found in {headerPath}");
        }
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Pin.Index != i)
            {
                throw new InvalidDataException(
                    $"Row {i} of the pin table in {headerPath} says it is {entries[i].Pin}, but row {i} is {new McuPin(i / 32, i % 32)}");
            }
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

    // Duet3Expansion's tables give the EXTINT line just before the names, as a number or Nx for none
    private static int? ExternalInterrupt(string[] columns, bool namesRemoved)
    {
        // Without a names string the last column is nullptr or a name macro, and the line is before it
        int index = namesRemoved ? columns.Length - 1 : columns.Length - 2;
        return index >= 0 && int.TryParse(columns[index], out int line) ? line : null;
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

    [GeneratedRegex("\"([^\"]*)\"\\s*$")]
    private static partial Regex NamesPattern();
}
