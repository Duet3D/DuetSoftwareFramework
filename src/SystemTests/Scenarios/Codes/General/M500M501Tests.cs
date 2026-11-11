using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace SystemTests.Scenarios.Codes.General;

/// <summary>M500 and M501, saving and restoring config-override.g</summary>
[TestFixture]
[GCode("M500", "M501")]
public class M500M501Tests : BenchFixture
{
    /// <summary>
    /// M500 P31 writes the Z probe values to sys/config-override.g and M501 loads them back: the
    /// probe's trigger height survives the round trip and overwrites a live change.
    /// </summary>
    /// <remarks>
    /// RRF GCodes::WriteConfigOverrideFile calls Platform::WritePlatformParameters, which for P31
    /// writes every probe through ZProbe::WriteParameters (ZProbe.cpp) as
    /// "G31 K%u P%d [axis offsets] Z%.2f". GCodes2.cpp case 501 runs config-override.g as a macro,
    /// so the saved value replaces the live one; the trigger height is
    /// sensors.probes[].triggerHeight
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M500WritesConfigOverrideAndM501RestoresIt()
    {
        await using Bench bench = await Bench.StartAsync($"M558 K0 P8 C\"{Expansion.GpioIn(Machines.DriverBoard, 1)}\"\n");
        await bench.Host.ExecuteCodeAsync("G31 K0 Z1.25");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.TriggerHeight), Is.EqualTo(1.25).Within(1e-3),
                    "G31 K0 Z sets sensors.probes[0].triggerHeight");

        string reply = await bench.Host.ExecuteCodeAsync("M500 P31");
        Assert.That(reply, Does.Not.Contain("Error"), "M500 writes config-override.g without an error (RRF WriteConfigOverrideFile)");

        string overridePath = Path.Combine(bench.Host.Sd.Root, "sys", "config-override.g");
        Assert.That(File.Exists(overridePath), Is.True, "M500 creates sys/config-override.g (RRF WriteConfigOverrideFile)");
        Assert.That(await File.ReadAllTextAsync(overridePath), Does.Match(@"G31 K0 .*Z1\.25"),
                    "M500 P31 persists the probe's trigger height (RRF ZProbe::WriteParameters)");

        await bench.Host.ExecuteCodeAsync("G31 K0 Z3.5");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.TriggerHeight), Is.EqualTo(3.5).Within(1e-3),
                    "the live trigger height moved before M501");

        await bench.Host.ExecuteCodeAsync("M501");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.TriggerHeight), Is.EqualTo(1.25).Within(1e-3),
                    "M501 restores sensors.probes[0].triggerHeight from config-override.g (RRF GCodes2.cpp case 501)");
    }
}
