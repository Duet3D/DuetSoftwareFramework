using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using DuetControlServer.Link.Protocol.Shared;

namespace DuetControlServer.Link.Protocol.CanMessages;

/// <summary>
/// Message used by expansion boards to request a firmware update.
/// Mirrors <c>CanMessageFirmwareUpdateRequest</c> in CANlib's <c>CanMessageFormats.h</c>:
/// <code>
/// uint32_t fileOffset : 24,			// the offset in the file of the data we need
/// 		 bootloaderVersion: 5,		// the protocol version of the bootloader or firmware making this request, currently 0
/// 		 uf2Format : 1,				// set if we want UF2 format, otherwise we want binary
/// 		 fileWanted : 2;			// 0 = want firmware file, 1 and 2 reserved, 3 = want bootloader
/// uint32_t lengthRequested : 24,		// how much data we want
/// 		 boardVersion : 8;			// the hardware version of this board, currently always 0 for production boards
/// char boardType[56];					// null-terminated board type name (firmware request) or bootloader class name (bootloader request)
/// char boardTypeAndFirmwareVersion[43];
/// </code>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 64)]
public struct CanMessageFirmwareUpdateRequest : ICanMessage
{
    /// <inheritdoc cref="ICanMessage.MessageType" />
    public static CanMessageType MessageType => CanMessageType.FirmwareBlockRequest;

    public static uint BootloaderVersion0 => 0;

    private uint _bitField1;
    private uint _bitField2;

    /// <summary>
    /// null-terminated board type name (firmware request) or bootloader class name (bootloader request)
    /// </summary>
    public BoardTypeBuffer BoardType;

    public uint FileOffset
    {
        readonly get => _bitField1 & 0xFFFFFF;
        set => _bitField1 = (_bitField1 & 0xFF000000) | (value & 0xFFFFFF);
    }

    /// <summary>
    /// The protocol version of the bootloader or firmware making this request (5-bit field)
    /// </summary>
    public byte BootloaderVersion
    {
        readonly get => (byte)((_bitField1 >> 24) & 0x1F);
        set => _bitField1 = (_bitField1 & 0xE0FFFFFF) | ((uint)(value & 0x1F) << 24);
    }

    /// <summary>
    /// Set if this board takes a main firmware binary in .uf2 format (1-bit field)
    /// </summary>
    public bool UsesUf2Binary
    {
        readonly get => ((_bitField1 >> 29) & 1) != 0;
        set => _bitField1 = (uint)(value ? (_bitField1 | (1 << 29)) : (_bitField1 & ~(1 << 29)));
    }

    /// <summary>
    /// 0 = want firmware file, 1 and 2 reserved, 3 = want bootloader (2-bit field)
    /// </summary>
    public byte FileWanted
    {
        readonly get => (byte)((_bitField1 >> 30) & 0x3);
        set => _bitField1 = (_bitField1 & 0x3FFFFFFF) | ((uint)(value & 0x3) << 30);
    }

    /// <summary>
    /// How much data we want (24-bit field)
    /// </summary>
    public uint LengthRequested
    {
        readonly get => _bitField2 & 0xFFFFFF;
        set => _bitField2 = (_bitField2 & 0xFF000000) | (value & 0xFFFFFF);
    }

    /// <summary>
    /// The hardware version of this board (8-bit field)
    /// </summary>
    public byte BoardVersion
    {
        readonly get => (byte)((_bitField2 >> 24) & 0xFF);
        set => _bitField2 = (_bitField2 & 0x00FFFFFF) | ((uint)value << 24);
    }

    public string BoardTypeString => Encoding.ASCII.GetString(BoardType).TrimEnd('\0');
}

/// <summary>
/// Blittable inline buffer for <c>char boardType[56]</c>
/// </summary>
[InlineArray(56)]
public struct BoardTypeBuffer
{
    private byte _element0;
}

