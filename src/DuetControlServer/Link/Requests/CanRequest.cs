using DuetAPI;
using DuetControlServer.Link.Protocol.Shared;
using System;
using System.Threading.Tasks;

namespace DuetControlServer.Link;

/// <summary>
/// Represents a request sent over the CAN bus and the expected reply.
/// </summary>
/// <param name="messageType">Type of the CAN message</param>
/// <param name="replyType">Type of the expected reply</param>
/// <remarks>
/// If no reply is expected then the task is completed immediately after the request is sent over SPI.
/// If a reply is expected then the task is completed when the reply is received or if the request times out.
/// </remarks>
public class CanRequest(CanMessageType messageType, CanMessageType replyType)
{
    public CanMessageType MessageType { get; } = messageType;

    public CanMessageType ReplyType { get; } = replyType;



    /// <summary>
    /// Internal TCS for the task
    /// </summary>
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Task that completes when the request has been fulfilled
    /// </summary>
    public Task Task => _tcs.Task;

    /// <summary>
    /// Set the result of the CAN request
    /// </summary>
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
