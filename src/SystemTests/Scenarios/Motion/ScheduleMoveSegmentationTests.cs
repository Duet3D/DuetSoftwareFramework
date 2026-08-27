using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Motion;

/// <summary>
/// How segmentation changes what reaches the link: one commanded move arriving as several scheduled
/// ones, and which moves are cut up at all.
/// </summary>
/// <remarks>
/// <para>
/// A geometry that maps axis space onto its motors non-linearly cannot draw a straight line by
/// transforming its two ends, so the move is chopped into pieces short enough that the bow is below
/// a step. M669 S and T mean the same thing on every geometry and turn it on and off, including on a
/// Cartesian, which is what lets these scenarios compare the two against one machine. How many
/// pieces follows from the two of them: the move's duration times S, capped by its length divided
/// by T, as <c>MoveInterpreter.SegmentCountFor</c> ports from RepRapFirmware.
/// </para>
/// <para>
/// The moves themselves are the same either way, which is what the rest of the ScheduleMove
/// scenarios assert by running under both settings. Here it is the count and the division that are
/// under test
/// </para>
/// </remarks>
[TestFixture]
public class ScheduleMoveSegmentationTests : BenchFixture
{
    private const byte XDriver = 0, ZDriver = 2, EDriver = 3;

    [Test]
    public async Task MoveIsScheduledInOnePieceWithSegmentationOff()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(segmented: false);

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X10 F6000");

        Assert.Multiple(() =>
        {
            Assert.That(moves, Has.Count.EqualTo(1), "a Cartesian draws a straight line without help");
            Assert.That(moves.Distance(), Is.EqualTo(10.0f).Within(1e-3f));
            Assert.That(moves.Steps(XDriver), Is.EqualTo(10 * ScheduleMoveBench.XyStepsPerMm));
        });
    }

    [Test]
    public async Task SegmentedMoveIsCutIntoAsManyPiecesAsItsDurationAsksFor()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(segmented: true);

        // 10 mm at 100 mm/s is a tenth of a second, and 100 segments per second of it is ten pieces
        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X10 F6000");
        const int expected = (int)(10.0f / (6000.0f / 60.0f) * ScheduleMoveBench.SegmentsPerSecond);

        Assert.Multiple(() =>
        {
            Assert.That(moves, Has.Count.EqualTo(expected), "the duration is what decides, being the tighter of the two");
            Assert.That(moves.Distance(), Is.EqualTo(10.0f).Within(1e-3f),
                        "the pieces still add up to the move that was commanded");
            Assert.That(moves.Steps(XDriver), Is.EqualTo(10 * ScheduleMoveBench.XyStepsPerMm),
                        "and to the same steps, which is what the motors actually do");
        });
        foreach (ScheduledMove move in moves)
        {
            Assert.That(move.Header.TotalDistance, Is.EqualTo(10.0f / expected).Within(1e-3f),
                        "the move is divided evenly");
        }
    }

    [Test]
    public async Task SlowMoveIsNotCutFinerThanTheShortestSegmentAllowed()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(segmented: true);

        // Four tenths of a second of movement would be 40 pieces, but 0.2 mm each allows only 20
        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X4 F600");
        const int expected = (int)(4.0f / ScheduleMoveBench.MinSegmentLength);

        Assert.Multiple(() =>
        {
            Assert.That(moves, Has.Count.EqualTo(expected),
                        "the minimum segment length is what decides, being the tighter of the two");
            Assert.That(moves.Distance(), Is.EqualTo(4.0f).Within(1e-2f));
            Assert.That(moves.Steps(XDriver), Is.EqualTo(4 * ScheduleMoveBench.XyStepsPerMm));
        });
    }

    [Test]
    public async Task SegmentsRunBackToBackAndTheirSpeedsJoinUp()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(segmented: true);

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X10 F6000");

        Assert.That(moves, Has.Count.GreaterThan(1), "the move was segmented");
        Assert.Multiple(() =>
        {
            Assert.That(moves[0].Header.StartSpeed, Is.Zero, "the first piece starts from rest");
            Assert.That(moves[^1].Header.EndSpeed, Is.Zero, "and the last ends at rest");
            for (int i = 1; i < moves.Count; i++)
            {
                Assert.That(moves[i].StartTime, Is.EqualTo(moves[i - 1].StartTime + moves[i - 1].TotalClocks),
                            $"piece {i + 1} starts as piece {i} ends");
                Assert.That(moves[i].Header.StartSpeed, Is.EqualTo(moves[i - 1].Header.EndSpeed).Within(1e-9f),
                            $"and at the speed piece {i} left off at, so the head does not step in speed");
            }
            Assert.That(ScheduleMoveBench.MmPerSecond(moves.Max(move => move.Header.TopSpeed)),
                        Is.EqualTo(MathF.Sqrt(2.0f * ScheduleMoveBench.XyAcceleration * 5.0f)).Within(0.01f),
                        "and the pieces together reach the same top speed the whole move would have");
        });
    }

    [Test]
    public async Task SegmentedPrintingMoveDividesItsExtrusionBetweenThePieces()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(segmented: true);

        // 4 mm at 50 mm/s is 0.08 s, which is eight pieces
        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X4 E0.4 F3000");
        const int expected = (int)(4.0f / (3000.0f / 60.0f) * ScheduleMoveBench.SegmentsPerSecond);
        float extrusionPerSegment = 0.4f * ScheduleMoveBench.EStepsPerMm / expected;

        Assert.Multiple(() =>
        {
            Assert.That(moves, Has.Count.EqualTo(expected));
            Assert.That(moves.Extrusion(EDriver), Is.EqualTo(0.4f * ScheduleMoveBench.EStepsPerMm).Within(1e-2f),
                        "the whole extrusion still reaches the extruder");
            Assert.That(moves.Steps(XDriver), Is.EqualTo(4 * ScheduleMoveBench.XyStepsPerMm));
        });
        foreach (ScheduledMove move in moves)
        {
            Assert.That(move.DriverFor(ScheduleMoveBench.DriverBoard, EDriver)!.Value.Extrusion,
                        Is.EqualTo(extrusionPerSegment).Within(1e-2f),
                        "each piece extrudes its own share, so the extrusion keeps pace with the axis");
        }
    }

    [Test]
    public async Task MoveThatSegmentationDoesNotApplyToIsScheduledWhole()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(segmented: true);

        IReadOnlyList<ScheduledMove> travel = await bench.RunMoveAsync("G0 X10");
        IReadOnlyList<ScheduledMove> upwards = await bench.RunMoveAsync("G1 Z1 F600");
        IReadOnlyList<ScheduledMove> extruding = await bench.RunMoveAsync("G1 E5 F1200");

        Assert.Multiple(() =>
        {
            Assert.That(travel, Has.Count.EqualTo(1),
                        "G0 is not a coordinated move, and a Cartesian does not ask for those to be segmented");
            Assert.That(travel.Steps(XDriver), Is.EqualTo(10 * ScheduleMoveBench.XyStepsPerMm));
            Assert.That(upwards, Has.Count.EqualTo(1),
                        "the length that is segmented is the one in the plane the geometry bows in, and Z is not in it");
            Assert.That(upwards.Steps(ZDriver), Is.EqualTo(1 * ScheduleMoveBench.ZStepsPerMm));
            Assert.That(extruding, Has.Count.EqualTo(1), "an extruder-only move has no such length at all");
            Assert.That(extruding.Extrusion(EDriver), Is.EqualTo(5 * ScheduleMoveBench.EStepsPerMm).Within(1e-2f));
        });
    }

    [Test]
    public async Task EndstopMoveIsRunInOnePieceEvenWithSegmentationOn()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(
            segmented: true, configExtra: "M574 X1 S1 P\"1.io0.in\"");
        Task<string> homing = bench.Host.ExecuteCodeAsync("G1 H1 X-20 F3000");

        IReadOnlyList<ScheduledMove> moves = await bench.WaitForMovesAsync(1, "the homing move arriving armed");
        await Task.Delay(250);                          // long enough for a second piece to have arrived

        moves = bench.CanMaster.ScheduledMoves();
        Assert.Multiple(() =>
        {
            Assert.That(moves, Has.Count.EqualTo(1),
                        "a move that watches an endstop runs in one piece, whatever the geometry asks for");
            Assert.That(moves.Steps(XDriver), Is.EqualTo(-20 * ScheduleMoveBench.XyStepsPerMm));
        });

        bench.StopMove(moves[0]);
        await homing;
    }

    [Test]
    public async Task SegmentationChangesThePiecesAndNothingElse()
    {
        await using JobBench whole = await ScheduleMoveBench.StartCartesianAsync(segmented: false);
        await using JobBench pieces = await ScheduleMoveBench.StartCartesianAsync(segmented: true);

        const string code = "G1 X10 Y5 E0.5 F3000";
        IReadOnlyList<ScheduledMove> unsegmented = await whole.RunMoveAsync(code);
        IReadOnlyList<ScheduledMove> segmented = await pieces.RunMoveAsync(code);

        Assert.Multiple(() =>
        {
            Assert.That(unsegmented, Has.Count.EqualTo(1), "the same code, in one piece");
            Assert.That(segmented, Has.Count.GreaterThan(1), "and cut up");
            Assert.That(segmented.Steps(XDriver), Is.EqualTo(unsegmented.Steps(XDriver)),
                        "the motors are told to do the same thing either way");
            Assert.That(segmented.Steps(1), Is.EqualTo(unsegmented.Steps(1)));
            Assert.That(segmented.Extrusion(EDriver), Is.EqualTo(unsegmented.Extrusion(EDriver)).Within(1e-2f));
            Assert.That(segmented.Distance(), Is.EqualTo(unsegmented.Distance()).Within(1e-2f));
            Assert.That(segmented.Select(move => move.Header.Flags).Distinct(),
                        Is.EqualTo(new[] { unsegmented[0].Header.Flags }),
                        "and every piece describes the same kind of move as the whole one");
        });
    }

    [Test]
    public async Task MoveTooLongForTheStepClockIsSplitWhateverTheGeometrySays()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(segmented: false);

        // 100 mm at 5 mm/min takes twenty minutes, and the step clock wraps in about forty-five;
        // five minutes each is what MoveInterpreter allows, so this is four pieces
        await bench.Host.ExecuteCodeAsync("G1 X100 F5");
        IReadOnlyList<ScheduledMove> moves = await bench.WaitForMovesAsync(1, "the first piece of the slow move");

        Assert.That(moves[0].Header.TotalDistance, Is.EqualTo(25.0f).Within(1e-2f),
                    "a quarter of the move, so that no piece lasts longer than five minutes");

        // The remaining pieces are left queued rather than stopped: the machine goes away with the
        // bench, and an M112 here would cost thirty seconds, because tearing the link down makes
        // DCS see a controller reset and run config.g again while the host is trying to stop
    }
}
