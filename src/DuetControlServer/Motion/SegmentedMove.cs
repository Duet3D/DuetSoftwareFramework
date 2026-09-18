using System;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// What a move is broken into, and what each piece has to be worked out from
/// </summary>
/// <remarks>
/// The move's own coordinates are overwritten segment by segment as it is submitted, so where it
/// started and where it is going have to be kept somewhere else
/// </remarks>
internal readonly struct SegmentedMove
{
    /// <summary>How many pieces the move is in</summary>
    public int Count { get; private init; }

    /// <summary>Number of axes the move touches</summary>
    public int NumAxes { get; private init; }

    /// <summary>Where the move began, in machine coordinates</summary>
    public float[] Start { get; private init; }

    /// <summary>Where it ends, in machine coordinates</summary>
    public float[] Target { get; private init; }

    /// <summary>Extrusion for one segment, by logical drive</summary>
    public float[] ExtrusionPerSegment { get; private init; }

    /// <summary>First logical drive that is an extruder</summary>
    public int FirstExtruderDrive { get; private init; }

    /// <summary>
    /// Take a built move apart into what its segments need
    /// </summary>
    /// <param name="raw">The move</param>
    /// <param name="start">Where the machine is, which is where the move begins</param>
    /// <param name="numAxes">Number of axes to consider</param>
    /// <param name="firstExtruderDrive">First logical drive that is an extruder</param>
    /// <returns>The pieces</returns>
    public static SegmentedMove From(RawMove raw, ReadOnlySpan<float> start, int numAxes, int firstExtruderDrive)
    {
        SegmentedMove segmented = new()
        {
            Count = Math.Max(1, raw.SegmentCount),
            NumAxes = numAxes,
            FirstExtruderDrive = firstExtruderDrive,
            Start = new float[MotionLimits.MaxAxes],
            Target = new float[MotionLimits.MaxAxes],
            ExtrusionPerSegment = new float[MotionLimits.MaxAxesPlusExtruders]
        };

        start[..numAxes].CopyTo(segmented.Start);
        raw.Coords.AsSpan(0, numAxes).CopyTo(segmented.Target);

        // Divided rather than repeated: the extrusion belongs to the whole move, so each segment
        // gets its share. RepRapFirmware does the same in FinaliseMove
        for (int drive = firstExtruderDrive; drive < MotionLimits.MaxAxesPlusExtruders; drive++)
        {
            segmented.ExtrusionPerSegment[drive] = raw.Coords[drive] / segmented.Count;
        }
        return segmented;
    }
}
