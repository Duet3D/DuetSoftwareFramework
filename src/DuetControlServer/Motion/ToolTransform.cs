using System;
using DuetAPI.ObjectModel;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// The step between the coordinates a G-code names and the coordinates the machine is driven to
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware's <c>ToolOffsetTransform</c> and <c>ToolOffsetInverseTransform</c>. A user
/// coordinate says where the <em>nozzle</em> should be; a machine coordinate says where the head
/// reference point should be. The difference is the selected tool's offsets, its Z hop while
/// retracted, and babystepping.
/// </para>
/// <para>
/// Static and given the tool rather than reading one, because the transform is a function of the
/// tool and the axis configuration and nothing else. Which tool is selected is the caller's business.
/// </para>
/// <para>
/// X, Y and Z are the axes carrying those letters, wherever M584 put them, rather than the first
/// three axes as in RepRapFirmware. A letter the machine does not have contributes nothing: there is
/// no coordinate to read for it and no axis to write back to
/// </para>
/// </remarks>
internal static class ToolTransform
{
    /// <summary>
    /// Convert user coordinates to head reference point coordinates
    /// </summary>
    /// <param name="tool">The selected tool, or null if none is</param>
    /// <param name="move">The move model</param>
    /// <param name="state">The channel's interpreter state, which the coordinates come from</param>
    /// <param name="coords">Machine coordinates, written for every axis below <paramref name="numAxes"/></param>
    /// <param name="numAxes">Number of axes to write</param>
    /// <param name="explicitAxes">Axes the code named, as a bitmap</param>
    /// <remarks>
    /// <para>
    /// Note what <paramref name="explicitAxes"/> is and is not: it is <em>not</em> a bound on the
    /// loop - every axis is written whether the code named it or not, because a move commands an
    /// absolute position for all of them. It selects the <em>input</em> axis under tool axis mapping,
    /// where an axis the code named reads its own coordinate while an axis that is only in the X map
    /// reads X's.
    /// </para>
    /// <para>
    /// Babystepping is a term of it, and the one term that exists so far. It is what makes the offset
    /// adjustable during a print without the reported coordinates moving: it is added on the way down
    /// and taken back off by <see cref="Remove"/> on the way up, so the operator reads back the
    /// coordinate they asked for
    /// </para>
    /// </remarks>
    public static void Apply(Tool? tool, Move move, MovementState state, Span<float> coords,
                             int numAxes, uint explicitAxes = 0)
    {
        // TODO apply the axis scale factors (M579) here, which is where RepRapFirmware multiplies
        // them in - move.axes[].scale does not exist yet, see §6
        if (tool is null)
        {
            for (int axis = 0; axis < numAxes; axis++)
            {
                coords[axis] = state.CurrentUserPosition[axis] + move.Axes[axis].Babystep;
            }
            return;
        }

        // Get axis index from OM
        int xAxis = AxisIndices.IndexOf(move, 'X');
        int yAxis = AxisIndices.IndexOf(move, 'Y');
        int zAxis = AxisIndices.IndexOf(move, 'Z');

        // Get mapped tool axes
        uint xAxes = AxisMap(tool, move, XMap);
        uint yAxes = AxisMap(tool, move, YMap);
        uint zAxes = AxisMap(tool, move, ZMap);

        for (int axis = 0; axis < numAxes && axis < MotionLimits.MaxAxes; axis++)
        {
            // We don't want to update mapped axes if the axis doesn't also map to itself
            if ((axis == xAxis && (xAxes & (1u << xAxis)) == 0)
                || (axis == yAxis && (yAxes & (1u << yAxis)) == 0)
                || (axis == zAxis && (zAxes & (1u << zAxis)) == 0))
            {
                continue;
            }

            // The offset is where the nozzle is relative to the head reference point, so reaching a
            // coordinate means moving the head the other way by it
            float offset = move.Axes[axis].Babystep - Offset(tool, axis);
            if ((zAxes & (1u << axis)) != 0)
            {
                offset += ActualZHop(tool);
            }

            // Which coordinate this axis takes. An axis the code named reads its own; one that is
            // only in a map reads the mapped letter's, which is what makes a single X move two
            // carriages on an IDEX machine. A map is only non-empty where its letter exists, since
            // M563 maps a missing letter to nothing, but a map recorded some other way is still not
            // read through a letter the machine does not have
            int inputAxis = (explicitAxes & (1u << axis)) != 0 ? axis
                            : (xAxes & (1u << axis)) != 0 && xAxis >= 0 ? xAxis
                            : (yAxes & (1u << axis)) != 0 && yAxis >= 0 ? yAxis
                            : (zAxes & (1u << axis)) != 0 && zAxis >= 0 ? zAxis
                            : axis;
            coords[axis] = state.CurrentUserPosition[inputAxis] + offset;
        }
    }

    /// <summary>
    /// Recover the coordinates that were asked for from the machine position they produced
    /// </summary>
    /// <param name="tool">The selected tool, or null if none is</param>
    /// <param name="move">The move model</param>
    /// <param name="coords">Machine coordinates in, user coordinates out</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <remarks>
    /// <para>
    /// RepRapFirmware's <c>ToolOffsetInverseTransform</c>, and it is deliberately not the exact
    /// inverse of <see cref="Apply"/>. Where a letter drives several axes the forward transform sends
    /// one coordinate to all of them, so there is no single coordinate to come back: RepRapFirmware
    /// reports the <em>mean</em> of the axes in the map, which is right when they agree and is the
    /// only defensible answer when they do not.
    /// </para>
    /// <para>
    /// That is exactly why the interpreter keeps its own position rather than reconstructing it. This
    /// runs only where the machine has ended up somewhere the interpreter did not put it - homing,
    /// probing, G92 - and losing a little there is the price of knowing where the machine is
    /// </para>
    /// </remarks>
    public static void Remove(Tool? tool, Move move, Span<float> coords, int numAxes)
    {
        if (tool is null)
        {
            for (int axis = 0; axis < numAxes; axis++)
            {
                coords[axis] -= move.Axes[axis].Babystep;
            }
            return;
        }

        uint xAxes = AxisMap(tool, move, XMap);
        uint yAxes = AxisMap(tool, move, YMap);
        uint zAxes = AxisMap(tool, move, ZMap);

        float xSum = 0.0f, ySum = 0.0f, zSum = 0.0f;
        int xCount = 0, yCount = 0, zCount = 0;

        for (int axis = 0; axis < numAxes && axis < MotionLimits.MaxAxes; axis++)
        {
            float offset = move.Axes[axis].Babystep - Offset(tool, axis);
            if ((zAxes & (1u << axis)) != 0)
            {
                offset += ActualZHop(tool);
            }

            float coord = coords[axis] - offset;
            coords[axis] = coord;

            if ((xAxes & (1u << axis)) != 0)
            {
                xSum += coord;
                xCount++;
            }
            if ((yAxes & (1u << axis)) != 0)
            {
                ySum += coord;
                yCount++;
            }
            if ((zAxes & (1u << axis)) != 0)
            {
                zSum += coord;
                zCount++;
            }
        }

        // Back into the axis carrying the letter, which is where Apply read the coordinate from
        int xAxis = AxisIndices.IndexOf(move, 'X'), yAxis = AxisIndices.IndexOf(move, 'Y'), zAxis = AxisIndices.IndexOf(move, 'Z');
        if (xCount > 0 && xAxis >= 0 && xAxis < numAxes)
        {
            coords[xAxis] = xSum / xCount;
        }
        if (yCount > 0 && yAxis >= 0 && yAxis < numAxes)
        {
            coords[yAxis] = ySum / yCount;
        }
        if (zCount > 0 && zAxis >= 0 && zAxis < numAxes)
        {
            coords[zAxis] = zSum / zCount;
        }
    }

    /// <summary>
    /// Bitmap of the axes a letter drives
    /// </summary>
    /// <param name="tool">The selected tool, or null if none is</param>
    /// <param name="move">The move model</param>
    /// <param name="letter">Axis letter</param>
    /// <returns>The bitmap</returns>
    /// <remarks>
    /// The selected tool's mapping where there is one, falling back to the axes literally carrying
    /// the letter where there is not. This is what decides whether a move counts as XY movement in
    /// user space, and therefore whether the printing jerk limits apply: on an IDEX machine an X move
    /// drives U, and a move that reached U through the X map is still an XY move
    /// </remarks>
    public static uint AxisBitmap(Tool? tool, Move move, char letter)
    {
        if (tool is not null)
        {
            int which = MapLetters.IndexOf(letter);
            if (which >= 0)
            {
                return AxisMap(tool, move, which);
            }
        }

        uint bitmap = 0;
        for (int axis = 0; axis < move.Axes.Count && axis < MotionLimits.MaxAxes; axis++)
        {
            if (move.Axes[axis].Letter == letter)
            {
                bitmap |= 1u << axis;
            }
        }
        return bitmap;
    }

    /// <summary>
    /// The letters a tool's axis maps are for, in the order <c>tools[].axes</c> stores them
    /// </summary>
    private const string MapLetters = "XYZ";

    /// <summary>Position of each map in <c>tools[].axes</c></summary>
    private const int XMap = 0, YMap = 1, ZMap = 2;

    /// <summary>
    /// One of a tool's axis maps, as a bitmap
    /// </summary>
    /// <param name="tool">The tool</param>
    /// <param name="move">The move model</param>
    /// <param name="which">Which map: 0 for X, 1 for Y, 2 for Z, the order the object model stores them in</param>
    /// <returns>The axes that letter drives</returns>
    /// <remarks>
    /// The object model stores each map as an array of axis numbers rather than a bitmap, so this is
    /// the conversion. A tool with no map recorded falls back to the letter driving the axis that
    /// carries it, which is RepRapFirmware's default, and to nothing on a machine without that letter
    /// </remarks>
    public static uint AxisMap(Tool tool, Move move, int which)
    {
        if (which >= tool.Axes.Count)
        {
            return AxisIndices.Bit(AxisIndices.IndexOf(move, MapLetters[which]));
        }

        uint bitmap = 0;
        foreach (int axis in tool.Axes[which])
        {
            if (axis is >= 0 and < MotionLimits.MaxAxes)
            {
                bitmap |= 1u << axis;
            }
        }
        return bitmap;
    }

    /// <summary>A tool's offset for one axis, or zero if it has none recorded</summary>
    /// <param name="tool">The tool</param>
    /// <param name="axis">The axis</param>
    /// <returns>The offset in mm</returns>
    public static float Offset(Tool tool, int axis)
        => axis >= 0 && axis < tool.Offsets.Count ? tool.Offsets[axis] : 0.0f;

    /// <summary>
    /// How far the tool is currently lifted by firmware retraction
    /// </summary>
    /// <param name="tool">The tool</param>
    /// <returns>The lift in mm</returns>
    /// <remarks>
    /// RepRapFirmware's <c>Tool::GetActualZHop</c>: the Z hop only applies while the tool is actually
    /// retracted, which is what makes it a lift rather than a permanent offset
    /// </remarks>
    public static float ActualZHop(Tool tool) => tool.IsRetracted ? tool.Retraction.ZHop : 0.0f;
}
