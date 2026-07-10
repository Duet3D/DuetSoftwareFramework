using DuetAPI;
using DuetAPI.ObjectModel;
using Code = DuetControlServer.Commands.Code;
using DuetControlServer.Utility;
using DuetSharedLibrary;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace DuetControlServer.Link.Adapter;

/// <summary>
/// Class to handle the SPI link to the firmware
/// </summary>
[DiagnosticsPriority(-4)]
public class SPI : IDiagnostics, ILinkAdapter
{
    // General variables
    private readonly EventLogger _eventLogger;
    private readonly Model.ObjectModel _model;
    private readonly ILogger<SPI> _logger;
    private readonly Settings _settings;

    // General transfer variables
    private readonly InputGpioPin _transferReadyPin;
    private readonly InputGpioPin _dataAvailablePin;
#if DEBUG
    private readonly OutputGpioPin _sbcDataAvailablePin;
#endif
    private readonly ManualResetEventSlim _transferReadyEvent = new(false);
    private volatile bool _lastPinValueFromCallback;

    // Sequence number of the most recently observed transfer ready rising edge, and the one already consumed
    // for the previous data exchange. The controller pulses the pin per sub-exchange, so a sub-exchange must
    // wait for a rising edge newer than the last consumed one rather than trusting a possibly stale high level
    private volatile uint _lastRisingEdgeSeq;
    private uint _consumedRisingEdgeSeq;

    // Signalled when there is a reason to initiate a full transfer: the data available pin has risen or
    // new data has been queued for transmission (see RequestTransfer). Kept separate from the transfer
    // ready event so that transfer ready pin edges do not needlessly wake the transfer gate
    private readonly AutoResetEvent _transferRequestEvent = new(false);
    private readonly SpiDevice _spiDevice;
    private bool _waitingForFirstTransfer = true, _connected, _hadTimeout, _resetting, _updating;
    private ushort _lastTransferNumber;

    private DateTime _lastTransferMeasureTime = DateTime.Now, _lastCodesMeasureTime = DateTime.Now;
    private volatile int _numMeasuredTransfers, _numMeasuredCodes, _maxRxSize, _maxTxSize, _numTfrPinGlitches;
    private TimeSpan _maxFullTransferDelay = TimeSpan.Zero, _maxPinWaitDurationFull = TimeSpan.Zero, _maxPinWaitDuration = TimeSpan.Zero;

    // Transfer headers
    private readonly Memory<byte> _rxHeaderBuffer = new byte[Marshal.SizeOf<TransferHeader>()];
    private readonly Memory<byte> _txHeaderBuffer = new byte[Marshal.SizeOf<TransferHeader>()];
    private TransferHeader _rxHeader;
    private TransferHeader _txHeader;
    private byte _packetId;

    // Transfer data. Keep three TX buffers so resend requests can be processed
    private readonly int _bufferSize;
    private const int NumTxBuffers = 3;
    private readonly Memory<byte> _rxBuffer;
    private readonly LinkedList<Memory<byte>> _txBuffers = new();
    private LinkedListNode<Memory<byte>> _txBuffer = null!;
    private int _rxPointer, _txPointer;
    private PacketHeader _lastPacket;
    private ReadOnlyMemory<byte> _packetData;

    // Keep track of packets being resent to avoid getting out-of-order
    private List<Protocol.SbcRequests.Request> _packetsBeingResent = [];

    /// <summary>
    /// Currently-used protocol version
    /// </summary>
    public int ProtocolVersion { get; private set; }

    /// <summary>
    /// Constructor of this class
    /// </summary>
    /// <param name="eventLogger">EVent logger</param>
    /// <param name="model">Object model</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settings">Settings</param>
    /// <exception cref="OperationCanceledException">Failed to connect to board</exception>
    public SPI(EventLogger eventLogger, Model.ObjectModel model, ILogger<SPI> logger, IOptions<Settings> settings)
    {
        // Initialize variables
        _eventLogger = eventLogger;
        _model = model;
        _logger = logger;
        _settings = settings.Value;
        _bufferSize = settings.Value.SbcBufferSize;
        _rxBuffer = new byte[_bufferSize];

        // Initialize TX header. This only needs to happen once
        Protocol.Writer.InitTransferHeader(ref _txHeader);

        // Initialize TX buffers
        for (int i = 0; i < NumTxBuffers; i++)
        {
            _txBuffers.AddLast(new byte[_bufferSize]);
        }
        _txBuffer = _txBuffers.First!;

        // Initialize transfer ready pin via the kernel GPIO character device (v1/v2 uAPI, no libgpiod)
        int transferReadyPin = settings.Value.TransferReadyPin;
        int dataAvailablePin = settings.Value.DataAvailablePin;
        _dataAvailablePin = new InputGpioPin(settings.Value.GpioChipDevice, dataAvailablePin, $"dcs-dap-{dataAvailablePin}");
        _transferReadyPin = new InputGpioPin(settings.Value.GpioChipDevice, transferReadyPin, $"dcs-trp-{transferReadyPin}");
        _lastPinValueFromCallback = _transferReadyPin.Value;
        _transferReadyPin.PinChanged += (value, sequenceNumber) =>
        {
            _lastPinValueFromCallback = value;
            if (value)
            {
                _lastRisingEdgeSeq = sequenceNumber;
            }
            _transferReadyEvent.Set();
        };
        _dataAvailablePin.PinChanged += (value, sequenceNumber) =>
        {
            if (value)
            {
                _transferRequestEvent.Set();
            }
        };
#if DEBUG
        int sbcDataAvailablePin = settings.Value.SbcDataAvailablePin;
        _sbcDataAvailablePin = new OutputGpioPin(settings.Value.GpioChipDevice, sbcDataAvailablePin, $"dcs-sbc-dap-{sbcDataAvailablePin}");
#endif
        _transferReadyPin.StartMonitoring();
        _dataAvailablePin.StartMonitoring();

        // Open the SPI device directly through the spidev character device
        _spiDevice = new SpiDevice(settings.Value.SpiDevice, settings.Value.SpiFrequency, settings.Value.SpiTransferMode);
    }

    /// <summary>
    /// Attempt to connect to the firmware
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    public void Connect(CancellationToken cancellationToken = default)
    {
        // Check if large transfers can be performed
        try
        {
            int maxSpiBufferSize = int.Parse(File.ReadAllText("/sys/module/spidev/parameters/bufsiz"));
            if (maxSpiBufferSize < _bufferSize)
            {
                _logger.LogWarning("Kernel SPI buffer size is smaller than RepRapFirmware buffer size ({MaxBufferSize} configured vs {RequiredMaxBufferSize} required)", maxSpiBufferSize, Consts.BufferSize);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to retrieve Kernel SPI buffer size");
        }

        // Perform the first transfer
        PerformFullTransfer(true, cancellationToken);
        _logger.LogInformation("Connected to controller over SPI");
    }

    /// <summary>
    /// Get the number of full transfers per second
    /// </summary>
    /// <returns>Full transfers per second</returns>
    private decimal GetFullTransfersPerSecond()
    {
        if (_numMeasuredTransfers == 0)
        {
            return 0;
        }

        decimal result = _numMeasuredTransfers / (decimal)(DateTime.Now - _lastTransferMeasureTime).TotalSeconds;
        _lastTransferMeasureTime = DateTime.Now;
        _numMeasuredTransfers = 0;
        return result;
    }

    /// <summary>
    /// Get the number of transferred codes per second and reset the counter
    /// </summary>
    /// <returns>Full transfers per second</returns>
    private decimal GetCodesPerSecond()
    {
        if (_numMeasuredCodes == 0)
        {
            return 0;
        }

        decimal result = _numMeasuredCodes / (decimal)(DateTime.Now - _lastCodesMeasureTime).TotalSeconds;
        _lastCodesMeasureTime = DateTime.Now;
        _numMeasuredCodes = 0;
        return result;
    }

    /// <summary>
    /// Get the maximum time between two full transfers
    /// </summary>
    /// <returns>Time in ms</returns>
    public double GetMaxFullTransferDelay()
    {
        double result = _maxFullTransferDelay.TotalMilliseconds;
        _maxFullTransferDelay = TimeSpan.Zero;
        return result;
    }

    /// <summary>
    /// Get the maximum time to wait for the transfer ready pin to be toggled and reset the counter
    /// </summary>
    /// <param name="fullTransferCounter">Query and reset the full transfer duration</param>
    /// <returns>Time in ms</returns>
    public double GetMaxPinWaitDuration(bool fullTransferCounter)
    {
        if (fullTransferCounter)
        {
            double fullResult = _maxPinWaitDurationFull.TotalMilliseconds;
            _maxPinWaitDurationFull = TimeSpan.Zero;
            return fullResult;
        }

        double result = _maxPinWaitDuration.TotalMilliseconds;
        _maxPinWaitDuration = TimeSpan.Zero;
        return result;
    }

    /// <summary>
    /// Print diagnostics to the given string builder
    /// </summary>
    /// <param name="builder">Target to write to</param>
    public void PrintDiagnostics(StringBuilder builder)
    {
        if (_settings.CommunicationMethod != CommunicationMethod.SPI)
        {
            return;
        }

        builder.AppendLine($"Configured SPI speed: {_settings.SpiFrequency}Hz, TfrRdy pin glitches: {_numTfrPinGlitches}, missed edges: {_transferReadyPin.MissedEdges}");
        builder.AppendLine($"Full transfers per second: {GetFullTransfersPerSecond():F2}, max time between full transfers: {GetMaxFullTransferDelay():0.0}ms, max pin wait times: {GetMaxPinWaitDuration(true):0.0}ms/{GetMaxPinWaitDuration(false):0.0}ms");
        builder.AppendLine($"Codes per second: {GetCodesPerSecond():F2}");
        builder.AppendLine($"Maximum length of RX/TX data transfers: {_maxRxSize}/{_maxTxSize}");
    }

    /// <summary>
    /// Static stopwatch to measure the times between full transfers with
    /// </summary>
    private readonly Stopwatch _fullTransferStopwatch = new();

    /// <summary>
    /// Stopwatch tracking the time elapsed since the last full transfer, used to force keep-alive transfers
    /// </summary>
    private readonly Stopwatch _keepAliveStopwatch = new();

    /// <summary>
    /// Perform a full data transfer synchronously
    /// </summary>
    /// <param name="connecting">Whether this an initial connection is being established</param>
    /// <param name="cancellationToken">Cancellation token to cancel the transfer</param>
    public void PerformFullTransfer(bool connecting = false, CancellationToken cancellationToken = default)
    {
        _packetsBeingResent.Clear();
        _lastTransferNumber = _rxHeader.SequenceNumber;

        // Reset RX transfer header
        _rxHeader.FormatCode = Consts.InvalidFormatCode;
        _rxHeader.NumPackets = 0;
        _rxHeader.ProtocolVersion = 0;
        _rxHeader.DataLength = 0;
        _rxHeader.ChecksumData32 = 0;
        _rxHeader.ChecksumHeader32 = 0;

        // Set up TX transfer header
        _txHeader.NumPackets = _packetId;
        _txHeader.SequenceNumber++;
        _txHeader.DataLength = (ushort)_txPointer;
        WriteCRC();

        // Perform the transfer
        int retry = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Don't retry forever
                if (retry > _settings.MaxSbcRetries)
                {
                    throw new OperationCanceledException("Maximum number of SPI transfer retries exceeded");
                }

                // Keep track of the maximum times between regular full transfers
                if (!connecting && !_waitingForFirstTransfer && _connected && !_hadTimeout && !_updating && !_resetting)
                {
                    if (_fullTransferStopwatch.IsRunning)
                    {
                        TimeSpan timeElapsed = _fullTransferStopwatch.Elapsed;
                        if (timeElapsed > _maxFullTransferDelay)
                        {
                            _maxFullTransferDelay = timeElapsed;
                        }
                        _fullTransferStopwatch.Reset();
                    }
                    else
                    {
                        _fullTransferStopwatch.Start();
                    }
                }

                // Exchange transfer headers. This also deals with transfer responses
                if (!ExchangeHeader())
                {
                    retry++;
                    continue;
                }

                // Exchange data if there is anything to transfer
                if ((_rxHeader.DataLength != 0 || _txPointer != 0) && !ExchangeData())
                {
                    retry++;
                    continue;
                }

                // Verify the protocol version
                ProtocolVersion = _rxHeader.ProtocolVersion;
                if ((_hadTimeout || !_connected) && ProtocolVersion != Consts.ProtocolVersion)
                {
                    _eventLogger.LogOutput(MessageType.Warning, "Incompatible firmware, please upgrade as soon as possible");
                }

                // Deal with timeouts and the first transmission
                if (_hadTimeout)
                {
                    _eventLogger.LogOutput(MessageType.Success, "Connection to Duet established");
                    _hadTimeout = _resetting = false;
                }
                else if (!_connected)
                {
                    _lastTransferNumber = (ushort)(_rxHeader.SequenceNumber - 1);
                }
                _connected = true;

                // Transfer OK
                _numMeasuredTransfers++;
                if (_maxRxSize < _rxHeader.DataLength)
                {
                    _maxRxSize = _rxHeader.DataLength;
                }
                if (_maxTxSize < _txHeader.DataLength)
                {
                    _maxTxSize = _txHeader.DataLength;
                }
                _txBuffer = _txBuffer.Next ?? _txBuffers.First!;
                _rxPointer = _txPointer = 0;
                _packetId = 0;
                _keepAliveStopwatch.Restart();
                break;
            }
            catch (OperationCanceledException e)
            {
                if (connecting || cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                _logger.LogDebug(e, "Lost connection to Duet");
                _txHeader.ProtocolVersion = Consts.ProtocolVersion;
                _waitingForFirstTransfer = true;

                if (!_hadTimeout && _connected)
                {
                    _hadTimeout = true;
                    _model.ConnectionLost();
                    _eventLogger.LogOutput(MessageType.Warning, $"Lost connection to Duet ({e.Message})");
                }
                _connected = false;
            }
        }

#if DEBUG
        _sbcDataAvailablePin?.Write(false);
#endif
    }

    /// <summary>
    /// Check if the controller has been reset
    /// </summary>
    /// <returns>Whether the controller has been reset</returns>
    public bool HadReset() => _connected && ((ushort)(_lastTransferNumber + 1) != _rxHeader.SequenceNumber);

    #region Read functions
    /// <summary>
    /// Returns the number of packets to read
    /// </summary>
    public int PacketsToRead => _rxHeader.NumPackets;

    /// <summary>
    /// Read the next packet
    /// </summary>
    /// <returns>The next packet or null if none is available</returns>
    public PacketHeader? ReadNextPacket()
    {
        if (_rxPointer >= _rxHeader.DataLength)
        {
            return null;
        }

        // Header
        _rxPointer += Protocol.Reader.ReadPacketHeader(_rxBuffer[_rxPointer..].Span, out _lastPacket);

        // Packet data
        _packetData = _rxBuffer.Slice(_rxPointer, _lastPacket.Length);
        int padding = 4 - (_lastPacket.Length % 4);
        _rxPointer += _lastPacket.Length + ((padding == 4) ? 0 : padding);

        return _lastPacket;
    }

    /// <summary>
    /// Read a code buffer update
    /// </summary>
    /// <param name="bufferSpace">Buffer space</param>
    public void ReadCodeBufferUpdate(out ushort bufferSpace)
    {
        Protocol.Reader.ReadCodeBufferUpdate(_packetData.Span, out bufferSpace);
    }

    /// <summary>
    /// Read an incoming message
    /// </summary>
    /// <param name="messageType">Message type flags of the reply</param>
    /// <param name="reply">Code reply</param>
    public void ReadMessage(out MessageTypeFlags messageType, out string reply)
    {
        Protocol.Reader.ReadMessage(_packetData.Span, out messageType, out reply);
    }

    /// <summary>
    /// Read a code channel
    /// </summary>
    /// <param name="channel">Code channel that has acquired the lock</param>
    /// <returns>Asynchronous task</returns>
    public void ReadCodeChannel(out CodeChannel channel)
    {
        Protocol.Reader.ReadCodeChannel(_packetData.Span, out channel);
    }

    /// <summary>
    /// Read a forwarded CAN message (single fragment) from an expansion board
    /// </summary>
    public void ReadCanResponse(out ushort txToken, out CanMessageType msgType, out byte srcAddress, out byte flags, out CanStatus status, out byte[] payload)
    {
        Protocol.Reader.ReadCANResponse(_packetData.Span, out txToken, out msgType, out srcAddress, out flags, out status, out payload);
    }

    /// <summary>
    /// Write the last packet + content for diagnostic purposes
    /// </summary>
    public void DumpMalformedPacket()
    {
        using (FileStream stream = new(Path.Combine(_settings.BaseDirectory, "sys/transferDump.bin"), FileMode.Create, FileAccess.Write))
        {
            stream.Write(_rxBuffer[.._rxHeader.DataLength].Span);
        }

        string dump = $"=== Packet #{_lastPacket.Id} from offset {_rxPointer} request {_lastPacket.Request} (length {_lastPacket.Length}) ===\n";
        foreach (byte c in _packetData.Span)
        {
            dump += ((int)c).ToString("x2");
        }
        dump += "\n";
        string str = Encoding.UTF8.GetString(_packetData.Span);
        foreach (char c in str)
        {
            dump += char.IsLetterOrDigit(c) ? c : '.';
        }
        dump += "\n";
        dump += "====================";
        _logger.LogError("Received malformed packet: {SpiDump}", dump);
    }
    #endregion

    #region Write functions
    /// <summary>
    /// Write a packet
    /// </summary>
    /// <param name="request">SBC request to send</param>
    /// <param name="dataLength">Length of the extra payload</param>
    private void WritePacket(Protocol.SbcRequests.Request request, int dataLength = 0)
    {
        PacketHeader header = new()
        {
            Request = (ushort)request,
            Id = _packetId++,
            Length = (ushort)dataLength,
            ResendPacketId = 0
        };

        Span<byte> span = _txBuffer.Value[_txPointer..].Span;
        MemoryMarshal.Write(span, header);
        _txPointer += Marshal.SizeOf<PacketHeader>();
    }

    /// <summary>
    /// Get a span on a 4-byte boundary for writing packet data
    /// </summary>
    /// <param name="dataLength">Required data length</param>
    /// <returns>Data span</returns>
    private Span<byte> GetWriteBuffer(int dataLength)
    {
        int padding = 4 - (dataLength % 4);
        if (padding != 4)
        {
            dataLength += padding;
        }

        Span<byte> result = _txBuffer.Value.Slice(_txPointer, dataLength).Span;
        _txPointer += dataLength;
        return result;
    }

    /// <summary>
    /// Resend a packet back to the firmware
    /// </summary>
    /// <param name="packet">Packet holding the resend request</param>
    /// <param name="sbcRequest">Content of the packet to resend</param>
    public void ResendPacket(PacketHeader packet, out Protocol.SbcRequests.Request sbcRequest)
    {
        Span<byte> buffer = (_txBuffer.Next ?? _txBuffers.First!).Value.Span;

        PacketHeader header;
        int headerSize = Marshal.SizeOf<PacketHeader>();
        do
        {
            // Read next packet
            header = MemoryMarshal.Cast<byte, PacketHeader>(buffer)[0];
            if (header.Id == packet.ResendPacketId)
            {
                // Resend it but use a new identifier
                sbcRequest = (Protocol.SbcRequests.Request)header.Request;
                WritePacket(sbcRequest, header.Length);
                buffer.Slice(headerSize, header.Length).CopyTo(GetWriteBuffer(header.Length));

                // Keep track of it
                if (!_packetsBeingResent.Contains(sbcRequest))
                {
                    _packetsBeingResent.Add(sbcRequest);
                }
                return;
            }

            // Move on to the next one
            int padding = 4 - (header.Length % 4);
            buffer = buffer[(headerSize + header.Length + ((padding == 4) ? 0 : padding))..];
        }
        while (header.Id < packet.ResendPacketId && buffer.Length > 0);

        throw new ArgumentException($"Firmware requested resend for invalid packet #{packet.ResendPacketId}");
    }

    /// <summary>
    /// Request an emergency stop
    /// </summary>
    /// <returns>True if the packet could be written</returns>
    public bool WriteEmergencyStop()
    {
        // E-STOP is unconditional
        if (!CanWritePacket())
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.EmergencyStop);
        return true;
    }

    /// <summary>
    /// Request a firmware reset
    /// </summary>
    /// <returns>True if the packet could be written</returns>
    public bool WriteReset()
    {
        // Reset is unconditional
        if (!CanWritePacket())
        {
            return false;
        }

        _txPointer = 0;
        _resetting = true;
        WritePacket(Protocol.SbcRequests.Request.Reset);
        return true;
    }

    /// <summary>
    /// Write another segment of the IAP binary
    /// </summary>
    /// <param name="stream">IAP binary</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether another segment could be written</returns>
    public bool WriteIapSegment(Stream stream, CancellationToken cancellationToken = default)
    {
        Span<byte> data = stackalloc byte[Consts.IapSegmentSize];
        int bytesRead = stream.Read(data);
        if (bytesRead <= 0)
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.WriteIap, bytesRead);
        data[..bytesRead].CopyTo(GetWriteBuffer(bytesRead));
        PerformFullTransfer(cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>
    /// Instruct the firmware to start the IAP binary
    /// </summary>
    /// <param name="firmwareLength">Firmware length (unused by SPI IAP but present for interface parity with USB)</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    public void StartIap(uint firmwareLength, CancellationToken cancellationToken = default)
    {
        _ = firmwareLength;

        // Tell the firmware to boot the IAP program
        WritePacket(Protocol.SbcRequests.Request.StartIap);
        PerformFullTransfer(cancellationToken: cancellationToken);

        // Wait for the first transfer
        // The IAP firmware will pull the transfer ready pin to high when it is ready to receive data
        _waitingForFirstTransfer = _updating = true;
    }

    /// <summary>
    /// Flash another segment of the firmware via the IAP binary
    /// </summary>
    /// <param name="stream">Stream of the firmware binary</param>
    /// <returns>Whether another segment could be sent</returns>
    public bool FlashFirmwareSegment(Stream stream)
    {
        Span<byte> readBuffer = stackalloc byte[Consts.FirmwareSegmentSize];
        Span<byte> writeBuffer = stackalloc byte[Consts.FirmwareSegmentSize];

        int bytesRead = stream.Read(writeBuffer);
        if (bytesRead <= 0)
        {
            return false;
        }

        if (bytesRead != Consts.FirmwareSegmentSize)
        {
            // Fill up the remaining space with 0xFF. The IAP program does the same once complete
            writeBuffer[bytesRead..].Fill(0xFF);
        }

        WaitForTransfer();
        _spiDevice.TransferFullDuplex(writeBuffer, readBuffer);
        return true;
    }

    /// <summary>
    /// Send the CRC16 checksum of the firmware binary to the IAP program and verify the written data
    /// </summary>
    /// <param name="firmwareLength">Length of the written firmware in bytes</param>
    /// <param name="crc16">CRC16 checksum of the firmware</param>
    /// <returns>Whether the firmware has been written successfully</returns>
    public bool VerifyFirmwareChecksum(long firmwareLength, ushort crc16)
    {
        // At this point IAP expects another segment so wait for it to be ready first. After that, wait a moment for IAP to acknowledge we're done
        WaitForTransfer();
        Thread.Sleep(Consts.FirmwareFinishedDelay);

        // Send the final firmware size plus CRC16 checksum to IAP
        Protocol.SbcRequests.FlashVerify verifyRequest = new()
        {
            firmwareLength = (uint)firmwareLength,
            crc16 = crc16
        };
        Span<byte> transferData = stackalloc byte[Marshal.SizeOf<Protocol.SbcRequests.FlashVerify>()];
        MemoryMarshal.Write(transferData, verifyRequest);
        WaitForTransfer();
        _spiDevice.TransferFullDuplex(transferData, transferData);

        // Check if the IAP can confirm our CRC16 checksum
        Span<byte> writeOk = stackalloc byte[1];
        WaitForTransfer();
        _spiDevice.TransferFullDuplex(writeOk, writeOk);
        return writeOk[0] == 0x0C;
    }

    /// <summary>
    /// Wait for the IAP program to reset the controller
    /// </summary>
    public void WaitForIapReset()
    {
        // Wait a moment for the firmware to start
        Thread.Sleep(Consts.IapRebootDelay);

        // Wait for the first data transfer from the firmware
        _updating = _connected = false;
        _waitingForFirstTransfer = true;
        _rxHeader.SequenceNumber = 1;
        _txHeader.SequenceNumber = 0;
    }

    /// <summary>
    /// Write a message
    /// </summary>
    /// <param name="flags">Message flags</param>
    /// <param name="message">Message content</param>
    /// <returns>Whether the firmware has been written successfully</returns>
    public bool WriteMessage(MessageTypeFlags flags, string message)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.Message))
        {
            return false;
        }

        // Serialize the request first to see how much space it requires
        Span<byte> span = stackalloc byte[_bufferSize - Marshal.SizeOf<PacketHeader>()];
        int dataLength = Protocol.Writer.WriteMessage(span, flags, message);

        // See if the request fits into the buffer
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        // Write it
        WritePacket(Protocol.SbcRequests.Request.Message, dataLength);
        span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
        return true;
    }

    /// <summary>
    /// Send a CAN message to an expansion board
    /// </summary>
    /// <returns>Whether the request could be written</returns>
    public bool WriteCanMessage(ushort txToken, ushort msgType, ushort replyType, byte dstAddress, byte flags, ReadOnlySpan<byte> payload)
    {
        // Serialize the request first to see how much space it requires
        Span<byte> span = stackalloc byte[_bufferSize - Marshal.SizeOf<PacketHeader>()];
        int dataLength = Protocol.Writer.WriteCANMessage(span, txToken, msgType, replyType, dstAddress, flags, payload);

        // See if the request fits into the buffer
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        // Write it
        WritePacket(Protocol.SbcRequests.Request.SendCANMessage, dataLength);
        span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
        return true;
    }

    /// <summary>
    /// Checks if there is enough remaining space to accomodate a packet header plus payload data
    /// </summary>
    /// <param name="dataLength">Payload data length</param>
    /// <returns>True if there is enough space</returns>
    private bool CanWritePacket(int dataLength = 0)
    {
        return _txPointer + Marshal.SizeOf<PacketHeader>() + dataLength <= _bufferSize;
    }
    #endregion

    #region Functions for data transfers
    /// <summary>
    /// Decide whether a full transfer should be started, blocking while idle until there is a reason to.
    /// The transfer loop must call this in a loop after staging outgoing data, transferring only once this
    /// returns <c>true</c>, e.g. <c>do { StageOutgoingData(); } while (!WaitForTransferReason());</c>. When
    /// it returns <c>false</c> the caller should stage any newly queued data and call again; because the
    /// data is (re-)staged immediately before every decision, this avoids both a leading and a trailing
    /// empty transfer. The transfer itself is still gated by the transfer ready pin in
    /// <see cref="WaitForTransfer"/>; this only decides whether to start one at all. A transfer is started
    /// (returns <c>true</c>) once any of the following holds:
    /// <list type="number">
    /// <item>DSF has data staged to send</item>
    /// <item>the controller has raised the data available pin to signal that it wants to send</item>
    /// <item>the keep-alive interval (<see cref="Settings.SbcConnectionKeepAliveInterval"/>) has elapsed
    /// since the last full transfer, so disconnects are still detected while idle</item>
    /// </list>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the wait</param>
    /// <returns>True if a transfer should be started now, false if the caller should re-stage data and retry</returns>
    public bool WaitForTransferReason(CancellationToken cancellationToken = default)
    {
        // Only gate transfers during normal operation; while connecting, reconnecting, resetting or updating
        // the protocol must always be free to make progress
        if (!_connected || _hadTimeout || _waitingForFirstTransfer || _updating || _resetting)
        {
            return true;
        }

        // Start the transfer straight away if DSF has data staged for transmission or the controller is
        // holding the data available pin high
        if (_txPointer != 0 || _dataAvailablePin.Read())
        {
            return true;
        }

        // Perform a keep-alive transfer if the keep-alive interval has elapsed since the last transfer
        int timeToWait = _settings.SbcConnectionKeepAliveInterval - (int)_keepAliveStopwatch.ElapsedMilliseconds;
        if (timeToWait <= 0)
        {
            return true;
        }

        // Otherwise block until a reason appears or the remaining keep-alive interval elapses, then let the
        // caller re-stage data and call again. The event is raised when the data available pin rises or new
        // data is queued for transmission (RequestTransfer); being an AutoResetEvent it stays signalled if
        // raised just before the wait, so a wake-up cannot be lost, and the single kernel wait avoids polling
        WaitHandle.WaitAny([_transferRequestEvent, cancellationToken.WaitHandle], timeToWait);

        // Proceed on cancellation so the caller can handle shutdown normally; otherwise re-stage and retry
        return cancellationToken.IsCancellationRequested;
    }

    /// <summary>
    /// Notify the transfer loop that there is a reason to initiate a full transfer, e.g. because new data
    /// has been queued for transmission. This wakes up <see cref="WaitForTransferReason"/> if it is waiting
    /// </summary>
    public void RequestTransfer()
    {
        _transferRequestEvent.Set();
#if DEBUG
        _sbcDataAvailablePin?.Write(true);
#endif
    }

    /// <summary>
    /// Wait for the controller to flag via the transfer ready pin that the next exchange can be performed.
    /// Within a transfer the controller pulses the pin per sub-exchange: it lowers the pin while it processes
    /// each exchange and raises it again when ready for the next. A sub-exchange (<paramref name="inTransfer"/>
    /// is true) must therefore wait for a rising edge newer than the one consumed for the previous exchange -
    /// trusting a high level alone would let a stale high left over from the previous exchange clock the next
    /// one before the controller is ready, which desynchronises the two sides. The header exchange
    /// (<paramref name="inTransfer"/> is false) and the first transfer after connecting instead run against
    /// the steady "ready" high level
    /// </summary>
    /// <param name="inTransfer">Whether a full transfer is being performed</param>
    /// <param name="cancellationToken">Cancellation token to cancel the wait</param>
    private void WaitForTransfer(bool inTransfer = true, CancellationToken cancellationToken = default)
    {
        // Sub-exchanges require a fresh rising edge; the header and the first transfer run against the level
        bool needFreshEdge = inTransfer && !_waitingForFirstTransfer;

        // Whether the transfer ready pin currently signals readiness for this exchange
        bool IsReady() => _transferReadyPin.Read() && (!needFreshEdge || (int)(_lastRisingEdgeSeq - _consumedRisingEdgeSeq) > 0);

        // Flush pending events by consuming them until the event stays reset
        while (_transferReadyEvent.Wait(0))
        {
            _transferReadyEvent.Reset();
        }

        // Proceed immediately if already ready, otherwise wait for the controller to (re-)assert the pin
        if (!IsReady())
        {
            // Determine how long to wait for the pin to be (re-)asserted
            int timeout;
            if (_waitingForFirstTransfer)
            {
                timeout = _updating ? Consts.IapTimeout : _settings.SbcConnectTimeout;
            }
            else
            {
                timeout = _updating ? Consts.IapTimeout : (inTransfer ? _settings.SbcTransferTimeout : _settings.SbcConnectionTimeout);
            }

            // Wait for the pin to be (re-)asserted, ignoring glitches
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                do
                {
                    int timeToWait = timeout - (int)stopwatch.ElapsedMilliseconds;
                    if (timeToWait <= 0 || cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException();
                    }

                    // Wait for any pin change event
                    if (_transferReadyEvent.Wait(timeToWait))
                    {
                        _transferReadyEvent.Reset();

                        // Only a rising edge is of interest; ignore falling edges
                        if (_lastPinValueFromCallback)
                        {
                            if (IsReady())
                            {
                                break;
                            }
                            // Callback reported a rising edge but the pin is no longer stably high, count as glitch
                            if (!_transferReadyPin.Read())
                            {
                                _numTfrPinGlitches++;
                            }
                        }
                    }
                } while (true);
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (stopwatch.ElapsedMilliseconds > timeout + 125)
                {
                    // In case the CTS is triggered very late, this application may not have gotten enough CPU time. Log this
                    _eventLogger.LogOutput(MessageType.Warning, "Did not get enough CPU time during SPI transfer, your SBC may be overloaded");
                }
                throw new OperationCanceledException($"{(inTransfer ? "Transfer" : "Connection")} timeout while waiting for TfrRdy pin");
            }

            // Keep track of the maximum wait times
            if (inTransfer)
            {
                if (stopwatch.Elapsed > _maxPinWaitDuration)
                {
                    _maxPinWaitDuration = stopwatch.Elapsed;
                }
            }
            else if (!_waitingForFirstTransfer)
            {
                if (stopwatch.Elapsed > _maxPinWaitDurationFull)
                {
                    _maxPinWaitDurationFull = stopwatch.Elapsed;
                }
            }
        }

        // Record the rising edge consumed for this exchange so the next sub-exchange waits for a newer one
        _consumedRisingEdgeSeq = _lastRisingEdgeSeq;
        _waitingForFirstTransfer = false;
    }

    /// <summary>
    /// Write the CRC16 or CRC32 checksums
    /// </summary>
    private void WriteCRC()
    {
        if (_txHeader.ProtocolVersion >= 4)
        {
            _txHeader.ChecksumData32 = CRC32.Calculate(_txBuffer.Value[.._txPointer].Span);
            MemoryMarshal.Write(_txHeaderBuffer.Span, _txHeader);
            _txHeader.ChecksumHeader32 = CRC32.Calculate(_txHeaderBuffer[..12].Span);
            MemoryMarshal.Write(_txHeaderBuffer.Span, _txHeader);
        }
        else
        {
            _txHeader.ChecksumData16 = CRC16.Calculate(_txBuffer.Value[.._txPointer].Span);
            MemoryMarshal.Write(_txHeaderBuffer.Span, _txHeader);
            _txHeader.ChecksumHeader16 = CRC16.Calculate(_txHeaderBuffer[..10].Span);
            MemoryMarshal.Write(_txHeaderBuffer.Span, _txHeader);
        }
    }

    /// <summary>
    /// Exchange the transfer header
    /// </summary>
    /// <returns>True on success</returns>
    private bool ExchangeHeader()
    {
        for (int retry = 0; retry < _settings.MaxSbcRetries; retry++)
        {
            // Perform SPI header exchange
            WaitForTransfer(false);
            if (_txHeader.ProtocolVersion >= 4)
            {
                _spiDevice.TransferFullDuplex(_txHeaderBuffer.Span, _rxHeaderBuffer.Span);
            }
            else
            {
                _spiDevice.TransferFullDuplex(_txHeaderBuffer[..12].Span, _rxHeaderBuffer[..12].Span);
            }

            // Check for possible response code
            uint responseCode = MemoryMarshal.Read<uint>(_rxHeaderBuffer.Span);
            if (responseCode == TransferResponse.BadResponse)
            {
                _logger.LogWarning("Received bad response instead of header, retrying exchange of the data response");
                if (_connected && ExchangeDataResponse(out bool success) && success)
                {
                    continue;
                }
                throw new OperationCanceledException("SPI data transfer failed");
            }

            // Read received header and verify the format code
            _rxHeader = MemoryMarshal.Read<TransferHeader>(_rxHeaderBuffer.Span);
            if (_rxHeader.FormatCode == 0 || _rxHeader.FormatCode == 0xFF)
            {
                _logger.LogWarning("Restarting full transfer because a bad header format code was received (0x{0:x2})", _rxHeader.FormatCode);
                ExchangeResponse(TransferResponse.BadResponse);
                return false;
            }

            // Change the protocol version if necessary
            ushort lastProtocolVersion = _txHeader.ProtocolVersion;
            if (_rxHeader.ProtocolVersion != lastProtocolVersion &&
                (_rxHeader.ProtocolVersion <= Consts.ProtocolVersion || _settings.UpdateOnly))
            {
                _txHeader.ProtocolVersion = _rxHeader.ProtocolVersion;
                WriteCRC();

                ExchangeResponse(TransferResponse.BadResponse);
                continue;
            }

            // Verify header checksum
            if (_rxHeader.ProtocolVersion >= 4)
            {
                uint crc32 = CRC32.Calculate(_rxHeaderBuffer[..12].Span);
                if (_rxHeader.ChecksumHeader32 != crc32)
                {
                    _logger.LogWarning("Bad header CRC32 (expected 0x{ExpectedChecksum:x8}, got 0x{ActualChecksum:x8})", _rxHeader.ChecksumHeader32.ToString("x8"), crc32.ToString(""));
                    responseCode = ExchangeResponse(TransferResponse.BadHeaderChecksum);
                    if (responseCode == TransferResponse.BadHeaderChecksum)
                    {
                        _logger.LogWarning("Note: RepRapFirmware didn't receive valid data either (code 0x{ResponseCode:x8})", responseCode);
                    }
                    else
                    {
                        if (responseCode == TransferResponse.BadResponse)
                        {
                            _logger.LogWarning("Restarting full transfer because RepRapFirmware received a bad header response");
                        }
                        else
                        {
                            _logger.LogWarning("Restarting full transfer because an unexpected response code has been received (code 0x{ResponseCode:x8})", responseCode);
                            ExchangeResponse(TransferResponse.BadResponse);
                        }
                        return false;
                    }
                    continue;
                }
            }
            else
            {
                ushort crc16 = CRC16.Calculate(_rxHeaderBuffer[..10].Span);
                if (_rxHeader.ChecksumHeader16 != crc16)
                {
                    _logger.LogWarning("Bad header CRC16 (expected 0x{ExpectedChecksum:x4}, got 0x{ActualChecksum:x4})", _rxHeader.ChecksumHeader16, crc16);
                    responseCode = ExchangeResponse(TransferResponse.BadHeaderChecksum);
                    if (responseCode == TransferResponse.BadResponse)
                    {
                        _logger.LogWarning("Restarting full transfer because RepRapFirmware received a bad header response");
                        return false;
                    }
                    if (responseCode != TransferResponse.Success)
                    {
                        _logger.LogWarning("Note: RepRapFirmware didn't receive valid data either (code 0x{ResponseCode:x8})", responseCode);
                    }
                    continue;
                }
            }

            // Check format code
            switch (_rxHeader.FormatCode)
            {
                case Consts.FormatCode:
                    // Format code OK
                    break;

                case Consts.FormatCodeStandalone:
                    // RRF is operating in stand-alone mode
                    throw new Exception("RepRapFirmware is operating in stand-alone mode");

                default:
                    ExchangeResponse(TransferResponse.BadFormat);
                    throw new Exception($"Invalid format code {_rxHeader.FormatCode:x2}");
            }

            // Check for changed protocol version
            if (_rxHeader.ProtocolVersion > Consts.ProtocolVersion && !_settings.UpdateOnly)
            {
                ExchangeResponse(TransferResponse.BadProtocolVersion);
                throw new Exception($"Invalid protocol version {_rxHeader.ProtocolVersion}");
            }

            if (lastProtocolVersion != _txHeader.ProtocolVersion)
            {
                _logger.LogWarning(_txHeader.ProtocolVersion < Consts.ProtocolVersion ? "Downgrading protocol version {ProtocolVersion} to {DowngradedProtocolVersion}" : "Upgrading protocol version {ProtocolVersion} to {UpgradedProtocolVersion}", lastProtocolVersion, _txHeader.ProtocolVersion);
            }

            // Check the data length
            if (_rxHeader.DataLength > _bufferSize)
            {
                ExchangeResponse(TransferResponse.BadDataLength);
                throw new Exception($"Data too long ({_rxHeader.DataLength} bytes)");
            }

            // Acknowledge receipt
            uint response = ExchangeResponse(TransferResponse.Success);
            switch (response)
            {
                case TransferResponse.Success:
                    return true;
                case TransferResponse.BadFormat:
                    throw new Exception("RepRapFirmware refused message format");
                case TransferResponse.BadProtocolVersion:
                    throw new Exception("RepRapFirmware refused protocol version");
                case TransferResponse.BadDataLength:
                    throw new Exception("RepRapFirmware refused data length");
                case TransferResponse.BadHeaderChecksum:
                    _logger.LogWarning("RepRapFirmware got a bad header checksum");
                    continue;
                case TransferResponse.BadResponse:
                    _logger.LogWarning("Restarting full transfer because RepRapFirmware received a bad header response");
                    return false;
                default:
                    _logger.LogWarning("Restarting full transfer because a bad header response was received (0x{ResponseCode:x8})", response);
                    if (_rxHeader.DataLength == 0 && _txPointer == 0)
                    {
                        // No data was transferred so we are still in sync. Continue with the next transfer
                        _lastTransferNumber = (ushort)(_rxHeader.SequenceNumber - 1);
                        return true;
                    }

                    // Transfer bad data response to restart the transfer
                    ExchangeResponse(TransferResponse.BadResponse);
                    return false;
            }
        }

        _logger.LogWarning("Restarting full transfer because the number of maximum retries has been exceeded");
        ExchangeResponse(TransferResponse.BadResponse);
        return false;
    }

    /// <summary>
    /// Exchange a response code
    /// </summary>
    /// <param name="response">Response to send</param>
    /// <returns>Received response</returns>
    private uint ExchangeResponse(uint response)
    {
        Span<byte> txResponseBuffer = stackalloc byte[sizeof(uint)], rxResponseBuffer = stackalloc byte[sizeof(uint)];
        MemoryMarshal.Write(txResponseBuffer, response);

        WaitForTransfer();
        _spiDevice.TransferFullDuplex(txResponseBuffer, rxResponseBuffer);

        return MemoryMarshal.Read<uint>(rxResponseBuffer);
    }

    /// <summary>
    /// Exchange the transfer body
    /// </summary>
    /// <returns>True on success</returns>
    private bool ExchangeData()
    {
        int bytesToTransfer = Math.Max(_rxHeader.DataLength, _txPointer);
        for (int retry = 0; retry < _settings.MaxSbcRetries; retry++)
        {
            WaitForTransfer();
            _spiDevice.TransferFullDuplex(_txBuffer.Value[..bytesToTransfer].Span, _rxBuffer[..bytesToTransfer].Span);

            // Check for possible response code
            uint responseCode = MemoryMarshal.Read<uint>(_rxBuffer.Span);
            if (responseCode == TransferResponse.BadResponse)
            {
                _logger.LogWarning("Restarting full transfer because RepRapFirmware received a bad data response");
                return false;
            }

            // Inspect received data
            if (_rxHeader.ProtocolVersion >= 4)
            {
                uint crc32 = CRC32.Calculate(_rxBuffer[.._rxHeader.DataLength].Span);
                if (crc32 != _rxHeader.ChecksumData32)
                {
                    _logger.LogWarning("Bad data CRC32 (expected 0x{ExpectedChecksum:x8}, got 0x{ActualChecksum:x8})", _rxHeader.ChecksumData32, crc32);
                    responseCode = ExchangeResponse(TransferResponse.BadDataChecksum);
                    if (responseCode == TransferResponse.BadDataChecksum)
                    {
                        _logger.LogWarning("Note: RepRapFirmware didn't receive valid data either (code 0x{0:x8})", responseCode);
                    }
                    else
                    {
                        if (responseCode == TransferResponse.BadResponse)
                        {
                            _logger.LogWarning("Restarting full transfer because RepRapFirmware received a bad data response");
                        }
                        else
                        {
                            _logger.LogWarning("Restarting full transfer because an unexpected response code has been received (code 0x{ResponseCode:x8})", responseCode);
                            ExchangeResponse(TransferResponse.BadResponse);
                        }
                        return false;
                    }
                    continue;
                }
            }
            else
            {
                ushort crc16 = CRC16.Calculate(_rxBuffer[.._rxHeader.DataLength].Span);
                if (crc16 != _rxHeader.ChecksumData16)
                {
                    _logger.LogWarning("Bad data CRC16 (expected 0x{ExpectedChecksum:x4}, got 0x{ActualChecksum:x4})", _rxHeader.ChecksumData16, crc16);
                    responseCode = ExchangeResponse(TransferResponse.BadDataChecksum);
                    if (responseCode == TransferResponse.BadResponse)
                    {
                        _logger.LogWarning("Restarting full transfer because RepRapFirmware received a bad data response");
                        return false;
                    }
                    if (responseCode != TransferResponse.Success)
                    {
                        _logger.LogWarning("Note: RepRapFirmware didn't receive valid data either (code 0x{ResponseCode:x8})", responseCode);
                    }
                    continue;
                }
            }

            // Exchange data response and restart the data transfer if it failed
            if (ExchangeDataResponse(out bool success))
            {
                return success;
            }
        }
        throw new OperationCanceledException("SPI connection reset because the number of maximum retries has been exceeded");
    }

    /// <summary>
    /// Exchange the data response
    /// </summary>
    /// <param name="success">Whether the transfer was successful</param>
    /// <returns>True when done</returns>
    private bool ExchangeDataResponse(out bool success)
    {
        for (int retry = 0; retry < _settings.MaxSbcRetries; retry++)
        {
            uint responseCode = ExchangeResponse(TransferResponse.Success);
            switch (responseCode)
            {
                case TransferResponse.Success:
                    success = true;
                    return true;
                case TransferResponse.BadDataChecksum:
                    _logger.LogWarning("RepRapFirmware got a bad data checksum");
                    success = false;
                    return false;
                case TransferResponse.BadResponse:
                    _logger.LogWarning("Restarting full transfer because RepRapFirmware received a bad data response");
                    success = false;
                    return true;
                default:
                    _logger.LogWarning("Restarting data response exchange because a bad code was received (0x{ResponseCode:x8})", responseCode);
                    ExchangeResponse(TransferResponse.BadResponse);
                    continue;
            }
        }
        throw new OperationCanceledException("SPI connection reset because the number of maximum retries has been exceeded");
    }
    #endregion
}
