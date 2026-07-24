using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DuetControlServer.Link;

/// <summary>
/// Represents a request sent over the CAN bus and the expected reply.
/// </summary>
/// <param name="messageType">Type of the CAN message</param>
/// <param name="replyType">Type of the expected reply (<see cref="CanMessageType.NoReply"/> if none)</param>
/// <param name="txToken">Token used to map the response back to this request</param>
/// <param name="dstAddress">CAN destination address (0..126, or 127 for broadcast)</param>
/// <param name="isResponse">Is the CAN message a response to an expansion board</param>
/// <param name="requestPayload">Serialized CAN message payload to send</param>
/// <remarks>
/// If no reply is expected then the task is completed immediately after the request is sent over SPI.
/// If a reply is expected then the task is completed once the (possibly fragmented) reply has been
/// fully received, or if the request times out or the connection is lost.
/// </remarks>
public class CanRequest(CanMessageType messageType, CanMessageType replyType, ushort txToken, byte dstAddress, bool isResponse, byte[] requestPayload)
{
    /// <summary>
    /// Type of the CAN message
    /// </summary>
    public CanMessageType MessageType { get; } = messageType;

    /// <summary>
    /// Type of the expected reply (<see cref="CanMessageType.NoReply"/> if none)
    /// </summary>
    public CanMessageType ReplyType { get; } = replyType;

    /// <summary>
    /// Token used to map the response back to this request
    /// </summary>
    public ushort TxToken { get; } = txToken;

    /// <summary>
    /// CAN destination address (0..126, or 127 for broadcast)
    /// </summary>
    public byte DstAddress { get; } = dstAddress;

    /// <summary>
    /// If the request is a response to another CAN message
    /// </summary>
    public bool IsResponse { get; } = isResponse;

    /// <summary>
    /// Serialized CAN message payload to send
    /// </summary>
    public byte[] RequestPayload { get; } = requestPayload;

    /// <summary>
    /// Whether this request has already been written to the firmware
    /// </summary>
    public bool Sent { get; set; }

    /// <summary>
    /// Whether a reply is expected for this request
    /// </summary>
    public bool ExpectsReply => ReplyType != CanMessageType.NoReply;

    /// <summary>
    /// Source address of the board that sent the reply
    /// </summary>
    public byte SrcAddress { get; private set; }

    /// <summary>
    /// Actual type of the received reply
    /// </summary>
    public CanMessageType ResponseType { get; private set; }

    /// <summary>
    /// Status of the received reply
    /// </summary>
    public CanStatus Status { get; private set; }

    /// <summary>
    /// Reassembled payload of the reply (concatenated content of all fragments)
    /// </summary>
    public byte[] ResponsePayload { get; private set; } = [];

    /// <summary>
    /// Received reply fragments, keyed by fragment number to handle out-of-order delivery
    /// </summary>
    private readonly SortedDictionary<int, byte[]> _fragments = [];

    /// <summary>
    /// Internal TCS for the task
    /// </summary>
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Task that completes when the request has been fulfilled
    /// </summary>
    public Task Task => _tcs.Task;

    /// <summary>
    /// Add a received reply fragment. Duplicate fragment numbers are ignored.
    /// </summary>
    /// <param name="fragmentNumber">Zero-based index of the fragment</param>
    /// <param name="content">Reassembly-relevant content of the fragment</param>
    public void AddFragment(int fragmentNumber, ReadOnlySpan<byte> content)
    {
        if (!_fragments.ContainsKey(fragmentNumber))
        {
            _fragments[fragmentNumber] = content.ToArray();
        }
    }

    /// <summary>
    /// Store the reply metadata, assemble the buffered fragments and complete the task
    /// </summary>
    /// <param name="status">Status of the reply</param>
    /// <param name="responseType">Actual type of the reply</param>
    /// <param name="srcAddress">Source address of the replying board</param>
    public void SetResult(CanStatus status, CanMessageType responseType, byte srcAddress)
    {
        Status = status;
        ResponseType = responseType;
        SrcAddress = srcAddress;

        using MemoryStream assembled = new();
        foreach (byte[] fragment in _fragments.Values)
        {
            assembled.Write(fragment);
        }
        ResponsePayload = assembled.ToArray();

        _tcs.TrySetResult();
    }

    /// <summary>
    /// Complete a request for which no reply is expected
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
