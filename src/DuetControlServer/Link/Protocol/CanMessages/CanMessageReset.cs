using System.Runtime.InteropServices;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Reset an expansion board.
/// Mirrors <c>CanMessageReset</c> in CANlib's <c>CanMessageFormats.h</c>:
/// <code>uint16_t requestId : 12, zero : 4;</code>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 2)]
public struct CanMessageReset : ICanMessage
{
    /// <inheritdoc cref="ICanMessage.MessageType" />
    public static CanMessageType MessageType => CanMessageType.Reset;

    /// <summary>
    /// Backing word holding <c>requestId : 12, zero : 4</c>
    /// </summary>
    private ushort _bits;

    /// <summary>
    /// Request ID of this message (12-bit field)
    /// </summary>
    public ushort RequestId
    {
        readonly get => (ushort)(_bits & 0x0FFF);
        set => _bits = (ushort)((_bits & 0xF000) | (value & 0x0FFF));
    }
}
