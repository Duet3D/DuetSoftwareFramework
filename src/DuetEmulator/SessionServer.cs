using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DuetEmulation;
using DuetEmulation.Captures;
using DuetEmulation.Scenarios;

namespace DuetEmulator;

/// <summary>
/// Interactive sessions over HTTP on localhost: a client boots a bench once, on one stack or on both
/// side by side, and drives it step by step, each step running on every stack and answering with what
/// it did there and how the stacks differed
/// </summary>
/// <remarks>
/// <para>Requests take JSON or YAML (JSON is YAML) and answer JSON. One session at a time.</para>
/// <list type="table">
/// <item><term>GET /</term><description>The session: its stacks, boards and emulated times</description></item>
/// <item><term>POST /session</term><description>Start a session from a scenario (boards, config, files, stacks); its steps run at once</description></item>
/// <item><term>DELETE /session</term><description>End the session</description></item>
/// <item><term>POST /steps</term><description>Run a step, or a list of them, on every stack</description></item>
/// <item><term>GET /om?key=...</term><description>The object model at a key on every stack</description></item>
/// <item><term>GET /capture?since=N&amp;format=text</term><description>The events recorded, from the Nth on</description></item>
/// <item><term>GET /differences</term><description>How the stacks' captures differ so far</description></item>
/// <item><term>POST /save</term><description>Save the captures and their differences; answers where</description></item>
/// </list>
/// </remarks>
public sealed class SessionServer : IAsyncDisposable
{
    /// <summary>The port it listens on unless told otherwise</summary>
    public const int DefaultPort = 8770;

    private readonly HttpListener _listener = new();
    private readonly string _outputRoot;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Session? _session;

    /// <summary>Where it listens</summary>
    public string Prefix { get; }

    /// <summary>Listen on <paramref name="port"/>, keeping each session's files under <paramref name="outputRoot"/></summary>
    public SessionServer(int port, string outputRoot)
    {
        Prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Prefix);
        _outputRoot = outputRoot;
    }

    /// <summary>Serve requests until cancelled</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        using CancellationTokenRegistration stop = cancellationToken.Register(_listener.Stop);
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                break;
            }
            await HandleAsync(context, cancellationToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        HttpListenerRequest request = context.Request;
        int status = 200;
        JsonNode? body;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            string text = request.HasEntityBody ? await new StreamReader(request.InputStream, Encoding.UTF8).ReadToEndAsync(cancellationToken) : string.Empty;
            body = (request.HttpMethod, request.Url!.AbsolutePath.TrimEnd('/')) switch
            {
                ("GET", "") => Status(),
                ("POST", "/session") => await StartSessionAsync(text, cancellationToken),
                ("DELETE", "/session") => await EndSessionAsync(),
                ("POST", "/steps") => await RunStepsAsync(text, cancellationToken),
                ("GET", "/om") => await ObjectModelAsync(request.QueryString["key"] ?? string.Empty, cancellationToken),
                ("GET", "/capture") => CaptureEvents(request.QueryString["since"], request.QueryString["format"]),
                ("GET", "/differences") => Differences(),
                ("POST", "/save") => Save(),
                _ => throw new KeyNotFoundException($"No {request.HttpMethod} {request.Url.AbsolutePath}")
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            status = e switch
            {
                KeyNotFoundException => 404,
                FormatException or ArgumentException or InvalidOperationException => 400,
                _ => 500
            };
            body = new JsonObject { ["error"] = e.Message };
        }
        finally
        {
            _lock.Release();
        }

        byte[] response = Encoding.UTF8.GetBytes((body ?? new JsonObject()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(response, cancellationToken);
        context.Response.Close();
    }

    private Session RequireSession() => _session ?? throw new InvalidOperationException("No session: POST /session first");

    private JsonNode Status() => _session is null
        ? new JsonObject { ["session"] = null }
        : new JsonObject
        {
            ["session"] = _session.Scenario.Name,
            ["directory"] = _session.Directory,
            ["boards"] = new JsonObject(_session.Scenario.Boards.Select(b => KeyValuePair.Create(b.Key.ToString(), (JsonNode?)b.Value))),
            ["stacks"] = new JsonArray([.. _session.Stacks.Select(s => (JsonNode)new JsonObject
            {
                ["stack"] = s.Name,
                ["time"] = s.Recorder.Now,
                ["events"] = s.Recorder.Capture.Events.Count
            })])
        };

    private async Task<JsonNode?> StartSessionAsync(string text, CancellationToken cancellationToken)
    {
        Scenario scenario = Scenario.Parse(text, "session");
        await EndSessionAsync();
        string directory = Path.Combine(_outputRoot, Output.SafeName(scenario.Name));
        List<StackSession> stacks = [];
        try
        {
            foreach (Stack stack in scenario.StackList())
            {
                string stackDirectory = Path.Combine(directory, stack.ToString().ToLowerInvariant());
                Bench bench = await Bench.StartAsync(stack, scenario.BoardTypes(), scenario.Config, stackDirectory, scenario.Files, cancellationToken);
                Recorder recorder = await Recorder.StartAsync(bench, Recorder.Header(scenario.Name, stack, scenario.BoardTypes(), scenario.Config),
                                                              scenario.CaptureBoot, cancellationToken: cancellationToken);
                stacks.Add(new StackSession(stack.ToString().ToLowerInvariant(), stackDirectory, recorder, new StepRunner(recorder)));
            }
        }
        catch
        {
            foreach (StackSession started in stacks)
            {
                await started.Recorder.Bench.DisposeAsync();
            }
            throw;
        }
        _session = new Session(scenario, directory, stacks);
        JsonObject result = Status().AsObject();
        if (scenario.Steps.Count > 0)
        {
            result["steps"] = await RunAsync(scenario.Steps, cancellationToken);
        }
        return result;
    }

    private async Task<JsonNode?> EndSessionAsync()
    {
        if (_session is Session session)
        {
            _session = null;
            foreach (StackSession stack in session.Stacks)
            {
                stack.Recorder.Capture.Save(stack.Directory);
                await stack.Recorder.Bench.DisposeAsync();
            }
            return new JsonObject { ["ended"] = session.Scenario.Name, ["directory"] = session.Directory };
        }
        return new JsonObject { ["ended"] = null };
    }

    private async Task<JsonNode?> RunStepsAsync(string text, CancellationToken cancellationToken) =>
        new JsonObject { ["steps"] = await RunAsync(Scenario.ParseSteps(text), cancellationToken) };

    // Run each step on every stack, answering with what it did on each and how they differ
    private async Task<JsonArray> RunAsync(IEnumerable<ScenarioStep> steps, CancellationToken cancellationToken)
    {
        Session session = RequireSession();
        JsonArray results = [];
        foreach (ScenarioStep step in steps)
        {
            JsonObject result = new() { ["step"] = step.Describe() };
            foreach (StackSession stack in session.Stacks)
            {
                int first = stack.Recorder.Capture.Events.Count;
                CodeReply? reply = await stack.Steps.RunAsync(step, cancellationToken);
                IEnumerable<CaptureEvent> events = stack.Recorder.Capture.Events.Skip(first);
                result[stack.Name] = new JsonObject
                {
                    ["time"] = stack.Recorder.Now,
                    ["reply"] = reply is null ? null : new JsonObject { ["severity"] = Severity(reply), ["text"] = reply.Text },
                    ["events"] = JsonSerializer.SerializeToNode(events.ToList(), MachineCapture.JsonOptions)
                };
            }
            if (session.Stacks.Count == 2)
            {
                // The whole captures are compared, so that the object model is compared by its state, and the step's differences kept
                MachineCapture first = session.Stacks[0].Recorder.Capture, second = session.Stacks[1].Recorder.Capture;
                IReadOnlyList<CaptureDifference> all = CaptureComparison.Compare(first, second);
                int last = all.Count == 0 ? 0 : Math.Max(CaptureComparison.StepCount(first), CaptureComparison.StepCount(second));
                result["differences"] = new JsonArray([.. all.Where(d => d.Step == last).Select(d => (JsonNode?)$"{d.Aspect}: {d.Description}")]);
            }
            results.Add(result);
        }
        return results;
    }

    private static string Severity(CodeReply reply) => reply.Severity switch
    {
        DuetAPI.ObjectModel.MessageType.Warning => "warning",
        DuetAPI.ObjectModel.MessageType.Error => "error",
        _ => "success"
    };

    private async Task<JsonNode?> ObjectModelAsync(string key, CancellationToken cancellationToken)
    {
        JsonObject result = [];
        foreach (StackSession stack in RequireSession().Stacks)
        {
            result[stack.Name] = await stack.Recorder.Bench.GetObjectModelAsync(key, cancellationToken);
        }
        return result;
    }

    private JsonNode CaptureEvents(string? since, string? format)
    {
        int first = since is null ? 0 : int.Parse(since, System.Globalization.CultureInfo.InvariantCulture);
        JsonObject result = [];
        foreach (StackSession stack in RequireSession().Stacks)
        {
            List<CaptureEvent> events = [.. stack.Recorder.Capture.Events.Skip(first)];
            result[stack.Name] = format == "text"
                ? JsonValue.Create(string.Join('\n', events.Select(e => $"{e.Time,12} {e.Describe()}")))
                : JsonSerializer.SerializeToNode(events, MachineCapture.JsonOptions);
        }
        return result;
    }

    private JsonNode Differences()
    {
        Session session = RequireSession();
        if (session.Stacks.Count != 2)
        {
            throw new InvalidOperationException("The session runs on one stack, so there is nothing to compare");
        }
        MachineCapture first = session.Stacks[0].Recorder.Capture, second = session.Stacks[1].Recorder.Capture;
        IReadOnlyList<CaptureDifference> differences = CaptureComparison.Compare(first, second);
        return new JsonObject
        {
            ["count"] = differences.Count,
            ["report"] = CaptureComparison.Report(differences, first, second)
        };
    }

    private JsonNode Save()
    {
        Session session = RequireSession();
        JsonObject result = [];
        foreach (StackSession stack in session.Stacks)
        {
            result[stack.Name] = stack.Recorder.Capture.Save(stack.Directory);
        }
        if (session.Stacks.Count == 2)
        {
            MachineCapture first = session.Stacks[0].Recorder.Capture, second = session.Stacks[1].Recorder.Capture;
            string path = Path.Combine(session.Directory, "differences.txt");
            File.WriteAllText(path, CaptureComparison.Report(CaptureComparison.Compare(first, second), first, second));
            result["differences"] = path;
        }
        return result;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await EndSessionAsync();
        _listener.Close();
        _lock.Dispose();
    }

    private sealed record StackSession(string Name, string Directory, Recorder Recorder, StepRunner Steps);

    private sealed record Session(Scenario Scenario, string Directory, List<StackSession> Stacks);
}

/// <summary>Naming output</summary>
internal static class Output
{
    /// <summary>A name made safe to use as a directory name</summary>
    public static string SafeName(string name)
    {
        string safe = new([.. name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')]);
        return safe.Trim('-').Length == 0 ? "scenario" : safe.Trim('-');
    }
}
