using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DuetEmulation.Boards;

/// <summary>The MCU families the bench has models for</summary>
public enum McuFamily
{
    /// <summary>SAME70 (Duet 3 main boards)</summary>
    Same70,

    /// <summary>SAME51 and SAME54 (the SAME5x expansion boards)</summary>
    Same5x
}

/// <summary>
/// A kind of board the bench can emulate: the firmware it runs, the platform description and
/// peripheral models that make up its machine in Renode, and where its pins and inputs are
/// </summary>
public sealed partial class BoardType
{
    /// <summary>The board's name as Duet's documentation and the regression suite give it, such as EXP3HC</summary>
    public required string Name { get; init; }

    /// <summary>The CMake preset of its firmware</summary>
    public required string FirmwarePreset { get; init; }

    /// <summary>Its MCU family, which decides how its pins and analogue inputs are addressed</summary>
    public required McuFamily Mcu { get; init; }

    /// <summary>Its platform description, in Renode/Platforms</summary>
    public required string Platform { get; init; }

    /// <summary>The peripheral models its platform uses, relative to Renode/Peripherals</summary>
    public required IReadOnlyList<string> Models { get; init; }

    /// <summary>The SVD files its platform applies, by the Renode variable the platform reads them from</summary>
    public required IReadOnlyDictionary<string, string> Svds { get; init; }

    /// <summary>Where the application image goes in flash</summary>
    public required ulong ApplicationAddress { get; init; }

    /// <summary>The preset of the bootloader that a real board has at address 0, or null if it boots the application directly</summary>
    public string? BootloaderPreset { get; init; }

    /// <summary>Its CAN controller on the board's CAN bus</summary>
    public required string CanController { get; init; }

    /// <summary>An expansion board's address switches, bit 0 first, which read low when on; empty if it has none</summary>
    public IReadOnlyList<McuPin> AddressSwitches { get; init; } = [];

    /// <summary>
    /// The address of an expansion board without switches, which it takes when none has been stored in
    /// its user area (CANlib's CanId::...DefaultAddress), or null
    /// </summary>
    // TODO: store an address in the user area, as M954 does, so that such a board can be at any address
    public int? DefaultAddress { get; init; }

    /// <summary>Where the part keeps its 128-bit serial number, which the firmware reports as its unique ID</summary>
    public IReadOnlyList<ulong> SerialNumberWords { get; init; } = [];

    /// <summary>The firmware header holding its pin table, relative to the repository root</summary>
    public required string ConfigurationHeader { get; init; }

    /// <summary>The name of the array in <see cref="ConfigurationHeader"/> listing the drivers' STEP pins</summary>
    public required string StepPinsArray { get; init; }

    /// <summary>The name of the array in <see cref="ConfigurationHeader"/> listing the drivers' DIR pins</summary>
    public required string DirectionPinsArray { get; init; }

    /// <summary>
    /// The resistor in series with each thermistor input, which with the thermistor divides the
    /// reference: the firmware's DefaultThermistorSeriesR
    /// </summary>
    public double ThermistorSeriesResistance => _thermistorSeriesResistance.Value;

    /// <summary>The UART carrying a main board's G-code console, or null</summary>
    public string? ConsoleUart { get; init; }

    /// <summary>The controller a main board's SD card attaches to, or null</summary>
    public string? SdCardController { get; init; }

    /// <summary>The controller's SBC SPI bridge, for a main board run with an SBC, or null</summary>
    public string? SbcBridge { get; init; }

    /// <summary>Whether it is an expansion board, at an address set by its switches or its default one</summary>
    public bool IsExpansion => AddressSwitches.Count > 0 || DefaultAddress is not null;

    /// <summary>Whether the bench can put it at a CAN address</summary>
    public bool CanHaveAddress(int address) =>
        DefaultAddress is int fixedAddress ? address == fixedAddress : address >= 1 && address < 1 << AddressSwitches.Count;

    /// <summary>Its pin table</summary>
    public PinTable PinTable => _pinTable.Value;

    /// <summary>Each driver's STEP pin</summary>
    public IReadOnlyList<McuPin> StepPins => _stepPins.Value;

    /// <summary>Each driver's DIR pin</summary>
    public IReadOnlyList<McuPin> DirectionPins => _directionPins.Value;

    private readonly Lazy<PinTable> _pinTable;
    private readonly Lazy<IReadOnlyList<McuPin>> _stepPins;
    private readonly Lazy<IReadOnlyList<McuPin>> _directionPins;
    private readonly Lazy<double> _thermistorSeriesResistance;

    /// <summary>Create a board type; its header is read when first needed</summary>
    public BoardType()
    {
        _pinTable = new(() => PinTable.Load(HeaderPath));
        _stepPins = new(() => ReadPinArray(StepPinsArray!));
        _directionPins = new(() => ReadPinArray(DirectionPinsArray!));
        _thermistorSeriesResistance = new(() => ReadConstant("DefaultThermistorSeriesR"));
    }

    private string HeaderPath => Path.Combine(RepositoryPaths.Root, ConfigurationHeader);

    /// <summary>The GPIO peripheral of a pin in the board's machine, and the pin's number on it</summary>
    public (string Peripheral, int Number) Gpio(McuPin pin) => Mcu switch
    {
        // One PORT peripheral with every group
        McuFamily.Same5x => ("port", pin.Index),
        // One PIO controller per port
        _ => ($"pio{(char)('a' + pin.Port)}", pin.Number)
    };

    /// <summary>The ADC peripheral and channel of an analogue input as the pin table names it (<c>adc1_9</c>)</summary>
    /// <exception cref="FormatException">It is not an ADC input name</exception>
    public (string Peripheral, int Channel) Adc(string adcInput)
    {
        Match match = AdcInputPattern().Match(adcInput);
        if (!match.Success)
        {
            throw new FormatException($"\"{adcInput}\" is not an ADC input such as adc1_9");
        }
        int instance = int.Parse(match.Groups[1].Value);
        int channel = int.Parse(match.Groups[2].Value);
        return (Mcu == McuFamily.Same70 ? $"afec{instance}" : $"adc{instance}", channel);
    }

    private IReadOnlyList<McuPin> ReadPinArray(string arrayName)
    {
        string header = File.ReadAllText(HeaderPath);
        Match array = Regex.Match(header, $@"\b{Regex.Escape(arrayName)}\s*\[[^\]]*\]\s*=\s*\{{(?<pins>[^}}]*)\}}");
        if (!array.Success)
        {
            throw new InvalidDataException($"{ConfigurationHeader} has no array {arrayName}");
        }
        List<McuPin> pins = [.. PortPinPattern().Matches(array.Groups["pins"].Value)
            .Select(m => new McuPin(m.Groups[1].Value[0] - 'A', int.Parse(m.Groups[2].Value)))];
        if (pins.Count == 0)
        {
            throw new InvalidDataException($"{arrayName} in {ConfigurationHeader} lists no pins");
        }
        return pins;
    }

    private double ReadConstant(string name)
    {
        Match constant = Regex.Match(File.ReadAllText(HeaderPath), $@"\b{Regex.Escape(name)}\s*=\s*([0-9.]+)");
        return constant.Success
            ? double.Parse(constant.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidDataException($"{ConfigurationHeader} does not define {name}");
    }

    [GeneratedRegex(@"^adc(\d)_(\d+)$")]
    private static partial Regex AdcInputPattern();

    [GeneratedRegex(@"Port([A-H])Pin\(\s*(\d+)\s*\)")]
    private static partial Regex PortPinPattern();
}
