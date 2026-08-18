using System;
using System.Runtime.InteropServices;

namespace DuetControlServer.Link.Native;

/// <summary>
/// Configuration passed to the native interface. Mirrors <c>DuetSbcConfig</c> in
/// <c>DuetSbcInterface/src/CApi.h</c>
/// </summary>
/// <remarks>
/// Kept fully blittable so the source-generated P/Invoke can pass it by reference with no marshalling
/// stub. The two string fields are therefore raw UTF-8 pointers which the caller allocates and frees;
/// see <see cref="NativeLink.Connect"/>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeConfig
{
    /// <summary>Path to the spidev character device (UTF-8, NUL-terminated)</summary>
    public IntPtr SpiDevice;

    /// <summary>SPI clock frequency in Hz</summary>
    public uint SpiFrequency;

    /// <summary>SPI transfer mode (0..3)</summary>
    public int SpiTransferMode;

    /// <summary>Size of a data transfer buffer in bytes</summary>
    public int BufferSize;

    /// <summary>Path to the GPIO character device (UTF-8, NUL-terminated)</summary>
    public IntPtr GpioChipDevice;

    /// <summary>TfrRdy input line</summary>
    public int TransferReadyPin;

    /// <summary>DataAvailable input line</summary>
    public int DataAvailablePin;

    /// <summary>Optional scope-trigger output line, or -1 to disable it</summary>
    public int SbcDataAvailablePin;

    /// <summary>Whether to pin the interface thread to an isolated core</summary>
    public int IsolateInterfaceThread;

    /// <summary>Core to pin the interface thread to</summary>
    public int IsolatedCoreId;

    /// <summary>Whether to run the interface thread with real-time scheduling</summary>
    public int UseRealtimeScheduling;

    /// <summary>SCHED_FIFO priority of the interface thread</summary>
    public int InterfaceRtPriority;

    /// <summary>Timeout for the initial connection in ms</summary>
    public int SbcConnectTimeout;

    /// <summary>Timeout for a sub-exchange within a transfer in ms</summary>
    public int SbcTransferTimeout;

    /// <summary>Timeout for a header exchange in ms</summary>
    public int SbcConnectionTimeout;

    /// <summary>Maximum idle time before a keep-alive transfer in ms</summary>
    public int SbcConnectionKeepAliveInterval;

    /// <summary>Maximum number of retries per transfer stage</summary>
    public int MaxSbcRetries;

    /// <summary>Whether to tolerate a newer-than-supported protocol version so it can be flashed</summary>
    public int UpdateOnly;
}

/// <summary>
/// P/Invoke declarations for <c>libduet_sbc.so</c>, the native SPI transfer loop
/// </summary>
/// <remarks>
/// <para>
/// Threading rules imposed by the native side:
/// the <c>Queue*</c>/<c>Request*</c> entry points are safe to call from any thread concurrently,
/// but <see cref="DuetSbc_PeekEvent"/>, <see cref="DuetSbc_ConsumeEvent"/> and
/// <see cref="DuetSbc_WaitForEvent"/> form a single-consumer API and must only ever be used by the
/// dispatcher thread owned by <see cref="LinkService"/>.
/// </para>
/// <para>
/// None of these calls block on a lock the real-time interface thread holds, which is what keeps a
/// managed GC pause from stalling an SPI transfer.
/// </para>
/// </remarks>
internal static partial class NativeMethods
{
    /// <summary>
    /// Name of the native library. Resolved from the application directory at runtime
    /// </summary>
    internal const string LibraryName = "duet_sbc";

    /// <summary>
    /// Fill the given config with the native defaults
    /// </summary>
    /// <param name="config">Config to populate</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_DefaultConfig(out NativeConfig config);

    /// <summary>
    /// Create an interface instance
    /// </summary>
    /// <param name="config">Interface configuration</param>
    /// <param name="errorBuf">Buffer receiving an error message on failure</param>
    /// <param name="errorBufLen">Size of <paramref name="errorBuf"/></param>
    /// <returns>Handle, or <see cref="IntPtr.Zero"/> on failure</returns>
    [LibraryImport(LibraryName)]
    internal static partial IntPtr DuetSbc_Create(ref NativeConfig config, byte[]? errorBuf, int errorBufLen);

    /// <summary>
    /// Connect to the firmware. Blocks until the first transfer succeeds
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="errorBuf">Buffer receiving an error message on failure</param>
    /// <param name="errorBufLen">Size of <paramref name="errorBuf"/></param>
    /// <returns>Zero on success</returns>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_Connect(IntPtr handle, byte[]? errorBuf, int errorBufLen);

    /// <summary>
    /// Start the transfer loop on its own real-time thread
    /// </summary>
    /// <param name="handle">Interface handle</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_Start(IntPtr handle);

    /// <summary>
    /// Stop the transfer loop and join its thread
    /// </summary>
    /// <param name="handle">Interface handle</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_Stop(IntPtr handle);

    /// <summary>
    /// Queue a message for transmission
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="flags">Message type flags</param>
    /// <param name="message">UTF-8 message content</param>
    /// <param name="length">Length of <paramref name="message"/> in bytes</param>
    /// <returns>Zero on success, non-zero if the outbound ring is full</returns>
    [LibraryImport(LibraryName)]
    internal static partial long DuetSbc_QueueMessage(IntPtr handle, uint flags, ReadOnlySpan<byte> message, int length);

    /// <summary>
    /// Queue a CAN message for transmission
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="txToken">Token used to map the response back to the request</param>
    /// <param name="msgType">CAN message type</param>
    /// <param name="replyType">Expected reply type</param>
    /// <param name="dstAddress">CAN destination address</param>
    /// <param name="isResponse">Whether this message is a response</param>
    /// <param name="payload">CAN payload</param>
    /// <param name="length">Length of <paramref name="payload"/></param>
    /// <returns>Zero on success, non-zero if the outbound ring is full</returns>
    [LibraryImport(LibraryName)]
    internal static partial long DuetSbc_QueueCanMessage(IntPtr handle, ushort txToken, ushort msgType,
        ushort replyType, byte dstAddress, int isResponse, ReadOnlySpan<byte> payload, int length);

    /// <summary>
    /// Queue a CAN enable/disable request
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="enable">Whether to enable the CAN bus</param>
    /// <param name="requestId">Request id to report completion against, or 0 for fire-and-forget</param>
    /// <returns>Zero on success, non-zero if the outbound ring is full</returns>
    [LibraryImport(LibraryName)]
    internal static partial long DuetSbc_QueueEnableCan(IntPtr handle, int enable, uint requestId);

    /// <summary>
    /// Request an immediate emergency stop
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="requestId">Request id to report completion against</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_RequestEmergencyStop(IntPtr handle, uint requestId);

    /// <summary>
    /// Request a firmware reset
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="requestId">Request id to report completion against</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_RequestReset(IntPtr handle, uint requestId);

    /// <summary>
    /// Stage a firmware update. Both buffers must stay pinned until the completion event arrives
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="iap">IAP binary</param>
    /// <param name="iapLength">Length of the IAP binary</param>
    /// <param name="firmware">Firmware binary</param>
    /// <param name="firmwareLength">Length of the firmware binary</param>
    /// <param name="firmwareCrc16">CRC16 of the firmware binary</param>
    /// <param name="requestId">Request id to report completion against</param>
    /// <returns>Zero on success, non-zero if an update is already running</returns>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_RequestFirmwareUpdate(IntPtr handle, IntPtr iap, int iapLength,
        IntPtr firmware, int firmwareLength, ushort firmwareCrc16, uint requestId);

    /// <summary>
    /// Ask the transfer loop to start a transfer without new data
    /// </summary>
    /// <param name="handle">Interface handle</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_RequestTransfer(IntPtr handle);

    /// <summary>
    /// Point at the next inbound event record without copying it
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="data">Receives a pointer into the native ring, valid until the next consume</param>
    /// <param name="length">Receives the record length in bytes</param>
    /// <returns>1 if an event is available, 0 otherwise</returns>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_PeekEvent(IntPtr handle, out IntPtr data, out int length);

    /// <summary>
    /// Release the event most recently returned by <see cref="DuetSbc_PeekEvent"/>
    /// </summary>
    /// <param name="handle">Interface handle</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_ConsumeEvent(IntPtr handle);

    /// <summary>
    /// Block until an inbound event is available, the timeout elapses, or the loop stops
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="timeoutMs">Maximum time to wait in ms</param>
    /// <returns>1 if an event is probably available, 0 on timeout</returns>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_WaitForEvent(IntPtr handle, int timeoutMs);

    /// <summary>
    /// Get the negotiated protocol version
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <returns>Protocol version</returns>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_GetProtocolVersion(IntPtr handle);

    /// <summary>
    /// Get and reset the maximum TfrRdy pin wait time
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <returns>Time in ms</returns>
    [LibraryImport(LibraryName)]
    internal static partial double DuetSbc_GetMaxPinWaitMs(IntPtr handle);

    /// <summary>
    /// Get and reset the maximum time between two full transfers
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <returns>Time in ms</returns>
    [LibraryImport(LibraryName)]
    internal static partial double DuetSbc_GetMaxFullTransferDelayMs(IntPtr handle);

    /// <summary>
    /// Get the number of observed TfrRdy pin glitches
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <returns>Glitch count</returns>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_GetTfrPinGlitches(IntPtr handle);

    /// <summary>
    /// Get the number of missed GPIO edges
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <returns>Missed edge count</returns>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_GetMissedEdges(IntPtr handle);

    /// <summary>
    /// Get the number of connection resyncs performed after an error
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <returns>Resync count</returns>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_GetResyncCount(IntPtr handle);

    /// <summary>
    /// Get the number of events dropped because the inbound ring was full
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <returns>Dropped event count</returns>
    [LibraryImport(LibraryName)]
    internal static partial ulong DuetSbc_GetDroppedEvents(IntPtr handle);

    /// <summary>
    /// The current step-clock reading, in the controller's ticks
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <returns>Step clock ticks</returns>
    /// <remarks>
    /// The SBC has no step clock of its own: it models the controller's, from the MasterClock reading the SPI transfer header carries
    /// the controller sends every transfer. Move start times are in that timebase
    /// </remarks>
    [LibraryImport(LibraryName)]
    internal static partial uint DuetSbc_GetStepClockTicks(IntPtr handle);

    /// <summary>
    /// How far the movement timebase lags the raw step clock, in ticks
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <returns>The delay in step clock ticks</returns>
    /// <remarks>
    /// Moves are scheduled in the movement timebase and an endstop reports its trigger in the raw
    /// one, so this is the difference the endstop correction has to reconcile. It only ever grows,
    /// and it grows whenever a board reports that it could not keep up
    /// </remarks>
    [LibraryImport(LibraryName)]
    internal static partial uint DuetSbc_GetMovementDelay(IntPtr handle);

    /// <summary>
    /// How well the step-clock model is tracking the controller
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="stats">Receives the statistics</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_GetClockStats(IntPtr handle, out NativeClockStats stats);

    /// <summary>
    /// What the motion engine has done since the counters were last reset
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="stats">Receives the statistics</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_MotionGetStats(IntPtr handle, out NativeMotionStats stats);

    /// <summary>
    /// Zero the motion engine's error and underrun counters
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <remarks>
    /// Separate from reading them so that reporting twice does not show zeros the second time, which
    /// is what the native side used to do by reporting and zeroing in one call
    /// </remarks>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_MotionResetStats(IntPtr handle);

    /// <summary>
    /// Push the machine description down to the motion engine
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="config">Serialised MachineConfig</param>
    /// <param name="length">Length of <paramref name="config"/> in bytes</param>
    /// <returns>1 on success, 0 if the length did not match</returns>
    /// <remarks>Safe only while no move is in flight</remarks>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_MotionConfigure(IntPtr handle, ReadOnlySpan<byte> config, int length);

    /// <summary>
    /// Start the motion thread
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="rtPriority">SCHED_FIFO priority, or 0 for the default scheduler</param>
    /// <returns>1 on success</returns>
    /// <remarks>
    /// The priority must stay below the interface thread's: a late transfer loses the link, while a
    /// late move preparation only costs a hiccup that every board slips by together
    /// </remarks>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_MotionStart(IntPtr handle, int rtPriority);

    /// <summary>
    /// Stop the motion thread
    /// </summary>
    /// <param name="handle">Interface handle</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_MotionStop(IntPtr handle);

    /// <summary>
    /// Whether the given ring has room for another move
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="ring">Ring number</param>
    /// <returns>1 if there is room</returns>
    /// <remarks>Advisory: the ring may retire a move and make room a moment later</remarks>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_MotionCanAddMove(IntPtr handle, int ring);

    /// <summary>
    /// Queue a move
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="moveParams">A MoveParamsHeader followed by its two arrays</param>
    /// <param name="length">Length of <paramref name="moveParams"/> in bytes</param>
    /// <returns>1 if queued, 0 if the caller must retry</returns>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_MotionSubmitMove(IntPtr handle, ReadOnlySpan<byte> moveParams, int length);

    /// <summary>
    /// Read the motor positions the motion engine last published
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="steps">Receives the positions in microsteps</param>
    /// <param name="count">Capacity of <paramref name="steps"/></param>
    /// <param name="whenTicks">Receives the step-clock time the snapshot was taken at</param>
    /// <returns>Number of positions written</returns>
    /// <remarks>
    /// Reads a snapshot rather than the live state, so a garbage collection here cannot stall the
    /// motion thread and the values never tear
    /// </remarks>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_MotionGetMotorPositions(IntPtr handle, Span<int> steps, int count, out uint whenTicks);
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_MotionGetLivePositions(IntPtr handle, Span<int> steps, int count, out uint whenTicks);

    /// <summary>
    /// Where one drive was at a given step-clock time, and where it was when its move began
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="drive">Logical drive</param>
    /// <param name="whenTicks">Master step-clock time to evaluate at, zero if none was reported</param>
    /// <param name="position">Receives the position in microsteps</param>
    /// <param name="positionAtMoveStart">Receives where the drive was when its current move began</param>
    /// <param name="usedTimestamp">Receives 1 if the answer came from <paramref name="whenTicks"/></param>
    /// <returns>1 on success, 0 if the drive is out of range</returns>
    /// <remarks>
    /// The one question only the native side can answer: it planned the motion and holds the segment
    /// chain, so it can evaluate the profile at an instant that has already passed. Undoing an endstop
    /// overshoot needs the position at the moment the switch fired, not the one the stop report caught
    /// </remarks>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_MotionGetPositionAt(IntPtr handle, int drive, uint whenTicks,
                                                            out int position, out int positionAtMoveStart,
                                                            out int usedTimestamp);

    /// <summary>
    /// Force motor positions, after homing or a move that stopped early
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="driveMask">Logical drives to set</param>
    /// <param name="positions">Positions in microsteps</param>
    /// <param name="count">Number of entries in <paramref name="positions"/></param>
    /// <returns>Non-zero if the engine took it, zero if its queue was full</returns>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_MotionSetMotorPositions(IntPtr handle, uint driveMask, ReadOnlySpan<int> positions, int count);

    /// <summary>
    /// Ask the engine to bring the machine to a controlled stop and drop the moves after it
    /// </summary>
    /// <param name="handle">Engine handle</param>
    /// <returns>Non-zero if the request was queued, zero if its queue was full</returns>
    /// <remarks>
    /// The answer does not come back from this call: dropping a move frees its segments and only the
    /// motion thread may do that. <see cref="DuetSbc_MotionGetFeedholdResult"/> reports what happened
    /// </remarks>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_MotionRequestFeedhold(IntPtr handle);

    /// <summary>
    /// What the last feedhold did
    /// </summary>
    /// <param name="handle">Engine handle</param>
    /// <param name="sequence">Receives the number of completed feedholds</param>
    /// <param name="firstPurgedMoveId">Receives the id of the earliest move dropped</param>
    /// <param name="movesPurged">Receives how many moves were dropped</param>
    /// <param name="stopped">Receives non-zero if the ring was brought to a planned stop</param>
    /// <returns>Non-zero on success</returns>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_MotionGetFeedholdResult(IntPtr handle, out uint sequence,
                                                                out uint firstPurgedMoveId, out uint movesPurged,
                                                                out int stopped);

    /// <summary>
    /// Store the ring state this side decides from its own bookkeeping
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="ring">Ring number</param>
    /// <param name="shouldStartMove">Whether queued moves should start executing</param>
    /// <param name="waitingForEmpty">Whether this side is waiting for the ring to drain</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_MotionSetRingState(IntPtr handle, int ring, int shouldStartMove, int waitingForEmpty);

    /// <summary>
    /// Number of moves the given ring has been given
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="ring">Ring number</param>
    /// <returns>Scheduled move count</returns>
    [LibraryImport(LibraryName)]
    internal static partial uint DuetSbc_MotionGetScheduledMoves(IntPtr handle, int ring);

    /// <summary>
    /// Number of moves the given ring has finished
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="ring">Ring number</param>
    /// <returns>Completed move count</returns>
    [LibraryImport(LibraryName)]
    internal static partial uint DuetSbc_MotionGetCompletedMoves(IntPtr handle, int ring);

    /// <summary>
    /// Submissions refused because the queue was full
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <returns>Dropped submission count</returns>
    /// <remarks>Non-zero means a retry was skipped: a move was lost</remarks>
    [LibraryImport(LibraryName)]
    internal static partial uint DuetSbc_MotionGetSubmissionsDropped(IntPtr handle);

    /// <summary>
    /// Forced positions the motion thread has adopted
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <returns>Applied position count</returns>
    /// <remarks>
    /// Lagging behind what this side has sent means a position was queued but has not taken effect
    /// </remarks>
    [LibraryImport(LibraryName)]
    internal static partial uint DuetSbc_MotionGetForcedPositionsApplied(IntPtr handle);

    /// <summary>
    /// Whether a submitted move has not yet been taken up by the motion thread
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <returns>Non-zero while a submission is still queued</returns>
    /// <remarks>
    /// A ring counts a move as scheduled only once the motion thread has taken it out of the
    /// submission queue, so the rings alone report a machine as idle while a move is on its way to it
    /// </remarks>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_MotionHasPendingSubmissions(IntPtr handle);

    /// <summary>
    /// Stop the loop and destroy the instance
    /// </summary>
    /// <param name="handle">Interface handle</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_Destroy(IntPtr handle);
}
