using System;
using DuetControlServer.Motion.Native;
using DuetControlServer.Motion.Kinematics;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// Which geometries need a straight move broken into short ones
/// </summary>
/// <remarks>
/// <para>
/// A geometry that maps axis space onto its motors non-linearly cannot draw a straight line by
/// transforming the two ends of one: the motors interpolate linearly between motor positions, so the
/// head bows. Chopping the move up until the bow is smaller than a step is how every such machine
/// does it, and nothing did it here before.
/// </para>
/// <para>
/// RepRapFirmware makes this optional per kinematics and skips it while simulating. Here it is not
/// optional - there is no local step generation to fall back on, so an unsegmented move on a delta is
/// simply executed as the wrong shape
/// </para>
/// </remarks>
[TestFixture]
public class SegmentationTests
{
    [Test]
    public void ACartesianMachineNeedsNoSegmentation()
    {
        // The transform is the identity, so a straight line in motor space already is one
        KinematicsEngine engine = CoreKinematicsEngine.TryCreate("cartesian")!;
        Assert.That(engine.Segmentation, Is.EqualTo(SegmentationType.None));
    }

    [Test]
    public void ACoreXyMachineNeedsNoSegmentationEither()
    {
        // CoreXY's transform is a matrix, which is still linear: a straight line stays straight
        KinematicsEngine engine = CoreKinematicsEngine.TryCreate("corexy")!;
        Assert.That(engine.Segmentation, Is.EqualTo(SegmentationType.None));
    }

    [Test]
    public void ALinearDeltaSegmentsTravelMovesButNotForItsZLength()
    {
        // A delta bows in every direction, so even an uncoordinated move has to be segmented. Z is
        // one of the towers rather than an independent axis, but its movement is deliberately not
        // counted towards the segment length - RepRapFirmware counts XY only for a linear delta
        LinearDeltaKinematicsEngine engine = LinearDeltaKinematicsEngine.CreateDefault();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.Segment), Is.True);
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.IncludeG0), Is.True, "travel moves too");
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.IncludeZ), Is.False);
        });
    }

    [Test]
    public void ARotaryDeltaCountsZAsWell()
    {
        // Every coordinate of the head is an arm angle here, so Z bows like the rest of it
        RotaryDeltaKinematicsEngine engine = new();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.Segment), Is.True);
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.IncludeZ), Is.True);
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.IncludeG0), Is.True);
        });
    }

    [Test]
    public void AScaraSegmentsInXyOnly()
    {
        // The arms bow in the plane; Z is an ordinary leadscrew, and a travel move may be left whole
        ScaraKinematicsEngine engine = new();
        Assert.Multiple(() =>
        {
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.Segment), Is.True);
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.IncludeZ), Is.False);
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.IncludeG0), Is.False);
        });
    }

    [Test]
    public void APolarSegmentsInXyOnly()
    {
        // A straight line across the bed is an arc in radius and angle, so it bows; Z is independent
        PolarKinematicsEngine engine = new(minRadius: 0.0f, maxRadius: 150.0f, homedRadius: 0.0f,
                                           maxTurntableSpeed: 30.0f, maxTurntableAcceleration: 30.0f);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.Segment), Is.True);
            Assert.That(engine.Segmentation.HasFlag(SegmentationType.IncludeZ), Is.False);
        });
    }

    [Test]
    public void EveryGeometryThatHomesIndividualDrivesAlsoSegments()
    {
        // Not a coincidence: both follow from the transform being non-linear. A geometry where the
        // endstop belongs to a motor rather than an axis is one where motor space and axis space are
        // different shapes, which is exactly when a straight line does not survive the transform
        KinematicsEngine[] engines =
        [
            LinearDeltaKinematicsEngine.CreateDefault(),
            new RotaryDeltaKinematicsEngine(),
            new ScaraKinematicsEngine(),
            new PolarKinematicsEngine(minRadius: 0.0f, maxRadius: 150.0f, homedRadius: 0.0f,
                                      maxTurntableSpeed: 30.0f, maxTurntableAcceleration: 30.0f),
            HangprinterKinematicsEngine.CreateDefault(),
            new FiveBarScaraKinematicsEngine(xOrigL: -50.0f, yOrigL: 0.0f, xOrigR: 50.0f, yOrigR: 0.0f,
                                             proximalL: 100.0f, proximalR: 100.0f, distalL: 100.0f, distalR: 100.0f)
        ];

        Assert.Multiple(() =>
        {
            foreach (KinematicsEngine engine in engines)
            {
                Assert.That(engine.HomesIndividualDrives, Is.True, engine.Name);
                Assert.That(engine.Segmentation.HasFlag(SegmentationType.Segment), Is.True, engine.Name);
            }
        });
    }

    [Test]
    public void SegmentingADeltaMoveStraightensIt()
    {
        // What all of this is for. The motors interpolate linearly between the motor positions they
        // are given, so on a delta the head follows a curve between them. Measuring that curve is
        // the point of this test: it is real, it is large enough to see on a normal-sized move, and
        // cutting the move up is what removes it
        LinearDeltaKinematicsEngine engine = LinearDeltaKinematicsEngine.CreateDefault();
        float[] stepsPerMm = new float[MotionLimits.MaxAxesPlusExtruders];
        Array.Fill(stepsPerMm, 100.0f);

        float[] from = [-60.0f, 0.0f, 50.0f];
        float[] to = [60.0f, 0.0f, 50.0f];

        float unsegmented = BowOver(engine, stepsPerMm, from, to, segments: 1);
        float segmented = BowOver(engine, stepsPerMm, from, to, segments: 32);

        Assert.Multiple(() =>
        {
            // Around 12mm on this machine: a 120mm move across the bed sags by more than a centimetre
            // in the middle if the two ends are all the motors are told about
            Assert.That(unsegmented, Is.GreaterThan(5.0f),
                        $"an unsegmented delta move bows badly (measured {unsegmented:F3}mm)");

            // Around 15 microns, which is a step and a half at these steps per mm
            Assert.That(segmented, Is.LessThan(0.05f),
                        $"32 segments bring the head back onto the line (measured {segmented:F5}mm)");
        });
    }

    /// <summary>
    /// How far the head strays from the straight line between two points
    /// </summary>
    /// <param name="engine">The geometry</param>
    /// <param name="stepsPerMm">Steps per mm per drive</param>
    /// <param name="from">Where the move starts</param>
    /// <param name="to">Where it ends</param>
    /// <param name="segments">How many pieces it is cut into</param>
    /// <returns>The largest deviation in Z, mm</returns>
    /// <remarks>
    /// Each segment is exact at its ends, because those go through the kinematics. In between, the
    /// motors run linearly from one motor position to the next, so the head takes whatever path that
    /// implies - which is what this samples
    /// </remarks>
    private static float BowOver(KinematicsEngine engine, float[] stepsPerMm, float[] from, float[] to, int segments)
    {
        const int numAxes = 3;
        float worst = 0.0f;

        for (int segment = 0; segment < segments; segment++)
        {
            float[] segStart = Along(from, to, (float)segment / segments);
            float[] segEnd = Along(from, to, (float)(segment + 1) / segments);

            int[] startSteps = new int[MotionLimits.MaxAxesPlusExtruders];
            int[] endSteps = new int[MotionLimits.MaxAxesPlusExtruders];
            engine.CartesianToMotorSteps(segStart, stepsPerMm, numAxes, numAxes, startSteps);
            engine.CartesianToMotorSteps(segEnd, stepsPerMm, numAxes, numAxes, endSteps);

            // Sample the path the motors will actually take within this segment
            for (int step = 1; step < 8; step++)
            {
                float t = step / 8.0f;
                int[] interpolated = new int[MotionLimits.MaxAxesPlusExtruders];
                for (int drive = 0; drive < numAxes; drive++)
                {
                    interpolated[drive] = (int)MathF.Round(startSteps[drive] + ((endSteps[drive] - startSteps[drive]) * t));
                }

                float[] actual = new float[MotionLimits.MaxAxesPlusExtruders];
                engine.MotorStepsToCartesian(interpolated, stepsPerMm, numAxes, numAxes, actual);

                float[] wanted = Along(segStart, segEnd, t);
                worst = MathF.Max(worst, MathF.Abs(actual[2] - wanted[2]));
            }
        }
        return worst;
    }

    private static float[] Along(float[] from, float[] to, float t)
    {
        float[] result = new float[MotionLimits.MaxAxesPlusExtruders];
        for (int axis = 0; axis < 3; axis++)
        {
            result[axis] = from[axis] + ((to[axis] - from[axis]) * t);
        }
        return result;
    }

    [Test]
    public void TheSegmentLimitsAreRepRapFirmwaresDefaults()
    {
        // Two limits rather than one: the length keeps the bow below a step on a fast move, and the
        // rate keeps a slow move from being cut into far more pieces than the error justifies. The
        // segment count is the smaller of what each asks for
        KinematicsEngine engine = LinearDeltaKinematicsEngine.CreateDefault();
        Assert.Multiple(() =>
        {
            Assert.That(engine.SegmentsPerSecond, Is.EqualTo(100.0f));
            Assert.That(engine.MinSegmentLength, Is.EqualTo(0.2f));
        });
    }
}
