using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Motion;
using DuetControlServer.Link;

namespace SystemTests;

/// <summary>
/// The scriptable fake DuetCANMaster endpoint of the system test bench: the controller side of the
/// framed exchange defined in <c>DuetSpiProtocol/SocketLinkFormats.h</c>, served over a Unix domain
/// socket. DuetControlServer and libduet_sbc run unmodified against it via the socket transport.
/// </summary>
/// <remarks>
/// <para>
/// Every response the protocol expects has a default this class gives unprompted (see
/// docs/devel/SYSTEM_EMULATION.md, stage 1): emergency stop and reset are acknowledged and then
/// behave like the reboot they cause, CAN configuration is acknowledged, scheduled moves are
/// accepted and recorded, CAN sends are acknowledged with <see cref="CanStatus.Ok"/> and routed to
/// any registered handler, IAP data is accepted and discarded. Tests override or inject at will.
/// </para>
/// <para>
/// Capture is total: every transfer's header and every packet, both directions, in order. The fake
/// owns the master clock through its <see cref="IControllerClock"/>, which is what makes the motion
/// timeline scriptable.
/// </para>
/// <para>
/// The C++ loopback peer in <c>src/DuetSbcInterface/tests/SocketTransportTests.cpp</c> is the
/// executable specification of the exchange this class implements.
/// </para>
/// </remarks>
internal sealed class ScriptedCanMaster : IDisposable
{
    /// <summary>Handles a captured SendCANMessage; runs after the exchange that carried it completed</summary>
    public delegate void CanMessageHandler(ScriptedCanMaster controller, SendCanMessageHeader header, byte[] payload);

    private readonly Socket _listener;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private Socket? _connection;

    private readonly object _lock = new();
    private readonly List<CapturedTransfer> _transfers = [];
    private readonly List<(FirmwareRequest Request, byte[] Data)> _staged = [];

    /// <summary>
    /// Bumped whenever the staging queue is discarded wholesale. A transfer is built from the queue
    /// but only removes what it carried once the exchange has completed, so a discard in between
    /// has to invalidate that pending removal rather than let it delete packets it never sent
    /// </summary>
    private int _stagedGeneration;
    private readonly Dictionary<ushort, CanMessageHandler> _canHandlers = [];
    private readonly Queue<CanStatus> _scriptedCanSendStatus = [];
    private ushort _sequenceNumber;
    private bool _canEnabled;
    private bool _corruptNextHeaderCrc;
    private bool _corruptNextDataCrc;
    private bool _armingPaused;
    private bool _rebootPending;
    private int _accepts;
    private int _completedExchanges;
    private int _flashedSegments;

    /// <summary>Path of the Unix domain socket this controller listens on</summary>
    public string SocketPath { get; }

    /// <summary>The master step clock this controller reports; see <see cref="IControllerClock"/></summary>
    public IControllerClock Clock { get; }

    /// <summary>
    /// Called for a SendCANMessage no per-type handler is registered for. The default leaves the
    /// message unanswered (its send is still acknowledged), so a code waiting for a reply runs into
    /// its own timeout - which is the honest default for a bus with nothing on it
    /// </summary>
    public CanMessageHandler? DefaultCanHandler { get; set; }

    /// <summary>Verdict the flasher returns for firmware verification requests</summary>
    public byte FlashVerdict { get; set; } = SpiWire.FlashVerifyOk;

    public ScriptedCanMaster(string socketPath, IControllerClock? clock = null)
    {
        VerifyLayouts();
        SocketPath = socketPath;
        Clock = clock ?? new FreeRunningClock();

        File.Delete(socketPath);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(4);

        _thread = new Thread(Run) { Name = "ScriptedCanMaster", IsBackground = true };
        _thread.Start();
    }

    public void Dispose()
    {
        _stopping = true;
        _listener.Close();
        lock (_lock)
        {
            _connection?.Close();
        }
        lock (_armGate)
        {
            Monitor.PulseAll(_armGate);
        }
        _thread.Join();
        File.Delete(SocketPath);
    }

    /// <summary>
    /// Verify that the managed struct layouts still match the native wire formats, so a drift fails
    /// loudly instead of corrupting every exchange
    /// </summary>
    private static void VerifyLayouts()
    {
        static void Check<T>(int expected) where T : struct
        {
            int actual = Marshal.SizeOf<T>();
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"Layout mismatch: {typeof(T).Name} is {actual} bytes, the wire format is {expected}. SpiWire.cs and MessageFormats.h are out of sync");
            }
        }

        Check<SocketFrameHeader>(8);
        Check<TransferHeader>(24);
        Check<PacketHeader>(8);
        Check<MessageHeader>(8);
        Check<EnableCanHeader>(4);
        Check<ScheduleMoveHeader>(56);
        Check<ScheduleMoveDriver>(16);
        Check<SendCanMessageHeader>(12);
        Check<FlashVerify>(8);
        Check<CodeBufferUpdateHeader>(4);
        Check<MotionStoppedHeader>(12);
        Check<MotionStoppedDriver>(4);
        Check<CanMessageSentHeader>(4);
        Check<CanMessageSentEntry>(4);
        Check<CanResponseHeader>(12);
    }

    #region Scripting
    /// <summary>Register a handler for SendCANMessage packets of the given CAN message type</summary>
    public void OnCanMessage(ushort msgType, CanMessageHandler handler)
    {
        lock (_lock)
        {
            _canHandlers[msgType] = handler;
        }
    }

    /// <summary>
    /// Answer every CAN message that expects a StandardReply with an empty success, like a healthy
    /// board with nothing to say. This is what lets configuration codes (M569, M906, ...) succeed
    /// against the fake; requests expecting any other reply type stay for the test to script
    /// </summary>
    public void AckCanRequestsWithStandardReplies()
    {
        DefaultCanHandler = static (fake, header, _) =>
        {
            if (header.ReplyType == (ushort)CanMessageType.StandardReply)
            {
                fake.InjectStandardReply(header);
            }
        };
    }

    /// <summary>Answer the given CAN request with a StandardReply</summary>
    public void InjectStandardReply(SendCanMessageHeader request,
                                    CodeResult result = CodeResult.Ok,
                                    string text = "")
    {
        CanMessageStandardReply reply = default;
        reply.ResultCode = result;
        reply.TextString = text;
        byte[] whole = new byte[64];
        MemoryMarshal.Write(whole, in reply);
        byte[] payload = whole.AsSpan(0, (int)reply.GetActualDataLength((uint)text.Length)).ToArray();
        InjectCanResponse(request.TxToken,
                          (ushort)CanMessageType.StandardReply,
                          srcAddress: request.DstAddress == 127 ? (byte)0 : request.DstAddress,
                          payload);
    }

    /// <summary>Answer the next CAN send with the given status instead of <see cref="CanStatus.Ok"/></summary>
    public void ScriptCanSendStatus(CanStatus status)
    {
        lock (_lock)
        {
            _scriptedCanSendStatus.Enqueue(status);
        }
    }

    /// <summary>Corrupt the header CRC of the next transfer this controller sends</summary>
    public void CorruptNextHeaderCrc()
    {
        lock (_lock)
        {
            _corruptNextHeaderCrc = true;
        }
    }

    /// <summary>Corrupt the data CRC of the next transfer this controller sends</summary>
    public void CorruptNextDataCrc()
    {
        lock (_lock)
        {
            _corruptNextDataCrc = true;
        }
    }

    private readonly object _armGate = new();

    /// <summary>
    /// Withhold readiness: arm no further exchange until <see cref="ResumeArming"/>, so the SBC's
    /// next transfer times out. The pause takes effect from the exchange after the one already armed
    /// </summary>
    public void PauseArming()
    {
        lock (_armGate)
        {
            _armingPaused = true;
        }
    }

    /// <summary>Arm exchanges again after <see cref="PauseArming"/></summary>
    public void ResumeArming()
    {
        lock (_armGate)
        {
            _armingPaused = false;
            Monitor.PulseAll(_armGate);
        }
    }

    /// <summary>
    /// Reboot the controller as an external event: the sequence numbers restart and the connection
    /// drops, exactly as a real controller falling off the link mid-session looks to the SBC
    /// </summary>
    public void SimulateReboot()
    {
        lock (_lock)
        {
            _sequenceNumber = 0;
            _canEnabled = false;
            _staged.Clear();
            _stagedGeneration++;
            _connection?.Close();
        }
    }
    #endregion

    #region Injection
    /// <summary>
    /// Stage a firmware-to-SBC packet for the next transfer and prompt the SBC to start one, like
    /// the DataAvailable pin rising
    /// </summary>
    public void InjectPacket(FirmwareRequest request, byte[] data)
    {
        lock (_lock)
        {
            _staged.Add((request, data));
        }
        PromptTransfer();
    }

    /// <summary>Report the available code buffer size</summary>
    public void InjectCodeBufferUpdate(ushort bufferSpace)
        => InjectPacket(FirmwareRequest.CodeBufferUpdate, Wire.ToBytes(new CodeBufferUpdateHeader { BufferSpace = bufferSpace }));

    /// <summary>Send a firmware message to the SBC</summary>
    public void InjectMessage(uint messageType, string text)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(text);
        byte[] data = new byte[SpiWire.AddPadding(8 + encoded.Length)];
        Wire.Write(data, new MessageHeader { MessageType = messageType, Length = (ushort)encoded.Length });
        encoded.CopyTo(data, 8);
        InjectPacket(FirmwareRequest.Message, data);
    }

    /// <summary>
    /// Report drives stopped by an endstop or stall, exactly as the controller reports a stop it
    /// performed close to the bus
    /// </summary>
    public void InjectMotionStopped(uint whenTriggered, uint moveId, params (byte Board, byte Driver)[] drivers)
    {
        byte[] data = new byte[12 + (drivers.Length * 4)];
        Wire.Write(data, new MotionStoppedHeader
        {
            WhenTriggered = whenTriggered,
            MoveId = moveId,
            NumDrivers = (byte)drivers.Length
        });
        for (int i = 0; i < drivers.Length; i++)
        {
            Wire.Write(data.AsSpan(12 + (i * 4)), new MotionStoppedDriver
            {
                BoardAddress = drivers[i].Board,
                DriverNumber = drivers[i].Driver
            });
        }
        InjectPacket(FirmwareRequest.MotionStopped, data);
    }

    /// <summary>
    /// Forward a CAN message as an expansion board would send it: a reply when <paramref name="txToken"/>
    /// matches a request the SBC sent, unsolicited (status reports, input changes, events) when it
    /// is <see cref="LinkInterface.UnsolicitedTxToken"/>
    /// </summary>
    public void InjectCanResponse(ushort txToken, ushort msgType, byte srcAddress, byte[] payload,
                                  CanStatus status = CanStatus.Ok, byte flags = 0)
    {
        byte[] data = new byte[SpiWire.AddPadding(12 + payload.Length)];
        Wire.Write(data, new CanResponseHeader
        {
            TxToken = txToken,
            MsgType = msgType,
            DataLength = (ushort)payload.Length,
            SrcAddress = srcAddress,
            Flags = flags,
            Status = (byte)status
        });
        payload.CopyTo(data, 12);
        InjectPacket(FirmwareRequest.CANResponse, data);
    }

    /// <summary>
    /// Broadcast a heater status report as an expansion board does. The model's heater state and
    /// current reading follow from these reports, so this is what makes a heater warm up (or stay
    /// cold) on the bench
    /// </summary>
    public void InjectHeatersStatus(byte srcAddress, int heaterNumber,
                                    HeaterMode mode,
                                    float currentTemperature, byte averagePwm = 128)
    {
        CanMessageHeatersStatus report = default;
        report.WhichHeaters = 1ul << heaterNumber;
        report.Reports[0].Mode = mode;
        report.Reports[0].AveragePwm = averagePwm;
        report.Reports[0].SetTemperature(currentTemperature);

        byte[] payload = new byte[report.GetActualDataLength(1)];
        CanMessageSerializer.Serialize(in report, payload);
        InjectCanResponse(LinkInterface.UnsolicitedTxToken,
                          (ushort)CanMessageType.HeatersStatusReport,
                          srcAddress, payload);
    }

    /// <summary>
    /// Broadcast a sensor temperatures report as an expansion board does, which is what feeds
    /// sensors.analog[].lastReading
    /// </summary>
    public void InjectSensorReport(byte srcAddress, int sensorNumber, float temperature, DuetAPI.ObjectModel.TemperatureError error = DuetAPI.ObjectModel.TemperatureError.Ok)
    {
        CanMessageSensorTemperatures report = default;
        report.WhichSensors = 1ul << sensorNumber;
        report.TemperatureReports[0].ErrorCode = error;
        report.TemperatureReports[0].SetTemperature(temperature);
        byte[] payload = new byte[report.GetActualDataLength(1)];
        CanMessageSerializer.Serialize(in report, payload);
        InjectCanResponse(LinkInterface.UnsolicitedTxToken,
                         (ushort)CanMessageType.SensorTemperaturesReport,
                         srcAddress,
                         payload);
    }

    /// <summary>
    /// Report an input level change from board 1, exactly as an expansion board reports the input a
    /// monitor watches. An active probe reads the top of the analog scale, which is what a closed
    /// digital probe reports
    /// </summary>
    public void InjectInputChange(byte srcAddress, RemoteInputHandle handle, bool active)
    {
        CanMessageInputChangedV2 changed = default;
        changed.AddEntry(handle.All, 0, active ? RemoteProbes.MaxReading : 0, active);
        byte[] payload = new byte[Marshal.SizeOf<CanMessageInputChangedV2>()];
        CanMessageSerializer.Serialize(in changed, payload);
        InjectCanResponse(LinkInterface.UnsolicitedTxToken,
                         (ushort)CanMessageType.InputStateChangedV2,
                         srcAddress,
                         payload);
    }

    /// <summary>Ask the SBC to resend the packet with the given id, exercising the retransmission path</summary>
    public void InjectResendRequest(ushort packetId)
    {
        lock (_lock)
        {
            _staged.Add((FirmwareRequest.ResendPacket, ResendMarker(packetId)));
        }
        PromptTransfer();
    }

    // A ResendPacket request carries no payload; the packet to resend rides in the header's
    // resendPacketId field, so it is marked out of band here and patched in at build time
    private static byte[] ResendMarker(ushort packetId) => [0xFE, (byte)packetId, (byte)(packetId >> 8)];
    #endregion

    #region Observation
    /// <summary>Snapshot of every transfer captured so far, both directions, in order</summary>
    public IReadOnlyList<CapturedTransfer> Transfers
    {
        get
        {
            lock (_lock)
            {
                return _transfers.ToArray();
            }
        }
    }

    /// <summary>Every captured SBC-to-controller packet of the given kind, in order</summary>
    public IReadOnlyList<CapturedPacket> SbcPackets(SbcRequest request)
        => Transfers.Where(t => t.Direction == TransferDirection.FromSbc)
                    .SelectMany(t => t.Packets)
                    .Where(p => p.SbcRequest == request)
                    .ToArray();

    /// <summary>Number of exchanges both sides completed successfully</summary>
    public int CompletedExchanges => Volatile.Read(ref _completedExchanges);

    /// <summary>Number of times the SBC (re)connected to this controller</summary>
    public int Accepts => Volatile.Read(ref _accepts);

    /// <summary>Number of bare firmware segments received (and discarded) while flashing</summary>
    public int FlashedSegments => Volatile.Read(ref _flashedSegments);

    /// <summary>Render the whole capture as a readable exchange log</summary>
    public string DumpCapture() => CapturedTransfer.Dump(Transfers);

    /// <summary>Wait until an SBC-to-controller packet matching the given kind (and predicate) was captured</summary>
    public async Task<CapturedPacket> WaitForSbcPacketAsync(SbcRequest request,
                                                            Func<CapturedPacket, bool>? predicate = null,
                                                            int timeoutMs = 10_000)
    {
        CapturedPacket? found = null;
        await WaitUntilAsync(
            () => (found = SbcPackets(request).FirstOrDefault(p => predicate == null || predicate(p))) != null,
            timeoutMs,
            $"no {request} packet arrived");
        return found!;
    }

    /// <summary>Wait until the given condition holds, failing with the capture dump if it never does</summary>
    public async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000, string? what = null)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting: {what ?? "condition"}\nCaptured exchanges:\n{DumpCapture()}");
            }
            await Task.Delay(10);
        }
    }
    #endregion

    #region Serving
    private void Run()
    {
        while (!_stopping)
        {
            Socket connection;
            try
            {
                connection = _listener.Accept();
            }
            catch (Exception) when (_stopping)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            lock (_lock)
            {
                _connection = connection;
            }
            Interlocked.Increment(ref _accepts);
            try
            {
                ServeConnection(connection);
            }
            catch (Exception) when (_stopping)
            {
                return;
            }
            catch (Exception)
            {
                // The SBC dropped the connection (a timeout, a scripted failure, a shutdown); state
                // survives and the next accept carries on
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_connection, connection))
                    {
                        _connection = null;
                    }
                }
                connection.Close();
            }
        }
    }

    private void ServeConnection(Socket connection)
    {
        bool retrying = false;
        byte[] txData = [];
        TransferHeader txHeader = default;
        List<CapturedPacket> txPackets = [];
        int stagedInTransfer = 0;
        int stagedGeneration = 0;

        while (!_stopping)
        {
            // Arm the exchange - or, while a test withholds readiness, wait here without arming
            lock (_armGate)
            {
                while (_armingPaused && !_stopping)
                {
                    Monitor.Wait(_armGate);
                }
            }
            if (_stopping)
            {
                return;
            }
            WriteFrame(connection, SocketFrameType.Ready, []);

            // What does the SBC want: a transfer, or one of the bare IAP steps?
            SocketFrameHeader frame = ReadFrameHeader(connection);
            byte[] payload = ReadExact(connection, checked((int)frame.Length));

            switch ((SocketFrameType)frame.Type)
            {
                case SocketFrameType.IapData:
                    // Accept and discard; flashing against emulated flash is stage 2's to test
                    Interlocked.Increment(ref _flashedSegments);
                    continue;

                case SocketFrameType.IapVerify:
                    WriteFrame(connection, SocketFrameType.IapVerdict, [FlashVerdict]);
                    continue;

                case SocketFrameType.Response:
                    // The SBC abandoned the exchange (BadResponse); re-arm and carry on
                    continue;

                case SocketFrameType.Transfer:
                    break;

                default:
                    throw new InvalidDataException($"Unexpected frame type {frame.Type} from the SBC");
            }

            TransferHeader rxHeader = Wire.Read<TransferHeader>(payload);
            Span<byte> rxData = payload.AsSpan(24);

            // Validate what arrived, as the controller does
            uint verdict = TransferResponse.Success;
            if (rxHeader.FormatCode != SpiWire.FormatCode)
            {
                verdict = TransferResponse.BadResponse;
            }
            else if (rxHeader.CrcHeader != DuetControlServer.Utility.CRC32.Calculate(payload.AsSpan(0, TransferHeader.CrcCoveredLength)))
            {
                verdict = TransferResponse.BadHeaderChecksum;
            }
            else if (rxHeader.DataLength != rxData.Length ||
                     rxHeader.CrcData != DuetControlServer.Utility.CRC32.Calculate(rxData))
            {
                verdict = TransferResponse.BadDataChecksum;
            }

            // Build this side's transfer, or on a retry re-send the same one
            if (!retrying)
            {
                (txHeader, txData, txPackets, stagedInTransfer, stagedGeneration) = BuildTransfer();
            }

            // A test may have asked for a corrupted CRC; the corruption goes out on the wire while
            // the built transfer stays intact for the retry that follows
            TransferHeader sentHeader = txHeader;
            lock (_lock)
            {
                if (_corruptNextHeaderCrc)
                {
                    _corruptNextHeaderCrc = false;
                    sentHeader.CrcHeader ^= 0xDEADBEEF;
                }
                else if (_corruptNextDataCrc)
                {
                    _corruptNextDataCrc = false;
                    sentHeader.CrcData ^= 0xDEADBEEF;
                    byte[] headerBytes = Wire.ToBytes(in sentHeader);
                    sentHeader.CrcHeader = DuetControlServer.Utility.CRC32.Calculate(headerBytes.AsSpan(0, TransferHeader.CrcCoveredLength));
                }
            }

            byte[] transferPayload = new byte[24 + txData.Length];
            Wire.Write(transferPayload, in sentHeader);
            txData.CopyTo(transferPayload, 24);
            WriteFrame(connection, SocketFrameType.Transfer, transferPayload);

            // Verdicts both ways
            WriteFrame(connection, SocketFrameType.Response, BitConverter.GetBytes(verdict));
            SocketFrameHeader responseFrame = ReadFrameHeader(connection);
            if ((SocketFrameType)responseFrame.Type != SocketFrameType.Response || responseFrame.Length != 4)
            {
                throw new InvalidDataException("The SBC answered the exchange with something other than a response code");
            }
            uint sbcVerdict = BitConverter.ToUInt32(ReadExact(connection, 4));

            bool completed = verdict == TransferResponse.Success && sbcVerdict == TransferResponse.Success;
            retrying = !completed;
            if (!completed)
            {
                continue;
            }

            // The exchange stands: the packets it carried are spent, so drop them from the staging
            // queue. They are only dropped here, because an exchange that never completes - the SBC
            // reconnecting mid-transfer, say - has to leave them staged for the next connection to
            // send. Removal is from the front, and anything injected meanwhile went on the back; a
            // reboot that discarded the queue in between shows up as a generation change instead
            List<CapturedPacket> rxPackets = ParsePackets(payload.AsSpan(24, rxHeader.DataLength));
            bool reboot;
            lock (_lock)
            {
                if (stagedGeneration == _stagedGeneration)
                {
                    _staged.RemoveRange(0, stagedInTransfer);
                }
                stagedInTransfer = 0;
                _transfers.Add(new CapturedTransfer(TransferDirection.FromSbc, rxHeader, rxPackets));
                _transfers.Add(new CapturedTransfer(TransferDirection.ToSbc, txHeader, txPackets));
                _completedExchanges++;
                reboot = _rebootPending;
                _rebootPending = false;
            }
            ProcessSbcPackets(rxPackets, ref reboot);

            if (reboot)
            {
                // Acknowledged; now behave like the reboot: state restarts and the link drops, so
                // the SBC runs its reconnect and reset paths for real
                lock (_lock)
                {
                    _sequenceNumber = 0;
                    _canEnabled = false;
                    _staged.Clear();
                    _stagedGeneration++;
                }
                return;
            }
        }
    }

    /// <summary>
    /// Build one transfer around the staged packets that fit in it, and report how many of them
    /// that was along with the generation of the queue they came from. The packets stay staged: the
    /// caller drops them once the exchange has completed, so that a connection lost part-way through
    /// one leaves them for the next connection to send
    /// </summary>
    private (TransferHeader Header, byte[] Data, List<CapturedPacket> Packets, int StagedConsumed, int StagedGeneration) BuildTransfer()
    {
        lock (_lock)
        {
            List<CapturedPacket> packets = [];
            using MemoryStream data = new();
            ushort packetId = 0;
            int consumed = 0;
            while (consumed < _staged.Count)
            {
                (FirmwareRequest request, byte[] packetData) = _staged[consumed];

                // The resend marker carries the id to ask for in-band; see ResendMarker
                bool isResendMarker = request == FirmwareRequest.ResendPacket && packetData is [0xFE, _, _];
                byte[] body = isResendMarker ? [] : packetData;
                if (data.Length + 8 + SpiWire.AddPadding(body.Length) > SpiWire.BufferSize)
                {
                    break;
                }
                consumed++;

                PacketHeader packetHeader = new()
                {
                    Request = (ushort)request,
                    Id = packetId++,
                    Length = (ushort)body.Length,
                    ResendPacketId = isResendMarker ? (ushort)(packetData[1] | (packetData[2] << 8)) : (ushort)0
                };
                data.Write(Wire.ToBytes(in packetHeader));
                data.Write(body);
                for (int pad = body.Length; pad % 4 != 0; pad++)
                {
                    data.WriteByte(0);
                }
                packets.Add(new CapturedPacket(packetHeader.Request, packetHeader.Id, body));
            }

            byte[] dataBytes = data.ToArray();
            TransferHeader header = new()
            {
                FormatCode = SpiWire.FormatCode,
                NumPackets = (byte)packets.Count,
                ProtocolVersion = SpiWire.ProtocolVersion,
                SequenceNumber = ++_sequenceNumber,
                DataLength = (ushort)dataBytes.Length,
                MasterClock = Clock.MasterClock,
                HiccupTime = 0,
                CrcData = DuetControlServer.Utility.CRC32.Calculate(dataBytes)
            };
            byte[] headerBytes = Wire.ToBytes(in header);
            header.CrcHeader = DuetControlServer.Utility.CRC32.Calculate(headerBytes.AsSpan(0, TransferHeader.CrcCoveredLength));
            return (header, dataBytes, packets, consumed, _stagedGeneration);
        }
    }

    private static List<CapturedPacket> ParsePackets(ReadOnlySpan<byte> data)
    {
        List<CapturedPacket> packets = [];
        int offset = 0;
        while (offset + 8 <= data.Length)
        {
            PacketHeader header = Wire.Read<PacketHeader>(data[offset..]);
            offset += 8;
            packets.Add(new CapturedPacket(header.Request, header.Id, data.Slice(offset, header.Length).ToArray()));
            offset += SpiWire.AddPadding(header.Length);
        }
        return packets;
    }

    /// <summary>The default response table: what real hardware would do with each request</summary>
    private void ProcessSbcPackets(List<CapturedPacket> packets, ref bool reboot)
    {
        List<CanMessageSentEntry> acks = [];
        List<(SendCanMessageHeader Header, byte[] Payload)> canMessages = [];
        lock (_lock)
        {
            foreach (CapturedPacket packet in packets)
            {
                switch (packet.SbcRequest)
                {
                    case SbcRequest.EmergencyStop:
                    case SbcRequest.Reset:
                        reboot = true;
                        break;

                    case SbcRequest.SendCANMessage:
                        (SendCanMessageHeader header, byte[] payload) = packet.DecodeCanMessage();
                        // With the bus disabled nothing reaches it and the SBC has no other way to
                        // find out, so the send is answered with BusError - exactly what
                        // DuetCANMaster's CanInterface::SendCanRequest reports while CAN is not
                        // enabled
                        CanStatus status = !_canEnabled ? CanStatus.BusError
                            : _scriptedCanSendStatus.Count > 0 ? _scriptedCanSendStatus.Dequeue()
                            : CanStatus.Ok;
                        acks.Add(new CanMessageSentEntry { TxToken = header.TxToken, Status = (byte)status });
                        if (status == CanStatus.Ok)
                        {
                            canMessages.Add((header, payload));
                        }
                        break;

                    case SbcRequest.EnableCAN:
                        _canEnabled = packet.DecodeEnableCan().Enable != 0;
                        break;

                    case SbcRequest.ConfigCAN:
                    case SbcRequest.ScheduleMove:
                    case SbcRequest.WriteIap:
                    case SbcRequest.StartIap:
                    case SbcRequest.Message:
                        // Accepted and recorded; nothing to answer
                        break;
                }
            }
        }

        // Acknowledge what was sent, batched as the controller batches them
        if (acks.Count > 0)
        {
            byte[] data = new byte[4 + (acks.Count * 4)];
            Wire.Write(data, new CanMessageSentHeader { Count = (ushort)acks.Count });
            for (int i = 0; i < acks.Count; i++)
            {
                Wire.Write(data.AsSpan(4 + (i * 4)), acks[i]);
            }
            InjectPacket(FirmwareRequest.CanMessageSent, data);
        }

        // Route delivered CAN messages to their handlers, outside the lock: handlers inject
        foreach ((SendCanMessageHeader header, byte[] payload) in canMessages)
        {
            CanMessageHandler? handler;
            lock (_lock)
            {
                if (!_canHandlers.TryGetValue(header.MsgType, out handler))
                {
                    handler = DefaultCanHandler;
                }
            }
            handler?.Invoke(this, header, payload);
        }
    }
    #endregion

    #region Socket I/O
    /// <summary>
    /// Prompt the SBC to start a transfer, like the DataAvailable pin rising. Harmless without a
    /// connection: the staged data goes out with the next keep-alive transfer instead
    /// </summary>
    private void PromptTransfer()
    {
        Socket? connection;
        lock (_lock)
        {
            connection = _connection;
        }
        if (connection != null)
        {
            try
            {
                WriteFrame(connection, SocketFrameType.DataAvailable, []);
            }
            catch (Exception)
            {
                // The connection is on its way down; the reconnect collects the staged data
            }
        }
    }

    private readonly object _writeLock = new();

    private void WriteFrame(Socket connection, SocketFrameType type, byte[] payload)
    {
        byte[] frame = new byte[8 + payload.Length];
        Wire.Write(frame, new SocketFrameHeader { Type = (byte)type, Length = (uint)payload.Length });
        payload.CopyTo(frame, 8);

        // One writer at a time: injections prompt from test threads while the serving thread is
        // mid-exchange, and interleaving bytes of two frames would desynchronise the stream
        lock (_writeLock)
        {
            int sent = 0;
            while (sent < frame.Length)
            {
                sent += connection.Send(frame, sent, frame.Length - sent, SocketFlags.None);
            }
        }
    }

    private SocketFrameHeader ReadFrameHeader(Socket connection)
    {
        SocketFrameHeader header = Wire.Read<SocketFrameHeader>(ReadExact(connection, 8));
        if (header.Length > 24 + SpiWire.BufferSize)
        {
            throw new InvalidDataException($"Oversized frame from the SBC ({header.Length} bytes)");
        }
        return header;
    }

    private byte[] ReadExact(Socket connection, int length)
    {
        byte[] buffer = new byte[length];
        int done = 0;
        while (done < length)
        {
            int received = connection.Receive(buffer, done, length - done, SocketFlags.None);
            if (received <= 0)
            {
                throw new EndOfStreamException("The SBC closed the connection");
            }
            done += received;
        }
        return buffer;
    }
    #endregion
}
