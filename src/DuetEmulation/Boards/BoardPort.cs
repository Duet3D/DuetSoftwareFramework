using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DuetEmulation.Boards;

/// <summary>What a board's port is for, as the bench can drive or watch it</summary>
public enum PortKind
{
    /// <summary>A digital input for a switch or sensor (<c>io0.in</c>)</summary>
    DigitalInput,

    /// <summary>A fan's tacho input (<c>out3.tach</c>)</summary>
    TachoInput,

    /// <summary>An analogue input for a thermistor (<c>temp0</c>)</summary>
    AnalogInput,

    /// <summary>An output the firmware drives (<c>out0</c>, <c>io0.out</c>)</summary>
    Output,

    /// <summary>A pin for something else, such as an SPI chip select or an encoder (<c>spi.cs0</c>)</summary>
    Other,

    /// <summary>A test point that only the factory test equipment uses (<c>ate.vin</c>)</summary>
    TestPoint
}

/// <summary>A port of a board: a pin the firmware gives names, and what it is for</summary>
/// <param name="Name">The name it is addressed by, the first its firmware gives it</param>
/// <param name="Aliases">Its other names</param>
/// <param name="Kind">What it is for</param>
/// <param name="Analog">Whether it can read an analogue value</param>
public sealed record BoardPort(string Name, IReadOnlyList<string> Aliases, PortKind Kind, bool Analog);

/// <summary>The ports of a kind of board, from its firmware's pin table</summary>
public static partial class BoardPorts
{
    /// <summary>
    /// Every pin of the board's pin table that has a name, in the order a person reads them: inputs,
    /// outputs, then the rest, each by number
    /// </summary>
    public static IReadOnlyList<BoardPort> Of(BoardType type) =>
        [.. type.PinTable.Entries
            .Where(e => e.Names.Count > 0)
            .Select(e => new BoardPort(e.Names[0], [.. e.Names.Skip(1)], Kind(e.Names[0], e.AdcInput is not null), e.AdcInput is not null))
            .OrderBy(p => p.Kind)
            .ThenBy(p => NaturalKey(p.Name), StringComparer.Ordinal)];

    /// <summary>What a port is for, from the name Duet's firmware gives it</summary>
    public static PortKind Kind(string name, bool analog) => name switch
    {
        _ when name.StartsWith("ate.", StringComparison.Ordinal) => PortKind.TestPoint,
        _ when DigitalInputPattern().IsMatch(name) => PortKind.DigitalInput,
        _ when TachoPattern().IsMatch(name) => PortKind.TachoInput,
        _ when OutputPattern().IsMatch(name) => PortKind.Output,
        _ when analog && ThermistorPattern().IsMatch(name) => PortKind.AnalogInput,
        _ => PortKind.Other
    };

    // Numbers padded so that out10 sorts after out9
    private static string NaturalKey(string name) => NumberPattern().Replace(name, m => m.Value.PadLeft(4, '0'));

    [GeneratedRegex(@"^io\d+\.in$")]
    private static partial Regex DigitalInputPattern();

    [GeneratedRegex(@"^out\d+\.tach$")]
    private static partial Regex TachoPattern();

    [GeneratedRegex(@"^(out\d+|io\d+\.out)$")]
    private static partial Regex OutputPattern();

    [GeneratedRegex(@"^temp\d+$")]
    private static partial Regex ThermistorPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();
}
