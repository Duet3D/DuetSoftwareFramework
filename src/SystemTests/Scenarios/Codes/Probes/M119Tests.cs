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

/// <summary>M119, the endstop report</summary>
[TestFixture]
[GCode("M119")]
public class M119Tests : BenchFixture
{
    /// <summary>
    /// A reported input change drives sensors.endstops[0].triggered both ways, and M119 renders the
    /// same state
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::GetM119report and TranslateEndStopResult (EndstopsManager.cpp):
    /// "at min stop" for a triggered low-end endstop, "not stopped" otherwise, "no endstop" for an
    /// axis without one, and the current probe appended as "Z probe: ...". DSF keeps the state
    /// current from the boards' change reports instead of fetching it per move, which is the
    /// deviation documented in rrf-differences.md section 2.3; the reported value is the same
    /// </remarks>
    [Test]
    public async Task EndstopTriggerIsLiveInTheModelAndM119()
    {
        await using Bench bench = await StartBenchAsync(XEndstopLine);

        bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteEndstops.HandleFor(0), active: true);
        await bench.CanMaster.WaitUntilAsync(
            () => bench.Host.Model.Sensors.Endstops.Count > 0 && bench.Host.Model.Sensors.Endstops[0]?.Triggered == true,
            what: "the input change reaching sensors.endstops[0].triggered");

        string triggered = await bench.Host.ExecuteCodeAsync("M119");
        Assert.That(triggered.Trim(),
                    Is.EqualTo("Endstops - X: at min stop, Y: no endstop, Z: no endstop, Z probe: not stopped"),
                    "M119 matches sensors.endstops[].triggered (RRF EndstopsManager::GetM119report)");

        bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteEndstops.HandleFor(0), active: false);
        await bench.CanMaster.WaitUntilAsync(
            () => bench.Host.Model.Sensors.Endstops[0]?.Triggered == false,
            what: "the release reaching sensors.endstops[0].triggered");

        string released = await bench.Host.ExecuteCodeAsync("M119");
        Assert.That(released.Trim(),
                    Is.EqualTo("Endstops - X: not stopped, Y: no endstop, Z: no endstop, Z probe: not stopped"),
                    "M119 reports the released endstop as not stopped (RRF TranslateEndStopResult)");
    }

    /// <summary>
    /// A triggered high-end endstop reports "at max stop" in M119
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::TranslateEndStopResult (EndstopsManager.cpp) returns
    /// "at max stop" when the triggered endstop is at the high end and "at min stop" only at the low
    /// end
    /// </remarks>
    [Category("KnownGap")]   // M119 must return the correct min/max string
    [Test]
    public async Task M119ReportsMaxStopForHighEndEndstop()
    {
        await using Bench bench = await StartBenchAsync($"M574 X2 S1 P\"{Expansion.GpioIn(Machines.DriverBoard, 0)}\"");

        bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteEndstops.HandleFor(0), active: true);
        await bench.CanMaster.WaitUntilAsync(
            () => bench.Host.Model.Sensors.Endstops.Count > 0 && bench.Host.Model.Sensors.Endstops[0]?.Triggered == true,
            what: "the input change reaching sensors.endstops[0].triggered");

        string report = await bench.Host.ExecuteCodeAsync("M119");
        Assert.That(report, Does.Contain("X: at max stop"),
                    "M119 reports a triggered high-end endstop as at max stop (RRF TranslateEndStopResult)");
    }
}
