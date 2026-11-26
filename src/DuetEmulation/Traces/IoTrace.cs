using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DuetEmulation.Boards;

namespace DuetEmulation.Traces;

/// <summary>What an output pin drives</summary>
public enum PinMode
{
    /// <summary>Nothing: the pin is an input</summary>
    Input,

    /// <summary>Low</summary>
    Low,

    /// <summary>High</summary>
    High,

    /// <summary>A PWM signal from a timer</summary>
    Pwm,

    /// <summary>A peripheral function other than a timer output, such as a UART</summary>
    Peripheral
}

/// <summary>What an output pin drives: its mode, and its duty and frequency for PWM</summary>
/// <param name="Mode">What it drives</param>
/// <param name="Duty">The fraction of the time it is high: 0 or 1 for a digital output, the duty for PWM</param>
/// <param name="Frequency">The PWM frequency in Hz, or 0 when it is not PWM</param>
public readonly record struct PinOutput(PinMode Mode, double Duty, double Frequency)
{
    /// <inheritdoc/>
    public override string ToString() => Mode == PinMode.Pwm ? $"PWM {Duty:P1} at {Frequency:0.#}Hz" : Mode.ToString();
}

/// <summary>A change of what an output pin drives</summary>
/// <param name="Time">The emulated time of the change</param>
/// <param name="Board">The board</param>
/// <param name="Pin">The pin</param>
/// <param name="Ports">Its port names in the board's firmware, if it has any</param>
/// <param name="Output">What it drives from then on</param>
public sealed record PinEvent(TimeSpan Time, EmulatedBoard Board, McuPin Pin, IReadOnlyList<string> Ports, PinOutput Output)
{
    /// <inheritdoc/>
    public override string ToString() =>
        $"{Time.TotalSeconds,11:F6} board {Board.Address} {Pin}{(Ports.Count > 0 ? $" ({string.Join(',', Ports)})" : string.Empty)}: {Output}";
}

/// <summary>A change a GPIO model's script made to an input, such as an endstop triggering after so many steps</summary>
/// <param name="Time">The emulated time of the change</param>
/// <param name="Board">The board</param>
/// <param name="Pin">The pin</param>
/// <param name="Ports">Its port names in the board's firmware, if it has any</param>
/// <param name="Value">What drives it from then on: high, low, released, or pulses at a frequency</param>
public sealed record InputEvent(TimeSpan Time, EmulatedBoard Board, McuPin Pin, IReadOnlyList<string> Ports, string Value);

/// <summary>
/// The changes of the boards' output pins so far, read from the file the GPIO models write
/// (Renode/Peripherals/Common/DuetIoTrace.cs). Pins that toggle too often, such as STEP pins, are left out
/// </summary>
/// <remarks>
/// As with the CAN trace, a mark divides the changes a test has looked at from those it has not
/// </remarks>
public sealed class IoTrace(string path, IReadOnlyList<EmulatedBoard> boards)
{
    private readonly Lock _lock = new();
    private readonly JsonLinesFile _file = new(path);
    private readonly List<PinEvent> _events = [];
    private readonly List<InputEvent> _inputs = [];
    private int _mark;

    /// <summary>The file the GPIO models write</summary>
    public string Path => _file.Path;

    /// <summary>Every change recorded so far</summary>
    public IReadOnlyList<PinEvent> All
    {
        get
        {
            lock (_lock)
            {
                Update();
                return [.. _events];
            }
        }
    }

    /// <summary>Every change a script made to an input so far</summary>
    public IReadOnlyList<InputEvent> Inputs
    {
        get
        {
            lock (_lock)
            {
                Update();
                return [.. _inputs];
            }
        }
    }

    /// <summary>The changes recorded since the mark</summary>
    public IReadOnlyList<PinEvent> SinceMark
    {
        get
        {
            lock (_lock)
            {
                Update();
                return _events[_mark..];
            }
        }
    }

    /// <summary>Make the queries ignore the changes recorded so far</summary>
    public void SetMark()
    {
        lock (_lock)
        {
            Update();
            _mark = _events.Count;
        }
    }

    /// <summary>The changes since the mark of a board's port, such as <c>out0</c></summary>
    public IReadOnlyList<PinEvent> Find(int address, string port)
    {
        EmulatedBoard board = BoardAt(address);
        McuPin pin = board.Type.PinTable[port].Pin;
        return [.. SinceMark.Where(e => e.Board == board && e.Pin == pin)];
    }

    /// <summary>What a board's port drove last, by everything recorded, or null if it has never changed</summary>
    public PinOutput? Last(int address, string port)
    {
        EmulatedBoard board = BoardAt(address);
        McuPin pin = board.Type.PinTable[port].Pin;
        return All.LastOrDefault(e => e.Board == board && e.Pin == pin)?.Output;
    }

    /// <summary>The changes as text, one per line</summary>
    public static string Dump(IEnumerable<PinEvent> events) => string.Join('\n', events);

    private EmulatedBoard BoardAt(int address) =>
        boards.FirstOrDefault(b => b.Address == address) ?? throw new KeyNotFoundException($"No board has CAN address {address}");

    private void Update()
    {
        foreach (string line in _file.ReadNewLines())
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement record = document.RootElement;
            string machine = record.GetProperty("machine").GetString() ?? string.Empty;
            EmulatedBoard? board = boards.FirstOrDefault(b => b.Machine == machine);
            if (board is null)
            {
                continue;
            }
            McuPin pin = McuPin.Parse(record.GetProperty("pin").GetString() ?? string.Empty);
            TimeSpan time = TimeSpan.FromMicroseconds(record.GetProperty("t").GetDouble());
            IReadOnlyList<string> ports = board.Type.PinTable.Find(pin)?.Names ?? [];
            if (record.TryGetProperty("input", out JsonElement input))
            {
                _inputs.Add(new InputEvent(time, board, pin, ports, input.GetString() ?? string.Empty));
                continue;
            }
            PinOutput output = new(
                Enum.Parse<PinMode>(record.GetProperty("mode").GetString() ?? nameof(PinMode.Input), ignoreCase: true),
                record.GetProperty("duty").GetDouble(),
                record.GetProperty("freq").GetDouble());
            _events.Add(new PinEvent(time, board, pin, ports, output));
        }
    }
}
