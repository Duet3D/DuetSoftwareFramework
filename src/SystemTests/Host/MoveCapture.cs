using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Native;
using NUnit.Framework;
using SystemTests.Host.Config;

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
/// Reads of the moves the link carried. Every assertion about motion is made against these packets,
/// because they are the whole of what the machine is told to do: the expansion boards see nothing else
/// </summary>
internal static class MoveCapture
{
    /// <summary>Every move scheduled so far, in the order the link carried them</summary>
    public static IReadOnlyList<ScheduledMove> ScheduledMoves(this ScriptedCanMaster canMaster)
        => canMaster.SbcPackets(SbcRequest.ScheduleMove)
                    .Select(packet =>
                    {
                        (ScheduleMoveHeader header, ScheduleMoveDriver[] drivers) = packet.DecodeScheduleMove();
                        return new ScheduledMove(header, drivers);
                    })
                    .ToArray();

    /// <summary>Every scheduled move's total distance so far, in order</summary>
    public static float[] MoveDistances(this ScriptedCanMaster canMaster)
        => canMaster.ScheduledMoves().Select(move => move.Header.TotalDistance).ToArray();

    /// <summary>
    /// The net steps scheduled so far for one driver of <see cref="Machines.DriverBoard"/>. Steps are
    /// what the expansion board would execute, so their sum stands in for where the physical head ends up
    /// </summary>
    public static int ScheduledSteps(this ScriptedCanMaster canMaster, byte driver)
        => canMaster.ScheduledMoves().Steps(driver);

    /// <summary>
    /// The net extrusion scheduled so far for one driver of <see cref="Machines.DriverBoard"/>, in
    /// microsteps. An extruder's movement is carried as extrusion rather than as steps, because
    /// pressure advance is applied on the board, so <see cref="ScheduledSteps"/> reads zero for one
    /// however much filament it is asked for
    /// </summary>
    public static float ScheduledExtrusion(this ScriptedCanMaster canMaster, byte driver)
        => canMaster.ScheduledMoves().Extrusion(driver);

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
    public static async Task<IReadOnlyList<ScheduledMove>> RunMoveAsync(this Bench bench, string code)
    {
        int before = bench.CanMaster.ScheduledMoves().Count;
        await bench.Host.ExecuteCodeAsync(code);
        await bench.Host.ExecuteCodeAsync("M400");
        return bench.CanMaster.ScheduledMoves().Skip(before).ToArray();
    }

    /// <summary>Wait until at least the given number of moves have been scheduled, and return them all</summary>
    /// <param name="bench">The bench to watch</param>
    /// <param name="count">Number of moves to wait for</param>
    /// <param name="what">What the test is waiting for, for the timeout message</param>
    /// <param name="timeoutMs">How long to wait</param>
    public static async Task<IReadOnlyList<ScheduledMove>> WaitForMovesAsync(this Bench bench, int count,
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
    public static void StopMove(this Bench bench, ScheduledMove move)
        => bench.CanMaster.InjectMotionStopped(bench.CanMaster.Clock.MasterClock, move.MoveId,
                                               move.Drivers.Select(d => (d.BoardAddress, d.DriverNumber)).ToArray());

    /// <summary>Net steps these moves command one driver of <see cref="Machines.DriverBoard"/> to take</summary>
    public static int Steps(this IEnumerable<ScheduledMove> moves, byte driver)
        => moves.SelectMany(move => move.Drivers)
                .Where(record => record.BoardAddress == Machines.DriverBoard && record.DriverNumber == driver)
                .Sum(record => record.Steps);

    /// <summary>Net microsteps of extrusion these moves command one driver of <see cref="Machines.DriverBoard"/></summary>
    public static float Extrusion(this IEnumerable<ScheduledMove> moves, byte driver)
        => moves.SelectMany(move => move.Drivers)
                .Where(record => record.BoardAddress == Machines.DriverBoard && record.DriverNumber == driver)
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
