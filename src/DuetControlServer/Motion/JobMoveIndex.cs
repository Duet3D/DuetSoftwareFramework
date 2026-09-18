using System;
using System.Collections.Generic;
using DuetAPI;

namespace DuetControlServer.Motion;

/// <summary>
/// A job-file movement code the interpreter is part-way through
/// </summary>
/// <remarks>
/// <para>
/// One of these per code, not per queued move: a code that segments produces several moves and they
/// all describe the same line of the file. It is RepRapFirmware's <c>ms.raw</c> together with
/// <c>ms.totalSegments</c> and <c>ms.segmentsLeft</c>, which is the set <c>DoAsynchronousPause</c>
/// reads when it stops part-way through a code (GCodes.cpp:1092).
/// </para>
/// <para>
/// Where to rewind the file to and how much of the code the machine has already made are two halves
/// of one fact, so they are fields of one record and <see cref="PointAt"/> is the only way to read
/// them. A fraction that names no file position cannot be expressed, which is what keeps the rewind
/// and the scaling describing the same line
/// </para>
/// </remarks>
internal sealed class JobMoveOrigin
{
    /// <summary>
    /// Where in the job file the code started
    /// </summary>
    public long? FilePosition { get; init; }

    /// <summary>
    /// Which of G0/G1/G2/G3 produced it, or -1 if not known
    /// </summary>
    public int GCommandNumber { get; init; }

    /// <summary>
    /// Feed rate in effect when the move was built, in mm/s
    /// </summary>
    /// <remarks>
    /// The modal feed rate the code was read with, not the speed the move was planned at:
    /// RepRapFirmware's <c>originalFeedRate</c>, which is what a resume puts back as the channel's F.
    /// The two differ by M220, and restoring the scaled one would fold the speed factor into the
    /// file's own feed rate every time the job was paused
    /// </remarks>
    public float FeedRateMmPerSec { get; init; }

    /// <summary>
    /// Whether axis words meant distances rather than targets when the code was read (G91)
    /// </summary>
    /// <remarks>
    /// Recorded with the feed rate and for the same reason, and needed for the same reason the
    /// modal G command is: the job reads ahead of the machine, so by the time a stop lands the
    /// interpreter may have executed a later G90 or G91 - a job whose last line is G90 does exactly
    /// that - and the rewind puts the file position back without undoing it. The line would then be
    /// re-read in the wrong mode, which turns a relative distance into an absolute target and sends
    /// the machine somewhere the file never asked for. RepRapFirmware needs no equivalent because it
    /// reads one code at a time and never runs past the point it may stop at
    /// </remarks>
    public bool AxesRelative { get; init; }

    /// <summary>
    /// Whether extrusion was relative when the code was read (M83), for the same reason
    /// </summary>
    public bool DrivesRelative { get; init; }

    /// <summary>
    /// How much of the code was already made when this move was built, 0..1
    /// </summary>
    /// <remarks>
    /// Non-zero only for the first move built after a resume that landed part-way through a code:
    /// that build is the remainder of the code rather than the whole of it, so a stop inside it has
    /// to compose its own fraction on top of this one. RepRapFirmware needs no equivalent because it
    /// re-reads the whole code and skips the leading segments, leaving <c>totalSegments</c> always
    /// the whole code's
    /// </remarks>
    public float FractionAtStart { get; init; }

    /// <summary>
    /// How long the code is in the file
    /// </summary>
    /// <remarks>
    /// So that a code every segment of which was queued can name the code after it. What is left of
    /// a code that will be made in full is the next one
    /// </remarks>
    public long CodeLength { get; init; }

    /// <summary>
    /// How many segments the build produced
    /// </summary>
    public int SegmentCount { get; init; }

    /// <summary>
    /// Whether this describes the job code that invoked a macro rather than a move of the job's own
    /// </summary>
    /// <remarks>
    /// A move made by a macro the job invoked - a tool change, an <c>M98</c> - is noted under the
    /// invoking code, because that is the only position in the job file that means anything: the
    /// macro's own offsets are into the macro. A stop that comes to rest on such a move therefore
    /// resumes by running the invocation again from its start, which is RepRapFirmware's
    /// <c>pausedInMacro</c> and <c>macroRestarted</c>. Only the whole invocation can be replayed, so
    /// the segment counting the rest of this record does is not meaningful for one of these
    /// </remarks>
    public bool IsMacroInvocation { get; init; }

    /// <summary>
    /// Where a resume would have to carry on from, having made this many of the segments
    /// </summary>
    /// <param name="segmentsMade">Segments of this build the machine will have made</param>
    /// <returns>The resume point, or null if the code has no file position to rewind to</returns>
    public JobResumePoint? PointAt(int segmentsMade)
    {
        if (FilePosition is not long filePosition)
        {
            return null;
        }

        if (IsMacroInvocation)
        {
            // The invocation runs again whole; nothing of it has been made that the job could skip
            return new JobResumePoint(filePosition, 0.0f, GCommandNumber, FeedRateMmPerSec,
                                      AxesRelative, DrivesRelative);
        }

        // Every segment queued means the whole code will be made, so what is left of it is the code
        // after it. RepRapFirmware reaches the same place with a proportion of one, which skips every
        // segment when the code is read again
        if (SegmentCount > 0 && segmentsMade >= SegmentCount)
        {
            return new JobResumePoint(filePosition + CodeLength, 0.0f, GCommandNumber, FeedRateMmPerSec,
                                      AxesRelative, DrivesRelative);
        }
        return new JobResumePoint(filePosition, ProportionAt(segmentsMade), GCommandNumber, FeedRateMmPerSec,
                                  AxesRelative, DrivesRelative);
    }

    /// <summary>
    /// How much of the whole code has been made, having made this many of the segments
    /// </summary>
    /// <param name="segmentsMade">Segments of this build the machine will have made</param>
    /// <returns>The fraction, 0..1</returns>
    /// <remarks>
    /// Of the whole code, however many times the job has been stopped inside it: the build is only
    /// what was left of the code when it started, so its own share is what is left to give
    /// </remarks>
    private float ProportionAt(int segmentsMade)
    {
        if (SegmentCount <= 0)
        {
            return FractionAtStart;
        }

        float made = Math.Clamp((float)segmentsMade / SegmentCount, 0.0f, 1.0f);
        return FractionAtStart + (1.0f - FractionAtStart) * made;
    }

    /// <summary>
    /// Whether a code is one of the job file's own
    /// </summary>
    /// <param name="code">The code</param>
    /// <returns>True if a stop may record it and a resume may scale it</returns>
    /// <remarks>
    /// <para>
    /// A code read from a macro carries an offset into the <em>macro</em>, and a resume rewinds the
    /// <em>job</em> file, so recording one would send the job to an unrelated position. It must not
    /// spend the fraction either: a macro invoked between the resume and the job's next move runs on
    /// the same channel, and shortening its move would consume what the job is owed.
    /// </para>
    /// <para>
    /// <c>File</c> and not <c>File2</c>, because there is one interpreter state and one pause restore
    /// point and both of them are the first channel's. TODO this widens with M596 and M598, along
    /// with the state it feeds
    /// </para>
    /// </remarks>
    public static bool IsJobFileCode(DuetAPI.Commands.Code code)
        => code.Channel == CodeChannel.File && (code as Commands.Code)?.File is not Files.MacroFile;

    /// <summary>
    /// Whether a code is one a macro the job invoked is running
    /// </summary>
    /// <param name="code">The code</param>
    /// <returns>True if its moves belong to the job code that started the macro</returns>
    /// <remarks>
    /// The other half of <see cref="IsJobFileCode"/>: such a move is the job's, but the position to
    /// rewind to is the invocation's rather than the code's own. What supplies that position is
    /// <see cref="Files.MacroFile.InvokingJobCode"/>
    /// </remarks>
    public static bool IsMacroCodeOfJob(DuetAPI.Commands.Code code)
        => code.Channel == CodeChannel.File && (code as Commands.Code)?.File is Files.MacroFile;
}

/// <summary>
/// Where in the job file the code that invoked a macro is
/// </summary>
/// <param name="FilePosition">Where in the job file the invoking code starts</param>
/// <param name="CodeLength">How long it is, so the code after it can be named</param>
/// <remarks>
/// The whole of what a macro's moves need to say about the job file. The modal state is not part of
/// it: the invocation is replayed in full, so the macro sets whatever it set the first time, and
/// the invoking line names its own command rather than repeating a modal one
/// </remarks>
internal readonly record struct JobMacroInvocation(long FilePosition, long CodeLength)
{
    /// <summary>
    /// The invocation a code describes, if it is one of the job file's own
    /// </summary>
    /// <param name="code">Code that started the macro</param>
    /// <returns>The invocation, or null if the code did not come from the job file</returns>
    public static JobMacroInvocation? From(DuetAPI.Commands.Code code)
        => JobMoveOrigin.IsJobFileCode(code) && code.FilePosition is long filePosition
           ? new JobMacroInvocation(filePosition, code.Length ?? 0)
           : null;
}

/// <summary>
/// Where a resume has to carry on from
/// </summary>
/// <param name="FilePosition">Where in the job file to read from again</param>
/// <param name="ProportionDone">How much of the code at that position is already made, 0..1</param>
/// <param name="GCommandNumber">The modal G command that code was read under, or -1</param>
/// <param name="FeedRateMmPerSec">The feed rate it was read with, unscaled by M220</param>
/// <param name="AxesRelative">Whether axis words were distances when it was read (G91)</param>
/// <param name="DrivesRelative">Whether extrusion was relative when it was read (M83)</param>
/// <remarks>
/// The whole of what a stop tells the job file, in one value. It is produced by
/// <see cref="MovePlanner.JobRewindPointFor"/> and read by the rewind, the restore point and the
/// modal state the resume puts back
/// </remarks>
internal readonly record struct JobResumePoint(long FilePosition, float ProportionDone, int GCommandNumber,
                                               float FeedRateMmPerSec, bool AxesRelative, bool DrivesRelative);

/// <summary>
/// Remembers which job code each queued move came from, so a stop can say where to resume
/// </summary>
/// <remarks>
/// <para>
/// The motion engine knows a move by its id and nothing else about it: <c>MoveParams</c> carries no
/// file position, and it should not - the engine has no idea what a file is. So a stop reports the id
/// of the first move it dropped and this is what turns that back into somewhere to resume from.
/// <c>EndstopCorrection.NoteMoveId</c> is the same arrangement for the same reason.
/// </para>
/// <para>
/// Bounded on purpose. A job queues moves far faster than they run, so an index that only forgot what
/// completed would still grow without limit whenever the ring is full and the engine is behind. The
/// oldest entry is dropped once the index is full, and <see cref="Capacity"/> is what makes that safe
/// rather than merely tidy.
/// </para>
/// <para>
/// Not thread-safe: the planner lock covers it, as it covers everything else the planner queues
/// </para>
/// </remarks>
internal sealed class JobMoveIndex
{
    /// <summary>
    /// How many moves are remembered
    /// </summary>
    /// <remarks>
    /// <para>
    /// A stop can only name a move that is still queued, and what bounds how many those are is the
    /// engine's ring. So this has to be at least
    /// <see cref="Native.MotionLimits.MaxDdasPerRing"/> - the largest ring M595 can ask for - or a
    /// machine configured with a big queue would evict an entry that was still needed, and the stop
    /// would fall back to resuming from the last completed code. That is *before* where the machine
    /// actually came to rest, so the job would re-run moves it had already made.
    /// </para>
    /// <para>
    /// The margin above it covers the moves that have been submitted but not yet taken into the
    /// ring: <c>CanAddMove</c> keeps that small, but it is not zero, and it is not worth being
    /// exact about when the entries cost a few dozen bytes each. Every segment of a segmented move
    /// is its own move to the engine and so has its own entry, which is why the count is of moves
    /// rather than of codes
    /// </para>
    /// </remarks>
    private const int Capacity = Native.MotionLimits.MaxDdasPerRing * 2;

    /// <summary>
    /// One queued move: the code it came from, and its own place in that code
    /// </summary>
    private readonly record struct Entry(JobMoveOrigin Origin, int Segment);

    private readonly Dictionary<uint, Entry> _moves = new(Capacity);
    private readonly Queue<uint> _order = new(Capacity);

    /// <summary>
    /// Record where a move came from
    /// </summary>
    /// <param name="moveId">Id the move was queued under</param>
    /// <param name="origin">The code it came from</param>
    /// <param name="segment">Which of that code's segments it is, counted from zero</param>
    public void Note(uint moveId, JobMoveOrigin origin, int segment)
    {
        if (moveId == 0)
        {
            return;             // zero means "no id" on the wire, so there is nothing to key on
        }

        Entry entry = new(origin, segment);
        if (_moves.TryAdd(moveId, entry))
        {
            _order.Enqueue(moveId);
        }
        else
        {
            _moves[moveId] = entry;
        }

        while (_order.Count > Capacity && _order.TryDequeue(out uint oldest))
        {
            _moves.Remove(oldest);
        }
    }

    /// <summary>
    /// Look up where a move came from
    /// </summary>
    /// <param name="moveId">Id the move was queued under</param>
    /// <param name="origin">Receives the code it came from</param>
    /// <param name="segment">Receives which of that code's segments it is</param>
    /// <returns>True if the move is still remembered</returns>
    public bool TryGet(uint moveId, out JobMoveOrigin origin, out int segment)
    {
        if (_moves.TryGetValue(moveId, out Entry entry))
        {
            origin = entry.Origin;
            segment = entry.Segment;
            return true;
        }

        origin = null!;
        segment = 0;
        return false;
    }

    /// <summary>
    /// Forget everything, for when the moves it describes are gone
    /// </summary>
    /// <remarks>
    /// Called when a job is selected and when the link is invalidated, the two events after which a
    /// move id from the previous run means nothing. Not by a pause: the entry a pause needs is the
    /// one describing the move the engine says survives, and that move has usually completed by the
    /// time this side reads the feedhold result, so clearing on a pause would discard exactly what
    /// the next lookup wants. What bounds the index instead is <see cref="Capacity"/>, which is
    /// twice a ring, so a lookup of a surviving move id always hits
    /// </remarks>
    public void Clear()
    {
        _moves.Clear();
        _order.Clear();
    }
}
