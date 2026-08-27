using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Motion;

/// <summary>
/// What a move that watches an input carries: the CheckEndstops flag, and per driver the input to
/// stop on and what a trigger on it stops.
/// </summary>
/// <remarks>
/// <para>
/// The controller does the stopping, because it is the only place close enough to the bus for the
/// latency to be acceptable, so everything it needs has to be in the move: the board and handle of
/// the input, the group the driver belongs to, and the action. These scenarios check that what
/// <c>EndstopArming</c> decided is what the link carried.
/// </para>
/// <para>
/// An endstop move is never segmented - RepRapFirmware runs a move of a type other than 0 in one
/// piece - so these run once rather than under both segmentation settings;
/// <see cref="ScheduleMoveSegmentationTests"/> is where that is asserted. Stall endstops (M574 S3
/// and S4) are out of scope here and belong with the stall detection plan
/// </para>
/// </remarks>
[TestFixture]
public class ScheduleMoveEndstopTests : BenchFixture
{
    private const byte XDriver = 0, YDriver = 1;

    /// <summary>The second and third motors of the gantry scenarios, beside X's own driver 1.0</summary>
    private const byte SecondXDriver = 4, ThirdXDriver = 5;

    /// <summary>X on three motors, which is a gantry each of whose sides can be squared on its own</summary>
    private const string GantryConfig = "M569 P1.4 S1\nM569 P1.5 S1\nM584 X1.0:1.4:1.5";

    /// <summary>Wait for the move that watches an input, which is the one an endstop or probe armed</summary>
    private static async Task<ScheduledMove> WaitForArmedMoveAsync(JobBench bench, string what)
    {
        await bench.CanMaster.WaitUntilAsync(
            () => bench.CanMaster.ScheduledMoves().Any(move => move.Has(ScheduleMoveFlags.CheckEndstops)), 20_000, what);
        return bench.CanMaster.ScheduledMoves().First(move => move.Has(ScheduleMoveFlags.CheckEndstops));
    }

    /// <summary>The driver record of a driver of the one board these scenarios configure</summary>
    private static ScheduleMoveDriver DriverOf(ScheduledMove move, byte driver)
    {
        ScheduleMoveDriver? record = move.DriverFor(ScheduleMoveBench.DriverBoard, driver);
        Assert.That(record, Is.Not.Null, $"driver {ScheduleMoveBench.DriverBoard}.{driver} is named in the move");
        return record!.Value;
    }

    [Test]
    public async Task HomingMoveIsFlaggedAndTellsEachDriveWhichSwitchStopsIt()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(
            configExtra: "M574 X1 S1 P\"1.io0.in\"\nM574 Y1 S1 P\"1.io1.in\"");
        Task<string> homing = bench.Host.ExecuteCodeAsync("G1 H1 X-250 Y-250 F3000");

        ScheduledMove move = await WaitForArmedMoveAsync(bench, "the homing move arriving armed");
        ScheduleMoveDriver x = DriverOf(move, XDriver), y = DriverOf(move, YDriver);

        Assert.Multiple(() =>
        {
            Assert.That((ScheduleMoveFlags)move.Header.Flags,
                        Is.EqualTo(ScheduleMoveFlags.CheckEndstops | ScheduleMoveFlags.LastPacket),
                        "the controller has to arm its stop list, and a homing move is not shaped");
            Assert.That(x.StopOnBoard, Is.EqualTo(ScheduleMoveBench.DriverBoard), "the board carrying X's switch");
            Assert.That(x.StopOnHandle, Is.EqualTo(RemoteEndstops.HandleFor(0).All), "X's endstop handle");
            Assert.That(y.StopOnHandle, Is.EqualTo(RemoteEndstops.HandleFor(1).All),
                        "and Y's own, so one move can home both axes on their own switches");
            Assert.That((StopAction)x.StopAction, Is.EqualTo(StopAction.Group),
                        "one switch for the whole axis stops the whole axis");
            Assert.That((StopAction)y.StopAction, Is.EqualTo(StopAction.Group));
            Assert.That(x.StopGroup, Is.Zero, "the group is the logical drive, which for X is 0");
            Assert.That(y.StopGroup, Is.EqualTo(1), "and for Y is 1, so X's switch does not stop Y");
            Assert.That(x.Steps, Is.EqualTo(-250 * ScheduleMoveBench.XyStepsPerMm),
                        "the move runs the full commanded distance; the switch is what ends it early");
        });

        bench.StopMove(move);
        await homing;
    }

    [Test]
    public async Task GantryWithASwitchPerMotorStopsEachMotorOnItsOwnSwitch()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(
            configExtra: GantryConfig + "\nM574 X1 S1 P\"1.io0.in+1.io1.in+1.io2.in\"");
        Task<string> homing = bench.Host.ExecuteCodeAsync("G1 H1 X-250 F3000");

        ScheduledMove move = await WaitForArmedMoveAsync(bench, "the homing move arriving armed");
        byte[] drivers = [XDriver, SecondXDriver, ThirdXDriver];

        Assert.Multiple(() =>
        {
            for (int i = 0; i < drivers.Length; i++)
            {
                ScheduleMoveDriver record = DriverOf(move, drivers[i]);
                Assert.That(record.StopOnHandle, Is.EqualTo(RemoteEndstops.HandleFor(0, i).All),
                            $"motor {i} of the axis watches switch {i} of its endstop");
                Assert.That((StopAction)record.StopAction, Is.EqualTo(StopAction.Driver),
                            "each motor runs on to its own switch, which is what squares the gantry");
                Assert.That(record.StopGroup, Is.Zero, "they all belong to the one drive, so the last one stops it");
                Assert.That(record.Steps, Is.EqualTo(-250 * ScheduleMoveBench.XyStepsPerMm));
            }
        });

        bench.StopMove(move);
        await homing;
    }

    [Test]
    public async Task MotorAlreadyOnItsSwitchIsCommandedNoStepsWhileTheRestMove()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(
            configExtra: GantryConfig + "\nM574 X1 S1 P\"1.io0.in+1.io1.in+1.io2.in\"");

        // The middle motor is already down when the homing move starts, which is a skewed gantry
        bench.CanMaster.InjectInputChange(ScheduleMoveBench.DriverBoard, RemoteEndstops.HandleFor(0, 1), active: true);
        await bench.Host.WaitForExpressionAsync("sensors.endstops[0].triggered", "true");
        Task<string> homing = bench.Host.ExecuteCodeAsync("G1 H1 X-250 F3000");

        ScheduledMove move = await WaitForArmedMoveAsync(bench, "the homing move arriving armed");

        Assert.Multiple(() =>
        {
            Assert.That(DriverOf(move, SecondXDriver).Steps, Is.Zero,
                        "the motor sitting on its switch has nowhere useful to go");
            Assert.That(DriverOf(move, SecondXDriver).StopOnHandle, Is.EqualTo(RemoteEndstops.HandleFor(0, 1).All),
                        "it is still named, so it is still enabled and the controller knows not to stop it twice");
            Assert.That(DriverOf(move, XDriver).Steps, Is.EqualTo(-250 * ScheduleMoveBench.XyStepsPerMm),
                        "the sides that are not down still run on, which is what squares the gantry");
            Assert.That(DriverOf(move, ThirdXDriver).Steps, Is.EqualTo(-250 * ScheduleMoveBench.XyStepsPerMm));
        });

        bench.StopMove(move);
        await homing;
    }

    [Test]
    public async Task AxisWithFewerSwitchesThanMotorsStopsAsAWhole()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(
            configExtra: GantryConfig + "\nM574 X1 S1 P\"1.io0.in+1.io1.in\"");
        Task<string> homing = bench.Host.ExecuteCodeAsync("G1 H1 X-250 F3000");

        ScheduledMove move = await WaitForArmedMoveAsync(bench, "the homing move arriving armed");

        Assert.Multiple(() =>
        {
            foreach (byte driver in new[] { XDriver, SecondXDriver, ThirdXDriver })
            {
                ScheduleMoveDriver record = DriverOf(move, driver);
                Assert.That(record.StopOnHandle, Is.EqualTo(RemoteEndstops.HandleFor(0).All),
                            "with no switch of its own to run on to, every motor watches the one endstop");
                Assert.That((StopAction)record.StopAction, Is.EqualTo(StopAction.Group),
                            "so a trigger stops the whole drive rather than one motor of it");
                Assert.That(record.StopGroup, Is.Zero);
            }
        });

        bench.StopMove(move);
        await homing;
    }

    [Test]
    public async Task CoupledGeometryStopsEveryDriveOfTheSet()
    {
        // On a CoreXY, holding X still needs both motors, so X's switch has to stop both
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(
            configExtra: "M669 K1\nM574 X1 S1 P\"1.io0.in\"");
        Task<string> homing = bench.Host.ExecuteCodeAsync("G1 H1 X-250 F3000");

        ScheduledMove move = await WaitForArmedMoveAsync(bench, "the homing move arriving armed");

        Assert.Multiple(() =>
        {
            foreach (byte driver in new[] { XDriver, YDriver })
            {
                ScheduleMoveDriver record = DriverOf(move, driver);
                Assert.That(record.StopOnHandle, Is.EqualTo(RemoteEndstops.HandleFor(0).All),
                            "both motors of the pair watch X's switch");
                Assert.That((StopAction)record.StopAction, Is.EqualTo(StopAction.Group),
                            "stopping one of them would drag the head diagonally into the switch");
                Assert.That(record.StopGroup, Is.Zero, "the group is X, whose endstop they are watching");
            }
        });

        bench.StopMove(move);
        await homing;
    }

    [Test]
    public async Task ProbingMoveWatchesTheProbeAndStopsEverything()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(
            configExtra: "M558 K0 P8 C\"1.io2.in\" F300 H5");
        Task<string> probing = bench.Host.ExecuteCodeAsync("G30");

        ScheduledMove dive = await WaitForArmedMoveAsync(bench, "the probing move arriving armed");
        ScheduleMoveDriver z = dive.Drivers.Single();

        Assert.Multiple(() =>
        {
            Assert.That((ScheduleMoveFlags)dive.Header.Flags,
                        Is.EqualTo(ScheduleMoveFlags.CheckEndstops | ScheduleMoveFlags.LastPacket));
            Assert.That(z.StopOnBoard, Is.EqualTo(ScheduleMoveBench.DriverBoard), "the board carrying the probe");
            Assert.That(z.StopOnHandle, Is.EqualTo(RemoteProbes.HandleFor(0).All),
                        "a probe is watched under its own handle rather than an endstop's");
            Assert.That((StopAction)z.StopAction, Is.EqualTo(StopAction.All),
                        "there is one probe for the machine, so a touch stops the whole move");
            Assert.That(z.StopGroup, Is.EqualTo(ScheduleMovePacket.NoStopGroup),
                        "stopping everything needs no group to stop");
            Assert.That(z.Steps, Is.Negative, "the dive goes down towards the bed");
        });

        bench.CanMaster.InjectInputChange(ScheduleMoveBench.DriverBoard, RemoteProbes.HandleFor(0), active: true);
        bench.StopMove(dive);
        await probing;
    }

    [Test]
    public async Task IndividualMotorMoveIsNotFlaggedAndWatchesNothing()
    {
        await using JobBench bench = await ScheduleMoveBench.StartCartesianAsync(
            configExtra: "M574 X1 S1 P\"1.io0.in\"");

        // H2 moves the motors directly and deliberately ignores the endstops
        IReadOnlyList<ScheduledMove> moves = await bench.RunMoveAsync("G1 H2 X5 F600");

        ScheduledMove move = moves.Single();
        Assert.Multiple(() =>
        {
            Assert.That((ScheduleMoveFlags)move.Header.Flags, Is.EqualTo(ScheduleMoveFlags.LastPacket),
                        "nothing is watched, and an isolated move is not shaped either");
            Assert.That(move.Drivers.Single().Steps, Is.EqualTo(5 * ScheduleMoveBench.XyStepsPerMm));
        });
        ScheduleMoveBench.AssertWatchesNothing(move.Drivers.Single(), "an H2 move stops on nothing");
    }
}
