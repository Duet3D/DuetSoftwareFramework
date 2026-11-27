using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetEmulation.Boards;
using DuetEmulation.Captures;
using DuetEmulation.Scenarios;
using DuetEmulation.Traces;

namespace DuetEmulation.Sessions;

/// <summary>Where a step of a session is</summary>
public enum StepState
{
    /// <summary>Running on the stacks</summary>
    Running,

    /// <summary>Done on every stack</summary>
    Done,

    /// <summary>Failed on at least one stack, such as a code that did not finish in time</summary>
    Failed
}

/// <summary>What a step did on one stack</summary>
/// <param name="Started">When it started, in seconds of emulated time</param>
/// <param name="Finished">When it finished, or null while it runs</param>
/// <param name="Reply">The reply of a step that finishes a code</param>
/// <param name="Error">Why it failed, or null</param>
public sealed record StackStepResult(double Started, double? Finished, CodeReply? Reply, string? Error);

/// <summary>A step a session ran, and what it did on each stack</summary>
/// <param name="Number">Its number, from 1; it is step <c>Number + 1</c> of a capture, whose step 1 is the start</param>
/// <param name="Step">The step</param>
/// <param name="State">Where it is</param>
/// <param name="Stacks">What it did, by stack name</param>
/// <param name="Differences">How the stacks differed in it, when there are two</param>
public sealed record SessionStep(int Number, ScenarioStep Step, StepState State, IReadOnlyDictionary<string, StackStepResult> Stacks,
                                 IReadOnlyList<CaptureDifference> Differences)
{
    /// <summary>The step of the captures it is</summary>
    public int CaptureStep => Number + 1;
}

/// <summary>What an output of a board drives</summary>
/// <param name="Mode">Low, High, Pwm, Peripheral or Input</param>
/// <param name="Duty">Its duty for PWM, as a fraction</param>
/// <param name="Frequency">Its frequency for PWM, in Hz</param>
public sealed record OutputState(string Mode, double? Duty, double? Frequency);

/// <summary>What a board's drivers and outputs have done, on one stack</summary>
/// <param name="Address">The board's CAN address</param>
/// <param name="StepCounts">Each driver's step pulses since the board started</param>
/// <param name="Outputs">What each output port drives, by port name</param>
public sealed record BoardState(int Address, IReadOnlyList<ulong> StepCounts, IReadOnlyDictionary<string, OutputState> Outputs);

/// <summary>One stack of a session: its bench, what it has recorded and where its files go</summary>
public sealed class SessionStack(string name, string directory, Recorder recorder)
{
    /// <summary>rrf or dsf</summary>
    public string Name { get; } = name;

    /// <summary>Where its logs, traces and capture go</summary>
    public string Directory { get; } = directory;

    /// <summary>Its recorder</summary>
    public Recorder Recorder { get; } = recorder;

    /// <summary>What it has recorded</summary>
    public MachineCapture Capture => Recorder.Capture;

    internal StepRunner Steps { get; } = new(recorder);
}

/// <summary>Which steps of a session to save as a scenario, and what to call it</summary>
/// <param name="Name">The scenario's name</param>
/// <param name="Description">What it does, written as the comment the file starts with</param>
/// <param name="Steps">The numbers of the steps to keep, all of them if null</param>
/// <param name="ToConfig">The numbers of code steps to move to the end of config.g instead</param>
public sealed record SessionExport(string? Name = null, string? Description = null, IReadOnlyCollection<int>? Steps = null,
                                   IReadOnlyCollection<int>? ToConfig = null);

/// <summary>
/// An interactive session: the boards booted once on one stack or on each side by side, and steps run
/// one at a time on every stack, recorded, compared and kept so that they can be saved as a scenario
/// </summary>
/// <remarks>
/// Steps and object model queries take turns at the benches. What has been recorded, the steps and
/// their comparison can be read from any thread at any time, including while a step runs.
/// </remarks>
public sealed class Session : IAsyncDisposable
{
    private readonly SemaphoreSlim _benches = new(1, 1);
    private readonly CancellationTokenSource _ending = new();
    private readonly Lock _stepsLock = new();
    private readonly List<SessionStep> _steps = [];

    /// <summary>What the session started from: its name, stacks, boards, config.g and files</summary>
    public Scenario Scenario { get; }

    /// <summary>Where its files go</summary>
    public string Directory { get; }

    /// <summary>Its stacks, in the order the scenario named them</summary>
    public IReadOnlyList<SessionStack> Stacks { get; }

    /// <summary>The steps it has run or is running, in order</summary>
    public IReadOnlyList<SessionStep> Steps
    {
        get
        {
            lock (_stepsLock)
            {
                return [.. _steps];
            }
        }
    }

    /// <summary>Raised when a step starts and when it finishes</summary>
    public event Action<SessionStep>? StepChanged;

    /// <summary>Raised when a stack's capture changes, with the index of the first event to read again</summary>
    public event Action<SessionStack, int>? CaptureChanged;

    /// <summary>Raised after each step with what each stack's boards have done</summary>
    public event Action<SessionStack, IReadOnlyList<BoardState>>? BoardsChanged;

    private Session(Scenario scenario, string directory, IReadOnlyList<SessionStack> stacks)
    {
        Scenario = scenario;
        Directory = directory;
        Stacks = stacks;
        foreach (SessionStack stack in stacks)
        {
            stack.Capture.Changed += from => CaptureChanged?.Invoke(stack, from);
        }
    }

    /// <summary>
    /// Boot the scenario's boards on each of its stacks, its files going in a directory per stack under
    /// <paramref name="directory"/>; its steps are left for <see cref="RunAsync"/>, once the caller is following the session
    /// </summary>
    /// <param name="scenario">The boards, config.g, files and stacks</param>
    /// <param name="directory">Where the session's files go</param>
    /// <param name="progress">Told what is booting</param>
    /// <param name="cancellationToken">Cancels booting</param>
    public static async Task<Session> StartAsync(Scenario scenario, string directory, IProgress<string>? progress = null,
                                                 CancellationToken cancellationToken = default)
    {
        Scenario start = new()
        {
            Name = scenario.Name,
            Description = scenario.Description,
            Stacks = [.. scenario.Stacks.Select(s => s.ToLowerInvariant())],
            Boards = new(scenario.Boards),
            Config = scenario.Config,
            Files = new(scenario.Files),
            CaptureBoot = scenario.CaptureBoot
        };
        List<SessionStack> stacks = [];
        try
        {
            foreach (Stack stack in start.StackList())
            {
                string name = stack.ToString().ToLowerInvariant();
                progress?.Report($"Booting {name}");
                string stackDirectory = Path.Combine(directory, name);
                Bench bench = await Bench.StartAsync(stack, start.BoardTypes(), start.Config, stackDirectory, start.Files, cancellationToken);
                try
                {
                    Recorder recorder = await Recorder.StartAsync(bench, Recorder.Header(start.Name, stack, start.BoardTypes(), start.Config),
                                                                  start.CaptureBoot, cancellationToken: cancellationToken);
                    stacks.Add(new SessionStack(name, stackDirectory, recorder));
                }
                catch
                {
                    await bench.DisposeAsync();
                    throw;
                }
            }
        }
        catch
        {
            foreach (SessionStack started in stacks)
            {
                await started.Recorder.Bench.DisposeAsync();
            }
            throw;
        }

        return new Session(start, directory, stacks);
    }

    /// <summary>
    /// Run steps one after another on every stack, after any steps already running, stopping at one that fails
    /// </summary>
    /// <returns>What each step did</returns>
    public async Task<IReadOnlyList<SessionStep>> RunAsync(IEnumerable<ScenarioStep> steps, CancellationToken cancellationToken = default)
    {
        List<ScenarioStep> list = [.. steps];
        list.ForEach(s => s.Validate());
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ending.Token);
        await _benches.WaitAsync(linked.Token);
        try
        {
            List<SessionStep> results = [];
            foreach (ScenarioStep step in list)
            {
                SessionStep result = await RunOneAsync(step, linked.Token);
                results.Add(result);
                if (result.State == StepState.Failed)
                {
                    break;
                }
            }
            return results;
        }
        finally
        {
            _benches.Release();
        }
    }

    private async Task<SessionStep> RunOneAsync(ScenarioStep step, CancellationToken cancellationToken)
    {
        Dictionary<string, StackStepResult> results = Stacks.ToDictionary(s => s.Name, s => new StackStepResult(s.Recorder.Now, null, null, null));
        SessionStep record;
        lock (_stepsLock)
        {
            record = new SessionStep(_steps.Count + 1, step, StepState.Running, results, []);
            _steps.Add(record);
        }
        StepChanged?.Invoke(record);

        foreach (SessionStack stack in Stacks)
        {
            double started = stack.Recorder.Now;
            try
            {
                CodeReply? reply = await stack.Steps.RunAsync(step, cancellationToken);
                results[stack.Name] = new StackStepResult(started, stack.Recorder.Now, reply, null);
            }
            catch (Exception e) when (e is TimeoutException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException)
            {
                // The stacks go on: what a step that failed left behind is in the capture
                await stack.Recorder.CatchUpAsync(cancellationToken);
                results[stack.Name] = new StackStepResult(started, stack.Recorder.Now, null, e.Message);
            }
            catch (OperationCanceledException)
            {
                results[stack.Name] = new StackStepResult(started, stack.Recorder.Now, null, "Cancelled");
                Finish(record with { State = StepState.Failed, Stacks = results });
                throw;
            }
        }

        IReadOnlyList<CaptureDifference> differences = Stacks.Count == 2
            ? [.. Align().Differences.Where(d => d.Step == record.CaptureStep)]
            : [];
        record = Finish(record with
        {
            State = results.Values.Any(r => r.Error is not null) ? StepState.Failed : StepState.Done,
            Stacks = results,
            Differences = differences
        });

        foreach (SessionStack stack in Stacks)
        {
            BoardsChanged?.Invoke(stack, await BoardStatesAsync(stack, cancellationToken));
        }
        return record;
    }

    private SessionStep Finish(SessionStep record)
    {
        lock (_stepsLock)
        {
            _steps[record.Number - 1] = record;
        }
        StepChanged?.Invoke(record);
        return record;
    }

    /// <summary>What each stack's boards have done: every driver's steps and every output, read at the benches between steps</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<BoardState>>> BoardStatesAsync(CancellationToken cancellationToken = default) =>
        await UseBenchesAsync(async token =>
        {
            Dictionary<string, IReadOnlyList<BoardState>> states = [];
            foreach (SessionStack stack in Stacks)
            {
                states[stack.Name] = await BoardStatesAsync(stack, token);
            }
            return (IReadOnlyDictionary<string, IReadOnlyList<BoardState>>)states;
        }, cancellationToken);

    private static async Task<IReadOnlyList<BoardState>> BoardStatesAsync(SessionStack stack, CancellationToken cancellationToken)
    {
        Bench bench = stack.Recorder.Bench;
        List<BoardState> states = [];
        foreach (EmulatedBoard board in bench.System.Boards.Where(b => b.Type.IsExpansion))
        {
            List<ulong> steps = [];
            for (int driver = 0; driver < board.Type.StepPins.Count; driver++)
            {
                steps.Add(await bench.Io.StepCountAsync(board.Address, driver, cancellationToken));
            }
            Dictionary<string, OutputState> outputs = [];
            foreach (BoardPort port in BoardPorts.Of(board.Type).Where(p => p.Kind == PortKind.Output))
            {
                PinOutput output = bench.System.Io.Last(board.Address, port.Name) ?? new PinOutput(PinMode.Input, 0, 0);
                bool pwm = output.Mode == PinMode.Pwm;
                outputs[port.Name] = new OutputState(output.Mode.ToString(), pwm ? output.Duty : null, pwm ? output.Frequency : null);
            }
            states.Add(new BoardState(board.Address, steps, outputs));
        }
        return states;
    }

    /// <summary>
    /// Use the benches between steps, such as to query each stack's object model: after the steps
    /// running now, and before any started later
    /// </summary>
    public async Task<T> UseBenchesAsync<T>(Func<CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ending.Token);
        await _benches.WaitAsync(linked.Token);
        try
        {
            return await use(linked.Token);
        }
        finally
        {
            _benches.Release();
        }
    }

    /// <summary>The two stacks' captures lined up and compared, as they stand</summary>
    /// <exception cref="InvalidOperationException">The session runs on one stack</exception>
    public CaptureAlignment Align(ComparisonOptions? options = null) => Stacks.Count == 2
        ? CaptureComparison.Align(Stacks[0].Capture, Stacks[1].Capture, options)
        : throw new InvalidOperationException("The session runs on one stack, so there is nothing to compare");

    /// <summary>
    /// The session as a scenario that replays it: its stacks, boards and files, config.g with any codes
    /// moved to it, and the steps chosen
    /// </summary>
    /// <exception cref="ArgumentException">A step to keep or move does not exist, or one to move is not a code</exception>
    public Scenario Export(SessionExport export)
    {
        IReadOnlyList<SessionStep> steps = Steps;
        HashSet<int> keep = [.. export.Steps ?? steps.Select(s => s.Number)];
        HashSet<int> toConfig = [.. export.ToConfig ?? []];
        foreach (int number in keep.Union(toConfig).Where(n => n < 1 || n > steps.Count))
        {
            throw new ArgumentException($"The session has no step {number}");
        }
        foreach (int number in toConfig.Where(n => steps[n - 1].Step.Code is null))
        {
            throw new ArgumentException($"Step {number} ({steps[number - 1].Step.Describe()}) is not a code, so it cannot go in config.g");
        }

        List<string> config = [.. Scenario.Config.Split('\n').Select(l => l.TrimEnd('\r'))];
        while (config.Count > 0 && config[^1].Length == 0)
        {
            config.RemoveAt(config.Count - 1);
        }
        config.AddRange(steps.Where(s => toConfig.Contains(s.Number)).Select(s => s.Step.Code!));
        return new Scenario
        {
            Name = string.IsNullOrWhiteSpace(export.Name) ? Scenario.Name : export.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(export.Description) ? Scenario.Description : export.Description.Trim(),
            Stacks = [.. Scenario.Stacks],
            Boards = new(Scenario.Boards),
            Config = config.Count == 0 ? string.Empty : string.Join('\n', config) + "\n",
            Files = new(Scenario.Files),
            CaptureBoot = Scenario.CaptureBoot,
            Steps = [.. steps.Where(s => keep.Contains(s.Number) && !toConfig.Contains(s.Number)).Select(s => s.Step)]
        };
    }

    /// <summary>Save each stack's capture in its directory, and their differences beside them</summary>
    /// <returns>The files written, by stack name, and by "differences" for the comparison</returns>
    public IReadOnlyDictionary<string, string> Save()
    {
        Dictionary<string, string> saved = [];
        foreach (SessionStack stack in Stacks)
        {
            saved[stack.Name] = stack.Capture.Save(stack.Directory);
        }
        if (Stacks.Count == 2)
        {
            string path = Path.Combine(Directory, "differences.txt");
            File.WriteAllText(path, CaptureComparison.Report(Align().Differences, Stacks[0].Capture, Stacks[1].Capture));
            saved["differences"] = path;
        }
        return saved;
    }

    /// <summary>End the session: stop what is running, save the captures and shut the benches down</summary>
    public async ValueTask DisposeAsync()
    {
        await _ending.CancelAsync();
        await _benches.WaitAsync();
        try
        {
            Save();
            foreach (SessionStack stack in Stacks)
            {
                await stack.Recorder.Bench.DisposeAsync();
            }
        }
        finally
        {
            _benches.Release();
            _ending.Dispose();
        }
    }
}
