using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace DuetControlServer.Link;

/// <summary>
/// Result of a CAN request, exposing the reassembled reply.
/// </summary>
/// <param name="Status">Status of the reply as the HAT saw it</param>
/// <param name="ResponseType">Actual type of the reply (<see cref="CanMessageType.NoReply"/> if none was expected)</param>
/// <param name="SrcAddress">Source address of the replying board</param>
/// <param name="DstAddress">Address the request was sent to</param>
/// <param name="Payload">Reassembled payload of the reply</param>
/// <param name="Extra">The reply's <c>extra</c> byte, which a few requests answer in rather than in the text</param>
/// <param name="ResultCode">Result code the board reported, or null if the reply type carries none</param>
/// <remarks>
/// Whether the board did what it was asked is <see cref="Status"/> and <see cref="ResultCode"/>, not
/// whether it sent any text: a board that refuses a request may say why, but it may equally say nothing,
/// and a board that carried one out may still have something to report.
/// </remarks>
public readonly record struct CanResponse(CanStatus Status, CanMessageType ResponseType, byte SrcAddress, byte DstAddress,
                                          byte[] Payload, byte Extra, CodeResult? ResultCode)
{
    /// <summary>
    /// Create a response from a completed request
    /// </summary>
    /// <param name="request">Completed CAN request</param>
    internal static CanResponse FromRequest(CanRequest request)
        => new(request.Status, request.ResponseType, request.SrcAddress, request.DstAddress, request.ResponsePayload,
               request.Extra, request.ResultCode);

    /// <summary>
    /// Text the board sent with the reply, empty if it said nothing
    /// </summary>
    /// <remarks>
    /// Only a standard reply carries text. For any other reply type the payload is the message body,
    /// which <see cref="AsCanMessage{T}" /> is the way to read
    /// </remarks>
    public string Text => ResponseType == CanMessageType.StandardReply ? Encoding.ASCII.GetString(Payload).TrimEnd('\0') : string.Empty;

    /// <summary>
    /// How this reply should be reported: what the board made of the request, or an error if it never
    /// answered
    /// </summary>
    public MessageType Severity => Status != CanStatus.Ok ? MessageType.Error
        : ResultCode?.ToMessageType() ?? MessageType.Success;

    /// <summary>
    /// The reply as a message to pass back to whoever sent the request
    /// </summary>
    /// <returns>What the board said, reported as what it made of the request</returns>
    /// <remarks>
    /// A board that did what it was asked without comment gives an empty success message, which is
    /// what this codebase means by "nothing to report": returning it as a code's result says the code
    /// is done and produced no output, and <see cref="CanReplies.ToMessage" /> leaves it out when
    /// collecting what several boards said. Callers therefore never have to decide whether there is
    /// anything worth passing on - the decision a warning would lose by
    /// </remarks>
    public Message ToMessage() => new(Severity, Description);

    /// <summary>
    /// Interpret the reply payload as the message body its type maps to
    /// </summary>
    /// <typeparam name="T">CAN message type this reply is sent as</typeparam>
    /// <returns>Deserialized message body</returns>
    /// <exception cref="InvalidOperationException">The reply is not of that type</exception>
    /// <remarks>
    /// A reply type maps to exactly one message struct, but C# cannot pick a type from a value known
    /// only at run time, so the caller names it and this checks the choice. Reading the reply as the
    /// wrong struct would otherwise be silent: the payload is bytes, and every message is happy to be
    /// read out of any of them. <c>T.MessageType</c> is a static abstract member on a value type, so
    /// the JIT resolves it per instantiation and the check costs a comparison against a constant.
    /// </remarks>
    public readonly T AsCanMessage<T>() where T : struct, ICanMessage<T>
    {
        if (T.MessageType != ResponseType)
        {
            throw new InvalidOperationException(ResponseType == CanMessageType.NoReply
                ? $"Cannot read a {T.MessageType} from a request that expected no reply"
                : $"Cannot read a {T.MessageType} from a reply of type {ResponseType}");
        }
        if (ResponseType == CanMessageType.StandardReply)
        {
            // Reassembly keeps only the text of a standard reply, since that is what the fragments
            // stitch together; its header is gone by the time anyone can ask for it
            throw new InvalidOperationException($"A standard reply is read through {nameof(Text)} and {nameof(ResultCode)}");
        }
        return CanMessageSerializer.Deserialize<T>(Payload);
    }

    /// <summary>
    /// What the board said, or why it did not say it
    /// </summary>
    private string Description => Status != CanStatus.Ok ? $"Board {DstAddress} did not answer ({Status})"
        : !string.IsNullOrWhiteSpace(Text) ? Text
        : Severity == MessageType.Error ? $"Board {DstAddress} rejected the request ({ResultCode})"
        : string.Empty;
}

/// <summary>
/// Helpers for the replies collected from several boards at once.
/// </summary>
public static class CanReplies
{
    /// <summary>
    /// Combine what several boards said into the one message the code they came from returns
    /// </summary>
    /// <param name="replies">What each board replied, ignoring the ones that said nothing</param>
    /// <returns>The collected text, reported as the worst of what the boards made of it</returns>
    public static Message ToMessage(this IEnumerable<Message?> replies)
    {
        MessageType type = MessageType.Success;
        List<string> lines = [];
        foreach (Message? reply in replies)
        {
            if (reply is null)
            {
                continue;
            }
            if (reply.Type > type)
            {
                type = reply.Type;
            }
            if (!string.IsNullOrWhiteSpace(reply.Content))
            {
                lines.Add(reply.Content);
            }
        }
        return new Message(type, string.Join('\n', lines));
    }
}
