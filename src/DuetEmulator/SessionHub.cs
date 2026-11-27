using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DuetEmulation.Captures;
using DuetEmulation.Scenarios;
using DuetEmulation.Sessions;

namespace DuetEmulator;

/// <summary>What the server is doing with its session</summary>
internal enum SessionPhase
{
    /// <summary>No session</summary>
    None,

    /// <summary>Booting a session's boards</summary>
    Booting,

    /// <summary>A session is waiting for steps</summary>
    Ready,

    /// <summary>A session is running steps</summary>
    Running,

    /// <summary>Starting a session failed</summary>
    Failed
}

/// <summary>An event for the clients following the session, as server-sent events name and carry them</summary>
internal sealed record FeedEvent(string Name, string Data);

/// <summary>
/// The server's one session and everyone following it: starts and ends the session, keeps what the
/// clients need to catch up (its phase, each stack's boards and object model), and sends every change
/// to each client in the order it happened
/// </summary>
internal sealed class SessionHub(string outputRoot) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _feed = new();
    private readonly List<Channel<FeedEvent>> _followers = [];
    private readonly Dictionary<string, IReadOnlyList<BoardState>> _boards = [];
    private readonly Dictionary<string, string> _models = [];
    private Session? _session;
    private int _sessionId;
    private SessionPhase _phase = SessionPhase.None;
    private string? _detail;
    private int _running;

    // The steps of the scenario a held session started from, which run once config.g has
    private IReadOnlyList<ScenarioStep> _afterConfig = [];

    /// <summary>The session</summary>
    /// <exception cref="InvalidOperationException">There is none</exception>
    public Session Session => _session ?? throw new InvalidOperationException("No session: start one first");

    /// <summary>The session, or null</summary>
    public Session? Current => _session;

    /// <summary>What the server is doing, and what its session is</summary>
    public JsonObject Status()
    {
        lock (_feed)
        {
            return StatusLocked();
        }
    }

    private JsonObject StatusLocked()
    {
        Session? session = _session;
        return new JsonObject
        {
            ["phase"] = ApiJson.Node(_phase),
            ["detail"] = _detail,
            ["session"] = session is null ? null : SessionJson(session),
            // Tells sessions apart, which their names and directories need not
            ["id"] = session is null ? null : _sessionId,
            ["ignoredPaths"] = new JsonArray([.. Recorder.DefaultIgnoredPaths.Concat(new ComparisonOptions().IgnoredPaths).Select(p => (JsonNode)p)]),
            ["periodicCanTypes"] = new JsonArray([.. ComparisonOptions.PeriodicCanTypes.Select(t => (JsonNode)t)]),
            ["directory"] = session?.Directory,
            ["stacks"] = session is null ? new JsonArray() : new JsonArray([.. session.Stacks.Select(s => (JsonNode)new JsonObject
            {
                ["stack"] = s.Name,
                ["time"] = s.Recorder.Now,
                ["events"] = s.Capture.Count
            })])
        };
    }

    private static JsonObject SessionJson(Session session)
    {
        JsonObject json = ApiJson.Scenario(session.Scenario);
        json["configHeld"] = session.ConfigHeld;
        return json;
    }

    /// <summary>
    /// Start a session from a scenario, ending the one there is; booting is followed by every client. The
    /// scenario's steps before config.g run, then config.g and the steps after it unless <paramref name="hold"/>,
    /// which leaves config.g held for <see cref="RunConfigAsync"/>
    /// </summary>
    public async Task<JsonObject> StartAsync(Scenario scenario, bool hold, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await EndLockedAsync();
            SetPhase(SessionPhase.Booting, $"Booting {string.Join(" and ", scenario.Stacks)}");
            Session session;
            try
            {
                session = await Session.StartAsync(scenario, Path.Combine(outputRoot, Output.SafeName(scenario.Name)),
                                                   new InlineProgress(detail => SetPhase(SessionPhase.Booting, detail)), cancellationToken);
            }
            catch (Exception e)
            {
                SetPhase(SessionPhase.Failed, e.Message);
                throw;
            }
            Attach(session);
            foreach ((string stack, IReadOnlyList<BoardState> boards) in await session.BoardStatesAsync(cancellationToken))
            {
                OnBoards(stack, boards);
            }
            lock (_feed)
            {
                _session = session;
                _sessionId++;
                _phase = SessionPhase.Ready;
                _detail = null;
                PublishLocked(Hello());
            }
            List<SessionStep> steps = [.. await RunAsync(scenario.BeforeConfig, cancellationToken)];
            _afterConfig = scenario.Steps;
            if (!hold && steps.All(s => s.State == StepState.Done))
            {
                steps.AddRange(await RunConfigAsync(cancellationToken));
            }
            JsonObject result = Status();
            if (steps.Count > 0)
            {
                result["steps"] = new JsonArray([.. steps.Select(s => ApiJson.SessionStep(s, session))]);
            }
            return result;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Start the session again from what it started from (its boards, config.g, files and stacks), so
    /// that the boards boot afresh; with <paramref name="replay"/>, the steps it had run run again
    /// </summary>
    public async Task<JsonObject> RestartAsync(bool replay, CancellationToken cancellationToken)
    {
        Session session = Session;
        Scenario scenario = session.Scenario;
        scenario.Steps = replay ? [.. session.Steps.Where(s => s.Kind == StepKind.Step).Select(s => s.Step!)] : [];
        return await StartAsync(scenario, session.ConfigHeld, cancellationToken);
    }

    /// <summary>
    /// Let config.g run on the session, every client following it, then the steps of the scenario the session
    /// started from that come after it
    /// </summary>
    /// <returns>config.g and the steps run after it</returns>
    public async Task<IReadOnlyList<SessionStep>> RunConfigAsync(CancellationToken cancellationToken)
    {
        Session session = Session;
        List<SessionStep> steps = [await RunAsync(token => session.RunConfigAsync(token), cancellationToken)];
        IReadOnlyList<ScenarioStep> after = Interlocked.Exchange(ref _afterConfig, []);
        if (steps[0].State == StepState.Done && after.Count > 0)
        {
            steps.AddRange(await RunAsync(after, cancellationToken));
        }
        return steps;
    }

    /// <summary>Run steps on the session, every client following them</summary>
    public Task<IReadOnlyList<SessionStep>> RunAsync(IEnumerable<ScenarioStep> steps, CancellationToken cancellationToken)
    {
        List<ScenarioStep> list = [.. steps];
        return list.Count == 0 ? Task.FromResult<IReadOnlyList<SessionStep>>([]) : RunAsync(token => Session.RunAsync(list, token), cancellationToken);
    }

    // Run something on the session in the running phase
    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> run, CancellationToken cancellationToken)
    {
        Session session = Session;
        Interlocked.Increment(ref _running);
        SetPhase(SessionPhase.Running, null);
        try
        {
            return await run(cancellationToken);
        }
        finally
        {
            if (Interlocked.Decrement(ref _running) == 0 && _session == session)
            {
                SetPhase(SessionPhase.Ready, null);
            }
        }
    }

    /// <summary>End the session, saving its captures</summary>
    public async Task<JsonObject> EndAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            return await EndLockedAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<JsonObject> EndLockedAsync()
    {
        Session? session = _session;
        if (session is null)
        {
            return new JsonObject { ["ended"] = null };
        }
        _afterConfig = [];
        lock (_feed)
        {
            _session = null;
            _boards.Clear();
            _models.Clear();
            _phase = SessionPhase.None;
            _detail = null;
            PublishLocked(Hello());
        }
        await session.DisposeAsync();
        return new JsonObject { ["ended"] = session.Scenario.Name, ["directory"] = session.Directory };
    }

    /// <summary>Each stack's object model as last recorded, as JSON text by stack name</summary>
    public IReadOnlyDictionary<string, string> ObjectModels()
    {
        lock (_feed)
        {
            return new Dictionary<string, string>(_models);
        }
    }

    /// <summary>Each stack's boards as last read</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<BoardState>> BoardStates()
    {
        lock (_feed)
        {
            return new Dictionary<string, IReadOnlyList<BoardState>>(_boards);
        }
    }

    /// <summary>
    /// Follow the session: first everything there is (the phase, the steps, every stack's capture and
    /// boards), then each change as it happens, until <paramref name="cancellationToken"/> is cancelled
    /// </summary>
    public async IAsyncEnumerable<FeedEvent> FollowAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Channel<FeedEvent> channel = Channel.CreateUnbounded<FeedEvent>(new UnboundedChannelOptions { SingleReader = true });
        lock (_feed)
        {
            channel.Writer.TryWrite(Hello());
            _followers.Add(channel);
        }
        try
        {
            await foreach (FeedEvent e in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return e;
            }
        }
        finally
        {
            lock (_feed)
            {
                _followers.Remove(channel);
            }
        }
    }

    // Everything a client needs to catch up, which later events add to
    private FeedEvent Hello()
    {
        Session? session = _session;
        JsonObject hello = new()
        {
            ["status"] = StatusLocked(),
            ["steps"] = session is null ? new JsonArray() : new JsonArray([.. session.Steps.Select(s => ApiJson.SessionStep(s))]),
            ["captures"] = session is null ? new JsonObject() : new JsonObject(session.Stacks.Select(s =>
                KeyValuePair.Create(s.Name, (JsonNode?)ApiJson.Events(s.Capture.Events)))),
            ["boards"] = new JsonObject(_boards.Select(b => KeyValuePair.Create(b.Key, (JsonNode?)ApiJson.Boards(b.Value))))
        };
        return new FeedEvent("hello", hello.ToText());
    }

    private void Attach(Session session)
    {
        session.StepChanged += step =>
        {
            if (step.State != StepState.Running)
            {
                RecordModels(session);
            }
            Publish(session, new FeedEvent("step", ApiJson.SessionStep(step).ToText()), status: true);
        };
        session.CaptureChanged += (stack, from) => Publish(session, new FeedEvent("capture", new JsonObject
        {
            ["stack"] = stack.Name,
            ["from"] = from,
            ["events"] = ApiJson.Events(stack.Capture.Since(from))
        }.ToText()));
        session.BoardsChanged += (stack, boards) => OnBoards(stack.Name, boards);
        RecordModels(session);
    }

    // The object models as recorded, as text: read on the thread that records them, and kept for any thread to serve
    private void RecordModels(Session session)
    {
        Dictionary<string, string> models = session.Stacks.ToDictionary(s => s.Name, s => s.Recorder.ObjectModel?.ToText() ?? "null");
        lock (_feed)
        {
            foreach ((string stack, string model) in models)
            {
                _models[stack] = model;
            }
        }
    }

    private void OnBoards(string stack, IReadOnlyList<BoardState> boards)
    {
        lock (_feed)
        {
            _boards[stack] = boards;
            PublishLocked(new FeedEvent("boards", new JsonObject { ["stack"] = stack, ["boards"] = ApiJson.Boards(boards) }.ToText()));
        }
    }

    private void SetPhase(SessionPhase phase, string? detail)
    {
        lock (_feed)
        {
            _phase = phase;
            _detail = detail;
            PublishLocked(new FeedEvent("status", StatusLocked().ToText()));
        }
    }

    // A session's event, unless it has been replaced; with the status too when the stacks' times have moved
    private void Publish(Session session, FeedEvent e, bool status = false)
    {
        lock (_feed)
        {
            if (_session != session)
            {
                // Booting, which the clients catch up on when it is done, or a session that has ended
                return;
            }
            PublishLocked(e);
            if (status)
            {
                PublishLocked(new FeedEvent("status", StatusLocked().ToText()));
            }
        }
    }

    private void PublishLocked(FeedEvent e)
    {
        foreach (Channel<FeedEvent> follower in _followers)
        {
            follower.Writer.TryWrite(e);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await EndAsync();
        lock (_feed)
        {
            foreach (Channel<FeedEvent> follower in _followers)
            {
                follower.Writer.TryComplete();
            }
        }
    }

    // Progress reported on the thread that reports it, so that it reaches the clients in order
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
