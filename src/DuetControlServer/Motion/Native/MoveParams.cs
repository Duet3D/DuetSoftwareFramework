using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DuetControlServer.Link.Protocol.CanMessages;

namespace DuetControlServer.Motion.Native;

/// <summary>
/// Managed mirror of <c>DuetSbcInterface/src/Motion/MoveParams.h</c>: the move as this side hands it
/// down to the native motion engine
/// </summary>
/// <remarks>
/// <para>
/// This is the split. This side interprets the G-code, runs the kinematics and works out where each
/// drive ends up and how fast the move may go - steps 1 to 6 of RepRapFirmware's
/// <c>DDA::InitStandardMove</c>. It stops there, because step 7 onwards is lookahead, and lookahead
/// needs the whole ring of queued moves, which lives natively.
/// </para>
/// <para>
/// Units are the firmware's internal ones rather than the user's: speed is mm per step clock,
/// acceleration mm per step clock squared, and endpoints are microsteps. The conversion happens once,
/// here, rather than in every consumer on the far side.
/// </para>
/// <para>
/// The layout must stay byte-for-byte identical to the C++ struct; <c>NativeLink</c> checks the size
/// at startup, exactly as it does for the link event records.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 28)]
internal struct MoveParamsHeader
{
    /// <summary>
    /// This side's correlation id, quoted back in <c>MoveCompleted</c> and <c>MoveFailed</c>. Never zero
    /// </summary>
    public uint MoveId;

    /// <summary>Logical drives this move is allowed to touch, as a bitmap</summary>
    public uint OwnedDrives;

    /// <summary>See <see cref="MoveFlags"/></summary>
    public uint Flags;

    /// <summary>Length of the move in hypercuboid space, mm</summary>
    public float TotalDistance;

    /// <summary>
    /// Acceleration and deceleration limit, always positive, mm/clock^2. The native side may lower
    /// this for an acceleration-only or deceleration-only move, so it is a limit and not a promise:
    /// do not use it to predict how long the move will take
    /// </summary>
    public float MaxAcceleration;

    /// <summary>
    /// The speed asked for, mm/clock, already limited here to the axis maxima and to whatever the
    /// kinematics allow
    /// </summary>
    public float RequestedSpeed;

    /// <summary>Which ring to queue this move on: 0 or 1</summary>
    public byte RingNumber;

    /// <summary>Entries in each of the two trailing arrays</summary>
    public byte NumDrives;

    /// <summary>Padding</summary>
    public ushort Padding;
}

/// <summary>
/// Bits of <see cref="MoveParamsHeader.Flags"/>
/// </summary>
/// <remarks>
/// The subset of RepRapFirmware's DDA flags that survives the split: the ones the native side still
/// reads during lookahead, preparation or retirement. The rest are either set natively or are this
/// side's business alone
/// </remarks>
internal static class MoveFlags
{
    /// <summary>The move may be paused after, i.e. it is not part of an indivisible sequence</summary>
    public const uint CanPauseAfter = 1u << 0;

    /// <summary>The move monitors endstops or a Z probe. Always an isolated move as well</summary>
    public const uint CheckEndstops = 1u << 1;

    /// <summary>The move runs at the standard feed rate, so a feed rate change may be applied while queued</summary>
    public const uint UsingStandardFeedrate = 1u << 2;

    /// <summary>Apply pressure advance to forward extrusion in this move</summary>
    public const uint UsePressureAdvance = 1u << 3;

    /// <summary>Both XY movement and extrusion, i.e. the printing jerk limits apply</summary>
    public const uint IsPrintingMove = 1u << 4;

    /// <summary>Movement along an X or Y axis was asked for, even if it rounds to no steps</summary>
    public const uint XyMoving = 1u << 5;

    /// <summary>An extruder-only move, or one involving reverse extrusion</summary>
    public const uint IsNonPrintingExtruderMove = 1u << 6;

    /// <summary>Continuous rotation axes took the short way round</summary>
    public const uint ContinuousRotationShortcut = 1u << 7;

    /// <summary>Do not meld this move with its neighbours, and let it finish before starting the next</summary>
    public const uint IsolatedMove = 1u << 8;

    /// <summary>Some extruder moves forwards during this move</summary>
    public const uint HasForwardExtrusion = 1u << 9;

    /// <summary>
    /// Any watched input stops every driver of this move, not just the drivers watching that input
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>EndstopHitAction::stopAll</c>. Set when moving the axis being homed needs
    /// drives other than its own - a CoreXY axis needs both motors - so stopping only the drivers
    /// that watch the switch would leave the others running and drag the head into it. The axis'
    /// switches are spread over the move's drivers so that all of them are watched, and whichever
    /// fires first stops everything.
    ///
    /// What reaches the controller is <see cref="MoveStopInput.Action"/> of
    /// <see cref="StopAction.All"/> per driver, not this flag: the action belongs to the endstop
    /// that fired rather than to the move. This stays because the native side needs it to spread the
    /// axis' switches over the move's drivers, which never crossed the wire
    /// </remarks>
    public const uint StopAllDrivers = 1u << 10;
}

/// <summary>
/// What a trigger on a drive's input stops
/// </summary>
/// <remarks>
/// The mirror of <c>duet::spi::protocol::StopAction</c> in
/// <c>lib/DuetSpiInterface/include/DuetSpiProtocol/StopRules.h</c>, which is RepRapFirmware's
/// <c>EndstopHitAction</c>. The values are on the wire, so they must not be renumbered on one side
/// </remarks>
internal enum StopAction : byte
{
    /// <summary>This drive watches nothing, so nothing it could match stops anything</summary>
    None = 0,

    /// <summary>
    /// Stop only the motor that triggered, while its drive has others still running
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>stopDriver</c>. The last motor of the drive escalates to
    /// <see cref="Group"/>, which the controller decides because it is what knows how many are left
    /// </remarks>
    Driver = 1,

    /// <summary>Stop every driver of the drive - RepRapFirmware's <c>stopAxis</c></summary>
    Group = 2,

    /// <summary>Stop every driver of the move - RepRapFirmware's <c>stopAll</c></summary>
    All = 3
}

/// <summary>
/// Which switches stop one drive during a move, and how its drivers share them
/// </summary>
/// <remarks>
/// <para>
/// This is RepRapFirmware's <c>SwitchEndstop</c> reduced to what a move needs. That class holds a
/// board number per port and derives the handle from the axis and the port index, so the board is
/// the only part that differs between one switch of an axis and the next; the handle follows from
/// which switch it is. The switches of an axis may be spread over several boards, as they may in the
/// firmware.
/// </para>
/// <para>
/// <see cref="NumSwitches"/> says how the drivers share them, exactly as
/// <c>SwitchEndstop::PrimeAxis</c> decides it: zero means the drive watches nothing, one means every
/// driver of the drive watches <c>Boards[0]</c> so the first trigger stops the axis, and n means
/// driver i watches <c>Boards[i]</c> so each motor runs on to its own switch
/// </para>
/// </remarks>
internal sealed class MoveStopInput
{
    /// <summary>
    /// Serialised size of one entry, which must match the native <c>MoveStopInput</c>
    /// </summary>
    public const int Length = 2 + 1 + MotionLimits.MaxDriversPerAxis + 1 + 1 + 1;

    /// <summary>
    /// Remote input handle the switches are registered under, with a minor field of zero
    /// </summary>
    /// <remarks>Driver i watches minor i, which is why only one handle has to be carried</remarks>
    public RemoteInputHandle Handle { get; set; }

    /// <summary>How many switches the drive watches</summary>
    public byte NumSwitches { get; set; }

    /// <summary>CAN address of each switch, in driver order</summary>
    public byte[] Boards { get; } = new byte[MotionLimits.MaxDriversPerAxis];

    /// <summary>
    /// Drivers already sitting on their own switch when the move was built, one bit per driver
    /// </summary>
    /// <remarks>
    /// Such a driver is given no steps, while the rest of the axis moves. An axis with a switch per
    /// driver is squared by letting each motor run on to its own switch, so a gantry that starts
    /// with one side already down has exactly one side left to move - holding the whole axis because
    /// one switch is closed would make the move that corrects the skew do nothing. RepRapFirmware
    /// does the same from <c>DDA::Prepare</c>, where <c>CheckEndstops(false)</c> zeroes the steps of
    /// the motors concerned before the movement messages go out
    /// </remarks>
    public byte HeldDrivers { get; set; }

    /// <summary>
    /// What a trigger on this drive's input stops
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's three <c>EndstopHitAction</c>s, decided from the endstop type and the
    /// kinematics. Carried per drive rather than per move because it belongs to the endstop that
    /// fired: one move may home an axis whose endstop has to stop every drive alongside one whose
    /// endstop stops only its own
    /// </remarks>
    public StopAction Action { get; set; }

    /// <summary>Stop watching anything, which is what every drive of an ordinary move carries</summary>
    public void Clear()
    {
        Handle = new RemoteInputHandle();
        NumSwitches = 0;
        HeldDrivers = 0;
        Action = StopAction.None;
        Array.Clear(Boards);
    }

    /// <summary>
    /// Watch one switch on behalf of the whole drive, so the first trigger stops every driver
    /// </summary>
    /// <param name="handle">Handle the switch is registered under</param>
    /// <param name="board">CAN address of the board carrying it</param>
    public void SetShared(RemoteInputHandle handle, byte board)
    {
        Clear();
        Handle = handle;
        NumSwitches = 1;
        Boards[0] = board;
    }

    /// <summary>
    /// Watch for a stall, which every driver of the drive does on its own board
    /// </summary>
    /// <param name="handle">The board-wide stall handle</param>
    /// <remarks>
    /// No board is written. A driver can only be stopped by its own stall and the board that reports
    /// it is the one carrying it, so the native side takes the board from the driver it is emitting
    /// and <see cref="Boards"/> selects nothing. <see cref="NumSwitches"/> is one because there is
    /// nothing per-switch to count here - it says the drive watches something, which is what
    /// everything reading it wants to know
    /// </remarks>
    public void SetStall(RemoteInputHandle handle)
    {
        Clear();
        Handle = handle;
        NumSwitches = 1;
    }

    /// <summary>
    /// Give each driver of the drive its own switch, in driver order
    /// </summary>
    /// <param name="handle">Handle the first switch is registered under; driver i uses minor i</param>
    /// <param name="boards">CAN address of each switch</param>
    /// <exception cref="ArgumentException">More switches than an axis can have drivers</exception>
    public void SetPerDriver(RemoteInputHandle handle, ReadOnlySpan<byte> boards)
    {
        if (boards.Length > MotionLimits.MaxDriversPerAxis)
        {
            throw new ArgumentException($"An axis may have at most {MotionLimits.MaxDriversPerAxis} endstop switches, got {boards.Length}");
        }

        Clear();
        Handle = handle;
        NumSwitches = (byte)boards.Length;
        boards.CopyTo(Boards);
    }

    /// <summary>Copy another entry over this one</summary>
    /// <param name="other">The entry to copy</param>
    public void CopyFrom(MoveStopInput other)
    {
        Handle = other.Handle;
        NumSwitches = other.NumSwitches;
        HeldDrivers = other.HeldDrivers;
        Action = other.Action;
        other.Boards.CopyTo(Boards, 0);
    }

    /// <summary>
    /// Give one driver no steps, because it is already on its own switch
    /// </summary>
    /// <param name="driverIndex">Which driver of the drive</param>
    public void HoldDriver(int driverIndex)
    {
        if (driverIndex >= 0 && driverIndex < MotionLimits.MaxDriversPerAxis)
        {
            HeldDrivers |= (byte)(1 << driverIndex);
        }
    }
}

/// <summary>
/// Builds the byte layout of a move submission: the header followed by its three arrays
/// </summary>
/// <remarks>
/// The arrays are <c>int endPoint[NumDrives]</c>, <c>float directionVector[NumDrives]</c> and
/// <c>MoveStopInput stopOnInput[NumDrives]</c>. <c>NumDrives</c> is the configured number of logical drives
/// rather than the number that actually move, because the native lookahead and preparation index
/// densely by logical drive
/// </remarks>
internal static class MoveParams
{
    /// <summary>
    /// Total size of a submission carrying the given number of drives
    /// </summary>
    /// <param name="numDrives">Number of logical drives</param>
    /// <returns>Size in bytes</returns>
    public static int Length(int numDrives)
        => Marshal.SizeOf<MoveParamsHeader>() + (numDrives * (sizeof(int) + sizeof(float) + MoveStopInput.Length));

    /// <summary>
    /// Write a move submission into <paramref name="destination"/>
    /// </summary>
    /// <param name="destination">Buffer of at least <see cref="Length"/> bytes</param>
    /// <param name="header">Fixed part of the submission; its NumDrives must match the arrays</param>
    /// <param name="endPoints">Machine position each drive ends at, in microsteps</param>
    /// <param name="directionVector">Normalised direction, first three entries Cartesian</param>
    /// <param name="stopOnInput">Which switches stop each drive</param>
    /// <returns>Number of bytes written</returns>
    /// <exception cref="ArgumentException">The buffer is too small, or the arrays disagree with the header</exception>
    public static int Write(Span<byte> destination, MoveParamsHeader header, ReadOnlySpan<int> endPoints,
                            ReadOnlySpan<float> directionVector, ReadOnlySpan<MoveStopInput> stopOnInput)
    {
        int numDrives = header.NumDrives;
        if (endPoints.Length != numDrives || directionVector.Length != numDrives || stopOnInput.Length != numDrives)
        {
            throw new ArgumentException($"Expected {numDrives} entries in each array, got {endPoints.Length}, {directionVector.Length} and {stopOnInput.Length}");
        }

        int total = Length(numDrives);
        if (destination.Length < total)
        {
            throw new ArgumentException($"Need {total} bytes for {numDrives} drives, got {destination.Length}");
        }

        int headerSize = Marshal.SizeOf<MoveParamsHeader>();
        MemoryMarshal.Write(destination, in header);
        MemoryMarshal.AsBytes(endPoints).CopyTo(destination[headerSize..]);
        MemoryMarshal.AsBytes(directionVector).CopyTo(destination[(headerSize + (numDrives * sizeof(int)))..]);

        // Field by field rather than as one block: the entries hold an array each, so there is no
        // blittable managed type to copy out of
        Span<byte> stops = destination[(headerSize + (numDrives * (sizeof(int) + sizeof(float))))..];
        for (int drive = 0; drive < numDrives; drive++)
        {
            Span<byte> entry = stops.Slice(drive * MoveStopInput.Length, MoveStopInput.Length);
            MoveStopInput stop = stopOnInput[drive];
            BinaryPrimitives.WriteUInt16LittleEndian(entry, stop.Handle.All);
            entry[2] = stop.NumSwitches;
            stop.Boards.CopyTo(entry[3..]);
            entry[3 + MotionLimits.MaxDriversPerAxis] = stop.HeldDrivers;
            entry[4 + MotionLimits.MaxDriversPerAxis] = (byte)stop.Action;
            entry[5 + MotionLimits.MaxDriversPerAxis] = 0;           // the native record's padding byte
        }
        return total;
    }
}
