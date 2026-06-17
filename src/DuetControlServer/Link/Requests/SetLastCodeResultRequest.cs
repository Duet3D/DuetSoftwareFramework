using DuetAPI;
using DuetControlServer.Link.Protocol.Shared;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Link;

/// <summary>
/// Internal storage class to update the last code result for a specific code channel
/// </summary>
/// <param name="channel">Where to update the result</param>
/// <param name="result">Code result to set</param>
public class SetLastCodeResultRequest(CodeChannel channel, CodeResult result)
{
    /// <summary>
    /// Where the expression is evaluated
    /// </summary>
    public CodeChannel Channel { get; } = channel;

    /// <summary>
    /// Expression to evaluate
    /// </summary>
    public CodeResult Result { get; } = result;

    /// <summary>
    /// Internal TCS for the task
    /// </summary>
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Task that completes when the request has been fulfilled
    /// </summary>
    public Task Task => _tcs.Task;

    /// <summary>
    /// Set the result of the evaluated expression
    /// </summary>
    /// <param name="result">Result to set</param>
    public void SetResult() => _tcs.TrySetResult();

    /// <summary>
    /// Set the task to canceled
    /// </summary>
    public void SetCanceled() => _tcs.TrySetCanceled();

    /// <summary>
    /// Set an exception for the task
    /// </summary>
    /// <param name="exception">Exception to set</param>
    public void SetException(Exception exception) => _tcs.TrySetException(exception);
}
