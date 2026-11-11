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

/// <summary>M851, the probe trigger height</summary>
[TestFixture]
[GCode("M851")]
public class M851Tests : BenchFixture
{
    /// <summary>
    /// M851 is the Marlin alias: Z sets the trigger height of probe 0 negated, and the report prints
    /// the negated trigger height
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 851 calls SetTriggerHeight(-Z) on probe 0, so M851 Z-0.8 leaves
    /// sensors.probes[0].triggerHeight at 0.8; without Z it reports
    /// "Z probe offset is -GetConfiguredTriggerHeight() mm"
    /// </remarks>
    [Category("KnownGap")]   // RRF hard sets offsets[2] regardless of whether a Z axis is configured
    [Test]
    public async Task M851SetsTriggerHeightNegated()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);

        string reply = await bench.Host.ExecuteCodeAsync("M851 Z-0.8");
        Assert.That(reply.Trim(), Is.Empty, "M851 Z was accepted");

        Probe? probe = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]);
        Assert.Multiple(() =>
        {
            Assert.That(probe!.TriggerHeight, Is.EqualTo(0.8f).Within(1e-4),
                        "M851 Z-0.8 sets sensors.probes[0].triggerHeight to 0.8 (RRF GCodes2.cpp case 851, SetTriggerHeight(-Z))");
            Assert.That(ProbeOffset(probe, 2), Is.EqualTo(-0.8f).Within(1e-4),
                        "M851 leaves sensors.probes[0].offsets[2] at the negated trigger height, one offset per visible axis (RRF ZProbe.cpp offsets array)");
        });

        string report = await bench.Host.ExecuteCodeAsync("M851");
        Assert.That(report.Trim(), Is.EqualTo("Z probe offset is -0.80mm"),
                    "M851 reports the negated trigger height (RRF GCodes2.cpp case 851)");
    }
}
