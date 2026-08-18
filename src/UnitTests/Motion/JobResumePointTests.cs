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
    private static MovePlanner NewPlanner() => new(null!, null!, NullLogger<MovePlanner>.Instance);

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
    public void AStopThatDroppedAMoveTakesTheCodeItCameFrom()
    {
        MovePlanner planner = NewPlanner();
        JobMoveOrigin origin = NewOrigin();
        for (int segment = 0; segment < 3; segment++)
        {
            planner.JobMoves.Note((uint)(segment + 1), origin, segment);
        }
        origin.SegmentsQueued = 3;
        planner.State.CurrentJobMove = origin;

        JobResumePoint? point = planner.TakeJobResumePoint(new MovePlanner.FeedholdOutcome(true, 2, 2));

        Assert.Multiple(() =>
        {
            Assert.That(point!.Value.FilePosition, Is.EqualTo(100L));
            Assert.That(point!.Value.ProportionDone, Is.EqualTo(0.1f).Within(1e-4f),
                        "the first dropped move is the boundary, not what the submission had queued");
        });
    }

    [Test]
    public void AStopThatPurgedNothingTakesWhatTheSubmissionHadQueued()
    {
        // Everything queued was already committed, so what the machine will make is every segment
        // that went out. This is also what a stop the engine refuses leaves behind: the queue drains,
        // the code that was going out ends where it ends, and the resume asks for the rest of it
        MovePlanner planner = NewPlanner();
        JobMoveOrigin origin = NewOrigin();
        origin.SegmentsQueued = 4;
        planner.State.CurrentJobMove = origin;

        JobResumePoint? point = planner.TakeJobResumePoint(new MovePlanner.FeedholdOutcome(false, 0, 0));

        Assert.Multiple(() =>
        {
            Assert.That(point!.Value.FilePosition, Is.EqualTo(100L));
            Assert.That(point!.Value.ProportionDone, Is.EqualTo(0.4f).Within(1e-4f));
        });
    }

    [Test]
    public void APurgeWhoseEarliestMoveCannotBeNamedTakesNothing()
    {
        // The earliest dropped move was a macro's, so the job's own code had not started. Its queued
        // segments went with the purge, so what it had submitted says nothing about where to resume
        MovePlanner planner = NewPlanner();
        JobMoveOrigin origin = NewOrigin();
        origin.SegmentsQueued = 4;
        planner.State.CurrentJobMove = origin;

        JobResumePoint? point = planner.TakeJobResumePoint(new MovePlanner.FeedholdOutcome(true, 9, 3));

        Assert.That(point, Is.Null, "the resume rewinds to the last completed job code");
    }

    [Test]
    public void NothingIsLeftForALaterPauseToFind()
    {
        // A pause sequence that goes no further than the stop has still taken the record, so a later
        // pause that makes no stop of its own - every synchronous one - cannot adopt what it left
        MovePlanner planner = NewPlanner();
        JobMoveOrigin origin = NewOrigin();
        origin.SegmentsQueued = 4;
        planner.State.CurrentJobMove = origin;
        planner.JobMoves.Note(1, origin, 0);

        Assert.That(planner.TakeJobResumePoint(new MovePlanner.FeedholdOutcome(false, 0, 0)), Is.Not.Null);

        Assert.Multiple(() =>
        {
            Assert.That(planner.TakeJobResumePoint(default), Is.Null);
            Assert.That(planner.State.CurrentJobMove, Is.Null);
            Assert.That(planner.JobMoves.TryGet(1, out _, out _), Is.False,
                        "the moves it described are no longer the pause's business either");
        });
    }

    [Test]
    public void APauseBetweenCodesTakesNothing()
    {
        MovePlanner planner = NewPlanner();

        Assert.That(planner.TakeJobResumePoint(default), Is.Null);
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
