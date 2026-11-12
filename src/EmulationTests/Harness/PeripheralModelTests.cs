using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuetEmulation;
using DuetEmulation.Boards;
using DuetEmulation.Emulator;
using NUnit.Framework;

namespace EmulationTests.Harness;

/// <summary>
/// The peripheral models compile in Renode. A bench combines the models it needs into one file, which
/// pools their using directives, so each set a bench can combine is compiled here: a model that only
/// compiled beside another would otherwise break the first bench without that other one
/// </summary>
[TestFixture]
[Category("Emulation")]
public class PeripheralModelTests
{
    private static IEnumerable<TestCaseData> ModelSets() =>
        BoardTypes.All.Select(b => new TestCaseData(b.FirmwarePreset, b.Models.ToArray()).SetArgDisplayNames(b.FirmwarePreset));

    [TestCaseSource(nameof(ModelSets))]
    public async Task TheModelsOfABoardCompileWithoutTheOthers(string preset, string[] models)
    {
        string directory = Path.Combine(Runs.Directory(this), preset);
        Directory.CreateDirectory(directory);
        await using RenodeInstance renode = await RenodeInstance.StartAsync(Path.Combine(directory, "renode.log"));

        await renode.ExecuteAsync($"include {MonitorSyntax.PathArgument(PeripheralModels.Combine(models))}");
    }

    [Test]
    public void EveryModelBelongsToABoard()
    {
        string root = Path.Combine(RepositoryPaths.Renode, "Peripherals");
        IEnumerable<string> models = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'));

        Assert.That(models, Is.SubsetOf(BoardTypes.All.SelectMany(b => b.Models).Distinct()));
    }
}
