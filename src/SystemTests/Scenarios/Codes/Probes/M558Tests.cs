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

/// <summary>M558, configuring a probe</summary>
[TestFixture]
[GCode("M558")]
public class M558Tests : BenchFixture
{
    /// <summary>
    /// M558 writes every probe parameter into sensors.probes[0]
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::Configure (ZProbe.cpp) for H, F, T, B, R, S and A; the type and port are
    /// GCodes2.cpp case 558 creating the probe. The OM fields and units are ZProbe.cpp's table:
    /// diveHeights in mm, speeds and travelSpeed reported in mm/min (InverseConvertSpeedToMmPerMin),
    /// maxProbeCount from maxTaps, tolerance from S, recoveryTime from R, disablesHeaters from B.
    /// The port is the DSF addition of rrf-differences.md section 3
    /// </remarks>
    [Category("KnownGap")]   // likely a units difference between gcode and OM
    [Test]
    public async Task M558ConfiguresProbeInObjectModel()
    {
        await using Bench bench = await StartBenchAsync();

        string reply = await bench.Host.ExecuteCodeAsync(
            $"M558 K0 P8 C\"{Expansion.GpioIn(Machines.DriverBoard, 1)}\" H5:2 F600:300 T5000 R0.2 S0.05 A3 B1");
        Assert.That(reply.Trim(), Is.Empty, "M558 was accepted");

        Probe? probe = await bench.Host.ReadModelAsync(
            model => model.Sensors.Probes.Count > 0 ? model.Sensors.Probes[0] : null);
        Assert.That(probe, Is.Not.Null, "M558 P8 creates sensors.probes[0] (RRF GCodes2.cpp case 558)");
        Assert.Multiple(() =>
        {
            Assert.That(probe!.Type, Is.EqualTo(ProbeType.UnfilteredDigital),
                        "M558 P8 sets sensors.probes[0].type to 8 (RRF ZProbe.cpp OM table, type)");
            Assert.That(probe.Port, Is.EqualTo(Expansion.GpioIn(Machines.DriverBoard, 1)),
                        "M558 C records sensors.probes[0].port (rrf-differences.md section 3)");
            Assert.That(probe.DiveHeights[0], Is.EqualTo(5.0f).Within(1e-3),
                        "M558 H sets sensors.probes[0].diveHeights[0] in mm (RRF ZProbe::Configure)");
            Assert.That(probe.DiveHeights[1], Is.EqualTo(2.0f).Within(1e-3),
                        "M558 H sets sensors.probes[0].diveHeights[1] in mm (RRF ZProbe::Configure)");
            Assert.That(probe.Speeds[0], Is.EqualTo(600.0f).Within(1e-3),
                        "M558 F sets sensors.probes[0].speeds[0] in mm/min (RRF ZProbe.cpp OM table, InverseConvertSpeedToMmPerMin)");
            Assert.That(probe.Speeds[1], Is.EqualTo(300.0f).Within(1e-3),
                        "M558 F sets sensors.probes[0].speeds[1] in mm/min (RRF ZProbe.cpp OM table, InverseConvertSpeedToMmPerMin)");
            Assert.That(probe.TravelSpeed, Is.EqualTo(5000.0f).Within(1e-3),
                        "M558 T sets sensors.probes[0].travelSpeed in mm/min (RRF ZProbe.cpp OM table, travelSpeed)");
            Assert.That(probe.RecoveryTime, Is.EqualTo(0.2f).Within(1e-4),
                        "M558 R sets sensors.probes[0].recoveryTime in seconds (RRF ZProbe::Configure)");
            Assert.That(probe.Tolerance, Is.EqualTo(0.05f).Within(1e-4),
                        "M558 S sets sensors.probes[0].tolerance in mm (RRF ZProbe::Configure)");
            Assert.That(probe.MaxProbeCount, Is.EqualTo(3),
                        "M558 A sets sensors.probes[0].maxProbeCount (RRF ZProbe.cpp OM table, maxTaps)");
            Assert.That(probe.DisablesHeaters, Is.True,
                        "M558 B1 sets sensors.probes[0].disablesHeaters (RRF ZProbe::Configure)");
            Assert.That(probe.DeployedByUser, Is.False,
                        "a new probe is not deployed by the user (RRF ZProbe.cpp OM table, deployedByUser)");
        });

        string report = await bench.Host.ExecuteCodeAsync("M558 K0");
        Assert.That(report, Does.Contain("Z Probe 0: type 8"),
                    "M558 with no parameters reports the probe (RRF ZProbe::Configure report)");
        Assert.That(report, Does.Contain("max taps 3"),
                    "the M558 report includes the tap limit (RRF ZProbe::Configure report)");
    }
}
