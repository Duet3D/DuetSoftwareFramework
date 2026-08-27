using System;
using DuetControlServer.Motion.Native;

namespace DuetControlServer.Motion;

/// <summary>
/// Where the machine was, so that it can be put back there
/// </summary>
/// <remarks>
/// <para>
/// Ported from RepRapFirmware's <c>RestorePoint</c>. A pause saves one and a resume reads it back:
/// the coordinates say where to move the head to, the feed rate and tool say what to restore, and the
/// file position says where to start reading again.
/// </para>
/// <para>
/// Four of the fields are not published. <see cref="FilePosition"/>, <see cref="ProportionDone"/>,
/// <see cref="InitialUserC0"/> and <see cref="InitialUserC1"/> describe how to resume the job rather
/// than where the machine is, and RepRapFirmware does not put them in its object model either -
/// <c>state.restorePoints[]</c> carries the other seven
/// </para>
/// </remarks>
internal sealed class RestorePoint
{
    /// <summary>
    /// The number of restore points a client can see, as <c>NumVisibleRestorePoints</c>
    /// </summary>
    /// <remarks>
    /// G60 may write any of these. Two of them have a fixed meaning - see
    /// <see cref="PauseNumber"/> and <see cref="ToolChangeNumber"/> - which is RepRapFirmware's
    /// arrangement and is why G60 with no S parameter writes number 0
    /// </remarks>
    public const int NumVisible = 6;

    /// <summary>
    /// Total number of restore points, including the two a client cannot see
    /// </summary>
    public const int NumTotal = NumVisible + 2;

    /// <summary>
    /// The restore point a pause writes and a resume reads
    /// </summary>
    public const int PauseNumber = 1;

    /// <summary>
    /// The restore point a tool change writes
    /// </summary>
    public const int ToolChangeNumber = 2;

    /// <summary>
    /// The restore point a simulation starts from
    /// </summary>
    public const int SimulationNumber = NumVisible;

    /// <summary>
    /// The restore point used when printing resumes after skipped objects
    /// </summary>
    public const int ResumeObjectNumber = NumVisible + 1;

    /// <summary>
    /// User coordinates when the point was saved
    /// </summary>
    public float[] Coords { get; } = new float[MotionLimits.MaxAxes];

    /// <summary>
    /// Feed rate of the move being executed, in mm/s
    /// </summary>
    public float FeedRate { get; set; }

    /// <summary>
    /// Virtual extruder position at the start of the move
    /// </summary>
    public float VirtualExtruderPosition { get; set; }

    /// <summary>
    /// How much of a multi-segment move had been done, 0 unless a move was interrupted part-way
    /// </summary>
    public float ProportionDone { get; set; }

    /// <summary>
    /// Position in the job file the move was read from, or null if it did not come from one
    /// </summary>
    public long? FilePosition { get; set; }

    /// <summary>
    /// Which of G0/G1/G2/G3 produced the move, or -1 if not known
    /// </summary>
    /// <remarks>
    /// A job file may leave the command letter out and rely on the last one given, so resuming into
    /// the middle of such a run needs the modal command restored along with the file position
    /// </remarks>
    public int GCommandNumber { get; set; } = -1;

    /// <summary>
    /// The distance modes the interrupted line was read with, or null when the stop had no line to
    /// name
    /// </summary>
    /// <remarks>
    /// Restored with the modal G command and the feed rate, and needed for the same reason: the job
    /// reads ahead of the machine, so a G90, G91, M82 or M83 further down the file may already have
    /// run by the time the stop lands, and the rewind does not undo it. Null rather than a default,
    /// because "the stop named no line" and "the line was read in absolute mode" are different
    /// things and only the second may be put back
    /// </remarks>
    public bool? AxesRelative { get; set; }

    /// <inheritdoc cref="AxesRelative"/>
    public bool? DrivesRelative { get; set; }

    /// <summary>
    /// X user coordinate at the start of an interrupted arc move
    /// </summary>
    public float InitialUserC0 { get; set; }

    /// <summary>
    /// Y user coordinate at the start of an interrupted arc move
    /// </summary>
    public float InitialUserC1 { get; set; }

    /// <summary>
    /// Tool that was active, or -1 if none
    /// </summary>
    public int ToolNumber { get; set; } = -1;

    /// <summary>
    /// Last speed set by an M106 addressing the current tool's fans, 0..1
    /// </summary>
    public float FanSpeed { get; set; }

    /// <summary>
    /// Forget the point
    /// </summary>
    public void Reset()
    {
        Array.Clear(Coords);
        FeedRate = 0.0f;
        VirtualExtruderPosition = 0.0f;
        ProportionDone = 0.0f;
        FilePosition = null;
        GCommandNumber = -1;
        InitialUserC0 = InitialUserC1 = 0.0f;
        ToolNumber = -1;
        FanSpeed = 0.0f;
    }
}
