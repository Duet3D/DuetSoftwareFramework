using System;
using System.Linq;
using DuetEmulation;
using DuetEmulation.Boards;
using DuetEmulation.Scenarios;
using NUnit.Framework;

namespace EmulationTests.Harness;

/// <summary>
/// Reading scenario files and steps, without an emulator
/// </summary>
[TestFixture]
public class ScenarioTests
{
    [Test]
    public void AScenarioReadsItsBoardsConfigAndSteps()
    {
        Scenario scenario = Scenario.Parse("""
            name: homing
            stacks: [dsf]
            boards:
              1: EXP3HC
              123: exp1hcl
            config: |
              M584 X1.0
            files:
              sys/homex.g: G1 H1 X-100
            steps:
              - code: G91
              - start: G1 H1 X-100 F600
              - drive: { board: 1, port: io0.in, level: high, afterSteps: { driver: 0, steps: 2000 } }
              - await: G1 H1 X-100 F600
                timeout: 20s
              - wait: 500ms
              - tacho: { board: 1, port: out3.tach, fan: out3, maxRpm: 3000 }
              - pulses: { board: 1, port: out4.tach, frequency: 100 }
            """, "homing.yaml");

        Assert.Multiple(() =>
        {
            Assert.That(scenario.Name, Is.EqualTo("homing"));
            Assert.That(scenario.StackList(), Is.EqualTo(new[] { Stack.Dsf }));
            Assert.That(scenario.BoardTypes()[1], Is.SameAs(BoardTypes.Exp3hc));
            Assert.That(scenario.BoardTypes()[123], Is.SameAs(BoardTypes.Exp1hcl));
            Assert.That(scenario.Config, Is.EqualTo("M584 X1.0\n"));
            Assert.That(scenario.Files["sys/homex.g"], Is.EqualTo("G1 H1 X-100"));
            Assert.That(scenario.Steps.Select(s => s.Describe()), Is.EqualTo(new[]
            {
                "G91", "start G1 H1 X-100 F600", "drive 1.io0.in high after 2000 steps of driver 0", "await G1 H1 X-100 F600",
                "wait 500ms", "tacho 1.out3.tach following out3", "pulses 1.out4.tach 100Hz"
            }));
            Assert.That(scenario.Steps[3].TimeoutSpan, Is.EqualTo(TimeSpan.FromSeconds(20)));
        });
    }

    [Test]
    public void AScenarioWithoutANameIsNamedByItsFile() =>
        Assert.That(Scenario.Parse("steps: []", "dir/fans.yaml").Name, Is.EqualTo("fans"));

    [TestCase("steps: [{ code: G1, wait: 1s }]", "step 1: a step can only do one thing")]
    [TestCase("steps: [{ timeout: 1s }]", "step 1: a step needs one of")]
    [TestCase("steps: [{ drive: { board: 1, port: io0.in } }]", "step 1: drive needs level")]
    [TestCase("steps: [{ drive: { board: 1, port: io0.in, level: sideways } }]", "is not a level")]
    [TestCase("steps: [{ wait: soon }]", "is not a duration")]
    [TestCase("steps: [{ tacho: { board: 1, port: out3.tach } }]", "tacho needs fan")]
    [TestCase("boards: { 1: EXP9ZZ }", "cannot emulate a EXP9ZZ")]
    [TestCase("stacks: [duet2]", "is not a stack")]
    [TestCase("steps: [{ cod: G1 }]", "test.yaml")]
    public void AnInvalidScenarioIsRefusedSayingWhy(string yaml, string reason) =>
        Assert.That(() => Scenario.Parse(yaml, "test.yaml"), Throws.TypeOf<FormatException>().With.Message.Contains(reason));

    [Test]
    public void StepsReadAsAListOrAsOne()
    {
        Assert.That(Scenario.ParseSteps("code: M115").Select(s => s.Code), Is.EqualTo(new[] { "M115" }));
        Assert.That(Scenario.ParseSteps("""{"code": "M115"}""").Select(s => s.Code), Is.EqualTo(new[] { "M115" }));
        Assert.That(Scenario.ParseSteps("- code: G91\n- wait: 2s").Select(s => s.Describe()), Is.EqualTo(new[] { "G91", "wait 2s" }));
        Assert.That(Scenario.ParseSteps("""[{"code": "G91"}, {"note": "now"}]""").Select(s => s.Describe()), Is.EqualTo(new[] { "G91", "# now" }));
    }

    [TestCase("500ms", 500)]
    [TestCase("2s", 2000)]
    [TestCase("1.5", 1500)]
    [TestCase("250us", 0.25)]
    [TestCase("1min", 60000)]
    public void DurationsReadInTheirUnits(string text, double milliseconds) =>
        Assert.That(Scenario.ParseDuration(text).TotalMilliseconds, Is.EqualTo(milliseconds));

    [TestCase(500, "500ms")]
    [TestCase(2000, "2s")]
    [TestCase(1500, "1.5s")]
    [TestCase(0.25, "250us")]
    [TestCase(0, "0s")]
    public void DurationsAreWrittenSoTheyReadBack(double milliseconds, string text)
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(milliseconds);
        Assert.That(Scenario.FormatDuration(duration), Is.EqualTo(text));
        Assert.That(Scenario.ParseDuration(text), Is.EqualTo(duration));
    }

    [Test]
    public void TheCommentAFileStartsWithIsItsDescription()
    {
        Scenario scenario = Scenario.Parse("""
            # A homing move stopped by an endstop
            # that triggers part way
            #
            # A second paragraph
            name: homing
            # not part of it
            """);

        Assert.That(scenario.Description, Is.EqualTo("A homing move stopped by an endstop that triggers part way\nA second paragraph"));
        Assert.That(Scenario.Parse("name: homing").Description, Is.Null);
    }

    [Test]
    public void AScenarioIsWrittenAsTheExamplesAreWritten()
    {
        Scenario scenario = new()
        {
            Name = "endstop homing",
            Description = "A homing move stopped by an endstop",
            Boards = { [121] = "TOOL1RR", [1] = "EXP3HC" },
            Config = "M584 X1.0\nM574 X1 S1 P\"1.io0.in\"",
            Steps = [.. Scenario.ParseSteps("""
                - code: G91
                - drive: { board: 1, port: io0.in, level: high, afterSteps: { driver: 0, steps: 2000 } }
                - code: G1 H1 X-100 F3000
                  timeout: 20s
                - wait: 1s
                """)]
        };

        Assert.That(scenario.ToYaml(), Is.EqualTo("""
            # A homing move stopped by an endstop
            name: endstop homing
            boards:
              1: EXP3HC
              121: TOOL1RR
            config: |
              M584 X1.0
              M574 X1 S1 P"1.io0.in"
            steps:
              - code: G91
              - drive: {board: 1, port: io0.in, level: high, afterSteps: {driver: 0, steps: 2000}}
              - code: G1 H1 X-100 F3000
                timeout: 20s
              - wait: 1s

            """.Replace("\r\n", "\n")));
    }

    [Test]
    public void AWrittenScenarioReadsBackTheSame()
    {
        Scenario scenario = new()
        {
            Name = "everything: at once",
            Description = "First paragraph\nSecond # paragraph",
            Stacks = ["rrf"],
            Boards = { [1] = "EXP3HC" },
            Config = "M950 F0 C\"1.out3+out3.tach\" ; a fan\n\nM106 P0 S0\n",
            Files = { ["sys/homex.g"] = "G91\nG1 H1 X-100 F3000\n", ["macros/one line.g"] = "M115" },
            CaptureBoot = true,
            Steps = [.. Scenario.ParseSteps("""
                - code: "M409 K\"move.axes[0]\" F\"v\""
                - start: "G1 X10 # not a comment"
                - await: "G1 X10 # not a comment"
                - note: "- looks like a list"
                - release: { board: 1, port: io0.in }
                - analog: { board: 1, port: temp0, value: 0 }
                - thermistor: { board: 1, port: temp0, celsius: 60.5 }
                - pulses: { board: 1, port: out4.tach, frequency: 0 }
                - tacho: { board: 1, port: out3.tach, fan: out3, maxRpm: 3000, inverted: true }
                """)]
        };

        Scenario read = Scenario.Parse(scenario.ToYaml());

        Assert.Multiple(() =>
        {
            Assert.That(read.Name, Is.EqualTo(scenario.Name));
            Assert.That(read.Description, Is.EqualTo(scenario.Description));
            Assert.That(read.Stacks, Is.EqualTo(scenario.Stacks));
            Assert.That(read.Boards, Is.EqualTo(scenario.Boards));
            Assert.That(read.Config, Is.EqualTo(scenario.Config));
            Assert.That(read.Files, Is.EqualTo(scenario.Files));
            Assert.That(read.CaptureBoot, Is.True);
            Assert.That(read.Steps.Select(s => s.ToYaml()), Is.EqualTo(scenario.Steps.Select(s => s.ToYaml())));
            Assert.That(read.Steps[^1].Tacho!.PulsesPerRev, Is.Null, "a default left out stays left out");
        });
    }

    [Test]
    public void TheDefaultsAreLeftOut() =>
        Assert.That(new Scenario { Name = "nothing" }.ToYaml(), Is.EqualTo("name: nothing\n"));
}
