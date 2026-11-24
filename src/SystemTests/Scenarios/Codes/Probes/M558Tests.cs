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
    /// <summary>The IO connectors of <see cref="Machines.DriverBoard"/> the scenarios put probes on</summary>
    private const int ProbeIo = 1, OtherProbeIo = 2;

    /// <summary>A board other than <see cref="Machines.DriverBoard"/>, for a probe that moves boards</summary>
    private const byte OtherBoard = 2;

    /// <summary>The probe's port as M558 C names it</summary>
    private static string ProbePort(int io = ProbeIo, byte board = Machines.DriverBoard) => Expansion.GpioIn(board, io);

    /// <summary>The probe's port as the board names it, without the board address</summary>
    private static string LocalProbePort(int io = ProbeIo) => $"io{io}.in";

    /// <summary>A probe of the given type on the probe port</summary>
    private static string ProbeOfType(int type, int probe = 0) => $"M558 K{probe} P{type} C\"{ProbePort()}\"";

    /// <summary>The probe a slot holds, or null if there is none</summary>
    private static Probe? ProbeAt(DuetControlServer.Model.ObjectModel model, int probe)
        => probe < model.Sensors.Probes.Count ? model.Sensors.Probes[probe] : null;

    /// <summary>
    /// M558 writes every probe parameter into sensors.probes[0]
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::Configure (ZProbe.cpp) for H, T, B, R, S and A; the type and port are
    /// EndstopsManager::HandleM558 creating the probe. The OM fields are ZProbe.cpp's table:
    /// diveHeights in mm, travelSpeed in mm/min, maxProbeCount from maxTaps, tolerance from S,
    /// recoveryTime from R, disablesHeaters from B. The port is the DSF addition of
    /// rrf-differences.md section 3. The speeds F sets are M558SetsProbingSpeeds'. Regression cases
    /// m558-probe-travel-and-dive, m558-probe-tolerance and m558-report-probe
    /// </remarks>
    [Test]
    public async Task M558ConfiguresProbeInObjectModel()
    {
        await using Bench bench = await StartBenchAsync();

        string reply = await bench.Host.ExecuteCodeAsync(
            $"M558 K0 P8 C\"{ProbePort()}\" H5:2 F600:300 T5000 R0.2 S0.05 A3 B1");
        Assert.That(reply.Trim(), Is.Empty, "M558 was accepted");

        Probe? probe = await bench.Host.ReadModelAsync(model => ProbeAt(model, 0));
        Assert.That(probe, Is.Not.Null, "M558 P8 creates sensors.probes[0] (RRF EndstopsManager::HandleM558)");
        Assert.Multiple(() =>
        {
            Assert.That(probe!.Type, Is.EqualTo(ProbeType.UnfilteredDigital),
                        "M558 P8 sets sensors.probes[0].type to 8 (RRF ZProbe.cpp OM table, type)");
            Assert.That(probe.Port, Is.EqualTo(ProbePort()),
                        "M558 C records sensors.probes[0].port (rrf-differences.md section 3)");
            Assert.That(probe.DiveHeights, Is.EqualTo(new[] { 5.0f, 2.0f }).Within(1e-3f),
                        "M558 H5:2 sets sensors.probes[0].diveHeights in mm (RRF ZProbe::Configure)");
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
        Assert.Multiple(() =>
        {
            Assert.That(report, Does.Contain("Z Probe 0: type 8"),
                        "M558 with no parameters reports the probe (RRF ZProbe::Configure report)");
            Assert.That(report, Does.Contain("dive heights 5.0,2.0mm"), "the report includes the dive heights");
            Assert.That(report, Does.Contain("probe speeds 600,300mm/min"), "the report includes the probing speeds in mm/min");
            Assert.That(report, Does.Contain("travel speed 5000mm/min"), "the report includes the travel speed");
            Assert.That(report, Does.Contain("recovery time 0.20 sec"), "the report includes the recovery time");
            Assert.That(report, Does.Contain("heaters suspended"), "the report says B1 suspends the heaters");
            Assert.That(report, Does.Contain("max taps 3, max diff 0.05"), "the report includes the tap limit and tolerance");
        });
    }

    /// <summary>
    /// A probe created with nothing but its type and port has RepRapFirmware's defaults for every
    /// setting M558 and G31 configure
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::SetDefaults (ZProbe.cpp) with Configuration.h's DefaultZDive 5,
    /// DefaultZProbeTravelSpeed 100 mm/s (reported as 6000 mm/min), DefaultZProbeADValue 500,
    /// DefaultZProbeTolerance 0.03, DefaultZProbeTaps 1 and DefaultZProbeTriggerHeight 0.7; recovery
    /// time 0, heaters normal, temperature coefficients zero. isCalibrated, scanCoefficients and
    /// touchMode are only reported for a scanning probe. Regression case m558-probe-type-switch
    /// (travelSpeed there is the T3000 the case gives) and the error-missing-parameters-probing-mcodes
    /// setup, which creates a probe with no settings and reports travelSpeed 6000
    /// </remarks>
    [Test]
    public async Task M558CreatesProbeWithDefaultSettings()
    {
        await using Bench bench = await StartBenchAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync(ProbeOfType(8))).Trim(), Is.Empty, "M558 P8 C was accepted");

        Probe probe = (await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)))!;
        Assert.Multiple(() =>
        {
            Assert.That(probe.DiveHeights, Is.EqualTo(new[] { 5.0f, 5.0f }).Within(1e-4f), "diveHeights default to DefaultZDive");
            Assert.That(probe.TravelSpeed, Is.EqualTo(6000.0f).Within(1e-3f), "travelSpeed defaults to 6000 mm/min");
            Assert.That(probe.Threshold, Is.EqualTo(500), "threshold defaults to DefaultZProbeADValue");
            Assert.That(probe.Tolerance, Is.EqualTo(0.03f).Within(1e-5f), "tolerance defaults to DefaultZProbeTolerance");
            Assert.That(probe.MaxProbeCount, Is.EqualTo(1), "maxProbeCount defaults to DefaultZProbeTaps");
            Assert.That(probe.RecoveryTime, Is.Zero, "recoveryTime defaults to zero");
            Assert.That(probe.DisablesHeaters, Is.False, "disablesHeaters defaults to false");
            Assert.That(probe.TriggerHeight, Is.EqualTo(0.7f).Within(1e-5f), "triggerHeight defaults to DefaultZProbeTriggerHeight");
            Assert.That(probe.TemperatureCoefficients, Is.EqualTo(new[] { 0.0f, 0.0f }), "temperatureCoefficients default to zero");
            Assert.That(probe.LastStopHeight, Is.Zero, "lastStopHeight starts at zero");
            Assert.That(probe.IsCalibrated, Is.Null, "isCalibrated is only reported for a scanning probe");
            Assert.That(probe.ScanCoefficients, Is.Null, "scanCoefficients are only reported for a scanning probe");
            Assert.That(probe.TouchMode, Is.Null, "touchMode is only reported for a scanning probe");
        });
    }

    /// <summary>
    /// A new probe has one offset per axis, with Z's at the negated default trigger height
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe.cpp OM array 0 counts GetVisibleAxes() and ZProbe::SetDefaults sets
    /// offsets[Z_AXIS] = -DefaultZProbeTriggerHeight, so a fresh probe on X, Y and Z reports
    /// offsets [0, 0, -0.7]. Regression cases m558-probe-type-switch and m558-probe-feedrates
    /// (offsets[2] -0.7); DSF only grows the list to the axes on the first G31
    /// </remarks>
    [Category("KnownGap")]   // DSF's new probe has two offsets, X and Y, until G31 grows the list
    [Test]
    public async Task M558NewProbeHasOneOffsetPerAxis()
    {
        await using Bench bench = await StartBenchAsync();

        await bench.Host.ExecuteCodeAsync(ProbeOfType(8));

        float[] offsets = await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)!.Offsets.ToArray());
        Assert.That(offsets, Is.EqualTo(new[] { 0.0f, 0.0f, -0.7f }).Within(1e-5f),
                    "sensors.probes[0].offsets has one entry per axis, Z's being -triggerHeight (RRF ZProbe::SetDefaults, OM array 0)");
    }

    /// <summary>
    /// A new probe's temperature compensation is calibrated at 25C
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::SetDefaults sets calibTemperature = DefaultZProbeTemperature (25.0,
    /// Configuration.h). Regression cases m558-probe-type-switch and m558-probe-feedrates report
    /// calibrationTemperature 25.0; g31-probe-temperature-compensation fails on DSF only because S25
    /// changes DSF's 0 where RRF's 25 is unchanged
    /// </remarks>
    [Category("KnownGap")]   // DSF's Probe.CalibrationTemperature defaults to 0
    [Test]
    public async Task M558NewProbeIsCalibratedAt25C()
    {
        await using Bench bench = await StartBenchAsync();

        await bench.Host.ExecuteCodeAsync(ProbeOfType(8));

        Assert.That(await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)!.CalibrationTemperature),
                    Is.EqualTo(25.0f).Within(1e-4f),
                    "sensors.probes[0].calibrationTemperature defaults to DefaultZProbeTemperature (RRF ZProbe::SetDefaults)");
    }

    /// <summary>The F parameters of <see cref="M558SetsProbingSpeeds"/> and the speeds RRF reports for each</summary>
    private static readonly object[] SpeedCases =
    [
        // No F: DefaultProbingSpeed is 2 mm/s, reported as 120 mm/min
        new object[] { 8, "", new[] { 120.0f, 120.0f } },
        new object[] { 11, "", new[] { 120.0f, 120.0f, 120.0f } },
        // One value is padded into every speed
        new object[] { 8, " F600", new[] { 600.0f, 600.0f } },
        new object[] { 11, " F600", new[] { 600.0f, 600.0f, 600.0f } },
        // Two values: a scanning probe's third speed is the first
        new object[] { 8, " F1200:200", new[] { 1200.0f, 200.0f } },
        new object[] { 11, " F1200:200", new[] { 1200.0f, 200.0f, 1200.0f } },
        // Three values: only a scanning probe reports the third
        new object[] { 8, " F1000:200:600", new[] { 1000.0f, 200.0f } },
        new object[] { 11, " F1000:200:600", new[] { 1000.0f, 200.0f, 600.0f } },
    ];

    /// <summary>
    /// M558 F sets the probing speeds, reported in mm/min, with one value padded into the rest and a
    /// third speed reported only for a scanning probe
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::Configure reads F with GetFloatArray(doPad = true) into three speeds, the
    /// third being the first when two are given; ZProbe.cpp OM array 1 counts 3 for scanningAnalog
    /// and 2 otherwise and reports InverseConvertSpeedToMmPerMin. DSF's OM convention is mm/min for
    /// speeds too (move.axes[].speed, sensors.probes[].travelSpeed), which DuetAPI's Probe.Speeds
    /// documentation contradicts. Regression cases m558-probe-type-switch (F120 -> 120, 120),
    /// m558-probe-feedrates (F1200:200), m558-probe-type-bltouch (F500:120) and
    /// m558-probe-type-scanning (F1000:200:600)
    /// </remarks>
    [Category("KnownGap")]   // DSF stores the speeds in mm/s, and sizes the list by the F values rather than the type
    [TestCaseSource(nameof(SpeedCases))]
    public async Task M558SetsProbingSpeeds(int type, string f, float[] speeds)
    {
        await using Bench bench = await Bench.With(Machines.Xyz).WithController(c => AnswerReadInputs(c, 0)).StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync(ProbeOfType(type) + f)).Trim(), Is.Empty, "M558 was accepted");
        Assert.That(await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)!.Speeds.ToArray()),
                    Is.EqualTo(speeds).Within(1e-3f),
                    "sensors.probes[0].speeds in mm/min, three for a scanning probe and two otherwise (RRF ZProbe::Configure, OM array 1)");
    }

    /// <summary>
    /// An H with more than two dive heights or an F with more than three speeds is refused, and the
    /// probe keeps what it had
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::Configure reads H into two values and F into three with GetFloatArray,
    /// whose StringParser::CheckArrayLength throws on one value too many
    /// </remarks>
    [TestCase("H1:2:3")]
    [TestCase("F100:200:300:400")]
    public async Task M558RefusesTooManyValues(string parameter)
    {
        await using Bench bench = await StartBenchAsync(ProbeLine + " H3 F300");

        Assert.That(await bench.Host.ExecuteCodeAsync($"M558 K0 {parameter}"), Does.StartWith("Error:"),
                    "one value too many is an error (RRF StringParser::CheckArrayLength)");

        Probe probe = (await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)))!;
        string report = await bench.Host.ExecuteCodeAsync("M558 K0");
        Assert.Multiple(() =>
        {
            Assert.That(probe.DiveHeights, Is.EqualTo(new[] { 3.0f, 3.0f }).Within(1e-4f), "the dive heights are unchanged");
            Assert.That(report, Does.Contain("probe speeds 300,300mm/min"), "the probing speeds are unchanged");
        });
    }

    /// <summary>
    /// A probe on an expansion board is an input monitor that board is asked to keep, under the
    /// probe's handle, reading the pin digitally for a switch and as analog for an analog probe
    /// </summary>
    /// <remarks>
    /// RRF truth: RemoteZProbe::Create (RemoteZProbe.cpp) sends CanMessageCreateInputMonitorV1 with
    /// handle typeZprobe/probe number/0, the pin without its board address, and a threshold of
    /// DefaultZProbeADValue for types 1, 11 and 12 and 0 otherwise, a nonzero threshold being what
    /// makes the handle analog; the reply's extra byte is the input's state, which a digital probe
    /// reports as reading 1000 (RemoteZProbe::GetRawReading). RRF creates at
    /// ActiveProbeReportInterval (2 ms); DSF creates at the inactive 25 ms, the deliberate difference
    /// of rrf-differences.md section 2.4. Regression cases m558-probe-type-switch,
    /// m558-probe-type-bltouch, m558-probe-type-unmodulated and m558-probe-type-scanning
    /// </remarks>
    [TestCase(8, 0)]
    [TestCase(9, 0)]
    [TestCase(1, 500)]
    [TestCase(11, 500)]
    public async Task M558CreatesInputMonitorOnTheBoard(int type, int threshold)
    {
        await using Bench bench = await Bench.With(Machines.Xyz)
                                             .WithController(c =>
                                             {
                                                 c.OnCanMessage((ushort)CanMessageType.CreateInputMonitorV1,
                                                                (fake, header, _) => fake.InjectStandardReply(header, extra: 1));
                                                 AnswerReadInputs(c, 0);
                                             })
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync(ProbeOfType(type));
        Assert.That(reply.Trim(), Is.Empty, $"M558 P{type} was accepted");

        var creates = bench.CanMaster.CanMessages<CanMessageCreateInputMonitorV1>();
        Assert.That(creates, Has.Count.EqualTo(1), "one input monitor is created (RRF RemoteZProbe::Create)");
        (byte board, CanMessageCreateInputMonitorV1 sent) = creates[0];
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "the monitor is created on the board the port names");
            Assert.That(sent.Handle.Type, Is.EqualTo(RemoteInputHandle.TypeZprobe), "under a Z probe handle");
            Assert.That(sent.Handle.Major, Is.Zero, "whose major is the probe number");
            Assert.That(sent.Handle.Minor, Is.Zero, "and whose minor is zero");
            Assert.That(sent.PinNameString, Is.EqualTo(LocalProbePort()), "naming the pin without the board address");
            Assert.That(sent.Threshold, Is.EqualTo(threshold),
                        "with DefaultZProbeADValue for an analog probe and zero for a switch (RRF RemoteZProbe::Create)");
            Assert.That(sent.MinInterval, Is.EqualTo(ProbeArming.InactiveReportInterval),
                        "at the idle report interval (rrf-differences.md section 2.4)");
        });

        if (threshold == 0)
        {
            Assert.That(await bench.Host.WaitForModelAsync(model => ProbeAt(model, 0)!.Value.ToArray(),
                                                           value => value.Length > 0, "the probe's reading"),
                        Is.EqualTo(new[] { RemoteProbes.MaxReading }),
                        "a digital probe the board reports active reads 1000 (RRF RemoteZProbe::GetRawReading)");
        }
    }

    /// <summary>
    /// A scanning probe takes a reading from its board as soon as it is created, and reports the
    /// scanning probe fields as uncalibrated
    /// </summary>
    /// <remarks>
    /// RRF truth: RemoteZProbe::Create calls GetCalibratedReading for a scanningAnalog probe, which
    /// sends CanMessageReadInputsRequest with mask and pattern both the probe's handle
    /// (CanInterface::ReadRemoteHandles). ZProbe.cpp's OM table reports isCalibrated, scanCoefficients
    /// (4) and touchMode for IsScanning(); touchMode defaults to inactive, 200 mm/min, threshold 0.5,
    /// trigger height -0.1 (ZProbe.h). Regression case m558-probe-type-scanning
    /// </remarks>
    [Category("KnownGap")]   // DSF sends no read request and leaves the scanning fields null
    [Test]
    public async Task M558ScanningProbeTakesAnInitialReading()
    {
        await using Bench bench = await Bench.With(Machines.Xyz).WithController(c => AnswerReadInputs(c, 0)).StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync(ProbeOfType(11) + " F1000:200:600")).Trim(), Is.Empty,
                    "M558 P11 was accepted");

        var reads = bench.CanMaster.CanMessages<CanMessageReadInputsRequest>();
        Assert.That(reads, Has.Count.EqualTo(1), "the board is asked for a reading (RRF RemoteZProbe::GetCalibratedReading)");
        Assert.Multiple(() =>
        {
            Assert.That(reads[0].Board, Is.EqualTo(Machines.DriverBoard), "of the board carrying the probe");
            Assert.That(reads[0].Message.Mask.All, Is.EqualTo(RemoteProbes.HandleFor(0).All), "masked to the probe's handle");
            Assert.That(reads[0].Message.Pattern.All, Is.EqualTo(RemoteProbes.HandleFor(0).All), "matching the probe's handle");
        });

        Probe probe = (await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)))!;
        Assert.Multiple(() =>
        {
            Assert.That(probe.IsCalibrated, Is.False, "a new scanning probe is not calibrated");
            Assert.That(probe.ScanCoefficients, Has.Count.EqualTo(4), "a scanning probe reports its four coefficients");
            Assert.That(probe.MeasuredHeight, Is.Null, "an uncalibrated probe has no measured height");
            Assert.That(probe.TouchMode, Is.Not.Null, "a scanning probe reports its touch mode");
            Assert.That(probe.TouchMode?.Active, Is.False, "touch mode starts inactive");
            Assert.That(probe.TouchMode?.Speed, Is.EqualTo(200.0f).Within(1e-3f), "touch mode speed in mm/min");
            Assert.That(probe.TouchMode?.Threshold, Is.EqualTo(0.5f).Within(1e-4f), "touch mode threshold");
            Assert.That(probe.TouchMode?.TriggerHeight, Is.EqualTo(-0.1f).Within(1e-4f), "touch mode trigger height");
        });
    }

    /// <summary>
    /// K selects which probe M558 configures: a second probe is created under its own handle and
    /// leaves the first as it was
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM558 reads K with GetLimitedUIValue against MaxZProbes and
    /// RemoteZProbe::Create builds the handle from the probe number. Regression case
    /// m558-probe-second-probe (handles 12288 and 12352)
    /// </remarks>
    [Test]
    public async Task M558SecondProbeUsesItsOwnHandle()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine + " H5 F600 T3000");

        string reply = await bench.Host.ExecuteCodeAsync($"M558 K1 P8 C\"{ProbePort(OtherProbeIo)}\" H3 F400 T2000");
        Assert.That(reply.Trim(), Is.Empty, "M558 K1 was accepted");

        (byte board, CanMessageCreateInputMonitorV1 sent) = bench.CanMaster.LastCanMessage<CanMessageCreateInputMonitorV1>();
        Assert.Multiple(() =>
        {
            Assert.That(board, Is.EqualTo(Machines.DriverBoard), "probe 1's monitor is created on its board");
            Assert.That(sent.Handle.Type, Is.EqualTo(RemoteInputHandle.TypeZprobe), "under a Z probe handle");
            Assert.That(sent.Handle.Major, Is.EqualTo(1), "whose major is probe 1");
            Assert.That(sent.PinNameString, Is.EqualTo(LocalProbePort(OtherProbeIo)), "watching probe 1's pin");
            Assert.That(bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>(), Is.Empty,
                        "probe 0's monitor is left alone");
        });

        (Probe first, Probe second) = await bench.Host.ReadModelAsync(model => (ProbeAt(model, 0)!, ProbeAt(model, 1)!));
        Assert.Multiple(() =>
        {
            Assert.That(second.DiveHeights[0], Is.EqualTo(3.0f).Within(1e-4f), "probe 1 has its own dive height");
            Assert.That(second.TravelSpeed, Is.EqualTo(2000.0f).Within(1e-3f), "and its own travel speed");
            Assert.That(second.Port, Is.EqualTo(ProbePort(OtherProbeIo)), "and its own port");
            Assert.That(first.DiveHeights[0], Is.EqualTo(5.0f).Within(1e-4f), "probe 0 keeps its dive height");
            Assert.That(first.TravelSpeed, Is.EqualTo(3000.0f).Within(1e-3f), "and its travel speed");
            Assert.That(first.Port, Is.EqualTo(ProbePort()), "and its port");
        });

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ExecuteCodeAsync("M558 K1"),
                        Does.Contain("Z Probe 1: type 8").And.Contain("dive heights 3.0,3.0mm").And.Contain("travel speed 2000mm/min"),
                        "M558 K1 reports probe 1");
            Assert.That(await bench.Host.ExecuteCodeAsync("M558 K0"),
                        Does.Contain("Z Probe 0: type 8").And.Contain("dive heights 5.0,5.0mm").And.Contain("travel speed 3000mm/min"),
                        "M558 K0 reports probe 0");
        });
    }

    /// <summary>
    /// The M558 report asks the probe's board to name the pin it is watching, and quotes the board
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::Configure's report calls AppendPinNames, which RemoteZProbe implements with
    /// CanInterface::GetHandlePinName: CanMessageChangeInputMonitorV1 action ReturnPinName, param 0,
    /// to the probe's handle, appending ", input pin " and the board's text (the pin and its minimum
    /// interval, "3.io1.in, min interval 2ms" on the rig). Regression cases m558-report-probe and
    /// m558-probe-second-probe
    /// </remarks>
    [Category("KnownGap")]   // DSF reports the port from the object model and asks the board nothing
    [Test]
    public async Task M558ReportQuotesTheBoardsPinDescription()
    {
        string description = $"{ProbePort()}, min interval {ProbeArming.InactiveReportInterval}ms";
        await using Bench bench = await Bench.With(Machines.Xyz, ProbeLine)
                                             .WithController(c => c.ReportWith<CanMessageChangeInputMonitorV1>(
                                                 m => m.Action == CanMessageChangeInputMonitorV1.ActionReturnPinName ? description : ""))
                                             .StartAsync();

        string report = await bench.Host.ExecuteCodeAsync("M558 K0");

        var asked = bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>();
        Assert.That(asked, Has.Count.EqualTo(1), "the board is asked to name the pin (RRF RemoteZProbe::AppendPinNames)");
        Assert.Multiple(() =>
        {
            Assert.That(asked[0].Board, Is.EqualTo(Machines.DriverBoard), "the board carrying the probe");
            Assert.That(asked[0].Message.Action, Is.EqualTo(CanMessageChangeInputMonitorV1.ActionReturnPinName), "action ReturnPinName");
            Assert.That(asked[0].Message.Param, Is.Zero, "param 0");
            Assert.That(asked[0].Message.Handle.All, Is.EqualTo(RemoteProbes.HandleFor(0).All), "the probe's handle");
            Assert.That(report, Does.Contain($", input pin {description}"), "the report quotes what the board said");
        });
    }

    /// <summary>
    /// A board that cannot name the probe's pin turns the M558 report into its error
    /// </summary>
    /// <remarks>
    /// RRF truth: RemoteZProbe::AppendPinNames copies the board's reply over the report when
    /// GetHandlePinName fails, and ZProbe::Configure returns that result
    /// </remarks>
    [Category("KnownGap")]   // DSF asks the board nothing, so it cannot see the board has lost the monitor
    [Test]
    public async Task M558ReportFailsWhenTheBoardCannotNameThePin()
    {
        const string refusal = "Unknown handle";
        await using Bench bench = await Bench.With(Machines.Xyz, ProbeLine)
                                             .WithController(c => c.ReportWith<CanMessageChangeInputMonitorV1>(refusal, CodeResult.Error))
                                             .StartAsync();

        string report = await bench.Host.ExecuteCodeAsync("M558 K0");
        Assert.That(report, Does.StartWith("Error:").And.Contain(refusal),
                    "the board's refusal is the reply (RRF RemoteZProbe::AppendPinNames)");
    }

    /// <summary>
    /// M558 for a probe that does not exist without a type, or with a port and no type, is refused and
    /// leaves the probes as they were
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM558 checks "Z probe %u not found" and "Missing Z probe type
    /// number" before deleting anything. Regression case error-missing-parameters-probing-mcodes
    /// (M558 K0 C without P)
    /// </remarks>
    [Test]
    public async Task M558WithoutATypeIsRefused()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine + " H3");

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ExecuteCodeAsync("M558 K1 H5"), Does.StartWith("Error:").And.Contain("not found"),
                        "configuring probe 1, which does not exist, is refused (RRF \"Z probe %u not found\")");
            Assert.That(await bench.Host.ExecuteCodeAsync($"M558 K0 C\"{ProbePort(OtherProbeIo)}\""),
                        Does.StartWith("Error:").And.Contain("Missing Z probe type"),
                        "a port without a type is refused (RRF \"Missing Z probe type number\")");
        });

        (Probe? first, Probe? second) = await bench.Host.ReadModelAsync(model => (ProbeAt(model, 0), ProbeAt(model, 1)));
        Assert.Multiple(() =>
        {
            Assert.That(second, Is.Null, "no probe 1 is created");
            Assert.That(first?.Port, Is.EqualTo(ProbePort()), "probe 0 keeps its port");
            Assert.That(first?.DiveHeights[0], Is.EqualTo(3.0f).Within(1e-4f), "and its settings");
            Assert.That(bench.CanMaster.CanMessages<CanMessageCreateInputMonitorV1>(), Is.Empty, "nothing is created on a board");
            Assert.That(bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>(), Is.Empty, "and nothing is deleted");
        });
    }

    /// <summary>
    /// A type that needs a port, given without one, is refused, and the probe it was meant to replace
    /// is gone: M558 P deletes the old probe before it creates the new one
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM558 calls DeleteObject on the existing probe as soon as P
    /// (or C) is seen, and only then refuses "Missing Z probe pin name(s)"; the deleted RemoteZProbe's
    /// destructor deletes its handle on its board. The wiki's M558 page says M558 with P deletes the
    /// existing probe. Regression case error-missing-parameters-probing-mcodes, whose last M558 K0 P8
    /// leaves sensors.probes empty
    /// </remarks>
    [Category("KnownGap")]   // DSF reuses the existing port, or refuses before touching the probe when it has none
    [TestCase(0)]
    [TestCase(8)]
    public async Task M558TypeWithoutAPortIsRefusedAndDeletesTheProbe(int existingType)
    {
        await using Bench bench = await StartBenchAsync(existingType == 0 ? "M558 K0 P0" : ProbeLine);

        Assert.That(await bench.Host.ExecuteCodeAsync("M558 K0 P9"), Does.StartWith("Error:").And.Contain("pin name"),
                    "a type that needs a port is refused without one (RRF \"Missing Z probe pin name(s)\")");
        Assert.That(await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)), Is.Null,
                    "the probe the code would have replaced is deleted (RRF EndstopsManager::HandleM558, DeleteObject)");
        if (existingType != 0)
        {
            Assert.That(bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>()
                                       .Where(m => m.Message.Action == CanMessageChangeInputMonitorV1.ActionDelete)
                                       .Select(m => (m.Board, m.Message.Handle.All)),
                        Is.EqualTo(new[] { (Machines.DriverBoard, RemoteProbes.HandleFor(0).All) }),
                        "and its monitor is deleted on its board (RRF ~RemoteZProbe)");
        }
    }

    /// <summary>
    /// Types RepRapFirmware 3 no longer has, and numbers past the last type, are refused before the
    /// existing probe is touched
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM558 reads P with TryGetLimitedUIValue against
    /// ZProbeType::numTypes (13) and refuses the obsolete types 3, 4, 6 and 7 with "Invalid Z probe
    /// type", both before DeleteObject
    /// </remarks>
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(13)]
    public async Task M558RefusesInvalidTypesBeforeTouchingTheProbe(int type)
    {
        await using Bench bench = await StartBenchAsync(ProbeLine + " H3");

        Assert.That(await bench.Host.ExecuteCodeAsync($"M558 K0 P{type} C\"{ProbePort(OtherProbeIo)}\""), Does.StartWith("Error:"),
                    $"type {type} is refused (RRF EndstopsManager::HandleM558)");

        Probe? probe = await bench.Host.ReadModelAsync(model => ProbeAt(model, 0));
        Assert.Multiple(() =>
        {
            Assert.That(probe?.Type, Is.EqualTo(ProbeType.UnfilteredDigital), "probe 0 keeps its type");
            Assert.That(probe?.Port, Is.EqualTo(ProbePort()), "and its port");
            Assert.That(probe?.DiveHeights[0], Is.EqualTo(3.0f).Within(1e-4f), "and its settings");
            Assert.That(bench.CanMaster.CanMessages<CanMessageCreateInputMonitorV1>(), Is.Empty, "nothing is created on a board");
            Assert.That(bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>(), Is.Empty, "and nothing is deleted");
        });
    }

    /// <summary>The M558 lines <see cref="M558ExpansionBoardRefusalsDeleteTheProbe"/> refuses</summary>
    private static readonly string[] ExpansionBoardRefusals =
    [
        // Types an input monitor cannot express
        $"M558 K0 P2 C\"{ProbePort(OtherProbeIo)}\"",
        $"M558 K0 P5 C\"{ProbePort(OtherProbeIo)}\"",
        // A modulation output, which only a main board probe has
        $"M558 K0 P8 C\"{ProbePort(OtherProbeIo)}+{Expansion.GpioOut(Machines.DriverBoard, OtherProbeIo)}\"",
    ];

    /// <summary>
    /// What an expansion board probe cannot be - types 2 and 5, or a port with a modulation output -
    /// is refused, after the probe it would have replaced is deleted
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM558 deletes the existing probe and then
    /// RemoteZProbe::Create refuses with "only Z probe types 1, 8, 9, 11 and 12 are supported on
    /// expansion boards" or "expansion boards do not support Z probe output ports"
    /// </remarks>
    [Category("KnownGap")]   // DSF refuses before touching the probe, so the old one survives
    [TestCaseSource(nameof(ExpansionBoardRefusals))]
    public async Task M558ExpansionBoardRefusalsDeleteTheProbe(string line)
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);

        Assert.That(await bench.Host.ExecuteCodeAsync(line), Does.StartWith("Error:"),
                    "the line is refused (RRF RemoteZProbe::Create)");
        Assert.Multiple(async () =>
        {
            Assert.That(bench.CanMaster.CanMessages<CanMessageCreateInputMonitorV1>(), Is.Empty, "nothing is created on a board");
            Assert.That(await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)), Is.Null,
                        "the probe it would have replaced is deleted (RRF EndstopsManager::HandleM558, DeleteObject)");
        });
    }

    /// <summary>
    /// A board that refuses to watch the probe's pin fails M558 with its reason, and leaves no probe
    /// behind, whether the probe is new or replacing one
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM558 deletes the existing probe, and when
    /// RemoteZProbe::Create returns anything but ok it deletes the new one too and returns the
    /// board's reply, so sensors.probes[0] is null. A probe left in the object model naming a pin no
    /// board is watching would read as configured and never trigger
    /// </remarks>
    [Category("KnownGap")]   // DSF writes the probe into the model first and keeps it when the board refuses
    [TestCase(false)]
    [TestCase(true)]
    public async Task M558BoardRefusalLeavesNoProbe(bool replacing)
    {
        const string refusal = "Pin io2.in is not free";
        await using Bench bench = await Bench.With(Machines.Xyz)
                                             .WithController(c => c.OnCanMessage((ushort)CanMessageType.CreateInputMonitorV1,
                                                 (fake, header, payload) =>
                                                 {
                                                     bool refused = CanMessageSerializer.Deserialize<CanMessageCreateInputMonitorV1>(payload)
                                                                                        .PinNameString == LocalProbePort(OtherProbeIo);
                                                     fake.InjectStandardReply(header, refused ? CodeResult.Error : CodeResult.Ok,
                                                                              refused ? refusal : "");
                                                 }))
                                             .StartAsync();
        if (replacing)
        {
            Assert.That((await bench.Host.ExecuteCodeAsync(ProbeLine)).Trim(), Is.Empty, "the first probe is accepted");
        }

        string reply = await bench.Host.ExecuteCodeAsync($"M558 K0 P8 C\"{ProbePort(OtherProbeIo)}\"");
        Assert.That(reply, Does.StartWith("Error:").And.Contain(refusal), "the board's refusal is the reply");
        Assert.That(await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)), Is.Null,
                    "no probe is left in sensors.probes[0] (RRF EndstopsManager::HandleM558)");
    }

    /// <summary>
    /// A board that watches the pin but has something to say about it passes its warning on, and the
    /// probe is kept
    /// </summary>
    /// <remarks>
    /// RRF truth is inconsistent here: RemoteZProbe::Create keeps the handle for any result below
    /// error ("don't set the handle unless it is valid"), but EndstopsManager::HandleM558 deletes the
    /// new probe for any result other than ok, warnings included, which then deletes the handle Create
    /// kept. Every other remote input DSF creates passes a board's warning on and keeps the device
    /// (MCodeHandler.Probes.cs, CreateProbeMonitorAsync), which is what this pins
    /// </remarks>
    [Test]
    public async Task M558PassesOnABoardWarningAndKeepsTheProbe()
    {
        const string warning = "Pin io1.in has no pull-up";
        await using Bench bench = await Bench.With(Machines.Xyz)
                                             .WithController(c => c.ReportWith<CanMessageCreateInputMonitorV1>(warning, CodeResult.Warning))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync(ProbeLine);
        Assert.That(reply, Does.StartWith("Warning:").And.Contain(warning), "the board's warning is the reply");
        Probe? probe = await bench.Host.ReadModelAsync(model => ProbeAt(model, 0));
        Assert.Multiple(() =>
        {
            Assert.That(probe?.Type, Is.EqualTo(ProbeType.UnfilteredDigital), "the probe is created");
            Assert.That(probe?.Port, Is.EqualTo(ProbePort()), "on the port the board took");
            Assert.That(bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>(), Is.Empty, "and its monitor is kept");
        });
    }

    /// <summary>
    /// Redefining a probe with M558 P replaces it with a new one, so every setting M558 and G31 made
    /// goes back to its default
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM558 deletes the existing probe and constructs a new one,
    /// whose constructor runs ZProbe::SetDefaults. The wiki's M558 page: "M558 with P parameter
    /// deletes the existing probe with that K number (if any) and creates a new probe. This resets
    /// the G31 values for that probe to default values". Regression case g31-set-probe-trigger notes
    /// the same ordering requirement
    /// </remarks>
    [Category("KnownGap")]   // DSF changes the type and port of the existing probe and keeps its other settings
    [Test]
    public async Task M558RedefinitionResetsTheProbe()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine + " H3:2 T2000 R0.5 S0.1 A4 B1\nG31 K0 P600 X5 Y-5 Z2");

        Assert.That((await bench.Host.ExecuteCodeAsync($"M558 K0 P8 C\"{ProbePort(OtherProbeIo)}\"")).Trim(), Is.Empty,
                    "the redefinition is accepted");

        Probe probe = (await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)))!;
        Assert.Multiple(() =>
        {
            Assert.That(probe.Port, Is.EqualTo(ProbePort(OtherProbeIo)), "the new probe is on the new port");
            Assert.That(probe.DiveHeights, Is.EqualTo(new[] { 5.0f, 5.0f }).Within(1e-4f), "diveHeights are back to the default");
            Assert.That(probe.TravelSpeed, Is.EqualTo(6000.0f).Within(1e-3f), "travelSpeed is back to the default");
            Assert.That(probe.RecoveryTime, Is.Zero, "recoveryTime is back to the default");
            Assert.That(probe.Tolerance, Is.EqualTo(0.03f).Within(1e-5f), "tolerance is back to the default");
            Assert.That(probe.MaxProbeCount, Is.EqualTo(1), "maxProbeCount is back to the default");
            Assert.That(probe.DisablesHeaters, Is.False, "disablesHeaters is back to the default");
            Assert.That(probe.Threshold, Is.EqualTo(500), "the G31 threshold is back to the default");
            Assert.That(probe.TriggerHeight, Is.EqualTo(0.7f).Within(1e-5f), "the G31 trigger height is back to the default");
            Assert.That(ProbeOffset(probe, 0), Is.Zero, "the G31 X offset is back to zero");
            Assert.That(ProbeOffset(probe, 1), Is.Zero, "the G31 Y offset is back to zero");
        });
    }

    /// <summary>
    /// Redefining a probe on another board deletes its monitor on the old board before creating it on
    /// the new one
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM558 deletes the old RemoteZProbe, whose destructor sends
    /// CanInterface::DeleteHandle (ChangeInputMonitor action Delete) to the old board, before
    /// RemoteZProbe::Create asks the new board. On the same board RRF sends the delete too; DSF lets
    /// the create replace the monitor, which Duet3Expansion's InputMonitor::Create does by deleting
    /// any monitor under the same handle first, so that case is not pinned frame by frame
    /// </remarks>
    [Test]
    public async Task M558RedefinitionOnAnotherBoardReleasesTheOldMonitor()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);

        Assert.That((await bench.Host.ExecuteCodeAsync($"M558 K0 P8 C\"{ProbePort(ProbeIo, OtherBoard)}\"")).Trim(), Is.Empty,
                    "the redefinition is accepted");

        string[] sent = [.. bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage)
                                           .Select(p => p.DecodeCanMessage().Header)
                                           .Select(h => $"{(CanMessageType)h.MsgType}@{h.DstAddress}")];
        Assert.That(sent, Is.EqualTo(new[] { $"{CanMessageType.ChangeInputMonitorV1}@{Machines.DriverBoard}",
                                             $"{CanMessageType.CreateInputMonitorV1}@{OtherBoard}" }),
                    "the old board is told first, then the new one (RRF ~RemoteZProbe, RemoteZProbe::Create)");

        (_, CanMessageChangeInputMonitorV1 released) = bench.CanMaster.LastCanMessage<CanMessageChangeInputMonitorV1>();
        (_, CanMessageCreateInputMonitorV1 created) = bench.CanMaster.LastCanMessage<CanMessageCreateInputMonitorV1>();
        Assert.Multiple(() =>
        {
            Assert.That(released.Action, Is.EqualTo(CanMessageChangeInputMonitorV1.ActionDelete), "the old monitor is deleted");
            Assert.That(released.Handle.All, Is.EqualTo(RemoteProbes.HandleFor(0).All), "under the probe's handle");
            Assert.That(created.Handle.All, Is.EqualTo(RemoteProbes.HandleFor(0).All), "and created under the same handle");
            Assert.That(created.PinNameString, Is.EqualTo(LocalProbePort()), "on the new board's pin");
        });
    }

    /// <summary>
    /// M558 P0 turns a probe into the manual probe, which watches no input, so the board is told to
    /// let the old one go
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM558 deletes the RemoteZProbe (its destructor sends
    /// DeleteHandle) and creates a DummyZProbe, which takes no port. Regression case
    /// m558-probe-type-none, and every case's M558 K0 P0 teardown
    /// </remarks>
    [Test]
    public async Task M558TypeNoneReleasesTheMonitor()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);

        Assert.That((await bench.Host.ExecuteCodeAsync("M558 K0 P0 H5 F120 T3000")).Trim(), Is.Empty, "M558 P0 is accepted");

        var released = bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>();
        Assert.Multiple(async () =>
        {
            Assert.That(released.Select(m => (m.Board, m.Message.Action, m.Message.Handle.All)),
                        Is.EqualTo(new[] { (Machines.DriverBoard, CanMessageChangeInputMonitorV1.ActionDelete, RemoteProbes.HandleFor(0).All) }),
                        "the board deletes the probe's monitor (RRF ~RemoteZProbe)");
            Assert.That(bench.CanMaster.CanMessages<CanMessageCreateInputMonitorV1>(), Is.Empty, "and is asked to watch nothing");
            Assert.That(await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)?.Type), Is.EqualTo(ProbeType.None),
                        "sensors.probes[0].type is 0");
        });
    }

    /// <summary>
    /// A motor stall probe needs no port and asks no board to watch an input
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleM558 creates a MotorStallZProbe for type 10 without looking
    /// for C. What DSF does with it when probing is rrf-differences.md section 2.2
    /// </remarks>
    [Test]
    public async Task M558MotorStallProbeWatchesNoInput()
    {
        await using Bench bench = await StartBenchAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M558 K0 P10 H5")).Trim(), Is.Empty, "M558 P10 is accepted without a port");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)?.Type), Is.EqualTo(ProbeType.ZMotorStall),
                        "sensors.probes[0].type is 10");
            Assert.That(bench.CanMaster.CanMessages<CanMessageCreateInputMonitorV1>(), Is.Empty, "no board is asked to watch an input");
        });
    }

    /// <summary>
    /// A probe number past the last one is refused, and nothing is created
    /// </summary>
    /// <remarks>RRF truth: EndstopsManager::HandleM558 reads K with GetLimitedUIValue against MaxZProbes (4)</remarks>
    [Test]
    public async Task M558RangeChecksTheProbeNumber()
    {
        await using Bench bench = await StartBenchAsync();

        Assert.That(await bench.Host.ExecuteCodeAsync($"M558 K{RemoteProbes.MaxProbes} P8 C\"{ProbePort()}\""), Does.StartWith("Error:"),
                    "K past the last probe is refused");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes.Any(p => p is not null)), Is.False,
                        "no probe is created");
            Assert.That(bench.CanMaster.CanMessages<CanMessageCreateInputMonitorV1>(), Is.Empty, "and no board is asked to watch anything");
        });
    }

    /// <summary>
    /// M558 A is limited to 31 taps
    /// </summary>
    /// <remarks>RRF truth: ZProbe::Configure, min(A, ZProbe::MaxTapsLimit) where MaxTapsLimit is 31; the wiki gives the same maximum</remarks>
    [Test]
    public async Task M558LimitsMaxTapsTo31()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);

        Assert.That((await bench.Host.ExecuteCodeAsync("M558 K0 A40")).Trim(), Is.Empty, "A40 is accepted");
        Assert.That(await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)!.MaxProbeCount), Is.EqualTo(31),
                    "and limited to MaxTapsLimit (RRF ZProbe::Configure)");
    }

    /// <summary>
    /// M558 A0 is stored as given: RepRapFirmware limits A from above only
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::Configure stores min(A, MaxTapsLimit), so A0 reports maxProbeCount 0.
    /// Probing still taps once: the tap loop only checks the count after a tap
    /// </remarks>
    [Category("KnownGap")]   // DSF clamps A to 1..31
    [Test]
    public async Task M558StoresZeroMaxTapsAsGiven()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);

        Assert.That((await bench.Host.ExecuteCodeAsync("M558 K0 A0")).Trim(), Is.Empty, "A0 is accepted");
        Assert.That(await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)!.MaxProbeCount), Is.Zero,
                    "sensors.probes[0].maxProbeCount is 0 (RRF ZProbe::Configure)");
    }

    /// <summary>
    /// M558 B suspends the heaters while probing only when it is exactly 1
    /// </summary>
    /// <remarks>RRF truth: ZProbe::Configure, turnHeatersOff = (B == 1); the report says "heaters suspended" or "heaters normal"</remarks>
    [TestCase(1, true)]
    [TestCase(0, false)]
    [TestCase(2, false)]
    public async Task M558BSetsWhetherHeatersAreSuspended(int b, bool suspended)
    {
        await using Bench bench = await StartBenchAsync(ProbeLine + (suspended ? "" : " B1"));

        Assert.That((await bench.Host.ExecuteCodeAsync($"M558 K0 B{b}")).Trim(), Is.Empty, $"B{b} is accepted");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)!.DisablesHeaters), Is.EqualTo(suspended),
                        "sensors.probes[0].disablesHeaters (RRF ZProbe::Configure)");
            Assert.That(await bench.Host.ExecuteCodeAsync("M558 K0"),
                        Does.Contain(suspended ? "heaters suspended" : "heaters normal"), "and the report says so");
        });
    }

    /// <summary>
    /// With M558 B1 a probing move runs with every heater suspended, and the heaters are resumed once
    /// it has stopped
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes4.cpp's probing states call Heat::SuspendHeaters(true) before the probing move
    /// when GetTurnHeatersOff() and SuspendHeaters(false) once it has finished; for a heater on an
    /// expansion board RemoteHeater::Suspend sends CanMessageSetHeaterTemperatureV1 commandSuspend and
    /// then commandUnsuspend, whether or not the heater is on. Regression case
    /// m558-heaters-off-while-probing. The probing move itself is G30's
    /// </remarks>
    [Category("KnownGap")]   // DSF probes with the heaters as they were
    [Test]
    public async Task M558B1SuspendsHeatersAroundTheProbingMove()
    {
        const int heater = 0;
        await using Bench bench = await StartBenchAsync(ProbeLine + $"""
             B1 F300
            G31 K0 Z1.5 P500
            M308 S{heater} P"{Expansion.Temp(Machines.DriverBoard, 0)}" Y"thermistor"
            M950 H{heater} C"{Expansion.Out(Machines.DriverBoard, 0)}" T{heater}
            """);

        Task<string> probeTask = bench.Host.ExecuteCodeAsync("G30");
        (uint MoveId, (byte Board, byte Driver)[] Drivers) dive =
            await WaitForArmedMoveAsync(bench.CanMaster, 0, "the probing move arriving armed on the probe");
        var suspendedBeforeMove = bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>()
                                                 .Where(m => m.Message.Command == CanMessageSetHeaterTemperatureV1.CommandSuspend)
                                                 .ToArray();

        bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteProbes.HandleFor(0), active: true);
        StopMove(bench.CanMaster, dive);
        await probeTask;

        var resumed = bench.CanMaster.CanMessages<CanMessageSetHeaterTemperatureV1>()
                                     .Where(m => m.Message.Command == CanMessageSetHeaterTemperatureV1.CommandUnsuspend)
                                     .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(suspendedBeforeMove.Select(m => (m.Board, (int)m.Message.HeaterNumber)),
                        Is.EqualTo(new[] { (Machines.DriverBoard, heater) }),
                        "the heater is suspended before the probing move (RRF RemoteHeater::Suspend(true))");
            Assert.That(resumed.Select(m => (m.Board, (int)m.Message.HeaterNumber)),
                        Is.EqualTo(new[] { (Machines.DriverBoard, heater) }),
                        "and resumed once it has stopped (RRF RemoteHeater::Suspend(false))");
        });
    }

    /// <summary>
    /// A load cell probe is accepted on an expansion board once it has its scale, is created as an
    /// analog monitor and tared at once; without V it is refused and nothing is left behind
    /// </summary>
    /// <remarks>
    /// RRF truth: RemoteZProbe::Create allows type 12 on an expansion board, creates the monitor with
    /// threshold DefaultZProbeADValue and tares it with ChangeInputMonitor action Tare, param
    /// TareAndTrack; ZProbe::Configure then refuses a load cell without V ("load cell Z probes need
    /// the V parameter"), and the failed probe is deleted. The wiki's M558 page documents V and U for
    /// RRF 3.7. Regression case m558-load-cell-probe, which the rig skips for want of a TOOLINDX
    /// </remarks>
    [Category("KnownGap")]   // DSF has no type 12
    [Test]
    public async Task M558CreatesALoadCellProbe()
    {
        string loadCell = $"{Machines.DriverBoard}.loadcell";
        await using Bench bench = await StartBenchAsync();

        Assert.That(await bench.Host.ExecuteCodeAsync($"M558 K0 P12 C\"{loadCell}\""),
                    Does.StartWith("Error:").And.Contain("V parameter"),
                    "a load cell without its scale is refused (RRF ZProbe::Configure)");
        Assert.That(await bench.Host.ReadModelAsync(model => ProbeAt(model, 0)), Is.Null, "and leaves no probe");
        bench.CanMaster.ClearCapture();

        Assert.That((await bench.Host.ExecuteCodeAsync($"M558 K0 P12 C\"{loadCell}\" V0.0025 U-50:50")).Trim(), Is.Empty,
                    "a load cell with its scale is accepted on an expansion board (RRF RemoteZProbe::Create)");
        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ReadModelAsync(model => (int?)ProbeAt(model, 0)?.Type), Is.EqualTo(12),
                        "sensors.probes[0].type is 12");
            Assert.That(bench.CanMaster.CanMessages<CanMessageCreateInputMonitorV1>().Select(m => m.Message.Threshold),
                        Is.EqualTo(new[] { 500 }), "the monitor is created analog");
            Assert.That(bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>().Select(m => (m.Message.Action, m.Message.Param)),
                        Is.EqualTo(new[] { (CanMessageChangeInputMonitorV1.ActionTare, CanMessageChangeInputMonitorV1.ParamTareAndTrack) }),
                        "and tared at once, tracking drift (RRF RemoteZProbe::Create, TareHandle)");
        });
    }

    /// <summary>
    /// M558 V and U belong to a load cell probe and are refused for any other
    /// </summary>
    /// <remarks>RRF truth: ZProbe::Configure, "V parameter is only valid for load cell Z probes" and the same for U</remarks>
    [Category("KnownGap")]   // DSF ignores V and U
    [TestCase("V0.0025")]
    [TestCase("U-50:50")]
    public async Task M558RefusesLoadCellParametersForOtherProbes(string parameter)
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);

        Assert.That(await bench.Host.ExecuteCodeAsync($"M558 K0 {parameter}"),
                    Does.StartWith("Error:").And.Contain("only valid for load cell"),
                    "the parameter is refused (RRF ZProbe::Configure)");
    }
}
