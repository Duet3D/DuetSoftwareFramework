using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DuetEmulation.Boards;
using DuetEmulation.Emulator;
using DuetEmulation.Traces;

namespace DuetEmulation;

/// <summary>
/// The boards' pins as a test sees them: inputs it drives and outputs it reads, addressed by the CAN
/// address of the board and the port names its firmware gives them (<c>io1.in</c>, <c>temp0</c>)
/// </summary>
public sealed class BoardIo(EmulatedSystem system)
{
    /// <summary>A scripted fan's speed at full duty unless a script says otherwise</summary>
    public const double DefaultTachoMaxRpm = 6000;

    /// <summary>Tacho pulses a revolution unless a script says otherwise, as RepRapFirmware assumes (DefaultFanTachoPulsesPerRev)</summary>
    public const double DefaultTachoPulsesPerRev = 2;

    /// <summary>Drive a digital input to a level, as a switch or sensor connected to it would</summary>
    /// <param name="address">The board's CAN address</param>
    /// <param name="port">The port name, such as <c>io1.in</c></param>
    /// <param name="level">The level at the pin</param>
    /// <param name="cancellationToken">Cancels the command</param>
    public Task DriveAsync(int address, string port, bool level, CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Board(address);
        return DriveAsync(board, board.Type.PinTable[port].Pin, level, cancellationToken);
    }

    /// <summary>Drive a pin to a level</summary>
    public Task DriveAsync(EmulatedBoard board, McuPin pin, bool level, CancellationToken cancellationToken = default)
    {
        (string gpio, int number) = board.Type.Gpio(pin);
        return system.ExecuteAsync(board, $"{gpio} Drive {number} {MonitorSyntax.Bool(level)}", cancellationToken);
    }

    /// <summary>Stop driving a digital input, leaving it to its pull resistor</summary>
    public Task ReleaseAsync(int address, string port, CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Board(address);
        (string gpio, int number) = board.Type.Gpio(board.Type.PinTable[port].Pin);
        return system.ExecuteAsync(board, $"{gpio} Release {number}", cancellationToken);
    }

    /// <summary>Make an analogue input read <paramref name="fraction"/> of its reference</summary>
    public Task SetAnalogAsync(int address, string port, double fraction, CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Board(address);
        PinTableEntry entry = board.Type.PinTable[port];
        string adcInput = entry.AdcInput ?? throw new ArgumentException($"{port} on board {address} is not an analogue input", nameof(port));
        (string adc, int channel) = board.Type.Adc(adcInput);
        return system.ExecuteAsync(board, $"{adc} Set {channel} {MonitorSyntax.Decimal(Math.Clamp(fraction, 0, 1))}", cancellationToken);
    }

    /// <summary>
    /// Make a thermistor input read a temperature, for an NTC thermistor of the given resistance at 25C
    /// and B value, against the board's series resistor
    /// </summary>
    public Task SetThermistorAsync(int address, string port, double celsius, double r25 = 100000, double beta = 4725,
                                   CancellationToken cancellationToken = default)
    {
        double resistance = Thermistor.Resistance(celsius, r25, beta);
        double series = system.Board(address).Type.ThermistorSeriesResistance;
        return SetAnalogAsync(address, port, resistance / (resistance + series), cancellationToken);
    }

    /// <summary>What a board's output port drives now, such as <c>out0</c></summary>
    public async Task<PinOutput> OutputAsync(int address, string port, CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Board(address);
        (string gpio, int number) = board.Type.Gpio(board.Type.PinTable[port].Pin);
        string[] fields = (await system.ExecuteAsync(board, $"{gpio} Output {number}", cancellationToken)).Split(' ');
        return new PinOutput(Enum.Parse<PinMode>(fields[0], ignoreCase: true),
                             double.Parse(fields[1], CultureInfo.InvariantCulture), double.Parse(fields[2], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Model a heater: the heater output <paramref name="heaterPort"/> heats a mass whose temperature the
    /// thermistor on <paramref name="thermistorPort"/> reads, as an NTC of the given resistance at 25C
    /// and B value against the board's series resistor. The model runs in the board's emulated time
    /// </summary>
    public Task AttachThermalModelAsync(int address, string heaterPort, string thermistorPort, ThermalModel model,
                                       double r25 = 100000, double beta = 4725, CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Board(address);
        (string gpio, int number) = board.Type.Gpio(board.Type.PinTable[heaterPort].Pin);
        string adcInput = board.Type.PinTable[thermistorPort].AdcInput
            ?? throw new ArgumentException($"{thermistorPort} on board {address} is not an analogue input", nameof(thermistorPort));
        (string adc, int channel) = board.Type.Adc(adcInput);
        return system.ExecuteAsync(board,
            $"{gpio} AttachThermalModel {number} {adc} {channel} {MonitorSyntax.Decimal(model.HeatingRate)} {MonitorSyntax.Decimal(model.TimeConstant)} " +
            $"{MonitorSyntax.Decimal(model.DeadTime)} {MonitorSyntax.Decimal(model.Ambient)} {MonitorSyntax.Decimal(r25)} {MonitorSyntax.Decimal(beta)} " +
            $"{MonitorSyntax.Decimal(board.Type.ThermistorSeriesResistance)}", cancellationToken);
    }

    /// <summary>The temperature of the mass a heater model on <paramref name="heaterPort"/> heats</summary>
    public async Task<double> ThermalModelTemperatureAsync(int address, string heaterPort, CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Board(address);
        (string gpio, int number) = board.Type.Gpio(board.Type.PinTable[heaterPort].Pin);
        return double.Parse(await system.ExecuteAsync(board, $"{gpio} ThermalModelTemperature {number}", cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Drive a digital input to a level once a driver on the same board has had <paramref name="steps"/>
    /// more step pulses, as an endstop or a probe triggers when the axis has moved so far
    /// </summary>
    public Task DriveAfterStepsAsync(int address, int driver, ulong steps, string port, bool level, CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Board(address);
        (string gpio, int stepPin) = board.Type.Gpio(StepPin(board, driver));
        (string inputGpio, int inputPin) = board.Type.Gpio(board.Type.PinTable[port].Pin);
        if (gpio != inputGpio)
        {
            throw new ArgumentException($"A trigger needs the STEP pin and {port} on the same GPIO peripheral", nameof(port));
        }
        return system.ExecuteAsync(board, $"{gpio} DriveAfterEdges {stepPin} {steps} {inputPin} {MonitorSyntax.Bool(level)}", cancellationToken);
    }

    /// <summary>Forget a board's step triggers that have not fired</summary>
    public async Task ClearStepTriggersAsync(int address, CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Board(address);
        foreach (string gpio in board.Type.GpioPeripherals)
        {
            await system.ExecuteAsync(board, $"{gpio} ClearEdgeTriggers", cancellationToken);
        }
    }

    /// <summary>Drive a digital input as a square wave at <paramref name="frequency"/> Hz, or stop and release it with 0</summary>
    public Task PulsesAsync(int address, string port, double frequency, CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Board(address);
        (string gpio, int number) = board.Type.Gpio(board.Type.PinTable[port].Pin);
        return system.ExecuteAsync(board, $"{gpio} Pulses {number} {MonitorSyntax.Decimal(frequency)}", cancellationToken);
    }

    /// <summary>
    /// Pulse a tacho input as a fan driven by an output would turn: the output's duty times
    /// <paramref name="maxRpm"/>, <paramref name="pulsesPerRev"/> pulses a revolution
    /// </summary>
    /// <param name="address">The board's CAN address</param>
    /// <param name="fanPort">The fan's output, such as out3</param>
    /// <param name="tachoPort">The tacho input, such as out3.tach</param>
    /// <param name="maxRpm">The fan's speed at full duty</param>
    /// <param name="pulsesPerRev">Tacho pulses a revolution</param>
    /// <param name="inverted">Whether the fan runs when the output is low</param>
    /// <param name="cancellationToken">Cancels the command</param>
    public Task TachoFollowsAsync(int address, string fanPort, string tachoPort, double maxRpm, double pulsesPerRev = DefaultTachoPulsesPerRev, bool inverted = false,
                                  CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Board(address);
        (string gpio, int fan) = board.Type.Gpio(board.Type.PinTable[fanPort].Pin);
        (string tachoGpio, int tacho) = board.Type.Gpio(board.Type.PinTable[tachoPort].Pin);
        if (gpio != tachoGpio)
        {
            throw new ArgumentException($"{fanPort} and {tachoPort} are on different GPIO peripherals", nameof(tachoPort));
        }
        return system.ExecuteAsync(board,
            $"{gpio} TachoFollows {fan} {tacho} {MonitorSyntax.Decimal(maxRpm)} {MonitorSyntax.Decimal(pulsesPerRev)} {MonitorSyntax.Bool(inverted)}",
            cancellationToken);
    }

    private static McuPin StepPin(EmulatedBoard board, int driver) =>
        driver >= 0 && driver < board.Type.StepPins.Count
            ? board.Type.StepPins[driver]
            : throw new ArgumentOutOfRangeException(nameof(driver), $"A {board.Type.Name} has {board.Type.StepPins.Count} drivers");

    /// <summary>How many step pulses a driver has been sent: the rising edges on its STEP pin since the machine started</summary>
    public async Task<ulong> StepCountAsync(int address, int driver, CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Board(address);
        (string gpio, int number) = board.Type.Gpio(StepPin(board, driver));
        return await system.Renode.ExecuteForNumberAsync($"{gpio} RisingEdges {number}", board.Machine, cancellationToken);
    }
}

/// <summary>
/// A heater as a first-order model with dead time, as RepRapFirmware models one (M307):
/// dT/dt = heatingRate * duty(t - deadTime) - (T - ambient) / timeConstant
/// </summary>
/// <param name="HeatingRate">The rate of rise at full power and ambient temperature, in K/s</param>
/// <param name="TimeConstant">The cooling time constant, in s</param>
/// <param name="DeadTime">The delay before a change of power starts to change the temperature, in s</param>
/// <param name="Ambient">The temperature of the surroundings, in C</param>
public sealed record ThermalModel(double HeatingRate, double TimeConstant, double DeadTime, double Ambient = 25)
{
    /// <summary>
    /// A heater that heats and settles in a few seconds of emulated time, for tests of a control loop
    /// that would otherwise take minutes: 425C at full power, 63% of the way there in 20s
    /// </summary>
    public static ThermalModel Fast { get; } = new(20, 20, 1);

    /// <summary>
    /// The M307 that gives RepRapFirmware this model for a heater: K is the cooling rate 100K above
    /// ambient, and a cooling rate exponent of 1 makes the cooling linear, as it is here
    /// </summary>
    public string M307(int heater) => string.Create(CultureInfo.InvariantCulture,
        $"M307 H{heater} R{HeatingRate:0.###} K{100 / TimeConstant:0.###} D{DeadTime:0.###} E1 S1.0 V0");
}

/// <summary>NTC thermistor arithmetic, with the B-parameter equation the firmware uses</summary>
public static class Thermistor
{
    private const double AbsoluteZero = -273.15;
    private const double ReferenceKelvin = 25.0 - AbsoluteZero;

    /// <summary>A thermistor's resistance at a temperature</summary>
    public static double Resistance(double celsius, double r25, double beta) =>
        r25 * Math.Exp(beta * (1.0 / (celsius - AbsoluteZero) - 1.0 / ReferenceKelvin));
}
