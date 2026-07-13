using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Message used by to configure the address and normal timing of the CAN controller on DuetCANMaster and Duet3Expansion boards.
/// Mirrors <c>CanMessageSetAddressAndNormalTiming</c> in CANlib's <c>CanMessageFormats.h</c>:
/// <code>
/// uint16_t requestId : 12,
///          zero : 4;
/// uint8_t oldAddress;
/// uint8_t newAddress;
/// uint8_t newAddressInverted;
/// uint8_t doSetTiming;
/// CanTiming normalTiming;
/// </code>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
public struct CanMessageSetAddressAndNormalTiming : ICanMessage<CanMessageSetAddressAndNormalTiming>
{
    /// <inheritdoc cref="ICanMessage.MessageType" />
    public static CanMessageType MessageType => CanMessageType.SetAddressAndNormalTiming;

    public static byte DoSetTimingYes => 0xB6;
    public static byte DoSetTimingNo => 0;

    private ushort _bitField1;

    public byte oldAddress;
    public byte newAddress;
    public byte newAddressInverted;
    public byte doSetTiming;
    public CanTiming normalTiming;
}
