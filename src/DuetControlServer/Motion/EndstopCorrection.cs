using System;
using System.Collections.Generic;
using DuetControlServer.Link.Native;
using DuetControlServer.Motion.Native;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DuetControlServer.Utility;
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
[DiagnosticsPriority(-4)]
internal sealed class EndstopCorrection(
    NativeLink nativeLink,
    MovePlanner planner,
    ILogger<EndstopCorrection> logger) : IDiagnostics
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
    /// Which drivers of each drive have stopped so far, one bit per driver of the axis
    /// </summary>
    /// <remarks>
    /// An axis with a switch per driver stops its motors one at a time, which is the whole point of
    /// that arrangement: a gantry squares itself by letting each motor run on to its own switch. The
    /// drive's tracker is what tells the motors yet to stop where they were when their own switch
    /// fired, so freezing it on the first trigger would revert the second motor to the first one's
    /// position and undo the squaring. The position is therefore adopted only once the last driver of
    /// the drive has stopped, while the revert for each driver still goes out as it is reported
    /// </remarks>
    private readonly uint[] _driversStopped = new uint[MotionLimits.MaxAxesPlusExtruders];

    /// <summary>Corrected positions, by logical drive, for the drives adopted by one report</summary>
    private readonly int[] _adoptedPositions = new int[MotionLimits.MaxAxesPlusExtruders];

    /// <summary>
    /// What the last stop worked out, for M122
    /// </summary>
    /// <remarks>
    /// The counters below say the mechanism ran; they cannot say whether the number it produced is
    /// any good. This is the number: how far into the move the switch was found, against how far the
    /// move was going to go. A wind-back that is a small fraction of the move is the trigger being
    /// located where it happened; one that is nearly all of it is the trigger being located at the
    /// end of the phase the drive was in, which is what an unusable timestamp looks like from here
    /// </remarks>
    private volatile string? _lastCorrection;

    // What this has been asked to do and what came of it, for M122. Four components have to agree
    // before an endstop move ends where it should - the board that sees the switch, the controller
    // that stops the drivers, the engine that says where they were, and this - and when the machine
    // ends up in the wrong place none of them says which one did not play its part. These are read
    // as a chain, each zero explaining the next: a stop that was never reported never reached here,
    // a driver that maps to no drive is a driver numbering disagreement, and a stop that arrives
    // after its move was concluded is an ordering fault rather than a lost one
    private long _reportsReceived;
    private long _driversReported;
    private long _driversUnmapped;
    private long _driversUnarmed;
    private long _stopsAfterConclusion;
    private long _positionQueriesFailed;
    private long _revertsSent;
    private long _positionsAdopted;
    private long _positionsRefused;
    private long _correctionsWithoutTimestamp;

    /// <summary>
    /// Drives the move in flight armed an endstop for
    /// </summary>
    /// <remarks>
    /// A stop can only name a driver because the move told the controller to watch it, so a report
    /// that maps to any other drive means the two sides disagree about what the move said - and
    /// acting on it corrects a drive that was not moving while leaving the one that was. This is
    /// what makes that a refusal rather than a wrong answer
    /// </remarks>
    private uint _armedDrives;

    /// <summary>
    /// How many moves have armed an endstop since startup
    /// </summary>
    /// <remarks>
    /// Quoted by everything that reports on one, so that two lines describing "the last" of something
    /// can be told apart from two lines describing the same thing. A stop reported for move 6 beside
    /// a move 7 that concluded nothing is not a contradiction, and without a number to compare it
    /// reads exactly like one. Read through <see cref="ConcludeMove"/> and through what
    /// <see cref="Apply"/> captures once per report, rather than exposed on its own: a number taken
    /// again later can name a different move from the one being described
    /// </remarks>
    private long _currentMove;

    /// <summary>
    /// Start a move that watches endstops, forgetting what the last one stopped
    /// </summary>
    /// <param name="armedDrives">Drives this move has armed an endstop for, as a bitmap</param>
    /// <remarks>
    /// Called where the move is armed rather than where it finishes: what this describes is "the
    /// move in flight", and an endstop move is isolated, so there is exactly one of them
    /// </remarks>
    public void ArmMove(uint armedDrives)
    {
        using (_lock.EnterScope())
        {
            Array.Clear(_driversStopped);
            _armedDrives = armedDrives;
            _currentMove++;
            _stopsForCurrentMove = 0;
            _currentMoveConcluded = false;
        }
    }

    /// <summary>
    /// Whether a stop has been reported for the move in flight
    /// </summary>
    /// <remarks>
    /// What the move waits on before deciding it stopped nothing. The rings draining says the engine
    /// has no motion left, which is a weaker statement than it looks: the controller stops the drives
    /// and reports it afterwards, over a link the engine knows nothing about, so the report is in
    /// flight while the move already looks finished from here
    /// </remarks>
    public bool StopReportedForCurrentMove
    {
        get
        {
            using (_lock.EnterScope())
            {
                return _stopsForCurrentMove > 0;
            }
        }
    }
    private int _stopsForCurrentMove;

    /// <summary>
    /// Say that the move in flight has been concluded, so a later stop belongs to nothing
    /// </summary>
    /// <returns>The move that was concluded</returns>
    /// <remarks>
    /// A stop arriving after this is one the move it belongs to has already decided without. It
    /// cannot be acted on - the axis has been given the coordinate of its switch and the next move
    /// planned from it - so it is refused and counted, which is the only way that ordering fault is
    /// visible at all
    /// </remarks>
    public long ConcludeMove()
    {
        using (_lock.EnterScope())
        {
            _currentMoveConcluded = true;
            return _currentMove;
        }
    }
    private bool _currentMoveConcluded;

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
    /// Both this side's position and the engine's are then set to match, so the next move is planned
    /// and scheduled as a delta from where the machine really stopped. The move's planned endpoints
    /// are simply not authoritative for a move that stopped short, and neither side may be left
    /// holding them: this side would command the wrong distance and the engine would schedule the
    /// wrong number of steps.
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
        Interlocked.Increment(ref _reportsReceived);
        Interlocked.Add(ref _driversReported, drivers.Length);
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
        uint adoptedDrives = 0;
        using (planner.Lock())
        {
            // Whether this is still wanted is decided under the planner lock, because that is the
            // lock a move takes to conclude itself. Deciding outside it and acting inside would let a
            // conclusion land in between: the check would pass, the conclusion would run while this
            // waited for the lock, and the correction would be applied to a move that had already
            // decided without it
            long move;
            bool tooLate;
            using (_lock.EnterScope())
            {
                _stopsForCurrentMove++;
                move = _currentMove;
                tooLate = _currentMoveConcluded;
            }

            if (tooLate)
            {
                // Acting now would revert drives the machine has since been told it is somewhere
                // else on, and overwrite a position the next move has already been planned from
                Interlocked.Increment(ref _stopsAfterConclusion);
                logger.LogError("An endstop stop for move #{Move} arrived after the move had been concluded; ignoring it", move);
                return;
            }

            foreach ((byte board, List<StoppedDriver> stopped) in byBoard)
            {
                if (TrySendRevert(board, stopped, move, whenTriggered, ref stoppedAxes, ref adoptedDrives))
                {
                    anySent = true;
                }
            }

            if (adoptedDrives != 0)
            {
                // The engine is told where the drives really stopped, which is what makes the move
                // end: it runs a move until the drives it moves have no motion left, and forcing the
                // position discards the rest of the profile. RepRapFirmware reaches the same place
                // from the step interrupt, where stopping the drive empties its segment list and the
                // move finishes as soon as the last one has gone
                if (nativeLink.SetMotorPositions(adoptedDrives, _adoptedPositions))
                {
                    Interlocked.Increment(ref _positionsAdopted);
                }
                else
                {
                    Interlocked.Increment(ref _positionsRefused);
                    logger.LogError("The motion engine would not take the position an endstop stopped the move at");
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
    /// Record that one driver of a drive has stopped, and say whether it was the last of them
    /// </summary>
    /// <param name="drive">The logical drive it moves</param>
    /// <param name="driverIndex">Which of that drive's drivers it is, or -1 if it is not known</param>
    /// <returns>True if every driver of the drive has now stopped</returns>
    /// <remarks>
    /// A drive with one driver answers true on the first stop, which is every drive on an ordinary
    /// machine. See <see cref="_driversStopped"/> for what the others are waiting for
    /// </remarks>
    private bool NoteDriverStopped(int drive, int driverIndex)
    {
        int numDrivers = drive >= 0 && drive < planner.Parameters.DriversPerDrive.Length
                         ? planner.Parameters.DriversPerDrive[drive]
                         : 0;
        if (numDrivers <= 1 || driverIndex < 0 || driverIndex >= MotionLimits.MaxDriversPerAxis)
        {
            return true;                        // nothing else can be waited for
        }

        using (_lock.EnterScope())
        {
            _driversStopped[drive] |= 1u << driverIndex;
            uint all = (1u << Math.Min(numDrivers, MotionLimits.MaxDriversPerAxis)) - 1;
            return (_driversStopped[drive] & all) == all;
        }
    }

    /// <summary>
    /// Tell one board where its stopped drivers should have ended up
    /// </summary>
    /// <param name="board">CAN address</param>
    /// <param name="stopped">Its stopped drivers</param>
    /// <param name="move">
    /// The move this report was attributed to, taken once by the caller so that everything said
    /// about one report names the same move
    /// </param>
    /// <param name="whenTriggered">Master step-clock time the endstop reported</param>
    /// <param name="stoppedAxes">Bitmap the axes these drivers move are added to</param>
    /// <param name="adoptedDrives">
    /// Bitmap the drives whose last driver has now stopped are added to, with their corrected
    /// positions written to <see cref="_adoptedPositions"/>
    /// </param>
    /// <returns>True if a message was sent</returns>
    /// <remarks>The caller must hold the planner lock</remarks>
    private bool TrySendRevert(byte board, List<StoppedDriver> stopped, long move, uint whenTriggered,
                               ref uint stoppedAxes, ref uint adoptedDrives)
    {
        CanMessageRevertPosition revert = new()
        {
            ClocksAllowed = (uint)(BasicRevertMillis * StepClockRate / 1000.0f)
        };

        int numReverting = 0;
        foreach (StoppedDriver driver in stopped)
        {
            DuetAPI.Utility.DriverId driverId = new(board, driver.Driver);
            int drive = planner.Parameters.DriveForDriver(driverId);
            if (drive < 0)
            {
                // A driver this side does not know about. Said out loud rather than skipped
                // silently: the controller only watches an input on a driver because a move this
                // side named it, so the two disagreeing about how drivers are numbered means the
                // move stopped and nothing will put the position right
                Interlocked.Increment(ref _driversUnmapped);
                logger.LogWarning("An endstop stopped driver {Board}.{Driver}, which belongs to no configured drive",
                                  board, driver.Driver);
                continue;
            }

            // Only a drive this move armed may be corrected. The controller watches an input on a
            // driver because this move told it to, so a report naming anything else means the two
            // sides disagree about which drive that driver belongs to - and correcting the drive the
            // lookup answered with would wind a motor that was not moving to a position it was never
            // at, while the axis that really stopped keeps the endpoint it never reached
            bool armed;
            using (_lock.EnterScope())
            {
                armed = drive < MotionLimits.MaxAxesPlusExtruders && (_armedDrives & (1u << drive)) != 0;
            }
            if (!armed)
            {
                Interlocked.Increment(ref _driversUnarmed);
                logger.LogWarning(
                    "An endstop stopped driver {Board}.{Driver}, which belongs to drive {Drive} - a drive this move armed no endstop for",
                    board, driver.Driver, drive);
                continue;
            }

            int axis = planner.Parameters.DriveToAxis(drive);
            if (axis >= 0 && axis < MotionLimits.MaxAxes)
            {
                stoppedAxes |= 1u << axis;
            }

            if (!nativeLink.GetPositionAt(drive, whenTriggered, out int position,
                                          out int positionAtMoveStart, out bool usedTimestamp))
            {
                Interlocked.Increment(ref _positionQueriesFailed);
                logger.LogWarning("The motion engine could not say where drive {Drive} was when the endstop fired", drive);
                continue;
            }

            if (!usedTimestamp)
            {
                Interlocked.Increment(ref _correctionsWithoutTimestamp);
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
            int stepsTaken = position - positionAtMoveStart;
            revert.FinalStepCounts[numReverting] = stepsTaken;
            revert.WhichDrives |= (ushort)(1u << driver.Driver);
            numReverting++;

            // Recorded against what the move was going to do, because the ratio is what says whether
            // the trigger was located at all. The planned endpoint is read before it is overwritten
            // below, which is the last moment it still describes this move; both ends of the
            // subtraction are reported, because a difference of zero has two causes and they need
            // telling apart
            int plannedEndpoint = drive < planner.Builder.EndPoints.Length
                                  ? planner.Builder.EndPoints[drive]
                                  : 0;
            _lastCorrection = $"move #{move}, drive {drive} stopped {stepsTaken} steps into a move of "
                              + $"{plannedEndpoint - positionAtMoveStart} "
                              + $"(from {positionAtMoveStart} towards {plannedEndpoint}, stopped at {position})";

            if (!NoteDriverStopped(drive, planner.Parameters.DriverIndexForDriver(driverId)))
            {
                // This drive has motors still running on switches of their own. Its tracker is what
                // will tell them where they were when those fired, so it has to keep running
                continue;
            }

            // The engine's own idea of where the drive is has to match what the board is being told,
            // because the next move is planned as a delta from it
            planner.Builder.SetDriveEndpoint(drive, position);
            if (drive < MotionLimits.MaxAxesPlusExtruders)
            {
                _adoptedPositions[drive] = position;
                adoptedDrives |= 1u << drive;
            }
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
        Interlocked.Increment(ref _revertsSent);
        return true;
    }

    /// <summary>
    /// Report what the endstops have stopped and what became of it (M122)
    /// </summary>
    /// <param name="builder">String builder to print to</param>
    /// <remarks>
    /// <para>
    /// Four components have to agree before a homing move ends where it should, and when the machine
    /// ends up somewhere else none of them says which one did not play its part. This is the chain,
    /// in the order it runs, so that one homing move and one <c>M122</c> say where it broke: a stop
    /// that was never reported never left the controller, drivers belonging to no drive are the two
    /// sides disagreeing about driver numbering, and positions taken by this side but not by the
    /// engine are the engine's queue backed up.
    /// </para>
    /// <para>
    /// Reported whether or not anything has happened. "None reported" is the answer to a question
    /// worth asking, and a line that only appears once it is too late to read is not diagnostics
    /// </para>
    /// </remarks>
    public void PrintDiagnostics(StringBuilder builder)
    {
        uint? applied = nativeLink.GetForcedPositionsApplied();
        string appliedText = applied is not null
                             ? $"{applied} applied by the engine"
                             : "the engine is too old to say how many it applied";

        long reports = Interlocked.Read(ref _reportsReceived);
        if (reports == 0)
        {
            builder.AppendLine($"Endstop stops: none reported since startup, {appliedText}");
            return;
        }

        builder.AppendLine(
            $"Endstop stops: {reports} reported, {Interlocked.Read(ref _driversReported)} drivers "
            + $"({Interlocked.Read(ref _driversUnmapped)} unmapped, "
            + $"{Interlocked.Read(ref _driversUnarmed)} unarmed, "
            + $"{Interlocked.Read(ref _stopsAfterConclusion)} too late, "
            + $"{Interlocked.Read(ref _positionQueriesFailed)} unlocatable), "
            + $"{Interlocked.Read(ref _revertsSent)} reverts sent, "
            + $"{Interlocked.Read(ref _positionsAdopted)} positions adopted "
            + $"({Interlocked.Read(ref _positionsRefused)} refused, "
            + $"{Interlocked.Read(ref _correctionsWithoutTimestamp)} without a trigger timestamp), "
            + appliedText);

        if (_lastCorrection is string lastCorrection)
        {
            builder.AppendLine($"Last endstop stop: {lastCorrection}");
        }

        // What the move made of it, which is a separate question from whether the stop was handled:
        // the correction puts the drives where they really are, and the move decides what that means
        // for the axis. Printed here because they are read together
        if (planner.State.LastSpecialMove is string lastSpecialMove)
        {
            builder.AppendLine($"Last special move: {lastSpecialMove}");
        }
    }
}
