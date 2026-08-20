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
    Ordered,

    /// <summary>
    /// The physical effect belongs at a point in the path. Until a deferral implementation lands,
    /// the pipeline dispatches these immediately, which is today's behaviour
    /// </summary>
    Deferred,

    /// <summary>
    /// Changes what an already-queued move means, or needs the board's reply to produce its own:
    /// flush, then wait for standstill before the handler runs
    /// </summary>
    Barrier
}
