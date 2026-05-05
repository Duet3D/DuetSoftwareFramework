using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.Shared;

/// <summary>
/// Header describing the content of a full USB transfer.
/// No CRC, format code, or sequence numbers - USB handles integrity and ordering.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public struct UsbTransferHeader
{
    /// <summary>
    /// Number of packets in the data transfer
    /// </summary>
    public byte NumPackets;

    /// <summary>
    /// Reserved for future use
    /// </summary>
    public byte Padding;

    /// <summary>
    /// Total length of the data transfer in bytes
    /// </summary>
    public ushort DataLength;

    /// <summary>
    /// Reserved for alignment
    /// </summary>
    public uint Padding2;
}
