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

/// <summary>G31, the probe parameters</summary>
[TestFixture]
[GCode("G31")]
public class G31Tests : BenchFixture
{
    /// <summary>
    /// G31 writes the threshold, trigger height and per-axis offsets, with the Z offset held as the
    /// negative of the trigger height
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::HandleG31 (ZProbe.cpp): P writes targetAdcValue (OM threshold), X and Y
    /// write offsets[axis], Z writes offsets[Z] = -Z. The OM reports triggerHeight as
    /// -offsets[Z_AXIS] and offsets[] as stored, so after G31 Z1.25 the trigger height is 1.25 and
    /// offsets[2] is -1.25. The report wording is the same function's else branch
    /// </remarks>
    [Test]
    public async Task G31SetsProbeParameters()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);

        string reply = await bench.Host.ExecuteCodeAsync("G31 K0 P600 X-10 Y5 Z1.25");
        Assert.That(reply.Trim(), Is.Empty, "G31 was accepted");

        Probe? probe = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]);
        Assert.Multiple(() =>
        {
            Assert.That(probe!.Threshold, Is.EqualTo(600),
                        "G31 P sets sensors.probes[0].threshold (RRF ZProbe::HandleG31, targetAdcValue)");
            Assert.That(probe.TriggerHeight, Is.EqualTo(1.25f).Within(1e-4),
                        "G31 Z sets sensors.probes[0].triggerHeight (RRF ZProbe.cpp OM table, -offsets[Z])");
            Assert.That(ProbeOffset(probe, 0), Is.EqualTo(-10.0f).Within(1e-4),
                        "G31 X sets sensors.probes[0].offsets[0] (RRF ZProbe::HandleG31)");
            Assert.That(ProbeOffset(probe, 1), Is.EqualTo(5.0f).Within(1e-4),
                        "G31 Y sets sensors.probes[0].offsets[1] (RRF ZProbe::HandleG31)");
            Assert.That(ProbeOffset(probe, 2), Is.EqualTo(-1.25f).Within(1e-4),
                        "G31 Z stores offsets[Z] as the negated trigger height (RRF ZProbe::HandleG31)");
        });

        string report = await bench.Host.ExecuteCodeAsync("G31 K0");
        Assert.That(report, Does.Contain("threshold 600"),
                    "the G31 report includes the threshold (RRF ZProbe::HandleG31 report)");
        Assert.That(report, Does.Contain("trigger height 1.250"),
                    "the G31 report includes the trigger height (RRF ZProbe::HandleG31 report)");
    }
}
