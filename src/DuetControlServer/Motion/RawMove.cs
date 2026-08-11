using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Where the move starts from, in the same coordinates as <see cref="Coords"/>
    /// </summary>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>ms.initialCoords</c>, and the distinction it draws is the reason this
    /// exists separately from <see cref="MoveBuilder.StartCoordinates"/>. Both say where the last
    /// move left the machine, but in different coordinates: this one has the tool transform applied
    /// and <em>not</em> the bed transform, while the builder's has both, because the builder's job is
    /// to difference one commanded position against the next and the bed correction is part of what
    /// was commanded.
    /// </para>
    /// <para>
    /// This is the one the G-code layer measures against - it is what the segments interpolate from,
    /// what the segment count is measured over, and what <c>LimitPosition</c> gets as its starting
    /// point - because <see cref="Coords"/> is uncompensated at that stage and the two ends of a line
    /// have to be in the same space. Interpolating from a compensated start to an uncompensated
    /// target and then compensating each segment applies the previous move's correction on top of
    /// this move's, decaying across it
    /// </para>
    /// </remarks>
    public float[] InitialCoords { get; } = new float[MotionLimits.MaxAxes];

    /// <summary>Requested speed in mm/sec, meaningless when <see cref="InverseTimeMode"/> is set</summary>
    public float FeedRateMmPerSec { get; set; } = 50.0f;

    /// <summary>
    /// How long the whole move should take, in seconds; only used when <see cref="InverseTimeMode"/>
    /// is set
    /// </summary>
    /// <remarks>
    /// A separate field rather than another meaning for <see cref="FeedRateMmPerSec"/>. G93's F is
    /// one over a time, so it describes this move and cannot carry over to the next one, and the
    /// speed it works out to is not known until the move's length is
    /// </remarks>
    public float DurationSec { get; set; }

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
    /// Axes this move armed an endstop for, i.e. the ones whose own endstop it watches
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not the same as the drives that carry a stop input. An axis on coupled kinematics arms every
    /// drive with its one endstop, because they all have to stop together, but only that axis ends up
    /// at a known position.
    /// </para>
    /// <para>
    /// What being armed means depends on <see cref="MoveType"/>, which is why this is not called
    /// "homing axes": H1 homes them, H3 measures how long they turned out to be, and H4 is probing.
    /// RepRapFirmware keeps the three in separate sets for the same reason
    /// </para>
    /// </remarks>
    public List<int> ArmedAxes { get; } = [];

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

    /// <summary>
    /// Whether the M220 speed factor and the M221 extrusion factors apply to this move
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>applyM220M221</c>. The overrides are the operator adjusting a print, so
    /// they apply to ordinary moves that name an axis and to nothing else: not to raw motor or
    /// homing moves, and not to the moves inside a macro the firmware asked for, which are the
    /// machine's own and not part of what is being printed
    /// </remarks>
    public bool ApplyM220M221 { get; set; }

    /// <summary>Whether pressure advance applies to forward extrusion in this move</summary>
    public bool UsePressureAdvance { get; set; }

    /// <summary>Whether the user mentioned any linear axis, even if it rounds to no movement</summary>
    public bool LinearAxesMentioned { get; set; }

    /// <summary>Whether the user mentioned any rotational axis</summary>
    public bool RotationalAxesMentioned { get; set; }

    /// <summary>
    /// true if the move includes positive extrusion
    /// </summary>
    public bool HasPositiveExtrusion { get; set; }

    /// <summary>Whether to use the reduced acceleration limits, e.g. while probing</summary>
    public bool ReduceAcceleration { get; set; }

    /// <summary>
    /// What kind of move this is, as chosen by the H parameter
    /// </summary>
    public MoveType MoveType { get; set; }

    /// <summary>Axes the current tool maps X onto, as a bitmap</summary>
    public uint XAxes { get; set; } = 1;

    /// <summary>Axes the current tool maps Y onto, as a bitmap</summary>
    public uint YAxes { get; set; } = 2;

    /// <summary>Logical drives this move is allowed to touch, as a bitmap</summary>
    /// <remarks>
    /// TODO never set: every drive is owned, which is right with one motion system and wrong with two.
    /// The builder already honours it, so this waits on M596 - §15.2
    /// </remarks>
    public uint OwnedDrives { get; set; } = uint.MaxValue;

    /// <summary>
    /// How many pieces this move has to be broken into
    /// </summary>
    /// <remarks>
    /// One for a move that can be executed as it stands, which is every move on a Cartesian machine
    /// with no height map. More where the geometry bows a straight line, where the height map has to
    /// be followed rather than applied at the ends, or where the move is long enough for the step
    /// clock to wrap during it
    /// </remarks>
    public int SegmentCount { get; set; } = 1;

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
        MoveType = MoveType.Normal;
    }
}
