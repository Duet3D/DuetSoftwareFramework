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
    internal static partial int DuetSbc_QueueMessage(IntPtr handle, uint flags, ReadOnlySpan<byte> message, int length);

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
    internal static partial int DuetSbc_QueueCanMessage(IntPtr handle, ushort txToken, ushort msgType,
        ushort replyType, byte dstAddress, int isResponse, ReadOnlySpan<byte> payload, int length);

    /// <summary>
    /// Queue a CAN enable/disable request
    /// </summary>
    /// <param name="handle">Interface handle</param>
    /// <param name="enable">Whether to enable the CAN bus</param>
    /// <param name="requestId">Request id to report completion against, or 0 for fire-and-forget</param>
    /// <returns>Zero on success, non-zero if the outbound ring is full</returns>
    [LibraryImport(LibraryName)]
    internal static partial int DuetSbc_QueueEnableCan(IntPtr handle, int enable, uint requestId);

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
    /// Stop the loop and destroy the instance
    /// </summary>
    /// <param name="handle">Interface handle</param>
    [LibraryImport(LibraryName)]
    internal static partial void DuetSbc_Destroy(IntPtr handle);
}
