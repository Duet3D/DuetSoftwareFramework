using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace SystemTests.Scenarios.Behaviour.ObjectModel;

/// <summary>When <c>state.status</c> changes (RepRap::GetStatusIndex)</summary>
[TestFixture]
public class MachineStatusTests : BenchFixture
{
    private static Task<MachineStatus> StatusAsync(Bench bench) => bench.Host.ReadModelAsync(model => model.State.Status);

    /// <summary>The status follows the job by the time the code that changed it is answered</summary>
    [Test]
    public async Task TheStatusFollowsTheJobAtOnce()
    {
        await using Bench bench = await Bench.With(Machines.Xye, JobMacros.Instrumented, Machines.Xye.Homed)
                                             .WithSd(sd => sd.WriteGCode("job.gcode", "G4 S20\n")).StartAsync();

        await bench.Host.ExecuteCodeAsync("M23 \"0:/gcodes/job.gcode\"");
        Assert.That(await StatusAsync(bench), Is.EqualTo(MachineStatus.Idle), "M23");
        for (int i = 0; i < 2; i++)
        {
            await bench.Host.ExecuteCodeAsync("M24");
            Assert.That(await StatusAsync(bench), Is.EqualTo(MachineStatus.Processing), "M24");
            await bench.Host.ExecuteCodeAsync("M25");
            Assert.That(await StatusAsync(bench), Is.EqualTo(MachineStatus.Paused), "M25");
        }
    }

    /// <summary>The machine is busy from the code that queues a move until the move has ended</summary>
    [Test]
    public async Task TheMachineIsBusyWhileItMoves()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye, Machines.Xye.Homed);

        await bench.Host.ExecuteCodeAsync("G1 X20 F3000");
        Assert.That(await StatusAsync(bench), Is.EqualTo(MachineStatus.Busy));
        await bench.Host.ExecuteCodeAsync("M400");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        Assert.That(await bench.Host.MachinePositionAsync(0), Is.EqualTo(20.0).Within(0.1), "idle once the move has ended");
    }

    /// <summary>The machine is busy while an input runs a macro (GCodes::DoingFileMacro)</summary>
    [Test]
    public async Task TheMachineIsBusyWhileItRunsAMacro()
    {
        await using Bench bench = await Bench.With(Machines.Xye, "global status = \"\"").WithSd(sd =>
            sd.WriteMacro("status.g", "set global.status = state.status\n")).StartAsync();

        await bench.Host.ExecuteCodeAsync("M98 P\"0:/macros/status.g\"");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Global["status"].ToString()), Is.EqualTo("busy"));
            Assert.That(await StatusAsync(bench), Is.EqualTo(MachineStatus.Idle), "the macro has ended");
        });
    }
}
