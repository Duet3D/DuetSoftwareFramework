using System;
using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.SbcRequests;

/// <summary>
/// Configure the CAN bus interface
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 12)]
public struct SendCanMessageHeader
{
    /// <summary>
    /// SBC-chosen token to map responses to the request. Not sent in the CAN message.
    /// </summary>
    public ushort TxToken;

    /// <summary>
    /// CanMessageType to place in the CAN id
    /// </summary>
    public ushort MsgType;

    /// <summary>
    /// Reply to expect from the expansion board. If no reply is expected, set to 0xFFFF
    /// </summary>
    public ushort ReplyType;

    /// <summary>
    /// CAN payload bytes that follow the header. Must be <= 64
    /// </summary>
    public byte DataLength;

    /// <summary>
    /// CAN destination: 0..126, or 127 for broadcast
    /// </summary>
    public byte DstAddress;

    /// <summary>
    /// Flags for the CAN message
    /// </summary>
    public byte Flags;
}
