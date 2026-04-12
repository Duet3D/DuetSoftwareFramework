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
using System.Device.Spi;
using SpiDevice = System.Device.Spi.SpiDevice;
using System.Device.Gpio;
using System.Device.Gpio.Drivers;

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
    private readonly GpioController _gpioController;
    private readonly int _transferReadyPin;
    private readonly ManualResetEventSlim _transferReadyEvent = new(false);
    private PinValue _expectedTfrRdyPinValue;
    private volatile int _lastPinValueFromCallback;
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

        // Initialize transfer ready pin
        _transferReadyPin = settings.Value.TransferReadyPin;
        int chipNumber = int.Parse(settings.Value.GpioChipDevice.Replace("/dev/gpiochip", ""));
        _gpioController = new GpioController(new LibGpiodDriver(chipNumber));
        _gpioController.OpenPin(_transferReadyPin, PinMode.Input);
        _lastPinValueFromCallback = (int)_gpioController.Read(_transferReadyPin);
        _gpioController.RegisterCallbackForPinValueChangedEvent(_transferReadyPin, PinEventTypes.Rising | PinEventTypes.Falling, (sender, args) =>
        {
            // Read pin value in callback to capture the state at the time of the interrupt
            _lastPinValueFromCallback = (int)_gpioController.Read(_transferReadyPin);
            _transferReadyEvent.Set();
        });

        // Parse SPI device path (e.g., /dev/spidev0.0 -> busId=0, chipSelectLine=0)
        string spiPath = settings.Value.SpiDevice;
        string[] parts = Path.GetFileName(spiPath).Replace("spidev", "").Split('.');
        int busId = int.Parse(parts[0]);
        int chipSelectLine = int.Parse(parts[1]);

        var spiSettings = new SpiConnectionSettings(busId, chipSelectLine)
        {
            ClockFrequency = settings.Value.SpiFrequency,
            Mode = (SpiMode)settings.Value.SpiTransferMode,
            DataBitLength = 8
        };
        _spiDevice = SpiDevice.Create(spiSettings);
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
        if (!_settings.CommunicationMethod.Equals("spi", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        builder.AppendLine($"Configured SPI speed: {_settings.SpiFrequency}Hz, TfrRdy pin glitches: {_numTfrPinGlitches}");
        builder.AppendLine($"Full transfers per second: {GetFullTransfersPerSecond():F2}, max time between full transfers: {GetMaxFullTransferDelay():0.0}ms, max pin wait times: {GetMaxPinWaitDuration(true):0.0}ms/{GetMaxPinWaitDuration(false):0.0}ms");
        builder.AppendLine($"Codes per second: {GetCodesPerSecond():F2}");
        builder.AppendLine($"Maximum length of RX/TX data transfers: {_maxRxSize}/{_maxTxSize}");
    }

    /// <summary>
    /// Static stopwatch to measure the times between full transfers with
    /// </summary>
    private readonly Stopwatch _fullTransferStopwatch = new();

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
    /// Read the result of a <see cref="Protocol.SbcRequests.Request.GetObjectModel"/> request
    /// </summary>
    /// <param name="json">JSON data</param>
    public void ReadObjectModel(out ReadOnlySpan<byte> json)
    {
        Protocol.Reader.ReadStringRequest(_packetData.Span, out json);
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
    /// Read the content of a <see cref="ExecuteMacroHeader"/> packet
    /// </summary>
    /// <param name="channel">Channel requesting a macro file</param>
    /// <param name="isSystemMacro">Indicates if this code is not bound to a code being executed (e.g. when a trigger macro is requested)</param>
    /// <param name="filename">Filename of the requested macro</param>
    public void ReadMacroRequest(out CodeChannel channel, out bool isSystemMacro, out string filename)
    {
        Protocol.Reader.ReadMacroRequest(_packetData.Span, out channel, out isSystemMacro, out filename);
    }

    /// <summary>
    /// Read the content of an <see cref="AbortFileHeader"/> packet
    /// </summary>
    /// <param name="channel">Code channel where all files are supposed to be aborted</param>
    /// <param name="abortAll">Whether all files are supposed to be aborted</param>
    public void ReadAbortFile(out CodeChannel channel, out bool abortAll)
    {
        Protocol.Reader.ReadAbortFile(_packetData.Span, out channel, out abortAll);
    }

    /// <summary>
    /// Read the content of a <see cref="PrintPausedHeader"/> packet
    /// </summary>
    /// <param name="filePosition">Position where the print has been paused</param>
    /// <param name="filePosition2">Position where the second open file has been paused (if applicable)</param>
    /// <param name="reason">Reason why the print has been paused</param>
    public void ReadPrintPaused(out uint filePosition, out uint filePosition2, out PrintPausedReason reason)
    {
        if (ProtocolVersion >= 7)
        {
            Protocol.Reader.ReadPrintPaused(_packetData.Span, out filePosition, out filePosition2, out reason);
        }
        else
        {
            Protocol.Reader.ReadLegacyPrintPaused(_packetData.Span, out filePosition, out reason);
            filePosition2 = Consts.NoFilePosition;
        }
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
    /// Read a chunk of a <see cref="Request.FileChunk"/> packet
    /// </summary>
    /// <param name="filename">Filename</param>
    /// <param name="offset">File offset</param>
    /// <param name="maxLength">Maximum chunk size</param>
    public void ReadFileChunkRequest(out string filename, out uint offset, out int maxLength)
    {
        Protocol.Reader.ReadFileChunkRequest(_packetData.Span, out filename, out offset, out maxLength);
    }

    /// <summary>
    /// Read the result of an expression evaluation request
    /// </summary>
    /// <param name="channel">Channel where the evaluation was performed</param>
    /// <param name="expression">Evaluated expression</param>
    /// <param name="result">Result</param>
    public void ReadEvaluationResult(out CodeChannel? channel, out string expression, out object? result)
    {
        Protocol.Reader.ReadEvaluationResult(_packetData.Span, out CodeChannel actualChannel, out expression, out result);
        channel = (ProtocolVersion >= 7) ? actualChannel : null;
    }

    /// <summary>
    /// Read a code request
    /// </summary>
    /// <param name="channel">Channel to execute this code on</param>
    /// <param name="code">Code to execute</param>
    public void ReadDoCode(out CodeChannel channel, out string code)
    {
        Protocol.Reader.ReadDoCode(_packetData.Span, out channel, out code);
    }

    /// <summary>
    /// Read a request to check if a file exists
    /// </summary>
    /// <param name="filename">Name of the file</param>
    public void ReadCheckFileExists(out string filename)
    {
        Protocol.Reader.ReadStringRequest(_packetData.Span, out filename);
    }

    /// <summary>
    /// Read a request to delete a file or directory
    /// </summary>
    /// <param name="filename">Name of the file</param>
    public void ReadDeleteFileOrDirectory(out string filename)
    {
        Protocol.Reader.ReadStringRequest(_packetData.Span, out filename);
    }

    /// <summary>
    /// Read an open file request
    /// </summary>
    /// <param name="filename">Filename to open</param>
    /// <param name="forWriting">Whether the file is supposed to be written to</param>
    /// <param name="append">Whether data is supposed to be appended in write mode</param>
    /// <param name="preAllocSize">How many bytes to allocate if the file is created or overwritten</param>
    public void ReadOpenFile(out string filename, out bool forWriting, out bool append, out long preAllocSize)
    {
        Protocol.Reader.ReadOpenFile(_packetData.Span, out filename, out forWriting, out append, out preAllocSize);
    }

    /// <summary>
    /// Read a request to seek in a file
    /// </summary>
    /// <param name="handle">File handle</param>
    /// <param name="offset">New file position</param>
    public void ReadSeekFile(out uint handle, out long offset)
    {
        Protocol.Reader.ReadSeekFile(_packetData.Span, out handle, out offset);
    }

    /// <summary>
    /// Read a request to truncate a file
    /// </summary>
    /// <param name="handle">File handle</param>
    public void ReadTruncateFile(out uint handle)
    {
        Protocol.Reader.ReadFileHandle(_packetData.Span, out handle);
    }

    /// <summary>
    /// Read a request to read data from a file
    /// </summary>
    /// <param name="handle">File handle</param>
    /// <param name="maxLength">Maximum data length</param>
    public void ReadFileRequest(out uint handle, out int maxLength)
    {
        Protocol.Reader.ReadFileRequest(_packetData.Span, out handle, out maxLength);
    }

    /// <summary>
    /// Read a request to write data to a file
    /// </summary>
    /// <param name="handle">File handle</param>
    /// <param name="data">Data to write</param>
    public void ReadWriteRequest(out uint handle, out ReadOnlySpan<byte> data)
    {
        int bytesRead = Protocol.Reader.ReadFileHandle(_packetData.Span, out handle);
        data = _packetData[bytesRead..].Span;
    }

    /// <summary>
    /// Read a request to close a file
    /// </summary>
    /// <param name="handle">File handle</param>
    public void ReadCloseFile(out uint handle)
    {
        Protocol.Reader.ReadFileHandle(_packetData.Span, out handle);
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
                if (sbcRequest != Protocol.SbcRequests.Request.LockAllMovementSystemsAndWaitForStandstill &&
                    !_packetsBeingResent.Contains(sbcRequest))
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
    /// Request a code to be executed
    /// </summary>
    /// <param name="code">Code to send</param>
    /// <returns>True if the packet could be written</returns>
    public bool WriteCode(Code code)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.Code))
        {
            return false;
        }

        // Attempt to serialize the code first
        Span<byte> span = stackalloc byte[_settings.MaxCodeBufferSize];
        int codeLength;
        try
        {
            codeLength = Protocol.Writer.WriteCode(span, code, ProtocolVersion);
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException("Failed to serialize code (too long?)", e);
        }

        // See if the code fits into the buffer
        if (!CanWritePacket(codeLength))
        {
            return false;
        }
        _numMeasuredCodes++;

        // Write it
        WritePacket(Protocol.SbcRequests.Request.Code, codeLength);
        span[..codeLength].CopyTo(GetWriteBuffer(codeLength));
        return true;
    }

    /// <summary>
    /// Request the key of a object module of a specific module
    /// </summary>
    /// <param name="key">Object model key to query</param>
    /// <param name="flags">Object model flags to query</param>
    /// <returns>True if the packet could be written</returns>
    public bool WriteGetObjectModel(string key, string flags)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.GetObjectModel))
        {
            return false;
        }

        // Serialize the request first to see how much space it requires
        Span<byte> span = stackalloc byte[_bufferSize - Marshal.SizeOf<PacketHeader>()];
        int dataLength = Protocol.Writer.WriteGetObjectModel(span, key, flags);

        // See if the request fits into the buffer
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        // Write it
        WritePacket(Protocol.SbcRequests.Request.GetObjectModel, dataLength);
        span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
        return true;
    }

#if false
    /// <summary>
    /// Set a specific value in the object model of RepRapFirmware
    /// </summary>
    /// <param name="field">Path to the field</param>
    /// <param name="value">New value</param>
    /// <returns>True if the packet could be written</returns>
    public bool WriteSetObjectModel(string field, object value)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.SetObjectModel))
        {
            return false;
        }

        // Serialize the request first to see how much space it requires
        Span<byte> span = stackalloc byte[bufferSize - Marshal.SizeOf<PacketHeader>()];
        int dataLength = Serialization.Writer.WriteSetObjectModel(span, field, value);

        // See if the request fits into the buffer
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        // Write it
        WritePacket(Communication.SbcRequests.Request.SetObjectModel, dataLength);
        span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
        return true;
    }
#endif

    /// <summary>
    /// Notify the firmware that a file print has started
    /// </summary>
    /// <param name="info">Information about the file being printed</param>
    /// <returns>True if the packet could be written</returns>
    public bool WritePrintFileInfo(GCodeFileInfo info)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.SetPrintFileInfo))
        {
            return false;
        }

        // Serialize the request first to see how much space it requires
        Span<byte> span = stackalloc byte[_bufferSize - Marshal.SizeOf<PacketHeader>()];
        int dataLength = Protocol.Writer.WritePrintFileInfo(span, info);

        // See if the request fits into the buffer
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        // Write it
        WritePacket(Protocol.SbcRequests.Request.SetPrintFileInfo, dataLength);
        span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
        return true;
    }

    /// <summary>
    /// Notify that a file print has been stopped
    /// </summary>
    /// <param name="reason">Reason why the print has been stopped</param>
    /// <returns>True if the packet could be written</returns>
    public bool WritePrintStopped(PrintStoppedReason reason)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.PrintStopped))
        {
            return false;
        }

        int dataLength = Marshal.SizeOf<Protocol.SbcRequests.PrintStoppedHeader>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.PrintStopped, dataLength);
        Protocol.Writer.WritePrintStopped(GetWriteBuffer(dataLength), reason);
        return true;
    }

    /// <summary>
    /// Notify the firmware about a completed macro file.
    /// This function is only used for macro files that the firmware requested
    /// </summary>
    /// <param name="channel">Code channel of the finished macro</param>
    /// <param name="error">Whether an error occurred</param>
    /// <returns>True if the packet could be written</returns>
    public bool WriteMacroCompleted(CodeChannel channel, bool error)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.MacroCompleted))
        {
            return false;
        }

        int dataLength = Marshal.SizeOf<Protocol.SbcRequests.MacroCompleteHeader>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.MacroCompleted, dataLength);
        Protocol.Writer.WriteMacroCompleted(GetWriteBuffer(dataLength), channel, error);
        return true;
    }

    /// <summary>
    /// Request the movement systems to be locked and wait for standstill
    /// </summary>
    /// <param name="channel">Code channel that requires the lock</param>
    /// <returns>True if the packet could be written</returns>
    public bool WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel channel)
    {
        // Resends of this request are expected as it may take a moment before the lock is acquired

        int dataLength = Marshal.SizeOf<CodeChannelHeader>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.LockAllMovementSystemsAndWaitForStandstill, dataLength);
        Protocol.Writer.WriteCodeChannel(GetWriteBuffer(dataLength), channel);
        return true;
    }

    /// <summary>
    /// Release all acquired locks again
    /// </summary>
    /// <param name="channel">Code channel that releases the locks</param>
    /// <returns>True if the packet could be written</returns>
    public bool WriteUnlock(CodeChannel channel)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.Unlock))
        {
            return false;
        }

        int dataLength = Marshal.SizeOf<CodeChannelHeader>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.Unlock, dataLength);
        Protocol.Writer.WriteCodeChannel(GetWriteBuffer(dataLength), channel);
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
    /// Write another chunk of the file being requested
    /// </summary>
    /// <param name="data">File chunk data</param>
    /// <param name="fileLength">Total length of the file in bytes</param>
    /// <returns>Whether the firmware has been written successfully</returns>
    public bool WriteFileChunk(Span<byte> data, long fileLength)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.FileChunk))
        {
            return false;
        }

        // Serialize the request first to see how much space it requires
        Span<byte> span = stackalloc byte[_bufferSize - Marshal.SizeOf<PacketHeader>()];
        int dataLength = Protocol.Writer.WriteFileChunk(span, data, fileLength);

        // See if the request fits into the buffer
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        // Write it
        WritePacket(Protocol.SbcRequests.Request.FileChunk, dataLength);
        span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
        return true;
    }

    /// <summary>
    /// Write a request for an expression evaluation
    /// </summary>
    /// <param name="channel">Where to evaluate the expression</param>
    /// <param name="expression">Expression to evaluate</param>
    /// <returns>Whether the evaluation request has been written successfully</returns>
    public bool WriteEvaluateExpression(CodeChannel channel, string expression)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.EvaluateExpression))
        {
            return false;
        }

        // Serialize the request first to see how much space it requires
        Span<byte> span = stackalloc byte[_bufferSize - Marshal.SizeOf<PacketHeader>()];
        int dataLength = Protocol.Writer.WriteEvaluateExpression(span, channel, expression);

        // See if the request fits into the buffer
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        // Write it
        WritePacket(Protocol.SbcRequests.Request.EvaluateExpression, dataLength);
        span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
        return true;
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
    /// Notify RepRapFirmware that a macro file could be started
    /// </summary>
    /// <param name="channel">Code channel that requires the lock</param>
    /// <returns>True if the packet could be written</returns>
    public bool WriteMacroStarted(CodeChannel channel)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.MacroStarted))
        {
            return false;
        }

        int dataLength = Marshal.SizeOf<CodeChannelHeader>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.MacroStarted, dataLength);
        Protocol.Writer.WriteCodeChannel(GetWriteBuffer(dataLength), channel);
        return true;
    }

    /// <summary>
    /// Called when a code channel is supposed to be invalidated (e.g. via abort keyword)
    /// </summary>
    /// <param name="channel">Code channel that requires the lock</param>
    /// <returns>True if the packet could be written</returns>
    public bool WriteInvalidateChannel(CodeChannel channel)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.InvalidateChannel))
        {
            return false;
        }

        int dataLength = Marshal.SizeOf<CodeChannelHeader>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.InvalidateChannel, dataLength);
        Protocol.Writer.WriteCodeChannel(GetWriteBuffer(dataLength), channel);
        return true;
    }

    /// <summary>
    /// Set a global or local variable
    /// </summary>
    /// <param name="channel">G-code channel</param>
    /// <param name="createVariable">Whether the variable should be created or updated</param>
    /// <param name="varName">Name of the variable including global or var prefix</param>
    /// <param name="expression">New value of the variable</param>
    /// <returns>True if the packet could be written</returns>
    public bool WriteSetVariable(CodeChannel channel, bool createVariable, string varName, string expression)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.SetVariable))
        {
            return false;
        }

        // Serialize the request first to see how much space it requires
        Span<byte> span = stackalloc byte[_bufferSize - Marshal.SizeOf<PacketHeader>()];
        int dataLength = Protocol.Writer.WriteSetVariable(span, channel, createVariable, varName, expression);

        // See if the request fits into the buffer
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        // Write it
        WritePacket(Protocol.SbcRequests.Request.SetVariable, dataLength);
        span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
        return true;
    }

    /// <summary>
    /// Delete a local variable at the end of the current code block
    /// </summary>
    /// <param name="channel">G-code channel</param>
    /// <param name="varName">Name of the variable excluding var prefix</param>
    /// <returns>True if the packet could be written</returns>
    public bool WriteDeleteLocalVariable(CodeChannel channel, string varName)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.DeleteLocalVariable))
        {
            return false;
        }

        // Serialize the request first to see how much space it requires
        Span<byte> span = stackalloc byte[_bufferSize - Marshal.SizeOf<PacketHeader>()];
        int dataLength = Protocol.Writer.WriteDeleteLocalVariable(span, channel, varName);

        // See if the request fits into the buffer
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        // Write it
        WritePacket(Protocol.SbcRequests.Request.DeleteLocalVariable, dataLength);
        span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
        return true;
    }

    /// <summary>
    /// Send back whether a file exists or not
    /// </summary>
    /// <param name="exists">Whether the file exists</param>
    /// <returns>If the packet could be written</returns>
    public bool WriteCheckFileExistsResult(bool exists)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.CheckFileExistsResult))
        {
            return false;
        }

        int dataLength = Marshal.SizeOf<Protocol.SbcRequests.BooleanHeader>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.CheckFileExistsResult, dataLength);
        Protocol.Writer.WriteBoolean(GetWriteBuffer(dataLength), exists);
        return true;
    }

    /// <summary>
    /// Send back whether a file or directory could be deleted
    /// </summary>
    /// <param name="success">Whether the file operation was successful</param>
    /// <returns>If the packet could be written</returns>
    public bool WriteFileDeleteResult(bool success)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.FileDeleteResult))
        {
            return false;
        }

        int dataLength = Marshal.SizeOf<Protocol.SbcRequests.BooleanHeader>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.FileDeleteResult, dataLength);
        Protocol.Writer.WriteBoolean(GetWriteBuffer(dataLength), success);
        return true;
    }

    /// <summary>
    /// Write the new file handle and file length of the file that has just been opened
    /// </summary>
    /// <param name="fileHandle">New file handle or noFileHandle if the file could not be opened</param>
    /// <param name="length">Length of the file</param>
    /// <returns>If the packet could be written</returns>
    public bool WriteOpenFileResult(uint fileHandle, long length)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.OpenFileResult))
        {
            return false;
        }

        int dataLength = Marshal.SizeOf<Protocol.SbcRequests.OpenFileResult>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.OpenFileResult, dataLength);
        Protocol.Writer.WriteOpenFileResult(GetWriteBuffer(dataLength), fileHandle, length);
        return true;
    }

    /// <summary>
    /// Write requested read data from a file
    /// </summary>
    /// <param name="data">File data</param>
    /// <param name="bytesRead">Number of bytes read or negative on error</param>
    /// <returns>If the packet could be written</returns>
    public bool WriteFileReadResult(Span<byte> data, int bytesRead)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.FileReadResult))
        {
            return false;
        }

        int dataLength = Marshal.SizeOf<Protocol.SbcRequests.FileDataHeader>() + data.Length;
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.FileReadResult, dataLength);
        Protocol.Writer.WriteFileReadResult(GetWriteBuffer(dataLength), data, bytesRead);
        return true;
    }

    /// <summary>
    /// Tell RRF if the last file block could be written
    /// </summary>
    /// <param name="success">If the file data could be written</param>
    /// <returns>If the packet could be written</returns>
    public bool WriteFileWriteResult(bool success)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.FileWriteResult))
        {
            return false;
        }

        int dataLength = Marshal.SizeOf<Protocol.SbcRequests.BooleanHeader>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.FileWriteResult, dataLength);
        Protocol.Writer.WriteBoolean(GetWriteBuffer(dataLength), success);
        return true;
    }

    /// <summary>
    /// Tell RRF if the seek operation was successful
    /// </summary>
    /// <param name="success">If the seek operation succeeded</param>
    /// <returns>If the packet could be written</returns>
    public bool WriteFileSeekResult(bool success)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.FileSeekResult))
        {
            return false;
        }

        int dataLength = Marshal.SizeOf<Protocol.SbcRequests.BooleanHeader>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.FileSeekResult, dataLength);
        Protocol.Writer.WriteBoolean(GetWriteBuffer(dataLength), success);
        return true;
    }

    /// <summary>
    /// Tell RRF if the seek operation was successful
    /// </summary>
    /// <param name="success">If the seek operation succeeded</param>
    /// <returns>If the packet could be written</returns>
    public bool WriteFileTruncateResult(bool success)
    {
        // Don't send a new request if another one is still pending
        if (_packetsBeingResent.Contains(Protocol.SbcRequests.Request.FileTruncateResult))
        {
            return false;
        }

        int dataLength = Marshal.SizeOf<Protocol.SbcRequests.BooleanHeader>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.FileTruncateResult, dataLength);
        Protocol.Writer.WriteBoolean(GetWriteBuffer(dataLength), success);
        return true;
    }

    /// <summary>
    /// Write the last code result for a specific code channel
    /// </summary>
    /// <param name="channel">Code channel</param>
    /// <param name="result">Last code result</param>
    /// <returns>If the packet could be written</returns>
    public bool WriteSetLastCodeResult(CodeChannel channel, CodeResult result)
    {
        if (ProtocolVersion < 7)
        {
            // not supported
            return true;
        }

        int dataLength = Marshal.SizeOf<Protocol.SbcRequests.SetLastCodeResultHeader>();
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.SetLastCodeResult, dataLength);
        Protocol.Writer.WriteSetLastCodeResult(GetWriteBuffer(dataLength), channel, result);
        return true;
    }

    /// <summary>
    /// Notify RRF that an object model key has changed
    /// </summary>
    /// <param name="key">Key that has changed</param>
    /// <returns>If the packet could be written</returns>
    public bool WriteObjectModelKeyChanged(string key)
    {
        if (ProtocolVersion < 7)
        {
            // not supported
            return true;
        }

        int dataLength = Marshal.SizeOf<StringHeader>() + Encoding.UTF8.GetByteCount(key);
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        WritePacket(Protocol.SbcRequests.Request.ObjectModelKeyChanged, dataLength);
        Protocol.Writer.WriteStringRequest(GetWriteBuffer(dataLength), key);
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
    /// Wait for the Duet to flag when it is ready to transfer data
    /// </summary>
    /// <param name="inTransfer">Whether a full transfer is being performed</param>
    /// <param name="cancellationToken">Cancellation token to cancel the wait</param>
    private void WaitForTransfer(bool inTransfer = true, CancellationToken cancellationToken = default)
    {
        if (_waitingForFirstTransfer)
        {
            // When a connection is established for the first time, the TfrRdy pin must be high
            _expectedTfrRdyPinValue = PinValue.High;
        }

        // Flush pending events by consuming them until the event stays reset
        while (_transferReadyEvent.Wait(0))
        {
            _transferReadyEvent.Reset();
        }
        
        // Check if the pin is already at the expected value
        PinValue currentValue = _gpioController.Read(_transferReadyPin);
        if (currentValue != _expectedTfrRdyPinValue)
        {
            // Determine how long to wait for the pin level transition
            int timeout;
            if (_waitingForFirstTransfer)
            {
                timeout = _updating ? Consts.IapTimeout : _settings.SbcConnectTimeout;
                _expectedTfrRdyPinValue = PinValue.High;
            }
            else
            {
                timeout = _updating ? Consts.IapTimeout : (inTransfer ? _settings.SbcTransferTimeout : _settings.SbcConnectionTimeout);
            }

            // Wait for the expected pin level, ignoring glitches
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
                        
                        // Use the pin value captured in the callback
                        currentValue = (PinValue)_lastPinValueFromCallback;
                        
                        // Check if this is the transition we're waiting for
                        if (currentValue == _expectedTfrRdyPinValue)
                        {
                            // Verify by reading again to ensure it's stable
                            PinValue verifyValue = _gpioController.Read(_transferReadyPin);
                            if (verifyValue == _expectedTfrRdyPinValue)
                            {
                                break;
                            }
                            // Pin changed again between callback and now, count as glitch
                            _numTfrPinGlitches++;
                        }
                        else
                        {
                            // This was a transition in the wrong direction, ignore it
                            // Don't count as glitch since this is expected with both edges registered
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

        // Transition complete
        _expectedTfrRdyPinValue = (_expectedTfrRdyPinValue == PinValue.High) ? PinValue.Low : PinValue.High;
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
