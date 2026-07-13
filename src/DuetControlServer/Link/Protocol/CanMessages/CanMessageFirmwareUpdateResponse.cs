using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Message used by expansion boards to request a firmware update.
/// Mirrors <c>CanMessageFirmwareUpdateRequest</c> in CANlib's <c>CanMessageFormats.h</c>:
/// <code>
/// uint32_t fileOffset : 24,			// the offset in the file where this block starts
/// 		 dataLength : 6,			// the number of bytes of data that follow
/// 		 err : 2;					// the error code
/// uint32_t fileLength : 24,			// the total size of the firmware file
/// 		 zero : 8;
/// uint8_t data[56];					// up to 56 bytes of data
/// </code>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
public struct CanMessageFirmwareUpdateResponse : ICanMessage<CanMessageFirmwareUpdateResponse>
{
    /// <inheritdoc cref="ICanMessage.MessageType" />
    public static CanMessageType MessageType => CanMessageType.FirmwareBlockResponse;

    public static byte ErrNone => 0;
    public static byte ErrNoFile => 1;
    public static byte ErrBadOffset => 2;
    public static byte ErrOther => 3;

    private uint _bitField1;
    private uint _bitField2;

    /// <summary>
    /// null-terminated board type name (firmware request) or bootloader class name (bootloader request)
    /// </summary>
    public CanMessageFirmwareUpdateResponseDataBuffer Data;

    /// <summary>
    /// The offset in the file where this block starts (24-bit field)
    /// </summary>
    public uint FileOffset
    {
        readonly get => _bitField1 & 0xFFFFFF;
        set => _bitField1 = (_bitField1 & 0xFF000000) | (value & 0xFFFFFF);
    }

    /// <summary>
    /// The number of bytes of data that follow (6-bit field)
    /// </summary>
    public uint DataLength
    {
        readonly get => (_bitField1 >> 24) & 0x3F;
        set => _bitField1 = (_bitField1 & 0xC0FFFFFF) | ((value & 0x3F) << 24);
    }

    /// <summary>
    /// The error code (2-bit field)
    /// </summary>
    public byte Err
    {
        readonly get => (byte)((_bitField1 >> 30) & 0x03);
        set => _bitField1 = (_bitField1 & 0x3FFFFFFF) | ((uint)(value & 0x03) << 30);
    }

    /// <summary>
    /// The total size of the firmware file (24-bit field)
    /// </summary>
    public uint FileLength
    {
        readonly get => _bitField2 & 0xFFFFFF;
        set => _bitField2 = (_bitField2 & 0xFF000000) | (value & 0xFFFFFF);
    }

    public readonly uint GetActualDataLength() => 2 * sizeof(uint) + DataLength;
}

/// <summary>
/// Blittable inline buffer for <c>char boardType[56]</c>
/// </summary>
[InlineArray(CanMessageFirmwareUpdateResponseDataBuffer.Length)]
public struct CanMessageFirmwareUpdateResponseDataBuffer
{
    public const int Length = 56;
    private byte _element0;
}

