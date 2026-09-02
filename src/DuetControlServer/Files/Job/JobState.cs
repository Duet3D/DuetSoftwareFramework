using System.Collections.Immutable;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;

namespace DuetControlServer.Files.Job;

/// <summary>
/// Where a job is in its life
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware keeps this in two places, <c>pauseState</c> and the machine state its
/// <c>GCodes::Spin</c> is in, because <c>Spin</c> cannot block. Everything here can await, so there
/// is one phase and every change of it is a transition of one state machine performed by one task.
/// </para>
/// <para>
/// Everything the rest of DuetControlServer asks about a job is a function of this: whether a job is
/// processing, whether one is in the way, what <c>state.status</c> reads. No combination that a
/// transition did not write is ever observable, which is what removes the windows in which a job
/// half exists
/// </para>
/// </remarks>
internal enum JobPhase
{
    /// <summary>No file is selected</summary>
    Idle,

    /// <summary>A file has been selected by M23, M32 or M37 but not started</summary>
    Selected,

    /// <summary><c>start.g</c> is running and the readers have not been told to read</summary>
    Starting,

    /// <summary>The job is reading and executing codes</summary>
    Running,

    /// <summary>The job is coming to a stop and <c>pause.g</c> may still be running</summary>
    Pausing,

    /// <summary>The job is paused and can be resumed or cancelled</summary>
    Paused,

    /// <summary><c>resume.g</c> and the restore moves are running</summary>
    Resuming,

    /// <summary>The job was cancelled while paused and <c>cancel.g</c> is running</summary>
    Cancelling,

    /// <summary>The run is over: <c>stop.g</c> if it is owed, then the teardown</summary>
    Finishing,

    /// <summary>The sequence in flight is being unwound before the run is finished</summary>
    Aborting
}

/// <summary>
/// The file a job is running, and what M37 said about how to run it
/// </summary>
/// <param name="File">The file being read from</param>
/// <param name="Info">What the file info parser made of it</param>
/// <param name="IsSimulating">Whether this is a simulation rather than a print (M37)</param>
/// <param name="UpdateSimulatedTime">Whether a completed simulation writes its time back (M37 F)</param>
internal sealed record JobFile(CodeFile File, GCodeFileInfo Info, bool IsSimulating, bool UpdateSimulatedTime);

/// <summary>
/// A file selected from the file channel while a run was still going, and what to do with it once
/// that run is torn down
/// </summary>
/// <param name="File">The file</param>
/// <param name="Start">
/// True for M32, which chains straight into the file; false for M23, which only selects it and
/// leaves the start to M24, as RepRapFirmware's <c>fileToPrint</c> does
/// </param>
internal sealed record NextSelection(JobFile File, bool Start);

/// <summary>
/// One stream of the job, and what the last pause worked out about it
/// </summary>
/// <param name="Index">Motion system this stream belongs to</param>
/// <param name="Channel">Channel its codes go to</param>
/// <param name="Reader">The reader that owns its file</param>
/// <param name="RewindPoint">Where a resume carries this stream on from, if a pause found a point</param>
/// <param name="AbandonedMacros">
/// Whether the pause abandoned macros this stream was inside, which is RepRapFirmware's
/// <c>pausedInMacro</c> and what the resume sets <c>firstCommandAfterRestart</c> from
/// </param>
/// <param name="Finished">Whether this stream has read its file to the end</param>
/// <remarks>
/// The rewind point is per stream because each stream reads its own copy of the file and stops
/// somewhere of its own: the engine stops one ring, so the first stream rewinds to the move that
/// survived and a forked one to the end of its last completed code
/// </remarks>
internal sealed record JobStream(int Index, CodeChannel Channel, JobReader Reader,
                                 JobResumePoint? RewindPoint, bool AbandonedMacros, bool Finished);

/// <summary>
/// Everything there is to know about the job, as one value
/// </summary>
/// <remarks>
/// Published into a volatile field as the last act of each transition, so a reader takes a snapshot
/// with one field read, cannot land inside a half-finished transition and cannot block anything.
/// The sequence in flight and its cancellation source are deliberately not here: nothing outside the
/// controller loop uses them, and a disposed source has no business in a record every reader keeps
/// </remarks>
internal sealed record JobState
{
    /// <summary>Where the job is in its life</summary>
    public JobPhase Phase { get; init; } = JobPhase.Idle;

    /// <summary>The file being run, or null when none is selected</summary>
    public JobFile? File { get; init; }

    /// <summary>The streams reading it, one per motion system</summary>
    public ImmutableArray<JobStream> Streams { get; init; } = [];

    /// <summary>Why the run that is ending ended, written once by the transition that ended it</summary>
    public PrintStoppedReason? StopReason { get; init; }

    /// <summary>A pause asked for while the job was inside a macro it cannot be interrupted in</summary>
    public PauseRequest? PendingPause { get; init; }

    /// <summary>A file selected from inside the job, taken up once this run is torn down</summary>
    public NextSelection? NextFile { get; init; }

    /// <summary>
    /// Whether a job is live
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's <c>IsPrinting</c> less the phases in which the file has already been closed:
    /// the machine is still working, but not on the job
    /// </remarks>
    public bool IsProcessing
        => Phase is JobPhase.Starting or JobPhase.Running or JobPhase.Pausing or JobPhase.Resuming;

    /// <summary>
    /// Whether a job is running and not pausing, paused or resuming
    /// </summary>
    /// <remarks>RepRapFirmware's <c>IsReallyPrinting()</c></remarks>
    public bool IsReallyPrinting => Phase is JobPhase.Starting or JobPhase.Running;

    /// <summary>Whether the job is paused and can be resumed or cancelled</summary>
    public bool IsPaused => Phase == JobPhase.Paused;

    /// <summary>
    /// Whether a job is in the way of selecting another
    /// </summary>
    /// <remarks>
    /// Everything but <see cref="JobPhase.Idle"/> and <see cref="JobPhase.Selected"/>: a run that
    /// has begun is replaced only by stopping it, which is what keeps the reason it ended from ever
    /// having to be guessed
    /// </remarks>
    public bool IsJobInProgress => Phase is not (JobPhase.Idle or JobPhase.Selected);

    /// <summary>Whether a file is selected, whether or not it has been started</summary>
    public bool IsFileSelected => File is not null;

    /// <summary>Whether the file being run is being simulated rather than printed</summary>
    public bool IsSimulating => File?.IsSimulating == true;

    /// <summary>Length of the file being run, or zero when none is selected</summary>
    public long FileLength => File?.File.Length ?? 0;

    /// <summary>
    /// The stream of the given motion system, or null if there is none
    /// </summary>
    /// <param name="index">Motion system</param>
    /// <returns>The stream</returns>
    public JobStream? Stream(int index)
    {
        foreach (JobStream stream in Streams)
        {
            if (stream.Index == index)
            {
                return stream;
            }
        }
        return null;
    }

    /// <summary>
    /// What <c>state.status</c> reads while the job is in this phase, or null when the job says
    /// nothing about it
    /// </summary>
    /// <remarks>
    /// RepRapFirmware's mapping. <see cref="JobPhase.Finishing"/> and <see cref="JobPhase.Aborting"/>
    /// read as <see cref="MachineStatus.Busy"/> because <c>StopPrint</c> has already reset
    /// <c>pauseState</c> and the print monitor by the time <c>stop.g</c> runs, and
    /// <see cref="JobPhase.Idle"/> and <see cref="JobPhase.Selected"/> leave the answer to whether
    /// anything is moving
    /// </remarks>
    public MachineStatus? Status => Phase switch
    {
        JobPhase.Starting or JobPhase.Running => IsSimulating ? MachineStatus.Simulating : MachineStatus.Processing,
        JobPhase.Pausing => MachineStatus.Pausing,
        JobPhase.Paused => MachineStatus.Paused,
        JobPhase.Resuming => MachineStatus.Resuming,
        JobPhase.Cancelling => MachineStatus.Cancelling,
        JobPhase.Finishing or JobPhase.Aborting => MachineStatus.Busy,
        _ => null
    };
}
