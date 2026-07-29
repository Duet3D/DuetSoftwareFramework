using System;
using System.Runtime.InteropServices;

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
}

/// <summary>
/// Builds the byte layout of a move submission: the header followed by its two arrays
/// </summary>
/// <remarks>
/// The two arrays are <c>int endPoint[NumDrives]</c> then <c>float directionVector[NumDrives]</c>.
/// <c>NumDrives</c> is the configured number of logical drives rather than the number that actually
/// move, because the native lookahead and preparation index densely by logical drive
/// </remarks>
internal static class MoveParams
{
    /// <summary>
    /// Total size of a submission carrying the given number of drives
    /// </summary>
    /// <param name="numDrives">Number of logical drives</param>
    /// <returns>Size in bytes</returns>
    public static int Length(int numDrives) => Marshal.SizeOf<MoveParamsHeader>() + (numDrives * (sizeof(int) + sizeof(float)));

    /// <summary>
    /// Write a move submission into <paramref name="destination"/>
    /// </summary>
    /// <param name="destination">Buffer of at least <see cref="Length"/> bytes</param>
    /// <param name="header">Fixed part of the submission; its NumDrives must match the arrays</param>
    /// <param name="endPoints">Machine position each drive ends at, in microsteps</param>
    /// <param name="directionVector">Normalised direction, first three entries Cartesian</param>
    /// <returns>Number of bytes written</returns>
    /// <exception cref="ArgumentException">The buffer is too small, or the arrays disagree with the header</exception>
    public static int Write(Span<byte> destination, MoveParamsHeader header, ReadOnlySpan<int> endPoints, ReadOnlySpan<float> directionVector)
    {
        int numDrives = header.NumDrives;
        if (endPoints.Length != numDrives || directionVector.Length != numDrives)
        {
            throw new ArgumentException($"Expected {numDrives} entries in each array, got {endPoints.Length} and {directionVector.Length}");
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
        return total;
    }
}
