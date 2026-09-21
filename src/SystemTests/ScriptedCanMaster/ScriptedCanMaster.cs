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
/// socket. DuetControlServer and libduet_realtime_core run unmodified against it via the socket transport.
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
/// The C++ loopback peer in <c>src/DuetRealtimeCore/tests/SocketTransportTests.cpp</c> is the
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
        Check<MinCurMaxValues>(12);
        Check<BoardInfoHeader>(25);
        Check<BoardStatusHeader>(44);
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

    /// <summary>
    /// Answer every request of one message type with the text a board would report, instead of the
    /// empty success <see cref="AckCanRequestsWithStandardReplies"/> answers with
    /// </summary>
    /// <typeparam name="TMessage">The CAN message being answered</typeparam>
    /// <param name="text">What the board says, given the request it is answering</param>
    /// <param name="result">The result code the board answers with</param>
    public void ReportWith<TMessage>(Func<TMessage, string> text, CodeResult result = CodeResult.Ok)
        where TMessage : struct, ICanMessage<TMessage>
        => OnCanMessage((ushort)TMessage.MessageType, (fake, header, payload) =>
            fake.InjectStandardReply(header, result, text(CanMessageSerializer.Deserialize<TMessage>(payload))));

    /// <summary>
    /// Answer every request of one message type with a fixed text, whatever it asked
    /// </summary>
    /// <typeparam name="TMessage">The CAN message being answered</typeparam>
    /// <param name="text">What the board says</param>
    /// <param name="result">The result code the board answers with</param>
    public void ReportWith<TMessage>(string text, CodeResult result = CodeResult.Ok)
        where TMessage : struct, ICanMessage<TMessage>
        => ReportWith<TMessage>(_ => text, result);

    /// <summary>
    /// Leave every request of one message type unanswered, as a board that does not know the command
    /// at all does
    /// </summary>
    /// <typeparam name="TMessage">The CAN message to ignore</typeparam>
    /// <remarks>
    /// Which is what the regression rig's second MB6HC does with M970: it runs RepRapFirmware in
    /// expansion mode and its CommandProcessor has no case for the message, so the request sits out
    /// its timeout. The machine has to carry on and say so
    /// </remarks>
    public void NeverAnswer<TMessage>()
        where TMessage : struct, ICanMessage<TMessage>
        => OnCanMessage((ushort)TMessage.MessageType, static (_, _, _) => { });

    /// <summary>How many bytes of reply text one StandardReply frame carries</summary>
    private const int StandardReplyTextLength = 60;

    /// <summary>
    /// Answer the given CAN request with a StandardReply, fragmented as a board fragments it
    /// </summary>
    /// <remarks>
    /// One frame carries 60 bytes of text, so a longer reply - a driver report, a closed loop
    /// configuration - goes out as several, each numbered and all but the last saying more follows.
    /// The HAT no longer reassembles them, so a fake that sent a long reply in one frame would be
    /// testing a transport neither end has
    /// </remarks>
    public void InjectStandardReply(SendCanMessageHeader request,
                                    CodeResult result = CodeResult.Ok,
                                    string text = "")
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        int fragments = Math.Max(1, (utf8.Length + StandardReplyTextLength - 1) / StandardReplyTextLength);
        for (int fragment = 0; fragment < fragments; fragment++)
        {
            int offset = fragment * StandardReplyTextLength;
            int length = Math.Min(StandardReplyTextLength, utf8.Length - offset);

            CanMessageStandardReply reply = default;
            reply.ResultCode = result;
            reply.FragmentNumber = (byte)fragment;
            reply.MoreFollows = fragment < fragments - 1;
            byte[] whole = new byte[64];
            MemoryMarshal.Write(whole, in reply);
            utf8.AsSpan(offset, length).CopyTo(whole.AsSpan((int)reply.GetActualDataLength(0)));

            byte[] payload = whole.AsSpan(0, (int)reply.GetActualDataLength((uint)length)).ToArray();
            InjectCanResponse(request.TxToken,
                              (ushort)CanMessageType.StandardReply,
                              srcAddress: request.DstAddress == CanId.BroadcastAddress ? CanId.MasterAddress : request.DstAddress,
                              payload);
        }
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
            RestartState();
            _connection?.Close();
        }
    }

    /// <summary>
    /// Reboot the controller without the link dropping: the sequence numbers restart but the
    /// transfers keep succeeding, which is how a controller that comes back between two of the SBC's
    /// transfers looks. The restart is then the only evidence there is, because no transfer ever
    /// timed out
    /// </summary>
    public void SimulateWarmReboot()
    {
        lock (_lock)
        {
            RestartState();
        }
    }

    /// <summary>
    /// Discard everything the running controller had, as a reboot does
    /// </summary>
    /// <remarks>The caller must hold <see cref="_lock"/></remarks>
    private void RestartState()
    {
        _sequenceNumber = 0;
        _canEnabled = false;
        _staged.Clear();
        _stagedGeneration++;
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
    /// Broadcast a fans report as an expansion board does, which is what feeds fans[].actualValue
    /// and fans[].rpm
    /// </summary>
    /// <param name="srcAddress">CAN address of the board reporting</param>
    /// <param name="fanNumber">Fan the report is about</param>
    /// <param name="actualPwm">PWM the board has the fan at, 0 to 1</param>
    /// <param name="rpm">Tacho reading, or -1 where the fan has no tacho</param>
    /// <remarks>
    /// A board reports the fans it holds, which is not the same set as the fans the machine has
    /// configured: it goes on reporting one until it acts on the message that released it. That gap
    /// is why a report must not create what it reports on
    /// </remarks>
    public void InjectFansReport(byte srcAddress, int fanNumber, float actualPwm, short rpm)
    {
        CanMessageFansReport report = default;
        report.WhichFans = 1ul << fanNumber;
        report.FanReports[0].ActualPwm = (ushort)Math.Clamp(actualPwm * 65535.0f, 0.0f, 65535.0f);
        report.FanReports[0].Rpm = rpm;

        byte[] payload = new byte[report.GetActualDataLength(1)];
        CanMessageSerializer.Serialize(in report, payload);
        InjectCanResponse(LinkInterface.UnsolicitedTxToken,
                          (ushort)CanMessageType.FansReport,
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

    /// <summary>
    /// Announce a board on the bus exactly as Duet3Expansion does when it starts or regains time sync
    /// </summary>
    /// <param name="srcAddress">CAN address of the board announcing itself</param>
    /// <param name="shortName">Board type, e.g. "EXP3HC"</param>
    /// <param name="firmwareVersion">Firmware version it runs</param>
    /// <param name="firmwareDate">Date that firmware was built</param>
    /// <param name="numDrivers">How many drivers it carries</param>
    /// <param name="uniqueId">Its 16-byte unique id, or null for all zeroes</param>
    /// <param name="usesUf2Binary">Whether its firmware binary is a .uf2 rather than a .bin</param>
    /// <remarks>
    /// The three text fields travel as one pipe-separated string, which is the whole of what a board
    /// says about itself: everything else in <c>boards[]</c> is derived from the type or comes from
    /// the status reports that follow
    /// </remarks>
    public void InjectAnnounce(byte srcAddress, string shortName, string firmwareVersion, string firmwareDate,
                               byte numDrivers, byte[]? uniqueId = null, bool usesUf2Binary = false)
    {
        CanMessageAnnounceV1 announce = default;
        announce.BoardTypeAndFirmwareVersionString = $"{shortName}|{firmwareVersion}|{firmwareDate}";
        announce.NumDrivers = numDrivers;
        announce.UsesUf2Binary = usesUf2Binary;
        for (int i = 0; i < ByteArray16.Length; i++)
        {
            announce.UniqueId[i] = (uniqueId is not null && i < uniqueId.Length) ? uniqueId[i] : (byte)0;
        }

        byte[] payload = new byte[Marshal.SizeOf<CanMessageAnnounceV1>()];
        CanMessageSerializer.Serialize(in announce, payload);
        InjectCanResponse(LinkInterface.UnsolicitedTxToken,
                          (ushort)CanMessageType.AnnounceV1,
                          srcAddress, payload);
    }

    /// <summary>
    /// Broadcast a board status report as an expansion board does, which is what feeds
    /// <c>boards[].vIn</c>, <c>.v12</c>, <c>.mcuTemp</c>, <c>.freeRam</c> and the three feature flags
    /// </summary>
    /// <param name="srcAddress">CAN address of the board reporting</param>
    /// <param name="neverUsedRam">Bytes of RAM it has never allocated</param>
    /// <param name="vIn">Input voltage, or null if the board has no monitor for it</param>
    /// <param name="v12">12V rail voltage, or null if the board has no monitor for it</param>
    /// <param name="mcuTemp">MCU temperature, or null if the board has no sensor for it</param>
    /// <param name="hasAccelerometer">Whether the board claims an accelerometer</param>
    /// <param name="hasClosedLoop">Whether the board claims closed loop support</param>
    /// <param name="hasInductiveSensor">Whether the board claims an inductive sensor</param>
    /// <remarks>
    /// The readings are packed in a fixed order and only the present ones take a slot, so which of
    /// the three are passed decides where the others land. Getting that wrong is the bug the
    /// scenarios about a board with no 12V rail exist to catch
    /// </remarks>
    public void InjectBoardStatus(byte srcAddress, int neverUsedRam = 0,
                                  float? vIn = null, float? v12 = null, float? mcuTemp = null,
                                  bool hasAccelerometer = false, bool hasClosedLoop = false,
                                  bool hasInductiveSensor = false)
    {
        CanMessageBoardStatusV1 status = default;
        status.Clear();
        status.NeverUsedRam = neverUsedRam;
        status.HasAccelerometer = hasAccelerometer;
        status.HasClosedLoop = hasClosedLoop;
        status.HasInductiveSensor = hasInductiveSensor;

        int index = 0;
        foreach ((bool present, float value) in new[] { (vIn.HasValue, vIn ?? 0.0f), (v12.HasValue, v12 ?? 0.0f), (mcuTemp.HasValue, mcuTemp ?? 0.0f) })
        {
            if (present)
            {
                status.ShortValues[index].Minimum = (Half)value;
                status.ShortValues[index].Current = (Half)value;
                status.ShortValues[index].Maximum = (Half)value;
                index++;
            }
        }
        status.HasVin = vIn.HasValue;
        status.HasV12 = v12.HasValue;
        status.HasMcuTemp = mcuTemp.HasValue;

        byte[] payload = new byte[status.GetActualDataLength()];
        CanMessageSerializer.Serialize(in status, payload);
        InjectCanResponse(LinkInterface.UnsolicitedTxToken,
                          (ushort)CanMessageType.BoardStatusReportV1,
                          srcAddress, payload);
    }

    /// <summary>
    /// Say what board the controller is, as DuetCANMaster does once per connection. This is what
    /// fills <c>boards[0]</c>, which no CAN message can
    /// </summary>
    /// <param name="name">Long board name</param>
    /// <param name="shortName">Short board name</param>
    /// <param name="firmwareName">Firmware the controller runs</param>
    /// <param name="firmwareVersion">Its version</param>
    /// <param name="firmwareDate">The date it was built</param>
    /// <param name="firmwareFileName">Binary that carries it</param>
    /// <param name="iapFileNameSbc">Programmer used to flash it from the SBC</param>
    /// <param name="iapFileNameSd">Programmer used to flash it from an SD card</param>
    /// <param name="uniqueId">Its 16-byte unique id, or null if the MCU has none</param>
    /// <remarks>
    /// What the controller can drive is not among what it says. It bridges SPI to CAN-FD and keeps
    /// the master step clock, so <c>maxMotors</c>, <c>maxHeaters</c> and <c>supportsDirectDisplay</c>
    /// cannot be anything but zero, zero and false, and the SBC states them rather than reading them
    /// off the wire
    /// </remarks>
    public void InjectControllerBoardInfo(string name = "", string shortName = "", string firmwareName = "",
                                          string firmwareVersion = "", string firmwareDate = "",
                                          string firmwareFileName = "", string iapFileNameSbc = "",
                                          string iapFileNameSd = "", byte[]? uniqueId = null)
    {
        // In BoardInfoString order, which is the order they go on the wire in
        string[] strings = [name, shortName, firmwareName, firmwareVersion, firmwareDate,
                            firmwareFileName, iapFileNameSbc, iapFileNameSd];
        byte[][] encoded = strings.Select(Encoding.UTF8.GetBytes).ToArray();
        int textLength = encoded.Sum(bytes => bytes.Length);

        BoardInfoHeader header = default;
        header.HasUniqueId = (byte)(uniqueId is not null ? 1 : 0);
        for (int i = 0; i < BoardUniqueId.Length; i++)
        {
            header.UniqueId[i] = (uniqueId is not null && i < uniqueId.Length) ? uniqueId[i] : (byte)0;
        }
        for (int i = 0; i < encoded.Length; i++)
        {
            header.TextLengths[i] = (byte)encoded[i].Length;
        }

        int headerSize = Marshal.SizeOf<BoardInfoHeader>();
        byte[] data = new byte[SpiWire.AddPadding(headerSize + textLength)];
        Wire.Write(data, header);
        int offset = headerSize;
        foreach (byte[] bytes in encoded)
        {
            bytes.CopyTo(data, offset);
            offset += bytes.Length;
        }
        InjectPacket(FirmwareRequest.BoardInfo, data);
    }

    /// <summary>
    /// Report the controller's own health, as DuetCANMaster does periodically. The counterpart of
    /// <see cref="InjectBoardStatus"/> for the board that is not on the bus
    /// </summary>
    /// <param name="neverUsedRam">Bytes of RAM it has never allocated</param>
    /// <param name="mcuTemp">MCU temperature, or null if the board has no sensor for it</param>
    /// <param name="vIn">Input voltage, or null if the board has no monitor for it</param>
    /// <param name="v12">12V rail voltage, or null if the board has no monitor for it</param>
    public void InjectControllerBoardStatus(int neverUsedRam = 0, float? mcuTemp = null,
                                            float? vIn = null, float? v12 = null)
    {
        static MinCurMaxValues Reading(float? value)
            => new() { Minimum = value ?? 0.0f, Current = value ?? 0.0f, Maximum = value ?? 0.0f };

        InjectPacket(FirmwareRequest.BoardStatus, Wire.ToBytes(new BoardStatusHeader
        {
            NeverUsedRam = neverUsedRam,
            McuTemp = Reading(mcuTemp),
            VIn = Reading(vIn),
            V12 = Reading(v12),
            HasMcuTemp = (byte)(mcuTemp.HasValue ? 1 : 0),
            HasVin = (byte)(vIn.HasValue ? 1 : 0),
            HasV12 = (byte)(v12.HasValue ? 1 : 0)
        }));
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
    /// <summary>Throw away everything captured so far, so the observations start again from empty</summary>
    /// <remarks>
    /// For a scenario that has to assert a code sent <em>nothing</em>: the configuration a bench
    /// starts from puts messages on the bus of its own, so "no fan speed was sent" cannot be
    /// <c>CanMessages&lt;T&gt;()</c> being empty unless what came before is discarded first. Every
    /// observation reads the same capture, so this clears the packet and transfer views along with
    /// the CAN messages, and a failure after it dumps only the exchanges that followed
    /// </remarks>
    public void ClearCapture()
    {
        lock (_lock)
        {
            _transfers.Clear();
        }
    }

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

    /// <summary>Every CAN message of one type the machine has sent, oldest first</summary>
    /// <typeparam name="T">The message, which names its own <c>CanMessageType</c></typeparam>
    /// <returns>The messages, each with the CAN address of the board it was addressed to</returns>
    /// <remarks>
    /// What a code does to a device is the message it puts on the bus, not the value it writes to the
    /// object model: the board is what drives the pin, so a code that updates the model and sends the
    /// wrong message leaves a machine that reads correctly and behaves wrongly. Asserting on both is
    /// what separates the two
    /// </remarks>
    public IReadOnlyList<(byte Board, T Message)> CanMessages<T>() where T : struct, ICanMessage<T>
        => [.. CanPayloads<T>().Select(sent => (sent.Board, CanMessageSerializer.Deserialize<T>(sent.Payload)))];

    /// <summary>
    /// Every CAN message of one type the machine has sent, as bytes on the wire
    /// </summary>
    /// <typeparam name="T">The message, which names its own <c>CanMessageType</c></typeparam>
    /// <returns>The board each went to and the payload exactly as long as it was transmitted</returns>
    /// <remarks>
    /// For the messages whose length is part of what they say. A per-driver request carries one value
    /// per set bit of its bitmap and stops, so a sender that transmitted the whole struct would be
    /// sending values for drivers it never named - which the deserialized view cannot show, because
    /// the missing bytes read back as zeroes
    /// </remarks>
    public IReadOnlyList<(byte Board, byte[] Payload)> CanPayloads<T>() where T : struct, ICanMessage<T>
    {
        List<(byte, byte[])> payloads = [];
        foreach (CapturedPacket packet in SbcPackets(SbcRequest.SendCANMessage))
        {
            (SendCanMessageHeader header, byte[] payload) = packet.DecodeCanMessage();
            if (header.MsgType == (ushort)T.MessageType)
            {
                payloads.Add((header.DstAddress, payload));
            }
        }
        return payloads;
    }


    /// <summary>The last CAN message of one type the machine sent, and the board it went to</summary>
    /// <typeparam name="T">The message, which names its own <c>CanMessageType</c></typeparam>
    /// <returns>The message and the CAN address it was addressed to</returns>
    /// <exception cref="InvalidOperationException">No message of that type was sent</exception>
    public (byte Board, T Message) LastCanMessage<T>() where T : struct, ICanMessage<T>
    {
        IReadOnlyList<(byte Board, T Message)> messages = CanMessages<T>();
        return messages.Count > 0
               ? messages[^1]
               : throw new InvalidOperationException(
                   $"No {typeof(T).Name} was sent\nCaptured exchanges:\n{DumpCapture()}");
    }

    /// <summary>Wait until a CAN message of one type (matching the given predicate) was sent</summary>
    /// <typeparam name="T">The message, which names its own <c>CanMessageType</c></typeparam>
    /// <param name="predicate">What the message has to say, or null for any of that type</param>
    /// <param name="timeoutMs">How long to wait</param>
    /// <returns>The message and the CAN address it was addressed to</returns>
    public async Task<(byte Board, T Message)> WaitForCanMessageAsync<T>(Func<T, bool>? predicate = null,
                                                                        int timeoutMs = 10_000)
        where T : struct, ICanMessage<T>
    {
        (byte Board, T Message) found = default;
        await WaitUntilAsync(
            () => CanMessages<T>().Any(sent => predicate == null || predicate(sent.Message))
                  && (found = CanMessages<T>().Last(sent => predicate == null || predicate(sent.Message))) is var _,
            timeoutMs,
            $"no {typeof(T).Name} was sent");
        return found;
    }

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
                    RestartState();
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
