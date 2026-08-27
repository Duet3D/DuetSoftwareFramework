using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Motion;

/// <summary>
/// The velocity profile a scheduled move carries: how fast it goes, how hard it accelerates, how
/// long each phase lasts and when the boards are to start it.
/// </summary>
/// <remarks>
/// <para>
/// The packet carries the profile in step clocks and millimetres, which is what DuetCANMaster's
/// <c>PrepParams</c> wants, so every expectation here is converted from the mm/s and mm/s² the
/// configuration is written in. What the numbers should be follows from the configured limits: the
/// requested feed rate capped by M203, the acceleration capped by M201 and M204, and the junction
/// speeds capped by the M566 jerk, exactly as RepRapFirmware's <c>DDA</c> plans a move.
/// </para>
/// <para>
/// These scenarios run without segmentation, because the profile of a whole move is what they are
/// about; how a segmented move divides one profile between its pieces is in
/// <see cref="ScheduleMoveSegmentationTests"/>
/// </para>
/// </remarks>
[TestFixture]
public class ScheduleMoveProfileTests : BenchFixture
{
    /// <summary>Read a header speed in mm/s</summary>
    private static float Speed(float wireSpeed) => ScheduleMoveBench.MmPerSecond(wireSpeed);

    /// <summary>Read a header acceleration in mm/s²</summary>
    private static float Acceleration(float wireAcceleration) => ScheduleMoveBench.MmPerSecondSquared(wireAcceleration);

    /// <summary>Read a phase duration in seconds</summary>
    private static float Duration(uint clocks) => ScheduleMoveBench.Seconds(clocks);

    /// <summary>The one move a code scheduled, when the scenario expects exactly one</summary>
    private static ScheduledMove Only(IReadOnlyList<ScheduledMove> moves)
    {
        Assert.That(moves, Has.Count.EqualTo(1), "the code scheduled exactly one move");
        return moves[0];
    }

    [Test]
    public async Task MoveTooShortToReachItsFeedRateAcceleratesAndDeceleratesOnly()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync();

        // 10 mm at 500 mm/s² tops out at sqrt(2 * 500 * 5) = 70.7 mm/s, short of the 100 mm/s asked for
        ScheduleMoveHeader header = Only(await bench.RunMoveAsync("G1 X10 F6000")).Header;
        float expectedTop = MathF.Sqrt(2.0f * ScheduleMoveBench.XyAcceleration * 5.0f);

        Assert.Multiple(() =>
        {
            Assert.That(Speed(header.TopSpeed), Is.EqualTo(expectedTop).Within(0.01f),
                        "the move only reaches the speed it can accelerate to by half way");
            Assert.That(Speed(header.StartSpeed), Is.Zero, "nothing precedes it, so it starts from rest");
            Assert.That(Speed(header.EndSpeed), Is.Zero, "nothing follows it, so it ends at rest");
            Assert.That(header.SteadyClocks, Is.Zero, "there is no constant-speed phase between the two ramps");
            Assert.That(Duration(header.AccelClocks),
                        Is.EqualTo(expectedTop / ScheduleMoveBench.XyAcceleration).Within(1e-3f),
                        "the ramp lasts as long as reaching that speed takes");
            Assert.That(Duration(header.DecelClocks), Is.EqualTo(Duration(header.AccelClocks)).Within(1e-3f),
                        "the two ramps are symmetrical");
            Assert.That(header.AccelDistance, Is.EqualTo(5.0f).Within(1e-2f), "it accelerates for half the move");
            Assert.That(header.DecelStartDistance, Is.EqualTo(5.0f).Within(1e-2f),
                        "and starts braking where it stopped accelerating");
        });
    }

    [Test]
    public async Task LongMoveHoldsTheRequestedFeedRateInBetweenTheRamps()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync();

        const float distance = 40.0f, top = 6000.0f / 60.0f;
        ScheduleMoveHeader header = Only(await bench.RunMoveAsync($"G1 X{distance} F6000")).Header;
        float rampDistance = top * top / (2.0f * ScheduleMoveBench.XyAcceleration);

        Assert.Multiple(() =>
        {
            Assert.That(Speed(header.TopSpeed), Is.EqualTo(top).Within(0.01f), "F6000 is 100 mm/s");
            Assert.That(header.TotalDistance, Is.EqualTo(distance).Within(1e-3f));
            Assert.That(header.AccelDistance, Is.EqualTo(rampDistance).Within(1e-2f),
                        "reaching 100 mm/s at 500 mm/s² takes 10 mm");
            Assert.That(header.DecelStartDistance, Is.EqualTo(distance - rampDistance).Within(1e-2f),
                        "and braking from it takes the last 10 mm");
            Assert.That(Duration(header.AccelClocks),
                        Is.EqualTo(top / ScheduleMoveBench.XyAcceleration).Within(1e-3f));
            Assert.That(Duration(header.SteadyClocks),
                        Is.EqualTo((distance - (2.0f * rampDistance)) / top).Within(1e-3f),
                        "the 20 mm in between is covered at the top speed");
            Assert.That(Duration(header.DecelClocks), Is.EqualTo(Duration(header.AccelClocks)).Within(1e-3f));
        });
    }

    [Test]
    public async Task AccelerationIsPositiveAndDecelerationIsItsNegative()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync();

        ScheduleMoveHeader header = Only(await bench.RunMoveAsync("G1 X10 F6000")).Header;

        Assert.Multiple(() =>
        {
            Assert.That(Acceleration(header.Acceleration), Is.EqualTo(ScheduleMoveBench.XyAcceleration).Within(0.1f),
                        "M201 X500 is the axis' limit");
            Assert.That(Acceleration(header.Deceleration), Is.EqualTo(-ScheduleMoveBench.XyAcceleration).Within(0.1f),
                        "the deceleration is carried negative, as DuetCANMaster's PrepParams expects");
        });
    }

    [Test]
    public async Task FeedRateIsCappedByTheAxisMaximum()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync();

        // M203 Z600 caps Z at 10 mm/s however fast the code asks for. Two millimetres is four times
        // the half a millimetre it takes to reach that at M201 Z100, so the cap is what shows
        ScheduleMoveHeader header = Only(await bench.RunMoveAsync("G1 Z2 F6000")).Header;

        Assert.That(Speed(header.TopSpeed), Is.EqualTo(600.0f / 60.0f).Within(0.01f),
                    "the requested 100 mm/s is cut to the axis' 10 mm/s");
    }

    [Test]
    public async Task AccelerationIsTheTightestLimitOfTheAxesTheMoveTouches()
    {
        // Y is the slow axis of this machine, and a diagonal is held back by it
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(configExtra: "M201 Y100");

        ScheduleMoveHeader header = Only(await bench.RunMoveAsync("G1 X10 Y10 F6000")).Header;

        Assert.That(Acceleration(header.Acceleration), Is.EqualTo(100.0f / (1.0f / MathF.Sqrt(2.0f))).Within(0.1f),
                    "Y covers 1/sqrt(2) of every mm of the diagonal, so its 100 mm/s² allows 141 mm/s² along it");
    }

    [Test]
    public async Task PrintingAndTravelMovesTakeTheirOwnAccelerationLimits()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(configExtra: "M204 P200 T400");

        ScheduleMoveHeader printing = Only(await bench.RunMoveAsync("G1 X20 E1 F6000")).Header;
        ScheduleMoveHeader travel = Only(await bench.RunMoveAsync("G0 X20 F6000")).Header;

        Assert.Multiple(() =>
        {
            Assert.That(Acceleration(printing.Acceleration), Is.EqualTo(200.0f).Within(0.1f),
                        "a move that extrudes forwards is a printing move, so M204 P applies");
            Assert.That(Acceleration(travel.Acceleration), Is.EqualTo(400.0f).Within(0.1f),
                        "a move that does not is a travel move, so M204 T applies");
        });
    }

    [Test]
    public async Task ExtruderOnlyMoveMeasuresItselfInFilament()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync();

        ScheduleMoveHeader header = Only(await bench.RunMoveAsync("G1 E5 F1200")).Header;

        Assert.Multiple(() =>
        {
            Assert.That(header.TotalDistance, Is.EqualTo(5.0f).Within(1e-3f),
                        "with no axis moving, the distance is the length of filament");
            Assert.That(Speed(header.TopSpeed), Is.EqualTo(1200.0f / 60.0f).Within(0.01f),
                        "and the feed rate is how fast that filament moves");
            Assert.That(Acceleration(header.Acceleration), Is.EqualTo(250.0f).Within(0.1f),
                        "M201 E250 is the extruder's own limit");
        });
    }

    /// <summary>
    /// Four moves in one macro, so that they are planned together: two collinear, then a right-angle
    /// corner, then a reversal
    /// </summary>
    private const string CornerMacro = """
        G91
        G1 X10 F3000
        G1 X10 F3000
        G1 Y10 F3000
        G1 Y-10 F3000
        G90
        """;

    [Test]
    public async Task MovesAreScheduledToRunOneStraightAfterAnother()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(
            prepareSd: sd => sd.WriteMacro("corners.g", CornerMacro));

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("M98 P\"0:/macros/corners.g\"");

        Assert.That(moves, Has.Count.EqualTo(4), "one move per G1");
        for (int i = 1; i < moves.Count; i++)
        {
            Assert.That(moves[i].StartTime, Is.EqualTo(moves[i - 1].StartTime + moves[i - 1].TotalClocks),
                        $"move {i + 1} starts exactly as move {i} ends, so the machine never pauses between them");
        }
    }

    [Test]
    public async Task JunctionSpeedsAreWhatTheJerkLimitAllows()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(
            prepareSd: sd => sd.WriteMacro("corners.g", CornerMacro));

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("M98 P\"0:/macros/corners.g\"");

        Assert.That(moves, Has.Count.EqualTo(4), "one move per G1");
        Assert.Multiple(() =>
        {
            Assert.That(Speed(moves[0].Header.StartSpeed), Is.Zero, "the first move starts from rest");
            Assert.That(Speed(moves[0].Header.EndSpeed), Is.EqualTo(3000.0f / 60.0f).Within(0.01f),
                        "two moves in a straight line join at full speed: neither axis changes speed at all");
            Assert.That(Speed(moves[1].Header.StartSpeed), Is.EqualTo(Speed(moves[0].Header.EndSpeed)).Within(0.01f),
                        "and the second is told to start where the first ends");
            Assert.That(Speed(moves[1].Header.EndSpeed), Is.EqualTo(ScheduleMoveBench.XyJerk).Within(0.01f),
                        "a right-angle corner changes each axis' speed by the whole junction speed, "
                        + "so the jerk limit is what it may be");
            Assert.That(Speed(moves[2].Header.StartSpeed), Is.EqualTo(Speed(moves[1].Header.EndSpeed)).Within(0.01f));
            Assert.That(Speed(moves[2].Header.EndSpeed), Is.EqualTo(ScheduleMoveBench.XyJerk / 2.0f).Within(0.01f),
                        "a reversal changes the one axis' speed by twice the junction speed, so it gets half as much");
            Assert.That(Speed(moves[3].Header.StartSpeed), Is.EqualTo(Speed(moves[2].Header.EndSpeed)).Within(0.01f));
            Assert.That(Speed(moves[3].Header.EndSpeed), Is.Zero, "the last move ends at rest");
        });
    }

    [Test]
    public async Task EveryMoveIsScheduledUnderItsOwnAscendingId()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync();

        List<ScheduledMove> moves = [];
        foreach (string code in new[] { "G1 X10 F6000", "G1 Y10 F6000", "G1 X-10 F6000" })
        {
            moves.AddRange(await bench.RunMoveAsync(code));
        }

        Assert.Multiple(() =>
        {
            Assert.That(moves.Select(move => move.MoveId).Distinct().Count(), Is.EqualTo(moves.Count),
                        "no two moves share an id, which is how the controller tells them apart");
            Assert.That(moves.Select(move => move.MoveId), Is.Ordered.Ascending,
                        "and the ids follow the order the moves were commanded in");
        });
    }
}
