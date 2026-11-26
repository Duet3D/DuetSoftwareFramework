using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DuetEmulation;
using DuetEmulation.Boards;
using NUnit.Framework;

namespace EmulationTests.Scenarios;

/// <summary>
/// Each bench is deterministic: the same boards given the same codes send the same CAN frames at the
/// same emulated times, give the same replies and end in the same state, every time. Tests and captures
/// rely on that to mean the same thing on every run, so check it with an expansion board and a move,
/// which is where it could go wrong
/// </summary>
[TestFixture(Stack.Rrf, Category = nameof(Stack.Rrf))]
[TestFixture(Stack.Dsf, Category = nameof(Stack.Dsf))]
[Category("Emulation")]
public class DeterminismTests(Stack stack)
{
    [Test]
    public async Task TwoRunsAreIdentical()
    {
        List<(string Can, string Replies, string Move)> runs = [];
        for (int run = 0; run < 2; run++)
        {
            string directory = Path.Combine(Runs.Directory(this), $"run{run}");
            await using Bench bench = await Bench.StartAsync(stack, new Dictionary<int, BoardType> { [1] = BoardTypes.Exp3hc },
                                                             "M564 H0 S0\nM584 X1.0\nM92 X80\n", directory);
            List<string> replies = [];
            foreach (string code in new[] { "M569 P1.0 S0", "G91", "G1 X10 F600", "M400", "M114" })
            {
                replies.Add((await bench.SendCodeAsync(code)).ToString());
            }
            runs.Add((await File.ReadAllTextAsync(bench.Can.Path), string.Join('\n', replies),
                      (await bench.GetObjectModelAsync("move.axes"))!.ToJsonString()));
        }

        Assert.That(runs[1].Can, Is.EqualTo(runs[0].Can));
        Assert.That(runs[1].Replies, Is.EqualTo(runs[0].Replies));
        Assert.That(runs[1].Move, Is.EqualTo(runs[0].Move));
    }
}
