namespace DuetControlServer.Codes;

/// <summary>
/// How a code relates to motion, declared per code in the handlers' <see cref="CodeTable{THandler}"/>
/// rows and enforced by the pipeline before the handler runs
/// </summary>
public enum CodeClass
{
    /// <summary>
    /// No relation to motion: act now, do not wait for the channel's pending codes
    /// </summary>
    Immediate,

    /// <summary>
    /// Applies to moves built after it and must not reach moves already built: flush the pipeline
    /// (order and expressions), no standstill; the move carries the value
    /// </summary>
    Flush,

    /// <summary>
    /// The physical effect belongs at a point in the path: a code from the job is held back until the
    /// move queued before it has retired, and any other flushes and applies now
    /// </summary>
    Deferred,

    /// <summary>
    /// Changes what an already-queued move means, or needs the board's reply to produce its own:
    /// flush, then wait for standstill before the handler runs
    /// </summary>
    FlushAndStandstill
}
