using System.Threading.Tasks;
using DuetControlServer.Motion;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace SystemTests.Scenarios.Behaviour.Moves;

/// <summary>
/// A motion engine that could not be started refuses moves rather than leaving them waiting for it
/// </summary>
/// <remarks>
/// The engine's rings are not built until it starts, and until then it has no room for a move. That is
/// the answer a move retries on, which is right for the moments before the engine comes up and wrong
/// once it never will: the code would wait for ever with nothing said. The real engine cannot be made
/// to fail from a configuration, so these give the planner the report <c>MotionService</c> gives it
/// </remarks>
[TestFixture]
public class MotionEngineFailureTests : BenchFixture
{
    [TestCase("G1 X10 F6000")]
    [TestCase("G0 Y10")]
    public async Task AMoveIsRefusedOnceTheEngineHasFailedToStart(string code)
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze, Machines.OneTool);
        bench.Host.Services.GetRequiredService<MovePlanner>().ReportEngineFailed();

        string reply = await bench.Host.ExecuteCodeAsync(code);

        Assert.That(reply, Does.StartWith("Error: ").And.Contain("Move could not be planned"));
    }

    [Test]
    public async Task WaitingForTheMachineToStopDoesNotHangOnAFailedEngine()
    {
        // No move was given to the engine, so there is nothing for M400 to wait for
        await using Bench bench = await Bench.StartAsync(Machines.Xyze, Machines.OneTool);
        bench.Host.Services.GetRequiredService<MovePlanner>().ReportEngineFailed();

        await bench.Host.ExecuteCodeAsync("G1 X10 F6000");
        string reply = await bench.Host.ExecuteCodeAsync("M400");

        Assert.That(reply, Does.Not.StartWith("Error: "));
    }
}
