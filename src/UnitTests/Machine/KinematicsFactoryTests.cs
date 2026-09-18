using DuetAPI.ObjectModel;
using NUnit.Framework;

namespace UnitTests.Machine;

/// <summary>
/// Tests for selecting a geometry, which is what M669 K does
/// </summary>
/// <remarks>
/// Several geometries share one object model class and differ only by name, so the factory has to get
/// both the type and the name right. A machine given the wrong class loses the parameters that class
/// carries; one given the wrong name is described as something it is not
/// </remarks>
[TestFixture]
public class KinematicsFactoryTests
{
    [TestCase(KinematicsName.Cartesian, typeof(CoreKinematics))]
    [TestCase(KinematicsName.CoreXY, typeof(CoreKinematics))]
    [TestCase(KinematicsName.CoreXZ, typeof(CoreKinematics))]
    [TestCase(KinematicsName.CoreXYU, typeof(CoreKinematics))]
    [TestCase(KinematicsName.CoreXYUV, typeof(CoreKinematics))]
    [TestCase(KinematicsName.MarkForged, typeof(CoreKinematics))]
    [TestCase(KinematicsName.LinearDelta, typeof(DeltaKinematics))]
    [TestCase(KinematicsName.RotaryDelta, typeof(DeltaKinematics))]
    [TestCase(KinematicsName.Scara, typeof(ScaraKinematics))]
    [TestCase(KinematicsName.FiveBarScara, typeof(ScaraKinematics))]
    [TestCase(KinematicsName.Polar, typeof(PolarKinematics))]
    [TestCase(KinematicsName.Hangprinter, typeof(HangprinterKinematics))]
    public void EachGeometryGetsTheClassThatHoldsItsParameters(KinematicsName name, System.Type expected)
    {
        Kinematics kinematics = Kinematics.Create(name);
        Assert.That(kinematics, Is.TypeOf(expected));
        Assert.That(kinematics.Name, Is.EqualTo(name), "the name has to survive, not just the type");
    }

    [Test]
    public void ADeltaKeepsItsTowersSeparateFromACoreMatrix()
    {
        // Switching geometry replaces the instance, so nothing of the old one may leak through
        Assert.That(Kinematics.Create(KinematicsName.LinearDelta), Is.Not.InstanceOf<CoreKinematics>());
        Assert.That(Kinematics.Create(KinematicsName.CoreXY), Is.Not.InstanceOf<DeltaKinematics>());
    }
}
