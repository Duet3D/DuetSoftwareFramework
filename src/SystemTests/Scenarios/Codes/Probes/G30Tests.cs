using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;
using NUnit.Framework;
using static SystemTests.Scenarios.Codes.Probes.ProbeSetup;

namespace SystemTests.Scenarios.Codes.Probes;

/// <summary>G30, probing a single point</summary>
[TestFixture]
[GCode("G30")]
public class G30Tests : BenchFixture
{
    /// <summary>
    /// Poll an object model expression until it reaches the wanted value and return what it last
    /// read. A position published as a move winds down settles shortly after the code replies, so
    /// the value is waited for; a value that never arrives comes back for the caller to assert on,
    /// which fails naming the field rather than timing out
    /// </summary>
    private static async Task<double> SettledValueAsync(DcsTestHost host, Func<DuetControlServer.Model.ObjectModel, double?> read, double expected,
                                                        double tolerance = 1e-3, int timeoutMs = 5_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        double value;
        do
        {
            value = await host.ReadModelAsync(read) ?? double.NaN;
            if (Math.Abs(value - expected) <= tolerance)
            {
                return value;
            }
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        return value;
    }

    /// <summary>
    /// Plain G30 probes down, records where the probe stopped, then redefines Z so that the point
    /// the probe stopped at reads as the trigger height, and homes Z
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes::ExecuteG30 (GCodes6.cpp) and the probingAtPoint states (GCodes4.cpp).
    /// SetLastStoppedHeight writes sensors.probes[0].lastStopHeight; for plain G30 the Z coordinate
    /// is reset to the trigger height with the height error zeroed and SetAxisIsHomed(Z_AXIS) is
    /// called, then the head moves back up to ZProbe::GetStartingHeight, which is the first dive
    /// height plus the trigger height. So the machine ends at dive height plus trigger height on the
    /// datum the probe just set: 5 + 1.5 here. The probing move is closed by the controller's stop
    /// report and the probe judged triggered from its input, per rrf-differences.md section 2
    /// </remarks>
    [Test]
    public async Task G30SetsZDatumAndHomesZ()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine + " F300\nG31 K0 Z1.5 P500");

        Task<string> probeTask = bench.Host.ExecuteCodeAsync("G30");
        (uint MoveId, (byte Board, byte Driver)[] Drivers) dive =
            await WaitForArmedMoveAsync(bench.CanMaster, 0, "the probing move arriving armed on the probe");
        Assert.That(dive.Drivers, Is.EquivalentTo(new[] { ((byte)1, (byte)2) }),
                    "the probing move arms driver 1.2, which is Z");

        // The input closes and the controller stops the move: both reports, in that order, so the
        // probe reads triggered when the tap is judged
        bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteProbes.HandleFor(0), active: true);
        StopMove(bench.CanMaster, dive);

        string reply = await probeTask;
        Assert.That(reply.Trim(), Is.Empty, "G30 completed without error");
        bool homed = await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Homed);
        const double diveHeight = 5.0, triggerHeight = 1.5;
        double zPosition = await SettledValueAsync(bench.Host, model => model.Move.Axes[2].MachinePosition,
                                                   diveHeight + triggerHeight, 0.01);
        float lastStopHeight = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight);
        Assert.Multiple(() =>
        {
            Assert.That(homed, Is.True,
                        "plain G30 homes Z (RRF GCodes4.cpp, SetAxisIsHomed(Z_AXIS))");
            Assert.That(zPosition, Is.EqualTo(diveHeight + triggerHeight).Within(0.01),
                        "plain G30 sets the Z datum from the probe, so the head reads the dive height above the trigger height (RRF GCodes4.cpp probingAtPoint4/5, ZProbe::GetStartingHeight)");
            Assert.That(lastStopHeight, Is.GreaterThan(0.0f),
                        "G30 records where the probe stopped in sensors.probes[0].lastStopHeight (RRF SetLastStoppedHeight)");
        });
    }
}
