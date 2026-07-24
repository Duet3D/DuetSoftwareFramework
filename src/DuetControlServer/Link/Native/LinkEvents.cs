using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Native;

/// <summary>
/// Managed mirror of the ring-buffer record formats defined in
/// <c>DuetSbcInterface/src/SBC/LinkEvents.h</c>.
/// </summary>
/// <remarks>
/// These structs are read straight out of the native ring with <see cref="MemoryMarshal"/>, so their
/// layout must stay byte-for-byte identical to the C++ definitions. Every one is declared
/// <see cref="LayoutKind.Sequential"/> with <c>Pack = 1</c> and an explicit <c>Size</c>, matching the
/// <c>static_assert</c>s on the native side. Changing one side without the other silently corrupts
/// the other; <c>NativeLink</c> verifies the sizes at startup.
/// </remarks>
internal static class LinkEventLayout
{
    /// <summary>
    /// Request id meaning "fire and forget" -- no completion event will be sent
    /// </summary>
    internal const uint NoRequestId = 0;
}

/// <summary>
/// Type discriminator of an inbound (native -> managed) event record
/// </summary>
internal enum InboundEventType : ushort
{
    /// <summary>Message from the firmware: <see cref="MessageEvent"/> plus UTF-8 text</summary>
    Message = 1,

    /// <summary>Forwarded CAN message: <see cref="CanResponseEvent"/> plus CAN payload</summary>
    CanResponse = 2,

    /// <summary>Code buffer space update: <see cref="CodeBufferEvent"/></summary>
    CodeBufferUpdate = 3,

    /// <summary>The controller restarted; every pending resource must be invalidated. No payload</summary>
    ControllerReset = 4,

    /// <summary>The link dropped, with a UTF-8 reason</summary>
    ConnectionLost = 5,

    /// <summary>The link came up: <see cref="ConnectionEstablishedEvent"/></summary>
    ConnectionEstablished = 6,

    /// <summary>An awaited request finished: <see cref="RequestCompletedEvent"/> plus optional error text</summary>
    RequestCompleted = 7,

    /// <summary>Diagnostics from the transfer loop: <see cref="LogEvent"/> plus UTF-8 text</summary>
    Log = 8,

    /// <summary>An unparseable packet: <see cref="MalformedPacketEvent"/> plus the raw bytes</summary>
    MalformedPacket = 9,

    /// <summary>Unrecoverable error, with a UTF-8 message. Terminates the link service</summary>
    FatalError = 10
}

/// <summary>
/// Severity of an <see cref="InboundEventType.Log"/> event
/// </summary>
internal enum NativeLogLevel : byte
{
    /// <summary>Trace-level detail</summary>
    Debug = 0,

    /// <summary>Informational progress</summary>
    Info = 1,

    /// <summary>Recovered from a problem</summary>
    Warning = 2,

    /// <summary>Failed</summary>
    Error = 3
}

/// <summary>
/// Outcome reported by a <see cref="RequestCompletedEvent"/>
/// </summary>
internal enum RequestResult : byte
{
    /// <summary>The request was served</summary>
    Success = 0,

    /// <summary>The connection dropped or the resource was invalidated before the request was served</summary>
    Cancelled = 1,

    /// <summary>The request failed; an error message follows the header</summary>
    Failed = 2
}

/// <summary>
/// Common leading field of every inbound record
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
internal struct InboundEventHeader
{
    /// <summary>Record type (<see cref="InboundEventType"/>)</summary>
    public ushort Type;

    /// <summary>Reserved for future use</summary>
    public ushort Reserved;
}

/// <summary>
/// Message from the firmware. UTF-8 text follows this header
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
internal struct MessageEvent
{
    /// <summary>Record header</summary>
    public InboundEventHeader Header;

    /// <summary>Message type flags</summary>
    public uint Flags;
}

/// <summary>
/// Forwarded CAN message from an expansion board. The CAN payload follows this header
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
internal struct CanResponseEvent
{
    /// <summary>Token mapping the response back to its request</summary>
    public InboundEventHeader Header;

    /// <summary>Token mapping the response back to its request</summary>
    public ushort TxToken;

    /// <summary>Type of the received CAN message</summary>
    public ushort MsgType;

    /// <summary>Number of CAN payload bytes that follow</summary>
    public ushort DataLength;

    /// <summary>Source address of the replying board</summary>
    public byte SrcAddress;

    /// <summary>Flags of the CAN message</summary>
    public byte Flags;

    /// <summary>Status of the CAN message</summary>
    public byte Status;

    /// <summary>Padding</summary>
    public byte Padding;

    /// <summary>Padding</summary>
    public ushort Padding2;
}

/// <summary>
/// Update about the available code buffer size
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
internal struct CodeBufferEvent
{
    /// <summary>Record header</summary>
    public InboundEventHeader Header;

    /// <summary>Buffer space available in the firmware</summary>
    public ushort BufferSpace;

    /// <summary>Padding</summary>
    public ushort Padding;
}

/// <summary>
/// The link came up
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
internal struct ConnectionEstablishedEvent
{
    /// <summary>Record header</summary>
    public InboundEventHeader Header;

    /// <summary>Negotiated protocol version</summary>
    public ushort ProtocolVersion;

    /// <summary>Padding</summary>
    public ushort Padding;
}

/// <summary>
/// An awaited request finished. Optional UTF-8 error text follows when the result is
/// <see cref="RequestResult.Failed"/>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 12)]
internal struct RequestCompletedEvent
{
    /// <summary>Record header</summary>
    public InboundEventHeader Header;

    /// <summary>Id of the request being completed</summary>
    public uint RequestId;

    /// <summary>Outcome (<see cref="RequestResult"/>)</summary>
    public byte Result;

    /// <summary>Padding</summary>
    public byte Padding;

    /// <summary>Padding</summary>
    public ushort Padding2;
}

/// <summary>
/// Diagnostics from the transfer loop. UTF-8 text follows this header
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
internal struct LogEvent
{
    /// <summary>Record header</summary>
    public InboundEventHeader Header;

    /// <summary>Severity (<see cref="NativeLogLevel"/>)</summary>
    public byte Level;

    /// <summary>Padding</summary>
    public byte Padding;

    /// <summary>Padding</summary>
    public ushort Padding2;
}

/// <summary>
/// An unparseable packet. The raw packet bytes follow this header
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 12)]
internal struct MalformedPacketEvent
{
    /// <summary>Record header</summary>
    public InboundEventHeader Header;

    /// <summary>Id of the offending packet</summary>
    public ushort PacketId;

    /// <summary>Request code of the offending packet</summary>
    public ushort Request;

    /// <summary>Declared length of the offending packet</summary>
    public ushort Length;

    /// <summary>Offset the packet was read from</summary>
    public ushort Offset;
}
