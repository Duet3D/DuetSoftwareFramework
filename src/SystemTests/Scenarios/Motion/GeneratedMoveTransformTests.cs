using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using SystemTests.Host;

namespace SystemTests.Scenarios.Motion;

/// <summary>
/// The moves DSF generates itself rather than reading from a G0 or G1 - the moves of a probing code,
/// and the move back to where a paused job stopped - are transformed the way RepRapFirmware transforms
/// them
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware builds these as special moves: <c>SetMoveBufferDefaults</c> clears the movement
/// tool, and <c>Move::MoveLoop</c> runs <c>AxisAndBedTransform</c> on every move of type 0 with that
/// tool. So skew and mesh compensation apply as they do to a G1, and the axes that count as X and Y
/// are the ones lettered X and Y whatever the selected tool maps them to. Probe coordinates go into the
/// move as they are, with no tool offset or axis map; the move back on resume goes through the tool
/// transform, because it returns the tool to where the job left it.
/// </para>
/// <para>
/// The machines are the regression suite's, so the step counts are the ones its references recorded
/// on RepRapFirmware: <c>g30-dive-with-mesh</c>, <c>g30-dive-with-axis-skew</c>,
/// <c>g30-probe-point-with-axis-skew</c>, <c>g30-probe-point-with-tool-axis-mapping</c>,
/// <c>m24-resume-with-axis-skew</c>, <c>m24-resume-with-tool-axis-mapping</c> and
/// <c>g29-probe-on-mapped-tool-carriage</c>. Speeds are raised where they only decide how long a
/// scenario takes. Nothing triggers the probe, so every probing code ends with its dive run to the
/// end and the not-triggered error, as on the bench
/// </para>
/// </remarks>
[TestFixture]
public class GeneratedMoveTransformTests : BenchFixture
{
    private const byte XDriver = 0, YDriver = 1, ZDriver = 2;

    /// <summary>The board carrying U, apart from X, Y and Z, as the bench has it</summary>
    private const byte UBoard = 2, UDriver = 0;

    private const float XyuStepsPerMm = 80.0f, ZStepsPerMm = 400.0f;

    /// <summary>Z's acceleration, and the reduced one a probing move is held to (M201, M201.1), mm/s²</summary>
    private const float ZAcceleration = 1000.0f, ZReducedAcceleration = 50.0f;

    /// <summary>
    /// X, Y and Z on board 1 with the regression bench's travel, which puts Z's minimum 100 mm below
    /// the origin, and a probe on board 1 that nothing triggers: dive height 1 mm, trigger height 0.
    /// Z's reduced acceleration is set apart from its own, so a probing move shows which it was given
    /// </summary>
    private const string XyzMachine = """
        M953
        M569 P1.0 S1
        M569 P1.1 S1
        M569 P1.2 S1
        M584 X1.0 Y1.1 Z1.2
        M92 X80 Y80 Z400
        M906 X800 Y800 Z800
        M203 X6000 Y6000 Z6000
        M201 X500 Y500 Z1000
        M201.1 Z50
        M566 X600 Y600 Z60
        M208 X-100:100 Y-100:100 Z-100:100
        M564 H0 S0
        M558 K0 P8 C"1.io1.in" H1 F3000 T6000 A1
        G31 K0 P500 X0 Y0 Z0
        """;

    /// <summary>
    /// <see cref="XyzMachine"/> with Z's minimum at the origin, for G29. RepRapFirmware's grid
    /// probing travels to the dive height and dives below it measured from Z0, where DSF measures both
    /// from the axis minimum; with the minimum at zero the two are the same moves
    /// </summary>
    private const string XyzGridMachine = XyzMachine + "\nM208 Z0:100";

    /// <summary>The bench's height map, read at (25, 25) as 0.07 mm</summary>
    private const string MeshFile = "drt-g30-mesh.csv";

    private const string Mesh = """
        RepRapFirmware height map file v2 generated at 2025-01-01 00:00, min error 0.000, max error 0.000, mean 0.000, deviation 0.000
        axis0,axis1,min0,max0,min1,max1,radius,spacing0,spacing1,num0,num1
        X,Y,-50.00,50.00,-50.00,50.00,-1.00,50.00,50.00,3,3
          0.010,  0.020,  0.030
          0.040,  0.050,  0.060
          0.070,  0.080,  0.090

        """;

    /// <summary>What every probing code here replies, since nothing triggers the probe</summary>
    private static string NotTriggered(string code) => $"Error: {code}: Probe was not triggered during probing move";

    /// <summary>Run each line of a block of codes in turn</summary>
    private static async Task RunAsync(JobBench bench, string lines)
    {
        foreach (string line in lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await bench.Host.ExecuteCodeAsync(line);
        }
    }

    /// <summary>Run a code that may fail, and return its reply and every move it scheduled</summary>
    private static async Task<(string Reply, IReadOnlyList<ScheduledMove> Moves)> RunCodeAsync(JobBench bench, string code)
    {
        int before = bench.CanMaster.ScheduledMoves().Count;
        string reply = await bench.Host.ExecuteCodeAsync(code);
        await bench.Host.ExecuteCodeAsync("M400");
        return (reply.Trim(), bench.CanMaster.ScheduledMoves().Skip(before).ToArray());
    }

    /// <summary>Steps one move gives a driver, zero for a driver it does not name</summary>
    private static int StepsOf(ScheduledMove move, byte board, byte driver) => move.DriverFor(board, driver)?.Steps ?? 0;

    /// <summary>Steps a move gives one driver of board 1</summary>
    private static int StepsOf(ScheduledMove move, byte driver) => StepsOf(move, ScheduleMoveBench.DriverBoard, driver);

    /// <summary>Read one axis' value by its letter</summary>
    private static Task<T> ReadAxisAsync<T>(JobBench bench, char letter, Func<Axis, T> read)
        => bench.Host.ReadModelAsync(model => read(model.Move.Axes.First(axis => axis.Letter == letter)));

    /// <summary>An acceleration as the packet carries it, in mm/s²</summary>
    private static float Acceleration(ScheduledMove move) => ScheduleMoveBench.MmPerSecondSquared(move.Header.Acceleration);

    /// <summary>The flags a move carries, less the one that only marks its last packet</summary>
    private static ScheduleMoveFlags FlagsOf(ScheduledMove move) => (ScheduleMoveFlags)move.Header.Flags & ~ScheduleMoveFlags.LastPacket;

    /// <summary>Whether every driver of a move stops when probe 0 on board 1 triggers</summary>
    private static bool ArmedOnTheProbe(ScheduledMove move)
        => move.Drivers.All(driver => driver.StopOnBoard == ScheduleMoveBench.DriverBoard &&
                                      driver.StopOnHandle == RemoteProbes.HandleFor(0).All);

    /// <summary>Whether no driver of a move watches an endstop or a probe</summary>
    private static bool WatchesNothing(ScheduledMove move)
        => move.Drivers.All(driver => driver.StopOnBoard == ScheduleMovePacket.NoEndstopBoard);

    /// <summary>The probing moves scheduled since a count of moves, in order</summary>
    private static ScheduledMove[] Dives(JobBench bench, int before)
        => bench.CanMaster.ScheduledMoves().Skip(before).Where(move => move.Has(ScheduleMoveFlags.CheckEndstops)).ToArray();

    // ---- Probing ---------------------------------------------------------------------------------

    /// <summary>
    /// A G30 dive gets the height map's correction, as a G1 to the same Z would
    /// </summary>
    /// <remarks>
    /// <c>g30-dive-with-mesh</c>: G92 runs before the map is loaded, so the machine Z it sets carries
    /// no correction, and the dive to Z −101 ends 0.07 mm higher, at machine Z −100.93: −40372 steps
    /// where an uncorrected dive is −40400. The position reported afterwards has the correction taken
    /// back off
    /// </remarks>
    [Test]
    public async Task AProbeDiveCarriesTheHeightMapCorrection()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync(XyzMachine, sd => sd.WriteSys(MeshFile, Mesh));
        await RunAsync(bench, $"G90\nG92 X25 Y25 Z0\nM375 P\"{MeshFile}\"");

        (string reply, IReadOnlyList<ScheduledMove> moves) = await RunCodeAsync(bench, "G30 S-1");

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.EqualTo(NotTriggered("G30")));
            Assert.That(moves.Select(FlagsOf), Is.EqualTo(new[] { (ScheduleMoveFlags)0, ScheduleMoveFlags.CheckEndstops }),
                        "the move to the dive height, which moves Z alone and watches nothing, and one dive");
            Assert.That(moves.Select(ArmedOnTheProbe), Is.EqualTo(new[] { false, true }), "the dive watches the probe on every driver");
            Assert.That(WatchesNothing(moves[0]), Is.True);
            Assert.That(moves.Select(Acceleration), Is.EqualTo(new[] { ZAcceleration, ZReducedAcceleration }).Within(0.1f),
                        "the dive at Z's reduced acceleration");
            Assert.That(moves.Sum(move => StepsOf(move, ZDriver)), Is.EqualTo(-40372),
                        "Z goes to −101 plus the map's 0.07 mm at (25, 25)");
            Assert.That(moves.Sum(move => StepsOf(move, XDriver)), Is.Zero, "X does not move");
            Assert.That(moves.Sum(move => StepsOf(move, YDriver)), Is.Zero, "nor Y");
            Assert.That(await ReadAxisAsync(bench, 'Z', axis => axis.StepPos), Is.EqualTo(-40372));
            Assert.That(await ReadAxisAsync(bench, 'Z', axis => axis.UserPosition), Is.EqualTo(-101.0f).Within(1e-3f),
                        "the reported Z has the correction taken back off");
            Assert.That(await ReadAxisAsync(bench, 'X', axis => axis.UserPosition), Is.EqualTo(25.0f).Within(1e-3f));
            Assert.That(await ReadAxisAsync(bench, 'Y', axis => axis.UserPosition), Is.EqualTo(25.0f).Within(1e-3f));
        });
    }

    /// <summary>
    /// A G30 dive gets the skew correction, X and Y following Z, and the reported X and Y stay where
    /// they were
    /// </summary>
    /// <remarks>
    /// <c>g30-dive-with-axis-skew</c> is where DSF deliberately differs. RepRapFirmware adds the
    /// correction to the dive's X and Y and then drops it, because its probing move owns only Z, yet
    /// takes it back off the position it reports, so the head reads X 10.1, Y 5.05 at motors that did
    /// not move. Here the dive moves X by tanXZ = 0.1 and Y by tanYZ = 0.05 per mm of Z, −808 and −404
    /// steps over Z's −101 mm, and X and Y read 0 afterwards. Each move of the code carries its own
    /// share, so the head stays on the line skew compensation says is vertical
    /// </remarks>
    [Test]
    public async Task AProbeDiveCarriesTheSkewCorrectionAndTheHeadStaysWhereItReads()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync(XyzMachine + "\nM556 S100 X0 Y5 Z10");
        await RunAsync(bench, "G90\nG92 X0 Y0 Z0");

        (string reply, IReadOnlyList<ScheduledMove> moves) = await RunCodeAsync(bench, "G30 S-1");

        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.EqualTo(NotTriggered("G30")));
            Assert.That(moves.Select(FlagsOf), Is.EqualTo(new[] { ScheduleMoveFlags.UseInputShaping, ScheduleMoveFlags.CheckEndstops }),
                        "the move to the dive height moves X and Y with Z, so it is XY movement and shaped; the dive is isolated");
            Assert.That(moves.Select(ArmedOnTheProbe), Is.EqualTo(new[] { false, true }), "the dive watches the probe on every driver");
            Assert.That(WatchesNothing(moves[0]), Is.True);
            float alongZ = 1.0f / MathF.Sqrt(1.0f + (0.1f * 0.1f) + (0.05f * 0.05f));
            Assert.That(moves.Select(Acceleration), Is.EqualTo(new[] { ZAcceleration / alongZ, ZReducedAcceleration / alongZ }).Within(0.5f),
                        "each limited by Z, the dive at Z's reduced acceleration");
            Assert.That(moves.Sum(move => StepsOf(move, ZDriver)), Is.EqualTo(-40400), "Z goes to −101");
            Assert.That(moves.Sum(move => StepsOf(move, XDriver)), Is.EqualTo(-808), "X by tanXZ × −101 mm");
            Assert.That(moves.Sum(move => StepsOf(move, YDriver)), Is.EqualTo(-404), "Y by tanYZ × −101 mm");
            foreach (ScheduledMove move in moves)
            {
                int z = StepsOf(move, ZDriver);
                Assert.That(StepsOf(move, XDriver), Is.EqualTo(z * 0.1f * XyuStepsPerMm / ZStepsPerMm).Within(1),
                            $"move {move.MoveId} moves X with its own Z");
                Assert.That(StepsOf(move, YDriver), Is.EqualTo(z * 0.05f * XyuStepsPerMm / ZStepsPerMm).Within(1),
                            $"move {move.MoveId} moves Y with its own Z");
            }
            Assert.That(await ReadAxisAsync(bench, 'X', axis => axis.StepPos), Is.EqualTo(-808));
            Assert.That(await ReadAxisAsync(bench, 'Y', axis => axis.StepPos), Is.EqualTo(-404));
            Assert.That(await ReadAxisAsync(bench, 'X', axis => axis.UserPosition), Is.EqualTo(0.0f).Within(1e-3f),
                        "the head is reported where it is");
            Assert.That(await ReadAxisAsync(bench, 'Y', axis => axis.UserPosition), Is.EqualTo(0.0f).Within(1e-3f));
            Assert.That(await ReadAxisAsync(bench, 'Z', axis => axis.UserPosition), Is.EqualTo(-101.0f).Within(1e-3f));
        });
    }

    /// <summary>
    /// The travel to a grid point gets the skew correction, and the reported X does not
    /// </summary>
    /// <remarks>
    /// <c>g30-probe-point-with-axis-skew</c> on the travel DSF can make, which is G29's: tanXY 0.01 and a
    /// first grid point at X0 Y50, so the commanded X never changes and the X steps of the travel are
    /// the correction, 0.5 mm or 40. The dive moves Z alone, since only Y carries a term into X
    /// </remarks>
    [Test]
    public async Task TheTravelToAProbePointCarriesTheSkewCorrection()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync(
            XyzGridMachine + "\nM556 S100 X1 Y0 Z0 P0\nM557 X0:10 Y50:60 P2");
        await RunAsync(bench, "G90\nG92 X0 Y0 Z0");

        (string reply, IReadOnlyList<ScheduledMove> moves) = await RunCodeAsync(bench, "G29 S0");

        ScheduledMove[] travel = moves.Where(move => !move.Has(ScheduleMoveFlags.CheckEndstops)).ToArray();
        ScheduledMove[] dive = moves.Where(move => move.Has(ScheduleMoveFlags.CheckEndstops)).ToArray();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.EqualTo(NotTriggered("G29")));
            Assert.That(travel, Has.Length.EqualTo(1), "one move to the point");
            Assert.That(dive, Has.Length.EqualTo(1), "and one dive");
            Assert.That(StepsOf(travel[0], XDriver), Is.EqualTo(40), "X by tanXY × 50 mm of Y");
            Assert.That(StepsOf(travel[0], YDriver), Is.EqualTo(50 * XyuStepsPerMm));
            Assert.That(StepsOf(travel[0], ZDriver), Is.EqualTo(1 * ZStepsPerMm), "up to the dive height");
            Assert.That(StepsOf(dive[0], ZDriver), Is.EqualTo(-2 * ZStepsPerMm), "down to the dive height below Z0");
            Assert.That(StepsOf(dive[0], XDriver), Is.Zero);
            Assert.That(StepsOf(dive[0], YDriver), Is.Zero);
            Assert.That(FlagsOf(travel[0]), Is.EqualTo(ScheduleMoveFlags.UseInputShaping), "the travel is XY movement, shaped");
            Assert.That(WatchesNothing(travel[0]), Is.True, "and watches nothing");
            Assert.That(FlagsOf(dive[0]), Is.EqualTo(ScheduleMoveFlags.CheckEndstops), "the dive is isolated");
            Assert.That(ArmedOnTheProbe(dive[0]), Is.True, "and watches the probe");
            Assert.That(Acceleration(dive[0]), Is.EqualTo(ZReducedAcceleration).Within(0.1f), "at Z's reduced acceleration");
            Assert.That(await ReadAxisAsync(bench, 'X', axis => axis.UserPosition), Is.EqualTo(0.0f).Within(1e-3f),
                        "the correction is not reported");
            Assert.That(await ReadAxisAsync(bench, 'Y', axis => axis.UserPosition), Is.EqualTo(50.0f).Within(1e-3f));
            Assert.That(await ReadAxisAsync(bench, 'Z', axis => axis.UserPosition), Is.EqualTo(-1.0f).Within(1e-3f));
        });
    }

    /// <summary>
    /// G29 with the probe on a tool's carriage moves the grid's own axis, with the probe's offset and
    /// without the tool's
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>g29-probe-on-mapped-tool-carriage</c>: the probe is on U with a U offset of −5, the grid is on
    /// U and Y, and the selected tool maps X onto U with a U offset of 3. The first point is U10 Y0, so
    /// the travel takes U to 15, 1200 steps on U's board, and up to the dive height; the tool's offset
    /// and map play no part. Afterwards the reported U and X are the selected tool's coordinates, the
    /// machine position plus its offset: 18.
    /// </para>
    /// <para>
    /// M204 is added to the bench's machine to show which axes count as X and Y: the travel moves U and
    /// not the axis lettered X, so it is not XY movement and accelerates at U's own 500 mm/s² rather than
    /// M204's 100. RepRapFirmware's <c>move.motionSystems[].userPosition</c> is not checked, because DSF
    /// does not publish it
    /// </para>
    /// </remarks>
    [Test]
    public async Task GridProbingMovesTheGridAxisWithTheProbeOffsetAndNotTheTools()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync("""
            M953
            M569 P1.0 S1
            M569 P1.1 S1
            M569 P1.2 S1
            M569 P2.0 S1
            M584 X1.0 Y1.1 Z1.2 U2.0
            M92 X80 Y80 Z400 U80
            M906 X800 Y800 Z800 U800
            M203 X6000 Y6000 Z1200 U6000
            M201 X500 Y500 Z100 U500
            M201.1 Z50
            M566 X600 Y600 Z60 U600
            M208 X-100:100 Y-100:100 Z0:100 U-100:100
            M564 H0 S0
            M204 P100 T100
            M558 K0 P8 C"1.io1.in" H1 F300 T1200 A1
            G31 K0 P500 X0 Y0 U-5 Z0
            M557 U10:20 Y0:10 P2:2
            """);
        await RunAsync(bench, "G90\nG92 X0 Y0 Z0 U0\nM563 P0 S\"uprobe\" X3\nG10 P0 U3\nT0 P0");

        (string reply, IReadOnlyList<ScheduledMove> moves) = await RunCodeAsync(bench, "G29 S0");

        ScheduledMove[] travel = moves.Where(move => !move.Has(ScheduleMoveFlags.CheckEndstops)).ToArray();
        ScheduledMove[] dive = moves.Where(move => move.Has(ScheduleMoveFlags.CheckEndstops)).ToArray();
        float alongU = 15.0f / MathF.Sqrt(15.0f * 15.0f + 1.0f);
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Is.EqualTo(NotTriggered("G29")));
            Assert.That(travel, Has.Length.EqualTo(1), "one move to the point");
            Assert.That(dive, Has.Length.EqualTo(1), "and one dive");
            Assert.That(StepsOf(travel[0], UBoard, UDriver), Is.EqualTo(15 * XyuStepsPerMm),
                        "U to 10 less the probe's −5 offset, and not the tool's");
            Assert.That(StepsOf(travel[0], ZDriver), Is.EqualTo(1 * ZStepsPerMm), "up to the dive height");
            Assert.That(StepsOf(travel[0], XDriver), Is.Zero, "the axis lettered X stays where it is");
            Assert.That(StepsOf(travel[0], YDriver), Is.Zero, "Y is at the grid's Y0 already");
            Assert.That(Acceleration(travel[0]), Is.EqualTo(500.0f / alongU).Within(0.5f),
                        "U is not XY movement for a probing move, so M204 does not limit it");
            Assert.That(FlagsOf(travel[0]), Is.EqualTo((ScheduleMoveFlags)0),
                        "nor is it shaped, since only XY movement is, and it watches nothing");
            Assert.That(WatchesNothing(travel[0]), Is.True);
            Assert.That(StepsOf(dive[0], ZDriver), Is.EqualTo(-2 * ZStepsPerMm), "the dive goes 1 mm below Z0");
            Assert.That(StepsOf(dive[0], UBoard, UDriver), Is.Zero);
            Assert.That(FlagsOf(dive[0]), Is.EqualTo(ScheduleMoveFlags.CheckEndstops), "the dive is isolated");
            Assert.That(ArmedOnTheProbe(dive[0]), Is.True, "and watches the probe");
            Assert.That(Acceleration(dive[0]), Is.EqualTo(ZReducedAcceleration).Within(0.1f), "at Z's reduced acceleration");
            Assert.That(await ReadAxisAsync(bench, 'U', axis => axis.StepPos), Is.EqualTo(1200));
            Assert.That(await ReadAxisAsync(bench, 'U', axis => axis.UserPosition), Is.EqualTo(18.0f).Within(1e-3f),
                        "U is reported in the selected tool's coordinates");
            Assert.That(await ReadAxisAsync(bench, 'X', axis => axis.UserPosition), Is.EqualTo(18.0f).Within(1e-3f),
                        "and so is X, which the tool maps onto U");
            Assert.That(await ReadAxisAsync(bench, 'Z', axis => axis.UserPosition), Is.EqualTo(-1.0f).Within(1e-3f));
        });
        await bench.Host.WaitForModelAsync(model => model.Move.Axes[3].MachinePosition, (float?)15.0f, "U's machine position");
    }

    /// <summary>
    /// G29 with a tool that maps X onto two carriages moves the axis lettered X between points and
    /// never the tool's other carriage, and every dive and lift moves Z alone
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selected tool duplicates X onto X and U with a U offset of −20, and <c>G1 X0</c> puts the
    /// two carriages where the tool's map has them: X at 0, U at 20. The grid is on X and Y, so the
    /// travels take X to 20, 40, 40 and 20 and Y to 0, 0, 10 and 10, and U stays at 20 throughout.
    /// RepRapFirmware's probing moves never use the tool's map, and each starts from the coordinates
    /// the one before left in <c>raw.coords</c>, so nothing pulls the carriages towards each other.
    /// Its user position is brought into step from those coordinates when the dive stops and when the
    /// grid is done, which reads X as the mean of what the tool's two carriages say, 20 and 20 − 20.
    /// </para>
    /// <para>
    /// Every tap is closed as on the bench: the probe input closes, the controller stops the dive,
    /// and the input is released once the tap has been judged. The position the engine reports after
    /// a stop can leave X one step from where the travel before put it (KNOWN_BUGS.md), so X is
    /// checked to within that step. The dive is the only move that watches the probe, on every driver
    /// it turns, and the only one at Z's reduced acceleration (M201.1). The travels move the axis
    /// lettered X or Y, so they are XY movement and input shaped; a dive is isolated and a lift moves
    /// Z alone, so neither is
    /// </para>
    /// <para>
    /// TODO this may be legacy behaviour which we don't want for DSF4. To confirm with @dc42
    /// </remarks>
    [Test]
    public async Task GridProbingWithADuplicatingToolLeavesTheToolsOtherCarriageAlone()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync($"""
            M953
            M569 P1.0 S1
            M569 P1.1 S1
            M569 P1.2 S1
            M569 P2.0 S1
            M584 X1.0 Y1.1 Z1.2 U2.0
            M92 X80 Y80 Z400 U80
            M906 X800 Y800 Z800 U800
            M203 X6000 Y6000 Z1200 U6000
            M201 X500 Y500 Z1000 U500
            M201.1 Z50
            M566 X600 Y600 Z60 U600
            M208 X-100:100 Y-100:100 Z0:100 U-100:100
            M564 H0 S0
            M558 K0 P8 C"{ScheduleMoveBench.DriverBoard}.io1.in" H1 F300 T6000 A1
            G31 K0 P500 X0 Y0 Z0
            M557 X20:40 Y0:10 P2:2
            """);
        await RunAsync(bench, "G90\nG92 X0 Y0 Z0 U0\nM563 P0 S\"dup\" X0:3\nG10 P0 U-20\nT0 P0\nG1 X0 F6000\nM400");
        int before = bench.CanMaster.ScheduledMoves().Count;

        Task<string> grid = bench.Host.ExecuteCodeAsync("G29 S0", timeoutMs: 60_000);
        for (int tap = 0; tap < 4; tap++)
        {
            await bench.CanMaster.WaitUntilAsync(() => Dives(bench, before).Length > tap, 20_000, $"dive {tap + 1} of 4");
            ScheduledMove dive = Dives(bench, before)[tap];

            // Stopped once it has started, so that the travel before it has run to its end
            await bench.CanMaster.WaitUntilAsync(() => (int)(bench.CanMaster.Clock.MasterClock - dive.StartTime) > 0, 20_000,
                                                 $"dive {tap + 1} starting");
            bench.CanMaster.InjectInputChange(ScheduleMoveBench.DriverBoard, RemoteProbes.HandleFor(0), active: true);
            int movesBefore = bench.CanMaster.ScheduledMoves().Count;
            bench.CanMaster.InjectMotionStopped(bench.CanMaster.Clock.MasterClock, dive.MoveId,
                                                dive.Drivers.Select(driver => (driver.BoardAddress, driver.DriverNumber)).ToArray());

            // The lift is only queued once the tap has been judged, so seeing it means the probe can
            // be released without failing the tap, and before the next one checks it is clear
            await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.ScheduledMoves().Count > movesBefore, 20_000,
                                                 $"the lift after dive {tap + 1}");
            bench.CanMaster.InjectInputChange(ScheduleMoveBench.DriverBoard, RemoteProbes.HandleFor(0), active: false);
        }
        string reply = await grid;
        await bench.Host.ExecuteCodeAsync("M400");

        ScheduledMove[] moves = bench.CanMaster.ScheduledMoves().Skip(before).ToArray();
        ScheduledMove[] dives = moves.Where(move => move.Has(ScheduleMoveFlags.CheckEndstops)).ToArray();
        ScheduledMove[] travels = moves.Where(move => !move.Has(ScheduleMoveFlags.CheckEndstops) &&
                                                      (StepsOf(move, XDriver) != 0 || StepsOf(move, YDriver) != 0)).ToArray();
        ScheduledMove[] lifts = moves.Except(dives).Except(travels).ToArray();
        Assert.Multiple(async () =>
        {
            Assert.That(reply, Does.StartWith("4 points probed"));
            Assert.That(moves.Select(move => StepsOf(move, UBoard, UDriver)), Has.All.Zero,
                        "U, the tool's other carriage, never moves");
            Assert.That(travels.Select(move => StepsOf(move, XDriver)), Is.EqualTo(new[] { 1600, 1600, 0, -1600 }).Within(1),
                        "the travels take the axis lettered X to 20, 40, 40 and 20");
            Assert.That(travels.Select(move => StepsOf(move, YDriver)), Is.EqualTo(new[] { 0, 0, 800, 0 }),
                        "and Y to 0, 0, 10 and 10");
            Assert.That(travels[0].Drivers.Single(driver => driver.DriverNumber == ZDriver).Steps, Is.EqualTo(1 * ZStepsPerMm),
                        "the first travel rises to the dive height");
            Assert.That(dives, Has.Length.EqualTo(4), "one dive at each point");
            Assert.That(lifts, Has.Length.EqualTo(4), "and one lift after each");
            foreach (ScheduledMove move in dives.Concat(lifts))
            {
                Assert.That(move.Drivers.Where(driver => driver.Steps != 0).Select(driver => (driver.BoardAddress, driver.DriverNumber)),
                            Is.EqualTo(new[] { (ScheduleMoveBench.DriverBoard, ZDriver) }), $"move {move.MoveId} moves Z alone");
            }
            Assert.That(dives.Select(FlagsOf), Has.All.EqualTo(ScheduleMoveFlags.CheckEndstops), "a dive watches the probe");
            Assert.That(dives.Select(ArmedOnTheProbe), Has.All.True, "on every driver it turns");
            Assert.That(dives.Select(Acceleration), Has.All.EqualTo(ZReducedAcceleration).Within(0.1f),
                        "a dive accelerates at Z's reduced acceleration");
            Assert.That(travels.Select(FlagsOf), Has.All.EqualTo(ScheduleMoveFlags.UseInputShaping),
                        "a travel moves the axis lettered X or Y, so it is XY movement and shaped, and watches nothing");
            Assert.That(lifts.Select(FlagsOf), Has.All.EqualTo((ScheduleMoveFlags)0), "a lift moves Z alone and watches nothing");
            Assert.That(travels.Concat(lifts).Select(WatchesNothing), Has.All.True, "on any driver");
            Assert.That(lifts.Select(Acceleration), Has.All.EqualTo(ZAcceleration).Within(0.1f),
                        "a lift accelerates at Z's own acceleration");
            Assert.That(await ReadAxisAsync(bench, 'X', axis => axis.StepPos), Is.EqualTo(20 * XyuStepsPerMm).Within(1));
            Assert.That(await ReadAxisAsync(bench, 'U', axis => axis.StepPos), Is.EqualTo(20 * XyuStepsPerMm));
            Assert.That(await ReadAxisAsync(bench, 'X', axis => axis.UserPosition), Is.EqualTo(10.0f).Within(1.0f / XyuStepsPerMm),
                        "X reads the mean of the tool's carriages, 20 on X and 20 − 20 on U");
            Assert.That(await ReadAxisAsync(bench, 'U', axis => axis.UserPosition), Is.EqualTo(0.0f).Within(1e-3f),
                        "U reads its machine position plus the tool's offset");
            Assert.That(await ReadAxisAsync(bench, 'Y', axis => axis.UserPosition), Is.EqualTo(10.0f).Within(1e-3f));
        });
    }

    /// <summary>
    /// A macro run between the taps of a probing code starts from where the probing moves left the head
    /// </summary>
    /// <remarks>
    /// RepRapFirmware 3.7.0 brings the user position into step at the standstill after every probing
    /// move, because <c>SetMoveBufferDefaults</c> marks it for re-reading. <c>g29-final-retract-position</c>
    /// shows it on the bench: the relative lift in the final <c>retractprobe.g</c>, which runs after the
    /// lift from the last dive, is exactly the 5 mm it asks for. Here G30 taps twice and
    /// <c>deployprobe0.g</c>, which runs before each tap, lifts Z 1 mm relative. Before the second tap
    /// the head has been lifted the 1 mm dive height above where the first tap stopped; a position left
    /// where that tap stopped would make the macro's 1 mm come out as no move at all
    /// </remarks>
    [Test]
    public async Task AMacroBetweenTapsStartsFromWhereTheLiftLeftTheHead()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync(
            XyzMachine + "\nM558 K0 P8 C\"1.io1.in\" H1 F3000 T6000 A2 R0.2",
            sd => sd.WriteSys("deployprobe0.g", "G91\nG1 Z1 F6000\nG90\n"));
        await RunAsync(bench, "G90\nG92 X0 Y0 Z0");
        int before = bench.CanMaster.ScheduledMoves().Count;

        Task<string> probing = bench.Host.ExecuteCodeAsync("G30 S-1");

        await bench.CanMaster.WaitUntilAsync(() => Dives(bench, before).Length >= 1, 20_000, "the first dive");
        ScheduledMove firstDive = Dives(bench, before)[0];
        bench.CanMaster.InjectInputChange(ScheduleMoveBench.DriverBoard, RemoteProbes.HandleFor(0), active: true);
        bench.StopMove(firstDive);

        // The lift is queued once the tap has been read as triggered, so the probe can be let go of in
        // time for the second tap's check, which waits out the recovery time after the macro
        int firstDiveIndex = bench.CanMaster.ScheduledMoves().ToList().FindIndex(move => move.MoveId == firstDive.MoveId);
        await bench.WaitForMovesAsync(firstDiveIndex + 2, "the lift after the first tap");
        bench.CanMaster.InjectInputChange(ScheduleMoveBench.DriverBoard, RemoteProbes.HandleFor(0), active: false);

        await bench.CanMaster.WaitUntilAsync(() => Dives(bench, before).Length >= 2, 20_000, "the second dive");
        ScheduledMove secondDive = Dives(bench, before)[1];
        bench.CanMaster.InjectInputChange(ScheduleMoveBench.DriverBoard, RemoteProbes.HandleFor(0), active: true);
        bench.StopMove(secondDive);
        string reply = await probing;

        ScheduledMove[] between = bench.CanMaster.ScheduledMoves()
                                       .SkipWhile(move => move.MoveId != firstDive.MoveId).Skip(1)
                                       .TakeWhile(move => move.MoveId != secondDive.MoveId)
                                       .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(reply.Trim(), Does.StartWith("Stopped at height"));
            Assert.That(between, Has.Length.EqualTo(3), "the lift, the macro's move and the move to the dive height");
            Assert.That(StepsOf(between[0], ZDriver), Is.EqualTo(1 * ZStepsPerMm).Within(1), "the lift to the dive height");
            Assert.That(StepsOf(between[1], ZDriver), Is.EqualTo(1 * ZStepsPerMm),
                        "the macro's relative 1 mm, measured from where the lift left the head");
        });
    }

    // ---- Resume ----------------------------------------------------------------------------------

    /// <summary>
    /// The move back to where the job paused gets the skew correction
    /// </summary>
    /// <remarks>
    /// <c>m24-resume-with-axis-skew</c>, with the head moved away by pause.g on the job's channel: the
    /// instrumented pause.g parks at X0 Y0, and runs only on a machine whose axes are all homed, so Z is
    /// homed with them. The restore point is X10 Y30, so with tanXY 0.01 the move back takes X to 10.3,
    /// 824 steps, where the uncorrected move is 800
    /// </remarks>
    [Test]
    public async Task TheMoveBackOnResumeCarriesTheSkewCorrection()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: "M556 S100 X1 Y0 Z0 P0\nG92 X0 Y0 Z0",
            machineConfig: XyzMachine,
            prepareSd: sd => sd.WriteGCode("job.gcode", "G90\nG1 X10 Y30 F3000\nM226\nG1 X0 Y0 F3000\n"));

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);
        Assert.That(await bench.Host.RestorePointAsync(1), Is.EqualTo((10.0, 30.0)), "the restore point");

        // pause.g's park is on its way to the controller by now, and has to have arrived before the
        // moves after it are counted
        await bench.Host.ExecuteCodeAsync("M400");
        int before = bench.CanMaster.ScheduledMoves().Count;

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        IReadOnlyList<ScheduledMove> moves = bench.CanMaster.ScheduledMoves().Skip(before).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(moves, Has.Count.EqualTo(2), "the move back and the job's last move");
            Assert.That(StepsOf(moves[0], XDriver), Is.EqualTo((int)MathF.Round((10.0f + 0.01f * 30.0f) * XyuStepsPerMm)),
                        "X goes back to 10 plus tanXY × Y30");
            Assert.That(StepsOf(moves[0], YDriver), Is.EqualTo(30 * XyuStepsPerMm));
            Assert.That(StepsOf(moves[1], XDriver), Is.EqualTo(-StepsOf(moves[0], XDriver)), "and the job's G1 X0 Y0 undoes it");
            Assert.That(StepsOf(moves[1], YDriver), Is.EqualTo(-30 * XyuStepsPerMm));
            Assert.That(moves.Select(FlagsOf), Has.All.EqualTo(ScheduleMoveFlags.UseInputShaping),
                        "both are XY movement, shaped, and neither watches anything");
            Assert.That(moves.Select(WatchesNothing), Has.All.True);
        });
    }

    /// <summary>
    /// The move back to where the job paused goes through the selected tool's map and is measured by
    /// it, as the job's own G1 moves are
    /// </summary>
    /// <remarks>
    /// <c>m24-resume-with-tool-axis-mapping</c>: the tool maps X onto U alone, pause.g moves X −5 and so
    /// U, and the move back takes U from 5 to 10, 400 steps on U's board. U is the tool's X, so the move
    /// back is XY movement and held to M204's 100 mm/s², as the job's next code, G1 X0, is. This is a
    /// deliberate difference: RepRapFirmware measures the move back with no tool and lets it accelerate
    /// at U's own 5000 mm/s², where the same move on a carriage lettered X is held to M204 (the
    /// known-bad note on <c>m24-resume-with-tool-axis-mapping</c>; rrf-differences.md section 5.3)
    /// </remarks>
    [Test]
    public async Task TheMoveBackOnResumeIsMeasuredByTheToolsMap()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: "M204 P100 T100\nG92 X0 Y0 Z0 U0\nM563 P0 S\"xmap\" X3\nT0 P0",
            machineConfig: """
                M953
                M569 P1.0 S1
                M569 P1.1 S1
                M569 P1.2 S1
                M569 P2.0 S1
                M584 X1.0 Y1.1 Z1.2 U2.0
                M92 X80 Y80 Z400 U80
                M906 U800
                M203 X6000 Y6000 Z1200 U6000
                M201 X500 Y500 Z100 U5000
                M566 X600 Y600 Z60 U600
                M208 X-500:500 Y-500:500 Z-500:500 U-500:500
                M564 H0 S0
                """,
            prepareSd: sd =>
            {
                sd.WriteSys("pause.g", "set global.pauseRan = global.pauseRan + 1\nG91\nG1 X-5 F3000\nG90\n");
                sd.WriteGCode("job.gcode", "G90\nG1 X10 F3000\nM226\nG1 X0 F3000\n");
            });

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        // pause.g's move is on its way to the controller by now, and has to have arrived before the
        // moves after it are counted
        await bench.Host.ExecuteCodeAsync("M400");
        int before = bench.CanMaster.ScheduledMoves().Count;

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        IReadOnlyList<ScheduledMove> moves = bench.CanMaster.ScheduledMoves().Skip(before).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(moves, Has.Count.EqualTo(2), "the move back and the job's last move");
            Assert.That(moves.Select(move => move.Drivers.Where(driver => driver.Steps != 0)
                                                         .Select(driver => (driver.BoardAddress, driver.DriverNumber))),
                        Has.All.EqualTo(new[] { (UBoard, UDriver) }), "each moves U alone");
            Assert.That(StepsOf(moves[0], UBoard, UDriver), Is.EqualTo(5 * XyuStepsPerMm), "U back from 5 to 10");
            Assert.That(Acceleration(moves[0]), Is.EqualTo(100.0f).Within(0.1f),
                        "U is the tool's X, so the move back is XY movement and M204 T applies");
            Assert.That(StepsOf(moves[1], UBoard, UDriver), Is.EqualTo(-10 * XyuStepsPerMm), "the job's G1 X0 moves U to 0");
            Assert.That(Acceleration(moves[1]), Is.EqualTo(100.0f).Within(0.1f),
                        "a G1 X with the tool selected is XY movement, so M204 T applies");
            Assert.That(moves.Select(FlagsOf), Has.All.EqualTo(ScheduleMoveFlags.UseInputShaping),
                        "both are XY movement, and so both are input shaped");
            Assert.That(moves.Select(WatchesNothing), Has.All.True, "neither watches anything");
        });
    }

    /// <summary>
    /// The move back to where the job paused is skewed through the selected tool's map, so it returns
    /// the tool's carriage to where the job's own moves put it and leaves the other carriage alone
    /// </summary>
    /// <remarks>
    /// The tool maps X onto U alone and tanXY is 0.01. The job's G1 X10 Y30 puts U at 10 + 0.01 × 30;
    /// pause.g moves X −5 Y −10, so U to 5 + 0.01 × 20 and Y to 20. The move back skews with the tool's
    /// X, so U returns to 10.3, 408 steps, and the X carriage does not move. Skewed with no tool, as
    /// RepRapFirmware skews it, the correction would go on the axis lettered X instead, moving that
    /// carriage 8 steps and leaving U short of where the job left it (rrf-differences.md section 5.3)
    /// </remarks>
    [Test]
    public async Task TheMoveBackOnResumeIsSkewedThroughTheToolsMap()
    {
        await using JobBench bench = await JobControlBench.StartAsync(
            configExtra: "M556 S100 X1 Y0 Z0 P0\nG92 X0 Y0 Z0 U0\nM563 P0 S\"xmap\" X3\nT0 P0",
            machineConfig: """
                M953
                M569 P1.0 S1
                M569 P1.1 S1
                M569 P1.2 S1
                M569 P2.0 S1
                M584 X1.0 Y1.1 Z1.2 U2.0
                M92 X80 Y80 Z400 U80
                M906 U800
                M203 X6000 Y6000 Z1200 U6000
                M201 X500 Y500 Z100 U500
                M566 X600 Y600 Z60 U600
                M208 X-500:500 Y-500:500 Z-500:500 U-500:500
                M564 H0 S0
                """,
            prepareSd: sd =>
            {
                sd.WriteSys("pause.g", "set global.pauseRan = global.pauseRan + 1\nG91\nG1 X-5 Y-10 F3000\nG90\n");
                sd.WriteGCode("job.gcode", "G90\nG1 X10 Y30 F3000\nM226\nG1 X0 Y0 F3000\n");
            });

        await bench.Host.ExecuteCodeAsync("M32 \"0:/gcodes/job.gcode\"");
        await bench.Host.WaitForStatusAsync(MachineStatus.Paused);

        // pause.g's move is on its way to the controller by now, and has to have arrived before the
        // moves after it are counted
        await bench.Host.ExecuteCodeAsync("M400");
        int before = bench.CanMaster.ScheduledMoves().Count;

        await bench.Host.ExecuteCodeAsync("M24");
        await bench.Host.WaitForStatusAsync(MachineStatus.Idle);
        IReadOnlyList<ScheduledMove> moves = bench.CanMaster.ScheduledMoves().Skip(before).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(moves, Has.Count.EqualTo(2), "the move back and the job's last move");
            Assert.That(StepsOf(moves[0], UBoard, UDriver), Is.EqualTo((int)MathF.Round((10.3f - 5.2f) * XyuStepsPerMm)),
                        "U back to 10 + tanXY × Y30 from 5 + tanXY × Y20");
            Assert.That(StepsOf(moves[0], YDriver), Is.EqualTo(10 * XyuStepsPerMm), "Y back from 20 to 30");
            Assert.That(StepsOf(moves[0], XDriver), Is.Zero, "the X carriage is not the tool's and is not skewed");
            Assert.That(StepsOf(moves[1], UBoard, UDriver), Is.EqualTo((int)MathF.Round(-10.3f * XyuStepsPerMm)),
                        "the job's G1 X0 Y0 takes U back to 0");
            Assert.That(StepsOf(moves[1], XDriver), Is.Zero);
            Assert.That(moves.Select(FlagsOf), Has.All.EqualTo(ScheduleMoveFlags.UseInputShaping),
                        "both are XY movement, shaped, and neither watches anything");
            Assert.That(moves.Select(WatchesNothing), Has.All.True);
        });
    }

    // ---- Known gaps ------------------------------------------------------------------------------

    /// <summary>
    /// G30 P with skew: the lift to the dive height, the travel to the point with the correction, the
    /// dive and the return, as <c>g30-probe-point-with-axis-skew</c> recorded them
    /// </summary>
    /// <remarks>G30 P is not ported, so DSF refuses the code</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30PTravelsToThePointWithTheSkewCorrection()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync(XyzMachine + "\nM556 S100 X1 Y0 Z0 P0");
        await RunAsync(bench, "G90\nG92 X0 Y0 Z0");

        (string reply, IReadOnlyList<ScheduledMove> moves) = await RunCodeAsync(bench, "G30 P0 X0 Y50 Z-99999");

        Assert.That(reply, Is.EqualTo(NotTriggered("G30")));
        Assert.That(moves, Has.Count.EqualTo(4), "lift, travel, dive and return");
        Assert.Multiple(async () =>
        {
            Assert.That(moves.Select(move => (StepsOf(move, XDriver), StepsOf(move, YDriver), StepsOf(move, ZDriver))),
                        Is.EqualTo(new[] { (0, 0, 400), (40, 4000, 0), (0, 0, -40800), (0, 0, 40800) }));
            Assert.That(moves.Select(move => move.Has(ScheduleMoveFlags.CheckEndstops)), Is.EqualTo(new[] { false, false, true, false }));
            Assert.That(await ReadAxisAsync(bench, 'X', axis => axis.UserPosition), Is.EqualTo(0.0f).Within(1e-3f));
            Assert.That(await ReadAxisAsync(bench, 'Y', axis => axis.UserPosition), Is.EqualTo(50.0f).Within(1e-3f));
            Assert.That(await ReadAxisAsync(bench, 'Z', axis => axis.UserPosition), Is.EqualTo(1.0f).Within(1e-3f));
        });
    }

    /// <summary>
    /// G30 P with a tool mapping X onto U: the travel moves the axis lettered X and leaves U, as
    /// <c>g30-probe-point-with-tool-axis-mapping</c> recorded it
    /// </summary>
    /// <remarks>G30 P is not ported, so DSF refuses the code</remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30PIgnoresTheToolsAxisMap()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync(XyzMachine + "\nM569 P2.0 S1\nM584 U2.0\nM92 U80\nM208 U-100:100");
        await RunAsync(bench, "G90\nG92 X0 Y0 Z0 U0\nM563 P0 S\"xmap\" X3\nT0 P0");

        (string reply, IReadOnlyList<ScheduledMove> moves) = await RunCodeAsync(bench, "G30 P0 X20 Y0 Z-99999");

        Assert.That(reply, Is.EqualTo(NotTriggered("G30")));
        Assert.That(moves, Has.Count.EqualTo(4), "lift, travel, dive and return");
        Assert.Multiple(() =>
        {
            Assert.That(moves.Select(move => (StepsOf(move, XDriver), StepsOf(move, ZDriver), StepsOf(move, UBoard, UDriver))),
                        Is.EqualTo(new[] { (0, 400, 0), (1600, 0, 0), (0, -40800, 0), (0, 40800, 0) }),
                        "X moves to 20 and U stays where it is");
        });
    }

    /// <summary>
    /// G30 without P probes from where the head is, so the code is the one dive
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>ExecuteG30</c> goes straight to <c>probingAtPoint2a</c> for G30 without P,
    /// and every <c>g30-dive-*</c> reference records the one frame. DSF first moves Z to the dive
    /// height above the axis minimum, which on the bench is 99 mm down
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task G30WithoutPDivesFromWhereTheHeadIs()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync(XyzMachine);
        await RunAsync(bench, "G90\nG92 X0 Y0 Z0");

        (string reply, IReadOnlyList<ScheduledMove> moves) = await RunCodeAsync(bench, "G30 S-1");

        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.EqualTo(NotTriggered("G30")));
            Assert.That(moves, Has.Count.EqualTo(1), "one move");
            Assert.That(moves.Select(move => move.Has(ScheduleMoveFlags.CheckEndstops)), Has.All.True, "the dive");
            Assert.That(moves.Select(move => StepsOf(move, ZDriver)), Is.EqualTo(new[] { -40400 }));
        });
    }

    /// <summary>
    /// Loading a height map moves the reported Z by the map's correction where the head is
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>LoadHeightMap</c> ends with <c>ActivateHeightmap</c>, which re-reads the
    /// position and brings the user position into step with <c>ToolOffsetInverseTransform</c>, and
    /// <c>g30-dive-with-mesh</c> reads Z −0.07 before its dive. DSF does not bring the interpreter back
    /// into step when a map is loaded
    /// </remarks>
    [Test]
    [Category("KnownGap")]
    public async Task LoadingAHeightMapReportsZWithTheCorrectionTakenOff()
    {
        await using JobBench bench = await ScheduleMoveBench.StartAsync(XyzMachine, sd => sd.WriteSys(MeshFile, Mesh));
        await RunAsync(bench, $"G90\nG92 X25 Y25 Z0\nM375 P\"{MeshFile}\"");

        Assert.That(await ReadAxisAsync(bench, 'Z', axis => axis.UserPosition), Is.EqualTo(-0.07f).Within(1e-3f));
    }
}
