using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Native;
using NUnit.Framework;

namespace SystemTests.Host;

/// <summary>
/// One decoded ScheduleMove packet, which is one move as the link carried it. A move with more
/// drivers than a packet holds arrives as several of these sharing a <see cref="MoveId"/>
/// </summary>
internal sealed record ScheduledMove(ScheduleMoveHeader Header, ScheduleMoveDriver[] Drivers)
{
    /// <summary>The move this packet belongs to</summary>
    public uint MoveId => Header.MoveId;

    /// <summary>Master step clock reading the boards are to start the move at</summary>
    public uint StartTime => Header.WhenToExecute;

    /// <summary>How long the boards will take to run the move, in step clocks</summary>
    public uint TotalClocks => Header.AccelClocks + Header.SteadyClocks + Header.DecelClocks;

    /// <summary>Whether the header's flags byte carries the given flag</summary>
    public bool Has(ScheduleMoveFlags flag) => ((ScheduleMoveFlags)Header.Flags & flag) != 0;

    /// <summary>The record for one driver, or null if the move does not turn it</summary>
    public ScheduleMoveDriver? DriverFor(byte board, byte driver)
    {
        foreach (ScheduleMoveDriver record in Drivers)
        {
            if (record.BoardAddress == board && record.DriverNumber == driver)
            {
                return record;
            }
        }
        return null;
    }
}

/// <summary>
/// The shared pieces of the ScheduleMove scenarios: the machine configurations the moves are
/// commanded against, and the helpers that turn the capture back into moves to assert on
/// </summary>
/// <remarks>
/// Every assertion in these scenarios is made against the packets the link carried, because that is
/// the whole of what the machine is told to do: the expansion boards see nothing else
/// </remarks>
internal static class ScheduleMoveBench
{
    /// <summary>CAN address of the expansion board carrying the drivers, endstops and probes</summary>
    /// <remarks>Board 0 runs DuetCANMaster and has no drivers of its own</remarks>
    public const byte DriverBoard = 1;

    /// <summary>
    /// X, Y, Z and one extruder on board 1, free to move without homing and with cold extrusion
    /// allowed so that a move may extrude with no heater configured. M953 comes first: with the bus
    /// disabled the configuration's CAN messages would be answered with BusError, as the real
    /// controller answers them. Tool 0 is selected because an extrusion with no tool moves nothing
    /// </summary>
    public const string CartesianConfig = """
        M953
        M569 P1.0 S1
        M569 P1.1 S1
        M569 P1.2 S1
        M569 P1.3 S1
        M584 X1.0 Y1.1 Z1.2 E1.3
        M92 X80 Y80 Z400 E420
        M906 X800 Y800 Z800 E800
        M201 X500 Y500 Z100 E250
        M203 X6000 Y6000 Z600 E3600
        M566 X900 Y900 Z60 E120
        M208 X0:200 Y0:200 Z0:150
        M302 P1
        M564 H0 S0
        M563 P0 D0 H-1
        T0
        """;

    /// <summary>Steps per mm of each configured drive of <see cref="CartesianConfig"/> (M92)</summary>
    public const float XyStepsPerMm = 80.0f, ZStepsPerMm = 400.0f, EStepsPerMm = 420.0f;

    /// <summary>Acceleration limit of X and Y in <see cref="CartesianConfig"/>, mm/s² (M201)</summary>
    public const float XyAcceleration = 500.0f;

    /// <summary>Maximum instantaneous speed change of X and Y in <see cref="CartesianConfig"/>, mm/s (M566)</summary>
    public const float XyJerk = 900.0f / 60.0f;

    /// <summary>
    /// M669 turning segmentation on: 100 segments per second of movement, and no segment shorter
    /// than 0.2 mm. Either value at zero turns it off again, which is what a Cartesian starts as.
    /// It names no geometry, because selecting one with M669 K starts the segmentation again, so
    /// this has to come after whatever geometry the scenario configures
    /// </summary>
    public const string SegmentationOn = "M669 S100 T0.2";

    /// <summary>Segments per second the above asks for (M669 S)</summary>
    public const float SegmentsPerSecond = 100.0f;

    /// <summary>Shortest segment the above allows, in mm (M669 T)</summary>
    public const float MinSegmentLength = 0.2f;

    /// <summary>
    /// Start a bench whose config.g is exactly the given configuration
    /// </summary>
    /// <param name="config">The configuration to boot from</param>
    /// <param name="prepareSd">Populates the rest of the virtual SD card, e.g. a macro of moves</param>
    public static async Task<JobBench> StartAsync(string config, Action<VirtualSd>? prepareSd = null)
    {
        ScriptedCanMaster canMaster = new(JobControlBench.SocketPath());
        canMaster.AckCanRequestsWithStandardReplies();
        DcsTestHost host;
        try
        {
            host = await DcsTestHost.StartAsync(canMaster, sd =>
            {
                sd.WriteSys("config.g", config + DcsTestHost.ConfigDoneMarker);
                prepareSd?.Invoke(sd);
            });
        }
        catch
        {
            canMaster.Dispose();
            throw;
        }
        await host.WaitForConfigDoneAsync();
        return new JobBench(canMaster, host);
    }

    /// <summary>
    /// Start a bench on the given machine, with or without segmentation, in relative coordinates so
    /// that each scenario's move is the distance it commands
    /// </summary>
    /// <param name="config">The machine to boot, without the segmentation line</param>
    /// <param name="segmented">Whether M669 turns segmentation on</param>
    /// <param name="prepareSd">Populates the rest of the virtual SD card</param>
    public static async Task<JobBench> StartRelativeAsync(string config, bool segmented,
                                                          Action<VirtualSd>? prepareSd = null)
    {
        // The segmentation comes last: M669 K starts it again, so a machine that selects a geometry
        // would otherwise turn it back off
        JobBench bench = await StartAsync(config + "\n" + (segmented ? SegmentationOn : ""), prepareSd);
        try
        {
            await bench.Host.ExecuteCodeAsync("G91");
        }
        catch
        {
            await bench.DisposeAsync();
            throw;
        }
        return bench;
    }

    /// <summary>
    /// Start a bench on <see cref="CartesianConfig"/> plus any per-scenario configuration
    /// </summary>
    /// <param name="segmented">Whether M669 turns segmentation on</param>
    /// <param name="configExtra">Extra configuration lines, e.g. an endstop or M572</param>
    /// <param name="prepareSd">Populates the rest of the virtual SD card</param>
    public static Task<JobBench> StartCartesianAsync(bool segmented = false, string configExtra = "",
                                                     Action<VirtualSd>? prepareSd = null)
        => StartRelativeAsync(CartesianConfig + "\n" + configExtra, segmented, prepareSd);

    /// <summary>Every move scheduled so far, in the order the link carried them</summary>
    public static IReadOnlyList<ScheduledMove> ScheduledMoves(this ScriptedCanMaster canMaster)
        => canMaster.SbcPackets(SbcRequest.ScheduleMove)
                    .Select(packet =>
                    {
                        (ScheduleMoveHeader header, ScheduleMoveDriver[] drivers) = packet.DecodeScheduleMove();
                        return new ScheduledMove(header, drivers);
                    })
                    .ToArray();

    /// <summary>
    /// Run one code and return every move it scheduled. The moves are waited for with M400, so what
    /// comes back is all of them however many pieces the move was cut into
    /// </summary>
    /// <param name="bench">The bench to run on</param>
    /// <param name="code">The code to run</param>
    /// <returns>The moves the code scheduled, in order</returns>
    /// <remarks>
    /// Only for a move that finishes on its own. A move that waits for an endstop never completes
    /// until the controller reports the stop, so those scenarios wait with
    /// <see cref="WaitForMovesAsync"/> and close the move themselves
    /// </remarks>
    public static async Task<IReadOnlyList<ScheduledMove>> RunMoveAsync(this JobBench bench, string code)
    {
        int before = bench.CanMaster.ScheduledMoves().Count;
        await bench.Host.ExecuteCodeAsync(code);
        await bench.Host.ExecuteCodeAsync("M400");
        return bench.CanMaster.ScheduledMoves().Skip(before).ToArray();
    }

    /// <summary>
    /// Wait until an object model expression reads the given text, evaluated through the interpreter
    /// </summary>
    /// <param name="host">The host to ask</param>
    /// <param name="read">What to read out of the object model, e.g. an endstop's triggered flag</param>
    /// <param name="expected">The value it should reach</param>
    /// <param name="what">What is being waited for, for the timeout message</param>
    /// <param name="timeoutMs">How long to wait</param>
    /// <remarks>
    /// What a board reports arrives asynchronously, so a scenario that depends on the machine having
    /// taken it in has to wait for it rather than for a round trip of its own
    /// </remarks>
    public static async Task WaitForModelAsync<T>(this DcsTestHost host, Func<DuetControlServer.Model.ObjectModel, T> read, T expected,
                                                  string what, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        T value;
        do
        {
            value = await host.ReadModelAsync(read);
            if (Equals(value, expected))
            {
                return;
            }
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"{what} stayed \"{value}\", expected \"{expected}\"");
    }

    /// <summary>Wait until at least the given number of moves have been scheduled, and return them all</summary>
    /// <param name="bench">The bench to watch</param>
    /// <param name="count">Number of moves to wait for</param>
    /// <param name="what">What the test is waiting for, for the timeout message</param>
    /// <param name="timeoutMs">How long to wait</param>
    public static async Task<IReadOnlyList<ScheduledMove>> WaitForMovesAsync(this JobBench bench, int count,
                                                                             string what, int timeoutMs = 20_000)
    {
        await bench.CanMaster.WaitUntilAsync(() => bench.CanMaster.ScheduledMoves().Count >= count, timeoutMs, what);
        return bench.CanMaster.ScheduledMoves();
    }

    /// <summary>
    /// Close an endstop or probing move, as the controller reports a stop it made itself
    /// </summary>
    /// <param name="bench">The bench the move is running on</param>
    /// <param name="move">The move to stop, which names the drivers to report stopped</param>
    public static void StopMove(this JobBench bench, ScheduledMove move)
        => bench.CanMaster.InjectMotionStopped(bench.CanMaster.Clock.MasterClock, move.MoveId,
                                               move.Drivers.Select(d => (d.BoardAddress, d.DriverNumber)).ToArray());

    /// <summary>Net steps these moves command one driver of <see cref="DriverBoard"/> to take</summary>
    public static int Steps(this IEnumerable<ScheduledMove> moves, byte driver)
        => moves.SelectMany(move => move.Drivers)
                .Where(record => record.BoardAddress == DriverBoard && record.DriverNumber == driver)
                .Sum(record => record.Steps);

    /// <summary>Net microsteps of extrusion these moves command one driver of <see cref="DriverBoard"/></summary>
    public static float Extrusion(this IEnumerable<ScheduledMove> moves, byte driver)
        => moves.SelectMany(move => move.Drivers)
                .Where(record => record.BoardAddress == DriverBoard && record.DriverNumber == driver)
                .Sum(record => record.Extrusion);

    /// <summary>How far these moves travel in total, in mm</summary>
    public static float Distance(this IEnumerable<ScheduledMove> moves)
        => moves.Sum(move => move.Header.TotalDistance);

    /// <summary>The drivers these moves turn, as board and driver number pairs, without repeats</summary>
    public static (byte Board, byte Driver)[] DriversMoved(this IEnumerable<ScheduledMove> moves)
        => moves.SelectMany(move => move.Drivers)
                .Select(record => (record.BoardAddress, record.DriverNumber))
                .Distinct()
                .ToArray();

    /// <summary>A speed the packet carries, in mm/s rather than the mm per step clock of the wire</summary>
    public static float MmPerSecond(float speed) => speed * SpiWire.StepClockRate;

    /// <summary>An acceleration the packet carries, in mm/s²</summary>
    public static float MmPerSecondSquared(float acceleration)
        => acceleration * SpiWire.StepClockRate * SpiWire.StepClockRate;

    /// <summary>A duration the packet carries, in seconds</summary>
    public static float Seconds(uint clocks) => clocks / (float)SpiWire.StepClockRate;

    /// <summary>Assert that a driver record watches no input, which is what every move but an endstop move carries</summary>
    /// <param name="driver">The driver record</param>
    /// <param name="because">What the assertion is showing</param>
    public static void AssertWatchesNothing(ScheduleMoveDriver driver, string because)
        => Assert.Multiple(() =>
        {
            Assert.That(driver.StopOnBoard, Is.EqualTo(ScheduleMovePacket.NoEndstopBoard), because);
            Assert.That(driver.StopOnHandle, Is.Zero, because);
            Assert.That(driver.StopGroup, Is.EqualTo(ScheduleMovePacket.NoStopGroup), because);
            Assert.That((StopAction)driver.StopAction, Is.EqualTo(StopAction.None), because);
        });
}
