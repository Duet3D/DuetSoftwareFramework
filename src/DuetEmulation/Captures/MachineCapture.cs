using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace DuetEmulation.Captures;

/// <summary>What a capture was taken of</summary>
/// <param name="Name">What the capture is of, such as the scenario's name</param>
/// <param name="Stack">rrf or dsf</param>
/// <param name="Boards">The expansion boards' type names, by CAN address</param>
/// <param name="Config">config.g, less the stack's own first line</param>
public sealed record CaptureHeader(string Name, string Stack, IReadOnlyDictionary<int, string> Boards, string Config);

/// <summary>
/// Everything a run of codes did, in emulated time order: the codes and replies, the CAN frames, the
/// object model's changes, the boards' output pins and what drove their inputs
/// </summary>
/// <remarks>
/// Saved as JSON Lines (<see cref="FileName"/>): the header, then one event per line, each with its
/// <c>kind</c>. <see cref="ReportFileName"/> beside it is the same as readable text.
/// </remarks>
public sealed class MachineCapture(CaptureHeader header)
{
    /// <summary>The name of the JSON Lines file</summary>
    public const string FileName = "capture.jsonl";

    /// <summary>The name of the readable report</summary>
    public const string ReportFileName = "capture.txt";

    /// <summary>How events and headers are written</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly List<CaptureEvent> _events = [];
    private readonly Lock _lock = new();
    private CaptureEvent[]? _snapshot;

    /// <summary>What it was taken of</summary>
    public CaptureHeader Header { get; } = header;

    /// <summary>
    /// The events, in time order: a snapshot, which events added later do not change, so that another
    /// thread can read a capture that is still being recorded
    /// </summary>
    public IReadOnlyList<CaptureEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _snapshot ??= [.. _events];
            }
        }
    }

    /// <summary>
    /// Raised after events are added, with the index of the first event that is new or has moved: the
    /// events from there on are to be read again
    /// </summary>
    public event Action<int>? Changed;

    /// <summary>
    /// Add events, keeping time order. Events at the same time keep the order they were added in,
    /// which is the order they happened in: the recorder adds what it does as it does it, and what
    /// that caused once it has been seen
    /// </summary>
    public void Add(IEnumerable<CaptureEvent> events)
    {
        int changed;
        lock (_lock)
        {
            int start = _events.Count;
            _events.AddRange(events);
            if (_events.Count == start)
            {
                return;
            }
            changed = start;
            if (start > 0 && _events.Skip(start).Min(e => e.T) < _events[start - 1].T)
            {
                // A stable sort, which keeps the order they were added in among those at the same time
                double earliest = _events.Skip(start).Min(e => e.T);
                changed = _events.FindIndex(e => e.T > earliest);
                List<CaptureEvent> sorted = [.. _events.OrderBy(e => e.T)];
                _events.Clear();
                _events.AddRange(sorted);
            }
            _snapshot = null;
        }
        Changed?.Invoke(changed);
    }

    /// <summary>Write the capture and its report into <paramref name="directory"/></summary>
    /// <returns>The path of the capture</returns>
    public string Save(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        using (StreamWriter writer = new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.WriteLine(JsonSerializer.Serialize(Header, JsonOptions));
            foreach (CaptureEvent e in Events)
            {
                writer.WriteLine(JsonSerializer.Serialize(e, JsonOptions));
            }
        }
        File.WriteAllText(Path.Combine(directory, ReportFileName), Report());
        return path;
    }

    /// <summary>Read a capture written by <see cref="Save"/></summary>
    /// <param name="path">The capture, or the directory it is in</param>
    public static MachineCapture Load(string path)
    {
        if (Directory.Exists(path))
        {
            path = Path.Combine(path, FileName);
        }
        using StreamReader reader = new(path);
        string headerLine = reader.ReadLine() ?? throw new InvalidDataException($"{path} is empty");
        MachineCapture capture = new(JsonSerializer.Deserialize<CaptureHeader>(headerLine, JsonOptions)
                                     ?? throw new InvalidDataException($"{path} has no header"));
        List<CaptureEvent> events = [];
        while (reader.ReadLine() is string line)
        {
            if (line.Length != 0)
            {
                events.Add(JsonSerializer.Deserialize<CaptureEvent>(line, JsonOptions) ?? throw new InvalidDataException($"{path} has an empty event"));
            }
        }
        capture._events.AddRange(events);
        return capture;
    }

    /// <summary>The capture as readable text: one line per event, the time first</summary>
    public string Report()
    {
        StringBuilder report = new();
        report.AppendLine($"{Header.Name} on {Header.Stack}");
        report.AppendLine("Boards: " + (Header.Boards.Count == 0 ? "main board only" : string.Join(", ", Header.Boards.Select(b => $"{b.Key}: {b.Value}"))));
        foreach (CaptureEvent e in Events)
        {
            report.AppendLine($"{e.Time,12} {e.Describe()}");
        }
        return report.ToString();
    }
}
