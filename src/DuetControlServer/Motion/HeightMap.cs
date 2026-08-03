using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DuetControlServer.Motion;

/// <summary>
/// A bed height map: the grid it was measured over, the height measured at each point, and the Z
/// correction that follows from them
/// </summary>
/// <remarks>
/// <para>
/// The file format is RepRapFirmware's, because it is the same file: a machine's <c>heightmap.csv</c>
/// has to survive this migration, and Duet Web Control reads it directly to draw the mesh.
/// </para>
/// <para>
/// A point that was never probed is written as a bare <c>0</c> with no decimal point, which is how
/// RepRapFirmware tells "measured as zero" from "not measured". Loading keeps that distinction so a
/// partial map reloads as the partial map it was
/// </para>
/// </remarks>
internal sealed class HeightMap
{
    /// <summary>
    /// First line of a height map file, which is also its version marker
    /// </summary>
    public const string FileComment = "RepRapFirmware height map file v2";

    /// <summary>
    /// The column names line, as written since RepRapFirmware 3.3-beta2
    /// </summary>
    private const string LabelLine = "axis0,axis1,min0,max0,min1,max1,radius,spacing0,spacing1,num0,num1";

    /// <summary>
    /// The column name lines of older versions, which are still read
    /// </summary>
    /// <remarks>
    /// A map saved by an older firmware is still a valid map. The three differ in whether the axes
    /// are named and whether the two spacings are separate, which is what <see cref="ReadParameters"/>
    /// switches on
    /// </remarks>
    private static readonly string[] LabelLines =
    [
        "xmin,xmax,ymin,ymax,radius,spacing,xnum,ynum",
        "xmin,xmax,ymin,ymax,radius,xspacing,yspacing,xnum,ynum",
        LabelLine
    ];

    /// <summary>Letters of the two axes the grid spans</summary>
    public char[] Axes { get; private set; } = ['X', 'Y'];

    /// <summary>Lowest coordinate of each axis</summary>
    public float[] Mins { get; private set; } = [0.0f, 0.0f];

    /// <summary>Highest coordinate of each axis</summary>
    public float[] Maxs { get; private set; } = [0.0f, 0.0f];

    /// <summary>Point spacing along each axis</summary>
    public float[] Spacings { get; private set; } = [0.0f, 0.0f];

    /// <summary>Number of points along each axis</summary>
    public int[] Nums { get; private set; } = [0, 0];

    /// <summary>Radius the grid is limited to, or negative if it is not circular</summary>
    public float Radius { get; private set; } = -1.0f;

    /// <summary>Height measured at each point, row by row along the first axis</summary>
    private float[] _heights = [];

    /// <summary>Which points were actually probed</summary>
    private bool[] _measured = [];

    /// <summary>
    /// Most points a map may have, as in RepRapFirmware's largest <c>MaxGridProbePoints</c>
    /// </summary>
    /// <remarks>
    /// A bound rather than a rule: it exists so that a corrupt parameter line cannot ask for an
    /// arbitrarily large allocation before anything has looked at the numbers
    /// </remarks>
    private const int MaxPoints = 961;

    /// <summary>Whether the map has any points to interpolate between</summary>
    public bool IsValid => Nums[0] >= 2 && Nums[1] >= 2 && _heights.Length == Nums[0] * Nums[1];

    /// <summary>Whether the grid the map claims to cover is one that could have been probed</summary>
    private bool HasUsableGrid => Nums[0] >= 2 && Nums[1] >= 2 && Nums[0] * Nums[1] <= MaxPoints
                                  && Spacings[0] > 0.0f && Spacings[1] > 0.0f;

    /// <summary>How many points were probed</summary>
    public int MeasuredPoints
    {
        get
        {
            int count = 0;
            foreach (bool measured in _measured)
            {
                if (measured)
                {
                    count++;
                }
            }
            return count;
        }
    }

    /// <summary>
    /// Read a height map
    /// </summary>
    /// <param name="reader">Source of the file</param>
    /// <param name="map">The map that was read</param>
    /// <param name="error">Why it could not be read</param>
    /// <returns>True if a map was read</returns>
    public static bool TryRead(TextReader reader, out HeightMap? map, out string? error)
    {
        map = null;

        string? comment = reader.ReadLine();
        if (comment is null || !comment.StartsWith(FileComment, StringComparison.Ordinal))
        {
            error = "bad header line or wrong version header";
            return false;
        }

        string? labels = reader.ReadLine();
        int version = labels is null ? -1 : Array.IndexOf(LabelLines, labels.Trim());
        if (version < 0)
        {
            error = "bad label line";
            return false;
        }

        string? parameters = reader.ReadLine();
        if (parameters is null)
        {
            error = "failed to read line from file";
            return false;
        }

        HeightMap read = new();
        if (!read.ReadParameters(parameters, version))
        {
            error = "failed to parse grid parameters";
            return false;
        }

        if (!read.HasUsableGrid)
        {
            error = "invalid grid";
            return false;
        }

        read._heights = new float[read.Nums[0] * read.Nums[1]];
        read._measured = new bool[read._heights.Length];
        for (int row = 0; row < read.Nums[1]; row++)
        {
            string? line = reader.ReadLine();
            if (line is null)
            {
                error = "failed to read line from file";
                return false;
            }

            string[] fields = line.Split(',');
            for (int column = 0; column < read.Nums[0]; column++)
            {
                if (column >= fields.Length)
                {
                    error = "not enough values in a row";
                    return false;
                }

                string field = fields[column].Trim();

                // A bare zero is what RepRapFirmware writes where it did not probe, which is why the
                // text is inspected rather than the value: 0.000 is a measurement of zero
                if (field == "0")
                {
                    continue;
                }

                if (!float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float height))
                {
                    error = "failed to parse a height value";
                    return false;
                }

                int index = (row * read.Nums[0]) + column;
                read._heights[index] = height;
                read._measured[index] = true;
            }
        }

        map = read;
        error = null;
        return true;
    }

    /// <summary>
    /// Write this height map
    /// </summary>
    /// <param name="writer">Destination</param>
    /// <param name="generatedAt">When the map was measured, for the header comment</param>
    public void Write(TextWriter writer, DateTime generatedAt)
    {
        (float mean, float deviation, float minError, float maxError) = GetStatistics();
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{FileComment} generated at {generatedAt:yyyy-MM-dd HH:mm}, min error {minError:F3}, "
            + $"max error {maxError:F3}, mean {mean:F3}, deviation {deviation:F3}"));

        writer.WriteLine(LabelLine);
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Axes[0]},{Axes[1]},{Mins[0]:F2},{Maxs[0]:F2},{Mins[1]:F2},{Maxs[1]:F2},"
            + $"{Radius:F2},{Spacings[0]:F2},{Spacings[1]:F2},{Nums[0]},{Nums[1]}"));

        StringBuilder row = new();
        for (int y = 0; y < Nums[1]; y++)
        {
            row.Clear();
            for (int x = 0; x < Nums[0]; x++)
            {
                if (x != 0)
                {
                    row.Append(',');
                }

                int index = (y * Nums[0]) + x;

                // Fixed width so the file can be read by eye, and a bare zero where nothing was
                // probed so that reloading it knows the difference
                row.Append(_measured[index]
                           ? _heights[index].ToString("F3", CultureInfo.InvariantCulture).PadLeft(7)
                           : "      0");
            }
            writer.WriteLine(row.ToString());
        }
    }

    /// <summary>
    /// The Z correction at a point on the bed
    /// </summary>
    /// <param name="axis0">Coordinate along the first axis of the grid</param>
    /// <param name="axis1">Coordinate along the second</param>
    /// <returns>How much higher the bed is there than nominal</returns>
    /// <remarks>
    /// A point outside the grid is clamped to its edge rather than extrapolated, as in
    /// RepRapFirmware: a bed is measured where it can be probed, and guessing beyond that would move
    /// the nozzle on evidence that was never collected
    /// </remarks>
    public float GetInterpolatedHeightError(float axis0, float axis1)
    {
        if (!IsValid)
        {
            return 0.0f;
        }

        float last0 = Mins[0] + ((Nums[0] - 1) * Spacings[0]);
        float last1 = Mins[1] + ((Nums[1] - 1) * Spacings[1]);

        // Just inside the last cell, so the interpolation below always has a cell to work in
        const float epsilon = 0.01f;
        axis0 = Math.Clamp(axis0, Mins[0], last0 - epsilon);
        axis1 = Math.Clamp(axis1, Mins[1], last1 - epsilon);

        float along0 = (axis0 - Mins[0]) / Spacings[0];
        float along1 = (axis1 - Mins[1]) / Spacings[1];
        int index0 = (int)MathF.Floor(along0);
        int index1 = (int)MathF.Floor(along1);
        return Interpolate(index0, index1, along0 - index0, along1 - index1);
    }

    /// <summary>
    /// Bilinear interpolation within one cell of the grid
    /// </summary>
    /// <param name="index0">Cell index along the first axis</param>
    /// <param name="index1">Cell index along the second</param>
    /// <param name="fraction0">How far across the cell along the first axis</param>
    /// <param name="fraction1">How far across it along the second</param>
    /// <returns>The height there</returns>
    private float Interpolate(int index0, int index1, float fraction0, float fraction1)
    {
        int lowLow = (index1 * Nums[0]) + index0;
        int highLow = lowLow + 1;
        int lowHigh = lowLow + Nums[0];
        int highHigh = lowHigh + 1;

        float both = fraction0 * fraction1;
        return (_heights[lowLow] * (1.0f - fraction0 - fraction1 + both))
               + (_heights[highLow] * (fraction0 - both))
               + (_heights[lowHigh] * (fraction1 - both))
               + (_heights[highHigh] * both);
    }

    /// <summary>
    /// Mean, standard deviation and extremes of the measured points
    /// </summary>
    /// <returns>The statistics, all zero if nothing was measured</returns>
    public (float Mean, float Deviation, float MinError, float MaxError) GetStatistics()
    {
        double sum = 0.0, sumOfSquares = 0.0;
        float minError = float.MaxValue, maxError = float.MinValue;
        int count = 0;

        for (int i = 0; i < _heights.Length; i++)
        {
            if (!_measured[i])
            {
                continue;
            }

            float height = _heights[i];
            sum += height;
            sumOfSquares += (double)height * height;
            minError = MathF.Min(minError, height);
            maxError = MathF.Max(maxError, height);
            count++;
        }

        if (count == 0)
        {
            return (0.0f, 0.0f, 0.0f, 0.0f);
        }

        double mean = sum / count;
        double variance = Math.Max(0.0, (sumOfSquares / count) - (mean * mean));
        return ((float)mean, (float)Math.Sqrt(variance), minError, maxError);
    }

    /// <summary>
    /// Read the grid parameters line
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="version">Which label line was found, indexing <see cref="LabelLines"/></param>
    /// <returns>True if the line was understood</returns>
    private bool ReadParameters(string line, int version)
    {
        string[] fields = line.Split(',');
        List<float> numbers = [];
        int first = 0;

        if (version >= 2)
        {
            // The newest form names the axes, which is what lets a grid span something other than XY
            if (fields.Length < 2 || fields[0].Trim().Length != 1 || fields[1].Trim().Length != 1)
            {
                return false;
            }
            Axes = [fields[0].Trim()[0], fields[1].Trim()[0]];
            first = 2;
        }
        else
        {
            Axes = ['X', 'Y'];
        }

        for (int i = first; i < fields.Length; i++)
        {
            if (!float.TryParse(fields[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return false;
            }
            numbers.Add(value);
        }

        // Version 0 wrote one spacing for both axes; the others write one each
        int expected = version == 0 ? 8 : 9;
        if (numbers.Count < expected)
        {
            return false;
        }

        if (version >= 2)
        {
            Mins = [numbers[0], numbers[2]];
            Maxs = [numbers[1], numbers[3]];
        }
        else
        {
            Mins = [numbers[0], numbers[2]];
            Maxs = [numbers[1], numbers[3]];
        }

        Radius = numbers[4];
        if (version == 0)
        {
            Spacings = [numbers[5], numbers[5]];
            Nums = [(int)numbers[6], (int)numbers[7]];
        }
        else
        {
            Spacings = [numbers[5], numbers[6]];
            Nums = [(int)numbers[7], (int)numbers[8]];
        }
        return true;
    }
}
