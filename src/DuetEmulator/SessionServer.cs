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
using DuetEmulation.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace DuetEmulator;

/// <summary>
/// Interactive sessions over HTTP on localhost, for people through the page it serves and for scripts and
/// AI agents through its API: a client boots a bench once, on one stack or on both side by side, and
/// drives it step by step, each step running on every stack, recorded and compared
/// </summary>
/// <remarks>
/// <para>
/// The page is at <c>/</c>. The API is under <c>/api</c>; requests take JSON or YAML (JSON is YAML) and
/// answer JSON, and a refusal answers <c>{"error": ...}</c>. There is one session at a time, which
/// every client shares: <c>GET /api/events</c> follows it as server-sent events, whoever drives it.
/// </para>
/// <list type="table">
/// <item><term>GET /api/session</term><description>What the server is doing, and its session</description></item>
/// <item><term>POST /api/session</term><description>Start a session from a scenario, ending the one there is; its steps run once booted</description></item>
/// <item><term>DELETE /api/session</term><description>End the session, saving its captures</description></item>
/// <item><term>POST /api/session/restart?replay=true</term><description>Boot the session's boards afresh; with replay, run its steps again</description></item>
/// <item><term>GET /api/events</term><description>Follow the session: <c>hello</c> with everything so far, then <c>status</c>, <c>step</c>, <c>capture</c> and <c>boards</c></description></item>
/// <item><term>GET /api/steps</term><description>The steps run so far</description></item>
/// <item><term>POST /api/steps</term><description>Run a step, or a list of them, on every stack, answering with what each did</description></item>
/// <item><term>GET /api/om?key=...</term><description>The object model at a key on every stack, read between steps</description></item>
/// <item><term>GET /api/model</term><description>Every stack's whole object model as last recorded, without waiting for a step</description></item>
/// <item><term>GET /api/capture?since=N&amp;format=text</term><description>The events recorded, from the Nth on</description></item>
/// <item><term>GET /api/alignment</term><description>The stacks' captures lined up; <c>periodic</c>, <c>fields</c>, <c>pins</c> and <c>tolerance</c> change what is compared</description></item>
/// <item><term>GET /api/differences</term><description>How the stacks' captures differ so far, as a report</description></item>
/// <item><term>POST /api/save</term><description>Save the captures and their differences</description></item>
/// <item><term>GET /api/boards</term><description>The session's expansion boards, with their ports and what they have done</description></item>
/// <item><term>GET /api/board-types</term><description>The expansion boards a session can have</description></item>
/// <item><term>POST /api/scenario</term><description>The session as a scenario: the steps to keep, those to move to config.g; with <c>path</c>, saved there</description></item>
/// <item><term>GET /api/scenarios</term><description>The scenario files kept with the bench</description></item>
/// <item><term>GET /api/scenarios/file?path=...</term><description>One scenario file, read</description></item>
/// <item><term>GET /api/runs</term><description>The saved runs under build/emulation</description></item>
/// <item><term>GET /api/runs/{kind}/{name}</term><description>A saved run's captures, lined up when it has two</description></item>
/// </list>
/// </remarks>
public sealed class SessionServer : IAsyncDisposable
{
    /// <summary>The port it listens on unless told otherwise</summary>
    public const int DefaultPort = 8770;

    /// <summary>Where the page's files are, served as they are so that a change shows on reload</summary>
    public static string PageDirectory => Path.Combine(RepositoryPaths.Root, "src", "DuetEmulator", "wwwroot");

    private readonly WebApplication _app;
    private readonly SessionHub _hub;

    /// <summary>Where it listens</summary>
    public string Prefix { get; }

    /// <summary>Listen on <paramref name="port"/> on localhost, keeping each session's files under <paramref name="outputRoot"/></summary>
    public SessionServer(int port, string outputRoot)
    {
        Prefix = $"http://127.0.0.1:{port}/";
        _hub = new SessionHub(outputRoot);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = PageDirectory, WebRootPath = PageDirectory });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        _app = builder.Build();
        _app.Use(RefusalsAsJson);
        PhysicalFileProvider page = new(PageDirectory);
        _app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = page });
        _app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = page,
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
        });
        MapApi(_app);
    }

    /// <summary>Serve requests until cancelled, then end the session</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _app.StartAsync(cancellationToken);
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        await _hub.EndAsync();
        await _app.StopAsync(CancellationToken.None);
    }

    private void MapApi(WebApplication app)
    {
        app.MapGet("/api/session", () => Json(_hub.Status()));
        app.MapPost("/api/session", async (HttpRequest request, CancellationToken token) =>
            Json(await _hub.StartAsync(Scenario.Parse(await BodyAsync(request), "session"), token)));
        app.MapDelete("/api/session", async () => Json(await _hub.EndAsync()));
        app.MapPost("/api/session/restart", async (bool? replay, CancellationToken token) => Json(await _hub.RestartAsync(replay == true, token)));
        app.MapGet("/api/events", FollowAsync);

        app.MapGet("/api/steps", () => Json(new JsonObject { ["steps"] = new JsonArray([.. _hub.Session.Steps.Select(s => ApiJson.SessionStep(s))]) }));
        app.MapPost("/api/steps", async (HttpRequest request, CancellationToken token) =>
        {
            IReadOnlyList<ScenarioStep> steps = Scenario.ParseSteps(await BodyAsync(request));
            Session session = _hub.Session;
            IReadOnlyList<SessionStep> results = await _hub.RunAsync(steps, token);
            return Json(new JsonObject { ["steps"] = new JsonArray([.. results.Select(s => ApiJson.SessionStep(s, session))]) });
        });

        app.MapGet("/api/om", async (string? key, CancellationToken token) =>
        {
            Session session = _hub.Session;
            return Json(await session.UseBenchesAsync(async t =>
            {
                JsonObject result = [];
                foreach (SessionStack stack in session.Stacks)
                {
                    result[stack.Name] = await stack.Recorder.Bench.GetObjectModelAsync(key ?? string.Empty, t);
                }
                return result;
            }, token));
        });
        app.MapGet("/api/model", () =>
        {
            // The models are kept as text, so they are joined rather than parsed again
            IReadOnlyDictionary<string, string> models = _hub.ObjectModels();
            return Results.Text("{" + string.Join(',', models.Select(m => $"{JsonSerializer.Serialize(m.Key)}:{m.Value}")) + "}", "application/json");
        });
        app.MapGet("/api/capture", (int? since, string? format) =>
        {
            JsonObject result = [];
            foreach (SessionStack stack in _hub.Session.Stacks)
            {
                IReadOnlyList<CaptureEvent> events = stack.Capture.Since(since ?? 0);
                result[stack.Name] = format == "text"
                    ? string.Join('\n', events.Select(e => $"{e.Time,12} {e.Describe()}"))
                    : ApiJson.Events(events);
            }
            return Json(result);
        });
        app.MapGet("/api/alignment", (HttpRequest request) =>
        {
            Session session = _hub.Session;
            return Json(ApiJson.Alignment(session.Align(Options(request)), session.Stacks[0].Name, session.Stacks[1].Name));
        });
        app.MapGet("/api/differences", (HttpRequest request) =>
        {
            Session session = _hub.Session;
            IReadOnlyList<CaptureDifference> differences = session.Align(Options(request)).Differences;
            return Json(new JsonObject
            {
                ["count"] = differences.Count,
                ["report"] = CaptureComparison.Report(differences, session.Stacks[0].Capture, session.Stacks[1].Capture)
            });
        });
        app.MapPost("/api/save", () => Json(ApiJson.Node(_hub.Session.Save())));

        app.MapGet("/api/boards", () =>
        {
            Session session = _hub.Session;
            IReadOnlyDictionary<string, IReadOnlyList<BoardState>> states = _hub.BoardStates();
            return Json(new JsonObject
            {
                ["boards"] = new JsonArray([.. session.Scenario.BoardTypes().OrderBy(b => b.Key).Select(b => (JsonNode)ApiJson.Board(b.Key, b.Value))]),
                ["states"] = new JsonObject(states.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)ApiJson.Boards(s.Value))))
            });
        });
        app.MapGet("/api/board-types", () => Json(new JsonArray([.. DuetEmulation.Boards.BoardTypes.All.Where(b => b.IsExpansion)
                                                                    .Select(b => (JsonNode)ApiJson.BoardType(b))])));

        app.MapPost("/api/scenario", async (HttpRequest request) => Json(await ExportAsync(await BodyAsync(request))));
        app.MapGet("/api/scenarios", () => Json(new JsonArray([.. RepositoryFiles.Scenarios().Select(f => (JsonNode)ScenarioFileJson(f, steps: false))])));
        app.MapGet("/api/scenarios/file", (string path) =>
        {
            ScenarioFile file = RepositoryFiles.Scenario(path);
            JsonObject json = ScenarioFileJson(file, steps: true);
            json["yaml"] = File.ReadAllText(RepositoryFiles.ScenarioPath(path));
            return Json(json);
        });

        app.MapGet("/api/runs", () => Json(ApiJson.Node(RepositoryFiles.Runs())));
        app.MapGet("/api/runs/{kind}/{name}", (string kind, string name, HttpRequest request) =>
        {
            IReadOnlyDictionary<string, MachineCapture> captures = RepositoryFiles.Run($"{kind}/{name}");
            JsonObject json = new()
            {
                ["id"] = $"{kind}/{name}",
                ["name"] = captures.Values.First().Header.Name,
                ["captures"] = new JsonObject(captures.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)new JsonObject
                {
                    ["header"] = ApiJson.Node(c.Value.Header),
                    ["events"] = ApiJson.Events(c.Value.Events)
                })))
            };
            if (captures.Count == 2)
            {
                (string first, string second) = (captures.Keys.First(), captures.Keys.Last());
                json["alignment"] = ApiJson.Alignment(CaptureComparison.Align(captures[first], captures[second], Options(request)), first, second);
            }
            return Json(json);
        });
    }

    // The session as a scenario, saved in the repository when the request gives a path
    private async Task<JsonObject> ExportAsync(string body)
    {
        JsonObject request = JsonNode.Parse(body.Length == 0 ? "{}" : body)?.AsObject() ?? [];
        SessionExport export = new(
            request["name"]?.GetValue<string>(),
            request["description"]?.GetValue<string>(),
            request["steps"]?.AsArray().Select(n => n!.GetValue<int>()).ToList(),
            request["toConfig"]?.AsArray().Select(n => n!.GetValue<int>()).ToList());
        string yaml = _hub.Session.Export(export).ToYaml();
        JsonObject result = new() { ["yaml"] = yaml };
        if (request["path"]?.GetValue<string>() is string path && path.Length > 0)
        {
            string full = RepositoryFiles.ScenarioPath(path);
            if (File.Exists(full) && request["overwrite"]?.GetValue<bool>() != true)
            {
                throw new ConflictException($"{RepositoryFiles.RelativePath(full)} exists");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, yaml);
            result["path"] = RepositoryFiles.RelativePath(full);
        }
        return result;
    }

    private static JsonObject ScenarioFileJson(ScenarioFile file, bool steps)
    {
        JsonObject json = new() { ["path"] = file.Path, ["error"] = file.Error };
        if (file.Scenario is Scenario scenario)
        {
            JsonObject read = ApiJson.Scenario(scenario);
            json["stepCount"] = scenario.Steps.Count;
            if (!steps)
            {
                read.Remove("steps");
                read.Remove("config");
                read.Remove("files");
            }
            json["scenario"] = read;
        }
        return json;
    }

    // Comparison options from a request: periodic=1 keeps the periodic CAN messages, fields=1 keeps the
    // fields each stack fills its own way, pins=0 leaves out pin changes, tolerance=S compares timing
    private static ComparisonOptions Options(HttpRequest request)
    {
        ComparisonOptions options = new();
        IQueryCollection query = request.Query;
        if (query["periodic"] == "1")
        {
            options = options with { IgnoredCanTypes = [] };
        }
        if (query["fields"] == "1")
        {
            options = options with { IgnoredCanFields = [] };
        }
        if (query["pins"] == "0")
        {
            options = options with { ComparePins = false };
        }
        if (double.TryParse(query["tolerance"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double tolerance))
        {
            options = options with { TimeTolerance = tolerance };
        }
        return options;
    }

    // Server-sent events: everything so far, then each change
    private async Task FollowAsync(HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        CancellationToken token = context.RequestAborted;
        try
        {
            await foreach (FeedEvent e in _hub.FollowAsync(token))
            {
                await context.Response.WriteAsync($"event: {e.Name}\ndata: {e.Data}\n\n", token);
                await context.Response.Body.FlushAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away
        }
    }

    private static async Task<string> BodyAsync(HttpRequest request)
    {
        using StreamReader reader = new(request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static IResult Json(JsonNode? node) => Results.Text((node ?? new JsonObject()).ToText(), "application/json");

    // A refusal as {"error": ...} with a status that says whose fault it is
    private static async Task RefusalsAsJson(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception e) when (e is not OperationCanceledException && !context.Response.HasStarted)
        {
            context.Response.StatusCode = e switch
            {
                ConflictException => StatusCodes.Status409Conflict,
                FileNotFoundException or KeyNotFoundException => StatusCodes.Status404NotFound,
                FormatException or ArgumentException or InvalidOperationException or JsonException or TimeoutException or BadHttpRequestException
                    => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status500InternalServerError
            };
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(new JsonObject { ["error"] = e.Message }.ToText());
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _hub.DisposeAsync();
        await _app.DisposeAsync();
    }

    private sealed class ConflictException(string message) : Exception(message);
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
