using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Motion.InputChannels;

namespace SystemTests.Scenarios.Codes.Motion;

/// <summary>M400, waiting for the machine to stop</summary>
[TestFixture]
[GCode("M400")]
public class M400Tests : BenchFixture
{
    /// <summary>
    /// M400 waits for the queued moves to finish, so afterwards the live machine position has
    /// reached the commanded target
    /// </summary>
    /// <remarks>RRF GCodes2.cpp case 400 locks the movement system and waits for standstill; the
    /// live position is Move.cpp machinePosition</remarks>
    [Test]
    public async Task M400WaitsForStandstill()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xye);

        await bench.Host.ExecuteCodeAsync("G91");
        await bench.Host.ExecuteCodeAsync("G1 X5 F6000");
        string reply = await bench.Host.ExecuteCodeAsync("M400");
        Assert.That(reply.Trim(), Is.Empty, "M400 executes without error (RRF GCodes2.cpp case 400)");

        Assert.That(bench.CanMaster.ScheduledSteps(Machines.XDriver), Is.EqualTo(5 * Machines.XyStepsPerMm),
                    "after M400 the whole 5 mm move has been sent (RRF GCodes2.cpp case 400)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].UserPosition), Is.EqualTo(5.0).Within(1e-3),
                    "after M400 move.axes[0].userPosition is at the target (RRF Move.cpp userPosition)");
        await WaitForMachinePositionAsync(bench, 0, 5.0);
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].MachinePosition), Is.EqualTo(5.0).Within(1e-3),
                    "after M400 move.axes[0].machinePosition settles at the target (RRF Move.cpp machinePosition)");
    }
}
