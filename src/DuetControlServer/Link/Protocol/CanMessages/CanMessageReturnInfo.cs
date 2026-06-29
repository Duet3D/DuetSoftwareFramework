using System.Runtime.InteropServices;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Return information from an expansion board.
/// Mirrors <c>CanMessageReturnInfo</c> in CANlib's <c>CanMessageFormats.h</c>:
/// <code>uint16_t requestId : 12, zero : 4;</code>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 2)]
public struct CanMessageReturnInfo : ICanMessage
{
    /// <inheritdoc cref="ICanMessage.MessageType" />
    public static CanMessageType MessageType => CanMessageType.ReturnInfo;

    public static byte TypeFirmwareVersion => 0;
    public static byte TypeBoardName => 1;
    public static byte UnusedWasTypePressureAdvance => 2;
    public static byte UnusedWasTypeM408 => 3;
    public static byte TypeBootloaderName => 4;
    public static byte TypeBoardUniqueId => 5;
    
    /// <summary>
    /// The first part of the diagnostics information, other parts of the diagnostics reply use 101, 102, etc so keep those free
    /// </summary>
    public static byte TypeDiagnosticsPart0 => 100;


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

    public ushort Param
    {
        readonly get => (ushort)(_bits & 0xF000);
        set => _bits = (ushort)((_bits & 0x0FFF) | (value & 0xF000));
    }

    public byte Type;
}
