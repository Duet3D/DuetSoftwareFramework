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
    /// <summary>The temperature sensor the compensation scenarios measure against</summary>
    private const int CompensationSensor = 4;

    /// <summary>A thermistor on <see cref="Machines.DriverBoard"/> as <see cref="CompensationSensor"/></summary>
    private static readonly string CompensationSensorLine =
        $"M308 S{CompensationSensor} P\"{Expansion.Temp(Machines.DriverBoard, 0)}\" Y\"thermistor\"";

    /// <summary>
    /// G31 writes the threshold, trigger height and per-axis offsets, with the Z offset held as the
    /// negative of the trigger height
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::HandleG31 (ZProbe.cpp): P writes targetAdcValue (OM threshold), X and Y
    /// write offsets[axis], Z writes offsets[Z] = -Z. The OM reports triggerHeight as
    /// -offsets[Z_AXIS] and offsets[] as stored, so after G31 Z1.25 the trigger height is 1.25 and
    /// offsets[2] is -1.25. The report wording is the same function's else branch. Regression case
    /// g31-set-probe-trigger
    /// </remarks>
    [Test]
    public async Task G31SetsProbeParameters()
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);

        string reply = await bench.Host.ExecuteCodeAsync("G31 K0 P600 X-10 Y5 Z1.25");
        Assert.That(reply, Reply.IsEmpty, "G31 was accepted");

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
        Assert.That(report, Reply.IsSuccess("threshold 600"),
                    "the G31 report includes the threshold (RRF ZProbe::HandleG31 report)");
        Assert.That(report, Reply.IsSuccess("trigger height 1.250"),
                    "the G31 report includes the trigger height (RRF ZProbe::HandleG31 report)");
    }

    /// <summary>The axes past Z each <see cref="G31SetsOffsetsOnAxesPastZ"/> case creates</summary>
    private static readonly object[] ExtraAxes =
    [
        new object[] { "UVW", new[] { 5.0f, -5.0f, 2.5f } },
        new object[] { "ABC", new[] { 1.5f, 2.5f, -3.5f } },
    ];

    /// <summary>
    /// G31 takes an offset for every axis but Z, stored by axis index and reported in axis order
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::HandleG31 loops over GetTotalAxes() and reads each letter but Z into
    /// offsets[i]; ZProbe.cpp's OM array 0 reports one offset per visible axis, and the report lists
    /// every letter but Z. Regression cases g31-offsets-extra-axes and g31-offsets-rotary-axes, which
    /// fail on the rig's DSF bench only because they put the extra axes on main board drivers
    /// </remarks>
    [TestCaseSource(nameof(ExtraAxes))]
    public async Task G31SetsOffsetsOnAxesPastZ(string letters, float[] offsets)
    {
        string drivers = string.Join(" ", letters.Select((letter, i) => $"{letter}{Expansion.Driver(Machines.DriverBoard, 3 + i)}"));
        await using Bench bench = await StartBenchAsync($"M584 {drivers}\n{ProbeLine}\nG31 K0 P500 X0 Y0 Z0");

        string offsetWords = string.Join(" ", letters.Select((letter, i) => FormattableString.Invariant($"{letter}{offsets[i]}")));
        Assert.That(await bench.Host.ExecuteCodeAsync($"G31 K0 P500 X0 Y0 {offsetWords} Z0"), Reply.IsEmpty, "G31 was accepted");

        float[] stored = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.Offsets.ToArray());
        Assert.That(stored, Is.EqualTo(new[] { 0.0f, 0.0f, 0.0f }.Concat(offsets)).Within(1e-4f),
                    "sensors.probes[0].offsets[3..5] hold the offsets of the axes past Z (RRF ZProbe::HandleG31)");

        string expected = "offsets X0.0 Y0.0 " + string.Join(" ", letters.Select((letter, i) => FormattableString.Invariant($"{letter}{offsets[i]:F1}")));
        Assert.That(await bench.Host.ExecuteCodeAsync("G31 K0"), Reply.IsSuccess(expected),
                    "the report lists the offset of every axis but Z (RRF ZProbe::HandleG31 report)");
    }

    /// <summary>
    /// G31 K applies to the probe K names and leaves the others as they were
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleG31 reads K with GetLimitedUIValue and calls that probe's
    /// HandleG31. Regression case g31-probe-by-type
    /// </remarks>
    [Test]
    public async Task G31KSelectsTheProbe()
    {
        await using Bench bench = await StartBenchAsync(
            $"{ProbeLine}\nM558 K1 P8 C\"{Expansion.GpioIn(Machines.DriverBoard, 2)}\"\nG31 K0 P500 Z2");

        Assert.That(await bench.Host.ExecuteCodeAsync("G31 K1 P600 Z1.20 X5 Y-5"), Reply.IsEmpty, "G31 K1 was accepted");

        (Probe first, Probe second) = await bench.Host.ReadModelAsync(model => (model.Sensors.Probes[0]!, model.Sensors.Probes[1]!));
        Assert.Multiple(() =>
        {
            Assert.That(second.Threshold, Is.EqualTo(600), "probe 1's threshold is set");
            Assert.That(second.TriggerHeight, Is.EqualTo(1.2f).Within(1e-4f), "probe 1's trigger height is set");
            Assert.That(ProbeOffset(second, 0), Is.EqualTo(5.0f).Within(1e-4f), "probe 1's X offset is set");
            Assert.That(ProbeOffset(second, 1), Is.EqualTo(-5.0f).Within(1e-4f), "probe 1's Y offset is set");
            Assert.That(ProbeOffset(second, 2), Is.EqualTo(-1.2f).Within(1e-4f), "probe 1's Z offset is the negated trigger height");
            Assert.That(first.Threshold, Is.EqualTo(500), "probe 0 keeps its threshold");
            Assert.That(first.TriggerHeight, Is.EqualTo(2.0f).Within(1e-4f), "probe 0 keeps its trigger height");
            Assert.That(ProbeOffset(first, 0), Is.Zero, "probe 0 keeps its X offset");
        });

        Assert.Multiple(async () =>
        {
            Assert.That(await bench.Host.ExecuteCodeAsync("G31 K1"),
                        Reply.IsSuccess("Z probe 1:", "threshold 600, trigger height 1.200", "offsets X5.0 Y-5.0"),
                        "G31 K1 reports probe 1");
            Assert.That(await bench.Host.ExecuteCodeAsync("G31 K0"),
                        Reply.IsSuccess("Z probe 0:", "threshold 500, trigger height 2.000", "offsets X0.0 Y0.0"),
                        "G31 K0 reports probe 0");
        });
    }

    /// <summary>
    /// G31 with nothing to set reports the probe's current reading, threshold, trigger height and
    /// offsets
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::HandleG31's report; a triggered digital probe on an expansion board reads
    /// 1000 (RemoteZProbe::GetRawReading). Regression case g31-report-probe-status
    /// </remarks>
    [Test]
    public async Task G31ReportsTheProbe()
    {
        await using Bench bench = await StartBenchAsync($"{ProbeLine}\nG31 K0 P500 X-20.5 Y10.25 Z2.75");
        bench.CanMaster.InjectInputChange(Machines.DriverBoard, RemoteProbes.HandleFor(0), active: true);
        await bench.Host.WaitForModelAsync(model => model.Sensors.Probes[0]!.Value.FirstOrDefault(),
                                           RemoteProbes.MaxReading, "probe 0's reading");

        Assert.That(await bench.Host.ExecuteCodeAsync("G31 K0"),
                    Reply.IsSuccess("Z probe 0: current reading 1000, threshold 500, trigger height 2.750, offsets X-20.5 Y10.2"),
                    "G31 reports the probe (RRF ZProbe::HandleG31 report)");
    }

    /// <summary>
    /// G31 T sets the temperature coefficients, with S the temperature they were calibrated at and H
    /// the sensor they are measured with
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::HandleG31 reads H into the sensor, T into both temperatureCoefficients
    /// (unspecified ones zero) and S into calibTemperature. The report adds " at %.1fC, temperature
    /// coefficients [...]" when either coefficient is nonzero. sensors.probes[].sensor is the DSF
    /// addition of rrf-differences.md section 3. Regression case g31-probe-temperature-compensation
    /// </remarks>
    [Test]
    public async Task G31SetsTemperatureCompensation()
    {
        await using Bench bench = await StartBenchAsync($"{CompensationSensorLine}\n{ProbeLine}");

        Assert.That(await bench.Host.ExecuteCodeAsync($"G31 K0 Z0.7 S30 H{CompensationSensor} T0.03:0.002"), Reply.IsEmpty,
                    "G31 was accepted");

        Probe probe = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!);
        Assert.Multiple(() =>
        {
            Assert.That(probe.TemperatureCoefficients, Is.EqualTo(new[] { 0.03f, 0.002f }).Within(1e-6f),
                        "G31 T sets sensors.probes[0].temperatureCoefficients");
            Assert.That(probe.CalibrationTemperature, Is.EqualTo(30.0f).Within(1e-4f),
                        "G31 S sets sensors.probes[0].calibrationTemperature");
            Assert.That(probe.Sensor, Is.EqualTo(CompensationSensor), "G31 H sets sensors.probes[0].sensor");
        });

        Assert.That(await bench.Host.ExecuteCodeAsync("G31 K0"),
                    Reply.IsSuccess("trigger height 0.700 at 30.0", "temperature coefficients [0.0/", "C^2]"),
                    "the report includes the calibration temperature and coefficients (RRF ZProbe::HandleG31 report)");

        Assert.That(await bench.Host.ExecuteCodeAsync($"G31 K0 H{CompensationSensor} T0.01 S20"), Reply.IsEmpty, "G31 with one T value was accepted");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.TemperatureCoefficients.ToArray()),
                    Is.EqualTo(new[] { 0.01f, 0.0f }).Within(1e-6f),
                    "one T value zeroes the second coefficient (RRF ZProbe::HandleG31 clears both first)");
    }

    /// <summary>
    /// G31 T without S calibrates at the sensor's current temperature
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::HandleG31 sets calibTemperature to GetSensorTemperature(H) when T is given
    /// without S. The wiki's G31 page: S "The default is current temperature"
    /// </remarks>
    [Category("KnownGap")]   // DSF only changes calibrationTemperature when S is given
    [Test]
    public async Task G31CoefficientWithoutSCalibratesAtTheCurrentTemperature()
    {
        const float current = 31.5f;
        await using Bench bench = await StartBenchAsync($"{CompensationSensorLine}\n{ProbeLine}");
        bench.CanMaster.InjectSensorReport(Machines.DriverBoard, CompensationSensor, current);
        await bench.Host.WaitForModelAsync(model => CompensationSensor < model.Sensors.Analog.Count
                                                    ? model.Sensors.Analog[CompensationSensor]?.LastReading
                                                    : null,
                                           current, "the sensor reading");

        Assert.That(await bench.Host.ExecuteCodeAsync($"G31 K0 H{CompensationSensor} T0.02"), Reply.IsEmpty, "G31 was accepted");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.CalibrationTemperature),
                    Is.EqualTo(current).Within(1e-3f),
                    "sensors.probes[0].calibrationTemperature is the sensor's reading (RRF ZProbe::HandleG31)");
    }

    /// <summary>
    /// G31 T against a sensor that exists but has no reading waits for one, then warns and calibrates
    /// at the default 25C
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::HandleG31 returns notFinished for up to 500 ms while the sensor has no valid
    /// reading, then warns "Sensor %d did not provide a valid temperature reading, using default" and
    /// sets calibTemperature to DefaultZProbeTemperature
    /// </remarks>
    [Category("KnownGap")]   // DSF only changes calibrationTemperature when S is given, and never warns
    [Test]
    public async Task G31CoefficientAgainstASilentSensorUsesTheDefault()
    {
        await using Bench bench = await StartBenchAsync($"{CompensationSensorLine}\n{ProbeLine}");

        Assert.That(await bench.Host.ExecuteCodeAsync($"G31 K0 H{CompensationSensor} T0.02"),
                    Reply.IsWarning("did not provide a valid temperature reading"),
                    "a sensor with no reading is warned about (RRF ZProbe::HandleG31)");
        Probe probe = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!);
        Assert.Multiple(() =>
        {
            Assert.That(probe.CalibrationTemperature, Is.EqualTo(25.0f).Within(1e-4f), "and the default temperature is used");
            Assert.That(probe.TemperatureCoefficients[0], Is.EqualTo(0.02f).Within(1e-6f), "the coefficient is still set");
        });
    }

    /// <summary>
    /// G31 T with a sensor that does not exist is ignored with a warning: the coefficients are
    /// cleared and the calibration temperature kept
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::HandleG31 zeroes temperatureCoefficients as soon as T is seen, then warns
    /// "Temperature coefficients ignored due to invalid sensor number %d" when GetSensorTemperature
    /// reports unknownSensor, leaving calibTemperature alone; H was seen, so the sensor is still
    /// recorded
    /// </remarks>
    [Category("KnownGap")]   // DSF stores the coefficients against a sensor that does not exist
    [Test]
    public async Task G31CoefficientForAnUnknownSensorIsIgnored()
    {
        const int unknownSensor = 9;
        await using Bench bench = await StartBenchAsync($"{CompensationSensorLine}\n{ProbeLine}\nG31 K0 H{CompensationSensor} T0.01 S40");

        Assert.That(await bench.Host.ExecuteCodeAsync($"G31 K0 H{unknownSensor} T0.02"),
                    Reply.IsWarning($"invalid sensor number {unknownSensor}"),
                    "an unknown sensor is warned about (RRF ZProbe::HandleG31)");
        Probe probe = await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!);
        Assert.Multiple(() =>
        {
            Assert.That(probe.TemperatureCoefficients, Is.EqualTo(new[] { 0.0f, 0.0f }), "the coefficients are cleared");
            Assert.That(probe.CalibrationTemperature, Is.EqualTo(40.0f).Within(1e-4f), "the calibration temperature is kept");
            Assert.That(probe.Sensor, Is.EqualTo(unknownSensor), "the sensor H named is recorded");
        });
    }

    /// <summary>
    /// G31 S is only read with T, so G31 S alone reports rather than changing the calibration
    /// temperature
    /// </summary>
    /// <remarks>RRF truth: ZProbe::HandleG31 reads S inside the T branch; with nothing else seen it reports</remarks>
    [Category("KnownGap")]   // DSF reads S on its own
    [Test]
    public async Task G31CalibrationTemperatureNeedsACoefficient()
    {
        await using Bench bench = await StartBenchAsync($"{CompensationSensorLine}\n{ProbeLine}\nG31 K0 H{CompensationSensor} T0.01 S40");

        Assert.That(await bench.Host.ExecuteCodeAsync("G31 K0 S30"), Reply.IsSuccess("Z probe 0: current reading"),
                    "G31 S alone reports the probe (RRF ZProbe::HandleG31)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.CalibrationTemperature),
                    Is.EqualTo(40.0f).Within(1e-4f), "and leaves the calibration temperature alone");
    }

    /// <summary>
    /// G31 P on an analog probe tells the board the new threshold; a digital probe has none to send
    /// </summary>
    /// <remarks>
    /// RRF truth: RemoteZProbe::HandleG31 sends CanInterface::ChangeHandleThreshold
    /// (ChangeInputMonitor action ChangeThreshold, param the threshold) for types 1, 11 and 12 when P
    /// is seen
    /// </remarks>
    [TestCase(1, true)]
    [TestCase(11, true)]
    [TestCase(8, false)]
    [TestCase(9, false)]
    public async Task G31PSendsTheThresholdToAnAnalogProbe(int type, bool sent)
    {
        await using Bench bench = await Bench.With(Machines.Xyz, $"M558 K0 P{type} C\"{Expansion.GpioIn(Machines.DriverBoard, 1)}\"")
                                             .WithController(c => AnswerReadInputs(c, 0))
                                             .StartAsync();

        Assert.That(await bench.Host.ExecuteCodeAsync("G31 K0 P600"), Reply.IsEmpty, "G31 P was accepted");

        var changes = bench.CanMaster.CanMessages<CanMessageChangeInputMonitorV1>();
        if (sent)
        {
            Assert.That(changes.Select(m => (m.Board, m.Message.Action, m.Message.Param, m.Message.Handle.All)),
                        Is.EqualTo(new[] { (Machines.DriverBoard, CanMessageChangeInputMonitorV1.ActionChangeThreshold, 600u, RemoteProbes.HandleFor(0).All) }),
                        "the board is told the new threshold (RRF RemoteZProbe::HandleG31)");
        }
        else
        {
            Assert.That(changes, Is.Empty, "a digital probe's board is told nothing");
        }
    }

    /// <summary>
    /// A board that refuses the new threshold fails G31 with its reason, and the model keeps the
    /// threshold G31 gave
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::HandleG31 writes targetAdcValue before RemoteZProbe::HandleG31 sends it,
    /// and the board's result replaces G31's when it is worse
    /// </remarks>
    [Test]
    public async Task G31ThresholdRefusedByTheBoardFailsTheCode()
    {
        const string refusal = "Threshold out of range";
        await using Bench bench = await Bench.With(Machines.Xyz, $"M558 K0 P1 C\"{Expansion.GpioIn(Machines.DriverBoard, 1)}\"")
                                             .WithController(c => c.ReportWith<CanMessageChangeInputMonitorV1>(refusal, CodeResult.Error))
                                             .StartAsync();

        Assert.That(await bench.Host.ExecuteCodeAsync("G31 K0 P600"), Reply.IsError(refusal),
                    "the board's refusal is the reply (RRF RemoteZProbe::HandleG31)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.Threshold), Is.EqualTo(600),
                    "sensors.probes[0].threshold holds what G31 gave (RRF ZProbe::HandleG31)");
    }

    /// <summary>
    /// G31 for a probe that does not exist, or a probe number past the last, is refused
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::HandleG31 refuses "invalid Z probe index" for a probe that does not
    /// exist, after GetLimitedUIValue has refused a K of MaxZProbes or more. Regression case
    /// error-missing-parameters-probing-gcodes (G31 K3)
    /// </remarks>
    [TestCase(3)]
    [TestCase(RemoteProbes.MaxProbes)]
    public async Task G31ForAMissingProbeIsRefused(int probe)
    {
        await using Bench bench = await StartBenchAsync(ProbeLine);

        Assert.That(await bench.Host.ExecuteCodeAsync($"G31 K{probe} Z2"), Reply.IsError(),
                    $"G31 K{probe} is refused (RRF EndstopsManager::HandleG31)");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes.Count(p => p is not null)), Is.EqualTo(1),
                    "and no probe is created");
    }
}
