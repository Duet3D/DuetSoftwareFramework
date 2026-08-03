using System;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// A move as the G-code layer describes it, before anything has been planned
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>RawMove</c>. This is the input to <see cref="MoveBuilder"/>: where
/// the machine should end up and how fast the user asked to get there, with none of the consequences
/// worked out yet.
/// </para>
/// <para>
/// <see cref="Coords"/> is indexed by logical drive and means different things at each end of it. For
/// an axis it is an absolute position in mm; for an extruder it is an amount of filament to move,
/// because extrusion is relative and accumulates. The planner keeps that distinction all the way
/// down
/// </para>
/// </remarks>
internal sealed class RawMove
{
    /// <summary>
    /// Target axis positions in mm, and extruder movements in mm, by logical drive
    /// </summary>
    public float[] Coords { get; } = new float[MotionLimits.MaxAxesPlusExtruders];

    /// <summary>Requested speed in mm/sec</summary>
    public float FeedRateMmPerSec { get; set; } = 50.0f;

    /// <summary>
    /// Whether the axes move together as one coordinated motion (G1) or independently (G0)
    /// </summary>
    public bool IsCoordinated { get; set; } = true;

    /// <summary>Whether the feed rate is a time for the whole move rather than a speed (G93)</summary>
    public bool InverseTimeMode { get; set; }

    /// <summary>Whether the print may be paused after this move</summary>
    public bool CanPauseAfter { get; set; } = true;

    /// <summary>Whether the move watches endstops or a Z probe, so it may stop short</summary>
    public bool CheckEndstops { get; set; }

    /// <summary>
    /// Which switches stop each drive during this move, by logical drive
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only meaningful when <see cref="CheckEndstops"/> is set; every entry watches nothing
    /// otherwise. An entry names one switch for the whole drive, or one per driver when the axis has
    /// as many switches as drivers - see <see cref="Native.MoveStopInput"/>.
    /// </para>
    /// <para>
    /// It is per drive rather than per move so that one move can home several axes at once, each
    /// stopping on its own endstop. The entries are passed down unchanged to the controller, which is
    /// what watches for the input change: an endstop that had to reach here before the axis stopped
    /// would already have been overrun
    /// </para>
    /// </remarks>
    public Native.MoveStopInput[] StopOnInput { get; } = CreateStopInputs();

    /// <summary>
    /// A stop input array with nothing being watched
    /// </summary>
    /// <returns>The array</returns>
    private static Native.MoveStopInput[] CreateStopInputs()
    {
        Native.MoveStopInput[] inputs = new Native.MoveStopInput[MotionLimits.MaxAxesPlusExtruders];
        for (int i = 0; i < inputs.Length; i++)
        {
            inputs[i] = new Native.MoveStopInput();
        }
        return inputs;
    }

    /// <summary>Whether the move runs at the standard feed rate, so a later change may apply to it</summary>
    public bool UsingStandardFeedrate { get; set; } = true;

    /// <summary>Whether pressure advance applies to forward extrusion in this move</summary>
    public bool UsePressureAdvance { get; set; }

    /// <summary>Whether the user mentioned any linear axis, even if it rounds to no movement</summary>
    public bool LinearAxesMentioned { get; set; }

    /// <summary>Whether the user mentioned any rotational axis</summary>
    public bool RotationalAxesMentioned { get; set; }

    /// <summary>Whether to use the reduced acceleration limits, e.g. while probing</summary>
    public bool ReduceAcceleration { get; set; }

    /// <summary>
    /// 0 for an ordinary move; non-zero for a raw motor move or one that bypasses the kinematics
    /// </summary>
    public int MoveType { get; set; }

    /// <summary>Axes the current tool maps X onto, as a bitmap</summary>
    public uint XAxes { get; set; } = 1;

    /// <summary>Axes the current tool maps Y onto, as a bitmap</summary>
    public uint YAxes { get; set; } = 2;

    /// <summary>Logical drives this move is allowed to touch, as a bitmap</summary>
    public uint OwnedDrives { get; set; } = uint.MaxValue;

    /// <summary>Which ring to queue this move on</summary>
    public byte RingNumber { get; set; }

    /// <summary>This side's correlation id for the move. Never zero</summary>
    public uint MoveId { get; set; }

    /// <summary>
    /// Reset everything that does not carry over between moves
    /// </summary>
    /// <remarks>
    /// The coordinates deliberately survive, because an axis the user did not mention keeps the
    /// position it had - that is what makes G1 X10 a move in X alone rather than a move to
    /// (10, 0, 0)
    /// </remarks>
    public void ClearFlags()
    {
        LinearAxesMentioned = RotationalAxesMentioned = false;
        CheckEndstops = false;
        UsePressureAdvance = false;
        ReduceAcceleration = false;
        MoveType = 0;
    }
}
