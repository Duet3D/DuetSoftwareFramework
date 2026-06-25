using System;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.Can;

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
    public static void GetFragmentInfo(CanMessageType replyType, ReadOnlySpan<byte> payload, out int fragmentNumber, out bool moreFollows, out ReadOnlySpan<byte> content)
    {
        switch (replyType)
        {
            case CanMessageType.StandardReply:
                // uint32_t requestId:12, resultCode:4, fragmentNumber:7, moreFollows:1, extra:8
                uint header = payload.Length >= sizeof(uint) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;
                fragmentNumber = (int)((header >> 16) & 0x7F);
                moreFollows = ((header >> 23) & 1) != 0;
                content = payload.Length > sizeof(uint) ? payload[sizeof(uint)..] : ReadOnlySpan<byte>.Empty;
                break;

            default:
                // Non-fragmented reply: a single fragment carrying the whole payload
                fragmentNumber = 0;
                moreFollows = false;
                content = payload;
                break;
        }
    }
}
