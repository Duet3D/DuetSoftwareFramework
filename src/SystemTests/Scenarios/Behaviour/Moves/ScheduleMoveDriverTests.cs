using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace SystemTests.Scenarios.Behaviour.Moves;

/// <summary>
/// Which drivers a commanded move reaches, and how much each of them is told to turn: the
/// <c>ScheduleMoveDriver</c> records of the packets one G-code produces.
/// </summary>
/// <remarks>
/// <para>
/// Every scenario runs twice, once with segmentation off and once with M669 turning it on, because
/// segmentation must not change what the machine is told to do: a move cut into ten pieces has to
/// command the same drivers the same number of steps in total as the same move in one piece. The
/// assertions are therefore made on the totals over every move one code produced, and the count of
/// the pieces themselves is what <see cref="ScheduleMoveSegmentationTests"/> covers.
/// </para>
/// <para>
/// The expected steps are the commanded distance times the M92 steps per mm, which is what
/// RepRapFirmware's <c>DDA::Prepare</c> sends per driver
/// </para>
/// </remarks>
[TestFixture]
public class ScheduleMoveDriverTests : BenchFixture
{
    private const byte XDriver = Machines.XDriver, YDriver = Machines.YDriver, ZDriver = Machines.ZDriver, EDriver = Machines.EDriver;

    /// <summary>A driver of <see cref="Machines.DriverBoard"/> no machine maps, for Z's second motor</summary>
    private const byte SecondZDriver = 4;

    /// <summary>A board other than <see cref="Machines.DriverBoard"/>, for a driver away from the rest</summary>
    private const byte OtherBoard = 2;

    private static Task<Bench> StartAsync(bool segmented, string configExtra = "")
        => Bench.With(Machines.Xyze, Machines.OneTool, configExtra, segmented ? Machines.Segmented : "")
                .StartRelativeAsync();

    [TestCase(false)]
    [TestCase(true)]
    public async Task AxisMoveCarriesTheOneDriverThatTurns(bool segmented)
    {
        await using Bench bench = await StartAsync(segmented);

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X10 F6000");

        Assert.That(moves, Is.Not.Empty, "the move reached the link");
        Assert.Multiple(() =>
        {
            Assert.That(moves.DriversMoved(), Is.EqualTo(new[] { (Machines.DriverBoard, XDriver) }),
                        "only the driver X is mapped to moves");
            Assert.That(moves.Steps(XDriver), Is.EqualTo(10 * Machines.XyStepsPerMm),
                        "10 mm at 80 steps/mm");
            Assert.That(moves.Distance(), Is.EqualTo(10.0f).Within(1e-3f), "10 mm commanded");
        });
        foreach (ScheduledMove move in moves)
        {
            ScheduleMoveDriver driver = move.DriverFor(Machines.DriverBoard, XDriver)!.Value;
            Assert.Multiple(() =>
            {
                Assert.That(driver.IsExtruder, Is.Zero, "an axis driver is not an extruder");
                Assert.That(driver.Extrusion, Is.Zero, "an axis driver carries its movement as steps");
            });
            MoveCapture.AssertWatchesNothing(driver, "a travel move stops on nothing");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MoveCarriesEveryAxisItTouchesAndNoOthers(bool segmented)
    {
        await using Bench bench = await StartAsync(segmented);

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X10 Y10 F6000");

        Assert.Multiple(() =>
        {
            Assert.That(moves.DriversMoved(), Is.EquivalentTo(new[]
            {
                (Machines.DriverBoard, XDriver), (Machines.DriverBoard, YDriver)
            }), "X and Y turn, Z and the extruder do not");
            Assert.That(moves.Steps(XDriver), Is.EqualTo(10 * Machines.XyStepsPerMm));
            Assert.That(moves.Steps(YDriver), Is.EqualTo(10 * Machines.XyStepsPerMm));
            Assert.That(moves.Distance(), Is.EqualTo(MathF.Sqrt(200.0f)).Within(1e-3f),
                        "the distance is the length of the diagonal, not of either side");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MoveInTheNegativeDirectionCarriesNegativeSteps(bool segmented)
    {
        await using Bench bench = await StartAsync(segmented);

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X-3 F6000");

        Assert.Multiple(() =>
        {
            Assert.That(moves.Steps(XDriver), Is.EqualTo(-3 * Machines.XyStepsPerMm),
                        "the direction is the sign of the steps");
            Assert.That(moves.Distance(), Is.EqualTo(3.0f).Within(1e-3f), "the distance itself is unsigned");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MoveTooShortToStepIsNotScheduledAtAll(bool segmented)
    {
        await using Bench bench = await StartAsync(segmented);

        // 0.001 mm at 80 steps/mm is 0.08 of a step, so there is nothing for any board to do
        IReadOnlyList<ScheduledMove> rounded = await bench.RunMoveAsync("G1 X0.001 F600");
        IReadOnlyList<ScheduledMove> nowhere = await bench.RunMoveAsync("G1 X0 F600");

        Assert.Multiple(() =>
        {
            Assert.That(rounded, Is.Empty, "a move that rounds to no steps commands nothing");
            Assert.That(nowhere, Is.Empty, "a move to where the machine already is commands nothing");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EveryMotorOfAnAxisIsToldToMakeTheWholeMove(bool segmented)
    {
        // Z on two motors, which is how a bed or a gantry with two leadscrews is configured
        await using Bench bench = await StartAsync(segmented, $"M569 P{Expansion.Driver(Machines.DriverBoard, SecondZDriver)} S1\nM584 Z{Expansion.Driver(Machines.DriverBoard, ZDriver)}:{Expansion.Driver(Machines.DriverBoard, SecondZDriver)}");

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 Z1 F600");

        Assert.Multiple(() =>
        {
            Assert.That(moves.DriversMoved(), Is.EquivalentTo(new[]
            {
                (Machines.DriverBoard, ZDriver), (Machines.DriverBoard, SecondZDriver)
            }), "both motors of the axis are named");
            Assert.That(moves.Steps(ZDriver), Is.EqualTo(1 * Machines.ZStepsPerMm));
            Assert.That(moves.Steps(SecondZDriver), Is.EqualTo(1 * Machines.ZStepsPerMm),
                        "the second motor makes the same move as the first");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExtruderCarriesItsMovementAsExtrusionRatherThanSteps(bool segmented)
    {
        await using Bench bench = await StartAsync(segmented);

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 E5 F1200");

        Assert.That(moves, Is.Not.Empty, "the extrusion reached the link");
        Assert.Multiple(() =>
        {
            Assert.That(moves.DriversMoved(), Is.EqualTo(new[] { (Machines.DriverBoard, EDriver) }),
                        "an extruder-only move turns nothing else");
            Assert.That(moves.Extrusion(EDriver), Is.EqualTo(5 * Machines.EStepsPerMm).Within(1e-2f),
                        "5 mm at 420 steps/mm, as a float so the board keeps the fraction");
            Assert.That(moves.Steps(EDriver), Is.Zero, "the steps field is for axis drivers");
        });
        foreach (ScheduledMove move in moves)
        {
            Assert.That(move.DriverFor(Machines.DriverBoard, EDriver)!.Value.IsExtruder, Is.Not.Zero,
                        "the record says it is an extruder");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PrintingMoveCarriesTheAxisAndTheExtruderTogether(bool segmented)
    {
        await using Bench bench = await StartAsync(segmented);

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X4 E0.4 F3000");

        Assert.Multiple(() =>
        {
            Assert.That(moves.Steps(XDriver), Is.EqualTo(4 * Machines.XyStepsPerMm));
            Assert.That(moves.Extrusion(EDriver), Is.EqualTo(0.4f * Machines.EStepsPerMm).Within(1e-2f));
            Assert.That(moves.Distance(), Is.EqualTo(4.0f).Within(1e-3f),
                        "the distance is the axis movement; the extrusion rides along with it");
        });
        foreach (ScheduledMove move in moves)
        {
            Assert.That(move.Drivers, Has.Length.EqualTo(2),
                        "both drives share one move, so they share its velocity profile");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExtruderRetractionIsCarriedAsNegativeExtrusion(bool segmented)
    {
        await using Bench bench = await StartAsync(segmented);

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 E-2 F1200");

        Assert.That(moves.Extrusion(EDriver), Is.EqualTo(-2 * Machines.EStepsPerMm).Within(1e-2f),
                    "a retraction is the same move with the other sign");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CoreXyMoveTurnsBothMotorsAsTheGeometryRequires(bool segmented)
    {
        await using Bench bench = await StartAsync(segmented, "M669 K1");

        IReadOnlyList<ScheduledMove> alongX = await bench.RunMoveAsync("G1 X10 F6000");
        IReadOnlyList<ScheduledMove> alongY = await bench.RunMoveAsync("G1 Y10 F6000");

        Assert.Multiple(() =>
        {
            Assert.That(alongX.Steps(XDriver), Is.EqualTo(10 * Machines.XyStepsPerMm),
                        "X is the sum of the two motors, so both turn the same way");
            Assert.That(alongX.Steps(YDriver), Is.EqualTo(10 * Machines.XyStepsPerMm));
            Assert.That(alongY.Steps(XDriver), Is.EqualTo(10 * Machines.XyStepsPerMm),
                        "Y is their difference, so they turn opposite ways");
            Assert.That(alongY.Steps(YDriver), Is.EqualTo(-10 * Machines.XyStepsPerMm));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DriversOnDifferentBoardsKeepTheirOwnAddress(bool segmented)
    {
        await using Bench bench = await StartAsync(segmented, $"M584 Y{Expansion.Driver(OtherBoard, 0)}");

        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 X10 Y10 F6000");

        Assert.That(moves.DriversMoved(), Is.EquivalentTo(new[] { (Machines.DriverBoard, XDriver), (OtherBoard, (byte)0) }),
                    "each record carries the CAN address of the board that has to turn it");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BacklashCompensationAddsItsStepsWhenTheDirectionReverses(bool segmented)
    {
        // 0.1 mm of backlash, taken up in a single move (S1)
        await using Bench bench = await StartAsync(segmented, "M425 X0.1 S1");

        IReadOnlyList<ScheduledMove> forward = await bench.RunMoveAsync("G1 X10 F6000");
        IReadOnlyList<ScheduledMove> back = await bench.RunMoveAsync("G1 X-10 F6000");

        Assert.Multiple(() =>
        {
            Assert.That(forward.Steps(XDriver), Is.EqualTo(10 * Machines.XyStepsPerMm),
                        "the first move in a direction takes up no backlash");
            Assert.That(back.Steps(XDriver),
                        Is.EqualTo(-((10 * Machines.XyStepsPerMm) + (0.1f * Machines.XyStepsPerMm))),
                        "the reversal adds the 8 steps of backlash to the commanded 800");
            Assert.That(back.Distance(), Is.EqualTo(10.0f).Within(1e-3f),
                        "the compensation is in the steps only; the move still travels what it was asked to");
        });
    }
}
