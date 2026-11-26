using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using DuetEmulation.Captures;
using NUnit.Framework;

namespace EmulationTests.Harness;

/// <summary>
/// The object model diff, captures and their comparison, without an emulator
/// </summary>
[TestFixture]
public class CaptureTests
{
    private static MachineCapture Capture(string stack, params CaptureEvent[] events)
    {
        MachineCapture capture = new(new CaptureHeader("test", stack, new Dictionary<int, string> { [1] = "EXP3HC" }, string.Empty));
        capture.Add(events);
        return capture;
    }

    [Test]
    public void TheDiffListsTheValuesThatChangedByPath()
    {
        JsonNode before = JsonNode.Parse("""{"move":{"axes":[{"homed":false,"letter":"X"}]},"state":{"upTime":1},"tools":[]}""")!;
        JsonNode after = JsonNode.Parse("""{"move":{"axes":[{"homed":true,"letter":"X"},{"homed":false}]},"state":{"upTime":2},"tools":[]}""")!;

        IReadOnlyList<ObjectModelChange> changes = ObjectModelDiff.Compare(before, after, ["state.upTime"]);

        Assert.That(changes.Select(c => c.ToString()), Is.EqualTo(new[]
        {
            "move.axes[0].homed: false -> true",
            "move.axes[1]: null -> {\"homed\":false}"
        }));
    }

    [Test]
    public void AnIgnoredPathCanMatchAnyIndex()
    {
        JsonNode before = JsonNode.Parse("""{"inputs":[{"lineNumber":1,"feedRate":10},{"lineNumber":5}]}""")!;
        JsonNode after = JsonNode.Parse("""{"inputs":[{"lineNumber":2,"feedRate":20},{"lineNumber":6}]}""")!;

        Assert.That(ObjectModelDiff.Compare(before, after, ["inputs[*].lineNumber"]).Select(c => c.Path), Is.EqualTo(new[] { "inputs[0].feedRate" }));
        Assert.That(ObjectModelDiff.Flatten(ObjectModelDiff.Without(after, ["inputs[*].lineNumber"])).Keys, Is.EqualTo(new[] { "inputs[0].feedRate" }));
    }

    [Test]
    public void EventsAtTheSameTimeKeepTheOrderTheyWereAddedIn()
    {
        MachineCapture capture = Capture("rrf");
        capture.Add([new CodeSent(1, "G91"), new CodeReplied(1, "G91", "success", "")]);
        capture.Add([new CodeSent(1, "G1 X1")]);
        capture.Add([new Note(0.5, "earlier")]);

        Assert.That(capture.Events.Select(e => e.Describe()), Is.EqualTo(new[] { "# earlier", "> G91", "< (empty)", "> G1 X1" }));
    }

    [Test]
    public void ACaptureReadsBackAsItWasSaved()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}");
        try
        {
            MachineCapture capture = Capture("dsf",
                new ObjectModelSnapshot(0, JsonNode.Parse("""{"move":{"axes":[]}}""")),
                new CodeSent(1, "M115"),
                new CanFrameSent(1.001, 0, 1, "M115", false, "0102", new JsonObject { ["P"] = 1 }),
                new CodeReplied(1.002, "M115", "warning", "text"),
                new ObjectModelChanged(1.002, "state.status", JsonValue.Create("idle"), JsonValue.Create("busy")),
                new PinChanged(1.5, 1, ["out0"], "Pwm", 0.5, 250),
                new InputDriven(2, 1, "io0.in", "high"),
                new InputChanged(2.5, 1, ["io0.in"], "low"));
            capture.Save(directory);

            MachineCapture read = MachineCapture.Load(directory);

            Assert.That(read.Header.Stack, Is.EqualTo("dsf"));
            Assert.That(read.Events.Select(e => e.Describe()), Is.EqualTo(capture.Events.Select(e => e.Describe())));
            Assert.That(File.ReadAllText(Path.Combine(directory, MachineCapture.ReportFileName)), Does.Contain("> M115"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void IdenticalCapturesDoNotDiffer()
    {
        CaptureEvent[] events =
        [
            new ObjectModelSnapshot(0, JsonNode.Parse("""{"fans":[{"requestedValue":0}]}""")),
            new CodeSent(1, "M106 S0.5"),
            new CanFrameSent(1.001, 0, 1, "SetFanSpeed", false, "00", new JsonObject { ["Pwm"] = 0.5, ["RequestId"] = 3 }),
            new CodeReplied(1.01, "M106 S0.5", "success", ""),
            new ObjectModelChanged(1.01, "fans[0].requestedValue", JsonValue.Create(0), JsonValue.Create(0.5))
        ];

        Assert.That(CaptureComparison.Compare(Capture("rrf", events), Capture("dsf", events)), Is.Empty);
    }

    [Test]
    public void RepliesDifferStepByStep()
    {
        MachineCapture first = Capture("rrf", new CodeSent(1, "M114"), new CodeReplied(1.1, "M114", "success", "X:0 Y:0"));
        MachineCapture second = Capture("dsf", new CodeSent(1, "M114"), new CodeReplied(1.0, "M114", "success", "X:0"));

        IReadOnlyList<CaptureDifference> differences = CaptureComparison.Compare(first, second);

        Assert.That(differences, Has.Count.EqualTo(1));
        Assert.That(differences[0].Aspect, Is.EqualTo("reply"));
        Assert.That(differences[0].Step, Is.EqualTo(2));
    }

    [Test]
    public void TheObjectModelIsComparedByItsStateAtTheEndOfEachStep()
    {
        JsonNode model = JsonNode.Parse("""{"move":{"axes":[{"stepPos":0,"machinePosition":0}]}}""")!;
        // DSF counts the steps when it plans the move, RepRapFirmware when it runs it; both end at 800
        MachineCapture first = Capture("rrf",
            new ObjectModelSnapshot(0, model.DeepClone()),
            new CodeSent(1, "G1 X10"), new CodeReplied(1, "G1 X10", "success", ""),
            new CodeSent(2, "M400"), new CodeReplied(3, "M400", "success", ""),
            new ObjectModelChanged(3, "move.axes[0].stepPos", JsonValue.Create(0), JsonValue.Create(800)),
            new ObjectModelChanged(3, "move.axes[0].machinePosition", JsonValue.Create(0), JsonValue.Create(10.0)));
        MachineCapture second = Capture("dsf",
            new ObjectModelSnapshot(0, model.DeepClone()),
            new CodeSent(1, "G1 X10"), new CodeReplied(1, "G1 X10", "success", ""),
            new ObjectModelChanged(1, "move.axes[0].stepPos", JsonValue.Create(0), JsonValue.Create(800)),
            new CodeSent(2, "M400"), new CodeReplied(3, "M400", "success", ""),
            new ObjectModelChanged(3, "move.axes[0].machinePosition", JsonValue.Create(0), JsonValue.Create(9.9995)));

        IReadOnlyList<CaptureDifference> differences = CaptureComparison.Compare(first, second);

        Assert.That(differences.Select(d => d.ToString()), Is.EqualTo(new[] { "step 2 (G1 X10) om: move.axes[0].stepPos: 0 vs 800" }));
    }

    [Test]
    public void AFrameSentInTheNextStepIsTheSameFrame()
    {
        CanFrameSent move = new(1.01, 0, 1, "MovementLinearShaped", false, "00", new JsonObject { ["Acceleration"] = 1.7777778e-10, ["WhenToExecute"] = 100 });
        MachineCapture first = Capture("rrf", new CodeSent(1, "G1 X10"), move, new CodeReplied(1.02, "G1 X10", "success", ""),
                                       new CodeSent(2, "M400"), new CodeReplied(3, "M400", "success", ""));
        MachineCapture second = Capture("dsf", new CodeSent(1, "G1 X10"), new CodeReplied(1, "G1 X10", "success", ""), new CodeSent(1, "M400"),
                                        move with { T = 1.01, Message = new JsonObject { ["Acceleration"] = 1.7777776e-10, ["WhenToExecute"] = 200 } },
                                        new CodeReplied(3, "M400", "success", ""));

        Assert.That(CaptureComparison.Compare(first, second), Is.Empty);
        Assert.That(CaptureComparison.Compare(first, second, new ComparisonOptions { TimeTolerance = 0.001 })
                        .Where(d => d.Description.Contains("MovementLinearShaped")).Select(d => d.Aspect),
                    Is.EqualTo(new[] { "time" }), "with a tolerance, a frame in another step is a difference of time");
    }

    [Test]
    public void AMissingFrameIsOneDifferenceInItsStep()
    {
        CanFrameSent enable = new(1.001, 0, 1, "SetDriverStates", false, "00", new JsonObject { ["DriversToUpdate"] = 1 });
        CanFrameSent move = new(1.002, 0, 1, "MovementLinearShaped", false, "11", null);
        MachineCapture first = Capture("rrf", new CodeSent(1, "G1 X10"), enable, move, new CodeReplied(1.1, "G1 X10", "success", ""));
        MachineCapture second = Capture("dsf", new CodeSent(1, "G1 X10"), move, new CodeReplied(1.1, "G1 X10", "success", ""));

        IReadOnlyList<CaptureDifference> differences = CaptureComparison.Compare(first, second);

        Assert.That(differences.Select(d => d.ToString()), Is.EqualTo(new[] { "step 2 (G1 X10) can: only in the first: CAN 0->1 SetDriverStates {\"DriversToUpdate\":1}" }));
    }

    [Test]
    public void PeriodicFramesAreLeftOutByDefault()
    {
        MachineCapture first = Capture("rrf", new CanFrameSent(0.5, 0, 63, "TimeSync", false, "00", null));
        MachineCapture second = Capture("dsf");

        Assert.That(CaptureComparison.Compare(first, second), Is.Empty);
        Assert.That(CaptureComparison.Compare(first, second, new ComparisonOptions { IgnoredCanTypes = [] }), Has.Count.EqualTo(1));
    }
}
