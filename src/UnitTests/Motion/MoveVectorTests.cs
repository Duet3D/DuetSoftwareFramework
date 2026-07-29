using System;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Native;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// The vector arithmetic the move planner is built on
/// </summary>
/// <remarks>
/// These decide what feed rate the user actually gets and how hard the machine is allowed to
/// accelerate, so they are worth pinning down on their own rather than only through the moves that
/// use them
/// </remarks>
[TestFixture]
public class MoveVectorTests
{
    private const int NumDrives = MotionLimits.MaxAxesPlusExtruders;

    private static uint Bit(int index) => 1u << index;

    [Test]
    public void MagnitudeOverSelectedAxesIgnoresTheRest()
    {
        float[] v = new float[NumDrives];
        v[0] = 3.0f;
        v[1] = 4.0f;
        v[31] = 100.0f;             // an extruder, which must not lengthen the move

        Assert.Multiple(() =>
        {
            Assert.That(MoveVector.Magnitude(v, Bit(0) | Bit(1)), Is.EqualTo(5.0f).Within(1e-5f));
            Assert.That(MoveVector.Magnitude(v), Is.EqualTo(MathF.Sqrt(9 + 16 + 10000)).Within(1e-3f));
        });
    }

    [Test]
    public void NormaliseScalesTheWholeVectorButMeasuresOnlyTheNamedAxes()
    {
        // This is what makes the feed rate mean what the user expects on an extruding move: the
        // extruder comes along for the ride rather than making the move look longer and run slow
        float[] v = new float[NumDrives];
        v[0] = 3.0f;
        v[1] = 4.0f;
        v[31] = 2.0f;

        float magnitude = MoveVector.Normalise(v, Bit(0) | Bit(1));

        Assert.Multiple(() =>
        {
            Assert.That(magnitude, Is.EqualTo(5.0f).Within(1e-5f));
            Assert.That(v[0], Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(v[1], Is.EqualTo(0.8f).Within(1e-5f));
            Assert.That(v[31], Is.EqualTo(0.4f).Within(1e-5f), "the extruder is scaled by the same factor");
        });
    }

    [Test]
    public void NormalisingAnEmptyVectorReportsZeroRatherThanDividingByIt()
    {
        float[] v = new float[NumDrives];
        Assert.That(MoveVector.Normalise(v, Bit(0) | Bit(1)), Is.EqualTo(0.0f));
        Assert.That(v[0], Is.EqualTo(0.0f));
    }

    [Test]
    public void LinearMotionAveragesAxesThatShareAToolMapping()
    {
        // A tool mapping X onto two axes moves both together. Counting each one would make the move
        // look sqrt(2) times longer than it is and run it proportionately slow
        float[] v = new float[NumDrives];
        v[0] = 3.0f;                // X
        v[3] = 3.0f;                // a second axis also mapped to X
        v[1] = 4.0f;                // Y

        uint linearAxes = Bit(0) | Bit(1) | Bit(3);
        float distance = MoveVector.NormaliseLinearMotion(v, linearAxes, xAxes: Bit(0) | Bit(3), yAxes: Bit(1));

        // sqrt((9+9)/2 + 16) = sqrt(25) = 5, not sqrt(9+9+16)
        Assert.That(distance, Is.EqualTo(5.0f).Within(1e-5f));
    }

    [Test]
    public void LinearMotionWithOneAxisPerLetterIsAPlainMagnitude()
    {
        float[] v = new float[NumDrives];
        v[0] = 3.0f;
        v[1] = 4.0f;

        float distance = MoveVector.NormaliseLinearMotion(v, Bit(0) | Bit(1) | Bit(2), Bit(0), Bit(1));

        Assert.Multiple(() =>
        {
            Assert.That(distance, Is.EqualTo(5.0f).Within(1e-5f));
            Assert.That(v[0], Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(v[1], Is.EqualTo(0.8f).Within(1e-5f));
        });
    }

    [Test]
    public void LinearMotionIgnoresRotationalAxes()
    {
        float[] v = new float[NumDrives];
        v[0] = 3.0f;
        v[1] = 4.0f;
        v[4] = 90.0f;               // a rotational axis, in degrees, not part of the linear distance

        float distance = MoveVector.NormaliseLinearMotion(v, Bit(0) | Bit(1) | Bit(2), Bit(0), Bit(1));

        Assert.That(distance, Is.EqualTo(5.0f).Within(1e-5f));
    }

    [Test]
    public void VectorBoxIntersectionIsTheFirstLimitTheMoveMeets()
    {
        // The move is scaled up until some single drive would have to exceed what it can do; that
        // drive, and no other, decides the answer
        float[] direction = new float[NumDrives];
        direction[0] = 0.6f;
        direction[1] = 0.8f;

        float[] box = new float[NumDrives];
        Array.Fill(box, float.MaxValue / NumDrives);
        box[0] = 300.0f;            // X could allow 300/0.6 = 500
        box[1] = 200.0f;            // Y only allows 200/0.8 = 250, so Y decides

        Assert.That(MoveVector.VectorBoxIntersection(direction, box), Is.EqualTo(250.0f).Within(0.01f));
    }

    [Test]
    public void VectorBoxIntersectionAllowsADiagonalToExceedEitherAxis()
    {
        // The reason a Cartesian machine may run a 45-degree move faster than either axis alone
        float[] direction = new float[NumDrives];
        direction[0] = MathF.Sqrt(0.5f);
        direction[1] = MathF.Sqrt(0.5f);

        float[] box = new float[NumDrives];
        Array.Fill(box, float.MaxValue / NumDrives);
        box[0] = 100.0f;
        box[1] = 100.0f;

        Assert.That(MoveVector.VectorBoxIntersection(direction, box),
                    Is.EqualTo(100.0f * MathF.Sqrt(2.0f)).Within(0.01f));
    }

    [Test]
    public void AbsoluteMovesAVectorIntoThePositiveHyperquadrant()
    {
        float[] v = [1.0f, -2.0f, 3.0f, -4.0f];
        MoveVector.Absolute(v);
        Assert.That(v, Is.EqualTo(new[] { 1.0f, 2.0f, 3.0f, 4.0f }));
    }

    [Test]
    public void ScaleMultipliesEveryComponent()
    {
        float[] v = [1.0f, -2.0f, 3.0f];
        MoveVector.Scale(v, 2.0f);
        Assert.That(v, Is.EqualTo(new[] { 2.0f, -4.0f, 6.0f }));
    }

    [Test]
    public void LowestBitsCoversTheRequestedCount()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MoveVector.LowestBits(0), Is.EqualTo(0u));
            Assert.That(MoveVector.LowestBits(3), Is.EqualTo(0b111u));
            Assert.That(MoveVector.LowestBits(MotionLimits.MaxAxesPlusExtruders), Is.EqualTo(uint.MaxValue));
        });
    }
}
