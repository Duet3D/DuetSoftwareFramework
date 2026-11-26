using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DuetEmulation.Captures;

/// <summary>What a comparison of two captures leaves out, and how close is equal</summary>
public sealed record ComparisonOptions
{
    /// <summary>
    /// CAN message types left out: by default the periodic ones, whose number depends on how long a
    /// step takes rather than on what it does
    /// </summary>
    public IReadOnlyList<string> IgnoredCanTypes { get; init; } = ["TimeSync", "StatusReport", "BoardStatusReport", "BoardStatusReportV1", "DriversStatusReport", "FansReport"];

    /// <summary>
    /// Fields of decoded CAN messages left out: by default the request id, which each stack numbers its
    /// own way, and the step clock times a move starts at and an input changed at, which depend on when
    /// each stack scheduled the move
    /// </summary>
    public IReadOnlyList<string> IgnoredCanFields { get; init; } = ["RequestId", "WhenToExecute", "When"];

    /// <summary>
    /// Object model paths left out, with their descendants (<c>[*]</c> matches any index): by default the
    /// main board's description, which differs between a standalone main board and DuetCANMaster
    /// </summary>
    public IReadOnlyList<string> IgnoredPaths { get; init; } = ["boards[0]"];

    /// <summary>Whether pin changes are compared</summary>
    public bool ComparePins { get; init; } = true;

    /// <summary>
    /// How far apart numbers may be and still be equal: RepRapFirmware reports the object model to a
    /// few decimal places, DuetControlServer in full
    /// </summary>
    public double NumberTolerance { get; init; } = 0.001;

    /// <summary>
    /// How far apart, in seconds after the start of their step, two matching events may be; null to
    /// ignore timing
    /// </summary>
    public double? TimeTolerance { get; init; }
}

/// <summary>One way two captures differ</summary>
/// <param name="Step">The number of the step, from 1</param>
/// <param name="Action">What the step did: the code, the input or the note that starts it</param>
/// <param name="Aspect">reply, can, om, pin, time or steps</param>
/// <param name="Description">How they differ, the first capture first</param>
public sealed record CaptureDifference(int Step, string Action, string Aspect, string Description)
{
    /// <inheritdoc/>
    public override string ToString() => $"step {Step} ({Action}) {Aspect}: {Description}";
}

/// <summary>
/// The differences between two captures of the same script, such as one on RepRapFirmware and one on
/// DuetControlServer
/// </summary>
/// <remarks>
/// <para>
/// A step starts at each code sent, input driven or note, and holds everything that followed until the
/// next; what came before the first is the step called start. Steps are paired in order.
/// </para>
/// <para>
/// Replies are compared step by step. The object model is compared by state: at the end of each step,
/// every path either capture changed in that step must have the same value in both. CAN frames and pin
/// changes are compared as one sequence each over the whole capture, aligned so that one missing frame
/// is one difference, and so that a frame one stack sends a moment later than the other, in the next
/// step, is still the same frame; a difference is reported in the step of the frame it is about.
/// </para>
/// </remarks>
public static class CaptureComparison
{
    /// <summary>Every difference, in step order</summary>
    public static IReadOnlyList<CaptureDifference> Compare(MachineCapture first, MachineCapture second, ComparisonOptions? options = null)
    {
        options ??= new ComparisonOptions();
        List<Step> a = Steps(first), b = Steps(second);
        List<CaptureDifference> differences = [];
        int common = Math.Min(a.Count, b.Count);
        for (int i = common; i < Math.Max(a.Count, b.Count); i++)
        {
            Step extra = i < a.Count ? a[i] : b[i];
            differences.Add(new(i + 1, extra.Action, "steps", $"only the {(i < a.Count ? "first" : "second")} capture has this step"));
        }
        for (int i = 0; i < common; i++)
        {
            if (a[i].Action != b[i].Action)
            {
                differences.Add(new(i + 1, a[i].Action, "steps", $"the second capture's step is {b[i].Action}"));
                common = i;
                break;
            }
        }

        CompareReplies(a, b, common, options, differences);
        CompareObjectModels(a, b, common, options, differences);
        CompareSequences("can", a, b, common, step => Can(step, options), options, differences);
        if (options.ComparePins)
        {
            CompareSequences("pin", a, b, common, Pins, options, differences);
        }
        return [.. differences.OrderBy(d => d.Step)];
    }

    /// <summary>How many steps a capture has, counting the start</summary>
    public static int StepCount(MachineCapture capture) => Steps(capture).Count;

    /// <summary>The differences as readable text</summary>
    public static string Report(IReadOnlyList<CaptureDifference> differences, MachineCapture first, MachineCapture second)
    {
        StringBuilder report = new();
        report.AppendLine($"first:  {first.Header.Name} on {first.Header.Stack}");
        report.AppendLine($"second: {second.Header.Name} on {second.Header.Stack}");
        if (differences.Count == 0)
        {
            report.AppendLine("No differences");
            return report.ToString();
        }
        report.AppendLine($"{differences.Count} difference{(differences.Count == 1 ? string.Empty : "s")}");
        foreach (IGrouping<int, CaptureDifference> step in differences.GroupBy(d => d.Step))
        {
            report.AppendLine();
            report.AppendLine($"step {step.Key}: {step.First().Action}");
            foreach (CaptureDifference difference in step)
            {
                report.AppendLine($"  {difference.Aspect,-5} {difference.Description.Replace("\n", "\n        ")}");
            }
        }
        return report.ToString();
    }

    private sealed record Step(int Number, string Action, double Start, List<CaptureEvent> Events);

    private static List<Step> Steps(MachineCapture capture)
    {
        List<Step> steps = [];
        Step current = new(1, "start", 0, []);
        foreach (CaptureEvent e in capture.Events)
        {
            string? action = e switch
            {
                CodeSent code => code.Code,
                InputDriven input => $"input {input.Board}.{input.Port} {input.Value}",
                Note note => $"# {note.Text}",
                _ => null
            };
            if (action is not null)
            {
                steps.Add(current);
                current = new Step(steps.Count + 1, action, e.T, []);
                continue;
            }
            current.Events.Add(e);
        }
        steps.Add(current);
        return steps;
    }

    private static void CompareReplies(List<Step> a, List<Step> b, int count, ComparisonOptions options, List<CaptureDifference> differences)
    {
        for (int i = 0; i < count; i++)
        {
            CodeReplied? replyA = a[i].Events.OfType<CodeReplied>().FirstOrDefault(), replyB = b[i].Events.OfType<CodeReplied>().FirstOrDefault();
            if (replyA?.Severity != replyB?.Severity || replyA?.Text != replyB?.Text)
            {
                differences.Add(new(i + 1, a[i].Action, "reply", $"{Describe(replyA)}\nvs {Describe(replyB)}"));
            }
            else if (TimeDifference(replyA, a[i], replyB, b[i], options) is string time)
            {
                differences.Add(new(i + 1, a[i].Action, "time", time));
            }
        }
    }

    private static string Describe(CodeReplied? reply) =>
        reply is null ? "no reply" : $"{reply.Severity}: {(reply.Text.Length == 0 ? "(empty)" : reply.Text)}";

    private static string? TimeDifference(CaptureEvent? a, Step stepA, CaptureEvent? b, Step stepB, ComparisonOptions options)
    {
        if (options.TimeTolerance is not double tolerance || a is null || b is null)
        {
            return null;
        }
        double offsetA = a.T - stepA.Start, offsetB = b.T - stepB.Start;
        return Math.Abs(offsetA - offsetB) <= tolerance && stepA.Number == stepB.Number
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"{a.Describe()} at {offsetA:0.000000}s into step {stepA.Number} vs {offsetB:0.000000}s into step {stepB.Number}");
    }

    // The value each path has at the end of each step, compared for the paths either capture changed in it
    private static void CompareObjectModels(List<Step> a, List<Step> b, int count, ComparisonOptions options, List<CaptureDifference> differences)
    {
        Dictionary<string, JsonNode?> stateA = [], stateB = [];
        for (int i = 0; i < count; i++)
        {
            HashSet<string> changed = [.. Apply(a[i], stateA), .. Apply(b[i], stateB)];
            foreach (string path in changed.Where(p => !ObjectModelDiff.Matches(p, options.IgnoredPaths)).Order(StringComparer.Ordinal))
            {
                bool inA = stateA.TryGetValue(path, out JsonNode? valueA), inB = stateB.TryGetValue(path, out JsonNode? valueB);
                if (inA == inB && Same(valueA, valueB, options.NumberTolerance))
                {
                    continue;
                }
                differences.Add(new(i + 1, a[i].Action, "om", $"{path}: {(inA ? Format(valueA) : "absent")} vs {(inB ? Format(valueB) : "absent")}"));
            }
        }
    }

    // Bring the state up to the end of a step, and return the paths that changed in it
    private static IEnumerable<string> Apply(Step step, Dictionary<string, JsonNode?> state)
    {
        List<string> changed = [];
        foreach (CaptureEvent e in step.Events)
        {
            if (e is ObjectModelSnapshot snapshot)
            {
                state.Clear();
                foreach ((string path, JsonNode? value) in ObjectModelDiff.Flatten(snapshot.Model))
                {
                    state[path] = value;
                }
            }
            else if (e is ObjectModelChanged change)
            {
                // A change to or from an object or array replaces everything under its path
                foreach (string stale in state.Keys.Where(k => k.StartsWith(change.Path + ".", StringComparison.Ordinal) ||
                                                               k.StartsWith(change.Path + "[", StringComparison.Ordinal)).ToList())
                {
                    state.Remove(stale);
                    changed.Add(stale);
                }
                state.Remove(change.Path);
                if (change.New is JsonObject or JsonArray)
                {
                    foreach ((string path, JsonNode? value) in ObjectModelDiff.Flatten(change.New))
                    {
                        string full = path.StartsWith('[') ? change.Path + path : $"{change.Path}.{path}";
                        state[full] = value;
                        changed.Add(full);
                    }
                }
                else if (change.New is not null)
                {
                    state[change.Path] = change.New;
                }
                else
                {
                    state[change.Path] = null;
                }
                changed.Add(change.Path);
            }
        }
        return changed;
    }

    private static bool Same(JsonNode? a, JsonNode? b, double tolerance)
    {
        if (a is JsonValue x && b is JsonValue y && x.GetValueKind() == JsonValueKind.Number && y.GetValueKind() == JsonValueKind.Number)
        {
            double p = double.Parse(x.ToJsonString(), CultureInfo.InvariantCulture), q = double.Parse(y.ToJsonString(), CultureInfo.InvariantCulture);
            return Math.Abs(p - q) <= tolerance * Math.Max(1, Math.Max(Math.Abs(p), Math.Abs(q)));
        }
        return JsonNode.DeepEquals(a, b);
    }

    private static string Format(JsonNode? value) => value?.ToJsonString() ?? "null";

    // What a sequence comparison compares: what the event is (its kind, addresses and type), what it
    // carries, compared with the number tolerance, the event itself and the step it is in
    private sealed record Item(string Head, JsonNode? Body, CaptureEvent Event, Step Step)
    {
        public string Describe() => Body is null ? Head : $"{Head} {Body.ToJsonString()}";
    }

    private static IEnumerable<(string Head, JsonNode? Body, CaptureEvent Event)> Can(Step step, ComparisonOptions options) =>
        step.Events.OfType<CanFrameSent>()
            .Where(f => !options.IgnoredCanTypes.Contains(f.Type))
            .Select(f => ($"CAN {f.Source}->{f.Destination} {f.Type}{(f.Response ? " (response)" : string.Empty)}", Payload(f, options), (CaptureEvent)f));

    private static JsonNode Payload(CanFrameSent frame, ComparisonOptions options)
    {
        if (frame.Message is null)
        {
            return JsonValue.Create(frame.Data);
        }
        JsonObject message = frame.Message.DeepClone().AsObject();
        foreach (string field in options.IgnoredCanFields)
        {
            message.Remove(field);
        }
        return message;
    }

    private static IEnumerable<(string Head, JsonNode? Body, CaptureEvent Event)> Pins(Step step) =>
        step.Events.OfType<PinChanged>()
            .Select(p => ($"PIN {p.Board}.{string.Join('/', p.Ports)} {p.Output}",
                          p.Duty is double duty ? (JsonNode)new JsonObject { ["duty"] = duty, ["frequency"] = p.Frequency } : null, (CaptureEvent)p));

    // Whether two JSON values are the same, numbers within the tolerance
    private static bool SameDeep(JsonNode? a, JsonNode? b, double tolerance) => (a, b) switch
    {
        (JsonObject x, JsonObject y) => x.Count == y.Count && x.All(p => y.ContainsKey(p.Key) && SameDeep(p.Value, y[p.Key], tolerance)),
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Select((v, i) => SameDeep(v, y[i], tolerance)).All(same => same),
        _ => Same(a, b, tolerance)
    };

    // Align the two captures' sequences of one kind of event (longest common subsequence) over the steps
    // both have, and report what each has that the other has not, in the step it is in
    private static void CompareSequences(string aspect, List<Step> stepsA, List<Step> stepsB, int count,
                                         Func<Step, IEnumerable<(string Head, JsonNode? Body, CaptureEvent Event)>> select, ComparisonOptions options,
                                         List<CaptureDifference> differences)
    {
        List<Item> a = [.. stepsA.Take(count).SelectMany(s => select(s).Select(e => new Item(e.Head, e.Body, e.Event, s)))];
        List<Item> b = [.. stepsB.Take(count).SelectMany(s => select(s).Select(e => new Item(e.Head, e.Body, e.Event, s)))];
        bool Equal(int i, int j) => a[i].Head == b[j].Head && SameDeep(a[i].Body, b[j].Body, options.NumberTolerance);
        int[,] lcs = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
        {
            for (int j = b.Count - 1; j >= 0; j--)
            {
                lcs[i, j] = Equal(i, j) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }
        int x = 0, y = 0;
        while (x < a.Count || y < b.Count)
        {
            if (x < a.Count && y < b.Count && Equal(x, y))
            {
                if (TimeDifference(a[x].Event, a[x].Step, b[y].Event, b[y].Step, options) is string time)
                {
                    differences.Add(new(a[x].Step.Number, a[x].Step.Action, "time", time));
                }
                x++;
                y++;
            }
            else if (y >= b.Count || (x < a.Count && lcs[x + 1, y] >= lcs[x, y + 1]))
            {
                differences.Add(new(a[x].Step.Number, a[x].Step.Action, aspect, $"only in the first: {a[x].Describe()}"));
                x++;
            }
            else
            {
                differences.Add(new(b[y].Step.Number, b[y].Step.Action, aspect, $"only in the second: {b[y].Describe()}"));
                y++;
            }
        }
    }
}
