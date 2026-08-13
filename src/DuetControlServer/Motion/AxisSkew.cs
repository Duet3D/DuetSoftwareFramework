using System;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Native;
using static DuetControlServer.Motion.AxisIndices;

namespace DuetControlServer.Motion;

/// <summary>
/// M556 axis skew compensation
/// </summary>
/// <remarks>
/// RepRapFirmware's <c>Move::AxisTransform</c> and <c>Move::InverseAxisTransform</c>. The machine's
/// axes are not quite at right angles to each other, so a move along one drags the head slightly
/// along another; M556 measures that as a deviation over a distance and stores the tangent
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
    /// One pass in axis order, reading the reference coordinates as it goes, because that is what
    /// RepRapFirmware does: with the correction on Y the term reads X, and X may already have been
    /// corrected for its own Z skew by the time Y is reached. The difference is second order but it
    /// is a difference, and the point of a port is that it does not have one
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
    /// Note that this undoes the pair in the opposite order to <see cref="Apply"/> - Y before X,
    /// where the forward transform does X before Y. Same reason the forward one reads its references
    /// live: whichever of the two carries the cross term has to see the other in the state it was in
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
