using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuetEmulation;
using DuetEmulation.Captures;
using DuetEmulation.Scenarios;
using NUnit.Framework;

namespace EmulationTests.Scenarios;

/// <summary>
/// The example scenarios run on each stack they name, every code finishing without an error, so that
/// they stay a working place to start from
/// </summary>
[TestFixture]
[Category("Emulation")]
public class ExampleScenarioTests
{
    private static readonly string Examples = Path.Combine(RepositoryPaths.Root, "src", "DuetEmulation", "Scenarios", "Examples");

    private static IEnumerable<TestCaseData> Cases() =>
        Directory.GetFiles(Examples, "*.yaml").Order().SelectMany(path => Scenario.Load(path).StackList()
            .Select(stack => new TestCaseData(path, stack)
                .SetName($"{Path.GetFileNameWithoutExtension(path)} on {stack}")
                .SetCategory(stack.ToString())));

    [TestCaseSource(nameof(Cases))]
    public async Task RunsWithoutErrors(string path, Stack stack)
    {
        Scenario scenario = Scenario.Load(path);
        string directory = Path.Combine(Runs.Directory(this), $"{Path.GetFileNameWithoutExtension(path)}-{stack}");

        MachineCapture capture = await ScenarioRunner.RunAsync(scenario, stack, directory);

        List<CodeReplied> replies = [.. capture.Events.OfType<CodeReplied>()];
        Assert.That(replies.Select(r => r.Code), Is.EqualTo(scenario.Steps.Where(s => s.Code is not null).Select(s => s.Code)));
        Assert.That(replies.Where(r => r.Severity == "error").Select(r => $"{r.Code}: {r.Text}"), Is.Empty);
        Assert.That(File.Exists(Path.Combine(directory, MachineCapture.FileName)));
    }
}
