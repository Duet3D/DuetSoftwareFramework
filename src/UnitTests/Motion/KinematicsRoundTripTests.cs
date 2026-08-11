using System.Text;
using System.Text.Json;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Kinematics;
using NUnit.Framework;
using Code = DuetAPI.Commands.Code;
using OmKinematics = DuetAPI.ObjectModel.Kinematics;

namespace UnitTests.Motion;

/// <summary>
/// Whether a geometry survives being written to the object model and read back
/// </summary>
/// <remarks>
/// <para>
/// The geometry is what the machine moves by; the object model is what describes it to DuetWebControl,
/// to the APIs and to M500. §14 of <c>docs/devel/MCODE_MIGRATION.md</c> makes the second a projection
/// of the first, and a projection that drops a parameter describes a machine that is not the one being
/// planned for.
/// </para>
/// <para>
/// A parameter missing from <c>WriteTo</c> shows up here as a difference between the first projection
/// and the second: the value reaches the object model, is read back into a geometry that does not have
/// it, and comes out as the default the second time. That is what
/// <see cref="AConfiguredGeometrySurvivesTheObjectModel"/> asserts, code by code
/// </para>
/// </remarks>
[TestFixture]
public class KinematicsRoundTripTests
{
    /// <summary>
    /// M-code sequences that configure each geometry, one case per geometry
    /// </summary>
    /// <remarks>
    /// Every parameter each geometry's <c>Configure</c> reads appears in one of these, so a value
    /// dropped on the way to the object model has somewhere to be noticed
    /// </remarks>
    private static readonly object[] ConfiguredGeometries =
    [
        new object[] { "cartesian", new[] { "M669 K0 S200 T0.05" } },
        new object[] { "corexy", new[] { "M669 K1 X1:1:0 Y1:-1:0 Z0:0:1" } },
        new object[] { "markforged", new[] { "M669 K11" } },
        new object[] { "delta", new[] { "M669 K3", "M665 L210:211:212 R150.5 B120.25 H300.75 X0.1 Y0.2 Z-0.3", "M666 X0.11 Y-0.22 Z0.33 A1.5 B-2.5" } },
        new object[] { "scara", new[] { "M669 K4 P150.5 D160.25 A-100:100 B-140:140 C0.1:0.2:0.3 X10.5 Y20.25 R30" } },
        new object[] { "polar", new[] { "M669 K7 R20:180 H50.5 F45.25 A90.75" } },
        new object[] { "fivebarscara", new[] { "M669 K9" } },
        new object[] { "rotarydelta", new[] { "M669 K10 S50 T0.1" } },
    ];

    [TestCaseSource(nameof(ConfiguredGeometries))]
    public void AConfiguredGeometrySurvivesTheObjectModel(string description, string[] codes)
    {
        KinematicsEngine engine = Configure(codes);

        // What the geometry says about itself
        OmKinematics first = Project(engine);

        // ...read back into a geometry, and asked again. A parameter the projection dropped is a
        // default by now, and a parameter the factory does not read is one the machine never had
        KinematicsEngine restored = KinematicsFactory.Create(first);
        OmKinematics second = Project(restored);

        Assert.Multiple(() =>
        {
            Assert.That(restored.Kind, Is.EqualTo(engine.Kind), $"{description}: the geometry changed identity");
            Assert.That(Serialize(second), Is.EqualTo(Serialize(first)), $"{description}: a parameter was lost");
        });
    }

    [TestCaseSource(nameof(ConfiguredGeometries))]
    public void TheObjectModelNodeMatchesTheGeometryThatWritesIt(string description, string[] codes)
    {
        // WriteTo casts to the node its geometry belongs in, and the node is created from the
        // geometry's own name. A geometry whose name resolves to a different class would throw here
        // rather than at the end of an M669 with the model write lock held
        KinematicsEngine engine = Configure(codes);
        Assert.DoesNotThrow(() => Project(engine), description);
    }

    [Test]
    public void SegmentationSurvivesConfiguringSomethingElse()
    {
        // Configuring a geometry returns a new instance of it, and the segmentation M669 S set is not
        // one of the parameters that instance is built from - so it has to be carried across
        KinematicsEngine engine = Configure(["M669 K3 S250 T0.05", "M665 R160"]);

        Assert.Multiple(() =>
        {
            Assert.That(engine.SegmentsPerSecond, Is.EqualTo(250.0f));
            Assert.That(engine.MinSegmentLength, Is.EqualTo(0.05f));
        });
    }

    [Test]
    public void SelectingADifferentGeometryStartsFromItsOwnDefaults()
    {
        // A new geometry is a new machine: RepRapFirmware constructs one, and what M669 S had set on
        // the geometry before it does not carry over
        KinematicsEngine engine = Configure(["M669 K3 S250 T0.05", "M669 K0"]);

        Assert.Multiple(() =>
        {
            Assert.That(engine.Kind, Is.EqualTo(KinematicsName.Cartesian));
            Assert.That(engine.SegmentsPerSecond, Is.EqualTo(KinematicsEngine.DefaultSegmentsPerSecond));
            Assert.That(engine.Segmentation, Is.EqualTo(SegmentationType.None));
        });
    }

    [Test]
    public void AParameterThatIsNotGivenKeepsItsValue()
    {
        // M665 R on its own must not reset the rest of the delta to nothing
        KinematicsEngine engine = Configure(["M669 K3", "M665 L210 H300", "M665 R160"]);
        LinearDeltaKinematicsEngine delta = (LinearDeltaKinematicsEngine)engine;

        Assert.Multiple(() =>
        {
            Assert.That(delta.Radius, Is.EqualTo(160.0f));
            Assert.That(delta.HomedHeight, Is.EqualTo(300.0f));
            Assert.That(delta.GetDiagonalSquared(0), Is.EqualTo(210.0f * 210.0f).Within(1e-3f));
        });
    }

    [Test]
    public void TheDeltaTiltIsAPercentage()
    {
        // RepRapFirmware's `xTilt = gb.GetFValue() * 0.01`, and the report multiplies it back up
        LinearDeltaKinematicsEngine delta = (LinearDeltaKinematicsEngine)Configure(["M669 K3", "M666 A1.5 B-2.5"]);

        Assert.Multiple(() =>
        {
            Assert.That(delta.XTilt, Is.EqualTo(0.015f).Within(1e-6f));
            Assert.That(delta.YTilt, Is.EqualTo(-0.025f).Within(1e-6f));
            Assert.That(Report(delta, 666), Does.Contain("tilt X1.50% Y-2.50%"));
        });
    }

    [Test]
    public void ReportsComeFromTheGeometry()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Report(Configure(["M669 K0"]), 669), Is.EqualTo("Kinematics is cartesian, no segmentation"));
            Assert.That(Report(Configure(["M669 K3"]), 669),
                        Is.EqualTo("Kinematics is delta, 100 segments/sec, min. segment length 0.20mm"));
            Assert.That(Report(Configure(["M669 K0"]), 665), Is.EqualTo("M665 parameters do not apply to cartesian kinematics"));
        });
    }

    [Test]
    public void TurntableLimitsAreReportedInTheUnitsTheyWereGivenIn()
    {
        // The planner works in step clocks and the object model does not. Converting on the way in and
        // back out again would land somewhere near 45 rather than on it
        PolarKinematicsEngine polar = (PolarKinematicsEngine)Configure(["M669 K7 F45.25 A90.75"]);
        PolarKinematics projected = (PolarKinematics)Project(polar);

        Assert.Multiple(() =>
        {
            Assert.That(projected.TTSpeedMax, Is.EqualTo(45.25f));
            Assert.That(projected.TTAccMax, Is.EqualTo(90.75f));
        });
    }

    /// <summary>
    /// Run a sequence of configuring codes against a fresh machine
    /// </summary>
    /// <param name="codes">The codes</param>
    /// <returns>The geometry they leave behind</returns>
    private static KinematicsEngine Configure(string[] codes)
    {
        KinematicsEngine engine = KinematicsFactory.Create(KinematicsName.Cartesian);
        foreach (string text in codes)
        {
            bool seen = false;
            engine = KinematicsConfigurator.Apply(engine, new Code(text), ref seen);
            Assert.That(seen, Is.True, $"'{text}' configured nothing");
        }
        return engine;
    }

    /// <summary>
    /// Write a geometry into a fresh object model node
    /// </summary>
    /// <param name="engine">The geometry</param>
    /// <returns>The node</returns>
    private static OmKinematics Project(KinematicsEngine engine)
    {
        OmKinematics kinematics = OmKinematics.Create(engine.Kind);
        engine.WriteTo(kinematics);
        return kinematics;
    }

    /// <summary>
    /// The whole of a node's state, for comparing two of them
    /// </summary>
    /// <param name="kinematics">The node</param>
    /// <returns>Its JSON</returns>
    private static string Serialize(OmKinematics kinematics) => JsonSerializer.Serialize(kinematics, kinematics.GetType());

    /// <summary>
    /// What a geometry reports for a code given no parameters
    /// </summary>
    /// <param name="engine">The geometry</param>
    /// <param name="mCode">The code asking</param>
    /// <returns>The report</returns>
    private static string Report(KinematicsEngine engine, int mCode)
    {
        StringBuilder builder = new();
        engine.AppendReport(builder, mCode);
        return builder.ToString();
    }
}
