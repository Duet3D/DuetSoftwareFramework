using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// M556 axis skew compensation
/// </summary>
/// <remarks>
/// The machine's axes are not quite at right angles, so a move along one drags the head slightly
/// along another. What is worth testing is which axis carries each correction: the XY term goes on
/// one axis or the other and never both, and applying it to both would double the error it exists to
/// take out
/// </remarks>
[TestFixture]
public class AxisSkewTests
{
    /// <summary>
    /// A machine with the given skew measured
    /// </summary>
    /// <remarks>
    /// <paramref name="compensateXY"/> keeps the object model's own default, which is to correct X
    /// when Y moves - the opposite way round from what the field name reads like
    /// </remarks>
    private static Move NewMove(float tanXY = 0.0f, float tanXZ = 0.0f, float tanYZ = 0.0f,
                                bool compensateXY = true, int numAxes = 3)
    {
        Move move = new();
        foreach (char letter in "XYZ"[..numAxes])
        {
            move.Axes.Add(new Axis { Letter = letter, Visible = true });
        }

        Skew skew = move.Compensation.Skew;
        skew.TanXY = tanXY;
        skew.TanXZ = tanXZ;
        skew.TanYZ = tanYZ;
        skew.CompensateXY = compensateXY;
        return move;
    }

    [Test]
    public void ASquareMachineIsLeftAlone()
    {
        // Every move goes through this, so a machine with no skew measured has to come out untouched
        // rather than approximately untouched
        float[] coords = [10.0f, 20.0f, 5.0f];
        AxisSkew.Apply(null, NewMove(), coords, 3);

        Assert.That(coords, Is.EqualTo(new[] { 10.0f, 20.0f, 5.0f }));
    }

    [Test]
    public void TheXyTermGoesOnOneAxisOrTheOtherAndNeverBoth()
    {
        // Correcting both would double it; which one it is is a matter of which axis the machine is
        // squared against, and is what M556 P selects
        float[] onY = [10.0f, 20.0f, 0.0f];
        AxisSkew.Apply(null, NewMove(tanXY: 0.01f, compensateXY: false), onY, 3);

        float[] onX = [10.0f, 20.0f, 0.0f];
        AxisSkew.Apply(null, NewMove(tanXY: 0.01f), onX, 3);

        Assert.Multiple(() =>
        {
            Assert.That(onY[0], Is.EqualTo(10.0f), "X untouched");
            Assert.That(onY[1], Is.EqualTo(20.1f).Within(1e-4f), "Y carries a term read from X");

            Assert.That(onX[0], Is.EqualTo(10.2f).Within(1e-4f), "X carries a term read from Y");
            Assert.That(onX[1], Is.EqualTo(20.0f), "Y untouched");
        });
    }

    [Test]
    public void TheHeightTermsMoveXAndYByTheirOwnTangents()
    {
        float[] coords = [10.0f, 20.0f, 50.0f];
        AxisSkew.Apply(null, NewMove(tanXZ: 0.002f, tanYZ: 0.004f), coords, 3);

        Assert.Multiple(() =>
        {
            Assert.That(coords[0], Is.EqualTo(10.1f).Within(1e-4f));
            Assert.That(coords[1], Is.EqualTo(20.2f).Within(1e-4f));
            Assert.That(coords[2], Is.EqualTo(50.0f), "Z is the reference, so nothing corrects it");
        });
    }

    [Test]
    public void AMachineWithNoYAxisHasNoPairToBeOutOfSquare()
    {
        Move move = NewMove(tanXY: 0.01f, tanXZ: 0.002f, numAxes: 1);

        float[] coords = [10.0f];
        AxisSkew.Apply(null, move, coords, 1);

        Assert.That(coords[0], Is.EqualTo(10.0f));
    }

    [TestCase(0.0f, 0.002f, 0.004f, true, TestName = "ApplyingAndRemovingTheSkewGivesBackTheRequestedPosition(height terms)")]
    [TestCase(0.01f, 0.0f, 0.0f, true, TestName = "ApplyingAndRemovingTheSkewGivesBackTheRequestedPosition(XY term)")]
    [TestCase(0.01f, 0.002f, 0.004f, true, TestName = "ApplyingAndRemovingTheSkewGivesBackTheRequestedPosition(every term, compensating X)")]
    [TestCase(0.01f, 0.002f, 0.004f, false, TestName = "ApplyingAndRemovingTheSkewGivesBackTheRequestedPosition(every term, compensating Y)")]
    public void ApplyingAndRemovingTheSkewGivesBackTheRequestedPosition(float tanXY, float tanXZ, float tanYZ,
                                                                        bool compensateXY)
    {
        // What the operator reads back has to be what they asked for. The inverse is written back
        // into the commanded position after homing and probing, so a round trip that does not cancel
        // is not a reporting error - it is movement, repeated every time the machine is probed
        Move move = NewMove(tanXY, tanXZ, tanYZ, compensateXY);

        float[] coords = [10.0f, 20.0f, 50.0f];
        AxisSkew.Apply(null, move, coords, 3);
        Assert.That(coords[compensateXY ? 0 : 1], Is.Not.EqualTo(compensateXY ? 10.0f : 20.0f),
                    "the correction was applied at all");

        AxisSkew.Remove(null, move, coords, 3);

        Assert.Multiple(() =>
        {
            Assert.That(coords[0], Is.EqualTo(10.0f).Within(1e-4f));
            Assert.That(coords[1], Is.EqualTo(20.0f).Within(1e-4f));
            Assert.That(coords[2], Is.EqualTo(50.0f).Within(1e-4f));
        });
    }

    [Test]
    public void TheXyTermAndAHeightTermTogetherCancelWhereTheFirmwareLeavesAResidual()
    {
        // The case DSF leads RepRapFirmware on. Its Move::InverseAxisTransform walks the axes once
        // with the Y branch ahead of the X branch, which only undoes the pair in the opposite order
        // when Y is the lower-numbered axis; on an ordinary machine X comes first, so X's correction
        // comes off before the Y it was computed from has been restored. Two passes each way avoid
        // that, and the residual it leaves in the firmware - tanXY times a height correction - is
        // what this asserts is gone
        foreach (bool compensateXY in new[] { true, false })
        {
            Move move = NewMove(tanXY: 0.01f, tanXZ: 0.002f, tanYZ: 0.004f, compensateXY);

            float[] coords = [10.0f, 20.0f, 50.0f];
            AxisSkew.Apply(null, move, coords, 3);
            AxisSkew.Remove(null, move, coords, 3);

            Assert.Multiple(() =>
            {
                Assert.That(coords[0] - 10.0f, Is.Zero.Within(1e-6f), $"X, compensateXY {compensateXY}");
                Assert.That(coords[1] - 20.0f, Is.Zero.Within(1e-6f), $"Y, compensateXY {compensateXY}");
            });
        }
    }

    [Test]
    public void TheCorrectionFollowsTheToolsIdeaOfWhichAxesAreXAndY()
    {
        // An IDEX machine's second carriage is skewed by the same amount as the first
        Move move = NewMove(tanXZ: 0.002f, numAxes: 3);
        move.Axes.Add(new Axis { Letter = 'U', Visible = true });

        Tool tool = new();
        tool.Axes.Add([0, 3]);                  // X drives axes 0 and 3

        float[] coords = [10.0f, 20.0f, 50.0f, 30.0f];
        AxisSkew.Apply(tool, move, coords, 4);

        Assert.Multiple(() =>
        {
            Assert.That(coords[0], Is.EqualTo(10.1f).Within(1e-4f));
            Assert.That(coords[3], Is.EqualTo(30.1f).Within(1e-4f), "U is an X axis as far as the skew is concerned");
        });
    }

    [Test]
    public void TheLowestSetAxisIsTheOneTheCrossTermsRead()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AxisSkew.LowestSetAxis(0b1010u, 4), Is.EqualTo(1));
            Assert.That(AxisSkew.LowestSetAxis(0b1000u, 3), Is.EqualTo(-1), "past the axes the machine has");
            Assert.That(AxisSkew.LowestSetAxis(0u, 4), Is.EqualTo(-1));
        });
    }
}
