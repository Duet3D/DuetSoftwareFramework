using System.Runtime.CompilerServices;
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
    FatalError = 10,

    /// <summary>A queued move finished executing: <see cref="MoveCompletedEvent"/></summary>
    MoveCompleted = 11,

    /// <summary>A move was rejected or could not be sent: <see cref="MoveFailedEvent"/></summary>
    MoveFailed = 12,

    /// <summary>
    /// An endstop cut a move short: <see cref="MotionStoppedEvent"/> plus one
    /// <see cref="MotionStoppedDriverEntry"/> per stopped driver
    /// </summary>
    /// <remarks>
    /// The controller's report, unchanged. Where the drives should end up is not in it, because the
    /// controller never generated the steps; that is worked out here from the trigger timestamp
    /// </remarks>
    MotionStopped = 13,

    /// <summary>Outbound commands reached the controller: <see cref="OutboundSeqEvent"/></summary>
    OutboundDelivered = 14,

    /// <summary>Outbound commands were abandoned instead: <see cref="OutboundSeqEvent"/></summary>
    OutboundDropped = 15,

    /// <summary>What became of CAN messages sent for us: <see cref="CanMessagesSentEvent"/> plus entries</summary>
    CanMessagesSent = 16
}

/// <summary>
/// How well the native step-clock model is tracking the controller. Mirrors <c>DuetSbcClockStats</c>
/// </summary>
/// <remarks>
/// Move start times are expressed in the modelled clock, so how well it tracks is how well moves
/// land. The margin they have to stay inside is the preparation lead time, 25ms
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NativeClockStats
{
    /// <summary>Fitted rate minus nominal, in parts per million</summary>
    public double DriftPpm;

    /// <summary>Samples in the current fit</summary>
    public uint NumSamples;

    /// <summary>Largest deviation of a sample from the fit since startup, in nanoseconds</summary>
    public uint PeakResidualNs;

    /// <summary>Times a new fit would have made the reading go backwards, and was clamped</summary>
    public uint NumBackwardClamps;

    /// <summary>Samples discarded as implausible</summary>
    public uint NumRejectedSamples;

    /// <summary>Non-zero once the fit rests on enough samples to be trusted</summary>
    public int Synced;
}

/// <summary>
/// What one DDA ring has done. Mirrors <c>DuetSbcRingStats</c>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NativeRingStats
{
    /// <summary>Moves this ring has taken, as a running total</summary>
    public uint ScheduledMoves;

    /// <summary>Moves this ring has retired, as a running total</summary>
    public uint CompletedMoves;

    /// <summary>Times the lookahead algorithm produced a speed it could not honour</summary>
    public uint NumLookaheadErrors;

    /// <summary>Times lookahead ran out of queued moves to adjust</summary>
    public uint NumLookaheadUnderruns;

    /// <summary>Times a move was wanted and the ring was empty</summary>
    public uint NumNoMoveUnderruns;
}

/// <summary>
/// The per-ring counters of <see cref="NativeMotionStats"/>
/// </summary>
/// <remarks>
/// An inline array rather than a marshalled one: the struct crosses the ABI through a
/// source-generated P/Invoke, which requires it to be blittable, and <c>[MarshalAs(ByValArray)]</c>
/// is not
/// </remarks>
[InlineArray(NativeMotionStats.MaxRings)]
public struct NativeRingStatsArray
{
    private NativeRingStats _element0;
}

/// <summary>
/// What the native motion engine has done. Mirrors <c>DuetSbcMotionStats</c>
/// </summary>
/// <remarks>
/// Counters rather than formatted text: this side owns the wording of M122, as it does for every
/// other reply, so the native side reports numbers and <see cref="Motion.MotionDiagnostics"/> renders
/// them
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NativeMotionStats
{
    /// <summary>Movement systems the engine builds. Must match the native <c>DUET_SBC_MAX_RINGS</c></summary>
    public const int MaxRings = 2;

    /// <summary>MoveSegments allocated since startup</summary>
    public uint SegmentsCreated;

    /// <summary>How far the movement timebase lags the raw step clock, in ticks</summary>
    public uint MovementDelayTicks;

    /// <summary>Moves refused because the submission queue was full</summary>
    public uint SubmissionsDropped;

    /// <summary>Forced positions the motion thread has adopted</summary>
    public uint ForcedPositionsApplied;

    /// <summary>ScheduleMove packets the link refused. Non-zero means motion was lost</summary>
    public uint DroppedSchedulePackets;

    /// <summary>Per-ring counters</summary>
    public NativeRingStatsArray Rings;
}

/// <summary>
/// Why a move could not be executed. Mirrors the native <c>MovementError</c>
/// </summary>
/// <remarks>
/// Public because it crosses out of the link layer: <see cref="Motion.MotionTracker"/> records it for
/// whoever submitted the move
/// </remarks>
public enum NativeMovementError : byte
{
    /// <summary>The move is fine</summary>
    Ok = 0,

    /// <summary>Nothing significant was commanded, so the move was dropped</summary>
    NoMovement = 1,

    /// <summary>More than about +/- 2^31 microsteps from the zero position</summary>
    MicrostepPositionTooLarge = 2,

    /// <summary>The kinematics cannot reach the requested position</summary>
    UnreachablePosition = 3,

    /// <summary>The move would take more than about 2^31 step clocks</summary>
    MoveDurationTooLong = 4
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
/// What became of the CAN messages the controller was asked to send
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
internal struct CanMessagesSentEvent
{
    /// <summary>Record header</summary>
    public InboundEventHeader Header;

    /// <summary>How many entries follow</summary>
    public ushort Count;

    /// <summary>Padding</summary>
    public ushort Padding;
}

/// <summary>
/// One CAN message the controller was asked to send, and what became of it
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
internal struct CanMessageSentEntry
{
    /// <summary>Token the message was queued with</summary>
    public ushort TxToken;

    /// <summary>Outcome, as a <see cref="Protocol.FirmwareRequests.CanStatus"/></summary>
    public byte Status;

    /// <summary>Padding</summary>
    public byte Padding;
}

/// <summary>
/// How far the outbound queue has got
/// </summary>
/// <remarks>
/// The queue is FIFO end to end, so one number says what became of every command up to it. That is
/// what keeps the report off the per-command path, where the move stream lives
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 8)]
internal struct OutboundSeqEvent
{
    /// <summary>Record header</summary>
    public InboundEventHeader Header;

    /// <summary>Last command this applies to</summary>
    public uint SequenceNumber;
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

    /// <summary>Non-zero when the controller had reset while it was away, rather than resuming</summary>
    public ushort HadReset;
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

/// <summary>
/// A queued move finished executing
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
internal struct MoveCompletedEvent
{
    /// <summary>Record header</summary>
    public InboundEventHeader Header;

    /// <summary>The id this side gave the move when it submitted it</summary>
    public uint MoveId;

    /// <summary>The ring's running total, so a missed event is detectable rather than silent</summary>
    public uint CompletedMoves;

    /// <summary>Which ring the move was queued on</summary>
    public byte Ring;

    /// <summary>Padding</summary>
    public byte Padding;

    /// <summary>Padding</summary>
    public ushort Padding2;
}

/// <summary>
/// A move was rejected or could not be sent
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 12)]
internal struct MoveFailedEvent
{
    /// <summary>The id this side gave the move when it submitted it</summary>
    public InboundEventHeader Header;

    /// <summary>The id this side gave the move when it submitted it</summary>
    public uint MoveId;

    /// <summary>Which ring the move was queued on</summary>
    public byte Ring;

    /// <summary>Why it failed</summary>
    public NativeMovementError Error;

    /// <summary>Padding</summary>
    public ushort Padding;
}

/// <summary>
/// One driver an endstop stopped, as the controller named it
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 4)]
internal struct MotionStoppedDriverEntry
{
    /// <summary>CAN address of the board carrying the driver</summary>
    public byte BoardAddress;

    /// <summary>Driver number on that board</summary>
    public byte DriverNumber;

    /// <summary>Padding</summary>
    public ushort Padding;
}

/// <summary>
/// The drives an endstop stopped, and when it fired
/// </summary>
/// <remarks>
/// <para>
/// The controller stops the drives because it is the only component close enough to the CAN bus for
/// the latency to be acceptable, but it cannot say where they should end up: it never generated the
/// steps. This is the raw report, and what to do about it is decided here - see
/// <c>EndstopCorrection</c>.
/// </para>
/// <para>
/// <see cref="WhenTriggered"/> is in the controller's step clock and is zero when the board that
/// reported it is too old to send one
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
internal struct MotionStoppedEvent
{
    /// <summary>Record header</summary>
    public InboundEventHeader Header;

    /// <summary>Master step-clock time the endstop reported, zero if it sent none</summary>
    public uint WhenTriggered;

    /// <summary>
    /// The move that was stopped, as this side numbered it in <see cref="MoveParamsHeader.MoveId"/>
    /// </summary>
    /// <remarks>
    /// Without it a report that arrives after the next move has armed is applied to that move: the
    /// drives it names belong to the move that really stopped, so the wrong axis is corrected and the
    /// one that stopped keeps an endpoint it never reached. Nothing else can tell the two apart - the
    /// drives are usually the same ones, and the timestamp only becomes comparable once the report
    /// has been attributed to a move
    /// </remarks>
    public uint MoveId;

    /// <summary>Entries in the trailing array</summary>
    public byte NumDrivers;

    /// <summary>Padding</summary>
    public byte Padding0;

    /// <summary>Padding</summary>
    public ushort Padding1;
}
