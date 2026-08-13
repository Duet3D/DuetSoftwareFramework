using DuetControlServer.Motion;
using DuetControlServer.Motion.Native;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// Taking a built move apart into the pieces its segments need
/// </summary>
/// <remarks>
/// The move's own coordinates are overwritten segment by segment as it is submitted, so where it
/// started and where it is going have to be kept somewhere else. That is all this is, and getting it
/// wrong loses the target rather than approximating it
/// </remarks>
[TestFixture]
public class SegmentedMoveTests
{
    private const int NumAxes = 3;
    private static readonly int FirstExtruderDrive = MotionLimits.MaxAxesPlusExtruders - 1;

    private static RawMove MoveTo(float x, float y, float z, float extrusion = 0.0f, int segments = 1)
    {
        RawMove raw = new() { SegmentCount = segments };
        raw.Coords[0] = x;
        raw.Coords[1] = y;
        raw.Coords[2] = z;
        raw.Coords[FirstExtruderDrive] = extrusion;
        return raw;
    }

    [Test]
    public void TheStartAndTargetAreKeptWhereTheMoveCannotOverwriteThem()
    {
        RawMove raw = MoveTo(10.0f, 20.0f, 5.0f, segments: 4);
        SegmentedMove segments = SegmentedMove.From(raw, [1.0f, 2.0f, 3.0f], NumAxes, FirstExtruderDrive);

        raw.Coords[0] = -999.0f;                // as submitting the first segment would

        Assert.Multiple(() =>
        {
            Assert.That(segments.Count, Is.EqualTo(4));
            Assert.That(segments.NumAxes, Is.EqualTo(NumAxes));
            Assert.That(segments.Start[..NumAxes], Is.EqualTo(new[] { 1.0f, 2.0f, 3.0f }));
            Assert.That(segments.Target[..NumAxes], Is.EqualTo(new[] { 10.0f, 20.0f, 5.0f }));
        });
    }

    [Test]
    public void TheExtrusionIsDividedBetweenTheSegmentsRatherThanRepeated()
    {
        // It belongs to the whole move, so each segment gets its share
        RawMove raw = MoveTo(10.0f, 0.0f, 0.0f, extrusion: 8.0f, segments: 4);
        SegmentedMove segments = SegmentedMove.From(raw, [0.0f, 0.0f, 0.0f], NumAxes, FirstExtruderDrive);

        Assert.That(segments.ExtrusionPerSegment[FirstExtruderDrive], Is.EqualTo(2.0f));
    }

    [Test]
    public void AMoveThatWasNeverSegmentedIsStillOnePiece()
    {
        // The count is what the submission loop runs to, so zero would submit nothing at all
        RawMove raw = MoveTo(10.0f, 0.0f, 0.0f, segments: 0);
        SegmentedMove segments = SegmentedMove.From(raw, [0.0f, 0.0f, 0.0f], NumAxes, FirstExtruderDrive);

        Assert.That(segments.Count, Is.EqualTo(1));
    }
}
