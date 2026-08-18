namespace DuetControlServer.Files;

/// <summary>
/// Where a job is between running and paused
/// </summary>
/// <remarks>
/// <para>
/// RepRapFirmware's <c>PauseState</c>. Pausing and resuming are not instantaneous - each runs a macro
/// and waits for the machine to stop moving - so the three transitional values are what makes an
/// operation in progress observable, and what lets a second request be refused rather than starting a
/// second sequence.
/// </para>
/// <para>
/// <strong>The order matters and must not be changed.</strong> RepRapFirmware relies on
/// <c>notPaused &lt; pausing &lt; { paused, resuming, cancelling }</c>, and so does the test for
/// whether a job should stop reading codes
/// </para>
/// </remarks>
public enum PauseState : byte
{
    /// <summary>
    /// The job is running normally, or there is no job
    /// </summary>
    NotPaused = 0,

    /// <summary>
    /// The job is coming to a stop and <c>pause.g</c> may still be running
    /// </summary>
    Pausing,

    /// <summary>
    /// The job is paused and can be resumed or cancelled
    /// </summary>
    Paused,

    /// <summary>
    /// The job is being resumed and <c>resume.g</c> may still be running
    /// </summary>
    Resuming,

    /// <summary>
    /// The job file has been closed and <c>cancel.g</c> is still running
    /// </summary>
    Cancelling
}
