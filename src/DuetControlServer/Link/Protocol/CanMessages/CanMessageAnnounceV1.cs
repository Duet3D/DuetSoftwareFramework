using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Message used by expansion boards (firmware 3.4.0beta5 and later) to announce their presence on
/// the CAN bus. Mirrors <c>CanMessageAnnounceV1</c> in CANlib's <c>CanMessageFormats.h</c>:
/// <code>
/// uint32_t timeSinceStarted;
/// uint8_t uniqueId[16];
/// uint8_t numDrivers : 4, usesUf2Binary : 1, zero : 3;
/// char boardTypeAndFirmwareVersion[43];
/// </code>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
public struct CanMessageAnnounceV1 : ICanMessage<CanMessageAnnounceV1>
{
    /// <inheritdoc cref="ICanMessage.MessageType" />
    public static CanMessageType MessageType => CanMessageType.AnnounceV1;

    /// <summary>
    /// How long since the board started up
    /// </summary>
    public uint TimeSinceStarted;

    /// <summary>
    /// Unique ID of the board
    /// </summary>
    public UniqueIdBuffer UniqueId;

    /// <summary>
    /// Backing byte holding <c>numDrivers : 4, usesUf2Binary : 1, zero : 3</c>
    /// </summary>
    private byte _driverBits;

    /// <summary>
    /// Type short name of the board followed by '|' and the firmware version
    /// </summary>
    public BoardTypeAndFirmwareVersionBuffer BoardTypeAndFirmwareVersion;

    /// <summary>
    /// Number of motor drivers on this board (4-bit field)
    /// </summary>
    public byte NumDrivers
    {
        readonly get => (byte)(_driverBits & 0x0F);
        set => _driverBits = (byte)((_driverBits & 0xF0) | (value & 0x0F));
    }

    /// <summary>
    /// Set if this board takes a main firmware binary in .uf2 format (1-bit field)
    /// </summary>
    public bool UsesUf2Binary
    {
        readonly get => ((_driverBits >> 4) & 1) != 0;
        set => _driverBits = (byte)(value ? (_driverBits | (1 << 4)) : (_driverBits & ~(1 << 4)));
    }
}

/// <summary>
/// Blittable inline buffer for <c>uint8_t uniqueId[16]</c>
/// </summary>
[InlineArray(16)]
public struct UniqueIdBuffer
{
    private byte _element0;
}

/// <summary>
/// Blittable inline buffer for <c>char boardTypeAndFirmwareVersion[43]</c>
/// </summary>
[InlineArray(43)]
public struct BoardTypeAndFirmwareVersionBuffer
{
    private byte _element0;
}
