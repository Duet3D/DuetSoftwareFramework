using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DuetEmulation.Boards;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
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

    /// <summary>What it does and why, from the comment the file starts with; null if it has none</summary>
    [YamlIgnore]
    public string? Description { get; set; }

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
        scenario.Description = LeadingComment(yaml);
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

    // The comment lines a file starts with, as one paragraph each run of them; null if it starts with none
    private static string? LeadingComment(string yaml)
    {
        List<string> paragraphs = [];
        List<string> lines = [];
        foreach (string line in yaml.Split('\n').Select(l => l.Trim()))
        {
            if (!line.StartsWith('#'))
            {
                break;
            }
            string text = line.TrimStart('#').Trim();
            if (text.Length == 0)
            {
                if (lines.Count > 0)
                {
                    paragraphs.Add(string.Join(' ', lines));
                    lines.Clear();
                }
                continue;
            }
            lines.Add(text);
        }
        if (lines.Count > 0)
        {
            paragraphs.Add(string.Join(' ', lines));
        }
        return paragraphs.Count == 0 ? null : string.Join('\n', paragraphs);
    }

    /// <summary>
    /// The scenario as YAML that <see cref="Parse"/> reads back the same: its description as the comment
    /// the file starts with, then what differs from the defaults, inputs written on one line as the
    /// examples write them
    /// </summary>
    public string ToYaml()
    {
        StringBuilder yaml = new();
        foreach (string paragraph in (Description ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (yaml.Length > 0)
            {
                yaml.AppendLine("#");
            }
            yaml.Append("# ").AppendLine(paragraph);
        }
        Document document = new()
        {
            Name = Name,
            Stacks = Stacks.Order().SequenceEqual(DefaultStacks.Order()) ? null : Stacks,
            Boards = Boards.Count == 0 ? null : new SortedDictionary<int, string>(Boards),
            Config = Config.Length == 0 ? null : Config.EndsWith('\n') ? Config : Config + "\n",
            Files = Files.Count == 0 ? null : new SortedDictionary<string, string>(Files, StringComparer.Ordinal),
            CaptureBoot = CaptureBoot ? true : null,
            Steps = Steps.Count == 0 ? null : Steps
        };
        yaml.Append(Serializer.Serialize(document));
        return yaml.ToString();
    }

    private static readonly IReadOnlyList<string> DefaultStacks = new Scenario().Stacks;

    // What ToYaml writes, in the order the examples write it, a null key left out
    private sealed class Document
    {
        public string Name { get; set; } = string.Empty;
        public List<string>? Stacks { get; set; }
        public SortedDictionary<int, string>? Boards { get; set; }
        public string? Config { get; set; }
        public SortedDictionary<string, string>? Files { get; set; }
        public bool? CaptureBoot { get; set; }
        public List<ScenarioStep>? Steps { get; set; }
    }

    // Inputs on one line, and text of several lines as a literal block
    private sealed class ScenarioEventEmitter(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(MappingStartEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Type == typeof(PinInput) || eventInfo.Source.Type == typeof(StepCount))
            {
                eventInfo.Style = MappingStyle.Flow;
            }
            base.Emit(eventInfo, emitter);
        }

        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Value is string text && text.Contains('\n'))
            {
                eventInfo.Style = ScalarStyle.Literal;
            }
            base.Emit(eventInfo, emitter);
        }
    }

    internal static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithIndentedSequences()
        .WithEventEmitter(next => new ScenarioEventEmitter(next))
        .DisableAliases()
        .Build();

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

    /// <summary>A duration as scenarios write it, in the largest unit that keeps it a whole or short number</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        double us = duration.Ticks / (double)TimeSpan.TicksPerMicrosecond;
        return us switch
        {
            _ when us % 1_000_000 == 0 || us >= 1_000_000 => FormattableString.Invariant($"{us / 1_000_000:0.######}s"),
            _ when us % 1000 == 0 || us >= 1000 => FormattableString.Invariant($"{us / 1000:0.###}ms"),
            _ => FormattableString.Invariant($"{us:0.#}us")
        };
    }

    [GeneratedRegex(@"^(?<value>\d+(\.\d+)?)\s*(?<unit>ms|us|s|min)?$")]
    private static partial Regex DurationPattern();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();
}
