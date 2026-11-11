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

/// <summary>M577, waiting for an endstop</summary>
[TestFixture]
[GCode("M577")]
public class M577Tests : BenchFixture
{
    /// <summary>
    /// M577 X blocks until the X endstop reports the wanted state, read from the same input state
    /// the object model holds
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes::WaitForPin (GCodes3.cpp) keeps the code unfinished until every named
    /// endstop reports the S state; S defaults to 1, waiting for the endstop to trigger
    /// </remarks>
    [Test]
    public async Task M577WaitsForEndstopTrigger()
    {
        await using Bench bench = await StartBenchAsync(XEndstopLine);

        Task<string> wait = bench.Host.ExecuteCodeAsync("M577 X");
        await Task.Delay(300);
        Assert.That(wait.IsCompleted, Is.False,
                    "M577 X keeps waiting while the endstop is not triggered (RRF GCodes::WaitForPin)");

        bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteEndstops.HandleFor(0), active: true);
        string reply = await wait;
        Assert.That(reply.Trim(), Is.Empty, "M577 X completed once the endstop triggered");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Triggered), Is.True,
                    "the state M577 waited for is sensors.endstops[0].triggered (RRF GCodes::WaitForPin)");
    }
}
