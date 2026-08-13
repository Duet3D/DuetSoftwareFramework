using System;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Native;
using static DuetControlServer.Motion.AxisIndices;

namespace DuetControlServer.Motion;

/// <summary>
/// M556 axis skew compensation
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware's <c>Move::AxisTransform</c> and <c>Move::InverseAxisTransform</c>. The machine's
/// axes are not quite at right angles to each other, so a move along one drags the head slightly
/// along another; M556 measures that as a deviation over a distance and stores the tangent.
/// </para>
/// <para>
/// Each direction is two passes over the axes rather than one, and that is a deliberate difference
/// from the firmware. RepRapFirmware walks the axes once and swaps the order of the two branches in
/// the inverse, which only undoes the pair in the opposite order when the Y axis has the lower axis
/// number; on an ordinary machine X comes first, so its correction is taken back off before the Y
/// value it was computed from has been restored. The round trip is then out by <c>tanXY</c> times
/// one of the height corrections, which lands on the commanded position rather than only on what is
/// reported - see <c>MovementState.UpdateCoordinatesFromLastKnownEndpoints</c> on that side. Two
/// passes make both directions read the same values, so the round trip cancels exactly
/// </para>
/// </remarks>
internal static class AxisSkew
{
    /// <summary>
    /// Apply the skew correction to a move's coordinates
    /// </summary>
    /// <param name="tool">The selected tool, whose axis mapping names X and Y, or null if none is</param>
    /// <param name="move">The move model</param>
    /// <param name="coords">Machine coordinates, corrected in place</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// <para>
    /// Correcting the skew means adding back what it will take away, which is a term per pair of
    /// axes.
    /// </para>
    /// <para>
    /// The XY term goes on one axis or the other, never both - <c>M556 P</c> chooses, and correcting
    /// both would double it. Which one is a matter of which axis the machine is squared against.
    /// </para>
    /// <para>
    /// Every X axis first and then every Y axis, reading the reference coordinates as it goes: with
    /// the correction on Y the term reads X, and X has already been corrected for its own Z skew by
    /// the time the Y pass runs. <see cref="Remove"/> undoes the two passes in the opposite order,
    /// which is what makes the round trip exact
    /// </para>
    /// </remarks>
    public static void Apply(Tool? tool, Move move, Span<float> coords, int numAxes)
    {
        Skew skew = move.Compensation.Skew;
        if (skew.TanXY == 0.0f && skew.TanXZ == 0.0f && skew.TanYZ == 0.0f)
        {
            return;                             // the machine is square, or says it is
        }

        uint xAxes = ToolTransform.AxisBitmap(tool, move, 'X');
        uint yAxes = ToolTransform.AxisBitmap(tool, move, 'Y');

        int lowestY = LowestSetAxis(yAxes, numAxes);
        if (lowestY < 0)
        {
            return;                             // no Y axis, so no pair to be out of square
        }
        int lowestX = LowestSetAxis(xAxes, numAxes);

        int zAxis = ZAxisIndex(move);
        float z = zAxis >= 0 && zAxis < numAxes ? coords[zAxis] : 0.0f;

        for (int axis = 0; axis < numAxes && axis < MotionLimits.MaxAxes; axis++)
        {
            if ((xAxes & (1u << axis)) != 0)
            {
                float fromY = skew.CompensateXY && lowestX >= 0 ? skew.TanXY * coords[lowestY] : 0.0f;
                coords[axis] += fromY + (skew.TanXZ * z);
            }
        }

        for (int axis = 0; axis < numAxes && axis < MotionLimits.MaxAxes; axis++)
        {
            if ((yAxes & (1u << axis)) != 0)
            {
                float fromX = !skew.CompensateXY && lowestX >= 0 ? skew.TanXY * coords[lowestX] : 0.0f;
                coords[axis] += fromX + (skew.TanYZ * z);
            }
        }
    }

    /// <summary>
    /// Take the skew correction back off a machine position
    /// </summary>
    /// <param name="tool">The selected tool, whose axis mapping names X and Y, or null if none is</param>
    /// <param name="move">The move model</param>
    /// <param name="coords">Machine coordinates, corrected on the way in and requested on the way out</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// The passes run in the opposite order to <see cref="Apply"/> - every Y axis and then every X
    /// axis, where the forward transform does X and then Y. That is what makes the cross terms
    /// cancel: whichever of the two carries one has to see the other in the state the forward pass
    /// saw it in, so Y is undone while X still carries its own correction, and X is undone once Y is
    /// back to the coordinate the move was commanded in
    /// </remarks>
    public static void Remove(Tool? tool, Move move, Span<float> coords, int numAxes)
    {
        Skew skew = move.Compensation.Skew;
        if (skew.TanXY == 0.0f && skew.TanXZ == 0.0f && skew.TanYZ == 0.0f)
        {
            return;
        }

        uint xAxes = ToolTransform.AxisBitmap(tool, move, 'X');
        uint yAxes = ToolTransform.AxisBitmap(tool, move, 'Y');

        int lowestY = LowestSetAxis(yAxes, numAxes);
        if (lowestY < 0)
        {
            return;
        }
        int lowestX = LowestSetAxis(xAxes, numAxes);

        int zAxis = ZAxisIndex(move);
        float z = zAxis >= 0 && zAxis < numAxes ? coords[zAxis] : 0.0f;

        for (int axis = 0; axis < numAxes && axis < MotionLimits.MaxAxes; axis++)
        {
            if ((yAxes & (1u << axis)) != 0)
            {
                float fromX = !skew.CompensateXY && lowestX >= 0 ? skew.TanXY * coords[lowestX] : 0.0f;
                coords[axis] -= fromX + (skew.TanYZ * z);
            }
        }

        for (int axis = 0; axis < numAxes && axis < MotionLimits.MaxAxes; axis++)
        {
            if ((xAxes & (1u << axis)) != 0)
            {
                float fromY = skew.CompensateXY && lowestX >= 0 ? skew.TanXY * coords[lowestY] : 0.0f;
                coords[axis] -= fromY + (skew.TanXZ * z);
            }
        }
    }

    /// <summary>
    /// The lowest-numbered axis in a bitmap
    /// </summary>
    /// <param name="axes">The bitmap</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <returns>The axis, or -1 if the bitmap names none that exists</returns>
    public static int LowestSetAxis(uint axes, int numAxes)
    {
        for (int axis = 0; axis < numAxes && axis < MotionLimits.MaxAxes; axis++)
        {
            if ((axes & (1u << axis)) != 0)
            {
                return axis;
            }
        }
        return -1;
    }
}
