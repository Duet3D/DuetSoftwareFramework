using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using NUnit.Framework;

namespace UnitTests.Motion;

/// <summary>
/// The step between the coordinates a G-code names and the coordinates the machine is driven to
/// </summary>
/// <remarks>
/// A user coordinate says where the nozzle should be and a machine coordinate says where the head
/// reference point should be, so everything here is about the difference between the two: the tool's
/// offsets, its Z hop while retracted, babystepping, and which axes a letter actually drives
/// </remarks>
[TestFixture]
public class ToolTransformTests
{
    /// <summary>A machine with as many axes as the test needs, named in order from XYZUV</summary>
    private static Move NewMove(int numAxes = 3)
    {
        Move move = new();
        foreach (char letter in "XYZUV"[..numAxes])
        {
            move.Axes.Add(new Axis { Letter = letter, Visible = true });
        }
        return move;
    }

    /// <summary>An interpreter sitting at the given user coordinates</summary>
    private static MovementState At(params float[] position)
    {
        MovementState state = new();
        position.CopyTo(state.CurrentUserPosition, 0);
        return state;
    }

    /// <summary>A tool with the given offsets, and no axis mapping of its own</summary>
    private static Tool NewTool(params float[] offsets)
    {
        Tool tool = new();
        foreach (float offset in offsets)
        {
            tool.Offsets.Add(offset);
        }
        return tool;
    }

    [Test]
    public void WithNoToolTheMachineGoesWhereTheUserSaidPlusBabystep()
    {
        Move move = NewMove();
        move.Axes[2].Babystep = -0.05f;

        float[] coords = new float[3];
        ToolTransform.Apply(null, move, At(10.0f, 20.0f, 5.0f), coords, 3);

        Assert.Multiple(() =>
        {
            Assert.That(coords[0], Is.EqualTo(10.0f));
            Assert.That(coords[1], Is.EqualTo(20.0f));
            Assert.That(coords[2], Is.EqualTo(4.95f).Within(1e-5f));
        });
    }

    [Test]
    public void ReachingACoordinateMovesTheHeadTheOtherWayByTheOffset()
    {
        // The offset is where the nozzle is relative to the head reference point
        Move move = NewMove();
        Tool tool = NewTool(3.0f, -2.0f, 0.5f);

        float[] coords = new float[3];
        ToolTransform.Apply(tool, move, At(10.0f, 20.0f, 5.0f), coords, 3);

        Assert.Multiple(() =>
        {
            Assert.That(coords[0], Is.EqualTo(7.0f));
            Assert.That(coords[1], Is.EqualTo(22.0f));
            Assert.That(coords[2], Is.EqualTo(4.5f));
        });
    }

    [Test]
    public void TheZHopOnlyLiftsWhileTheToolIsRetracted()
    {
        // Which is what makes it a lift rather than a permanent offset
        Move move = NewMove();
        Tool tool = NewTool(0.0f, 0.0f, 0.0f);
        tool.Retraction.ZHop = 0.4f;

        float[] down = new float[3], up = new float[3];
        ToolTransform.Apply(tool, move, At(0.0f, 0.0f, 5.0f), down, 3);
        tool.IsRetracted = true;
        ToolTransform.Apply(tool, move, At(0.0f, 0.0f, 5.0f), up, 3);

        Assert.Multiple(() =>
        {
            Assert.That(down[2], Is.EqualTo(5.0f));
            Assert.That(up[2], Is.EqualTo(5.4f).Within(1e-5f));
        });
    }

    [Test]
    public void OneXWordDrivesEveryAxisTheToolMapsItTo()
    {
        // An IDEX machine, where X drives both carriages
        Move move = NewMove(4);
        Tool tool = NewTool(0.0f, 0.0f, 0.0f, 1.5f);
        tool.Axes.Add([0, 3]);                  // X drives axes 0 and 3

        float[] coords = new float[4];
        ToolTransform.Apply(tool, move, At(10.0f, 20.0f, 5.0f, 99.0f), coords, 4);

        Assert.Multiple(() =>
        {
            Assert.That(coords[0], Is.EqualTo(10.0f), "the axis literally called X");
            Assert.That(coords[3], Is.EqualTo(8.5f), "and U, which reads X's coordinate less its own offset");
        });
    }

    [Test]
    public void AnAxisXIsMappedAwayFromKeepsWhatItHeld()
    {
        // With X mapped to U alone the X slot is not a machine position at all, so writing one would
        // move an axis the tool does not drive
        Move move = NewMove(4);
        Tool tool = NewTool(0.0f, 0.0f, 0.0f, 0.0f);
        tool.Axes.Add([3]);                     // X drives U only

        float[] coords = [-1.0f, -1.0f, -1.0f, -1.0f];
        ToolTransform.Apply(tool, move, At(10.0f, 20.0f, 5.0f), coords, 4);

        Assert.Multiple(() =>
        {
            Assert.That(coords[0], Is.EqualTo(-1.0f), "untouched");
            Assert.That(coords[3], Is.EqualTo(10.0f), "and U took the X coordinate");
        });
    }

    [Test]
    public void AnAxisTheCodeNamedReadsItsOwnCoordinate()
    {
        // What explicitAxes selects: the input axis under a map, not which axes get written
        Move move = NewMove(4);
        Tool tool = NewTool(0.0f, 0.0f, 0.0f, 0.0f);
        tool.Axes.Add([0, 3]);

        float[] mapped = new float[4], named = new float[4];
        MovementState state = At(10.0f, 20.0f, 5.0f, 40.0f);

        ToolTransform.Apply(tool, move, state, mapped, 4);
        ToolTransform.Apply(tool, move, state, named, 4, explicitAxes: 1u << 3);

        Assert.Multiple(() =>
        {
            Assert.That(mapped[3], Is.EqualTo(10.0f), "U follows X through the map");
            Assert.That(named[3], Is.EqualTo(40.0f), "unless the code named U itself");
        });
    }

    [Test]
    public void TheInverseReportsTheMeanOfAMapsAxes()
    {
        // There is no single coordinate to come back to, so the mean is the only defensible answer
        // when the mapped axes disagree
        Move move = NewMove(4);
        Tool tool = NewTool(0.0f, 0.0f, 0.0f, 0.0f);
        tool.Axes.Add([0, 3]);

        float[] coords = [10.0f, 0.0f, 0.0f, 20.0f];
        ToolTransform.Remove(tool, move, coords, 4);

        Assert.That(coords[0], Is.EqualTo(15.0f));
    }

    [Test]
    public void ApplyingAndRemovingATransformGivesBackWhatWasAskedFor()
    {
        Move move = NewMove();
        move.Axes[2].Babystep = -0.05f;
        Tool tool = NewTool(3.0f, -2.0f, 0.5f);
        tool.Retraction.ZHop = 0.4f;
        tool.IsRetracted = true;

        float[] coords = new float[3];
        ToolTransform.Apply(tool, move, At(10.0f, 20.0f, 5.0f), coords, 3);
        ToolTransform.Remove(tool, move, coords, 3);

        Assert.Multiple(() =>
        {
            Assert.That(coords[0], Is.EqualTo(10.0f).Within(1e-4f));
            Assert.That(coords[1], Is.EqualTo(20.0f).Within(1e-4f));
            Assert.That(coords[2], Is.EqualTo(5.0f).Within(1e-4f));
        });
    }

    [Test]
    public void ALetterWithNoToolNamesTheAxesThatCarryIt()
    {
        Move move = NewMove(4);
        move.Axes[3].Letter = 'X';              // a second axis called X, as M584 X0:3 would give

        Assert.Multiple(() =>
        {
            Assert.That(ToolTransform.AxisBitmap(null, move, 'X'), Is.EqualTo(0b1001u));
            Assert.That(ToolTransform.AxisBitmap(null, move, 'Y'), Is.EqualTo(0b0010u));
            Assert.That(ToolTransform.AxisBitmap(null, move, 'A'), Is.Zero, "a letter the machine does not have");
        });
    }

    [Test]
    public void ALetterWithAToolNamesWhateverTheToolMapsItTo()
    {
        // Which is what makes a move that reached U through the X map still count as XY movement
        Move move = NewMove(4);
        Tool tool = NewTool();
        tool.Axes.Add([3]);

        Assert.Multiple(() =>
        {
            Assert.That(ToolTransform.AxisBitmap(tool, move, 'X'), Is.EqualTo(1u << 3));
            Assert.That(ToolTransform.AxisBitmap(tool, move, 'Y'), Is.EqualTo(1u << 1), "a letter the tool has no map for drives its own axis");
        });
    }

    [Test]
    public void AnOffsetTheToolDoesNotRecordIsZero()
    {
        Tool tool = NewTool(1.0f);

        Assert.Multiple(() =>
        {
            Assert.That(ToolTransform.Offset(tool, 0), Is.EqualTo(1.0f));
            Assert.That(ToolTransform.Offset(tool, 1), Is.Zero, "past the end of the list");
            Assert.That(ToolTransform.Offset(tool, -1), Is.Zero);
        });
    }
}
