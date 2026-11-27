using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DuetEmulation;
using DuetEmulation.Boards;
using DuetEmulation.Captures;
using DuetEmulation.Scenarios;
using DuetEmulation.Sessions;

namespace DuetEmulator;

/// <summary>What the session server's API answers with, as JSON</summary>
internal static class ApiJson
{
    /// <summary>How the API writes JSON: as captures are written, with enums by name</summary>
    public static readonly JsonSerializerOptions Options = new(MachineCapture.JsonOptions)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // Only ever read by the bench's own clients, so text such as a G-code's quotes is left as it is
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>JSON as text, written as <see cref="Options"/> write it</summary>
    public static string ToText(this JsonNode? node) => node?.ToJsonString(Options) ?? "null";

    /// <summary>A value as JSON, in the API's conventions</summary>
    public static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    /// <summary>Events as JSON, each with its kind</summary>
    public static JsonArray Events(IEnumerable<CaptureEvent> events) => Node<IReadOnlyList<CaptureEvent>>([.. events])!.AsArray();

    /// <summary>What a session started from</summary>
    public static JsonObject Scenario(Scenario scenario) => new()
    {
        ["name"] = scenario.Name,
        ["description"] = scenario.Description,
        ["stacks"] = new JsonArray([.. scenario.Stacks.Select(s => (JsonNode)s)]),
        ["boards"] = new JsonObject(scenario.Boards.OrderBy(b => b.Key).Select(b => KeyValuePair.Create(b.Key.ToString(), (JsonNode?)b.Value))),
        ["config"] = scenario.Config,
        ["files"] = new JsonObject(scenario.Files.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))),
        ["steps"] = new JsonArray([.. scenario.Steps.Select(Step)])
    };

    /// <summary>A step as the JSON a client sends to run it</summary>
    public static JsonObject Step(ScenarioStep step) => Node(step)!.AsObject();

    /// <summary>A step of a session; with <paramref name="events"/>, what it recorded on each stack</summary>
    public static JsonObject SessionStep(SessionStep step, Session? events = null)
    {
        JsonObject stacks = [];
        foreach ((string name, StackStepResult result) in step.Stacks)
        {
            JsonObject stack = new()
            {
                ["started"] = result.Started,
                ["finished"] = result.Finished,
                ["reply"] = result.Reply is null ? null : new JsonObject { ["severity"] = Node(result.Reply.Severity), ["text"] = result.Reply.Text },
                ["error"] = result.Error
            };
            if (events?.Stacks.FirstOrDefault(s => s.Name == name) is SessionStack session)
            {
                stack["events"] = Events(CaptureComparison.EventsOfStep(session.Capture.Events, step.CaptureStep));
            }
            stacks[name] = stack;
        }
        return new JsonObject
        {
            ["number"] = step.Number,
            ["captureStep"] = step.CaptureStep,
            ["step"] = step.Step.Describe(),
            ["yaml"] = step.Step.ToYaml(),
            ["action"] = Step(step.Step),
            ["state"] = Node(step.State),
            ["stacks"] = stacks,
            ["differences"] = new JsonArray([.. step.Differences.Select(d => (JsonNode)$"{d.Aspect}: {d.Description}")])
        };
    }

    /// <summary>What each board of a stack has done</summary>
    public static JsonArray Boards(IReadOnlyList<BoardState> boards) => Node(boards)!.AsArray();

    /// <summary>A board of a session, with its ports</summary>
    public static JsonObject Board(int address, BoardType type) => new()
    {
        ["address"] = address,
        ["type"] = type.Name,
        ["drivers"] = type.StepPins.Count,
        ["ports"] = Node(BoardPorts.Of(type))
    };

    /// <summary>A kind of expansion board a session can have</summary>
    public static JsonObject BoardType(BoardType type) => new()
    {
        ["name"] = type.Name,
        ["defaultAddress"] = type.DefaultAddress,
        ["addresses"] = type.DefaultAddress is int fixedAddress ? new JsonArray(fixedAddress) : new JsonArray(1, (1 << type.AddressSwitches.Count) - 1),
        ["drivers"] = type.StepPins.Count,
        ["firmware"] = type.FirmwarePreset
    };

    /// <summary>Two captures lined up</summary>
    public static JsonObject Alignment(CaptureAlignment alignment, string first, string second) => new()
    {
        ["stacks"] = new JsonArray(first, second),
        ["steps"] = Node(alignment.Steps),
        ["can"] = Node(alignment.Can),
        ["pins"] = Node(alignment.Pins),
        ["differences"] = Node(alignment.Differences)
    };
}
