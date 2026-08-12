using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DuetControlServer.Motion.Native;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetControlServer.Link.Protocol.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DuetControlServer.Link.Native;

/// <summary>
/// Managed wrapper around <c>libduet_sbc.so</c>, the native SPI transfer loop
/// </summary>
/// <remarks>
/// <para>
/// The SPI protocol lives in C++ (see <c>src/DuetSbcInterface</c>) so its transfer loop can run pinned
/// and <c>SCHED_FIFO</c> without a managed runtime scheduling work onto that thread. This class is the
/// only place that talks to it. Work crosses the boundary through lock-free ring buffers rather than
/// callbacks, so the real-time thread never executes managed code and a GC pause can never stall a
/// transfer in flight.
/// </para>
/// <para>
/// Outgoing work is pushed with the <c>Queue*</c> methods from any thread. Incoming events are drained
/// by exactly one consumer -- the dispatcher thread owned by <see cref="LinkService"/> -- via
/// <see cref="WaitForEvent"/> and <see cref="TryReadEvent"/>.
/// </para>
/// <para>
/// Requests the caller awaits (emergency stop, reset, CAN enable, firmware update) are given an id;
/// the native loop reports the outcome with a matching completion event, which
/// <see cref="CompleteRequest"/> resolves back onto the originating <see cref="TaskCompletionSource"/>.
/// </para>
/// </remarks>
/// <param name="logger">Logger instance</param>
/// <param name="settings">Settings</param>
public sealed class NativeLink(ILogger<NativeLink> logger, IOptions<Settings> settings) : IDisposable
{
    /// <summary>
    /// Size of the buffer used to receive error messages from the native side
    /// </summary>
    private const int ErrorBufferSize = 512;

    /// <summary>
    /// Native interface handle
    /// </summary>
    private IntPtr _handle = IntPtr.Zero;

    /// <summary>
    /// Whether the transfer loop has been started
    /// </summary>
    private bool _started;

    /// <summary>
    /// Pending requests awaiting a completion event, keyed by request id
    /// </summary>
    private readonly ConcurrentDictionary<uint, TaskCompletionSource> _pendingRequests = new();

    /// <summary>
    /// Source of request ids. Id 0 is skipped because it means "fire and forget"
    /// </summary>
    private int _nextRequestId;

    /// <summary>
    /// Buffers pinned for the duration of a firmware update, released once it completes
    /// </summary>
    private readonly List<GCHandle> _pinnedUpdateBuffers = [];

    /// <summary>
    /// Protocol version reported by the firmware
    /// </summary>
    public int ProtocolVersion { get; private set; }

    /// <summary>
    /// Verify that the managed struct layouts still match the native ones
    /// </summary>
    /// <remarks>
    /// The ring records are reinterpreted rather than marshalled, so a drift between
    /// <c>LinkEvents.h</c> and <c>LinkEvents.cs</c> would silently corrupt every event instead of
    /// failing loudly. This turns that into a startup error
    /// </remarks>
    /// <exception cref="InvalidOperationException">A layout does not match</exception>
    private static void VerifyLayouts()
    {
        static void Check<T>(int expected) where T : struct
        {
            int actual = Marshal.SizeOf<T>();
            if (actual != expected)
            {
                throw new InvalidOperationException($"Layout mismatch: {typeof(T).Name} is {actual} bytes, native side expects {expected}. LinkEvents.cs and LinkEvents.h are out of sync");
            }
        }

        Check<InboundEventHeader>(4);
        Check<MessageEvent>(8);
        Check<CanResponseEvent>(16);
        Check<CodeBufferEvent>(8);
        Check<ConnectionEstablishedEvent>(8);
        Check<OutboundSeqEvent>(8);
        Check<RequestCompletedEvent>(12);
        Check<LogEvent>(8);
        Check<MalformedPacketEvent>(12);
        Check<MoveCompletedEvent>(16);
        Check<MoveFailedEvent>(12);
        Check<MotionStoppedEvent>(12);
        Check<MotionStoppedDriverEntry>(4);
        Check<MoveParamsHeader>(28);
    }

    /// <summary>
    /// Create the native interface and connect to the firmware
    /// </summary>
    /// <exception cref="InvalidOperationException">Failed to create or connect</exception>
    public void Connect()
    {
        VerifyLayouts();

        // The config carries raw UTF-8 pointers, so they must stay alive across the Create call
        IntPtr spiDevice = Marshal.StringToCoTaskMemUTF8(settings.Value.SpiDevice);
        IntPtr gpioChipDevice = Marshal.StringToCoTaskMemUTF8(settings.Value.GpioChipDevice);
        try
        {
            NativeConfig config = new()
            {
                SpiDevice = spiDevice,
                SpiFrequency = (uint)settings.Value.SpiFrequency,
                SpiTransferMode = settings.Value.SpiTransferMode,
                BufferSize = settings.Value.SbcBufferSize,
                GpioChipDevice = gpioChipDevice,
                TransferReadyPin = settings.Value.TransferReadyPin,
                DataAvailablePin = settings.Value.DataAvailablePin,
                // The scope-trigger output line is a debug aid only
#if DEBUG
                SbcDataAvailablePin = settings.Value.SbcDataAvailablePin,
#else
                SbcDataAvailablePin = -1,
#endif
                IsolateInterfaceThread = settings.Value.IsolateInterfaceThread ? 1 : 0,
                IsolatedCoreId = settings.Value.IsolatedCoreId,
                UseRealtimeScheduling = settings.Value.UseRealtimeScheduling ? 1 : 0,
                InterfaceRtPriority = settings.Value.InterfaceRtPriority,
                SbcConnectTimeout = settings.Value.SbcConnectTimeout,
                SbcTransferTimeout = settings.Value.SbcTransferTimeout,
                SbcConnectionTimeout = settings.Value.SbcConnectionTimeout,
                SbcConnectionKeepAliveInterval = settings.Value.SbcConnectionKeepAliveInterval,
                MaxSbcRetries = settings.Value.MaxSbcRetries,
                UpdateOnly = settings.Value.UpdateOnly ? 1 : 0
            };

            byte[] errorBuffer = new byte[ErrorBufferSize];
            _handle = NativeMethods.DuetSbc_Create(ref config, errorBuffer, errorBuffer.Length);
            if (_handle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Failed to create native SPI interface: {ReadError(errorBuffer)}");
            }

            Array.Clear(errorBuffer);
            if (NativeMethods.DuetSbc_Connect(_handle, errorBuffer, errorBuffer.Length) != 0)
            {
                string error = ReadError(errorBuffer);
                NativeMethods.DuetSbc_Destroy(_handle);
                _handle = IntPtr.Zero;
                throw new InvalidOperationException($"Failed to connect to controller over SPI: {error}");
            }

            ProtocolVersion = NativeMethods.DuetSbc_GetProtocolVersion(_handle);
            logger.LogInformation("Connected to controller over SPI (protocol version {ProtocolVersion})", ProtocolVersion);
        }
        finally
        {
            Marshal.FreeCoTaskMem(spiDevice);
            Marshal.FreeCoTaskMem(gpioChipDevice);
        }
    }

    /// <summary>
    /// Start the native transfer loop on its own real-time thread
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();
        NativeMethods.DuetSbc_Start(_handle);
        _started = true;
    }

    /// <summary>
    /// Stop the native transfer loop
    /// </summary>
    public void Stop()
    {
        if (_handle != IntPtr.Zero && _started)
        {
            NativeMethods.DuetSbc_Stop(_handle);
            _started = false;
        }

        // Nothing more will be served, so fail anything still waiting rather than leaving it hanging
        foreach (var kv in _pendingRequests)
        {
            kv.Value.TrySetCanceled();
        }
        _pendingRequests.Clear();
        ReleasePinnedBuffers();
    }

    /// <summary>
    /// Read a NUL-terminated UTF-8 error message out of a native error buffer
    /// </summary>
    /// <param name="buffer">Buffer to read</param>
    /// <returns>Error message</returns>
    private static string ReadError(byte[] buffer)
    {
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }
        return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : "unknown error";
    }

    /// <summary>
    /// Throw if the native interface is not available
    /// </summary>
    /// <exception cref="ObjectDisposedException">Interface has been disposed</exception>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
    }

    #region Outbound
    /// <summary>
    /// Queue a message for transmission to the firmware
    /// </summary>
    /// <param name="flags">Message type flags</param>
    /// <param name="message">Message content</param>
    /// <exception cref="InvalidOperationException">The outbound ring is full</exception>
    public void QueueMessage(MessageTypeFlags flags, string message)
    {
        ThrowIfDisposed();
        byte[] encoded = Encoding.UTF8.GetBytes(message);
        if (NativeMethods.DuetSbc_QueueMessage(_handle, (uint)flags, encoded, encoded.Length) < 0)
        {
            // The transfer loop is not draining the ring, so the message would be silently lost
            throw new InvalidOperationException("Failed to queue message: native outbound buffer is full");
        }
    }

    /// <summary>
    /// Queue a CAN message for transmission to an expansion board
    /// </summary>
    /// <param name="txToken">Token used to map the response back to the request</param>
    /// <param name="msgType">CAN message type to place in the CAN id</param>
    /// <param name="replyType">Expected reply type</param>
    /// <param name="dstAddress">CAN destination: 0..126, or 127 for broadcast</param>
    /// <param name="isResponse">Whether this message is a response</param>
    /// <param name="payload">CAN payload (0..64 bytes)</param>
    /// <returns>Sequence number to wait on with <see cref="WaitForDeliveryAsync"/></returns>
    /// <exception cref="InvalidOperationException">The outbound ring is full</exception>
    public uint QueueCanMessage(ushort txToken, ushort msgType, ushort replyType, byte dstAddress, bool isResponse, ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();
        long sequenceNumber = NativeMethods.DuetSbc_QueueCanMessage(_handle, txToken, msgType, replyType, dstAddress, isResponse ? 1 : 0, payload, payload.Length);
        if (sequenceNumber < 0)
        {
            throw new InvalidOperationException("Failed to queue CAN message: native outbound buffer is full");
        }
        return (uint)sequenceNumber;
    }

    /// <summary>
    /// Ask the transfer loop to start a transfer without new data
    /// </summary>
    public void RequestTransfer()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.DuetSbc_RequestTransfer(_handle);
        }
    }

    /// <summary>
    /// Allocate a request id and register a completion source against it
    /// </summary>
    /// <param name="tcs">Completion source to resolve when the native loop reports the outcome</param>
    /// <returns>Request id</returns>
    private uint RegisterRequest(TaskCompletionSource tcs)
    {
        // Skip 0, which the native side reserves for fire-and-forget commands. The wrap-around after
        // 2^32 requests is harmless: any id that old has long since been completed and removed.
        uint id;
        do
        {
            id = unchecked((uint)Interlocked.Increment(ref _nextRequestId));
        }
        while (id == LinkEventLayout.NoRequestId || !_pendingRequests.TryAdd(id, tcs));
        return id;
    }

    /// <summary>
    /// Request an immediate emergency stop and wait for it to be sent
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        uint requestId = RegisterRequest(tcs);
        NativeMethods.DuetSbc_RequestEmergencyStop(_handle, requestId);
        await AwaitRequestAsync(requestId, tcs, cancellationToken);
    }

    /// <summary>
    /// Request a firmware reset and wait for it to be sent
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        uint requestId = RegisterRequest(tcs);
        NativeMethods.DuetSbc_RequestReset(_handle, requestId);
        await AwaitRequestAsync(requestId, tcs, cancellationToken);
    }

    /// <summary>
    /// Enable or disable the CAN bus and wait for the request to be sent
    /// </summary>
    /// <param name="enable">Whether to enable the CAN bus</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    public async Task EnableCanAsync(bool enable, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        uint requestId = RegisterRequest(tcs);
        if (NativeMethods.DuetSbc_QueueEnableCan(_handle, enable ? 1 : 0, requestId) < 0)
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw new InvalidOperationException("Failed to queue CAN enable request: native outbound buffer is full");
        }
        await AwaitRequestAsync(requestId, tcs, cancellationToken);
    }

    /// <summary>
    /// Perform a firmware update via IAP and wait for it to finish
    /// </summary>
    /// <remarks>
    /// The native loop takes over for the duration of the flash. Both binaries are pinned for that
    /// time because the native side keeps raw pointers to them
    /// </remarks>
    /// <param name="iap">IAP binary</param>
    /// <param name="firmware">Firmware binary</param>
    /// <param name="firmwareCrc16">CRC16 checksum of the firmware binary</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="InvalidOperationException">An update is already in progress</exception>
    public async Task UpdateFirmwareAsync(byte[] iap, byte[] firmware, ushort firmwareCrc16, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        uint requestId = RegisterRequest(tcs);

        GCHandle iapHandle = GCHandle.Alloc(iap, GCHandleType.Pinned);
        GCHandle firmwareHandle = GCHandle.Alloc(firmware, GCHandleType.Pinned);
        lock (_pinnedUpdateBuffers)
        {
            _pinnedUpdateBuffers.Add(iapHandle);
            _pinnedUpdateBuffers.Add(firmwareHandle);
        }

        try
        {
            if (NativeMethods.DuetSbc_RequestFirmwareUpdate(_handle, iapHandle.AddrOfPinnedObject(), iap.Length,
                    firmwareHandle.AddrOfPinnedObject(), firmware.Length, firmwareCrc16, requestId) != 0)
            {
                _pendingRequests.TryRemove(requestId, out _);
                throw new InvalidOperationException("Firmware is already being updated");
            }

            // A flash must not be interrupted, so the cancellation token is deliberately not honoured
            // here: aborting mid-write would leave the board needing manual recovery
            await tcs.Task;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
            ReleasePinnedBuffers();
        }
    }

    /// <summary>
    /// Release any buffers pinned for a firmware update
    /// </summary>
    private void ReleasePinnedBuffers()
    {
        lock (_pinnedUpdateBuffers)
        {
            foreach (GCHandle handle in _pinnedUpdateBuffers)
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }
            _pinnedUpdateBuffers.Clear();
        }
    }

    /// <summary>
    /// Await a registered request, removing it again if the wait is cancelled
    /// </summary>
    /// <param name="requestId">Request id</param>
    /// <param name="tcs">Completion source</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    private async Task AwaitRequestAsync(uint requestId, TaskCompletionSource tcs, CancellationToken cancellationToken)
    {
        try
        {
            await tcs.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Resolve a pending request from a completion event
    /// </summary>
    /// <param name="requestId">Request id reported by the native loop</param>
    /// <param name="result">Outcome</param>
    /// <param name="error">Error message if the request failed</param>
    internal void CompleteRequest(uint requestId, RequestResult result, string? error)
    {
        if (!_pendingRequests.TryRemove(requestId, out TaskCompletionSource? tcs))
        {
            // Already cancelled or timed out on the managed side
            return;
        }

        switch (result)
        {
            case RequestResult.Success:
                tcs.TrySetResult();
                break;
            case RequestResult.Cancelled:
                tcs.TrySetCanceled();
                break;
            default:
                tcs.TrySetException(new InvalidOperationException(string.IsNullOrEmpty(error) ? "Request failed" : error));
                break;
        }
    }

    /// <summary>
    /// Commands waiting to hear that they reached the controller, by sequence number
    /// </summary>
    private readonly SortedDictionary<uint, TaskCompletionSource> _outboundWaiters = [];

    /// <summary>
    /// Wait until a queued command has reached the controller
    /// </summary>
    /// <param name="sequenceNumber">Sequence number the command was given when it was queued</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Asynchronous task</returns>
    /// <exception cref="OperationCanceledException">The command was dropped instead</exception>
    internal Task WaitForDeliveryAsync(uint sequenceNumber, CancellationToken cancellationToken)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_outboundWaiters)
        {
            if (sequenceNumber <= _deliveredSequenceNumber)
            {
                return Task.CompletedTask;
            }
            _outboundWaiters[sequenceNumber] = tcs;
        }
        return tcs.Task.WaitAsync(cancellationToken);
    }

    private uint _deliveredSequenceNumber;

    /// <summary>
    /// Resolve everything the controller has taken, or everything that was abandoned
    /// </summary>
    /// <param name="sequenceNumber">Last command this applies to</param>
    /// <param name="delivered">Whether the commands reached the controller</param>
    internal void CompleteOutbound(uint sequenceNumber, bool delivered)
    {
        List<TaskCompletionSource> completed = [];
        lock (_outboundWaiters)
        {
            if (delivered)
            {
                _deliveredSequenceNumber = sequenceNumber;
            }

            // Sorted by sequence number, so everything this covers is at the front
            List<uint> keys = [];
            foreach (var kv in _outboundWaiters)
            {
                if (kv.Key > sequenceNumber)
                {
                    break;
                }
                keys.Add(kv.Key);
                completed.Add(kv.Value);
            }
            foreach (uint key in keys)
            {
                _outboundWaiters.Remove(key);
            }
        }

        foreach (TaskCompletionSource tcs in completed)
        {
            if (delivered)
            {
                tcs.TrySetResult();
            }
            else
            {
                tcs.TrySetCanceled();
            }
        }
    }

    /// <summary>
    /// Cancel every pending request, e.g. because the connection was lost
    /// </summary>
    internal void CancelPendingRequests()
    {
        foreach (var kv in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kv.Key, out TaskCompletionSource? tcs))
            {
                tcs.TrySetCanceled();
            }
        }
    }
    #endregion

    #region Inbound
    /// <summary>
    /// Block until an inbound event is available or the timeout elapses
    /// </summary>
    /// <remarks>Single-consumer: only the dispatcher thread may call this</remarks>
    /// <param name="timeoutMs">Maximum time to wait in ms</param>
    /// <returns>True if an event is probably available</returns>
    internal bool WaitForEvent(int timeoutMs)
    {
        return _handle != IntPtr.Zero && NativeMethods.DuetSbc_WaitForEvent(_handle, timeoutMs) != 0;
    }

    /// <summary>
    /// Read the next inbound event without copying it
    /// </summary>
    /// <remarks>
    /// Single-consumer: only the dispatcher thread may call this. The returned span points straight
    /// into the native ring and is only valid until <see cref="ConsumeEvent"/> is called
    /// </remarks>
    /// <param name="record">Receives the event record</param>
    /// <returns>True if an event was available</returns>
    internal unsafe bool TryReadEvent(out ReadOnlySpan<byte> record)
    {
        if (_handle != IntPtr.Zero && NativeMethods.DuetSbc_PeekEvent(_handle, out IntPtr data, out int length) != 0)
        {
            record = new ReadOnlySpan<byte>(data.ToPointer(), length);
            return true;
        }
        record = default;
        return false;
    }

    /// <summary>
    /// Release the event most recently returned by <see cref="TryReadEvent"/>
    /// </summary>
    internal void ConsumeEvent()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.DuetSbc_ConsumeEvent(_handle);
        }
    }
    #endregion

    #region Diagnostics
    /// <summary>
    /// Get and reset the maximum time between two full transfers
    /// </summary>
    /// <returns>Time in ms</returns>
    public double GetMaxFullTransferDelay() => _handle != IntPtr.Zero ? NativeMethods.DuetSbc_GetMaxFullTransferDelayMs(_handle) : 0;

    /// <summary>
    /// Get and reset the maximum TfrRdy pin wait time
    /// </summary>
    /// <returns>Time in ms</returns>
    public double GetMaxPinWaitDuration() => _handle != IntPtr.Zero ? NativeMethods.DuetSbc_GetMaxPinWaitMs(_handle) : 0;

    /// <summary>
    /// Number of observed TfrRdy pin glitches
    /// </summary>
    public int TfrPinGlitches => _handle != IntPtr.Zero ? NativeMethods.DuetSbc_GetTfrPinGlitches(_handle) : 0;

    /// <summary>
    /// Number of missed GPIO edges
    /// </summary>
    public int MissedEdges => _handle != IntPtr.Zero ? NativeMethods.DuetSbc_GetMissedEdges(_handle) : 0;

    /// <summary>
    /// Number of connection resyncs performed after an error
    /// </summary>
    public int ResyncCount => _handle != IntPtr.Zero ? NativeMethods.DuetSbc_GetResyncCount(_handle) : 0;

    /// <summary>
    /// Number of events dropped because the inbound ring was full
    /// </summary>
    public ulong DroppedEvents => _handle != IntPtr.Zero ? NativeMethods.DuetSbc_GetDroppedEvents(_handle) : 0;

    /// <summary>
    /// The controller's step clock, as the native side models it
    /// </summary>
    /// <remarks>
    /// The SBC has no step clock of its own. Move start times are in this timebase, so a model that
    /// has drifted schedules moves that arrive late
    /// </remarks>
    public uint StepClockTicks => _handle != IntPtr.Zero ? NativeMethods.DuetSbc_GetStepClockTicks(_handle) : 0;

    /// <summary>
    /// How far the movement timebase lags the raw step clock, in ticks
    /// </summary>
    /// <returns>The delay, or null if the loaded library does not report it</returns>
    /// <remarks>
    /// Moves are scheduled in the movement timebase and an endstop reports its trigger in the raw
    /// one. Anything other than zero here is the gap the endstop correction has to reconcile, and it
    /// is the one part of the clock that grows silently: every board slips by the same amount so
    /// nothing about the motion looks wrong
    /// </remarks>
    public uint? GetMovementDelay()
    {
        if (_handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return NativeMethods.DuetSbc_GetMovementDelay(_handle);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// How well the step-clock model is tracking the controller
    /// </summary>
    /// <returns>The statistics, or a zeroed struct if the link is not up</returns>
    public NativeClockStats GetClockStats()
    {
        if (_handle == IntPtr.Zero)
        {
            return default;
        }
        NativeMethods.DuetSbc_GetClockStats(_handle, out NativeClockStats stats);
        return stats;
    }

    /// <summary>
    /// Push the machine description down to the native motion engine
    /// </summary>
    /// <param name="config">Serialised MotionConfig</param>
    /// <returns>True if it was accepted</returns>
    /// <remarks>Safe only while no move is in flight</remarks>
    public bool ConfigureMotion(ReadOnlySpan<byte> config)
        => _handle != IntPtr.Zero && NativeMethods.DuetSbc_MotionConfigure(_handle, config, config.Length) != 0;

    /// <summary>
    /// Start the native motion thread
    /// </summary>
    /// <param name="rtPriority">SCHED_FIFO priority, or 0 for the default scheduler</param>
    /// <returns>True if it started</returns>
    public bool StartMotion(int rtPriority)
        => _handle != IntPtr.Zero && NativeMethods.DuetSbc_MotionStart(_handle, rtPriority) != 0;

    /// <summary>
    /// Stop the native motion thread
    /// </summary>
    public void StopMotion()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.DuetSbc_MotionStop(_handle);
        }
    }

    /// <summary>
    /// Whether the given ring has room for another move
    /// </summary>
    /// <param name="ring">Ring number</param>
    /// <returns>True if there is room</returns>
    public bool CanAddMove(int ring)
        => _handle != IntPtr.Zero && NativeMethods.DuetSbc_MotionCanAddMove(_handle, ring) != 0;

    /// <summary>
    /// Queue a move
    /// </summary>
    /// <param name="moveParams">A MoveParamsHeader followed by its two arrays</param>
    /// <returns>True if queued, false if the caller must retry</returns>
    public bool SubmitMove(ReadOnlySpan<byte> moveParams)
        => _handle != IntPtr.Zero && NativeMethods.DuetSbc_MotionSubmitMove(_handle, moveParams, moveParams.Length) != 0;

    /// <summary>
    /// Read the motor positions the motion engine last published
    /// </summary>
    /// <param name="steps">Receives the positions in microsteps</param>
    /// <param name="whenTicks">Receives the step-clock time the snapshot was taken at</param>
    /// <returns>Number of positions written</returns>
    public int GetMotorPositions(Span<int> steps, out uint whenTicks)
    {
        if (_handle == IntPtr.Zero)
        {
            whenTicks = 0;
            return 0;
        }
        return NativeMethods.DuetSbc_MotionGetMotorPositions(_handle, steps, steps.Length, out whenTicks);
    }

    /// <summary>
    /// Where the drives are now, interpolated within the segment each is running
    /// </summary>
    /// <param name="steps">Receives the positions in microsteps</param>
    /// <param name="whenTicks">Receives the step-clock time the snapshot was taken at</param>
    /// <returns>Number of positions written</returns>
    /// <remarks>
    /// <see cref="GetMotorPositions"/> reports what the drives were <em>commanded</em> to, which only
    /// advances as each segment of a move retires - so a trapezoidal move moves it three times, once
    /// per phase. That is the right answer for resynchronising the planner and the wrong one for a
    /// position display, which is what this is for
    /// </remarks>
    public int GetLivePositions(Span<int> steps, out uint whenTicks)
    {
        if (_handle == IntPtr.Zero)
        {
            whenTicks = 0;
            return 0;
        }
        return NativeMethods.DuetSbc_MotionGetLivePositions(_handle, steps, steps.Length, out whenTicks);
    }

    /// <summary>
    /// Where one drive was at a given step-clock time
    /// </summary>
    /// <param name="drive">Logical drive</param>
    /// <param name="whenTicks">Master step-clock time to evaluate at, zero if none was reported</param>
    /// <param name="position">Receives the position in microsteps</param>
    /// <param name="positionAtMoveStart">Receives where the drive was when its current move began</param>
    /// <param name="usedTimestamp">
    /// Receives whether the answer came from <paramref name="whenTicks"/> rather than from where the
    /// drive is now, which it does not when no timestamp was reported or the step clock is not yet
    /// synchronised
    /// </param>
    /// <returns>True on success</returns>
    /// <remarks>
    /// Only the engine can answer this: it planned the motion and holds the segment chain, so it can
    /// evaluate the profile at an instant that has already passed. That is what undoing an endstop
    /// overshoot needs - where the drive was when the switch fired, not where the report caught it
    /// </remarks>
    public bool GetPositionAt(int drive, uint whenTicks, out int position, out int positionAtMoveStart,
                              out bool usedTimestamp)
    {
        position = positionAtMoveStart = 0;
        usedTimestamp = false;
        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        int ok = NativeMethods.DuetSbc_MotionGetPositionAt(_handle, drive, whenTicks, out position,
                                                           out positionAtMoveStart, out int usedTimestampFlag);
        usedTimestamp = usedTimestampFlag != 0;
        return ok != 0;
    }

    /// <summary>
    /// Force motor positions, after homing or a move that stopped early
    /// </summary>
    /// <param name="driveMask">Logical drives to set</param>
    /// <param name="positions">Positions in microsteps</param>
    /// <returns>True if the engine took the position</returns>
    /// <remarks>
    /// The engine adopts it on its own thread, because adopting a position discards the pending
    /// motion of the drives it names and that is not something another thread may do to it. It
    /// happens before any move submitted afterwards, so a caller that forces a position and then
    /// queues a move gets the move it meant
    /// </remarks>
    public bool SetMotorPositions(uint driveMask, ReadOnlySpan<int> positions)
        => _handle != IntPtr.Zero
           && NativeMethods.DuetSbc_MotionSetMotorPositions(_handle, driveMask, positions, positions.Length) != 0;

    /// <summary>
    /// Store the ring state decided here for the motion thread to read
    /// </summary>
    /// <param name="ring">Ring number</param>
    /// <param name="shouldStartMove">Whether queued moves should start executing</param>
    /// <param name="waitingForEmpty">Whether this side is waiting for the ring to drain</param>
    public void SetRingState(int ring, bool shouldStartMove, bool waitingForEmpty)
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.DuetSbc_MotionSetRingState(_handle, ring, shouldStartMove ? 1 : 0, waitingForEmpty ? 1 : 0);
        }
    }

    /// <summary>
    /// Number of moves the given ring has been given
    /// </summary>
    /// <param name="ring">Ring number</param>
    /// <returns>Scheduled move count</returns>
    public uint GetScheduledMoves(int ring) => _handle != IntPtr.Zero ? NativeMethods.DuetSbc_MotionGetScheduledMoves(_handle, ring) : 0;

    /// <summary>
    /// Number of moves the given ring has finished
    /// </summary>
    /// <param name="ring">Ring number</param>
    /// <returns>Completed move count</returns>
    public uint GetCompletedMoves(int ring) => _handle != IntPtr.Zero ? NativeMethods.DuetSbc_MotionGetCompletedMoves(_handle, ring) : 0;

    /// <summary>
    /// Submissions refused because the queue was full. Non-zero means a move was lost
    /// </summary>
    public uint SubmissionsDropped => _handle != IntPtr.Zero ? NativeMethods.DuetSbc_MotionGetSubmissionsDropped(_handle) : 0;

    /// <summary>
    /// Whether a submitted move has not yet been taken up by the engine's motion thread
    /// </summary>
    /// <remarks>
    /// <see cref="SubmitMove"/> hands the move to a lock-free queue and returns; the ring that
    /// executes it only counts it as scheduled once the motion thread has taken it out. Between
    /// those two the ring counters describe a machine with nothing to do while a move is already on
    /// its way to it, so anything waiting for the machine to stop has to ask this as well
    /// </remarks>
    public bool HasPendingSubmissions
        => _handle != IntPtr.Zero && NativeMethods.DuetSbc_MotionHasPendingSubmissions(_handle) != 0;

    /// <summary>
    /// Forced positions the engine has adopted
    /// </summary>
    /// <returns>The count since startup, or null if the loaded library does not report it</returns>
    /// <remarks>
    /// <para>
    /// <see cref="SetMotorPositions"/> queues a position for the motion thread; this says how many it
    /// has taken up. The two are the difference between a position that was sent and one that took
    /// effect, which nothing else distinguishes.
    /// </para>
    /// <para>
    /// Null rather than a throw when the symbol is absent. This is a diagnostic, and a diagnostic
    /// that takes the whole of <c>M122</c> down with it when the native library is older than this
    /// program is worse than no diagnostic - not least because "the library was not updated" is
    /// exactly what it would have been reporting
    /// </para>
    /// </remarks>
    public uint? GetForcedPositionsApplied()
    {
        if (_handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return NativeMethods.DuetSbc_MotionGetForcedPositionsApplied(_handle);
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }
    #endregion

    /// <summary>
    /// Update the cached protocol version after a (re)connect
    /// </summary>
    /// <param name="protocolVersion">Negotiated protocol version</param>
    internal void SetProtocolVersion(int protocolVersion) => ProtocolVersion = protocolVersion;

    /// <summary>
    /// Dispose this instance
    /// </summary>
    public void Dispose()
    {
        Stop();
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.DuetSbc_Destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
