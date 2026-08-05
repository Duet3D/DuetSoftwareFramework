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
    /// Forget everything, for when the machine position is no longer meaningful
    /// </summary>
    public void Reset() => Array.Clear(CurrentUserPosition);
}
