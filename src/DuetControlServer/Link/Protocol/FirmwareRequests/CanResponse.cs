using DuetAPI;
using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Protocol.FirmwareRequests;

/// <summary>
/// CAN bus message received by the SBC
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 12)]
public struct CanResponseHeader
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
    /// CAN payload bytes that follow the header. Always &lt;= 64: the HAT no longer reassembles
    /// fragmented replies, so each packet carries exactly one CAN frame and DSF does the reassembly.
    /// </summary>
    public ushort DataLength;

    /// <summary>
    /// CAN destination: 0..126, or 127 for broadcast
    /// </summary>
    public byte SrcAddress;

    /// <summary>
    /// Flags for the CAN message
    /// </summary>
    public byte Flags;

    /// <summary>
    /// Status of the CAN message
    /// </summary>
    public CanStatus Status;
}
