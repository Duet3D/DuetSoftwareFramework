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

/// <summary>M401 and M402, deploying and retracting the probe</summary>
[TestFixture]
[GCode("M401", "M402")]
public class M401M402Tests : BenchFixture
{
    /// <summary>
    /// M401 runs deployprobe.g and marks the probe deployed by the user; M402 runs retractprobe.g
    /// and clears the mark
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp cases 401 and 402: M401 clears isDeployedByUser, deploys, then sets it
    /// (OM deployedByUser, ZProbe.cpp table); M402 clears it first and retracts. The macros are
    /// sys/deployprobe.g and sys/retractprobe.g, with the probe-numbered variant tried first
    /// </remarks>
    [Test]
    public async Task M401AndM402TrackDeployedByUser()
    {
        await using Bench bench = await StartBenchAsync(
            ProbeLine + "\nglobal deployRan = 0\nglobal retractRan = 0",
            sd =>
            {
                sd.WriteSys("deployprobe.g", "set global.deployRan = global.deployRan + 1\n");
                sd.WriteSys("retractprobe.g", "set global.retractRan = global.retractRan + 1\n");
            });

        await bench.Host.ExecuteCodeAsync("M401");
        bool deployedAfter401 = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.DeployedByUser);
        int deployRuns = await bench.Host.GlobalAsync("deployRan");
        Assert.Multiple(() =>
        {
            Assert.That(deployedAfter401, Is.True,
                        "M401 sets sensors.probes[0].deployedByUser (RRF GCodes2.cpp case 401)");
            Assert.That(deployRuns, Is.EqualTo(1),
                        "M401 runs deployprobe.g exactly once (RRF DeployZProbe)");
        });

        await bench.Host.ExecuteCodeAsync("M402");
        bool deployedAfter402 = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.DeployedByUser);
        int retractRuns = await bench.Host.GlobalAsync("retractRan");
        Assert.Multiple(() =>
        {
            Assert.That(deployedAfter402, Is.False,
                        "M402 clears sensors.probes[0].deployedByUser (RRF GCodes2.cpp case 402)");
            Assert.That(retractRuns, Is.EqualTo(1),
                        "M402 runs retractprobe.g exactly once (RRF RetractZProbe)");
        });

        // deployprobe.g should always run
        await bench.Host.ExecuteCodeAsync("M401");
        await bench.Host.ExecuteCodeAsync("M401");
        deployedAfter401 = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.DeployedByUser);
        deployRuns = await bench.Host.GlobalAsync("deployRan");
        Assert.Multiple(() =>
        {
            Assert.That(deployedAfter401, Is.True,
                        "M401 sets sensors.probes[0].deployedByUser (RRF GCodes2.cpp case 401)");
            Assert.That(deployRuns, Is.EqualTo(3),
                        "M401 runs deployprobe.g once per invokation");
        });

        // Retract regardless of number of times deployed
        await bench.Host.ExecuteCodeAsync("M402");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.DeployedByUser).Result, Is.False);
            Assert.That(bench.Host.GlobalAsync("retractRan").Result, Is.EqualTo(2));
        });

        // retractprobe.g should always run even if already retracted
        await bench.Host.ExecuteCodeAsync("M402");
        Assert.Multiple(() =>
        {
            Assert.That(bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.DeployedByUser).Result, Is.False);
            Assert.That(bench.Host.GlobalAsync("retractRan").Result, Is.EqualTo(3));
        });
    }
}
