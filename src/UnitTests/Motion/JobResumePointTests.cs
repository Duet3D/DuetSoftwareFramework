using DuetAPI;
using DuetControlServer.Motion;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Code = DuetAPI.Commands.Code;

namespace UnitTests.Motion;

/// <summary>
/// What a stop tells the job file about where to carry on from
/// </summary>
/// <remarks>
/// Where to rewind to and how much of the code at that position is already made are two halves of one
/// fact, so what is checked here is mostly that they always come from the same record: the two cannot
/// describe different lines, and a fraction that names no position cannot be produced at all
/// </remarks>
[TestFixture]
public class JobResumePointTests
{
    /// <summary>A planner with only the state a resume point is taken from</summary>
    private static MovePlanner NewPlanner() => new(null!, null!, null!, NullLogger<MovePlanner>.Instance);

    /// <summary>A job code the interpreter is part-way through</summary>
    private static JobMoveOrigin NewOrigin(int segmentCount = 10, float fractionAtStart = 0.0f)
        => new()
        {
            FilePosition = 100,
            CodeLength = 20,
            GCommandNumber = 1,
            FeedRateMmPerSec = 50.0f,
            FractionAtStart = fractionAtStart,
            SegmentCount = segmentCount
        };

    [Test]
    public void TheFractionIsOfTheWholeCode()
    {
        JobResumePoint? point = NewOrigin().PointAt(4);

        Assert.That(point, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(point!.Value.FilePosition, Is.EqualTo(100L), "the code is read again from its start");
            Assert.That(point!.Value.ProportionDone, Is.EqualTo(0.4f).Within(1e-4f));
            Assert.That(point!.Value.GCommandNumber, Is.EqualTo(1));
            Assert.That(point!.Value.FeedRateMmPerSec, Is.EqualTo(50.0f));
        });
    }

    [Test]
    public void TheFractionComposesWhenTheCodeIsItselfARemainder()
    {
        // A resume builds only what is left of the code, so a second stop inside the same code
        // measures its segments against that remainder. What the file is owed is still counted from
        // the whole code, which is where RepRapFirmware's totalSegments always counts from
        JobResumePoint? point = NewOrigin(segmentCount: 6, fractionAtStart: 0.4f).PointAt(3);

        Assert.That(point!.Value.ProportionDone, Is.EqualTo(0.7f).Within(1e-4f));
    }

    [Test]
    public void ACodeThatWentOutWholeResumesAtTheCodeAfterIt()
    {
        // Everything queued is committed and will run, so there is nothing of this code left to ask
        // for. Rewinding to it and skipping all of it would ask the machine for a move of no length
        JobResumePoint? point = NewOrigin().PointAt(10);

        Assert.Multiple(() =>
        {
            Assert.That(point!.Value.FilePosition, Is.EqualTo(120L));
            Assert.That(point!.Value.ProportionDone, Is.Zero);
        });
    }

    [Test]
    public void ACodeWithNoFilePositionNamesNothing()
    {
        JobMoveOrigin origin = new() { FilePosition = null, SegmentCount = 4 };

        Assert.That(origin.PointAt(2), Is.Null, "a fraction that names no position cannot be expressed");
    }

    [Test]
    public void TheRewindPointIsTheSegmentAfterTheOneTheMachineStopsOn()
    {
        // The rule reads what survives, never what was dropped: the machine comes to rest at the end
        // of the surviving move, so what is left of the code is everything after it
        MovePlanner planner = NewPlanner();
        JobMoveOrigin origin = NewOrigin();
        for (int segment = 0; segment < 5; segment++)
        {
            planner.JobMoves.Note((uint)(segment + 1), origin, segment);
        }

        MovePlanner.JobRewindPoint rewind = planner.RewindPointAfter(3);

        Assert.Multiple(() =>
        {
            Assert.That(rewind.Point!.Value.FilePosition, Is.EqualTo(100L));
            Assert.That(rewind.Point!.Value.ProportionDone, Is.EqualTo(0.3f).Within(1e-4f),
                        "three of the ten segments have been made");
            Assert.That(rewind.RestartMacro, Is.False);
        });
    }

    [Test]
    public void AMoveOfAMacroTheJobInvokedRewindsToTheInvocation()
    {
        // The macro's own offsets are into the macro, so the only position in the job file that
        // means anything is the code that started it - and the whole macro runs again
        MovePlanner planner = NewPlanner();
        JobMoveOrigin origin = new()
        {
            FilePosition = 100,
            CodeLength = 20,
            GCommandNumber = -1,
            SegmentCount = 4,
            IsMacroInvocation = true
        };
        planner.JobMoves.Note(7, origin, 3);

        MovePlanner.JobRewindPoint rewind = planner.RewindPointAfter(7);

        Assert.Multiple(() =>
        {
            Assert.That(rewind.Point!.Value.FilePosition, Is.EqualTo(100L),
                        "the invocation, not the code after it, however many of its moves were made");
            Assert.That(rewind.Point!.Value.ProportionDone, Is.Zero);
            Assert.That(rewind.RestartMacro, Is.True);
        });
    }

    [Test]
    public void AMoveNoJobCodeProducedNamesNothing()
    {
        // A move from another channel: the stop says nothing about the job file, and where the
        // reader got to stands
        MovePlanner planner = NewPlanner();
        planner.JobMoves.Note(1, NewOrigin(), 0);

        MovePlanner.JobRewindPoint rewind = planner.RewindPointAfter(9);

        Assert.Multiple(() =>
        {
            Assert.That(rewind.Point, Is.Null);
            Assert.That(rewind.RestartMacro, Is.False);
        });
    }

    [Test]
    public void TheIndexSurvivesAPauseSoTheNextLookupStillHits()
    {
        // The entry a pause needs describes the move the engine says survives, and that move has
        // usually completed by the time this side reads the feedhold result. Clearing on a pause
        // would discard exactly what the next lookup wants
        MovePlanner planner = NewPlanner();
        planner.JobMoves.Note(1, NewOrigin(), 0);

        Assert.That(planner.RewindPointAfter(1).Point, Is.Not.Null);
        Assert.That(planner.RewindPointAfter(1).Point, Is.Not.Null, "a second pause finds it too");
    }

    [Test]
    public void TheIndexNamesTheCodeAMoveCameFromAndItsPlaceInIt()
    {
        JobMoveIndex index = new();
        JobMoveOrigin origin = NewOrigin();
        index.Note(4, origin, 2);

        Assert.That(index.TryGet(4, out JobMoveOrigin found, out int segment), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(found, Is.SameAs(origin), "one record for the whole code, not a copy per move");
            Assert.That(segment, Is.EqualTo(2));
        });
    }

    [TestCase(CodeChannel.File, true)]
    [TestCase(CodeChannel.File2, false)]
    [TestCase(CodeChannel.Daemon, false)]
    public void OnlyTheFirstFileChannelIsTheJob(CodeChannel channel, bool expected)
    {
        Code code = new("G1 X10") { Channel = channel };

        Assert.That(JobMoveOrigin.IsJobFileCode(code), Is.EqualTo(expected));
    }
}
