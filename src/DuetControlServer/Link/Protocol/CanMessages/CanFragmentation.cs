using System;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// One received fragment of a CAN reply, decoded according to the reply type.
/// </summary>
/// <param name="number">Zero-based index of this fragment</param>
/// <param name="moreFollows">Whether further fragments are expected after this one</param>
/// <param name="extra">The reply's <c>extra</c> byte, which a few requests answer in rather than in the text</param>
/// <param name="resultCode">Result code the board reported, if this reply type carries one</param>
/// <param name="content">Reassembly-relevant content of this fragment</param>
public readonly ref struct CanFragment(int number, bool moreFollows, byte extra, CodeResult? resultCode, ReadOnlySpan<byte> content)
{
    /// <summary>
    /// Zero-based index of this fragment
    /// </summary>
    public int Number { get; } = number;

    /// <summary>
    /// Whether further fragments are expected after this one
    /// </summary>
    public bool MoreFollows { get; } = moreFollows;

    /// <summary>
    /// The reply's <c>extra</c> byte, which a few requests answer in rather than in the text
    /// </summary>
    public byte Extra { get; } = extra;

    /// <summary>
    /// Result code the board reported, or null if this reply type does not carry one
    /// </summary>
    public CodeResult? ResultCode { get; } = resultCode;

    /// <summary>
    /// Reassembly-relevant content of this fragment
    /// </summary>
    public ReadOnlySpan<byte> Content { get; } = content;
}

/// <summary>
/// Helper that decodes a received CAN payload according to the reply type it was sent as.
/// </summary>
/// <remarks>
/// The CAN-SBC HAT no longer reassembles fragmented replies, so each <c>CANResponse</c> packet is
/// exactly one CAN frame and this codebase must stitch the fragments back together. Fragmentation is
/// message-type specific; reply types without an explicit fragmentation scheme are treated as a
/// single, final fragment carrying the whole payload.
/// </remarks>
public static class CanFragmentation
{
    /// <summary>
    /// Decode a received reply fragment
    /// </summary>
    /// <param name="replyType">Expected reply type of the request</param>
    /// <param name="payload">Raw CAN payload of this fragment</param>
    /// <returns>What the fragment says about itself and the reply it belongs to</returns>
    public static CanFragment Parse(CanMessageType replyType, ReadOnlySpan<byte> payload)
    {
        switch (replyType)
        {
            case CanMessageType.StandardReply:
                CanMessageStandardReply reply = CanMessageSerializer.Deserialize<CanMessageStandardReply>(payload);

                // The text is taken from the payload rather than from the deserialized copy because it
                // is the received bytes that get stitched together, and the reply is a stack copy that
                // pads a short fragment out to the full 60 characters. A reply with an empty text is
                // exactly the header, so that is where the text starts
                int headerLength = (int)reply.GetActualDataLength(0);
                ReadOnlySpan<byte> text = payload.Length > headerLength ? payload[headerLength..] : ReadOnlySpan<byte>.Empty;

                // Extra is mostly unused, but some requests answer in it rather than in the text:
                // creating an input monitor returns the input's current state this way, which is the
                // only way to learn a switch that is already closed - the boards report changes, and a
                // switch that never changes never reports
                return new CanFragment(reply.FragmentNumber, reply.MoreFollows, reply.Extra, reply.ResultCode, text);

            // These replies are never fragmented, but they put the result code where a standard reply
            // has it, which is the whole reason their headers are laid out that way
            case CanMessageType.HeaterModelReport:
                return Unfragmented(CanMessageSerializer.Deserialize<CanMessageHeaterModelReport>(payload).ResultCode, payload);

            case CanMessageType.ReadInputsReplyV0:
                return Unfragmented(CanMessageSerializer.Deserialize<CanMessageReadInputsReplyV0>(payload).ResultCode, payload);

            case CanMessageType.ReadInputsReplyV1:
                return Unfragmented(CanMessageSerializer.Deserialize<CanMessageReadInputsReplyV1>(payload).ResultCode, payload);

            default:
                // A reply type with no result code of its own: whether it worked is only what the
                // transport says about it
                return Unfragmented(null, payload);
        }
    }

    private static CanFragment Unfragmented(CodeResult? resultCode, ReadOnlySpan<byte> payload)
        => new(0, false, 0, resultCode, payload);
}
