using DuetAPI;
using DuetAPI.ObjectModel;
using Code = DuetControlServer.Commands.Code;
using DuetControlServer.Utility;
using System;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Link.Protocol.FirmwareRequests;
using DuetSharedLibrary;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;

namespace DuetControlServer.Link.Adapter;

/// <summary>
/// Class to handle the USB link to the firmware over ttyACM
/// </summary>
[DiagnosticsPriority(-4)]
public class USB : IDiagnostics, ILinkAdapter
{
    // General variables
    private readonly EventLogger _eventLogger;
    private readonly Model.ObjectModel _model;
    private readonly ILogger<USB> _logger;
    private readonly Settings _settings;

    // General transfer variables
    private readonly SerialPort _serialPort;
    private SerialPort? _iapSerialPort;
    private bool _connected, _hadTimeout, _resetting, _updating, _needsReconnect;
    private int _consecutiveFailures;

    private DateTime _lastTransferMeasureTime = DateTime.Now, _lastCodesMeasureTime = DateTime.Now;
    private volatile int _numMeasuredTransfers, _numMeasuredCodes, _maxRxSize, _maxTxSize;
    private TimeSpan _maxFullTransferDelay = TimeSpan.Zero;

    // Transfer headers
    private readonly Memory<byte> _rxHeaderBuffer = new byte[Marshal.SizeOf<UsbTransferHeader>()];
    private readonly Memory<byte> _txHeaderBuffer = new byte[Marshal.SizeOf<UsbTransferHeader>()];
    private UsbTransferHeader _rxHeader;
    private UsbTransferHeader _txHeader;
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

    /// <summary>
    /// Currently-used protocol version
    /// </summary>
    public int ProtocolVersion { get; private set; }

    /// <summary>
    /// Constructor of this class
    /// </summary>
    /// <param name="eventLogger">Event logger</param>
    /// <param name="model">Object model</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="settings">Settings</param>
    /// <exception cref="OperationCanceledException">Failed to connect to board</exception>
    public USB(EventLogger eventLogger, Model.ObjectModel model, ILogger<USB> logger, IOptions<Settings> settings)
    {
        // Initialize variables
        _eventLogger = eventLogger;
        _model = model;
        _logger = logger;
        _settings = settings.Value;
        _bufferSize = settings.Value.SbcBufferSize;
        _rxBuffer = new byte[_bufferSize];

        // Initialize TX buffers
        for (int i = 0; i < NumTxBuffers; i++)
        {
            _txBuffers.AddLast(new byte[_bufferSize]);
        }
        _txBuffer = _txBuffers.First!;

        // Initialize serial port
        _serialPort = new SerialPort(settings.Value.UsbDevice, 115200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = settings.Value.UsbReadTimeout,
            WriteTimeout = settings.Value.UsbWriteTimeout,
            DtrEnable = true
        };
    }

    /// <summary>
    /// Attempt to connect to the firmware by sending M576.1 and parsing the init response
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    public void Connect(CancellationToken cancellationToken = default)
    {
        // Open the serial port or discard any pending data
        if (!_serialPort.IsOpen)
        {
            // Check explicitly so we get a clear FileNotFoundException instead of a misleading UnauthorizedAccessException from SerialPort.Open
            if (!File.Exists(_serialPort.PortName))
            {
                throw new FileNotFoundException("Serial device not found", _serialPort.PortName);
            }
            _serialPort.Open();
        }
        else
        {
            _serialPort.DiscardInBuffer();
            _serialPort.DiscardOutBuffer();
        }

        // Flush any stale partial command in RRF's input buffer, then drain responses
        _serialPort.Write("\n");
        Thread.Sleep(100);

        // Send M576.1 to switch RRF to USB SBC mode
        _logger.LogDebug("USB: Sending M576.1 P{Version}", Consts.ProtocolVersion);
        _serialPort.Write($"M576.1 P{Consts.ProtocolVersion}\n");

        // Read lines until we find the init response (skip boot messages and empty lines)
        while (true)
        {
            string? statusLine = _serialPort.ReadLine();
            if (string.IsNullOrWhiteSpace(statusLine))
            {
                continue;
            }
            if (statusLine.StartsWith("Switching to binary SBC mode", StringComparison.Ordinal))
            {
                break;
            }
            _logger.LogDebug("USB: Skipping line: {Line}", statusLine);
        }

        // Read init response line 2: JSON with protocol details and buffer sizes
        string? jsonLine = _serialPort.ReadLine();
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            throw new IOException("Missing protocol details in M576.1 response");
        }

        // Parse JSON: {"protocol":7,"rxBuffer":8192,"txBuffer":8192}
        _logger.LogDebug("USB: Init JSON: {Json}", jsonLine);
        using JsonDocument doc = JsonDocument.Parse(jsonLine);
        JsonElement root = doc.RootElement;

        int protocol = root.GetProperty("protocol").GetInt32();
        if (protocol != Consts.ProtocolVersion)
        {
            throw new IOException($"Incompatible protocol version {protocol} (expected {Consts.ProtocolVersion})");
        }
        ProtocolVersion = protocol;

        int rxBuffer = root.GetProperty("rxBuffer").GetInt32();
        int txBuffer = root.GetProperty("txBuffer").GetInt32();
        if (rxBuffer < _bufferSize || txBuffer < _bufferSize)
        {
            _logger.LogWarning("Firmware buffer sizes (rx={RxBuffer}, tx={TxBuffer}) are smaller than configured buffer size ({BufferSize})", rxBuffer, txBuffer, _bufferSize);
        }

        // Send handover packet to complete BeginDirectMode on the RRF side
        // These 8 bytes are consumed by the CDC OUT completion hook and never
        // reach DoTransferUsb - they just unblock BeginDirectMode
        _serialPort.Write(new byte[8], 0, 8);
        _serialPort.BaseStream.Flush();

        // RRF is now in binary mode, start transfers
        PerformFullTransfer(true, cancellationToken);
        _logger.LogInformation("Connected to controller over USB");
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
    /// Print diagnostics to the given string builder
    /// </summary>
    /// <param name="builder">Target to write to</param>
    public void PrintDiagnostics(StringBuilder builder)
    {
        if (_settings.CommunicationMethod != CommunicationMethod.USB)
        {
            return;
        }

        builder.AppendLine($"USB device: {_settings.UsbDevice}");
        builder.AppendLine($"Full transfers per second: {GetFullTransfersPerSecond():F2}, max time between full transfers: {GetMaxFullTransferDelay():0.0}ms");
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
        // Attempt reconnection if needed (but not during initial connection)
        if (_needsReconnect && !connecting)
        {
            // Wait out the firmware's USB disconnect dwell before re-probing, otherwise re-opening the
            // port can keep the old ttyACM minor referenced and shift the indices on re-enumeration
            Thread.Sleep(Consts.UsbReconnectDelay);
            try
            {
                _logger.LogDebug("USB: Attempting reconnection...");
                if (_serialPort.IsOpen)
                {
                    _serialPort.Close();
                }
                _txPointer = 0;
                _packetId = 0;
                _needsReconnect = false;
                Connect(cancellationToken);
                // Connect succeeded and performed the first transfer already
                _consecutiveFailures = 0;
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _needsReconnect = true;
                _connected = false;
                _consecutiveFailures++;
                if (_consecutiveFailures > _settings.MaxSbcRetries)
                {
                    throw new OperationCanceledException("Maximum number of USB transfer retries exceeded");
                }
                _logger.LogDebug("USB: Reconnection failed ({Error}), will retry ({Attempt}/{Max})", ex.Message, _consecutiveFailures, _settings.MaxSbcRetries);
                Thread.Sleep(500);
                return;
            }
        }

        // Reset RX transfer header
        _rxHeader.NumPackets = 0;
        _rxHeader.DataLength = 0;

        // Set up TX transfer header
        _txHeader.NumPackets = _packetId;
        _txHeader.DataLength = (ushort)_txPointer;

        try
        {
            // Keep track of the maximum times between regular full transfers
            if (!connecting && _connected && !_hadTimeout && !_updating && !_resetting)
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

            // Exchange transfer headers
            ExchangeHeader();

            // Validate data length
            if (_rxHeader.DataLength > _bufferSize)
            {
                throw new IOException($"Received data too long ({_rxHeader.DataLength} bytes, max {_bufferSize})");
            }

            // Exchange data if there is anything to transfer
            if (_rxHeader.DataLength != 0 || _txPointer != 0)
            {
                ExchangeData();
            }

            // Deal with timeouts
            if (_hadTimeout)
            {
                _eventLogger.LogOutput(MessageType.Success, "Connection to Duet established");
                _hadTimeout = _resetting = false;
            }
            _connected = true;

            // Transfer OK
            _consecutiveFailures = 0;
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
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException or InvalidOperationException)
        {
            if (connecting || cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogDebug(e, "Lost USB connection to Duet");

            if (!_hadTimeout && _connected)
            {
                _hadTimeout = true;
                _model.ConnectionLost();
                _eventLogger.LogOutput(MessageType.Warning, $"Lost USB connection to Duet ({e.Message})");
            }
            _connected = false;
            _needsReconnect = true;

            // Close the port to ensure clean state for reconnection
            try
            {
                _serialPort.Close();
            }
            catch
            {
                // Best effort
            }

            _consecutiveFailures++;
            if (_consecutiveFailures > _settings.MaxSbcRetries)
            {
                throw new OperationCanceledException("Maximum number of USB transfer retries exceeded");
            }
        }
    }

    /// <summary>
    /// Check if the controller has been reset or disconnected.
    /// Unlike SPI (which uses sequence numbers), USB detects this via communication failure.
    /// </summary>
    /// <returns>Whether the controller has been reset</returns>
    public bool HadReset() => _needsReconnect;

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
        Protocol.Reader.ReadPrintPaused(_packetData.Span, out filePosition, out filePosition2, out reason);
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
        channel = actualChannel;
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
        _logger.LogError("Received malformed packet: {UsbDump}", dump);
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
        // USB transfers are sequential: the firmware sees our data before composing its response, so a
        // resend request always refers to the exchange that was just sent. That buffer is the previous
        // one because the transfer routine has already rotated to the next buffer at this point (unlike
        // SPI, where the full-duplex pipeline makes resend requests refer to the transfer before that)
        Span<byte> buffer = (_txBuffer.Previous ?? _txBuffers.Last!).Value.Span;

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

    /// <summary>
    /// Notify the firmware that a file print has started
    /// </summary>
    /// <param name="info">Information about the file being printed</param>
    /// <returns>True if the packet could be written</returns>
    public bool WritePrintFileInfo(GCodeFileInfo info)
    {
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
    /// <param name="firmwareLength">Length of the firmware binary in bytes; sent to IAP as part of the USB handshake</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    public void StartIap(uint firmwareLength, CancellationToken cancellationToken = default)
    {
        // Tell the firmware to boot the IAP program
        WritePacket(Protocol.SbcRequests.Request.StartIap);
        PerformFullTransfer(cancellationToken: cancellationToken);

        // The board will shut down TinyUSB and reboot into IAP, which re-initializes
        // USB with a bare-metal CDC driver. This causes a USB disconnect + reconnect
        _updating = true;

        _serialPort.Close();

        try
        {
            _logger.LogDebug("IAP: Scanning for IAP USB device (firmware length {Length} bytes)...", firmwareLength);
            _iapSerialPort = FindIapDevice(Consts.IapTimeout, firmwareLength, cancellationToken);
            if (_iapSerialPort == null)
            {
                throw new OperationCanceledException("IAP: Timed out waiting for IAP USB device to appear");
            }
            _logger.LogDebug("IAP: Connected to IAP device on {Port}", _iapSerialPort.PortName);
        }
        catch
        {
            // Recovery: try to reopen the main RRF port so reconnection can work
            try
            {
                _serialPort.Open();
                _logger.LogWarning("IAP: Recovered main port after IAP device scan failure");
            }
            catch
            {
                // If we can't reopen, flag for full reconnection
                _needsReconnect = true;
                _logger.LogWarning("IAP: Could not recover main port, will attempt full reconnection");
            }
            _updating = false;
            throw;
        }
    }

    /// <summary>
    /// Scan for the IAP serial port by trying the IAPR handshake on each available ttyACM port.
    /// Ports are retried each scan cycle since the IAP device may reappear on the same path.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds</param>
    /// <param name="firmwareLength">Length of the firmware binary, sent as part of the handshake</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The opened serial port, or null if not found</returns>
    private SerialPort? FindIapDevice(int timeoutMs, uint firmwareLength, CancellationToken cancellationToken)
    {
        Stopwatch sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(500);

            string[] ports = SerialPort.GetPortNames();
            if (ports.Length == 0)
            {
                continue;
            }

            foreach (string port in ports)
            {
                // Only try CDC ACM ports
                if (!Path.GetFileName(port).StartsWith("ttyACM"))
                {
                    continue;
                }

                // Abort if we've used up our time budget
                if (sw.ElapsedMilliseconds >= timeoutMs)
                {
                    break;
                }

                // Check the USB product string via sysfs -- only try ports that
                // identify as "IAP". This is what tells the IAP device apart from RRF
                // (whether RRF stayed put or re-enumerated on a different port), so the
                // handshake is never sent to a running RRF instance
                string? product = GetUsbProductString(port);
                if (product == null || !product.Equals("IAP", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("IAP: Skipping {Port} (product={Product})", port, product ?? "(none)");
                    continue;
                }

                _logger.LogDebug("IAP: Trying handshake on {Port} (product={Product})", port, product);
                if (TryIapHandshake(port, firmwareLength) is SerialPort sp)
                {
                    return sp;
                }
            }
        }
        _logger.LogWarning("IAP: Device not found after {Elapsed}ms", sw.ElapsedMilliseconds);
        return null;
    }

    /// <summary>
    /// Try to open a serial port and perform the IAPR handshake.
    /// Sends "IAPR" followed by the 4-byte firmware length (little-endian), then waits for the IAPR echo.
    /// Returns quickly (within ~500ms) if the device doesn't respond.
    /// </summary>
    /// <param name="portName">Serial port to try</param>
    /// <param name="firmwareLength">Firmware length to send as part of the handshake</param>
    /// <returns>The opened serial port if handshake succeeded, null otherwise</returns>
    private SerialPort? TryIapHandshake(string portName, uint firmwareLength)
    {
        SerialPort? sp = null;
        try
        {
            sp = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = true
            };
            sp.Open();
            sp.DiscardInBuffer();

            // Send "IAPR" + 4-byte firmware length as one 8-byte header, then expect IAPR echo
            byte[] header = new byte[8];
            header[0] = (byte)'I';
            header[1] = (byte)'A';
            header[2] = (byte)'P';
            header[3] = (byte)'R';
            header[4] = (byte)(firmwareLength & 0xFF);
            header[5] = (byte)((firmwareLength >> 8) & 0xFF);
            header[6] = (byte)((firmwareLength >> 16) & 0xFF);
            header[7] = (byte)((firmwareLength >> 24) & 0xFF);
            sp.Write(header, 0, header.Length);

            byte[] echo = new byte[4];
            int totalRead = 0;
            while (totalRead < 4)
            {
                try
                {
                    int n = sp.Read(echo, totalRead, 4 - totalRead);
                    if (n == 0) break;
                    totalRead += n;
                }
                catch (TimeoutException)
                {
                    break;
                }
            }

            if (totalRead == 4 && echo[0] == 'I' && echo[1] == 'A' && echo[2] == 'P' && echo[3] == 'R')
            {
                _logger.LogDebug("IAP: Handshake successful on {Port}", portName);
                return sp;
            }

            _logger.LogDebug("IAP: Handshake failed on {Port} (got {Bytes} bytes)", portName, totalRead);
            sp.Close();
            sp.Dispose();
            sp = null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("IAP: Could not open {Port}: {Error}", portName, ex.Message);
            if (sp != null)
            {
                try
                {
                    sp.Close();
                    sp.Dispose();
                }
                catch
                {
                    // Best effort
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Read the USB product string for a ttyACM device from sysfs
    /// </summary>
    /// <param name="portName">Serial port path (e.g., /dev/ttyACM0)</param>
    /// <returns>Product string, or null if not available</returns>
    private static string? GetUsbProductString(string portName)
    {
        // Resolve the sysfs path through symlinks using POSIX realpath(),
        // because .NET's Path.GetFullPath normalizes ".." lexically
        string productPath = $"/sys/class/tty/{Path.GetFileName(portName)}/device/../product";
        string? resolvedProductPath = Path.GetRealPath(productPath);
        return File.Exists(resolvedProductPath) ? File.ReadAllText(resolvedProductPath).Trim() : null;
    }

    /// <summary>
    /// Flash another segment of the firmware via the IAP binary.
    /// The IAP sends a 0x1A ready byte before each block.
    /// </summary>
    /// <param name="stream">Stream of the firmware binary</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether another segment could be sent</returns>
    public bool FlashFirmwareSegment(Stream stream)
    {
        if (_iapSerialPort == null)
        {
            throw new InvalidOperationException("IAP serial port not connected");
        }

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

        // Wait for the 0x1A ready byte from IAP
        WaitForIapReady();

        // Send the firmware block followed by a short packet to flush the device's
        // USB DMA buffer (the ASF UDI CDC DMA only commits on buffer-full or short packet)
        _iapSerialPort.Write(writeBuffer.ToArray(), 0, writeBuffer.Length);
        _iapSerialPort.BaseStream.Flush();
        return true;
    }

    /// <summary>
    /// Wait for the IAP ready byte (0x1A) from the IAP device
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    private void WaitForIapReady()
    {
        byte[] buf = new byte[1];
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < Consts.IapTimeout)
        {
            try
            {
                int n = _iapSerialPort!.Read(buf, 0, 1);
                if (n == 1 && buf[0] == 0x1A)
                {
                    return;
                }
            }
            catch (TimeoutException)
            {
                // Keep trying
            }
        }
        throw new TimeoutException("Timed out waiting for IAP ready signal");
    }

    /// <summary>
    /// Send the CRC16 checksum of the firmware binary to the IAP program and verify the written data.
    /// Uses timing-based end-of-transfer detection (matching SPI IAP protocol).
    /// </summary>
    /// <param name="firmwareLength">Length of the written firmware in bytes</param>
    /// <param name="crc16">CRC16 checksum of the firmware</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether the firmware has been written successfully</returns>
    public bool VerifyFirmwareChecksum(long firmwareLength, ushort crc16)
    {
        if (_iapSerialPort == null)
        {
            throw new InvalidOperationException("IAP serial port not connected");
        }

        // IAP detects end-of-transfer by a timing gap (TransferCompleteDelay = 400ms)
        // We wait FirmwareFinishedDelay (750ms) so IAP recognizes we're done sending blocks
        // By now IAP has sent two ready bytes (one for "next block", one for verify phase)
        // and is waiting for the FlashVerifyRequest. Just drain any stale data and send it
        Thread.Sleep(Consts.FirmwareFinishedDelay);
        _iapSerialPort.DiscardInBuffer();

        // Send the final firmware size plus CRC16 checksum to IAP
        Protocol.SbcRequests.FlashVerify verifyRequest = new()
        {
            firmwareLength = (uint)firmwareLength,
            crc16 = crc16
        };
        Span<byte> transferData = stackalloc byte[Marshal.SizeOf<Protocol.SbcRequests.FlashVerify>()];
        MemoryMarshal.Write(transferData, verifyRequest);
        _iapSerialPort.Write(transferData.ToArray(), 0, transferData.Length);

        // Read the 1-byte verification result from IAP (0x0C = success, 0xFF = failure)
        byte[] result = new byte[1];
        int totalRead = 0;
        Stopwatch sw = Stopwatch.StartNew();
        while (totalRead < 1 && sw.ElapsedMilliseconds < Consts.IapTimeout)
        {
            try
            {
                totalRead += _iapSerialPort.Read(result, totalRead, 1 - totalRead);
            }
            catch (TimeoutException)
            {
                // Keep trying
            }
        }

        if (totalRead < 1)
        {
            _logger.LogError("IAP: Timed out waiting for verification response");
            return false;
        }

        _logger.LogDebug("IAP: Verification response: 0x{Response:X2}", result[0]);
        return result[0] == 0x0C;
    }
    /// <summary>
    /// Wait for the IAP program to reset the controller.
    /// Close the IAP serial port and flag for reconnection to the main firmware.
    /// </summary>
    public void WaitForIapReset()
    {
        // Close the IAP serial port
        if (_iapSerialPort != null)
        {
            try
            {
                _iapSerialPort.Close();
            }
            catch
            {
                // Ignore close errors
            }
            _iapSerialPort.Dispose();
            _iapSerialPort = null;
        }

        // Wait for the board to reboot
        Thread.Sleep(Consts.IapRebootDelay);

        // Flag for reconnection - the main loop will re-establish the normal SBC connection
        _resetting = true;
        _updating = false;
        _needsReconnect = true;
    }

    /// <summary>
    /// Write another chunk of the file being requested
    /// </summary>
    /// <param name="data">File chunk data</param>
    /// <param name="fileLength">Total length of the file in bytes</param>
    /// <returns>Whether the firmware has been written successfully</returns>
    public bool WriteFileChunk(Span<byte> data, long fileLength)
    {
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
        // Serialize the request first to see how much space it requires
        Span<byte> span = stackalloc byte[_bufferSize - Marshal.SizeOf<PacketHeader>()];
        int dataLength = Protocol.Writer.WriteOpenFileResult(span, fileHandle, length);

        // See if the request fits into the buffer
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        // Write it
        WritePacket(Protocol.SbcRequests.Request.OpenFileResult, dataLength);
        span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
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
        // Serialize the request first to see how much space it requires
        Span<byte> span = stackalloc byte[_bufferSize - Marshal.SizeOf<PacketHeader>()];
        int dataLength = Protocol.Writer.WriteFileReadResult(span, data, bytesRead);

        // See if the request fits into the buffer
        if (!CanWritePacket(dataLength))
        {
            return false;
        }

        // Write it
        WritePacket(Protocol.SbcRequests.Request.FileReadResult, dataLength);
        span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
        return true;
    }

    /// <summary>
    /// Tell RRF if the last file block could be written
    /// </summary>
    /// <param name="success">If the file data could be written</param>
    /// <returns>If the packet could be written</returns>
    public bool WriteFileWriteResult(bool success)
    {
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
    /// Exchange the transfer header. No CRC, format code, or response exchange over USB.
    /// </summary>
    private void ExchangeHeader()
    {
        int headerSize = Marshal.SizeOf<UsbTransferHeader>();

        // Send TX header
        MemoryMarshal.Write(_txHeaderBuffer.Span, _txHeader);
        _serialPort.Write(_txHeaderBuffer.ToArray(), 0, headerSize);
        _serialPort.BaseStream.Flush();

        // Receive RX header
        ReadExactly(_rxHeaderBuffer.Span, "header");
        _rxHeader = MemoryMarshal.Read<UsbTransferHeader>(_rxHeaderBuffer.Span);
    }

    /// <summary>
    /// Read exactly the specified number of bytes from the serial port
    /// </summary>
    /// <param name="buffer">Buffer to read into</param>
    /// <param name="what">What is being read, used in the timeout message to distinguish a silent Duet from a truncated/desynced transfer</param>
    private void ReadExactly(Span<byte> buffer, string what)
    {
        int totalRead = 0;
        byte[] tempBuffer = new byte[buffer.Length];
        Stopwatch sw = Stopwatch.StartNew();
        int totalTimeout = _serialPort.ReadTimeout * 2;     // overall limit: 2x the per-read timeout

        while (totalRead < buffer.Length)
        {
            if (sw.ElapsedMilliseconds > totalTimeout)
            {
                throw new TimeoutException(totalRead == 0
                    ? $"Duet sent no {what} within {totalTimeout}ms (no response)"
                    : $"Duet sent only {totalRead} of {buffer.Length} {what} bytes in {sw.ElapsedMilliseconds}ms (transfer truncated, stream desynced)");
            }

            int bytesRead;
            try
            {
                bytesRead = _serialPort.Read(tempBuffer, totalRead, buffer.Length - totalRead);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(totalRead == 0
                    ? $"Duet sent no {what} within {_serialPort.ReadTimeout}ms (no response)"
                    : $"Duet sent only {totalRead} of {buffer.Length} {what} bytes before timing out (transfer truncated, stream desynced)");
            }
            totalRead += bytesRead;
        }

        tempBuffer.AsSpan().CopyTo(buffer);
    }

    /// <summary>
    /// Exchange the transfer body. No CRC or response exchange over USB.
    /// </summary>
    private void ExchangeData()
    {
        // Send TX data
        if (_txPointer > 0)
        {
            _serialPort.Write(_txBuffer.Value.ToArray(), 0, _txPointer);
            _serialPort.BaseStream.Flush();
        }

        // Receive RX data
        if (_rxHeader.DataLength > 0)
        {
            ReadExactly(_rxBuffer[.._rxHeader.DataLength].Span, "transfer data");
        }
    }
    #endregion
}
