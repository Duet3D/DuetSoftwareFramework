using System;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// The interpreter's own idea of where the machine is going
/// </summary>
/// <remarks>
/// <para>
/// Ported from the position half of RepRapFirmware's <c>MovementState</c>. This is the state a
/// G-code is interpreted against, and it runs ahead of the machine: it is where the last move
/// <em>commanded</em> the head to, not where the head is. The two are different by however many moves
/// the engine still has queued, which is the whole point of a look-ahead.
/// </para>
/// <para>
/// The object model publishes both, and its two fields mean different things for exactly this reason:
/// <c>move.axes[].machinePosition</c> is live, taken from the engine, while
/// <c>move.axes[].userPosition</c> is a projection of <see cref="CurrentUserPosition"/> and therefore
/// runs ahead with the interpreter. Neither is the source of truth for the next move - this is.
/// </para>
/// <para>
/// The direction of travel matters. RepRapFirmware keeps <c>currentUserPosition</c> as forward state
/// and derives machine coordinates from it through <c>ToolOffsetTransform</c>; it never reconstructs
/// it by inverting that transform, because the transform is not invertible once an axis is mapped -
/// <c>ToolOffsetInverseTransform</c> has to pick one axis of the map to report. The inverse is used
/// only where the machine position is redefined from outside the interpreter, which is homing,
/// probing and G92
/// </para>
/// </remarks>
internal sealed class MovementState
{
    /// <summary>
    /// Where the last move commanded each axis to go, in user coordinates
    /// </summary>
    /// <remarks>
    /// This is RepRapFirmware's <c>ms.currentUserPosition</c>, and it carries the same convention:
    /// the workplace offset is <em>included</em>, while tool offsets, babystepping, axis scale
    /// factors and Z hop are not. So the value reported to the user is this minus the workplace
    /// offset, and the machine coordinate is this put through the tool transform
    /// </remarks>
    public float[] CurrentUserPosition { get; } = new float[MotionLimits.MaxAxes];

    /// <summary>
    /// Segments of the move being submitted that have not gone out yet
    /// </summary>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>ms.segmentsLeft</c>, and it exists here for the reason RepRapFirmware
    /// tests it before doing anything else with a movement code: <c>if (GetMovementState(gb)
    /// .segmentsLeft != 0) return false;</c>. A move too long for the engine's ring is submitted a
    /// few segments at a time, giving the ring up in between - that is the point of segmenting it,
    /// because a long move must not block the channel that issued it. But the locks go with the ring,
    /// so in that window a second channel can build its own move measured from a position that is
    /// part-way through the first one, and the two end up interleaved.
    /// </para>
    /// <para>
    /// So this is what a second channel waits on. It is deliberately not a lock held across the wait:
    /// the whole reason the wait exists is that holding it would be the thing that blocks
    /// </para>
    /// </remarks>
    public int SegmentsLeft { get; set; }

    /// <summary>
    /// Axes whose endstop stopped the move that is running, as a bitmap
    /// </summary>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>ms.endstopsTriggered</c>. It is a <em>latch</em>, and that is the whole
    /// point of it: what a homing move has to know afterwards is whether the endstop fired, not
    /// whether it is closed now. Those are different questions once the drives have been wound back
    /// to where they were at the instant it fired, because that is the instant the switch had just
    /// closed - so the axis ends up sitting on the switch's threshold and reading it live is a coin
    /// toss. RepRapFirmware records the fact when the stop is <em>reported</em> and never looks at
    /// the endstop again.
    /// </para>
    /// <para>
    /// It also has to be a latch to work at all for a stall or a Z probe used as an endstop. Those
    /// report under handles of their own, so nothing ever writes <c>sensors.endstops[].triggered</c>
    /// for them, and a homing move that consulted that flag would decide a move which worked
    /// perfectly had failed
    /// </para>
    /// </remarks>
    public uint EndstopsTriggered { get; private set; }

    /// <summary>
    /// The saved positions a pause, a tool change, a simulation or G60 can put the machine back to
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>ms.restorePoints</c>, numbered the same way: see
    /// <see cref="RestorePoint.PauseNumber"/> and its siblings. The first
    /// <see cref="RestorePoint.NumVisible"/> are published; the last two are working state
    /// </remarks>
    public RestorePoint[] RestorePoints { get; } = CreateRestorePoints();

    /// <summary>
    /// Last speed an M106 set on the current tool's fans, 0..1
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>ms.virtualFanSpeed</c>. It is not the speed of any particular fan: a tool
    /// may map several, and what has to be saved in a restore point and written to
    /// <c>config-override.g</c> is the one speed the operator asked for
    /// </remarks>
    public float VirtualFanSpeed { get; set; }

    /// <summary>
    /// What the last <c>G1 H</c> move concluded, for M122
    /// </summary>
    /// <remarks>
    /// A special move is where the machine finds out where it is, and it can decline to conclude
    /// anything for several unrelated reasons: the move armed no axis, the endstop never reported,
    /// the axis has no endstop configured. All of them look identical from outside - the position is
    /// simply not what was expected - so the conclusion is recorded rather than inferred
    /// </remarks>
    public string? LastSpecialMove { get; set; }

    /// <summary>
    /// Start a move that watches endstops, forgetting what stopped the last one
    /// </summary>
    public void ArmEndstops() => EndstopsTriggered = 0;

    /// <summary>
    /// Record that the move was stopped by the endstops of these axes
    /// </summary>
    /// <param name="axes">The axes, as a bitmap</param>
    /// <remarks>
    /// RepRapFirmware's <c>RecordEndstopTriggered</c>, called as each stop is reported. A move may be
    /// stopped more than once - a Cartesian homing X, Y and Z together stops each axis as it reaches
    /// its own switch - so this accumulates rather than assigns
    /// </remarks>
    public void RecordEndstopTriggered(uint axes) => EndstopsTriggered |= axes;

    /// <summary>
    /// Save where the machine is to one of the restore points
    /// </summary>
    /// <param name="restorePointNumber">Which point to write</param>
    /// <param name="numAxes">How many axes are visible</param>
    /// <param name="feedRate">Feed rate of the channel that asked, in mm/s</param>
    /// <param name="toolNumber">Tool that is active, or -1 if none</param>
    /// <param name="filePosition">Position in the job file to resume from, if there is one</param>
    /// <remarks>
    /// RepRapFirmware's <c>MovementState::SavePosition</c>. The modal command number is deliberately
    /// left unknown: every caller of this - a synchronous pause, a tool change, G60, the start of a
    /// simulation - is a command that has already replaced the modal motion command, so a value saved
    /// here would be the wrong one. The asynchronous pause path fills it in from the move it stopped
    /// before instead
    /// </remarks>
    public void SavePosition(int restorePointNumber, int numAxes, float feedRate, int toolNumber, long? filePosition)
    {
        RestorePoint rp = RestorePoints[restorePointNumber];
        for (int axis = 0; axis < Math.Min(numAxes, MotionLimits.MaxAxes); axis++)
        {
            rp.Coords[axis] = CurrentUserPosition[axis];
        }

        rp.FeedRate = feedRate;
        rp.FilePosition = filePosition;
        rp.GCommandNumber = -1;
        rp.ToolNumber = toolNumber;
        rp.FanSpeed = VirtualFanSpeed;

        // TODO virtualExtruderPosition needs the extrusion totals RepRapFirmware keeps in
        // ms.latestVirtualExtruderPosition, which ApplyExtrusion does not track yet - see
        // MCODE_MIGRATION.md §15.2. It stays zero until it does
        rp.VirtualExtruderPosition = 0.0f;
        rp.ProportionDone = 0.0f;
        rp.InitialUserC0 = rp.InitialUserC1 = 0.0f;
    }

    /// <summary>
    /// Forget everything, for when the machine position is no longer meaningful
    /// </summary>
    public void Reset()
    {
        Array.Clear(CurrentUserPosition);
        EndstopsTriggered = 0;
        SegmentsLeft = 0;
        VirtualFanSpeed = 0.0f;
        foreach (RestorePoint rp in RestorePoints)
        {
            rp.Reset();
        }
    }

    /// <summary>
    /// Build the restore point array
    /// </summary>
    private static RestorePoint[] CreateRestorePoints()
    {
        RestorePoint[] points = new RestorePoint[RestorePoint.NumTotal];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new RestorePoint();
        }
        return points;
    }
}
