using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using static System.FormattableString;

namespace SystemTests.Scenarios.Codes.General;

/// <summary>M500 and M501, saving the calibrated settings to config-override.g and loading them back</summary>
/// <remarks>
/// RRF truth: GCodes::WriteConfigOverrideFile (GCodes.cpp) writes the header, then
/// Kinematics::WriteCalibrationParameters, Heat::WriteModelParameters, Move::WriteMoveParameters,
/// EndstopsManager::WriteZProbeParameters, GCodes::WriteToolParameters (GCodes5.cpp),
/// GCodes::WriteWorkplaceCoordinates and, for P68, GCodes::WriteCoordinateRotation (GCodes3.cpp).
/// GCodes2.cpp case 501 runs config-override.g through DoFileMacro with runningM501 set, which is
/// what marks the values it loads as ones M500 has to write back. Regression references:
/// m500-store-parameters, m501-read-stored-parameters, error-missing-parameters-system-2
/// </remarks>
[TestFixture]
[GCode("M500", "M501")]
public class M500M501Tests : BenchFixture
{
    /// <summary>The warning RRF's M500 gives when config.g did not load config-override.g</summary>
    private const string NoM501Warning = "No M501 command was executed in config.g";

    /// <summary>Where M500 writes and M501 reads, on the bench's virtual SD card</summary>
    private static string OverridePath(Bench bench) => Path.Combine(bench.Host.Sd.Root, "sys", "config-override.g");

    /// <summary>Run an M500 that has to succeed, and return what it wrote</summary>
    private static async Task<string> SaveAsync(Bench bench, string code = "M500")
    {
        string reply = await bench.Host.ExecuteCodeAsync(code);
        Assert.That(reply, Does.Not.StartWith("Error"), $"{code} writes config-override.g");
        return await File.ReadAllTextAsync(OverridePath(bench));
    }

    /// <summary>The non-comment lines of a config-override.g, which are what M501 runs</summary>
    private static string[] CodeLines(string written)
        => written.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith(';')).ToArray();

    /// <summary>A workplace's offsets, by axis, numbered from 1 as G10 L2 P numbers them</summary>
    private static Task<float[]> ReadWorkplaceAsync(Bench bench, int workplace)
        => bench.Host.ReadModelAsync(model => model.Move.Axes.Select(axis => axis.WorkplaceOffsets[workplace - 1]).ToArray());

    /// <summary>Probe 0's trigger height, sensors.probes[0].triggerHeight</summary>
    private static Task<double> ReadTriggerHeightAsync(Bench bench)
        => bench.Host.ReadModelAsync(model => (double)model.Sensors.Probes[0]!.TriggerHeight);

    #region Writing the file

    /// <summary>
    /// The file opens with the two comment lines RRF writes, the first carrying the date and time
    /// </summary>
    /// <remarks>RRF truth: GCodes::WriteConfigOverrideHeader</remarks>
    [Test]
    public async Task M500WritesTheHeader()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);

        string written = await SaveAsync(bench);
        Assert.That(written, Does.Match(@"^; config-override\.g file generated in response to M500 at \d{4}-\d{2}-\d{2} \d{2}:\d{2}\n; This is a system-generated file - do not edit\n"),
                    "the header comment and the do-not-edit line (RRF WriteConfigOverrideHeader)");
    }

    /// <summary>
    /// Every line M500 writes is either a comment or a G-code, because M501 runs the file
    /// </summary>
    /// <remarks>
    /// RRF truth: Kinematics::WriteCalibrationParameters writes nothing for the kinematics that do
    /// not override it, Cartesian among them. A line of report text in the file would be an
    /// unknown code every time M501 ran it
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M500WritesOnlyCodesAndComments()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);

        string[] lines = CodeLines(await SaveAsync(bench));
        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.All.Match(@"^[GM]\d+(\.\d+)?( |$)"), "every line that is not a comment is a G- or M-code");
            Assert.That(lines, Has.None.Match(@"^M66[59]\b"), "Cartesian kinematics has no calibration to save (RRF Kinematics::WriteCalibrationParameters)");
        });
    }

    /// <summary>
    /// The heater model section is written even when there is no heater, as RRF writes it
    /// unconditionally
    /// </summary>
    /// <remarks>RRF truth: Heat::WriteModelParameters. M307Tests covers the models themselves</remarks>
    [Test]
    public async Task M500WritesTheHeaterSectionWithNoHeaters()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);

        string written = await SaveAsync(bench);
        Assert.Multiple(() =>
        {
            Assert.That(written, Does.Contain("; Heater model parameters\n"), "the section comment is always written");
            Assert.That(CodeLines(written), Has.None.StartWith("M307"), "with no heater there is no model to write");
        });
    }

    /// <summary>
    /// Each M500 replaces the whole file, so nothing a previous one wrote survives it
    /// </summary>
    /// <remarks>RRF truth: WriteConfigOverrideFile opens the file with OpenMode::write</remarks>
    [Test]
    public async Task M500ReplacesTheWholeFile()
    {
        await using Bench bench = await Bench.With(Machines.Xyz)
                                             .WithSd(sd => sd.WriteSys("config-override.g", "M117 \"left from before\"\n"))
                                             .StartAsync();

        string written = await SaveAsync(bench);
        Assert.That(written, Does.Not.Contain("left from before"), "the previous content is gone");
    }

    /// <summary>
    /// An M500 that cannot write the file reports an error naming it
    /// </summary>
    /// <remarks>
    /// RRF truth: WriteConfigOverrideFile, "Failed to create file config-override.g" when the file
    /// cannot be opened. A directory of the same name is what makes it impossible here
    /// </remarks>
    [Test]
    public async Task M500ReportsAFileItCannotWrite()
    {
        await using Bench bench = await Bench.With(Machines.Xyz)
                                             .WithSd(sd => Directory.CreateDirectory(Path.Combine(sd.Root, "sys", "config-override.g")))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M500");
        Assert.That(reply, Does.StartWith("Error").And.Contain("config-override.g"),
                    "the failure is an error naming the file (RRF WriteConfigOverrideFile)");
    }

    #endregion

    #region The missing-M501 warning

    /// <summary>
    /// When config.g ran no M501 the file is still written, and every form of M500 warns that it
    /// will not be loaded at startup
    /// </summary>
    /// <remarks>
    /// RRF truth: WriteConfigOverrideFile returns GCodeResult::warning when m501SeenInConfigFile
    /// is clear. Reference m500-store-parameters: all four codes answer
    /// "Warning: M500: No M501 command was executed in config.g"
    /// </remarks>
    [Category("KnownGap")]
    [TestCase("M500")]
    [TestCase("M500 P10")]
    [TestCase("M500 P31")]
    [TestCase("M500 P10:31")]
    public async Task M500WarnsWhenConfigGRanNoM501(string code)
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);

        string reply = (await bench.Host.ExecuteCodeAsync(code)).TrimEnd();
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Warning").And.EndWith(NoM501Warning), "the reply warns that config.g does not load the file");
            Assert.That(File.Exists(OverridePath(bench)), Is.True, "the file is written all the same");
        });
    }

    /// <summary>
    /// An M501 in config.g silences the warning
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 501 sets m501SeenInConfigFile while runningConfigFile</remarks>
    [Test]
    public async Task M500IsQuietWhenConfigGRanM501()
    {
        await using Bench bench = await Bench.With(Machines.Xyz, "M501")
                                             .WithSd(sd => sd.WriteSys("config-override.g", "G4 P0\n"))
                                             .StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M500")).Trim(), Is.Empty, "config.g loads the file, so there is nothing to warn about");
    }

    /// <summary>
    /// So does an M501 in a macro config.g calls, because RRF's flag covers the whole of config.g's
    /// run rather than its own lines
    /// </summary>
    /// <remarks>
    /// RRF truth: runningConfigFile is set in GCodes::RunConfigFile and cleared only once the
    /// trigger channel finishes config.g, so it is still set inside a macro config.g calls
    /// </remarks>
    [Test]
    public async Task M500IsQuietWhenAMacroConfigGCallsRanM501()
    {
        await using Bench bench = await Bench.With(Machines.Xyz, "M98 P\"load-override.g\"")
                                             .WithSd(sd =>
                                             {
                                                 sd.WriteSys("load-override.g", "M501\n");
                                                 sd.WriteSys("config-override.g", "G4 P0\n");
                                             })
                                             .StartAsync();

        Assert.That((await bench.Host.ExecuteCodeAsync("M500")).Trim(), Is.Empty, "the macro's M501 ran while config.g was running");
    }

    /// <summary>
    /// An M501 run after startup does not silence the warning: the file is still not loaded at the
    /// next startup, which is what the warning is about
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 501 sets m501SeenInConfigFile only while runningConfigFile</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M500StillWarnsAfterAnM501OutsideConfigG()
    {
        await using Bench bench = await Bench.With(Machines.Xyz)
                                             .WithSd(sd => sd.WriteSys("config-override.g", "G4 P0\n"))
                                             .StartAsync();

        await bench.Host.ExecuteCodeAsync("M501");
        Assert.That((await bench.Host.ExecuteCodeAsync("M500")).TrimEnd(), Does.StartWith("Warning").And.EndWith(NoM501Warning),
                    "config.g still does not load the file");
    }

    #endregion

    #region Kinematics calibration

    /// <summary>The delta geometry the kinematics scenarios save</summary>
    private const string DeltaGeometry = "M665 L300 R150 B120 H350 X0.5 Y-0.5 Z0.25";

    /// <summary>The delta endstop corrections and tilt the kinematics scenarios save</summary>
    private const string DeltaCorrections = "M666 X0.1 Y-0.2 Z0.3 A0.5 B-0.25";

    /// <summary>
    /// A linear delta's geometry is written as the M665 and M666 that set it
    /// </summary>
    /// <remarks>
    /// RRF truth: LinearDeltaKinematics::WriteCalibrationParameters, "M665 L%.3f:%.3f:%.3f R%.3f
    /// H%.3f B%.1f X%.3f Y%.3f Z%.3f" and "M666 X%.3f Y%.3f Z%.3f A%.2f B%.2f", the tilts in
    /// percent as M666 takes them
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M500WritesLinearDeltaCalibration()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);
        await bench.Host.ExecuteCodeAsync(DeltaGeometry);
        await bench.Host.ExecuteCodeAsync(DeltaCorrections);

        string written = await SaveAsync(bench);
        Assert.That(written, Does.Contain("; Delta parameters\n"
                                          + "M665 L300.000:300.000:300.000 R150.000 H350.000 B120.0 X0.500 Y-0.500 Z0.250\n"
                                          + "M666 X0.100 Y-0.200 Z0.300 A0.50 B-0.25\n"),
                    "the delta parameters as the codes that set them (RRF LinearDeltaKinematics::WriteCalibrationParameters)");
    }

    /// <summary>
    /// M501 puts a saved delta calibration back over a changed one
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 501 runs the M665 and M666 M500 wrote</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M501RestoresLinearDeltaCalibration()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);
        await bench.Host.ExecuteCodeAsync(DeltaGeometry);
        await bench.Host.ExecuteCodeAsync(DeltaCorrections);
        await SaveAsync(bench);

        await bench.Host.ExecuteCodeAsync("M665 R160 H340");
        await bench.Host.ExecuteCodeAsync("M666 X0 Y0 Z0");
        Assert.That((await bench.Host.ExecuteCodeAsync("M501")).Trim(), Is.Empty, "the saved calibration loads without complaint");

        DeltaKinematics delta = await bench.Host.ReadModelAsync(model => (DeltaKinematics)model.Move.Kinematics);
        Assert.Multiple(() =>
        {
            Assert.That(delta.DeltaRadius, Is.EqualTo(150).Within(1e-3), "move.kinematics.deltaRadius is the saved one");
            Assert.That(delta.HomedHeight, Is.EqualTo(350).Within(1e-3), "move.kinematics.homedHeight is the saved one");
            Assert.That(delta.Towers.Select(tower => tower.EndstopAdjustment), Is.EqualTo(new[] { 0.1f, -0.2f, 0.3f }).Within(1e-3f),
                        "move.kinematics.towers[].endstopAdjustment are the saved ones");
        });
    }

    #endregion

    #region Probed axis limits

    /// <summary>An endstop at the high end of X on the expansion board, which G1 H3 measures X's length against</summary>
    private static readonly string XHighEndstop = $"M574 X2 S1 P\"{Expansion.GpioIn(Machines.DriverBoard, 0)}\"";

    /// <summary>
    /// Limits set in config.g are not written: they are in config.g already
    /// </summary>
    /// <remarks>RRF truth: Move::WriteMoveParameters writes only axisMinimaProbed and axisMaximaProbed</remarks>
    [Test]
    public async Task M500LeavesConfiguredAxisLimitsOut()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);
        await bench.Host.ExecuteCodeAsync("M208 X-5:180");

        string written = await SaveAsync(bench);
        Assert.Multiple(() =>
        {
            Assert.That(written, Does.Not.Contain("; Probed axis limits"), "no limit was probed");
            Assert.That(CodeLines(written), Has.None.StartWith("M208"), "so none is written");
        });
    }

    /// <summary>
    /// A limit G1 H3 measured is marked probed, and M500 writes it as the M208 that sets it
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes4.cpp sets the axis limit with byProbing for a G1 H3 move, which sets
    /// axisMaximaProbed (move.axes[].maxProbed); Move::WriteAxisLimits writes "M208 S0 X%.2f"
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M500WritesALimitG1H3Measured()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, XHighEndstop);
        Task<string> measuring = bench.Host.ExecuteCodeAsync(Invariant($"G1 H3 X{Machines.XyMax + 100} F3000"));
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.ScheduledMoves().Any(move => move.Has(ScheduleMoveFlags.CheckEndstops)),
                                             20_000, "the G1 H3 move arriving armed");
        bench.StopMove(bench.CanMaster.ScheduledMoves().First(move => move.Has(ScheduleMoveFlags.CheckEndstops)));
        await measuring;

        Axis x = await bench.Host.ReadModelAsync(model => model.Move.Axes[0]);
        Assert.That(x.MaxProbed, Is.True, "G1 H3 against a high-end switch marks move.axes[0].maxProbed");

        string written = await SaveAsync(bench);
        Assert.That(written, Does.Contain(Invariant($"; Probed axis limits\nM208 S0 X{x.Max:F2}\n")),
                    "the measured maximum is written, and only it (RRF Move::WriteMoveParameters)");
    }

    /// <summary>
    /// A limit config-override.g sets is marked probed, so the next M500 writes it back
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 208 passes runningM501 to Move::ConfigureAxisLimits as byProbing,
    /// so the axes M208 names in config-override.g get minProbed or maxProbed
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M501MarksTheLimitsItSetsAsProbed()
    {
        await using Bench bench = await Bench.With(Machines.Xyz)
                                             .WithSd(sd => sd.WriteSys("config-override.g", "M208 S0 Z148.5\nM208 S1 X-2.25\n"))
                                             .StartAsync();

        await bench.Host.ExecuteCodeAsync("M501");
        Axis[] axes = await bench.Host.ReadModelAsync(model => model.Move.Axes.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(axes[2].Max, Is.EqualTo(148.5).Within(1e-3), "M501 set the Z maximum");
            Assert.That(axes[2].MaxProbed, Is.True, "move.axes[2].maxProbed, because it came from config-override.g");
            Assert.That(axes[2].MinProbed, Is.False, "the Z minimum was not touched");
            Assert.That(axes[0].MinProbed, Is.True, "move.axes[0].minProbed, likewise");
        });

        string written = await SaveAsync(bench);
        Assert.That(written, Does.Contain("; Probed axis limits\nM208 S1 X-2.25\nM208 S0 Z148.50\n"),
                    "the loaded limits are saved again, minima first (RRF Move::WriteMoveParameters)");
    }

    #endregion

    #region Z probe

    /// <summary>The Z probe the probe scenarios save, with the trigger threshold and offsets G31 gives it</summary>
    private static readonly string ProbeConfig = $"M558 K0 P8 C\"{Expansion.GpioIn(Machines.DriverBoard, 1)}\"\nG31 K0 P400 X12 Y-3.5 Z1.25";

    /// <summary>The G31 M500 writes for <see cref="ProbeConfig"/>; RRF writes Z to two decimals</summary>
    private const string ProbeLine = "G31 K0 P400 X12.0 Y-3.5 Z1.25";

    /// <summary>
    /// Without P31 the probe's values are not written: they were typed into config.g, not measured
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::WriteZProbeParameters writes a probe only once saveToConfigOverride
    /// is set, which only P31 and loading through M501 do
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M500LeavesTheProbeOutWithoutP31()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, ProbeConfig);

        string written = await SaveAsync(bench);
        Assert.Multiple(() =>
        {
            Assert.That(written, Does.Not.Contain("; Z probe parameters"), "no probe asked to be saved");
            Assert.That(CodeLines(written), Has.None.StartWith("G31"), "so no G31 is written");
        });
    }

    /// <summary>
    /// P31 writes each probe as the G31 that sets its threshold, offsets and trigger height
    /// </summary>
    /// <remarks>
    /// RRF truth: ZProbe::WriteParameters, "G31 K%u P%d" then " %c%.1f" for every axis but Z and
    /// " Z%.2f" for the trigger height
    /// </remarks>
    [Test]
    public async Task M500P31WritesTheProbe()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, ProbeConfig);

        string written = await SaveAsync(bench, "M500 P31");
        Assert.That(written, Does.Contain("; Z probe parameters\n" + ProbeLine),
                    "the probe as its G31 (RRF ZProbe::WriteParameters)");
    }

    /// <summary>
    /// With no probe configured P31 has nothing to write, not even the section comment
    /// </summary>
    /// <remarks>
    /// RRF truth: EndstopsManager::WriteZProbeParameters skips empty probe slots, and a board
    /// without ALLOCATE_DEFAULT_PORTS (the MB6HC) has no probe until M558 creates one
    /// </remarks>
    [Test]
    public async Task M500P31WritesNothingWithoutAProbe()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);

        string written = await SaveAsync(bench, "M500 P31");
        Assert.Multiple(() =>
        {
            Assert.That(written, Does.Not.Contain("; Z probe parameters"), "no section for no probe");
            Assert.That(CodeLines(written), Has.None.StartWith("G31"), "and no G31");
        });
    }

    /// <summary>
    /// Every probe is written, in probe number order
    /// </summary>
    /// <remarks>RRF truth: EndstopsManager::WriteZProbeParameters loops over every probe slot</remarks>
    [Test]
    public async Task M500P31WritesEveryProbe()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, ProbeConfig,
                                                         $"M558 K1 P8 C\"{Expansion.GpioIn(Machines.DriverBoard, 2)}\"\nG31 K1 P300 X-20 Y4 Z2.5");

        string[] probeLines = CodeLines(await SaveAsync(bench, "M500 P31")).Where(line => line.StartsWith("G31")).ToArray();
        Assert.That(probeLines, Has.Length.EqualTo(2), "one G31 per probe");
        Assert.Multiple(() =>
        {
            Assert.That(probeLines[0], Does.StartWith(ProbeLine), "probe 0 first");
            Assert.That(probeLines[1], Does.StartWith("G31 K1 P300 X-20.0 Y4.0 Z2.50"), "then probe 1");
        });
    }

    /// <summary>
    /// P31 sticks: once asked for, the probe is written by every later M500 too
    /// </summary>
    /// <remarks>RRF truth: EndstopsManager::WriteZProbeParameters sets saveToConfigOverride for P31, and nothing clears it</remarks>
    [Test]
    public async Task M500KeepsWritingTheProbeAfterP31()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, ProbeConfig);
        await SaveAsync(bench, "M500 P31");
        await bench.Host.ExecuteCodeAsync("G31 K0 Z0.75");

        string written = await SaveAsync(bench);
        Assert.That(written, Does.Contain("G31 K0 P400 X12.0 Y-3.5 Z0.75"), "the bare M500 still writes the probe, with its new trigger height");
    }

    /// <summary>
    /// A probe config-override.g sets is written by the next M500 without P31
    /// </summary>
    /// <remarks>RRF truth: ZProbe::HandleG31 sets saveToConfigOverride when runningM501</remarks>
    [Test]
    public async Task M500WritesAProbeM501Loaded()
    {
        await using Bench bench = await Bench.With(Machines.Xyz, ProbeConfig)
                                             .WithSd(sd => sd.WriteSys("config-override.g", "G31 K0 Z0.9\n"))
                                             .StartAsync();
        await bench.Host.ExecuteCodeAsync("M501");

        string written = await SaveAsync(bench);
        Assert.That(written, Does.Contain("G31 K0 P400 X12.0 Y-3.5 Z0.90"), "the loaded trigger height is saved again");
    }

    /// <summary>
    /// M500 P31 writes the Z probe values and M501 loads them back: the trigger height survives the
    /// round trip and overwrites a live change
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 501 runs the G31 M500 wrote; the trigger height is sensors.probes[].triggerHeight</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M501RestoresTheProbe()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, ProbeConfig);
        await SaveAsync(bench, "M500 P31");

        await bench.Host.ExecuteCodeAsync("G31 K0 Z3.5");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.TriggerHeight), Is.EqualTo(3.5).Within(1e-3),
                    "the live trigger height moved before M501");

        await bench.Host.ExecuteCodeAsync("M501");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Sensors.Probes[0]!.TriggerHeight), Is.EqualTo(1.25).Within(1e-3),
                    "M501 restores sensors.probes[0].triggerHeight from config-override.g");
    }

    #endregion

    #region Tool offsets

    /// <summary>Two tools, the first with offsets typed into config.g</summary>
    private static readonly string ToolConfig = "M563 P0 D0\nM563 P1 D0\nG10 P0 X1.235 Y-2.5 Z0.75";

    /// <summary>
    /// Offsets typed into config.g are not written without P10
    /// </summary>
    /// <remarks>RRF truth: GCodes::WriteToolParameters writes a tool only if one of its offsets was probed, or for P10</remarks>
    [Test]
    public async Task M500LeavesConfiguredToolOffsetsOut()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze, ToolConfig);

        string written = await SaveAsync(bench);
        Assert.That(CodeLines(written), Has.None.Match(@"^G10 P\d"), "no tool offset was probed, so none is written");
    }

    /// <summary>
    /// P10 writes every tool's offsets on every axis, to three decimals
    /// </summary>
    /// <remarks>RRF truth: GCodes::WriteToolParameters, "G10 P%d" then " %c%.3f" for each visible axis</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M500P10WritesEveryToolOffset()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze, ToolConfig);

        string[] toolLines = CodeLines(await SaveAsync(bench, "M500 P10")).Where(line => Regex.IsMatch(line, @"^G10 P\d")).ToArray();
        Assert.That(toolLines, Is.EqualTo(new[] { "G10 P0 X1.235 Y-2.500 Z0.750", "G10 P1 X0.000 Y0.000 Z0.000" }),
                    "every tool, in number order, with every axis (RRF GCodes::WriteToolParameters)");
    }

    /// <summary>
    /// A tool offset config-override.g sets is marked probed, and the next M500 writes that axis alone
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes::SetOrReportOffsets passes runningM501 to Tool::SetOffset as byProbing,
    /// which sets the axis in tools[].offsetsProbed; WriteToolParameters then writes that axis
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M501MarksTheToolOffsetsItSetsAsProbed()
    {
        await using Bench bench = await Bench.With(Machines.Xyze, ToolConfig)
                                             .WithSd(sd => sd.WriteSys("config-override.g", "G10 P0 Z0.5\n"))
                                             .StartAsync();

        await bench.Host.ExecuteCodeAsync("M501");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Tools[0]!.OffsetsProbed), Is.EqualTo(1 << 2),
                    "tools[0].offsetsProbed has the Z bit alone");

        string written = await SaveAsync(bench);
        Assert.That(written, Does.Contain("; Probed tool offsets\nG10 P0 Z0.500\n"),
                    "the probed axis of the probed tool, and nothing else (RRF GCodes::WriteToolParameters)");
    }

    /// <summary>
    /// M501 puts saved tool offsets back over changed ones
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 501 runs the G10 M500 P10 wrote</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M501RestoresToolOffsets()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyze, ToolConfig);
        await SaveAsync(bench, "M500 P10");

        await bench.Host.ExecuteCodeAsync("G10 P0 X0 Y0 Z0");
        await bench.Host.ExecuteCodeAsync("M501");
        float[] offsets = await bench.Host.ReadModelAsync(model => model.Tools[0]!.Offsets.Take(3).ToArray());
        Assert.That(offsets, Is.EqualTo(new[] { 1.235f, -2.5f, 0.75f }).Within(1e-3f), "tools[0].offsets are the saved ones");
    }

    #endregion

    #region Workplace coordinates

    /// <summary>
    /// Every one of the nine workplaces is written, a zero one included, on every visible axis
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes::WriteWorkplaceCoordinates writes "G10 L2 P%u" and " %c%.2f" per visible
    /// axis for all NumCoordinateSystems (9). Writing the zero ones is what makes M501 put back a
    /// workplace that was cleared after config.g set it
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M500WritesEveryWorkplace()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);
        await bench.Host.ExecuteCodeAsync("G10 L2 P2 X5 Y6 Z7.125");

        string written = await SaveAsync(bench);
        string[] expected = Enumerable.Range(1, 9)
                                      .Select(workplace => workplace == 2 ? "G10 L2 P2 X5.00 Y6.00 Z7.13" : $"G10 L2 P{workplace} X0.00 Y0.00 Z0.00")
                                      .ToArray();
        Assert.That(written, Does.Contain("; Workplace coordinates\n" + string.Join('\n', expected) + "\n"),
                    "all nine workplaces in order (RRF GCodes::WriteWorkplaceCoordinates)");
    }

    /// <summary>
    /// A hidden axis has no workplace offset written
    /// </summary>
    /// <remarks>RRF truth: GCodes::WriteWorkplaceCoordinates iterates numVisibleAxes</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M500LeavesHiddenAxesOutOfWorkplaces()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, $"M584 U{Expansion.Driver(Machines.DriverBoard, 4)} P3");
        await bench.Host.ExecuteCodeAsync("G10 L2 P2 X5 Y6 Z7");

        string[] workplaceLines = CodeLines(await SaveAsync(bench)).Where(line => line.StartsWith("G10 L2")).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(workplaceLines, Has.Some.EqualTo("G10 L2 P2 X5.00 Y6.00 Z7.00"), "the visible axes are written");
            Assert.That(workplaceLines, Has.None.Contain(" U"), "the hidden U axis is not");
        });
    }

    /// <summary>
    /// M501 applies the workplace offsets in the file, with an empty reply
    /// </summary>
    /// <remarks>
    /// Reference m501-read-stored-parameters: the file holds "G10 L2 P2 X5 Y6 Z7", M501 replies
    /// with nothing and move.axes[*].workplaceOffsets[1] become 5, 6 and 7
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M501AppliesWorkplaceOffsets()
    {
        await using Bench bench = await Bench.With(Machines.Xyz)
                                             .WithSd(sd => sd.WriteSys("config-override.g", "G10 L2 P2 X5 Y6 Z7\n"))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M501");
        float[] offsets = await ReadWorkplaceAsync(bench, 2);
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty, "M501 replies with nothing");
            Assert.That(offsets, Is.EqualTo(new[] { 5f, 6f, 7f }).Within(1e-3f), "move.axes[*].workplaceOffsets[1] are the file's");
        });
    }

    /// <summary>
    /// What M500 writes, M501 reads back: the saved workplace replaces a changed one
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 501 runs the G10 L2 WriteWorkplaceCoordinates wrote</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M501RestoresWhatM500Saved()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);
        await bench.Host.ExecuteCodeAsync("G10 L2 P3 X-1.5 Y2.25 Z10");
        await SaveAsync(bench);

        await bench.Host.ExecuteCodeAsync("G10 L2 P3 X9 Y9 Z9");
        await bench.Host.ExecuteCodeAsync("M501");
        Assert.That(await ReadWorkplaceAsync(bench, 3), Is.EqualTo(new[] { -1.5f, 2.25f, 10f }).Within(1e-3f),
                    "move.axes[*].workplaceOffsets[2] are the saved ones");
    }

    #endregion

    #region Coordinate rotation

    /// <summary>
    /// P68 writes the coordinate rotation, as G69 when there is none
    /// </summary>
    /// <remarks>RRF truth: GCodes::WriteCoordinateRotation, "G69" when g68Angle is 0</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M500P68WritesG69WithoutRotation()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);

        string[] lines = CodeLines(await SaveAsync(bench, "M500 P68"));
        Assert.That(lines[^1], Is.EqualTo("G69"), "the rotation comes last, and there is none");
    }

    /// <summary>
    /// P68 writes a rotation in effect as the G68 that sets it
    /// </summary>
    /// <remarks>RRF truth: GCodes::WriteCoordinateRotation, "G68 X%.3f Y%.3f R%.2f"</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M500P68WritesTheRotation()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);
        await bench.Host.ExecuteCodeAsync("G68 X10 Y20 R30");

        string[] lines = CodeLines(await SaveAsync(bench, "M500 P68"));
        Assert.That(lines[^1], Is.EqualTo("G68 X10.000 Y20.000 R30.00"), "the rotation as its G68");
    }

    /// <summary>
    /// Without P68 the rotation is not written
    /// </summary>
    /// <remarks>RRF truth: WriteConfigOverrideFile calls WriteCoordinateRotation only for P68</remarks>
    [Test]
    public async Task M500LeavesTheRotationOutWithoutP68()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);

        string[] lines = CodeLines(await SaveAsync(bench));
        Assert.That(lines, Has.None.Match(@"^G6[89]\b"), "no G68 or G69");
    }

    /// <summary>
    /// A P value M500 does not know is ignored without a word
    /// </summary>
    /// <remarks>RRF truth: WriteConfigOverrideFile's switch over P ends in "other values are ignored without warning"</remarks>
    [Test]
    public async Task M500IgnoresAnUnknownP()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);

        string reply = await bench.Host.ExecuteCodeAsync("M500 P99");
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.Not.StartWith("Error"), "an unknown P is not refused");
            Assert.That(File.Exists(OverridePath(bench)), Is.True, "the file is written");
        });
    }

    #endregion

    #region Loading the file

    /// <summary>
    /// An M501 in config.g loads the file at startup
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 501 runs config-override.g as a macro from config.g</remarks>
    [Test]
    public async Task M501InConfigGLoadsTheFileAtStartup()
    {
        await using Bench bench = await Bench.With(Machines.Xyz, ProbeConfig, "M501")
                                             .WithSd(sd => sd.WriteSys("config-override.g", "G31 K0 Z0.9\n"))
                                             .StartAsync();

        Assert.That(await ReadTriggerHeightAsync(bench), Is.EqualTo(0.9).Within(1e-3),
                    "the file's trigger height is in place once config.g has finished");
    }

    /// <summary>
    /// M501 with no config-override.g warns that the macro is missing, rather than failing
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 501 calls DoFileMacro with reportMissing, which reports a
    /// warning. Reference error-missing-parameters-system-2: "Warning: Macro file
    /// config-override.g not found"
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M501WarnsWhenTheFileIsMissing()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz);

        string reply = (await bench.Host.ExecuteCodeAsync("M501")).TrimEnd();
        Assert.That(reply, Does.StartWith("Warning").And.EndWith("Macro file config-override.g not found"),
                    "a missing file is a warning (RRF DoFileMacro)");
    }

    /// <summary>
    /// An M501 inside config-override.g does nothing, so the file does not run itself again
    /// </summary>
    /// <remarks>RRF truth: GCodes2.cpp case 501 does nothing while runningM501 is set</remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M501InsideConfigOverrideIsIgnored()
    {
        await using Bench bench = await Bench.With(Machines.Xyz, "global overrideRuns = 0")
                                             .WithSd(sd => sd.WriteSys("config-override.g", "set global.overrideRuns = global.overrideRuns + 1\nM501\n"))
                                             .StartAsync();

        string reply = await bench.Host.ExecuteCodeAsync("M501", timeoutMs: 10_000);
        string runs = await bench.Host.ExecuteCodeAsync("echo global.overrideRuns");
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty, "the nested M501 is passed over without complaint");
            Assert.That(runs.Trim(), Is.EqualTo("1"), "the file ran once");
        });
    }

    /// <summary>
    /// M502 runs config.g with its M501 passed over, so the saved settings are not reapplied
    /// </summary>
    /// <remarks>
    /// RRF truth: GCodes2.cpp case 501 does nothing while runningM502 is set. Reference
    /// m502-revert-to-config-defaults covers M502 itself
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M501IsIgnoredDuringM502()
    {
        await using Bench bench = await Bench.With(Machines.Xyz, ProbeConfig, "M501")
                                             .WithSd(sd => sd.WriteSys("config-override.g", "G31 K0 Z0.9\n"))
                                             .StartAsync();
        Assert.That(await ReadTriggerHeightAsync(bench), Is.EqualTo(0.9).Within(1e-3), "config.g's M501 applied the file at startup");

        await bench.Host.ExecuteCodeAsync("M502");
        Assert.That(await ReadTriggerHeightAsync(bench), Is.EqualTo(1.25).Within(1e-3),
                    "config.g's own trigger height, with its M501 passed over");
    }

    #endregion
}
