using System;
using System.Threading;
using System.Threading.Tasks;
using DuetEmulation.Boards;
using DuetEmulation.Emulator;

namespace DuetEmulation;

/// <summary>
/// The boards' pins as a test sees them: inputs it drives and outputs it reads, addressed by the CAN
/// address of the board and the port names its firmware gives them (<c>io1.in</c>, <c>temp0</c>)
/// </summary>
public sealed class BoardIo(EmulatedSystem system)
{
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

    /// <summary>How many step pulses a driver has been sent: the rising edges on its STEP pin since the machine started</summary>
    public async Task<ulong> StepCountAsync(int address, int driver, CancellationToken cancellationToken = default)
    {
        EmulatedBoard board = system.Board(address);
        if (driver < 0 || driver >= board.Type.StepPins.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(driver), $"A {board.Type.Name} has {board.Type.StepPins.Count} drivers");
        }
        (string gpio, int number) = board.Type.Gpio(board.Type.StepPins[driver]);
        return await system.Renode.ExecuteForNumberAsync($"{gpio} RisingEdges {number}", board.Machine, cancellationToken);
    }
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
