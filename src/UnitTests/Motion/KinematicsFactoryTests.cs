using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Kinematics;
using NUnit.Framework;
using OmKinematics = DuetAPI.ObjectModel.Kinematics;

namespace UnitTests.Motion;

/// <summary>
/// The translation from the object model's kinematics into the geometry engine that plans with them
/// </summary>
/// <remarks>
/// <para>
/// <see cref="KinematicsFactory"/> is a hand-written switch, and a hand-written switch is exactly
/// where a configuration parameter goes missing without anything looking wrong: M669 writes it, the
/// object model reports it, DWC shows it, and the machine ignores it. That is what happened to the
/// segmentation parameters.
/// </para>
/// <para>
/// So the first test here is not about any one geometry. It asserts that every property the object
/// model's kinematics classes have is either read by the factory or explicitly recorded as not worth
/// reading, and it fails when someone adds a property and classifies it as neither. See §14 of
/// <c>docs/devel/MCODE_MIGRATION.md</c>
/// </para>
/// </remarks>
[TestFixture]
public class KinematicsFactoryTests
{
    /// <summary>The object model classes that describe a geometry</summary>
    private static readonly Type[] KinematicsTypes =
    [
        typeof(OmKinematics),
        typeof(ZLeadscrewKinematics),
        typeof(CoreKinematics),
        typeof(DeltaKinematics),
        typeof(DeltaTower),
        typeof(ScaraKinematics),
        typeof(PolarKinematics),
        typeof(HangprinterKinematics)
    ];

    /// <summary>
    /// Properties the factory reads when it builds an engine
    /// </summary>
    private static readonly HashSet<string> Consumed =
    [
        "Kinematics.Name",
        "Kinematics.Segmentation",
        "CoreKinematics.InverseMatrix",
        "DeltaKinematics.DeltaRadius",
        "DeltaKinematics.HomedHeight",
        "DeltaKinematics.PrintRadius",
        "DeltaKinematics.Towers",
        "DeltaKinematics.XTilt",
        "DeltaKinematics.YTilt",
        "DeltaTower.AngleCorrection",
        "DeltaTower.Diagonal",
        "DeltaTower.EndstopAdjustment",
        "ScaraKinematics.Crosstalk",
        "ScaraKinematics.DistalLength",
        "ScaraKinematics.MinRadius",
        "ScaraKinematics.ProximalLength",
        "ScaraKinematics.PsiLimits",
        "ScaraKinematics.ThetaLimits",
        "ScaraKinematics.XOffset",
        "ScaraKinematics.YOffset",
        "PolarKinematics.RadiusHomed",
        "PolarKinematics.RadiusMax",
        "PolarKinematics.RadiusMin",
        "PolarKinematics.TTAccMax",
        "PolarKinematics.TTSpeedMax",
        "HangprinterKinematics.Anchors",
        "HangprinterKinematics.PrintRadius"
    ];

    /// <summary>
    /// Properties the factory deliberately does not read, and why
    /// </summary>
    /// <remarks>
    /// A property belongs here when the engine works it out for itself or when it configures
    /// something other than the transform. It does not belong here because nobody got round to it -
    /// that is the case this fixture exists to catch
    /// </remarks>
    private static readonly Dictionary<string, string> NotConsumed = new()
    {
        ["CoreKinematics.ForwardMatrix"] = "the engine inverts the inverse matrix itself, so reading both would let them disagree",
        ["DeltaTower.XPos"] = "derived by the engine from the delta radius and the tower's angle correction",
        ["DeltaTower.YPos"] = "derived by the engine from the delta radius and the tower's angle correction",
        ["ZLeadscrewKinematics.TiltCorrection"] = "M671 leadscrew levelling, which corrects the bed rather than describing the transform"
    };

    /// <summary>
    /// The configuration properties a type declares
    /// </summary>
    /// <param name="type">The object model class</param>
    /// <returns>Its own properties, without the serialisation plumbing</returns>
    /// <remarks>
    /// <c>Descriptor</c> is generated onto every model object to drive serialisation and patching. It
    /// describes the class rather than the machine, so classifying it would be noise
    /// </remarks>
    private static IEnumerable<PropertyInfo> ConfigurationProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
               .Where(property => property.PropertyType != typeof(IModelObjectDescriptor));

    [Test]
    public void EveryObjectModelKinematicsPropertyIsClassified()
    {
        List<string> unclassified = [];
        foreach (Type type in KinematicsTypes)
        {
            foreach (PropertyInfo property in ConfigurationProperties(type))
            {
                string key = $"{type.Name}.{property.Name}";
                if (!Consumed.Contains(key) && !NotConsumed.ContainsKey(key))
                {
                    unclassified.Add(key);
                }
            }
        }

        Assert.That(unclassified, Is.Empty,
                    "These object model properties are neither read by KinematicsFactory nor recorded as deliberately unread. "
                    + "A geometry parameter that is written by an M-code and never read plans moves for a machine that is not "
                    + "the configured one - add it to Consumed and read it, or to NotConsumed with the reason");
    }

    [Test]
    public void NothingIsClassifiedTwiceOrDoesNotExist()
    {
        HashSet<string> declared = [];
        foreach (Type type in KinematicsTypes)
        {
            foreach (PropertyInfo property in ConfigurationProperties(type))
            {
                declared.Add($"{type.Name}.{property.Name}");
            }
        }

        Assert.Multiple(() =>
        {
            // A stale entry means a property was renamed or removed and the classification was not
            // followed - which would silently stop the fixture above from covering its replacement
            Assert.That(Consumed.Except(declared), Is.Empty, "Consumed names a property that no longer exists");
            Assert.That(NotConsumed.Keys.Except(declared), Is.Empty, "NotConsumed names a property that no longer exists");
            Assert.That(Consumed.Intersect(NotConsumed.Keys), Is.Empty, "A property is classified both ways");
        });
    }

    [Test]
    public void SegmentationConfiguredByM669ReachesTheEngine()
    {
        // The bug this fixture was written for: M669 S/T wrote the object model, the factory never
        // read it, and the geometry kept the hardcoded defaults it was born with
        DeltaKinematics delta = (DeltaKinematics)OmKinematics.Create(KinematicsName.LinearDelta);
        delta.Segmentation = new MoveSegmentation { SegmentsPerSec = 250.0f, MinSegLength = 0.05f };

        KinematicsEngine engine = KinematicsFactory.Create(delta);

        Assert.Multiple(() =>
        {
            Assert.That(engine.SegmentsPerSecond, Is.EqualTo(250.0f));
            Assert.That(engine.MinSegmentLength, Is.EqualTo(0.05f));
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.Segment), Is.True);
        });
    }

    [Test]
    public void AGeometryThatSegmentsStartsWithRepRapFirmwaresDefaults()
    {
        // Nothing has configured it, so it segments the way an unconfigured delta does in RRF
        KinematicsEngine engine = KinematicsFactory.Create(OmKinematics.Create(KinematicsName.LinearDelta));

        Assert.Multiple(() =>
        {
            Assert.That(engine.SegmentsPerSecond, Is.EqualTo(KinematicsEngine.DefaultSegmentsPerSecond));
            Assert.That(engine.MinSegmentLength, Is.EqualTo(KinematicsEngine.DefaultMinSegmentLength));
            Assert.That(engine.Segmentation, Is.EqualTo(SegmentationType.Segment | SegmentationType.IncludeG0));
        });
    }

    [Test]
    public void EitherParameterAtZeroTurnsSegmentationOff()
    {
        // RepRapFirmware's TryConfigureSegmentation: useSegmentation = minSegmentLength > 0 &&
        // segmentsPerSecond > 0. This is how a delta is told not to segment
        DeltaKinematics noRate = (DeltaKinematics)OmKinematics.Create(KinematicsName.LinearDelta);
        noRate.Segmentation = new MoveSegmentation { SegmentsPerSec = 0.0f, MinSegLength = 0.2f };

        DeltaKinematics noLength = (DeltaKinematics)OmKinematics.Create(KinematicsName.LinearDelta);
        noLength.Segmentation = new MoveSegmentation { SegmentsPerSec = 100.0f, MinSegLength = 0.0f };

        Assert.Multiple(() =>
        {
            Assert.That(KinematicsFactory.Create(noRate).Segmentation.HasFlag(SegmentationType.Segment), Is.False);
            Assert.That(KinematicsFactory.Create(noLength).Segmentation.HasFlag(SegmentationType.Segment), Is.False);

            // The rest of the geometry's segmentation is not configuration and does not go away with
            // it: which axes count towards a segment's length is a property of the machine
            Assert.That(KinematicsFactory.Create(noRate).Segmentation.HasFlag(SegmentationType.IncludeG0), Is.True);
        });
    }

    [Test]
    public void SegmentationCanBeTurnedOnForAGeometryThatDoesNotNeedIt()
    {
        // RepRapFirmware allows this - CoreKinematics::Configure calls TryConfigureSegmentation like
        // every other geometry, and the flag is recomputed from the two values rather than being
        // fixed by the geometry
        CoreKinematics cartesian = (CoreKinematics)OmKinematics.Create(KinematicsName.Cartesian);

        Assert.That(KinematicsFactory.Create(cartesian).Segmentation, Is.EqualTo(SegmentationType.None));

        cartesian.Segmentation = new MoveSegmentation { SegmentsPerSec = 200.0f, MinSegLength = 0.1f };
        Assert.That(KinematicsFactory.Create(cartesian).Segmentation, Is.EqualTo(SegmentationType.Segment));
    }

    [Test]
    public void EveryGeometryTheObjectModelNamesCanBeBuilt()
    {
        // The one switch that turns a name into a geometry. A name added to the object model without
        // a case here would silently build a Cartesian machine, which is a machine that moves - just
        // not the one that was asked for
        foreach (KinematicsName name in Enum.GetValues<KinematicsName>())
        {
            if (name == KinematicsName.Unknown)
            {
                // Not a geometry: it is what an unrecognised name deserialises to, and an
                // unconfigured machine is Cartesian
                Assert.That(KinematicsFactory.Create(name).Kind, Is.EqualTo(KinematicsName.Cartesian));
                continue;
            }

            Assert.That(KinematicsFactory.Create(name).Kind, Is.EqualTo(name), $"{name} builds a different geometry");
        }
    }

    [Test]
    public void AGeometryWithNothingToDescribeItGetsItsOwnDefaults()
    {
        // An object model node that carries no parameters - which is every geometry before its
        // M-code has run - must still build the geometry it names rather than a Cartesian one
        Assert.Multiple(() =>
        {
            Assert.That(KinematicsFactory.Create(OmKinematics.Create(KinematicsName.RotaryDelta)),
                        Is.TypeOf<RotaryDeltaKinematicsEngine>(), "a rotary delta shares the delta node with a linear one");
            Assert.That(KinematicsFactory.Create(OmKinematics.Create(KinematicsName.FiveBarScara)),
                        Is.TypeOf<FiveBarScaraKinematicsEngine>(), "both SCARAs share one node");
            Assert.That(KinematicsFactory.Create(OmKinematics.Create(KinematicsName.MarkForged)).Kind,
                        Is.EqualTo(KinematicsName.MarkForged));
        });
    }
}
