using System;
using System.Collections.Generic;
using DuetAPI.Utility;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Native;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace DuetControlServer.Motion;

/// <summary>
/// Undoing the overshoot between an endstop firing and the drives actually stopping
/// </summary>
/// <remarks>
/// <para>
/// Three components share this and none of them can do it alone. An expansion board notices the
/// input change; DuetCANMaster stops the drivers, because it is the only one close enough to the bus
/// for the latency to be acceptable; but neither of them knows where the drives <em>should</em> end
/// up, because neither generated the steps. The motion engine did, and it holds the segment chain, so
/// it can say where a drive was at an instant that has already passed. This is what asks it and what
/// decides the consequences.
/// </para>
/// <para>
/// The decision is here rather than in the engine deliberately. The engine can answer "where was
/// drive D at tick T"; it cannot answer "what was this move for", which is what says whether a drive
/// has finished stopping and what its position should become. Keeping the question native and the
/// answer managed is also what leaves every CAN message originating in DuetControlServer, which was
/// true of everything except the revert.
/// </para>
/// <para>
/// RepRapFirmware does the equivalent in <c>CanMotion::GetUrgentMessage</c>, from step counts its own
/// step interrupt captured at the trigger. The quantity on the wire is the same one
/// </para>
/// </remarks>
internal sealed class EndstopCorrection(
    NativeLink nativeLink,
    MovePlanner planner,
    ILogger<EndstopCorrection> logger)
{
    /// <summary>
    /// How long the boards are given to wind back, as in RepRapFirmware's
    /// <c>BasicDriverPositionRevertMillis</c>
    /// </summary>
    /// <remarks>
    /// The duration of the corrective move the board synthesises, not a deadline for the message to
    /// arrive in: the board takes the difference from the steps it actually took, so a late revert is
    /// still a correct one
    /// </remarks>
    public const uint BasicRevertMillis = 40;

    /// <summary>
    /// How long to wait for the wind-back before a move may follow it
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>TotalDriverPositionRevertMillis</c>: the wind-back plus an allowance for
    /// getting the messages out. `RevertStoppedDrivers` holds the move open for this long, and it has
    /// to be waited out here for the same reason - the corrective move is synthesised on the board, so
    /// the engine's ring counters never see it and draining the rings does not mean the machine has
    /// stopped
    /// </remarks>
    public static readonly TimeSpan TotalRevertTime = TimeSpan.FromMilliseconds(BasicRevertMillis + 10);

    /// <summary>
    /// Step clock rate the revert duration is expressed in
    /// </summary>
    private const float StepClockRate = MotionLimits.StepClockRate;

    /// <summary>
    /// One driver the controller stopped
    /// </summary>
    /// <param name="Board">CAN address of the board carrying it</param>
    /// <param name="Driver">Driver number on that board</param>
    private readonly record struct StoppedDriver(byte Board, byte Driver);

    private readonly Lock _lock = new();
    private DateTime _lastRevertSentAt = DateTime.MinValue;

    /// <summary>
    /// Whether a wind-back is still in progress
    /// </summary>
    /// <remarks>
    /// Checked before a move that follows an endstop move is allowed to run. Scheduling onto a driver
    /// that is still winding back would have the two overlap
    /// </remarks>
    public bool IsReverting
    {
        get
        {
            using (_lock.EnterScope())
            {
                return DateTime.UtcNow - _lastRevertSentAt < TotalRevertTime;
            }
        }
    }

    /// <summary>
    /// Correct the drives an endstop cut short
    /// </summary>
    /// <param name="whenTriggered">Master step-clock time the endstop reported, zero if it sent none</param>
    /// <param name="drivers">The drivers the controller stopped</param>
    /// <remarks>
    /// <para>
    /// Each board is told the step counts its own drivers should have ended up with, expressed as
    /// steps since the move began - which is what <c>CanMessageRevertPosition</c> means, and what the
    /// board differences against the steps it actually took. One message per board, because a message
    /// names drivers by their number on the board that carries them.
    /// </para>
    /// <para>
    /// The engine's own position is then set to match, so the next move is planned as a delta from
    /// where the machine really stopped. That replaces the endpoint patching the engine used to do to
    /// the move in flight: the move's planned endpoints are simply not authoritative for a move that
    /// stopped short, and treating them as such was what made this fragile.
    /// </para>
    /// <para>
    /// Which axes were stopped is latched into <see cref="MovementState.EndstopsTriggered"/> as it
    /// goes, because this is the only moment at which it is known. Nothing else reports it: a stall
    /// and a Z probe used as an endstop arrive under handles of their own rather than as an endstop
    /// state, and even a switch has been wound back onto its own threshold by the time the move
    /// finishes. RepRapFirmware latches the same fact in the same place, from its step interrupt
    /// </para>
    /// </remarks>
    public void Apply(uint whenTriggered, ReadOnlySpan<MotionStoppedDriverEntry> drivers)
    {
        if (drivers.IsEmpty)
        {
            return;
        }

        // Grouped by board, because a revert message names drivers by their number on one board
        Dictionary<byte, List<StoppedDriver>> byBoard = [];
        foreach (MotionStoppedDriverEntry entry in drivers)
        {
            if (!byBoard.TryGetValue(entry.BoardAddress, out List<StoppedDriver>? list))
            {
                byBoard[entry.BoardAddress] = list = [];
            }
            list.Add(new StoppedDriver(entry.BoardAddress, entry.DriverNumber));
        }

        bool anySent = false;
        uint stoppedAxes = 0;
        using (planner.Lock())
        {
            foreach ((byte board, List<StoppedDriver> stopped) in byBoard)
            {
                if (TrySendRevert(board, stopped, whenTriggered, ref stoppedAxes))
                {
                    anySent = true;
                }
            }

            // Recorded for every axis the stop reached, and narrowed to the axes the move was armed
            // for where it is read. That is RepRapFirmware's division of the work as well: a coupled
            // geometry stops every drive on the one switch, so the drives that stopped say which
            // axes moved rather than which endstop fired, and only the move knows which of them it
            // was homing
            planner.State.RecordEndstopTriggered(stoppedAxes);
        }

        if (anySent)
        {
            using (_lock.EnterScope())
            {
                _lastRevertSentAt = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Tell one board where its stopped drivers should have ended up
    /// </summary>
    /// <param name="board">CAN address</param>
    /// <param name="stopped">Its stopped drivers</param>
    /// <param name="whenTriggered">Master step-clock time the endstop reported</param>
    /// <param name="stoppedAxes">Bitmap the axes these drivers move are added to</param>
    /// <returns>True if a message was sent</returns>
    /// <remarks>The caller must hold the planner lock</remarks>
    private bool TrySendRevert(byte board, List<StoppedDriver> stopped, uint whenTriggered, ref uint stoppedAxes)
    {
        CanMessageRevertPosition revert = new()
        {
            ClocksAllowed = (uint)(BasicRevertMillis * StepClockRate / 1000.0f)
        };

        int numReverting = 0;
        foreach (StoppedDriver driver in stopped)
        {
            int drive = planner.Parameters.DriveForDriver(new DuetAPI.Utility.DriverId(board, driver.Driver));
            if (drive < 0)
            {
                continue;                       // a driver this side does not know about
            }

            int axis = planner.Parameters.DriveToAxis(drive);
            if (axis >= 0 && axis < 32)
            {
                stoppedAxes |= 1u << axis;
            }

            if (!nativeLink.GetPositionAt(drive, whenTriggered, out int position,
                                          out int positionAtMoveStart, out bool usedTimestamp))
            {
                continue;
            }

            if (!usedTimestamp)
            {
                // Either the board sent no timestamp or the step-clock fit is not yet trusted. The
                // engine has fallen back to where the drives are now, which leaves the overshoot the
                // timestamp exists to remove - a small error rather than a wild one
                logger.LogDebug("Reverting drive {Drive} without a trigger timestamp", drive);
            }

            if (numReverting >= IntArray8.Length)
            {
                logger.LogWarning("Board {Board} stopped more drivers than one revert message can carry", board);
                break;
            }

            // A revert says what the move should have amounted to, so it is steps since the move
            // began rather than an absolute position
            revert.FinalStepCounts[numReverting] = position - positionAtMoveStart;
            revert.WhichDrives |= (ushort)(1u << driver.Driver);
            numReverting++;

            // The engine's own idea of where the drive is has to match what the board is being told,
            // because the next move is planned as a delta from it
            planner.Builder.SetDriveEndpoint(drive, position);
        }

        if (numReverting == 0)
        {
            return false;
        }

        // No request id and no reply: the board acts on it or it does not, and there is nothing this
        // side could usefully do about a failure it only heard about milliseconds later. RRF sends it
        // the same way, with SetupRequestMessageNoRid.
        //
        // The reply type has to be NoReply rather than any other "nothing" value. The controller
        // reads anything else as a reply being expected, and a request that expects a reply must
        // carry an all-ones request id placeholder for it to allocate over - which this message has
        // no field for. It drops the message rather than sending it, so the boards are never told to
        // wind back and the machine silently keeps the overshoot
        revert.ClearReservedFields();
        ReadOnlySpan<byte> payload = MemoryMarshal.AsBytes(
            new ReadOnlySpan<CanMessageRevertPosition>(in revert))[..(int)CanMessageRevertPosition.GetActualDataLength((uint)numReverting)];

        nativeLink.QueueCanMessage(0, (ushort)CanMessageType.RevertPosition, (ushort)CanMessageType.NoReply,
                                   board, isResponse: false, payload);
        return true;
    }
}
