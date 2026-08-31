using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;

namespace DuetControlServer.Files.Job;

/// <summary>
/// Which macro a pause runs, if any
/// </summary>
/// <remarks>
/// RepRapFirmware encodes this in the state it enters - <c>pausing1</c> runs <c>pause.g</c>,
/// <c>pausing2</c> runs nothing, <c>filamentChangePause1</c> prefers <c>filament-change.g</c> - so
/// this is that choice named rather than left implicit in a state number
/// </remarks>
public enum PauseMacro
{
    /// <summary>
    /// Run no macro, as <c>M226 P0</c> and a driver error ask for
    /// </summary>
    None,

    /// <summary>
    /// Run <c>pause.g</c>
    /// </summary>
    Pause,

    /// <summary>
    /// Run <c>filament-change.g</c>, falling back to <c>pause.g</c> if there is none
    /// </summary>
    FilamentChange
}

/// <summary>
/// A request to pause the job
/// </summary>
/// <param name="Channel">Channel the pause was commanded from, which the macro runs on</param>
/// <param name="Reason">Why the job is pausing</param>
/// <param name="Macro">Which macro to run once the machine has stopped</param>
/// <param name="Synchronous">
/// Whether the pause came from a command in the job file itself. Such a pause makes no feedhold:
/// the file has already reached the point it stops at, so everything queued ahead of it must run
/// </param>
/// <param name="ReportPosition">Whether to announce where the job paused</param>
internal sealed record PauseRequest(CodeChannel Channel, PrintPausedReason Reason, PauseMacro Macro,
                                    bool Synchronous, bool ReportPosition);

/// <summary>
/// How far a reader is allowed to get before it stops
/// </summary>
internal enum FreezeAt
{
    /// <summary>
    /// Now: the generation is cancelled, so nothing more is read and the codes read ahead are
    /// dropped before they are dispatched
    /// </summary>
    Now,

    /// <summary>
    /// At the end of the code the reader is on, and of any macro that code is running. The
    /// read-ahead is held at the dispatch barrier instead of being cancelled
    /// </summary>
    AfterCurrentCode
}

/// <summary>
/// Where one stream carries on from, as a pause worked it out
/// </summary>
/// <param name="Stream">Motion system</param>
/// <param name="Point">The rewind point, or null when the reader's own position is the answer</param>
/// <param name="AbandonedMacros">Whether the resume replays a macro invocation</param>
internal readonly record struct StreamRewind(int Stream, JobResumePoint? Point, bool AbandonedMacros);

/// <summary>
/// How a sequence ended
/// </summary>
/// <param name="Reply">What to tell whoever asked for it</param>
/// <param name="Failed">Whether it could not do what it was asked</param>
/// <param name="Rewinds">Where each stream carries on from, for a pause</param>
/// <remarks>
/// The settling transition is chosen by the controller from this, rather than by a <c>finally</c>
/// reading fields it did not write. A sequence writes no job state: what it worked out travels back
/// here, and the transition that settles the phase is what records it
/// </remarks>
internal readonly record struct SequenceOutcome(Message Reply, bool Failed, IReadOnlyList<StreamRewind>? Rewinds = null);

/// <summary>
/// Something for the controller loop to do
/// </summary>
/// <remarks>
/// One command at a time, each carrying the completion of whoever asked for it. What is accepted,
/// what is refused and what is held is decided inside the loop from the phase it holds, never by a
/// handler reading the phase first and then choosing which command to post
/// </remarks>
internal abstract record JobCommand
{
    /// <summary>
    /// Completion of the caller waiting on this command
    /// </summary>
    public TaskCompletionSource<Message> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Answer the caller
    /// </summary>
    /// <param name="message">The reply</param>
    public void Reply(Message message) => Completion.TrySetResult(message);

    /// <summary>
    /// Answer the caller with an error
    /// </summary>
    /// <param name="content">What went wrong</param>
    public void Refuse(string content) => Completion.TrySetResult(new Message(MessageType.Error, content));

    /// <summary>Select a file to print or simulate (M23, M32, M37 P)</summary>
    public sealed record SelectFile(JobFile File, CodeChannel Channel) : JobCommand;

    /// <summary>Start a selected job or resume a paused one (M24, M32, M37)</summary>
    public sealed record StartOrResume(CodeChannel Channel, bool RunMacro) : JobCommand;

    /// <summary>Pause the job (M25, M226, M600, M601, an event)</summary>
    public sealed record Pause(PauseRequest Request) : JobCommand;

    /// <summary>Stop the job, or put the machine down when there is none (M0, M1, M2)</summary>
    public sealed record Stop(CodeChannel Channel) : JobCommand;

    /// <summary>Tear the job down because something went wrong</summary>
    public sealed record Abort : JobCommand;

    /// <summary>Set where in the file the job starts or carries on from (M26)</summary>
    public sealed record SetFilePosition(int Stream, long Position) : JobCommand;

    /// <summary>Fork the job onto the second file channel (M606 S1)</summary>
    public sealed record Fork : JobCommand;

    /// <summary>A stream has stopped where it was told to stop</summary>
    public sealed record ReaderStopped(int Stream, long Position) : JobCommand;

    /// <summary>A stream has run out of codes and the last of them has completed</summary>
    public sealed record ReaderFinished(int Stream) : JobCommand;

    /// <summary>A stream could not carry on</summary>
    public sealed record ReaderFailed(int Stream, Exception Error) : JobCommand;

    /// <summary>
    /// The sequence the controller started has ended
    /// </summary>
    /// <remarks>
    /// The id is what makes a completion belong to the sequence that is in flight. A sequence that
    /// was cancelled and replaced - the finish a pause landed on top of - still reports, and its
    /// report must not settle the phase its replacement is working towards
    /// </remarks>
    public sealed record SequenceCompleted(int Id, SequenceOutcome Outcome) : JobCommand;
}
