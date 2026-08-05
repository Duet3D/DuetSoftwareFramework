using System;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Helper that decodes per-reply-type fragmentation information from a received CAN payload.
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
    /// Decode the fragmentation information of a received reply fragment
    /// </summary>
    /// <param name="replyType">Expected reply type of the request</param>
    /// <param name="payload">Raw CAN payload of this fragment</param>
    /// <param name="fragmentNumber">Zero-based index of this fragment</param>
    /// <param name="moreFollows">Whether further fragments are expected after this one</param>
    /// <param name="content">Reassembly-relevant content of this fragment</param>
    public static void GetFragmentInfo(CanMessageType replyType, ReadOnlySpan<byte> payload, out int fragmentNumber,
                                       out bool moreFollows, out byte extra, out ReadOnlySpan<byte> content)
    {
        switch (replyType)
        {
            case CanMessageType.StandardReply:
                CanMessageStandardReply reply = CanMessageSerializer.Deserialize<CanMessageStandardReply>(payload);
                fragmentNumber = reply.FragmentNumber;
                moreFollows = reply.MoreFollows;

                // Mostly unused, but some requests answer in it rather than in the text: creating an
                // input monitor returns the input's current state this way, which is the only way to
                // learn a switch that is already closed - the boards report changes, and a switch
                // that never changes never reports
                extra = reply.Extra;

                // The text is taken from the payload rather than from the deserialized copy because it
                // is the received bytes that get stitched together, and the reply is a stack copy that
                // pads a short fragment out to the full 60 characters. A reply with an empty text is
                // exactly the header, so that is where the text starts
                int headerLength = (int)reply.GetActualDataLength(0);
                content = payload.Length > headerLength ? payload[headerLength..] : ReadOnlySpan<byte>.Empty;
                break;

            default:
                // Non-fragmented reply: a single fragment carrying the whole payload
                fragmentNumber = 0;
                moreFollows = false;
                extra = 0;
                content = payload;
                break;
        }
    }
}
