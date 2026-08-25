using System;
using System.Runtime.InteropServices;

namespace SystemTests;

/// <summary>
/// C# mirror of the transfer wire protocol in <c>lib/DuetSpiInterface/include/DuetSpiProtocol/MessageFormats.h</c>
/// and its socket framing in <c>SocketLinkFormats.h</c>, for the controller side the fake endpoint plays.
/// Every struct here is a wire format: the layout must match the C++ definitions byte for byte.
/// <see cref="ScriptedCanMaster.VerifyLayouts"/> asserts the sizes at startup
/// </summary>
internal static class SpiWire
{
    /// <summary>Unique format code for binary transfers</summary>
    public const byte FormatCode = 0x5F;

    /// <summary>Format code indicating that RRF is operating in standalone mode</summary>
    public const byte FormatCodeStandalone = 0x60;

    /// <summary>Unique format code that is not used anywhere else</summary>
    public const byte InvalidFormatCode = 0xC9;

    /// <summary>Protocol version of MessageFormats.h. CRC32 is used for version >= 4</summary>
    public const ushort ProtocolVersion = 8;

    /// <summary>Default size of a data transfer buffer</summary>
    public const int BufferSize = 8192;

    /// <summary>The step clock rate common to all Duet 3 boards, in Hz (Motion/StepClock.h)</summary>
    public const uint StepClockRate = 48_000_000 / 64;

    /// <summary>Byte the flasher sends back to confirm a verified firmware checksum</summary>
    public const byte FlashVerifyOk = 0x0C;

    /// <summary>Round a length up to the next 4-byte boundary, matching both sides' padding rules</summary>
    public static int AddPadding(int length) => (length + 3) & ~3;
}

/// <summary>Result codes for header and data transfers (Shared/TransferResponse.cs)</summary>
internal static class TransferResponse
{
    public const uint Success = 1;
    public const uint BadFormat = 2;
    public const uint BadProtocolVersion = 3;
    public const uint BadDataLength = 4;
    public const uint BadHeaderChecksum = 5;
    public const uint BadDataChecksum = 6;
    public const uint BadResponse = 0xFEFEFEFE;
}

/// <summary>Request indices SBC -> firmware</summary>
internal enum SbcRequest : ushort
{
    EmergencyStop = 0,
    Reset = 1,
    ConfigCAN = 2,
    EnableCAN = 3,
    ScheduleMove = 4,
    SendCANMessage = 5,
    WriteIap = 6,
    StartIap = 7,
    Message = 8,
}

/// <summary>Request indices firmware -> SBC</summary>
internal enum FirmwareRequest : ushort
{
    ResendPacket = 0,
    CodeBufferUpdate = 2,
    Message = 3,
    CANResponse = 5,
    MotionStopped = 6,
    CanMessageSent = 7,
}

/// <summary>Status of a forwarded CAN message</summary>
internal enum CanStatus : byte
{
    Ok = 0,
    Timeout = 1,
    BusError = 2,
    NoBuffer = 3,
    Overflow = 4,
}

/// <summary>Frame types of the socket framing (SocketLinkFormats.h)</summary>
internal enum SocketFrameType : byte
{
    Ready = 1,
    DataAvailable = 2,
    Transfer = 3,
    Response = 4,
    IapData = 5,
    IapVerify = 6,
    IapVerdict = 7,
}

/// <summary>Prefixes every socket frame; the payload length excludes this header</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
internal struct SocketFrameHeader
{
    public byte Type;
    public byte Padding;
    public ushort Padding2;
    public uint Length;
}

/// <summary>Header describing the content of a full transfer (SpiTransferHeader)</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 24)]
internal struct TransferHeader
{
    public byte FormatCode;
    public byte NumPackets;
    public ushort ProtocolVersion;
    public ushort SequenceNumber;
    public ushort DataLength;
    public uint CrcData;
    public uint MasterClock;
    public uint HiccupTime;
    public uint CrcHeader;

    /// <summary>The header checksum covers everything before <see cref="CrcHeader"/></summary>
    public const int CrcCoveredLength = 20;
}

/// <summary>Header used for single packets in both directions</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
internal struct PacketHeader
{
    public ushort Request;
    public ushort Id;
    public ushort Length;
    public ushort ResendPacketId;
}

/// <summary>Header for arbitrary messages; MessageType is a MessageTypeFlags bitmap</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
internal struct MessageHeader
{
    public uint MessageType;
    public ushort Length;
    public ushort Padding;
}

/// <summary>Enable/disable a CAN bus interface</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
internal struct EnableCanHeader
{
    public byte Channel;
    public byte Enable;
    public ushort Padding;
}

/// <summary>Schedule a move on the controller; ScheduleMoveDriver records follow</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 56)]
internal struct ScheduleMoveHeader
{
    public uint WhenToExecute;
    public uint AccelClocks;
    public uint SteadyClocks;
    public uint DecelClocks;
    public float Acceleration;
    public float Deceleration;
    public float TotalDistance;
    public float AccelDistance;
    public float DecelStartDistance;
    public float StartSpeed;
    public float TopSpeed;
    public float EndSpeed;
    public uint MoveId;
    public byte NumDrivers;
    public byte Flags;
    public ushort Padding;
}

/// <summary>One driver's share of a scheduled move</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
internal struct ScheduleMoveDriver
{
    public byte BoardAddress;
    public byte DriverNumber;
    public byte IsExtruder;
    public byte StopOnBoard;
    public int Steps;
    public float Extrusion;
    public ushort StopOnHandle;
    public byte StopGroup;
    public byte StopAction;
}

/// <summary>Send a CAN message to the controller</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 12)]
internal struct SendCanMessageHeader
{
    public ushort TxToken;
    public ushort MsgType;
    public ushort ReplyType;
    public byte DataLength;
    public byte DstAddress;
    public byte Flags;
    public byte Padding;
    public ushort Padding2;
}

/// <summary>Final message to the IAP program checking the flashed firmware</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
internal struct FlashVerify
{
    public uint FirmwareLength;
    public ushort Crc16;
    public ushort Padding;
}

/// <summary>Update about the available code buffer size</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
internal struct CodeBufferUpdateHeader
{
    public ushort BufferSpace;
    public ushort Padding;
}

/// <summary>One driver whose motion an endstop cut short</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
internal struct MotionStoppedDriver
{
    public byte BoardAddress;
    public byte DriverNumber;
    public ushort Padding;
}

/// <summary>Drives the controller stopped because an endstop fired; MotionStoppedDriver records follow</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 12)]
internal struct MotionStoppedHeader
{
    public uint WhenTriggered;
    public uint MoveId;
    public byte NumDrivers;
    public byte Padding0;
    public byte Padding1;
    public byte Padding2;
}

/// <summary>What became of the CAN messages the SBC asked to be sent; entries follow</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
internal struct CanMessageSentHeader
{
    public ushort Count;
    public ushort Padding;
}

/// <summary>One acknowledged CAN send</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
internal struct CanMessageSentEntry
{
    public ushort TxToken;
    public byte Status;
    public byte Padding;
}

/// <summary>CAN bus message received by the SBC</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 12)]
internal struct CanResponseHeader
{
    public ushort TxToken;
    public ushort MsgType;
    public ushort DataLength;
    public byte SrcAddress;
    public byte Flags;
    public byte Status;
    public byte Padding;
    public ushort Padding2;
}

/// <summary>Read/write helpers for the wire structs</summary>
internal static class Wire
{
    public static T Read<T>(ReadOnlySpan<byte> source) where T : struct => MemoryMarshal.Read<T>(source);

    public static byte[] ToBytes<T>(in T value) where T : struct
    {
        byte[] buffer = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(buffer, in value);
        return buffer;
    }

    public static void Write<T>(Span<byte> destination, in T value) where T : struct
        => MemoryMarshal.Write(destination, in value);
}
