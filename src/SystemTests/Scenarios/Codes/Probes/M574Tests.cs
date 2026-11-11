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

/// <summary>M574, configuring an endstop</summary>
[TestFixture]
[GCode("M574")]
public class M574Tests : BenchFixture
{
    /// <summary>
    /// M574 X1 S1 P creates a low-end switch endstop: sensors.endstops[0] reports type, highEnd,
    /// triggered and the port, and M574 with no axes reports the configuration
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 (EndstopsManager.cpp) creates a SwitchEndstop at the
    /// given position; the OM fields are Endstop.cpp's table (highEnd, triggered, type), with type 1
    /// reported as "inputPin". The report wording is SwitchEndstop::AppendDetails. The port itself is
    /// a DSF addition documented in rrf-differences.md section 3 (the object model has to be able to
    /// recreate the machine)
    /// </remarks>
    [Test]
    public async Task M574ConfiguresLowEndSwitchEndstop()
    {
        await using Bench bench = await StartBenchAsync();

        string reply = await bench.Host.ExecuteCodeAsync(XEndstopLine);
        Assert.That(reply.Trim(), Is.Empty, "M574 X1 S1 P was accepted");

        (EndstopType Type, bool HighEnd, bool Triggered, string? Port)? endstop = await bench.Host.ReadModelAsync(
            model => model.Sensors.Endstops.Count > 0 && model.Sensors.Endstops[0] is Endstop e
                ? (e.Type, e.HighEnd, e.Triggered, e.Port)
                : ((EndstopType, bool, bool, string?)?)null);
        Assert.That(endstop, Is.Not.Null, "M574 X1 creates sensors.endstops[0] (RRF EndstopsManager::HandleM574)");
        Assert.Multiple(() =>
        {
            Assert.That(endstop!.Value.Type, Is.EqualTo(EndstopType.InputPin),
                        "M574 S1 sets sensors.endstops[0].type to inputPin (RRF Endstop.cpp OM table)");
            Assert.That(endstop!.Value.HighEnd, Is.False,
                        "M574 X1 sets sensors.endstops[0].highEnd false (RRF Endstop.cpp GetAtHighEnd)");
            Assert.That(endstop!.Value.Triggered, Is.False,
                        "a new endstop starts untriggered (RRF Endstop.cpp Stopped)");
            Assert.That(endstop!.Value.Port, Is.EqualTo(Expansion.GpioIn(Machines.DriverBoard, 0)),
                        "M574 P records sensors.endstops[0].port (rrf-differences.md section 3)");
        });

        string report = await bench.Host.ExecuteCodeAsync("M574");
        Assert.That(report, Does.Contain("Endstop configuration:"),
                    "M574 with no axes reports the configuration (RRF EndstopsManager::HandleM574)");
        Assert.That(report, Does.Contain($"X: low end switch connected to pin {Expansion.GpioIn(Machines.DriverBoard, 0)}"),
                    "the report names the position and pin (RRF SwitchEndstop::AppendDetails)");
    }

    /// <summary>
    /// M574 S5 asks for a stall detected from the encoder position error rather than from StallGuard
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 (EndstopsManager.cpp) creates a StallDetectionEndstop
    /// with useEncoder for EndStopType::motorStallEncoder, which GetEndstopType reports back as that
    /// type. The mechanism travels to the board in CanMessageEnableStallEndstop's endstopType field
    /// (CANlib), and a board too old to know the field reads those bits as zero, which is the
    /// StallGuard it would have used anyway
    /// </remarks>
    [Test]
    public async Task M574ConfiguresEncoderStallEndstop()
    {
        await using Bench bench = await StartBenchAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M574 X1 S5");
        Assert.That(reply.Trim(), Is.Empty, "M574 X1 S5 was accepted");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Type),
                    Is.EqualTo(EndstopType.MotorStallEncoder),
                    "M574 S5 sets sensors.endstops[0].type to motorStallEncoder "
                    + "(RRF StallDetectionEndstop::GetEndstopType)");

        Assert.That(await bench.Host.ExecuteCodeAsync("M574 X1 S6"), Does.Contain("Invalid endstop input type"),
                    "S6 names no mechanism, so it is refused rather than stored");
    }

    /// <summary>
    /// M574 K names the Z probe that stands in for the endstop, and a number out of range is refused
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 reads K with GetLimitedUIValue against MaxZProbes, which
    /// is issue #1275: it used to take any number, and sensors.endstops[].probe would then point at a
    /// probe that does not exist. K is read whether or not S asked for a probe endstop, as RRF does
    /// </remarks>
    [Test]
    public async Task M574RangeChecksTheProbeNumber()
    {
        await using Bench bench = await StartBenchAsync();

        await bench.Host.ExecuteCodeAsync($"M558 P8 C\"{Expansion.GpioIn(Machines.DriverBoard, 3)}\"");
        string reply = await bench.Host.ExecuteCodeAsync("M574 X1 S2 K0");
        Assert.That(reply.Trim(), Is.Empty, "M574 S2 K0 was accepted");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Type),
                        Is.EqualTo(EndstopType.ZProbeAsEndstop), "M574 S2 makes the probe stand in for the endstop");
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Probe), Is.Zero,
                        "M574 K records sensors.endstops[0].probe");
        });

        Assert.That(await bench.Host.ExecuteCodeAsync("M574 X1 S2 K99"),
                    Does.Contain("Z probe number must be between 0 and"),
                    "a K past the last probe is refused (RRF issue #1275, GetLimitedUIValue against MaxZProbes)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Probe), Is.Zero,
                    "and the endstop still names the probe it was given");
    }

    /// <summary>
    /// M574 Y2 makes a high-end endstop and M574 Y0 removes it again, leaving the slot empty
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM574 (EndstopsManager.cpp): position 2 is
    /// EndStopPosition::highEndStop, position 0 deletes the endstop so the OM array reports null for
    /// that axis (EndstopsManager.cpp object model array 1, FindEndstopWhenLockOwned)
    /// </remarks>
    [Test]
    public async Task M574HighEndAndRemovalUpdateTheModel()
    {
        await using Bench bench = await StartBenchAsync();

        await bench.Host.ExecuteCodeAsync($"M574 Y2 S1 P\"{Expansion.GpioIn(Machines.DriverBoard, 2)}\"");
        bool? highEnd = await bench.Host.ReadModelAsync(
            model => model.Sensors.Endstops.Count > 1 ? model.Sensors.Endstops[1]?.HighEnd : null);
        Assert.That(highEnd, Is.True,
                    "M574 Y2 sets sensors.endstops[1].highEnd (RRF EndstopsManager::HandleM574, highEndStop)");

        await bench.Host.ExecuteCodeAsync("M574 Y0");
        bool removed = await bench.Host.ReadModelAsync(
            model => model.Sensors.Endstops.Count > 1 && model.Sensors.Endstops[1] is null);
        Assert.That(removed, Is.True,
                    "M574 Y0 removes the endstop, so sensors.endstops[1] is null (RRF EndstopsManager::HandleM574, noEndStop)");
    }
}
