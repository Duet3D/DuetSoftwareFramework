using System;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// The vector arithmetic the move planner is built on, over the logical drive space
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>DDA</c> static helpers. A move is a vector in a space with one
/// dimension per logical drive: the axes and the extruders together. Planning a move is mostly
/// arithmetic on that vector - normalise it so its length is the distance the user asked to travel,
/// then find how fast and how hard it may be pushed before some individual drive exceeds its own
/// limit.
/// </para>
/// <para>
/// These take spans rather than fixed arrays so the caller's buffer can be stack- or pool-allocated;
/// the move path runs per G-code and should not be allocating.
/// </para>
/// </remarks>
internal static class MoveVector
{
    /// <summary>
    /// Length of a vector measured over every drive
    /// </summary>
    /// <param name="v">The vector</param>
    /// <returns>Its magnitude</returns>
    public static float Magnitude(ReadOnlySpan<float> v)
    {
        float magnitudeSquared = 0.0f;
        foreach (float component in v)
        {
            magnitudeSquared += component * component;
        }
        return MathF.Sqrt(magnitudeSquared);
    }

    /// <summary>
    /// Length of a vector measured over the given orthogonal axes only
    /// </summary>
    /// <param name="v">The vector</param>
    /// <param name="axes">Bitmap of the axes to measure over</param>
    /// <returns>Its magnitude over those axes</returns>
    public static float Magnitude(ReadOnlySpan<float> v, uint axes)
    {
        float magnitudeSquared = 0.0f;
        for (int axis = 0; axis < v.Length; axis++)
        {
            if ((axes & (1u << axis)) != 0)
            {
                magnitudeSquared += v[axis] * v[axis];
            }
        }
        return MathF.Sqrt(magnitudeSquared);
    }

    /// <summary>
    /// Multiply every component of a vector by a scalar
    /// </summary>
    /// <param name="v">Vector to scale in place</param>
    /// <param name="scale">Factor to apply</param>
    public static void Scale(Span<float> v, float scale)
    {
        for (int i = 0; i < v.Length; i++)
        {
            v[i] *= scale;
        }
    }

    /// <summary>
    /// Move a vector into the positive hyperquadrant
    /// </summary>
    /// <param name="v">Vector to take the absolute value of, in place</param>
    public static void Absolute(Span<float> v)
    {
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = MathF.Abs(v[i]);
        }
    }

    /// <summary>
    /// Scale a vector to unit length over the given axes, returning what its length was
    /// </summary>
    /// <param name="v">Vector to normalise in place</param>
    /// <param name="unitLengthAxes">Axes the resulting unit length is measured over</param>
    /// <returns>The magnitude before normalising, or 0 if the vector was empty</returns>
    /// <remarks>
    /// The whole vector is scaled, but only the named axes decide by how much. That is what makes the
    /// feed rate mean what the user expects on a move that also extrudes: the extruder component
    /// comes along for the ride rather than lengthening the vector and slowing the move down
    /// </remarks>
    public static float Normalise(Span<float> v, uint unitLengthAxes)
    {
        float magnitude = Magnitude(v, unitLengthAxes);
        if (magnitude <= 0.0f)
        {
            return 0.0f;
        }
        Scale(v, 1.0f / magnitude);
        return magnitude;
    }

    /// <summary>
    /// Scale a vector to unit length over every drive, returning what its length was
    /// </summary>
    /// <param name="v">Vector to normalise in place</param>
    /// <returns>The magnitude before normalising, or 0 if the vector was empty</returns>
    public static float Normalise(Span<float> v)
    {
        float magnitude = Magnitude(v);
        if (magnitude <= 0.0f)
        {
            return 0.0f;
        }
        Scale(v, 1.0f / magnitude);
        return magnitude;
    }

    /// <summary>
    /// Normalise the direction vector so that its length is the distance moved in linear space
    /// </summary>
    /// <param name="directionVector">Direction vector to normalise in place</param>
    /// <param name="linearAxes">Bitmap of the axes that are linear rather than rotational</param>
    /// <param name="xAxes">Bitmap of the axes the current tool maps X onto</param>
    /// <param name="yAxes">Bitmap of the axes the current tool maps Y onto</param>
    /// <returns>The linear distance the move covers, in mm</returns>
    /// <remarks>
    /// NIST standard section 2.1.2.5 rule A: if any linear axis moves then the feed rate applies to
    /// the linear movement. Where a tool maps X or Y onto several axes those axes are averaged rather
    /// than summed - they are meant to move together, so counting each one would make the move look
    /// longer than it is and run it proportionately slow
    /// </remarks>
    public static float NormaliseLinearMotion(Span<float> directionVector, uint linearAxes, uint xAxes, uint yAxes)
    {
        float xMagSquared = 0.0f, yMagSquared = 0.0f, magSquared = 0.0f;
        int numXaxes = 0, numYaxes = 0;

        for (int axis = 0; axis < directionVector.Length; axis++)
        {
            if ((linearAxes & (1u << axis)) == 0)
            {
                continue;
            }

            float squared = directionVector[axis] * directionVector[axis];
            if ((xAxes & (1u << axis)) != 0)
            {
                xMagSquared += squared;
                numXaxes++;
            }
            else if ((yAxes & (1u << axis)) != 0)
            {
                yMagSquared += squared;
                numYaxes++;
            }
            else
            {
                magSquared += squared;
            }
        }

        if (numXaxes > 1)
        {
            xMagSquared /= numXaxes;
        }
        if (numYaxes > 1)
        {
            yMagSquared /= numYaxes;
        }

        float magnitude = MathF.Sqrt(xMagSquared + yMagSquared + magSquared);
        if (magnitude <= 0.0f)
        {
            return 0.0f;
        }

        Scale(directionVector, 1.0f / magnitude);
        return magnitude;
    }

    /// <summary>
    /// How far a unit vector may be scaled before any component exceeds its own limit
    /// </summary>
    /// <param name="v">Unit vector in the positive hyperquadrant, i.e. every component non-negative</param>
    /// <param name="box">Per-drive limit, e.g. maximum speed or acceleration</param>
    /// <returns>The largest factor the vector may be scaled by</returns>
    /// <remarks>
    /// This is what turns per-drive limits into one limit for the move. Geometrically it is the
    /// length at which the vector first touches the surface of the hyperbox the limits describe: the
    /// fastest the move may go before some single drive would have to exceed what it can do
    /// </remarks>
    public static float VectorBoxIntersection(ReadOnlySpan<float> v, ReadOnlySpan<float> box)
    {
        // Start from a length that certainly exceeds the box, then bring it in until every drive fits
        float magnitude = 0.0f;
        foreach (float limit in box)
        {
            magnitude += limit;
        }

        int count = Math.Min(v.Length, box.Length);
        for (int d = 0; d < count; d++)
        {
            if (magnitude * v[d] > box[d])
            {
                magnitude = box[d] / v[d];
            }
        }
        return magnitude;
    }

    /// <summary>
    /// A bitmap with the lowest <paramref name="count"/> bits set
    /// </summary>
    /// <param name="count">Number of bits</param>
    /// <returns>The bitmap</returns>
    public static uint LowestBits(int count)
    {
        if (count <= 0)
        {
            return 0;
        }
        return count >= MotionLimits.MaxAxesPlusExtruders ? uint.MaxValue : (1u << count) - 1;
    }
}
