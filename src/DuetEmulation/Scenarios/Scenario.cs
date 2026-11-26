using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DuetEmulation.Boards;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DuetEmulation.Scenarios;

/// <summary>
/// A script for the bench, read from a YAML file: the boards, config.g, and the steps to run, each a
/// code to send, an input to drive or time to let pass. The same scenario runs on each stack, and the
/// captures it makes can be compared
/// </summary>
/// <example>
/// <code>
/// name: an endstop stops a homing move
/// boards:
///   1: EXP3HC
/// config: |
///   M584 X1.0
///   M574 X1 S1 P"1.io0.in"
/// steps:
///   - code: G91
///   - start: G1 H1 X-100 F600
///   - drive: { board: 1, port: io0.in, level: high, after: { driver: 1.0, steps: 2000 } }
///   - await: G1 H1 X-100 F600
/// </code>
/// </example>
public sealed partial class Scenario
{
    /// <summary>What it is</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The stacks it runs on, rrf and dsf by default</summary>
    public List<string> Stacks { get; set; } = ["rrf", "dsf"];

    /// <summary>The expansion boards: board type name by CAN address</summary>
    public Dictionary<int, string> Boards { get; set; } = [];

    /// <summary>config.g, after the stack's own first line</summary>
    public string Config { get; set; } = string.Empty;

    /// <summary>Further files for the SD card, by path on the card</summary>
    public Dictionary<string, string> Files { get; set; } = [];

    /// <summary>Whether the capture starts when the boards are created rather than once they have booted</summary>
    public bool CaptureBoot { get; set; }

    /// <summary>The steps</summary>
    public List<ScenarioStep> Steps { get; set; } = [];

    /// <summary>The expansion boards as board types</summary>
    /// <exception cref="ArgumentException">A board type has no emulation</exception>
    public IReadOnlyDictionary<int, BoardType> BoardTypes() =>
        Boards.ToDictionary(b => b.Key, b => DuetEmulation.Boards.BoardTypes.Expansion(b.Value));

    /// <summary>The stacks to run on</summary>
    /// <exception cref="ArgumentException">One is neither rrf nor dsf</exception>
    public IReadOnlyList<Stack> StackList() => [.. Stacks.Select(ParseStack)];

    /// <summary>A stack by the name scenarios use</summary>
    public static Stack ParseStack(string name) => name.ToLowerInvariant() switch
    {
        "rrf" => Stack.Rrf,
        "dsf" => Stack.Dsf,
        _ => throw new ArgumentException($"\"{name}\" is not a stack: rrf or dsf")
    };

    /// <summary>Read a scenario from YAML</summary>
    /// <exception cref="FormatException">It is not a valid scenario</exception>
    public static Scenario Parse(string yaml, string source = "scenario")
    {
        Scenario scenario;
        try
        {
            scenario = Deserializer.Deserialize<Scenario>(yaml) ?? new Scenario();
        }
        catch (YamlException e)
        {
            throw new FormatException($"{source}: {e.Message}{(e.InnerException is null ? string.Empty : ": " + e.InnerException.Message)}", e);
        }
        if (string.IsNullOrWhiteSpace(scenario.Name))
        {
            scenario.Name = Path.GetFileNameWithoutExtension(source);
        }
        for (int i = 0; i < scenario.Steps.Count; i++)
        {
            try
            {
                scenario.Steps[i].Validate();
            }
            catch (FormatException e)
            {
                throw new FormatException($"{source}: step {i + 1}: {e.Message}", e);
            }
        }
        try
        {
            _ = scenario.BoardTypes();
            _ = scenario.StackList();
        }
        catch (ArgumentException e)
        {
            throw new FormatException($"{source}: {e.Message}", e);
        }
        return scenario;
    }

    /// <summary>Read steps from YAML: a list of steps, or one step</summary>
    /// <exception cref="FormatException">They are not valid steps</exception>
    public static IReadOnlyList<ScenarioStep> ParseSteps(string yaml)
    {
        List<ScenarioStep> steps;
        try
        {
            steps = yaml.TrimStart().StartsWith('-') || yaml.TrimStart().StartsWith('[')
                ? Deserializer.Deserialize<List<ScenarioStep>>(yaml) ?? []
                : [Deserializer.Deserialize<ScenarioStep>(yaml) ?? new ScenarioStep()];
        }
        catch (YamlException e)
        {
            throw new FormatException($"{e.Message}{(e.InnerException is null ? string.Empty : ": " + e.InnerException.Message)}", e);
        }
        for (int i = 0; i < steps.Count; i++)
        {
            try
            {
                steps[i].Validate();
            }
            catch (FormatException e)
            {
                throw new FormatException($"step {i + 1}: {e.Message}", e);
            }
        }
        return steps;
    }

    /// <summary>Read a scenario file</summary>
    public static Scenario Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>A duration as scenarios write it: <c>500ms</c>, <c>2s</c>, <c>1.5</c> (seconds)</summary>
    /// <exception cref="FormatException">It is not one</exception>
    public static TimeSpan ParseDuration(string text)
    {
        Match match = DurationPattern().Match(text.Trim());
        if (!match.Success)
        {
            throw new FormatException($"\"{text}\" is not a duration such as 500ms or 2s");
        }
        double value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(value),
            "us" => TimeSpan.FromMicroseconds(value),
            "min" => TimeSpan.FromMinutes(value),
            _ => TimeSpan.FromSeconds(value)
        };
    }

    [GeneratedRegex(@"^(?<value>\d+(\.\d+)?)\s*(?<unit>ms|us|s|min)?$")]
    private static partial Regex DurationPattern();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();
}
