using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DuetEmulation.Boards;
using DuetEmulation.Rrf;
using NUnit.Framework;

namespace EmulationTests.Rrf;

/// <summary>
/// The RepRapFirmware bench is deterministic: the same boards given the same codes send the same CAN
/// frames at the same emulated times and print the same console text, every time. Tests rely on that to
/// mean the same thing on every run, so check it with two boards, which is where it could go wrong
/// </summary>
[TestFixture]
[Category("Emulation")]
public class DeterminismTests
{
    [Test]
    public async Task TwoRunsAreIdentical()
    {
        List<(string Can, string Console)> runs = [];
        for (int run = 0; run < 2; run++)
        {
            string directory = Path.Combine(Runs.Directory(this), $"run{run}");
            await using RrfBench bench = await RrfBench.StartAsync(
                new Dictionary<int, BoardType> { [1] = BoardTypes.Exp3hc }, "M564 H0 S0\nM584 X1.0\nM92 X80\n", directory);
            await bench.SendCodeAsync("M569 P1.0 S0");
            await bench.SendCodeAsync("G91");
            await bench.SendCodeAsync("G1 X10 F600");
            await bench.SendCodeAsync("M400");
            runs.Add((await File.ReadAllTextAsync(bench.Can.Path), bench.Console.Transcript));
        }

        Assert.That(runs[1].Can, Is.EqualTo(runs[0].Can));
        Assert.That(runs[1].Console, Is.EqualTo(runs[0].Console));
    }
}
