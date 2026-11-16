using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;
using static System.FormattableString;
using static SystemTests.Scenarios.Codes.Compensation.XyzSetup;

namespace SystemTests.Scenarios.Codes.Kinematics;

/// <summary>M669, selecting and configuring the kinematics</summary>
/// <remarks>
/// GCodes2.cpp case 669 selects a geometry with K, by RepRapFirmware 3.7's name or its legacy number, then hands the code
/// to the geometry's <c>Configure</c>. A code that selects or changes the geometry clears the homed flags; one that changes
/// the kind of geometry also puts the machine where the new geometry assumes it to be
/// (<c>GCodes::SetInitialAxisAndDrivePositions</c>). A code with no parameters reports the geometry
/// </remarks>
[TestFixture]
[GCode("M669")]
public class M669Tests : BenchFixture
{
    /// <summary>Drivers of <see cref="Machines.DriverBoard"/> that carry U and V where a scenario needs them</summary>
    private const byte UDriver = 4, VDriver = 5;

    /// <summary>The lines that add an axis on <see cref="Machines.DriverBoard"/> to a machine</summary>
    private static string AddAxis(char letter, byte driver) => Invariant($"""
        M569 P{Expansion.Driver(Machines.DriverBoard, driver)} S1
        M584 {letter}{Expansion.Driver(Machines.DriverBoard, driver)}
        M92 {letter}{Machines.XyStepsPerMm}
        M208 {letter}0:{Machines.XyMax}
        """);

    /// <summary>X, Y, Z and U</summary>
    private static readonly ConfigPart UAxis = AddAxis('U', UDriver);

    /// <summary>V, after <see cref="UAxis"/></summary>
    private static readonly ConfigPart VAxis = AddAxis('V', VDriver);

    /// <summary>What RepRapFirmware reports for the identity matrix of a machine with X, Y and Z</summary>
    private const string XyzIdentity = "matrix:\n1.00 0 0\n0 1.00 0\n0 0 1.00";

    /// <summary>Read the selected geometry's object model node</summary>
    private static Task<T> ReadKinematicsAsync<T>(DcsTestHost host) where T : DuetAPI.ObjectModel.Kinematics
        => host.ReadModelAsync(model => (T)model.Move.Kinematics);

    /// <summary>Whether every axis is homed</summary>
    private static Task<bool> AllHomedAsync(DcsTestHost host) => host.ReadModelAsync(model => model.Move.Axes.All(axis => axis.Homed));

    // ---- Selecting a geometry ------------------------------------------------------------------------

    /// <summary>
    /// M669 K also takes the geometry's name as a quoted string, which selects the same geometry as
    /// its number and clears the homed flags in the same way
    /// </summary>
    [Test]
    public async Task M669SelectsAGeometryByName()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        string reply = await host.ExecuteCodeAsync("M669 K\"corexy\"");
        KinematicsName name = await host.ReadModelAsync(model => model.Move.Kinematics.Name);
        bool xHomed = await host.ReadModelAsync(model => model.Move.Axes[0].Homed);
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(name, Is.EqualTo(KinematicsName.CoreXY));
            Assert.That(xHomed, Is.False, "a kinematics change clears the homed flags");
        });
    }

    /// <summary>The names and numbers of every geometry, and the name RepRapFirmware 3.7 reports for it</summary>
    private static readonly object[] Geometries =
    [
        new object[] { "K0", "K\"cartesian\"", KinematicsName.Cartesian, "cartesian" },
        new object[] { "K1", "K\"coreXY\"", KinematicsName.CoreXY, "coreXY" },
        new object[] { "K2", "K\"coreXZ\"", KinematicsName.CoreXZ, "coreXZ" },
        new object[] { "K3", "K\"linearDelta\"", KinematicsName.LinearDelta, "linearDelta" },
        new object[] { "K4", "K\"scara\"", KinematicsName.Scara, "scara" },
        new object[] { "K7", "K\"polar\"", KinematicsName.Polar, "polar" },
        new object[] { "K9", "K\"fiveBarScara\"", KinematicsName.FiveBarScara, "fiveBarScara" },
        new object[] { "K10", "K\"rotaryDelta\"", KinematicsName.RotaryDelta, "rotaryDelta" },
        new object[] { "K11", "K\"markForged\"", KinematicsName.MarkForged, "markForged" },
    ];

    /// <summary>
    /// Every geometry a machine with X, Y and Z can have is selected by its number and by its name, and is reported by
    /// RepRapFirmware 3.7's name, in the object model and in the M669 report
    /// </summary>
    /// <remarks>KinematicsType in Kinematics.h, Kinematics::MatchesLegacyType and Kinematics::GetName</remarks>
    [TestCaseSource(nameof(Geometries))]
    [Category("KnownGap")]
    public async Task M669SelectsEachGeometryByNumberAndByName(string byNumber, string byName, KinematicsName expected, string reported)
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        foreach (string selection in new[] { byNumber, byName })
        {
            await host.ExecuteCodeAsync("M669 K\"cartesian\"");
            string reply = await host.ExecuteCodeAsync($"M669 {selection}");
            KinematicsName name = await host.ReadModelAsync(model => model.Move.Kinematics.Name);
            string report = await host.ExecuteCodeAsync("M669");
            Assert.Multiple(() =>
            {
                Assert.That(reply.Trim(), Is.Empty, $"M669 {selection} succeeds silently");
                Assert.That(name, Is.EqualTo(expected), $"M669 {selection} sets move.kinematics.name");
                Assert.That(KinematicsNameConverter.ToName(name), Is.EqualTo(reported), "the object model spells it as RRF 3.7 does");
                Assert.That(report, Does.StartWith($"Kinematics is {reported}, "), "the report names it as RRF 3.7 does");
            });
        }
    }

    /// <summary>
    /// A K that names no geometry, by number or by name, is an error and leaves the geometry as it was
    /// </summary>
    /// <remarks>RepRapFirmware's reply, GCodes2.cpp case 669: "Unknown kinematics type"</remarks>
    [TestCase("M669 K12")]
    [TestCase("M669 K99")]
    [TestCase("M669 K\"cubic\"")]
    public async Task M669RefusesAKThatNamesNoGeometry(string code)
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K1");
        await host.ExecuteCodeAsync("G92 X0 Y0 Z0");
        string reply = await host.ExecuteCodeAsync(code);
        KinematicsName name = await host.ReadModelAsync(model => model.Move.Kinematics.Name);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error: M669: Unknown kinematics type"));
            Assert.That(name, Is.EqualTo(KinematicsName.CoreXY), "the geometry that was selected stays");
            Assert.That(await AllHomedAsync(host), Is.True, "a refused code does not un-home the machine");
        });
    }

    /// <summary>
    /// Selecting the geometry that is already selected keeps its configuration, but still clears the homed flags
    /// </summary>
    /// <remarks>GCodes2.cpp case 669: seen is set by K whether or not the kinematics changed, and SetKinematics runs only if it did</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669SelectingTheSameGeometryKeepsItsConfigurationAndUnhomes()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K1 Z0:0:2");
        await host.ExecuteCodeAsync("G92 X0 Y0 Z0");
        await host.ExecuteCodeAsync("M669 K1");

        string report = await host.ExecuteCodeAsync("M669");
        Assert.Multiple(async () =>
        {
            Assert.That(report.TrimEnd(), Is.EqualTo("Kinematics is coreXY, no segmentation, modified matrix:\n1.00 1.00 0\n1.00 -1.00 0\n0 0 2.00"),
                        "the modified row survives");
            Assert.That(await AllHomedAsync(host), Is.False, "K clears the homed flags even when it names the same geometry");
        });
    }

    /// <summary>
    /// Selecting a geometry sends nothing to the expansion boards: the geometry is the host's
    /// </summary>
    /// <remarks>The regression references of the M669 cases capture no CAN frames</remarks>
    [Test]
    public async Task M669SendsNothingToTheBoards()
    {
        await using Bench bench = await StartXyzAsync();

        bench.CanMaster.ClearCapture();
        await bench.Host.ExecuteCodeAsync("M669 K1 S50 T0.5");
        Assert.That(bench.CanMaster.SbcPackets(SbcRequest.SendCANMessage), Is.Empty);
    }

    // ---- Matrix geometries ---------------------------------------------------------------------------

    /// <summary>
    /// M669 without parameters reports the geometry, its segmentation and its matrix: a row per axis and a column per motor,
    /// zero written without decimals
    /// </summary>
    /// <remarks>Regression case m669-report-kinematics; CoreKinematics::Configure</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669ReportsTheCartesianMatrix()
    {
        await using Bench bench = await StartXyzAsync();

        string report = await bench.Host.ExecuteCodeAsync("M669");
        Assert.That(report.TrimEnd(), Is.EqualTo($"Kinematics is cartesian, no segmentation, {XyzIdentity}"));
        Assert.That(report, Does.Not.StartWith("Error"));
    }

    /// <summary>
    /// M669 K1 selects CoreXY: the kinematics name changes, the inverse matrix carries the CoreXY
    /// motor coupling, the forward matrix is its inverse, and the homed flags are cleared
    /// </summary>
    /// <remarks>
    /// GCodes2.cpp case 669 (SetKinematics then SetAllAxesNotHomed on a change) and the
    /// CoreKinematics constructor in Movement/Kinematics/CoreKinematics.cpp: for coreXY the
    /// inverse matrix rows are [1,1,0], [1,-1,0], [0,0,1] and Recalc inverts it into the forward
    /// matrix [0.5,0.5,0], [0.5,-0.5,0], [0,0,1]. OM fields (name, forwardMatrix, inverseMatrix)
    /// from the CoreKinematics object model tables in the same file
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669SelectsCoreXY()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K1");
        KinematicsName name = await host.ReadModelAsync(model => model.Move.Kinematics.Name);
        bool xHomed = await host.ReadModelAsync(model => model.Move.Axes[0].Homed);
        Assert.Multiple(() =>
        {
            Assert.That(name, Is.EqualTo(KinematicsName.CoreXY), "M669 K1 sets move.kinematics.name (RRF CoreKinematics::GetName)");
            Assert.That(xHomed, Is.False, "a kinematics change clears the homed flags (RRF GCodes2.cpp case 669)");
        });

        float[][] inverse = await host.ReadModelAsync(model => ((CoreKinematics)model.Move.Kinematics).InverseMatrix.Select(row => row.ToArray()).ToArray());
        float[][] forward = await host.ReadModelAsync(model => ((CoreKinematics)model.Move.Kinematics).ForwardMatrix.Select(row => row.ToArray()).ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(inverse, Is.EqualTo(new[] { new[] { 1f, 1f, 0f }, new[] { 1f, -1f, 0f }, new[] { 0f, 0f, 1f } }),
                        "move.kinematics.inverseMatrix has one row per axis and one column per motor");
            Assert.That(forward, Is.EqualTo(new[] { new[] { 0.5f, 0.5f, 0f }, new[] { 0.5f, -0.5f, 0f }, new[] { 0f, 0f, 1f } }),
                        "move.kinematics.forwardMatrix is the inverse of the inverse matrix");
        });

        string report = await host.ExecuteCodeAsync("M669");
        Assert.That(report.TrimEnd(), Is.EqualTo("Kinematics is coreXY, no segmentation, matrix:\n1.00 1.00 0\n1.00 -1.00 0\n0 0 1.00"),
                    "regression case m669-kinematics-corexy");
    }

    /// <summary>The default matrices of the named geometries a machine with X, Y and Z can have, as RRF reports them</summary>
    private static readonly object[] NamedMatrices =
    [
        new object[] { "K2", "Kinematics is coreXZ, no segmentation, matrix:\n1.00 0 1.00\n0 1.00 0\n3.00 0 -3.00" },
        new object[] { "K11", "Kinematics is markForged, no segmentation, matrix:\n1.00 0 0\n-1.00 1.00 0\n0 0 1.00" },
    ];

    /// <summary>CoreXZ assumes a 3:1 reduction on Z, and MarkForged couples Y to X</summary>
    /// <remarks>Regression cases m669-kinematics-corexz and m669-kinematics-markforged; the CoreKinematics constructor</remarks>
    [TestCaseSource(nameof(NamedMatrices))]
    [Category("KnownGap")]
    public async Task M669ReportsTheNamedMatrix(string selection, string expected)
    {
        await using Bench bench = await StartXyzAsync();

        await bench.Host.ExecuteCodeAsync($"M669 {selection}");
        Assert.That((await bench.Host.ExecuteCodeAsync("M669")).TrimEnd(), Is.EqualTo(expected));
    }

    /// <summary>
    /// An axis letter replaces that axis' row of the matrix, its values being the motors in axis order and the motors it
    /// does not reach zero. The report says the matrix is modified once a value differs from the named geometry's, the
    /// object model carries the new rows, and the homed flags are cleared even when nothing changed
    /// </summary>
    /// <remarks>Regression cases m669-kinematics-corexy and m669-kinematics-corexz; CoreKinematics::Configure</remarks>
    [TestCase("K1", "X1:1:0 Y-1:1:0 Z0:0:2", "Kinematics is coreXY, no segmentation, modified matrix:\n1.00 1.00 0\n-1.00 1.00 0\n0 0 2.00")]
    [TestCase("K2", "Z1:0:-1", "Kinematics is coreXZ, no segmentation, modified matrix:\n1.00 0 1.00\n0 1.00 0\n1.00 0 -1.00")]
    [TestCase("K0", "X1:0:1 Y0:1:0 Z1:0:-1", "Kinematics is cartesian, no segmentation, modified matrix:\n1.00 0 1.00\n0 1.00 0\n1.00 0 -1.00")]
    [TestCase("K0", "Z0:1:1", "Kinematics is cartesian, no segmentation, modified matrix:\n1.00 0 0\n0 1.00 0\n0 1.00 1.00")]
    [TestCase("K1", "X1:1 Y1:-1", "Kinematics is coreXY, no segmentation, matrix:\n1.00 1.00 0\n1.00 -1.00 0\n0 0 1.00")]
    [Category("KnownGap")]
    public async Task M669RowsModifyTheMatrix(string selection, string rows, string expected)
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync($"M669 {selection}");
        await host.ExecuteCodeAsync("G92 X0 Y0 Z0");
        string reply = await host.ExecuteCodeAsync($"M669 {rows}");
        string report = await host.ExecuteCodeAsync("M669");
        float[][] inverse = await host.ReadModelAsync(model => ((CoreKinematics)model.Move.Kinematics).InverseMatrix.Select(row => row.ToArray()).ToArray());

        string[] reportedRows = report.TrimEnd().Split('\n')[1..];
        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(report.TrimEnd(), Is.EqualTo(expected));
            Assert.That(inverse.Select(row => string.Join(' ', row.Select(value => value == 0 ? "0" : value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))),
                        Is.EqualTo(reportedRows), "move.kinematics.inverseMatrix holds what is reported");
            Assert.That(await AllHomedAsync(host), Is.False, "a row changes the geometry, so the axes need homing again");
        });
    }

    /// <summary>
    /// A row with more values than the machine has motors is refused, and the matrix stays as it was
    /// </summary>
    /// <remarks>GCodeBuffer::GetFloatArray: "array too long"</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669RefusesARowLongerThanTheMachineHasMotors()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K1");
        string reply = await host.ExecuteCodeAsync("M669 X1:1:0:0");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error: ").And.Contain("array too long"));
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(), Does.EndWith(" matrix:\n1.00 1.00 0\n1.00 -1.00 0\n0 0 1.00"));
        });
    }

    /// <summary>
    /// A matrix that cannot be inverted is refused with RepRapFirmware's message, and the geometry that was there stays
    /// </summary>
    /// <remarks>
    /// CoreKinematics::Recalc reports "Invalid kinematics matrix" and carries on with the singular matrix; DSF refuses it
    /// (rrf-differences.md, Core matrices)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669RefusesAMatrixThatCannotBeInverted()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K1");
        await host.ExecuteCodeAsync("G92 X0 Y0 Z0");
        string reply = await host.ExecuteCodeAsync("M669 X1:1 Y1:1");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error: ").And.Contain("Invalid kinematics matrix"));
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(), Does.EndWith(", matrix:\n1.00 1.00 0\n1.00 -1.00 0\n0 0 1.00"));
            Assert.That(await AllHomedAsync(host), Is.True, "a refused code leaves the axes homed");
        });
    }

    /// <summary>
    /// A row for a letter the machine has no axis for is refused, and nothing of the code is applied
    /// </summary>
    /// <remarks>RepRapFirmware ignores the letter; DSF refuses it (rrf-differences.md, Core matrices)</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669RefusesARowForAMissingAxis()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K1");
        string reply = await host.ExecuteCodeAsync("M669 Z0:0:2 A1:0:0");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error: ").And.Contain("The machine has no A axis"));
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(), Does.EndWith(", matrix:\n1.00 1.00 0\n1.00 -1.00 0\n0 0 1.00"),
                        "the Z row given with it is not applied either");
        });
    }

    /// <summary>
    /// CoreXYUV couples U and V as a second CoreXY pair, and its U and V rows can be changed like any other
    /// </summary>
    /// <remarks>Regression case m669-corexyuv-motor-coefficients</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669ConfiguresCoreXYUV()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, UAxis, VAxis);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K8");
        Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                    Is.EqualTo("Kinematics is coreXYUV, no segmentation, matrix:\n1.00 1.00 0 0 0\n1.00 -1.00 0 0 0\n0 0 1.00 0 0\n0 0 0 1.00 1.00\n0 0 0 1.00 -1.00"));

        string reply = await host.ExecuteCodeAsync("M669 K8 U0:0:0:1:-1 V0:0:0:1:1");
        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                        Is.EqualTo("Kinematics is coreXYUV, no segmentation, modified matrix:\n1.00 1.00 0 0 0\n1.00 -1.00 0 0 0\n0 0 1.00 0 0\n0 0 0 1.00 -1.00\n0 0 0 1.00 1.00"));
        });
    }

    /// <summary>
    /// CoreXYU drives U with the U and V motors, which Y drives as well
    /// </summary>
    /// <remarks>The CoreKinematics constructor, case coreXYU</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669SelectsCoreXYU()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, UAxis, VAxis);

        await bench.Host.ExecuteCodeAsync("M669 K5");
        Assert.That((await bench.Host.ExecuteCodeAsync("M669")).TrimEnd(),
                    Is.EqualTo("Kinematics is coreXYU, no segmentation, matrix:\n1.00 1.00 0 0 0\n1.00 -1.00 0 1.00 -1.00\n0 0 1.00 0 0\n0 0 0 1.00 1.00\n0 0 0 0 1.00"));
    }

    /// <summary>
    /// CoreXYU on a machine without V is refused, because V's motor is half of what drives U
    /// </summary>
    /// <remarks>
    /// Regression case m669-kinematics-corexyu: RepRapFirmware accepts it, and the V column of the Y row has no motor to
    /// act on. DSF needs the axes a geometry names (rrf-differences.md section 5.1)
    /// </remarks>
    [Test]
    public async Task M669RefusesCoreXYUWithoutAVAxis()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, UAxis);
        DcsTestHost host = bench.Host;

        string reply = await host.ExecuteCodeAsync("M669 K5");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error: ").And.Contain("no V axis"));
            Assert.That(await host.ReadModelAsync(model => model.Move.Kinematics.Name), Is.EqualTo(KinematicsName.Cartesian));
        });
    }

    /// <summary>
    /// The report has a row for each visible axis only, but a column for every motor, hidden ones included
    /// </summary>
    /// <remarks>CoreKinematics::Configure: rows to numVisibleAxes, columns to numTotalAxes</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669ReportsOnlyTheVisibleAxesRows()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, UAxis, "M584 P3");

        Assert.That((await bench.Host.ExecuteCodeAsync("M669")).TrimEnd(),
                    Is.EqualTo("Kinematics is cartesian, no segmentation, matrix:\n1.00 0 0 0\n0 1.00 0 0\n0 0 1.00 0"));
    }

    // ---- Segmentation --------------------------------------------------------------------------------

    /// <summary>
    /// S and T turn segmentation on for any geometry, are reported, and appear in move.kinematics.segmentation. They do not
    /// change the geometry, so on their own they leave the axes homed
    /// </summary>
    /// <remarks>
    /// Regression case m669-segmentation; Kinematics::TryConfigureSegmentation. CoreKinematics::Configure returns only
    /// whether K or a row was seen, so GCodes2.cpp case 669 does not clear the homed flags for S and T alone
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669ConfiguresSegmentation()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        string reply = await host.ExecuteCodeAsync("M669 S200 T0.2");
        MoveSegmentation? segmentation = await host.ReadModelAsync(model => model.Move.Kinematics.Segmentation);
        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(segmentation?.SegmentsPerSec, Is.EqualTo(200f), "move.kinematics.segmentation.segmentsPerSec");
            Assert.That(segmentation?.MinSegLength, Is.EqualTo(0.2f), "move.kinematics.segmentation.minSegLength");
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                        Is.EqualTo($"Kinematics is cartesian, 200 segments/sec, min. segment length 0.20mm, {XyzIdentity}"));
            Assert.That(await AllHomedAsync(host), Is.True, "segmentation does not change the geometry");
        });

        await host.ExecuteCodeAsync("M669 T0");
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Move.Kinematics.Segmentation), Is.Null,
                        "T0 turns segmentation off, and move.kinematics.segmentation goes");
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(), Is.EqualTo($"Kinematics is cartesian, no segmentation, {XyzIdentity}"));
        });
    }

    /// <summary>
    /// Selecting a geometry with K together with S and T segments it, and K clears the homed flags
    /// </summary>
    /// <remarks>Regression case m669-segmentation: M669 K0 S200 T0.2</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669SelectsAndSegmentsInOneCode()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K0 S200 T0.2");
        Assert.Multiple(async () =>
        {
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                        Is.EqualTo($"Kinematics is cartesian, 200 segments/sec, min. segment length 0.20mm, {XyzIdentity}"));
            Assert.That(await AllHomedAsync(host), Is.False);
        });
    }

    // ---- Changing the kind of geometry ---------------------------------------------------------------

    /// <summary>
    /// A different kind of geometry starts from a fresh object model node, which drops the M671 leadscrews and puts the
    /// tilt correction back to RepRapFirmware's defaults
    /// </summary>
    /// <remarks>
    /// Move::SetKinematics replaces the Kinematics object; ZLeadscrewKinematics's constructor sets correctionFactor 1,
    /// maxCorrection 1 and screwPitch M3ScrewPitch (0.5)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669ResetsTheTiltCorrectionWithTheGeometry()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M671 X10:190 Y5:195 S2.5 P0.7 F1.2");
        await host.ExecuteCodeAsync("M669 K0");
        Assert.That(await host.ReadModelAsync(model => ((ZLeadscrewKinematics)model.Move.Kinematics).TiltCorrection.ScrewX.Count), Is.EqualTo(2),
                    "selecting the geometry already selected keeps the leadscrews");

        await host.ExecuteCodeAsync("M669 K1");
        TiltCorrection tilt = await host.ReadModelAsync(model => ((ZLeadscrewKinematics)model.Move.Kinematics).TiltCorrection);
        Assert.Multiple(() =>
        {
            Assert.That(tilt.ScrewX, Is.Empty);
            Assert.That(tilt.ScrewY, Is.Empty);
            Assert.That(tilt.CorrectionFactor, Is.EqualTo(1.0f));
            Assert.That(tilt.MaxCorrection, Is.EqualTo(1.0f));
            Assert.That(tilt.ScrewPitch, Is.EqualTo(0.5f));
        });
    }

    /// <summary>
    /// Changing the kind of geometry puts the machine where the new geometry assumes it to be, brought within its reach
    /// </summary>
    /// <remarks>
    /// GCodes::SetInitialAxisAndDrivePositions: Kinematics::GetAssumedInitialPosition, then LimitPosition without the M208
    /// limits. Regression cases m669-kinematics-polar (X 30, the minimum radius), m669-kinematics-scara (X 247.25, the
    /// reach of both arms less the bed offset) and m669-kinematics-rotary-delta (Z 117.712, the arms horizontal)
    /// </remarks>
    [TestCase("M669 K7 A400 F600 H50 R30:150 S100 T0.2", 30.0, 0.0, 0.0)]
    [TestCase("M669 K4 P300 D250 A-90:90 B-135:135 C0:0:0 S100 X300 Y0", 247.25, 0.0, 0.0)]
    [TestCase("M669 K10", 0.0, 0.0, 117.712)]
    [Category("KnownGap")]
    public async Task M669PutsTheMachineWhereTheNewGeometryAssumesItIs(string code, double x, double y, double z)
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("G92 X10 Y20 Z5");
        await host.ExecuteCodeAsync(code);
        Assert.Multiple(async () =>
        {
            Assert.That(await host.ReadModelAsync(model => model.Move.Axes[0].UserPosition), Is.EqualTo(x).Within(0.1));
            Assert.That(await host.ReadModelAsync(model => model.Move.Axes[1].UserPosition), Is.EqualTo(y).Within(0.1));
            Assert.That(await host.ReadModelAsync(model => model.Move.Axes[2].UserPosition), Is.EqualTo(z).Within(0.01));
        });
    }

    /// <summary>
    /// Configuring the geometry already selected leaves the machine where it is
    /// </summary>
    /// <remarks>GCodes2.cpp case 669 calls SetInitialAxisAndDrivePositions only when the kinematics changed</remarks>
    [Test]
    public async Task M669ConfiguringTheSameGeometryKeepsThePosition()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K1");
        await host.ExecuteCodeAsync("G92 X10 Y20 Z5");
        await host.ExecuteCodeAsync("M669 K1 Z0:0:2");
        Assert.That(await host.ReadModelAsync(model => model.Move.Axes[0].UserPosition), Is.EqualTo(10.0f).Within(0.01));
    }

    // ---- Serial SCARA --------------------------------------------------------------------------------

    /// <summary>
    /// M669 K4 configures a serial SCARA: arm lengths, joint limits, crosstalk and bed origin, reported and in the object
    /// model
    /// </summary>
    /// <remarks>Regression case m669-kinematics-scara; ScaraKinematics::Configure and its object model table</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669ConfiguresASerialScara()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K4 P300 D250 A-90:90 B-135:135 C0.1:0.2:0.3 S100 X300 Y10 R20");
        ScaraKinematics scara = await ReadKinematicsAsync<ScaraKinematics>(host);
        Assert.Multiple(async () =>
        {
            Assert.That(scara.ProximalLength, Is.EqualTo(300f));
            Assert.That(scara.DistalLength, Is.EqualTo(250f));
            Assert.That(scara.ThetaLimits, Is.EqualTo(new[] { -90f, 90f }));
            Assert.That(scara.PsiLimits, Is.EqualTo(new[] { -135f, 135f }));
            Assert.That(scara.Crosstalk, Is.EqualTo(new[] { 0.1f, 0.2f, 0.3f }));
            Assert.That(scara.XOffset, Is.EqualTo(300f));
            Assert.That(scara.YOffset, Is.EqualTo(10f));
            Assert.That(scara.MinRadius, Is.EqualTo(20f));
            Assert.That(scara.Segmentation?.SegmentsPerSec, Is.EqualTo(100f));
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                        Is.EqualTo("Kinematics is scara, 100 segments/sec, min. segment length 0.20mm, proximal arm 300.00mm range -90.0 to 90.0°, distal arm 250.00mm range -135.0 to 135.0°, crosstalk 0.1:0.2:0.3, bed origin (300.0, 10.0)"));
        });
    }

    /// <summary>A pair of joint limits needs both values</summary>
    /// <remarks>GCodeBuffer::TryGetFloatArray: "Wrong number of values in array, expected 2"</remarks>
    [Test]
    public async Task M669RefusesHalfAScaraLimit()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K4");
        string reply = await host.ExecuteCodeAsync("M669 A-90");
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error: ").And.Contain("Wrong number of values in array, expected 2"));
            Assert.That((await ReadKinematicsAsync<ScaraKinematics>(host)).ThetaLimits, Is.EqualTo(new[] { -90f, 90f }));
        });
    }

    // ---- Polar ---------------------------------------------------------------------------------------

    /// <summary>
    /// M669 K7 configures a polar machine: radius limits, homed radius and the turntable's limits, reported in the units
    /// they were given in
    /// </summary>
    /// <remarks>
    /// Regression case m669-kinematics-polar; PolarKinematics::Configure. Its object model reports the turntable limits
    /// in internal units (ttSpeedMax 0.0008, ttAccMax 7.1e-10); DSF reports what was configured
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669ConfiguresAPolarMachine()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K7 A400 F600 H50 R30:150 S100 T0.2");
        PolarKinematics polar = await ReadKinematicsAsync<PolarKinematics>(host);
        Assert.Multiple(async () =>
        {
            Assert.That(polar.RadiusMin, Is.EqualTo(30f));
            Assert.That(polar.RadiusMax, Is.EqualTo(150f));
            Assert.That(polar.RadiusHomed, Is.EqualTo(50f));
            Assert.That(polar.TTAccMax, Is.EqualTo(400f));
            Assert.That(polar.TTSpeedMax, Is.EqualTo(600f));
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                        Is.EqualTo("Kinematics is polar, 100 segments/sec, min. segment length 0.20mm, radius 30.0 to 150.0mm, homed radius 50.0mm, max table acc. 400.0deg/sec^2, max table speed 600.0deg/sec"));
        });
    }

    /// <summary>
    /// One R value is the maximum radius with the minimum at zero, and R puts the homed radius at the minimum unless H
    /// gives it
    /// </summary>
    /// <remarks>PolarKinematics::Configure</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669PolarRadiusLimits()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K7 R30:150 H50");
        await host.ExecuteCodeAsync("M669 R120");
        PolarKinematics single = await ReadKinematicsAsync<PolarKinematics>(host);
        Assert.Multiple(() =>
        {
            Assert.That(single.RadiusMin, Is.Zero, "one value leaves no minimum");
            Assert.That(single.RadiusMax, Is.EqualTo(120f));
            Assert.That(single.RadiusHomed, Is.Zero, "the homed radius follows the minimum");
        });

        await host.ExecuteCodeAsync("M669 R40:160");
        Assert.That((await ReadKinematicsAsync<PolarKinematics>(host)).RadiusHomed, Is.EqualTo(40f));
    }

    /// <summary>
    /// The turntable's limits do not change the geometry, so they leave the axes homed; the radii do
    /// </summary>
    /// <remarks>PolarKinematics::Configure returns seen only for R and H</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669PolarTurntableLimitsDoNotUnhome()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K7");
        await host.ExecuteCodeAsync("G92 X60 Y0 Z0");
        await host.ExecuteCodeAsync("M669 A200 F300");
        Assert.That(await AllHomedAsync(host), Is.True);

        await host.ExecuteCodeAsync("M669 H55");
        Assert.That(await AllHomedAsync(host), Is.False);
    }

    // ---- Five-bar parallel SCARA ---------------------------------------------------------------------

    /// <summary>
    /// M669 K9 configures a five-bar SCARA from X, Y, P and D, the rest at their defaults, and reports every parameter
    /// </summary>
    /// <remarks>
    /// Regression case m669-kinematics-five-bar-scara; FiveBarScaraKinematics::Configure. RepRapFirmware reports only the
    /// name in the object model; DSF keeps the configuration there (rrf-differences.md section 3)
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669ConfiguresAFiveBarScara()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        string reply = await host.ExecuteCodeAsync("M669 K9 X-25:25 Y0:0 P100:100 D100:100");
        FiveBarScaraKinematics fiveBar = await ReadKinematicsAsync<FiveBarScaraKinematics>(host);
        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(fiveBar.XOrigins, Is.EqualTo(new[] { -25f, 25f }));
            Assert.That(fiveBar.YOrigins, Is.EqualTo(new[] { 0f, 0f }));
            Assert.That(fiveBar.ProximalLengths, Is.EqualTo(new[] { 100f, 100f }));
            Assert.That(fiveBar.DistalLengths, Is.EqualTo(new[] { 100f, 100f }));
            Assert.That(fiveBar.CantileverLengths, Is.EqualTo(new[] { 0f, 0f }));
            Assert.That(fiveBar.WorkMode, Is.EqualTo(1));
            Assert.That(fiveBar.HomingAngles, Is.EqualTo(new[] { 20f, 10f }));
            Assert.That(fiveBar.HeadAngleLimits, Is.EqualTo(new[] { 15f, 165f }));
            Assert.That(fiveBar.ProximalDistalAngleLimits, Is.EqualTo(new[] { 0f, 360f, 0f, 360f }));
            Assert.That(fiveBar.ActuatorAngleLimits, Is.EqualTo(new[] { 10f, 170f, 10f, 170f }));
            Assert.That(fiveBar.PrintArea, Is.Empty);
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                        Is.EqualTo("Kinematics is fiveBarScara, 100 segments/sec, min. segment length 0.20mm, actuator origins (-25.0, 0.0) and (25.0, 0.0), proximal arms 100.0:100.0mm, distal arms 100.0:100.0mm, cantilevers 0.0:0.0mm, work mode 1, homing angles 20.0:10.0, head angle 15.0 to 165.0, proximal-distal angles 0.0 to 360.0 and 0.0 to 360.0, actuator angles 10.0 to 170.0 and 10.0 to 170.0"));
        });
    }

    /// <summary>
    /// Every optional five-bar parameter, and the homing angles each work mode defaults to
    /// </summary>
    /// <remarks>FiveBarScaraKinematics::Configure</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669ConfiguresEveryFiveBarParameter()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K9 X-50:50 Y0:5 P100:110 D120:130:10:0 L2 B100:30 A10:170:1:350:5:355 C5:175:6:174 Z-50:50:50:150");
        Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                    Is.EqualTo("Kinematics is fiveBarScara, 100 segments/sec, min. segment length 0.20mm, actuator origins (-50.0, 0.0) and (50.0, 5.0), proximal arms 100.0:110.0mm, distal arms 120.0:130.0mm, cantilevers 10.0:0.0mm, work mode 2, homing angles 100.0:30.0, head angle 10.0 to 170.0, proximal-distal angles 1.0 to 350.0 and 5.0 to 355.0, actuator angles 5.0 to 175.0 and 6.0 to 174.0, print area (-50.0, 50.0) to (50.0, 150.0)"));

        // Omitted parameters go back to their defaults, the homing angles to the work mode's
        await host.ExecuteCodeAsync("M669 X-50:50 Y0:5 P100:110 D120:130 L4");
        FiveBarScaraKinematics fiveBar = await ReadKinematicsAsync<FiveBarScaraKinematics>(host);
        Assert.Multiple(() =>
        {
            Assert.That(fiveBar.WorkMode, Is.EqualTo(4));
            Assert.That(fiveBar.HomingAngles, Is.EqualTo(new[] { 110f, 100f }));
            Assert.That(fiveBar.CantileverLengths, Is.EqualTo(new[] { 0f, 0f }));
            Assert.That(fiveBar.HeadAngleLimits, Is.EqualTo(new[] { 15f, 165f }));
            Assert.That(fiveBar.PrintArea, Is.Empty);
        });
    }

    /// <summary>
    /// A five-bar SCARA M669 has not described is all zeros, and S and T alone neither describe it nor report it
    /// </summary>
    /// <remarks>FiveBarScaraKinematics's member defaults, and its Configure returning before MustSee when none of XYPDLBACZ is given</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669UnconfiguredFiveBarScara()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K9");
        Assert.Multiple(async () =>
        {
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                        Is.EqualTo("Kinematics is fiveBarScara, 100 segments/sec, min. segment length 0.20mm, actuator origins (0.0, 0.0) and (0.0, 0.0), proximal arms 0.0:0.0mm, distal arms 0.0:0.0mm, cantilevers 0.0:0.0mm, work mode 1, homing angles 0.0:0.0, head angle 0.0 to 0.0, proximal-distal angles 0.0 to 0.0 and 0.0 to 0.0, actuator angles 0.0 to 0.0 and 0.0 to 0.0"));
            Assert.That((await host.ExecuteCodeAsync("M669 S50 T0.5")).Trim(), Is.Empty);
            Assert.That((await ReadKinematicsAsync<FiveBarScaraKinematics>(host)).Segmentation?.SegmentsPerSec, Is.EqualTo(50f));
        });
    }

    /// <summary>The five-bar SCARA's refusals, each leaving the geometry it had and the axes homed</summary>
    /// <remarks>FiveBarScaraKinematics::Configure: MustSee X, Y, P and D, then the L and D checks</remarks>
    [TestCase("M669 K9 X-25:25 Y0:0 P100:100", "missing parameter 'D'")]
    [TestCase("M669 K9 Y0:0 P100:100 D100:100", "missing parameter 'X'")]
    [TestCase("M669 K9 X-25:25 Y0:0 P100:100 D100:100 L3", "L parameter must be 1, 2 or 4")]
    [TestCase("M669 K9 X-25:25 Y0:0 P100:100 D100:100:5", "D parameter must have 2 or 4 values")]
    [TestCase("M669 K9 X-25 Y0:0 P100:100 D100:100", "Wrong number of values in array, expected 2")]
    [Category("KnownGap")]
    public async Task M669RefusesAnIncompleteFiveBarScara(string code, string error)
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        string reply = await host.ExecuteCodeAsync(code);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("Error: ").And.Contain(error));
            Assert.That(await host.ReadModelAsync(model => model.Move.Kinematics.Name), Is.EqualTo(KinematicsName.Cartesian),
                        "nothing of a refused code is applied");
            Assert.That(await AllHomedAsync(host), Is.True);
        });
    }

    // ---- Rotary delta --------------------------------------------------------------------------------

    /// <summary>A rotary delta M669 has not configured reports RepRapFirmware's defaults</summary>
    /// <remarks>Regression case m669-kinematics-rotary-delta; RotaryDeltaKinematics::Init and Configure</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669ReportsARotaryDelta()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K10");
        Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                    Is.EqualTo("Kinematics is rotaryDelta, 100 segments/sec, min. segment length 0.20mm, arms (100.000,100.00,100.000)mm, rods (200.000,200.000,200.000)mm, bearingHeights (250.000,250.00,250.000)mm, arm movement -45.0 to 45.0°, delta radius 50.000, bed radius 80.0, angle corrections (0.000,0.000,0.000)°"));
    }

    /// <summary>
    /// M669 configures a rotary delta: one value for all three arms or one each, the arm angle limits, the radii and the
    /// angle corrections; B also sets the X and Y limits. M666 sets each arm's endstop adjustment
    /// </summary>
    /// <remarks>RotaryDeltaKinematics::Configure, cases 669 and 666</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669ConfiguresARotaryDelta()
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K10 U90:91:92 L190 H240:241:242 A-40:50 R55 B85 X1 Y2 Z3");
        string m666Reply = await host.ExecuteCodeAsync("M666 X0.5 Y-0.5 Z0.25");
        RotaryDeltaKinematics rotary = await ReadKinematicsAsync<RotaryDeltaKinematics>(host);
        Assert.Multiple(async () =>
        {
            Assert.That(m666Reply.Trim(), Is.Empty);
            Assert.That(rotary.ArmLengths, Is.EqualTo(new[] { 90f, 91f, 92f }));
            Assert.That(rotary.RodLengths, Is.EqualTo(new[] { 190f, 190f, 190f }), "one value is all three");
            Assert.That(rotary.BearingHeights, Is.EqualTo(new[] { 240f, 241f, 242f }));
            Assert.That(rotary.ArmAngleLimits, Is.EqualTo(new[] { -40f, 50f }));
            Assert.That(rotary.DeltaRadius, Is.EqualTo(55f));
            Assert.That(rotary.PrintRadius, Is.EqualTo(85f));
            Assert.That(rotary.AngleCorrections, Is.EqualTo(new[] { 1f, 2f, 3f }));
            Assert.That(rotary.EndstopAdjustments, Is.EqualTo(new[] { 0.5f, -0.5f, 0.25f }));
            Assert.That(await host.ReadModelAsync(model => (model.Move.Axes[0].Min, model.Move.Axes[0].Max, model.Move.Axes[1].Min, model.Move.Axes[1].Max)),
                        Is.EqualTo((-85f, 85f, -85f, 85f)), "B sets the X and Y limits to the printable radius");
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                        Is.EqualTo("Kinematics is rotaryDelta, 100 segments/sec, min. segment length 0.20mm, arms (90.000,91.00,92.000)mm, rods (190.000,190.000,190.000)mm, bearingHeights (240.000,241.00,242.000)mm, arm movement -40.0 to 50.0°, delta radius 55.000, bed radius 85.0, angle corrections (1.000,2.000,3.000)°"));
            Assert.That((await host.ExecuteCodeAsync("M666")).TrimEnd(), Is.EqualTo("Endstop adjustments X0.50 Y-0.50 Z0.25°"));
        });
    }

    /// <summary>The rotary delta's refusals, each leaving the geometry as it was</summary>
    /// <remarks>RotaryDeltaKinematics::Configure: TryGetFloatArray with padding for U, L and H, without for A; GetPositiveFValue for B</remarks>
    [TestCase("M669 U90:91", "Wrong number of values in array, expected 3")]
    [TestCase("M669 A-40", "Wrong number of values in array, expected 2")]
    [TestCase("M669 B0", "greater than zero")]
    [Category("KnownGap")]
    public async Task M669RefusesABadRotaryDeltaParameter(string code, string error)
    {
        await using Bench bench = await StartXyzAsync();
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K10");
        string reply = await host.ExecuteCodeAsync(code);
        RotaryDeltaKinematics rotary = await ReadKinematicsAsync<RotaryDeltaKinematics>(host);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error: ").And.Contain(error));
            Assert.That(rotary.ArmLengths, Is.EqualTo(new[] { 100f, 100f, 100f }));
            Assert.That(rotary.ArmAngleLimits, Is.EqualTo(new[] { -45f, 45f }));
            Assert.That(rotary.PrintRadius, Is.EqualTo(80f));
        });
    }

    // ---- Linear delta --------------------------------------------------------------------------------

    /// <summary>
    /// On a linear delta, X and Y place the towers after the third, one value each; any other number of values is refused
    /// </summary>
    /// <remarks>LinearDeltaKinematics::Configure, case 669: "Wrong number of X values"</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669PlacesALinearDeltasExtraTowers()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, UAxis);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M665 L250 R100 H200 B80");
        string threeTowers = await host.ExecuteCodeAsync("M669 X10");

        await host.ExecuteCodeAsync("M665 L250:250:250:260");
        string reply = await host.ExecuteCodeAsync("M669 X50 Y-60");
        string tooMany = await host.ExecuteCodeAsync("M669 Y1:2");
        DeltaKinematics delta = await ReadKinematicsAsync<DeltaKinematics>(host);
        Assert.Multiple(async () =>
        {
            Assert.That(threeTowers, Does.StartWith("Error: ").And.Contain("Wrong number of X values"), "a three-tower delta has no extra towers");
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(tooMany, Does.StartWith("Error: ").And.Contain("Wrong number of Y values"));
            Assert.That(delta.Towers, Has.Count.EqualTo(4), "M665 L gives one rod length per tower");
            Assert.That(delta.Towers[3].Diagonal, Is.EqualTo(260f));
            Assert.That(delta.Towers[3].XPos, Is.EqualTo(50f), "move.kinematics.towers[3].xPos");
            Assert.That(delta.Towers[3].YPos, Is.EqualTo(-60f), "move.kinematics.towers[3].yPos");
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                        Is.EqualTo("Kinematics is linearDelta, 100 segments/sec, min. segment length 0.20mm"));
        });
    }

    // ---- Hangprinter ---------------------------------------------------------------------------------

    /// <summary>
    /// M669 K6 selects a hangprinter with RepRapFirmware's default anchors, and reports each anchor and the print radius
    /// </summary>
    /// <remarks>
    /// HangprinterKinematics::Init and Configure. Regression case m669-kinematics-hangprinter is marked known bad: on the
    /// bench RepRapFirmware restarts after M669 K6
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669SelectsAHangprinter()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, UAxis);
        DcsTestHost host = bench.Host;

        string reply = await host.ExecuteCodeAsync("M669 K6");
        HangprinterKinematics hangprinter = await ReadKinematicsAsync<HangprinterKinematics>(host);
        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(hangprinter.Anchors, Is.EqualTo(new[] { new[] { 0f, -2000f, -100f }, new[] { 2000f, 1000f, -100f }, new[] { -2000f, 1000f, -100f }, new[] { 0f, 0f, 3000f } }));
            Assert.That(hangprinter.PrintRadius, Is.EqualTo(1500f));
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(),
                        Is.EqualTo("Kinematics is hangprinter, 100 segments/sec, min. segment length 0.20mm\nA:0.00, -2000.00, -100.00\nB:2000.00, 1000.00, -100.00\nC:-2000.00, 1000.00, -100.00\nD:0.00, 0.00, 3000.00\nP:Print radius: 1500.0"));
        });
    }

    /// <summary>
    /// N sets the number of anchors, each anchor's letter its position and P the print radius. A further anchor needs the
    /// axis that drives it
    /// </summary>
    /// <remarks>HangprinterKinematics::Configure, case 669; ANCHOR_CHARS "ABCDIJLO"</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task M669ConfiguresAHangprinter()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, UAxis, VAxis);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K6");
        string reply = await host.ExecuteCodeAsync("M669 N5 A0:-1500:-90 I10:20:30 P1200");
        HangprinterKinematics hangprinter = await ReadKinematicsAsync<HangprinterKinematics>(host);
        Assert.Multiple(async () =>
        {
            Assert.That(reply.Trim(), Is.Empty);
            Assert.That(hangprinter.Anchors, Has.Count.EqualTo(5));
            Assert.That(hangprinter.Anchors[0], Is.EqualTo(new[] { 0f, -1500f, -90f }));
            Assert.That(hangprinter.Anchors[1], Is.EqualTo(new[] { 2000f, 1000f, -100f }), "an anchor not given keeps its position");
            Assert.That(hangprinter.Anchors[4], Is.EqualTo(new[] { 10f, 20f, 30f }), "I is the fifth anchor");
            Assert.That(hangprinter.PrintRadius, Is.EqualTo(1200f));
            Assert.That((await host.ExecuteCodeAsync("M669")).TrimEnd(), Does.EndWith("\nI:10.00, 20.00, 30.00\nP:Print radius: 1200.0"));
        });
    }

    /// <summary>The hangprinter's refusals, each leaving the geometry as it was</summary>
    /// <remarks>
    /// HangprinterKinematics::Configure takes N unchecked, which overruns its arrays past eight anchors; DSF refuses a
    /// count outside 3 to 8 (rrf-differences.md). Six anchors need a W axis this machine does not have
    /// </remarks>
    [TestCase("M669 N9", "N")]
    [TestCase("M669 N2", "N")]
    [TestCase("M669 N6", "no W axis")]
    [TestCase("M669 A0:-1500", "Wrong number of values in array, expected 3")]
    [TestCase("M669 P0", "greater than zero")]
    [Category("KnownGap")]
    public async Task M669RefusesABadHangprinterParameter(string code, string error)
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, UAxis, VAxis);
        DcsTestHost host = bench.Host;

        await host.ExecuteCodeAsync("M669 K6");
        string reply = await host.ExecuteCodeAsync(code);
        HangprinterKinematics hangprinter = await ReadKinematicsAsync<HangprinterKinematics>(host);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Does.StartWith("Error: ").And.Contain(error));
            Assert.That(hangprinter.Anchors, Has.Count.EqualTo(4));
            Assert.That(hangprinter.Anchors[0], Is.EqualTo(new[] { 0f, -2000f, -100f }));
            Assert.That(hangprinter.PrintRadius, Is.EqualTo(1500f));
        });
    }

    /// <summary>
    /// Selecting a hangprinter sets the line motors' steps per mm from its spool defaults: 2 x 25 full steps x 16
    /// microsteps x 255 spool teeth / (2 pi x 20 motor teeth) / 75mm spool radius
    /// </summary>
    /// <remarks>
    /// HangprinterKinematics::Recalc calls SetDriveStepsPerMm for every anchor; regression case m669-kinematics-hangprinter
    /// records 21.65 for X, Y and Z. Needs the spool parameters M666 configures, which are not ported
    /// </remarks>
    [Category("KnownGap")]
    [Test]
    public async Task M669HangprinterSetsTheLineMotorsStepsPerMm()
    {
        await using Bench bench = await Bench.StartAsync(Machines.Xyz, UAxis);

        await bench.Host.ExecuteCodeAsync("M669 K6");
        Assert.That(await bench.Host.ReadModelAsync(model => model.Move.Axes[0].StepsPerMm), Is.EqualTo(21.65f).Within(0.01f));
    }
}
