using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Motion;

/// <summary>
/// The flags byte of a scheduled move: what the controller and the boards are told about the move
/// beyond its numbers, and how a move too wide for one packet is carried.
/// </summary>
/// <remarks>
/// Every scenario runs with segmentation off and on, because a flag describes the move and a
/// segment is the same move: cutting one up must set the same flags on every piece
/// </remarks>
[TestFixture]
public class ScheduleMoveFlagTests : BenchFixture
{
    /// <summary>Assert that every move a code produced carries exactly the given flags</summary>
    /// <param name="moves">The moves the code produced</param>
    /// <param name="expected">The flags every one of them should carry</param>
    /// <param name="because">What the assertion is showing</param>
    private static void AssertFlags(IReadOnlyList<ScheduledMove> moves, ScheduleMoveFlags expected, string because)
    {
        Assert.That(moves, Is.Not.Empty, "the move reached the link");
        foreach (ScheduledMove move in moves)
        {
            Assert.That((ScheduleMoveFlags)move.Header.Flags, Is.EqualTo(expected), because);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MoveInTheXyPlaneIsPlannedForInputShaping(bool segmented)
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(segmented);

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X10 F6000");

        AssertFlags(moves, ScheduleMoveFlags.UseInputShaping | ScheduleMoveFlags.LastPacket,
                    "an XY move is planned expecting the boards to shape it, and fits in one packet");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MoveWithNothingInTheXyPlaneIsNotShaped(bool segmented)
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(segmented);

        IReadOnlyList<ScheduledMove> upwards = await bench.RunMoveAsync("G1 Z1 F600");
        IReadOnlyList<ScheduledMove> extruding = await bench.RunMoveAsync("G1 E5 F1200");

        Assert.Multiple(() =>
        {
            AssertFlags(upwards, ScheduleMoveFlags.LastPacket, "shaping is for the XY plane, and Z is not in it");
            AssertFlags(extruding, ScheduleMoveFlags.LastPacket, "nor is an extruder");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PressureAdvanceIsFlaggedOnlyWhereItWouldBeApplied(bool segmented)
    {
        // 50 ms of pressure advance on the one extruder
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(segmented, "M572 D0 S0.05");

        IReadOnlyList<ScheduledMove> printing = await bench.RunMoveAsync("G1 X4 E0.4 F3000");
        IReadOnlyList<ScheduledMove> wiping = await bench.RunMoveAsync("G1 X4 E-0.4 F3000");
        IReadOnlyList<ScheduledMove> retracting = await bench.RunMoveAsync("G1 E-2 F1200");

        Assert.Multiple(() =>
        {
            AssertFlags(printing,
                        ScheduleMoveFlags.UseInputShaping | ScheduleMoveFlags.UsePressureAdvance | ScheduleMoveFlags.LastPacket,
                        "extruding forwards along an XY move is what pressure advance is for");
            AssertFlags(wiping, ScheduleMoveFlags.UseInputShaping | ScheduleMoveFlags.LastPacket,
                        "retracting while moving is not, so the boards are told not to apply it");
            AssertFlags(retracting, ScheduleMoveFlags.LastPacket,
                        "and neither is a retraction with no axis movement to advance against");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PressureAdvanceIsFlaggedFromTheMoveRatherThanTheCoefficient(bool segmented)
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(segmented);

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X4 E0.4 F3000");

        AssertFlags(moves,
                    ScheduleMoveFlags.UseInputShaping | ScheduleMoveFlags.UsePressureAdvance | ScheduleMoveFlags.LastPacket,
                    "the flag says the move is one to advance on; how much to advance by is the board's own "
                    + "M572 coefficient, so a machine with none set still flags it (RRF GCodes.cpp, "
                    + "usePressureAdvance = axesMentionedExceptZ.IsNonEmpty())");
    }

    /// <summary>
    /// Five axes of seven motors each, spread over three boards: 35 drivers, which is more than the
    /// 32 one packet holds. The drivers are what matters, so the machine is otherwise plain
    /// </summary>
    private const string ThirtyFiveDriverConfig = """
        M953
        M584 X1.0:1.1:1.2:1.3:1.4:1.5:1.6 Y1.7:1.8:1.9:1.10:1.11:1.12:1.13 Z2.0:2.1:2.2:2.3:2.4:2.5:2.6 U2.7:2.8:2.9:2.10:2.11:2.12:2.13 V3.0:3.1:3.2:3.3:3.4:3.5:3.6
        M92 X80 Y80 Z80 U80 V80
        M201 X500 Y500 Z500 U500 V500
        M203 X6000 Y6000 Z6000 U6000 V6000
        M566 X900 Y900 Z900 U900 V900
        M208 X0:200 Y0:200 Z0:200 U0:200 V0:200
        M564 H0 S0
        """;

    [TestCase(false)]
    [TestCase(true)]
    public async Task MoveWithMoreDriversThanOnePacketHoldsIsSplitUnderOneMoveId(bool segmented)
    {
        await using JobBench bench = await ScheduleMoveBench.StartRelativeAsync(ThirtyFiveDriverConfig, segmented);

        IReadOnlyList<ScheduledMove> packets = await bench.RunMoveAsync("G1 X10 Y10 Z10 U10 V10 F6000");

        Assert.That(packets, Is.Not.Empty, "the move reached the link");
        foreach (IGrouping<uint, ScheduledMove> move in packets.GroupBy(packet => packet.MoveId))
        {
            ScheduledMove[] parts = move.ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(parts, Has.Length.EqualTo(2), "35 drivers need a second packet");
                Assert.That(parts[0].Drivers, Has.Length.EqualTo(ScheduleMovePacket.MaxDrivers),
                            "the first packet is filled before another is started");
                Assert.That(parts[1].Drivers, Has.Length.EqualTo(35 - ScheduleMovePacket.MaxDrivers),
                            "and the rest follow in the second");
                Assert.That(parts[0].Has(ScheduleMoveFlags.LastPacket), Is.False,
                            "the controller must hold the first packet rather than send it on its own");
                Assert.That(parts[1].Has(ScheduleMoveFlags.LastPacket), Is.True,
                            "the last packet is what tells the controller the move is complete");
                Assert.That(parts[1].StartTime, Is.EqualTo(parts[0].StartTime),
                            "both packets describe the one move, so they carry the one profile");
                Assert.That(parts[1].Header.TotalDistance, Is.EqualTo(parts[0].Header.TotalDistance));
                Assert.That(parts.SelectMany(part => part.Drivers)
                                 .Select(driver => (driver.BoardAddress, driver.DriverNumber))
                                 .Distinct()
                                 .Count(),
                            Is.EqualTo(35), "every driver of the machine is named exactly once");
            });
        }
    }
}
