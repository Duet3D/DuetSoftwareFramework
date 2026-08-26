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
using SystemTests.Host;

namespace SystemTests.Scenarios.Codes;

/// <summary>
/// Endstop, Z probe and homing codes against the object model: M574, M558, G31, M851, M119, M401,
/// M402, M577, G28 (through home macros), G30 and G29 S0. The expected effects are RepRapFirmware's,
/// derived from the reference clone; where rrf-differences.md documents a deliberate deviation the
/// documented DSF behaviour is asserted instead and the section is cited.
/// </summary>
/// <remarks>
/// <para>
/// Endstop and probe inputs are driven by injecting <c>CanMessageInputChangedV2</c> reports, which is
/// how a board reports a switch or probe level; homing and probing moves are closed by injecting a
/// MotionStopped report for the scheduled move, which is how the controller reports a stop it made
/// close to the bus. Both are the same packets real hardware sends.
/// </para>
/// <para>
/// Out of scope here: M574 E (extruder endstops need M950 J general-purpose inputs, which DSF does
/// not have; M577 P is untestable for the same reason and only the axis form is covered), stall
/// endstops (S3/S4, covered by the stall detection plan), G30 with P/S-2, and G29 S1..S3 (aliases of
/// M375/M561/M374).
/// </para>
/// </remarks>
[TestFixture]
public class ProbeEndstopHomingCodeTests : SystemTests.Host.BenchFixture
{
    /// <summary>
    /// X, Y and Z on board 1 (board 0 runs DuetCANMaster and has no drivers), free to move without
    /// homing. M953 comes first: with the bus disabled the configuration's CAN messages would be
    /// answered with BusError, as the real controller answers them
    /// </summary>
    private const string XyzConfig = """
        M953
        M569 P1.0 S1
        M569 P1.1 S1
        M569 P1.2 S1
        M584 X1.0 Y1.1 Z1.2
        M92 X80 Y80 Z400
        M906 X800 Y800 Z800
        M201 X500 Y500 Z100
        M203 X6000 Y6000 Z600
        M566 X900 Y900 Z60
        M208 X0:200 Y0:200 Z0:150
        M564 H0 S0
        """;

    /// <summary>
    /// CAN address of the endstop expansion board
    /// </summary>
    private const byte EndstopBoard = 1;

    /// <summary>A low-end switch endstop for X on board 1, as M574 configures one</summary>
    private readonly string XEndstopLine = $"M574 X1 S1 P\"{EndstopBoard}.io0.in\"";

    /// <summary>An unfiltered digital Z probe on board 1, as M558 configures one</summary>
    private readonly string ProbeLine = $"M558 K0 P8 C\"{EndstopBoard}.io1.in\"";

    /// <summary>
    /// Start a bench from <see cref="XyzConfig"/> plus per-test configuration lines and SD files
    /// </summary>
    private static async Task<JobBench> StartBenchAsync(string configExtra = "", Action<VirtualSd>? prepareSd = null)
    {
        ScriptedCanMaster canMaster = new(Path.Combine(Path.GetTempPath(), $"dsf-fake-{Guid.NewGuid():N}.sock"));
        canMaster.AckCanRequestsWithStandardReplies();
        DcsTestHost host;
        try
        {
            host = await DcsTestHost.StartAsync(canMaster, sd =>
            {
                sd.WriteSys("config.g", XyzConfig + "\n" + configExtra + DcsTestHost.ConfigDoneMarker);
                prepareSd?.Invoke(sd);
            });
        }
        catch
        {
            canMaster.Dispose();
            throw;
        }
        await host.WaitForConfigDoneAsync();
        return new JobBench(canMaster, host);
    }

    /// <summary>
    /// The scheduled moves that watch an input, in order: a homing or probing move carries the stop
    /// input per driver, so a nonzero handle is what tells them apart from travel moves
    /// </summary>
    private static (uint MoveId, (byte Board, byte Driver)[] Drivers)[] ArmedMoves(ScriptedCanMaster canMaster)
        => canMaster.SbcPackets(SbcRequest.ScheduleMove)
                    .Select(p => p.DecodeScheduleMove())
                    .Where(m => m.Drivers.Any(d => d.StopOnHandle != 0))
                    .Select(m => (m.Header.MoveId,
                                  m.Drivers.Where(d => d.StopOnHandle != 0)
                                           .Select(d => (d.BoardAddress, d.DriverNumber))
                                           .ToArray()))
                    .ToArray();

    /// <summary>Wait for the (index + 1)th endstop- or probe-armed move to be scheduled</summary>
    private static async Task<(uint MoveId, (byte Board, byte Driver)[] Drivers)> WaitForArmedMoveAsync(
        ScriptedCanMaster canMaster, int index, string what)
    {
        await canMaster.WaitUntilAsync(() => ArmedMoves(canMaster).Length > index, 20_000, what);
        return ArmedMoves(canMaster)[index];
    }

    /// <summary>Stop an armed move's drivers, as the controller reports a stop it performed</summary>
    private static void StopMove(ScriptedCanMaster canMaster, (uint MoveId, (byte Board, byte Driver)[] Drivers) move)
        => canMaster.InjectMotionStopped(canMaster.Clock.MasterClock, move.MoveId, move.Drivers);

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
        await using JobBench bench = await StartBenchAsync();

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
            Assert.That(endstop!.Value.Port, Is.EqualTo($"{EndstopBoard}.io0.in"),
                        "M574 P records sensors.endstops[0].port (rrf-differences.md section 3)");
        });

        string report = await bench.Host.ExecuteCodeAsync("M574");
        Assert.That(report, Does.Contain("Endstop configuration:"),
                    "M574 with no axes reports the configuration (RRF EndstopsManager::HandleM574)");
        Assert.That(report, Does.Contain($"X: low end switch connected to pin {EndstopBoard}.io0.in"),
                    "the report names the position and pin (RRF SwitchEndstop::AppendDetails)");
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
        await using JobBench bench = await StartBenchAsync();

        await bench.Host.ExecuteCodeAsync($"M574 Y2 S1 P\"{EndstopBoard}.io2.in\"");
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
        await using JobBench bench = await StartBenchAsync(XEndstopLine);

        bench.CanMaster.InjectInputChange(EndstopBoard, RemoteEndstops.HandleFor(0), active: true);
        await bench.CanMaster.WaitUntilAsync(
            () => bench.Host.Model.Sensors.Endstops.Count > 0 && bench.Host.Model.Sensors.Endstops[0]?.Triggered == true,
            what: "the input change reaching sensors.endstops[0].triggered");

        string triggered = await bench.Host.ExecuteCodeAsync("M119");
        Assert.That(triggered.Trim(),
                    Is.EqualTo("Endstops - X: at min stop, Y: no endstop, Z: no endstop, Z probe: not stopped"),
                    "M119 matches sensors.endstops[].triggered (RRF EndstopsManager::GetM119report)");

        bench.CanMaster.InjectInputChange(EndstopBoard, RemoteEndstops.HandleFor(0), active: false);
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
    /// TODO fix M119 to return correct min/max string
    [Test]
    public async Task M119ReportsMaxStopForHighEndEndstop()
    {
        await using JobBench bench = await StartBenchAsync($"M574 X2 S1 P\"{EndstopBoard}.io0.in\"");

        bench.CanMaster.InjectInputChange(EndstopBoard, RemoteEndstops.HandleFor(0), active: true);
        await bench.CanMaster.WaitUntilAsync(
            () => bench.Host.Model.Sensors.Endstops.Count > 0 && bench.Host.Model.Sensors.Endstops[0]?.Triggered == true,
            what: "the input change reaching sensors.endstops[0].triggered");

        string report = await bench.Host.ExecuteCodeAsync("M119");
        Assert.That(report, Does.Contain("X: at max stop"),
                    "M119 reports a triggered high-end endstop as at max stop (RRF TranslateEndStopResult)");
    }

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
    /// TODO fix scenario, likely a units difference between gcode and OM
    [Test]
    public async Task M558ConfiguresProbeInObjectModel()
    {
        await using JobBench bench = await StartBenchAsync();

        string reply = await bench.Host.ExecuteCodeAsync(
            $"M558 K0 P8 C\"{EndstopBoard}.io1.in\" H5:2 F600:300 T5000 R0.2 S0.05 A3 B1");
        Assert.That(reply.Trim(), Is.Empty, "M558 was accepted");

        Probe? probe = await bench.Host.ReadModelAsync(
            model => model.Sensors.Probes.Count > 0 ? model.Sensors.Probes[0] : null);
        Assert.That(probe, Is.Not.Null, "M558 P8 creates sensors.probes[0] (RRF GCodes2.cpp case 558)");
        Assert.Multiple(() =>
        {
            Assert.That(probe!.Type, Is.EqualTo(ProbeType.UnfilteredDigital),
                        "M558 P8 sets sensors.probes[0].type to 8 (RRF ZProbe.cpp OM table, type)");
            Assert.That(probe.Port, Is.EqualTo($"{EndstopBoard}.io1.in"),
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
        await using JobBench bench = await StartBenchAsync(ProbeLine);

        string reply = await bench.Host.ExecuteCodeAsync("G31 K0 P600 X-10 Y5 Z1.25");
        Assert.That(reply.Trim(), Is.Empty, "G31 was accepted");

        Probe? probe = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]);
        Assert.Multiple(() =>
        {
            Assert.That(probe!.Threshold, Is.EqualTo(600),
                        "G31 P sets sensors.probes[0].threshold (RRF ZProbe::HandleG31, targetAdcValue)");
            Assert.That(probe.TriggerHeight, Is.EqualTo(1.25f).Within(1e-4),
                        "G31 Z sets sensors.probes[0].triggerHeight (RRF ZProbe.cpp OM table, -offsets[Z])");
            Assert.That(probe.Offsets[0], Is.EqualTo(-10.0f).Within(1e-4),
                        "G31 X sets sensors.probes[0].offsets[0] (RRF ZProbe::HandleG31)");
            Assert.That(probe.Offsets[1], Is.EqualTo(5.0f).Within(1e-4),
                        "G31 Y sets sensors.probes[0].offsets[1] (RRF ZProbe::HandleG31)");
            Assert.That(probe.Offsets[2], Is.EqualTo(-1.25f).Within(1e-4),
                        "G31 Z stores offsets[Z] as the negated trigger height (RRF ZProbe::HandleG31)");
        });

        string report = await bench.Host.ExecuteCodeAsync("G31 K0");
        Assert.That(report, Does.Contain("threshold 600"),
                    "the G31 report includes the threshold (RRF ZProbe::HandleG31 report)");
        Assert.That(report, Does.Contain("trigger height 1.250"),
                    "the G31 report includes the trigger height (RRF ZProbe::HandleG31 report)");
    }

    /// <summary>
    /// M851 is the Marlin alias: Z sets the trigger height of probe 0 negated, and the report prints
    /// the negated trigger height
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 851 calls SetTriggerHeight(-Z) on probe 0, so M851 Z-0.8 leaves
    /// sensors.probes[0].triggerHeight at 0.8; without Z it reports
    /// "Z probe offset is -GetConfiguredTriggerHeight() mm"
    /// </remarks>
    /// TODO fix scenario, RRF hard sets offsets[2] regardless of if a Z axis is configured.
    [Test]
    public async Task M851SetsTriggerHeightNegated()
    {
        await using JobBench bench = await StartBenchAsync(ProbeLine);

        string reply = await bench.Host.ExecuteCodeAsync("M851 Z-0.8");
        Assert.That(reply.Trim(), Is.Empty, "M851 Z was accepted");

        Probe? probe = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]);
        Assert.Multiple(() =>
        {
            Assert.That(probe!.TriggerHeight, Is.EqualTo(0.8f).Within(1e-4),
                        "M851 Z-0.8 sets sensors.probes[0].triggerHeight to 0.8 (RRF GCodes2.cpp case 851, SetTriggerHeight(-Z))");
            Assert.That(probe.Offsets[2], Is.EqualTo(-0.8f).Within(1e-4),
                        "the Z offset follows the trigger height negated (RRF ZProbe::SetTriggerHeight)");
        });

        string report = await bench.Host.ExecuteCodeAsync("M851");
        Assert.That(report.Trim(), Is.EqualTo("Z probe offset is -0.80mm"),
                    "M851 reports the negated trigger height (RRF GCodes2.cpp case 851)");
    }

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
        await using JobBench bench = await StartBenchAsync(
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

    /// <summary>
    /// G28 X runs homex.g; the G1 H1 move stopped by its endstop homes the axis and sets it to the
    /// endstop's coordinate, which for a low-end endstop is the axis minimum
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes::DoHome (GCodes.cpp) runs the homing macro; the
    /// waitingForSpecialMoveToComplete state (GCodes4.cpp) intersects the axes being homed with
    /// ms.endstopsTriggered, sets each such axis to AxisMinimum or AxisMaximum by its endstop's
    /// position and calls SetAxisIsHomed, so move.axes[0].homed becomes true and machinePosition the
    /// axis minimum. Which axes triggered comes from the latched stop reports, not from re-reading
    /// the switch: rrf-differences.md section 2
    /// </remarks>
    [Test]
    public async Task G28HomesAxisWhenEndstopStopsTheMove()
    {
        await using JobBench bench = await StartBenchAsync(XEndstopLine,
            sd => sd.WriteSys("homex.g", "G91\nG1 H1 X-150 F3000\nG90\n"));

        Task<string> home = bench.Host.ExecuteCodeAsync("G28 X");
        (uint MoveId, (byte Board, byte Driver)[] Drivers) move =
            await WaitForArmedMoveAsync(bench.CanMaster, 0, "the G1 H1 move arriving armed on the endstop");
        Assert.That(move.Drivers, Is.EquivalentTo(new[] { ((byte)1, (byte)0) }),
                    "the homing move arms driver 1.0, which is X");
        StopMove(bench.CanMaster, move);

        string reply = await home;
        Assert.That(reply.Trim(), Is.Empty, "G28 X completed without error");
        bool homed = await bench.Host.ReadModelAsync(model => model.Move.Axes[0].Homed);
        double position = await bench.Host.EvaluateAsync("move.axes[0].machinePosition");
        Assert.Multiple(() =>
        {
            Assert.That(homed, Is.True,
                        "G28 X sets move.axes[0].homed after the endstop stop (RRF GCodes4.cpp SetAxisIsHomed)");
            Assert.That(position, Is.EqualTo(0.0).Within(1e-3),
                        "a low-end endstop homes X to the axis minimum of M208 (RRF GCodes4.cpp AxisMinimum)");
        });
    }

    /// <summary>
    /// A homing macro that only runs G92 still homes the axis, because G92 marks every axis it sets
    /// as homed
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes::SetPositions for G92 (GCodes3.cpp) does
    /// axesHomed |= AxesAssumedHomed(axesIncluded) for the axes given, which for a Cartesian machine
    /// is all of them, so move.axes[1].homed is true and G28 Y succeeds without a homing move
    /// </remarks>
    [Test]
    public async Task G28UsingG92MacroMarksAxisHomed()
    {
        await using JobBench bench = await StartBenchAsync(prepareSd: sd => sd.WriteSys("homey.g", "G92 Y0\n"));

        string reply = await bench.Host.ExecuteCodeAsync("G28 Y");
        Assert.That(reply.Trim(), Is.Empty, "G28 Y completed without error");
        bool homed = await bench.Host.ReadModelAsync(model => model.Move.Axes[1].Homed);
        double position = await bench.Host.EvaluateAsync("move.axes[1].machinePosition");
        Assert.Multiple(() =>
        {
            Assert.That(homed, Is.True,
                        "G92 Y0 marks move.axes[1].homed (RRF GCodes3.cpp SetPositions, axesHomed |= AxesAssumedHomed)");
            Assert.That(position, Is.EqualTo(0.0).Within(1e-3),
                        "G92 Y0 sets move.axes[1].machinePosition to 0 (RRF GCodes3.cpp SetPositions)");
        });
    }

    /// <summary>
    /// Plain G30 probes down, records where the probe stopped, then redefines Z so the nozzle reads
    /// the trigger height, homing Z
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes::ExecuteG30 (GCodes6.cpp) and the probingAtPoint states (GCodes4.cpp):
    /// SetLastStoppedHeight writes sensors.probes[0].lastStopHeight, and for plain G30 the Z
    /// coordinate is reset to the trigger height and SetAxisIsHomed(Z_AXIS) is called. The probing
    /// move is closed by the controller's stop report and the probe judged triggered from its input,
    /// per rrf-differences.md section 2
    /// </remarks>
    /// TODO fix scenario
    [Test]
    public async Task G30SetsZDatumAndHomesZ()
    {
        await using JobBench bench = await StartBenchAsync(ProbeLine + " F300\nG31 K0 Z1.5 P500");

        Task<string> probeTask = bench.Host.ExecuteCodeAsync("G30");
        (uint MoveId, (byte Board, byte Driver)[] Drivers) dive =
            await WaitForArmedMoveAsync(bench.CanMaster, 0, "the probing move arriving armed on the probe");
        Assert.That(dive.Drivers, Is.EquivalentTo(new[] { ((byte)1, (byte)2) }),
                    "the probing move arms driver 1.2, which is Z");

        // The input closes and the controller stops the move: both reports, in that order, so the
        // probe reads triggered when the tap is judged
        bench.CanMaster.InjectInputChange(EndstopBoard, RemoteProbes.HandleFor(0), active: true);
        StopMove(bench.CanMaster, dive);

        string reply = await probeTask;
        Assert.That(reply.Trim(), Is.Empty, "G30 completed without error");
        bool homed = await bench.Host.ReadModelAsync(model => model.Move.Axes[2].Homed);
        double zPosition = await bench.Host.EvaluateAsync("move.axes[2].machinePosition");
        float lastStopHeight = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.LastStopHeight);
        Assert.Multiple(() =>
        {
            Assert.That(homed, Is.True,
                        "plain G30 homes Z (RRF GCodes4.cpp, SetAxisIsHomed(Z_AXIS))");
            Assert.That(zPosition, Is.EqualTo(1.5).Within(0.01),
                        "plain G30 sets Z to the G31 trigger height (RRF GCodes4.cpp, coords[Z] = trigger height)");
            Assert.That(lastStopHeight, Is.GreaterThan(0.0f),
                        "G30 records where the probe stopped in sensors.probes[0].lastStopHeight (RRF SetLastStoppedHeight)");
        });
    }

    /// <summary>
    /// G29 S0 probes the M557 grid, adopts the height map and saves it, reporting the statistics
    /// </summary>
    /// <remarks>
    /// RRF truth: the gridProbing states (GCodes4.cpp): every reachable grid point is probed, the
    /// map becomes the mesh in use (move.compensation.type "mesh" with meshDeviation, Move.cpp OM
    /// table) and TrySaveHeightMap(DefaultHeightMapFile) saves it, so move.compensation.file names
    /// 0:/sys/heightmap.csv. Each tap is closed like G30's: the input closes, the controller stops
    /// the move, and the input is released again once the tap has been judged so the next tap's
    /// pre-check sees a clear probe
    /// </remarks>
    /// TODO fix scenario
    [Test]
    public async Task G29S0ProbesTheGridAndAdoptsTheMap()
    {
        await using JobBench bench = await StartBenchAsync(
            ProbeLine + " F300 T6000 R0.5\nG31 K0 Z0.7 P500\nM557 X20:40 Y20:40 S20");

        // G29 S0 needs a homed machine; G92 homes all three axes at a height above the bed
        await bench.Host.ExecuteCodeAsync("G92 X0 Y0 Z10");

        Task<string> gridTask = bench.Host.ExecuteCodeAsync("G29 S0", timeoutMs: 120_000);
        for (int tap = 0; tap < 4; tap++)
        {
            (uint MoveId, (byte Board, byte Driver)[] Drivers) dive =
                await WaitForArmedMoveAsync(bench.CanMaster, tap, $"probing move {tap + 1} of 4 arriving armed");
            bench.CanMaster.InjectInputChange(EndstopBoard, RemoteProbes.HandleFor(0), active: true);
            int movesBefore = bench.CanMaster.SbcPackets(SbcRequest.ScheduleMove).Count;
            StopMove(bench.CanMaster, dive);

            if (tap < 3)
            {
                // The travel to the next point is only scheduled once this tap has been judged, so
                // seeing it means the probe can be released without failing the tap
                await bench.CanMaster.WaitUntilAsync(
                    () => bench.CanMaster.SbcPackets(SbcRequest.ScheduleMove).Count > movesBefore,
                    20_000, $"the travel move after tap {tap + 1}");
                bench.CanMaster.InjectInputChange(EndstopBoard, RemoteProbes.HandleFor(0), active: false);
            }
        }

        string reply = await gridTask;
        Assert.That(reply, Does.StartWith("4 points probed"),
                    "G29 S0 reports the probed points and statistics (RRF GCodes4.cpp gridProbing report)");

        (MoveCompensationType Type, bool HasDeviation, string? File) compensation = await bench.Host.ReadModelAsync(
            model => (model.Move.Compensation.Type, model.Move.Compensation.MeshDeviation is not null,
                      model.Move.Compensation.File));
        Assert.Multiple(() =>
        {
            Assert.That(compensation.Type, Is.EqualTo(MoveCompensationType.Mesh),
                        "G29 S0 sets move.compensation.type to mesh (RRF Move.cpp OM table, GetCompensationTypeString)");
            Assert.That(compensation.HasDeviation, Is.True,
                        "G29 S0 sets move.compensation.meshDeviation (RRF Move.cpp OM table, meshDeviation)");
            Assert.That(compensation.File, Is.EqualTo("0:/sys/heightmap.csv"),
                        "G29 S0 saves the map, so move.compensation.file names it (RRF GCodes4.cpp TrySaveHeightMap(DefaultHeightMapFile))");
        });
    }

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
        await using JobBench bench = await StartBenchAsync(XEndstopLine);

        Task<string> wait = bench.Host.ExecuteCodeAsync("M577 X");
        await Task.Delay(300);
        Assert.That(wait.IsCompleted, Is.False,
                    "M577 X keeps waiting while the endstop is not triggered (RRF GCodes::WaitForPin)");

        bench.CanMaster.InjectInputChange(EndstopBoard, RemoteEndstops.HandleFor(0), active: true);
        string reply = await wait;
        Assert.That(reply.Trim(), Is.Empty, "M577 X completed once the endstop triggered");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Endstops[0]!.Triggered), Is.True,
                    "the state M577 waited for is sensors.endstops[0].triggered (RRF GCodes::WaitForPin)");
    }
}
