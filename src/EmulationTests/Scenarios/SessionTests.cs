using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetEmulation;
using DuetEmulation.Captures;
using DuetEmulation.Scenarios;
using DuetEmulation.Sessions;
using NUnit.Framework;

namespace EmulationTests.Scenarios;

/// <summary>
/// An interactive session on RepRapFirmware with an EXP3HC: its steps kept as they run, a failed step,
/// the benches shared between steps and queries, and the session saved as a scenario that replays it
/// </summary>
[TestFixture]
[Category("Emulation")]
[Category(nameof(Stack.Rrf))]
public class SessionTests
{
    private const int Address = 1;

    private Session _session = null!;
    private readonly ConcurrentQueue<SessionStep> _changes = new();
    private readonly ConcurrentQueue<int> _captureChanges = new();

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        Scenario scenario = Scenario.Parse($"""
            name: session tests
            stacks: [rrf]
            boards:
              {Address}: EXP3HC
            config: |
              M564 H0 S0
              M584 X{Address}.0
              M92 X80
            steps:
              - code: G91
            """);
        _session = await Session.StartAsync(scenario, Runs.Directory(this));
        _session.StepChanged += _changes.Enqueue;
        _session.CaptureChanged += (_, from) => _captureChanges.Enqueue(from);
    }

    [OneTimeTearDown]
    public async Task StopAsync() => await _session.DisposeAsync();

    [Test, Order(1)]
    public void BootingLeavesTheStepsAndConfigToRun()
    {
        Assert.That(_session.Steps, Is.Empty, "the scenario's steps are the caller's to run, once it follows the session");
        Assert.That(_session.Scenario.Steps, Is.Empty, "what the session started from is its boards, config.g and files");
        Assert.That(_session.ConfigHeld, Is.True, "config.g waits for the steps before it");
        Assert.That(_session.Stacks.Select(s => s.Name), Is.EqualTo(new[] { "rrf" }));
        Assert.That(_session.Stacks[0].Recorder.ObjectModel?["move"]?["axes"]?[0]?["drivers"]?[0]?.GetValue<string>(), Is.EqualTo("0.0"),
                    "the model is read before config.g has mapped X to the expansion board");
    }

    [Test, Order(2)]
    public async Task ConfigIsAStepAfterTheStepsBeforeIt()
    {
        IReadOnlyList<SessionStep> before = await _session.RunAsync(Scenario.ParseSteps("code: M115"));
        SessionStep config = await _session.RunConfigAsync();

        IReadOnlyList<CaptureEvent> events = _session.Stacks[0].Capture.Events;
        IReadOnlyList<CaptureEvent> configEvents = CaptureComparison.EventsOfStep(events, config.CaptureStep);
        Assert.Multiple(() =>
        {
            Assert.That(before.Single().Kind, Is.EqualTo(StepKind.BeforeConfig));
            Assert.That(config.Kind, Is.EqualTo(StepKind.Config));
            Assert.That(config.State, Is.EqualTo(StepState.Done));
            Assert.That(config.Describe(), Is.EqualTo("config.g"));
            Assert.That(CaptureComparison.StepAction(configEvents[0]), Is.EqualTo("config.g"));
            Assert.That(configEvents.OfType<ObjectModelChanged>().Select(c => c.Path), Has.Some.StartsWith("move.axes[0].drivers"),
                        "what config.g changed in the model is config.g's step");
            Assert.That(configEvents.OfType<CanFrameSent>(), Is.Not.Empty, "config.g configures the expansion board over CAN");
            Assert.That(_session.ConfigHeld, Is.False);
            Assert.That(_session.Scenario.BeforeConfig.Select(s => s.Code), Is.EqualTo(new[] { "M115" }));
            Assert.That(() => _session.RunConfigAsync(), Throws.InvalidOperationException);
        });
    }

    [Test, Order(3)]
    public async Task EachStepIsKeptWithWhatItDidOnEachStack()
    {
        int before = _session.Steps.Count;

        IReadOnlyList<SessionStep> steps = await _session.RunAsync(Scenario.ParseSteps("""
            - code: G91
            - note: moving
            - code: G1 X10 F6000
            - wait: 100ms
            """));

        Assert.Multiple(() =>
        {
            Assert.That(steps.Select(s => s.Number), Is.EqualTo(Enumerable.Range(before + 1, 4)));
            Assert.That(steps.All(s => s.State == StepState.Done && s.Kind == StepKind.Step));
            Assert.That(steps[0].Stacks["rrf"].Reply, Is.EqualTo(new CodeReply(string.Empty, MessageType.Success)));
            Assert.That(steps[1].Stacks["rrf"].Reply, Is.Null, "a note has no reply");
            Assert.That(steps[3].Stacks["rrf"].Finished - steps[3].Stacks["rrf"].Started, Is.GreaterThanOrEqualTo(0.1));
            Assert.That(_session.Steps.Skip(before), Is.EqualTo(steps));
            Assert.That(_changes.Count(c => c.Number == steps[0].Number), Is.EqualTo(2), "a step is announced when it starts and when it finishes");
            Assert.That(_captureChanges, Is.Not.Empty);
        });

        // Each step is one step of the capture, after its start
        IReadOnlyList<CaptureEvent> events = _session.Stacks[0].Capture.Events;
        Assert.That(steps.Select(s => CaptureComparison.StepAction(CaptureComparison.EventsOfStep(events, s.CaptureStep)[0])),
                    Is.EqualTo(new[] { "G91", "# moving", "G1 X10 F6000", "wait 100ms" }));
    }

    [Test, Order(4)]
    public async Task AFailedStepStopsTheStepsAfterIt()
    {
        int before = _session.Steps.Count;

        IReadOnlyList<SessionStep> steps = await _session.RunAsync(Scenario.ParseSteps("""
            - await: G1 X999
            - code: M115
            """));

        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].State, Is.EqualTo(StepState.Failed));
        Assert.That(steps[0].Stacks["rrf"].Error, Does.Contain("was not started"));
        Assert.That(_session.Steps, Has.Count.EqualTo(before + 1));

        // The failed step is a step of the capture all the same, so the steps after it stay in line with theirs
        IReadOnlyList<SessionStep> after = await _session.RunAsync(Scenario.ParseSteps("code: M115"));
        IReadOnlyList<CaptureEvent> events = _session.Stacks[0].Capture.Events;
        Assert.That(CaptureComparison.StepAction(CaptureComparison.EventsOfStep(events, steps[0].CaptureStep)[0]), Is.EqualTo("await G1 X999"));
        Assert.That(CaptureComparison.StepAction(CaptureComparison.EventsOfStep(events, after[0].CaptureStep)[0]), Is.EqualTo("M115"));
        Assert.That(CaptureComparison.StepCount(_session.Stacks[0].Capture), Is.EqualTo(_session.Steps.Count + 1));
    }

    [Test, Order(5)]
    public async Task AQueryUsesTheBenchesBetweenSteps()
    {
        Task<IReadOnlyList<SessionStep>> running = _session.RunAsync(Scenario.ParseSteps("wait: 500ms"));
        Task<string?> axis = _session.UseBenchesAsync(async token =>
            (await _session.Stacks[0].Recorder.Bench.GetObjectModelAsync("move.axes[0].letter", token))?.GetValue<string>());

        // The capture is read while the step may be recording into it
        Assert.That(_session.Stacks[0].Capture.Events, Is.Not.Empty);
        Assert.That(await axis, Is.EqualTo("X"));
        Assert.That((await running)[0].State, Is.EqualTo(StepState.Done));
    }

    [Test, Order(6)]
    public async Task TheSessionIsSavedAsAScenarioThatReplaysIt()
    {
        IReadOnlyList<SessionStep> steps = _session.Steps;
        SessionStep move = steps.First(s => s.Step?.Code == "G1 X10 F6000");
        SessionStep failed = steps.First(s => s.State == StepState.Failed);

        Scenario scenario = _session.Export(new SessionExport(
            Name: "replayed",
            Description: "A session saved and replayed",
            Steps: [.. steps.Where(s => s != failed).Select(s => s.Number)],
            ToConfig: [steps.First(s => s.Step?.Code == "G91").Number]));

        Assert.Multiple(() =>
        {
            Assert.That(scenario.Name, Is.EqualTo("replayed"));
            Assert.That(scenario.Boards, Is.EqualTo(new Dictionary<int, string> { [Address] = "EXP3HC" }));
            Assert.That(scenario.Config, Is.EqualTo("M564 H0 S0\nM584 X1.0\nM92 X80\nG91\n"), "the code moved to config.g goes at its end");
            Assert.That(scenario.Steps.Select(s => s.Describe()), Does.Not.Contain("await G1 X999"));
            Assert.That(scenario.Steps.Any(s => s.Code == "G91"), Is.False, "a code moved to config.g is not a step as well");
            Assert.That(scenario.BeforeConfig.Select(s => s.Code), Is.EqualTo(new[] { "M115" }), "a step before config.g stays before it");
            Assert.That(scenario.Steps.Any(s => s.Code == "M115" && s == scenario.BeforeConfig[0]), Is.False);
            Assert.That(() => _session.Export(new SessionExport(ToConfig: [steps.First(s => s.Step?.Note is not null).Number])),
                        Throws.ArgumentException.With.Message.Contains("is not a code"));
            Assert.That(() => _session.Export(new SessionExport(ToConfig: [steps.First(s => s.Kind == StepKind.BeforeConfig).Number])),
                        Throws.ArgumentException.With.Message.Contains("is not a code run after config.g"));
            Assert.That(() => _session.Export(new SessionExport(Steps: [999])), Throws.ArgumentException.With.Message.Contains("no step 999"));
        });

        Scenario read = Scenario.Parse(scenario.ToYaml());
        MachineCapture replay = await ScenarioRunner.RunAsync(read, Stack.Rrf, Path.Combine(Runs.Directory(this), "replay"));
        Assert.That(replay.Events.OfType<CodeReplied>().Single(r => r.Code == move.Step!.Code).Severity, Is.EqualTo("success"));
        Assert.That(read.Description, Is.EqualTo("A session saved and replayed"));
    }
}
