using System;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.Serialization;

namespace DuetEmulation.Scenarios;

/// <summary>
/// One step of a scenario: exactly one of its actions is set
/// </summary>
public sealed class ScenarioStep
{
    /// <summary>Send a code and wait for it to finish</summary>
    public string? Code { get; set; }

    /// <summary>Send a code without waiting for it</summary>
    public string? Start { get; set; }

    /// <summary>Wait for a code sent with <see cref="Start"/> to finish, by its text</summary>
    public string? Await { get; set; }

    /// <summary>How long <see cref="Code"/> or <see cref="Await"/> may take, as a duration (10s by default)</summary>
    public string? Timeout { get; set; }

    /// <summary>Let emulated time pass, as a duration</summary>
    public string? Wait { get; set; }

    /// <summary>A remark recorded in the capture, which also starts a step when captures are compared</summary>
    public string? Note { get; set; }

    /// <summary>Drive a digital input high or low</summary>
    public PinInput? Drive { get; set; }

    /// <summary>Stop driving a digital input</summary>
    public PinInput? Release { get; set; }

    /// <summary>Make an analogue input read a fraction of its reference</summary>
    public PinInput? Analog { get; set; }

    /// <summary>Make a thermistor input read a temperature</summary>
    public PinInput? Thermistor { get; set; }

    /// <summary>Drive a digital input as a square wave at a frequency, or stop it with 0</summary>
    public PinInput? Pulses { get; set; }

    /// <summary>Pulse a tacho input as the fan on an output would turn</summary>
    public PinInput? Tacho { get; set; }

    /// <summary>What the step does, as captures and errors name it</summary>
    public string Describe() =>
        Code ?? (Start is not null ? $"start {Start}" : null) ?? (Await is not null ? $"await {Await}" : null) ?? (Wait is not null ? $"wait {Wait}" : null)
        ?? (Note is not null ? $"# {Note}" : null) ?? (Drive is not null ? $"drive {Drive}" : null) ?? (Release is not null ? $"release {Release}" : null)
        ?? (Analog is not null ? $"analog {Analog}" : null) ?? (Thermistor is not null ? $"thermistor {Thermistor}" : null)
        ?? (Pulses is not null ? $"pulses {Pulses}" : null) ?? (Tacho is not null ? $"tacho {Tacho}" : null) ?? "nothing";

    /// <summary>The step as a scenario writes it, such as <c>drive: {board: 1, port: io0.in, level: high}</c></summary>
    public string ToYaml() => Scenario.Serializer.Serialize(this).TrimEnd('\n');

    /// <summary>Check that the step has one action, and that what it needs is there</summary>
    /// <exception cref="FormatException">It does not</exception>
    public void Validate()
    {
        object?[] actions = [Code, Start, Await, Wait, Note, Drive, Release, Analog, Thermistor, Pulses, Tacho];
        int count = actions.Count(a => a is not null);
        if (count != 1)
        {
            throw new FormatException(count == 0
                ? "a step needs one of code, start, await, wait, note, drive, release, analog, thermistor, pulses or tacho"
                : "a step can only do one thing");
        }
        if (Timeout is not null)
        {
            _ = Scenario.ParseDuration(Timeout);
        }
        if (Wait is not null)
        {
            _ = Scenario.ParseDuration(Wait);
        }
        Drive?.Validate("drive", needsLevel: true);
        Release?.Validate("release");
        Analog?.Validate("analog", needsValue: true);
        Thermistor?.Validate("thermistor", needsCelsius: true);
        Pulses?.Validate("pulses", needsFrequency: true);
        Tacho?.Validate("tacho", needsFan: true);
    }

    /// <summary>How long a code may take</summary>
    [YamlIgnore]
    public TimeSpan? TimeoutSpan => Timeout is null ? null : Scenario.ParseDuration(Timeout);
}

/// <summary>An input of a board, and what to drive it with</summary>
public sealed class PinInput
{
    /// <summary>The board's CAN address</summary>
    public int Board { get; set; } = -1;

    /// <summary>The port name the board's firmware gives the pin, such as io0.in or temp0</summary>
    public string Port { get; set; } = string.Empty;

    /// <summary>For drive: high or low (true or false)</summary>
    public string? Level { get; set; }

    /// <summary>For analog: the fraction of the reference</summary>
    public double? Value { get; set; }

    /// <summary>For thermistor: the temperature in C</summary>
    public double? Celsius { get; set; }

    /// <summary>For drive: change the input only once a driver on the board has made so many more steps</summary>
    public StepCount? AfterSteps { get; set; }

    /// <summary>For pulses: the frequency in Hz, 0 to stop</summary>
    public double? Frequency { get; set; }

    /// <summary>For tacho: the fan's output, such as out3</summary>
    public string? Fan { get; set; }

    /// <summary>For tacho: the fan's speed at full duty, <see cref="BoardIo.DefaultTachoMaxRpm"/> if not given</summary>
    public double? MaxRpm { get; set; }

    /// <summary>For tacho: pulses a revolution, <see cref="BoardIo.DefaultTachoPulsesPerRev"/> if not given</summary>
    public double? PulsesPerRev { get; set; }

    /// <summary>For tacho: whether the fan runs when its output is low</summary>
    public bool? Inverted { get; set; }

    /// <summary>The level as a bool</summary>
    [YamlIgnore]
    public bool LevelValue => Level?.ToLowerInvariant() switch
    {
        "high" or "true" or "1" or "on" => true,
        "low" or "false" or "0" or "off" => false,
        _ => throw new FormatException($"\"{Level}\" is not a level: high or low")
    };

    /// <inheritdoc/>
    public override string ToString() => $"{Board}.{Port}{(Level is null ? string.Empty : " " + Level)}" +
                                         (Value is double v ? $" {v}" : string.Empty) + (Celsius is double c ? $" {c}C" : string.Empty) +
                                         (AfterSteps is StepCount after ? $" after {after.Steps} steps of driver {after.Driver}" : string.Empty) +
                                         (Frequency is double f ? $" {f}Hz" : string.Empty) + (Fan is string fan ? $" following {fan}" : string.Empty);

    internal void Validate(string action, bool needsLevel = false, bool needsValue = false, bool needsCelsius = false,
                           bool needsFrequency = false, bool needsFan = false)
    {
        List<string> missing = [];
        if (Board < 0)
        {
            missing.Add("board");
        }
        if (Port.Length == 0)
        {
            missing.Add("port");
        }
        if (needsLevel && Level is null)
        {
            missing.Add("level");
        }
        if (needsValue && Value is null)
        {
            missing.Add("value");
        }
        if (needsCelsius && Celsius is null)
        {
            missing.Add("celsius");
        }
        if (needsFrequency && Frequency is null)
        {
            missing.Add("frequency");
        }
        if (needsFan && Fan is null)
        {
            missing.Add("fan");
        }
        if (AfterSteps is StepCount after && (after.Driver < 0 || after.Steps == 0))
        {
            missing.Add("afterSteps.driver and afterSteps.steps");
        }
        if (missing.Count > 0)
        {
            throw new FormatException($"{action} needs {string.Join(", ", missing)}");
        }
        if (needsLevel)
        {
            _ = LevelValue;
        }
    }
}

/// <summary>A number of steps of one of a board's drivers</summary>
public sealed class StepCount
{
    /// <summary>The driver on the board, from 0</summary>
    public int Driver { get; set; } = -1;

    /// <summary>How many more steps</summary>
    public ulong Steps { get; set; }
}
