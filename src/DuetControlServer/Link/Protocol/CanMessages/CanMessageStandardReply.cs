using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Standard reply used by many calls. It carries a GCodeResult, some text, and occasionally 8 bits
/// of extra information. It can be split into multiple fragments so that the text is not limited to
/// a single CAN frame. Mirrors <c>CanMessageStandardReply</c> in CANlib's <c>CanMessageFormats.h</c>:
/// <code>uint32_t requestId : 12, resultCode : 4, fragmentNumber : 7, moreFollows : 1, extra : 8; char text[60];</code>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
public struct CanMessageStandardReply : ICanMessage<CanMessageStandardReply>
{
    /// <inheritdoc cref="ICanMessage.MessageType" />
    public static CanMessageType MessageType => CanMessageType.StandardReply;

    /// <summary>
    /// Maximum number of text characters carried by a single fragment
    /// </summary>
    public const int MaxTextLength = 60;

    /// <summary>
    /// Backing word holding <c>requestId : 12, resultCode : 4, fragmentNumber : 7, moreFollows : 1, extra : 8</c>
    /// </summary>
    private uint _bits;

    /// <summary>
    /// Text of this fragment (not necessarily null-terminated; trailing bytes are zero)
    /// </summary>
    public TextBuffer60 Text;

    /// <summary>
    /// Request ID of the message we are replying to (12-bit field)
    /// </summary>
    public ushort RequestId
    {
        readonly get => (ushort)(_bits & 0x0FFF);
        set => _bits = (_bits & ~0x0FFFu) | (value & 0x0FFFu);
    }

    /// <summary>
    /// Result code, normally a GCodeResult (4-bit field)
    /// </summary>
    public byte ResultCode
    {
        readonly get => (byte)((_bits >> 12) & 0x0F);
        set => _bits = (_bits & ~(0x0Fu << 12)) | ((value & 0x0Fu) << 12);
    }

    /// <summary>
    /// Fragment number of this message (7-bit field)
    /// </summary>
    public byte FragmentNumber
    {
        readonly get => (byte)((_bits >> 16) & 0x7F);
        set => _bits = (_bits & ~(0x7Fu << 16)) | ((value & 0x7Fu) << 16);
    }

    /// <summary>
    /// Set if this is not the last fragment of the reply (1-bit field)
    /// </summary>
    public bool MoreFollows
    {
        readonly get => ((_bits >> 23) & 1) != 0;
        set => _bits = value ? (_bits | (1u << 23)) : (_bits & ~(1u << 23));
    }

    /// <summary>
    /// Extra data, normally unused (8-bit field)
    /// </summary>
    public byte Extra
    {
        readonly get => (byte)((_bits >> 24) & 0xFF);
        set => _bits = (_bits & ~(0xFFu << 24)) | ((value & 0xFFu) << 24);
    }

    /// <summary>
    /// Decode the text content of a received fragment from its raw CAN payload
    /// </summary>
    /// <param name="payload">Raw CAN payload (header word followed by text)</param>
    /// <returns>The fragment text (without trailing null padding)</returns>
    public static string GetText(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> text = payload.Length > sizeof(uint) ? payload[sizeof(uint)..] : ReadOnlySpan<byte>.Empty;
        int end = text.IndexOf((byte)0);
        if (end >= 0)
        {
            text = text[..end];
        }
        return Encoding.UTF8.GetString(text);
    }
}

/// <summary>
/// Blittable inline buffer for <c>char text[60]</c>
/// </summary>
[InlineArray(CanMessageStandardReply.MaxTextLength)]
public struct TextBuffer60
{
    private byte _element0;
}
