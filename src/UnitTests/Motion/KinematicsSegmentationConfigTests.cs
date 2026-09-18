using DuetControlServer.Motion.Kinematics;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using Code = DuetAPI.Commands.Code;

namespace UnitTests.Motion;

/// <summary>
/// What M669's S and T do to a geometry's segmentation
/// </summary>
/// <remarks>
/// RepRapFirmware leaves the parameter a code did not give at its existing value, and recomputes
/// whether to segment from the pair. Both halves matter: a code that gives one of them must not
/// silently zero the other, because zero is what turns segmentation off
/// </remarks>
[TestFixture]
public class KinematicsSegmentationConfigTests
{
    private static KinematicsEngine Apply(KinematicsEngine engine, string codeText)
    {
        bool seen = false;
        return KinematicsConfigurator.Apply(engine, new Code(codeText), ref seen);
    }

    [Test]
    public void SegmentsPerSecondAloneTurnsSegmentationOn()
    {
        // The reported bug: M669 S10 left the minimum segment length reading zero, so the pair
        // resolved to "off" on a code whose whole purpose was to ask for segmentation
        KinematicsEngine engine = Apply(CoreKinematicsEngine.TryCreate("cartesian")!, "M669 K0 S10");

        Assert.Multiple(() =>
        {
            Assert.That(engine.SegmentsPerSecond, Is.EqualTo(10.0f));
            Assert.That(engine.MinSegmentLength, Is.GreaterThan(0.0f), "T was not given, so it keeps its value");
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.Segment), Is.True);
        });
    }

    [Test]
    public void MinSegmentLengthAloneTurnsSegmentationOn()
    {
        KinematicsEngine engine = Apply(CoreKinematicsEngine.TryCreate("cartesian")!, "M669 T0.5");

        Assert.Multiple(() =>
        {
            Assert.That(engine.MinSegmentLength, Is.EqualTo(0.5f));
            Assert.That(engine.SegmentsPerSecond, Is.GreaterThan(0.0f), "S was not given, so it keeps its value");
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.Segment), Is.True);
        });
    }

    [Test]
    public void ZeroTurnsSegmentationOff()
    {
        // Explicitly zero is different from absent, and is how a delta is told not to segment
        KinematicsEngine engine = Apply(LinearDeltaKinematicsEngine.CreateDefault(), "M669 S0");
        Assert.That(engine.Segmentation.HasFlag(SegmentationType.Segment), Is.False);
    }

    [Test]
    public void SelectingTheSameGeometryKeepsItsSegmentation()
    {
        // M669 K0 on a machine that is already Cartesian is not a new machine, so what an earlier
        // M669 S set has to survive it
        KinematicsEngine engine = Apply(CoreKinematicsEngine.TryCreate("cartesian")!, "M669 S20 T0.3");
        engine = Apply(engine, "M669 K0");

        Assert.Multiple(() =>
        {
            Assert.That(engine.SegmentsPerSecond, Is.EqualTo(20.0f));
            Assert.That(engine.MinSegmentLength, Is.EqualTo(0.3f));
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.Segment), Is.True);
        });
    }
}
