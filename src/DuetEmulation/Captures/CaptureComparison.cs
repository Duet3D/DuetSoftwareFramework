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
    /// Fields of decoded CAN messages left out, at any depth: by default the request id, which each stack
    /// numbers its own way, and the step clock times a move starts at and an input changed at, which
    /// depend on when each stack scheduled the move
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
/// A pair of events a comparison lined up: the same event in both captures, or one only one of them has
/// </summary>
/// <param name="First">The event in the first capture, or null if only the second has it</param>
/// <param name="FirstStep">The step <paramref name="First"/> is in</param>
/// <param name="Second">The event in the second capture, or null if only the first has it</param>
/// <param name="SecondStep">The step <paramref name="Second"/> is in</param>
public sealed record AlignedEvent(CaptureEvent? First, int? FirstStep, CaptureEvent? Second, int? SecondStep)
{
    /// <summary>Whether both captures have it</summary>
    public bool Matched => First is not null && Second is not null;

    /// <summary>Whether it is in a step, in either capture</summary>
    public bool IsIn(int step) => FirstStep == step || SecondStep == step;
}

/// <summary>An object model path's value at some point of a capture</summary>
/// <param name="Present">Whether the model has the path</param>
/// <param name="Value">Its value</param>
public sealed record ModelValue(bool Present, JsonNode? Value)
{
    /// <summary>A path the model does not have</summary>
    public static readonly ModelValue Absent = new(false, null);

    /// <inheritdoc/>
    public override string ToString() => Present ? Value?.ToJsonString() ?? "null" : "absent";
}

/// <summary>An object model path either capture changed in a step, with its value in each before and after it</summary>
public sealed record AlignedPath(string Path, ModelValue FirstBefore, ModelValue First, ModelValue SecondBefore, ModelValue Second, bool Same);

/// <summary>A step of each capture, paired by their order</summary>
/// <param name="Number">The step's number, from 1; step 1 is what came before the first action</param>
/// <param name="FirstAction">What the first capture's step did, or null if it has no such step</param>
/// <param name="SecondAction">What the second capture's step did, or null if it has no such step</param>
/// <param name="Compared">Whether the steps were compared: both captures have them, and every step before did the same</param>
/// <param name="FirstReply">The first capture's reply in the step</param>
/// <param name="SecondReply">The second capture's reply in the step</param>
/// <param name="ObjectModel">The paths either capture changed in the step, for a compared step</param>
public sealed record AlignedStep(int Number, string? FirstAction, string? SecondAction, bool Compared, CodeReplied? FirstReply, CodeReplied? SecondReply,
                                 IReadOnlyList<AlignedPath> ObjectModel)
{
    /// <summary>What the step did</summary>
    public string Action => FirstAction ?? SecondAction ?? string.Empty;
}

/// <summary>Two captures lined up step by step and event by event, and how they differ</summary>
/// <param name="Steps">Each step</param>
/// <param name="Can">The CAN frames compared, lined up over the whole captures</param>
/// <param name="Pins">The pin changes compared, lined up over the whole captures; empty if pins are not compared</param>
/// <param name="Differences">Every difference, in step order</param>
public sealed record CaptureAlignment(IReadOnlyList<AlignedStep> Steps, IReadOnlyList<AlignedEvent> Can, IReadOnlyList<AlignedEvent> Pins,
                                      IReadOnlyList<CaptureDifference> Differences);

/// <summary>
/// The differences between two captures of the same script, such as one on RepRapFirmware and one on
/// DuetControlServer
/// </summary>
/// <remarks>
/// <para>
/// A step starts at each code sent or awaited, wait, input driven or note, and holds everything that
/// followed until the next; what came before the first is the step called start. Steps are paired in order.
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
    public static IReadOnlyList<CaptureDifference> Compare(MachineCapture first, MachineCapture second, ComparisonOptions? options = null) =>
        Align(first, second, options).Differences;

    /// <summary>Line the two captures up step by step and event by event, and find how they differ</summary>
    public static CaptureAlignment Align(MachineCapture first, MachineCapture second, ComparisonOptions? options = null)
    {
        options ??= new ComparisonOptions();
        List<Step> a = Steps(first.Events), b = Steps(second.Events);
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
        List<IReadOnlyList<AlignedPath>> models = CompareObjectModels(a, b, common, options, differences);
        List<AlignedEvent> can = CompareSequences("can", a, b, common, step => Can(step, options), options, differences);
        List<AlignedEvent> pins = options.ComparePins ? CompareSequences("pin", a, b, common, Pins, options, differences) : [];

        List<AlignedStep> steps = [];
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            Step? stepA = i < a.Count ? a[i] : null, stepB = i < b.Count ? b[i] : null;
            steps.Add(new AlignedStep(i + 1, stepA?.Action, stepB?.Action, i < common, stepA?.Reply, stepB?.Reply, i < common ? models[i] : []));
        }
        return new CaptureAlignment(steps, can, pins, [.. differences.OrderBy(d => d.Step)]);
    }

    /// <summary>How many steps a capture has, counting the start</summary>
    public static int StepCount(MachineCapture capture) => Steps(capture.Events).Count;

    /// <summary>The events of one step of a capture: the event that starts it, then everything until the next step</summary>
    /// <param name="events">The capture's events</param>
    /// <param name="step">The step's number; step 1 is what came before the first action</param>
    public static IReadOnlyList<CaptureEvent> EventsOfStep(IReadOnlyList<CaptureEvent> events, int step)
    {
        List<CaptureEvent> result = [];
        int current = 1;
        foreach (CaptureEvent e in events)
        {
            if (StepAction(e) is not null && ++current > step)
            {
                break;
            }
            if (current == step)
            {
                result.Add(e);
            }
        }
        return result;
    }

    /// <summary>What a capture event does to start a step, or null if it does not start one</summary>
    public static string? StepAction(CaptureEvent e) => e switch
    {
        CodeSent code => code.Code,
        CodeAwaited awaited => $"await {awaited.Code}",
        WaitStarted wait => $"wait {wait.Duration}",
        InputDriven input => $"input {input.Board}.{input.Port} {input.Value}",
        Note note => $"# {note.Text}",
        _ => null
    };

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

    private sealed record Step(int Number, string Action, double Start, List<CaptureEvent> Events)
    {
        public CodeReplied? Reply => Events.OfType<CodeReplied>().FirstOrDefault();
    }

    private static List<Step> Steps(IReadOnlyList<CaptureEvent> events)
    {
        List<Step> steps = [];
        Step current = new(1, "start", 0, []);
        foreach (CaptureEvent e in events)
        {
            if (StepAction(e) is string action)
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
            CodeReplied? replyA = a[i].Reply, replyB = b[i].Reply;
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
    private static List<IReadOnlyList<AlignedPath>> CompareObjectModels(List<Step> a, List<Step> b, int count, ComparisonOptions options,
                                                                        List<CaptureDifference> differences)
    {
        Dictionary<string, JsonNode?> stateA = [], stateB = [];
        List<IReadOnlyList<AlignedPath>> steps = [];
        for (int i = 0; i < count; i++)
        {
            Dictionary<string, ModelValue> beforeA = Apply(a[i], stateA), beforeB = Apply(b[i], stateB);
            List<AlignedPath> paths = [];
            foreach (string path in beforeA.Keys.Union(beforeB.Keys).Where(p => !ObjectModelDiff.Matches(p, options.IgnoredPaths)).Order(StringComparer.Ordinal))
            {
                ModelValue valueA = Value(stateA, path), valueB = Value(stateB, path);
                ModelValue wasA = beforeA.GetValueOrDefault(path) ?? valueA, wasB = beforeB.GetValueOrDefault(path) ?? valueB;
                if (!valueA.Present && !valueB.Present && !wasA.Present && !wasB.Present)
                {
                    // An object or array replaced in both, whose own path holds no value
                    continue;
                }
                bool same = valueA.Present == valueB.Present && Same(valueA.Value, valueB.Value, options.NumberTolerance);
                paths.Add(new AlignedPath(path, wasA, valueA, wasB, valueB, same));
                if (!same)
                {
                    differences.Add(new(i + 1, a[i].Action, "om", $"{path}: {valueA} vs {valueB}"));
                }
            }
            steps.Add(paths);
        }
        return steps;
    }

    private static ModelValue Value(Dictionary<string, JsonNode?> state, string path) =>
        state.TryGetValue(path, out JsonNode? value) ? new ModelValue(true, value) : ModelValue.Absent;

    // Bring the state up to the end of a step, and return the paths that changed in it with the values they had before it
    private static Dictionary<string, ModelValue> Apply(Step step, Dictionary<string, JsonNode?> state)
    {
        Dictionary<string, ModelValue> before = [];
        void Changing(string path) => before.TryAdd(path, Value(state, path));
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
                    Changing(stale);
                    state.Remove(stale);
                }
                Changing(change.Path);
                state.Remove(change.Path);
                if (change.New is JsonObject or JsonArray)
                {
                    foreach ((string path, JsonNode? value) in ObjectModelDiff.Flatten(change.New))
                    {
                        string full = path.StartsWith('[') ? change.Path + path : $"{change.Path}.{path}";
                        Changing(full);
                        state[full] = value;
                    }
                }
                else
                {
                    state[change.Path] = change.New;
                }
            }
        }
        return before;
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
        RemoveFields(message, options.IgnoredCanFields);
        return message;
    }

    // Leave out the ignored fields wherever they are, such as each result's time in an input report
    private static void RemoveFields(JsonNode? node, IReadOnlyList<string> fields)
    {
        if (node is JsonObject obj)
        {
            foreach (string field in fields)
            {
                obj.Remove(field);
            }
            foreach ((_, JsonNode? child) in obj)
            {
                RemoveFields(child, fields);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                RemoveFields(child, fields);
            }
        }
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
    // both have, and report what each has that the other has not, in the step it is in. What the steps
    // after those are hold is listed unaligned, as their steps already differ
    private static List<AlignedEvent> CompareSequences(string aspect, List<Step> stepsA, List<Step> stepsB, int count,
                                                       Func<Step, IEnumerable<(string Head, JsonNode? Body, CaptureEvent Event)>> select,
                                                       ComparisonOptions options, List<CaptureDifference> differences)
    {
        List<Item> Items(IEnumerable<Step> steps) => [.. steps.SelectMany(s => select(s).Select(e => new Item(e.Head, e.Body, e.Event, s)))];
        List<Item> a = Items(stepsA.Take(count)), b = Items(stepsB.Take(count));
        bool Equal(int i, int j) => a[i].Head == b[j].Head && SameDeep(a[i].Body, b[j].Body, options.NumberTolerance);

        // The common prefix and suffix are part of every longest common subsequence, so only what lies between needs the table
        int prefix = 0;
        while (prefix < a.Count && prefix < b.Count && Equal(prefix, prefix))
        {
            prefix++;
        }
        int suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && Equal(a.Count - 1 - suffix, b.Count - 1 - suffix))
        {
            suffix++;
        }
        int endA = a.Count - suffix, endB = b.Count - suffix;
        int[,] lcs = new int[endA - prefix + 1, endB - prefix + 1];
        for (int i = endA - 1; i >= prefix; i--)
        {
            for (int j = endB - 1; j >= prefix; j--)
            {
                lcs[i - prefix, j - prefix] = Equal(i, j) ? lcs[i - prefix + 1, j - prefix + 1] + 1 : Math.Max(lcs[i - prefix + 1, j - prefix], lcs[i - prefix, j - prefix + 1]);
            }
        }

        List<AlignedEvent> aligned = [];
        void Match(int x, int y)
        {
            if (TimeDifference(a[x].Event, a[x].Step, b[y].Event, b[y].Step, options) is string time)
            {
                differences.Add(new(a[x].Step.Number, a[x].Step.Action, "time", time));
            }
            aligned.Add(new AlignedEvent(a[x].Event, a[x].Step.Number, b[y].Event, b[y].Step.Number));
        }
        for (int i = 0; i < prefix; i++)
        {
            Match(i, i);
        }
        int p = prefix, q = prefix;
        while (p < endA || q < endB)
        {
            if (p < endA && q < endB && Equal(p, q))
            {
                Match(p++, q++);
            }
            else if (q >= endB || (p < endA && lcs[p - prefix + 1, q - prefix] >= lcs[p - prefix, q - prefix + 1]))
            {
                differences.Add(new(a[p].Step.Number, a[p].Step.Action, aspect, $"only in the first: {a[p].Describe()}"));
                aligned.Add(new AlignedEvent(a[p].Event, a[p].Step.Number, null, null));
                p++;
            }
            else
            {
                differences.Add(new(b[q].Step.Number, b[q].Step.Action, aspect, $"only in the second: {b[q].Describe()}"));
                aligned.Add(new AlignedEvent(null, null, b[q].Event, b[q].Step.Number));
                q++;
            }
        }
        for (int i = 0; i < suffix; i++)
        {
            Match(endA + i, endB + i);
        }

        aligned.AddRange(Items(stepsA.Skip(count)).Select(i => new AlignedEvent(i.Event, i.Step.Number, null, null)));
        aligned.AddRange(Items(stepsB.Skip(count)).Select(i => new AlignedEvent(null, null, i.Event, i.Step.Number)));
        return aligned;
    }
}
