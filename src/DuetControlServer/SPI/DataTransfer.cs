using DuetAPI;
using DuetAPI.ObjectModel;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Code = DuetControlServer.Commands.Code;
using LinuxApi;
using System.Collections.Generic;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.Shared;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using System.Diagnostics;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// Helper class for SPI data transfers
    /// </summary>
    public static class DataTransfer
    {
        /// <summary>
        /// Logger instance
        /// </summary>
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();

        // General transfer variables
        private static InputGpioPin _transferReadyPin = null!;
        private static bool _expectedTfrRdyPinValue;
        private static SpiDevice _spiDevice = null!;
        private static bool _waitingForFirstTransfer = true, _connected, _hadTimeout, _resetting, _updating;
        private static ushort _lastTransferNumber;

        private static DateTime _lastTransferMeasureTime = DateTime.Now, _lastCodesMeasureTime = DateTime.Now;
        private static volatile int _numMeasuredTransfers, _numMeasuredCodes, _maxRxSize, _maxTxSize, _numTfrPinGlitches;
        private static TimeSpan _maxFullTransferDelay = TimeSpan.Zero, _maxPinWaitDurationFull = TimeSpan.Zero, _maxPinWaitDuration = TimeSpan.Zero;

        // Transfer headers
        private static readonly Memory<byte> _rxHeaderBuffer = new byte[Marshal.SizeOf<TransferHeader>()];
        private static readonly Memory<byte> _txHeaderBuffer = new byte[Marshal.SizeOf<TransferHeader>()];
        private static TransferHeader _rxHeader;
        private static TransferHeader _txHeader;
        private static byte _packetId;

        // Transfer data. Keep three TX buffers so resend requests can be processed
        private static readonly int bufferSize = Settings.SpiBufferSize;
        private const int NumTxBuffers = 3;
        private static readonly Memory<byte> _rxBuffer = new byte[bufferSize];
        private static readonly LinkedList<Memory<byte>> _txBuffers = new();
        private static LinkedListNode<Memory<byte>> _txBuffer = null!;
        private static int _rxPointer, _txPointer;
        private static PacketHeader _lastPacket;
        private static ReadOnlyMemory<byte> _packetData;

        /// <summary>
        /// Currently-used protocol version
        /// </summary>
        public static int ProtocolVersion { get; private set; }

        /// <summary>
        /// Set up the SPI device and the controller for the transfer ready pin
        /// </summary>
        /// <exception cref="OperationCanceledException">Failed to connect to board</exception>
        public static void Init()
        {
            // Initialize TX header. This only needs to happen once
            Serialization.Writer.InitTransferHeader(ref _txHeader);

            // Initialize TX buffers
            for (int i = 0; i < NumTxBuffers; i++)
            {
                _txBuffers.AddLast(new byte[bufferSize]);
            }
            _txBuffer = _txBuffers.First!;

            // Initialize transfer ready pin and SPI device
            _transferReadyPin = new InputGpioPin(Settings.GpioChipDevice, Settings.TransferReadyPin, $"dcs-trp-{Settings.TransferReadyPin}");
            _spiDevice = new SpiDevice(Settings.SpiDevice, Settings.SpiFrequency, Settings.SpiTransferMode);

            // Check if large transfers can be performed
            try
            {
                int maxSpiBufferSize = int.Parse(File.ReadAllText("/sys/module/spidev/parameters/bufsiz"));
                if (maxSpiBufferSize < bufferSize)
                {
                    _logger.Warn("Kernel SPI buffer size is smaller than RepRapFirmware buffer size ({0} configured vs {1} required)", maxSpiBufferSize, Consts.BufferSize);
                }
            }
            catch (Exception e)
            {
                _logger.Warn(e, "Failed to retrieve Kernel SPI buffer size");
            }

            // Perform the first transfer
            PerformFullTransfer(true);
        }

        /// <summary>
        /// Get the number of full transfers per second
        /// </summary>
        /// <returns>Full transfers per second</returns>
        private static decimal GetFullTransfersPerSecond()
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
        private static decimal GetCodesPerSecond()
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
        public static double GetMaxFullTransferDelay()
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
        public static double GetMaxPinWaitDuration(bool fullTransferCounter)
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
        public static void Diagnostics(StringBuilder builder)
        {
            builder.AppendLine($"Configured SPI speed: {Settings.SpiFrequency}Hz, TfrRdy pin glitches: {_numTfrPinGlitches}");
            builder.AppendLine($"Full transfers per second: {GetFullTransfersPerSecond():F2}, max time between full transfers: {GetMaxFullTransferDelay():0.0}ms, max pin wait times: {GetMaxPinWaitDuration(true):0.0}ms/{GetMaxPinWaitDuration(false):0.0}ms");
            builder.AppendLine($"Codes per second: {GetCodesPerSecond():F2}");
            builder.AppendLine($"Maximum length of RX/TX data transfers: {_maxRxSize}/{_maxTxSize}");
        }

        /// <summary>
        /// Static stopwatch to measure the times between full transfers with
        /// </summary>
        private static readonly Stopwatch _fullTransferStopwatch = new();

        /// <summary>
        /// Perform a full data transfer synchronously
        /// </summary>
        /// <param name="connecting">Whether this an initial connection is being established</param>
        public static void PerformFullTransfer(bool connecting = false)
        {
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

            do
            {
                try
                {
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
                        continue;
                    }

                    // Exchange data if there is anything to transfer
                    if ((_rxHeader.DataLength != 0 || _txPointer != 0) && !ExchangeData())
                    {
                        continue;
                    }

                    // Verify the protocol version
                    ProtocolVersion = _rxHeader.ProtocolVersion;
                    if ((_hadTimeout || !_connected) && ProtocolVersion != Consts.ProtocolVersion)
                    {
                        Logger.LogOutput(MessageType.Warning, "Incompatible firmware, please upgrade as soon as possible");
                    }

                    // Deal with timeouts and the first transmission
                    if (_hadTimeout)
                    {
                        Logger.LogOutput(MessageType.Success, "Connection to Duet established");
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

                    // Deal with reset requests
                    if (_resetting && Settings.NoTerminateOnReset)
                    {
                        _connected = _resetting = false;
                        _waitingForFirstTransfer = true;
                        PerformFullTransfer();
                    }
                    break;
                }
                catch (OperationCanceledException e)
                {
                    if (connecting || Program.CancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    _logger.Debug(e, "Lost connection to Duet");
                    _txHeader.ProtocolVersion = Consts.ProtocolVersion;
                    _waitingForFirstTransfer = true;

                    if (!_hadTimeout && _connected)
                    {
                        _hadTimeout = true;
                        Updater.ConnectionLost();
                        Logger.LogOutput(MessageType.Warning, $"Lost connection to Duet ({e.Message})");
                    }
                    _connected = false;
                }
            }
            while (!Program.CancellationToken.IsCancellationRequested);
        }

        /// <summary>
        /// Check if the controller has been reset
        /// </summary>
        /// <returns>Whether the controller has been reset</returns>
        public static bool HadReset() => _connected && ((ushort)(_lastTransferNumber + 1) != _rxHeader.SequenceNumber);

        #region Read functions
        /// <summary>
        /// Returns the number of packets to read
        /// </summary>
        public static int PacketsToRead => _rxHeader.NumPackets;

        /// <summary>
        /// Read the next packet
        /// </summary>
        /// <returns>The next packet or null if none is available</returns>
        public static PacketHeader? ReadNextPacket()
        {
            if (_rxPointer >= _rxHeader.DataLength)
            {
                return null;
            }

            // Header
            _rxPointer += Serialization.Reader.ReadPacketHeader(_rxBuffer[_rxPointer..].Span, out _lastPacket);

            // Packet data
            _packetData = _rxBuffer.Slice(_rxPointer, _lastPacket.Length);
            int padding = 4 - (_lastPacket.Length % 4);
            _rxPointer += _lastPacket.Length + ((padding == 4) ? 0 : padding);

            return _lastPacket;
        }

        /// <summary>
        /// Read the legacy result of a <see cref="Communication.SbcRequests.Request.GetObjectModel"/> request
        /// </summary>
        /// <param name="json">JSON data</param>
        public static void ReadLegacyConfigResponse(out ReadOnlySpan<byte> json)
        {
            Serialization.Reader.ReadLegacyConfigResponse(_packetData.Span, out json);
        }

        /// <summary>
        /// Read the result of a <see cref="Communication.SbcRequests.Request.GetObjectModel"/> request
        /// </summary>
        /// <param name="json">JSON data</param>
        public static void ReadObjectModel(out ReadOnlySpan<byte> json)
        {
            Serialization.Reader.ReadStringRequest(_packetData.Span, out json);
        }

        /// <summary>
        /// Read a code buffer update
        /// </summary>
        /// <param name="bufferSpace">Buffer space</param>
        public static void ReadCodeBufferUpdate(out ushort bufferSpace)
        {
            Serialization.Reader.ReadCodeBufferUpdate(_packetData.Span, out bufferSpace);
        }

        /// <summary>
        /// Read an incoming message
        /// </summary>
        /// <param name="messageType">Message type flags of the reply</param>
        /// <param name="reply">Code reply</param>
        public static void ReadMessage(out MessageTypeFlags messageType, out string reply)
        {
            Serialization.Reader.ReadMessage(_packetData.Span, out messageType, out reply);
        }

        /// <summary>
        /// Read the content of a <see cref="ExecuteMacroHeader"/> packet
        /// </summary>
        /// <param name="channel">Channel requesting a macro file</param>
        /// <param name="isSystemMacro">Indicates if this code is not bound to a code being executed (e.g. when a trigger macro is requested)</param>
        /// <param name="filename">Filename of the requested macro</param>
        public static void ReadMacroRequest(out CodeChannel channel, out bool isSystemMacro, out string filename)
        {
            Serialization.Reader.ReadMacroRequest(_packetData.Span, out channel, out isSystemMacro, out filename);
        }

        /// <summary>
        /// Read the content of an <see cref="AbortFileHeader"/> packet
        /// </summary>
        /// <param name="channel">Code channel where all files are supposed to be aborted</param>
        /// <param name="abortAll">Whether all files are supposed to be aborted</param>
        public static void ReadAbortFile(out CodeChannel channel, out bool abortAll)
        {
            Serialization.Reader.ReadAbortFile(_packetData.Span, out channel, out abortAll);
        }

        /// <summary>
        /// Read the content of a <see cref="PrintPausedHeader"/> packet
        /// </summary>
        /// <param name="filePosition">Position where the print has been paused</param>
        /// <param name="reason">Reason why the print has been paused</param>
        public static void ReadPrintPaused(out uint filePosition, out PrintPausedReason reason)
        {
            Serialization.Reader.ReadPrintPaused(_packetData.Span, out filePosition, out reason);
        }

        /// <summary>
        /// Read a code channel
        /// </summary>
        /// <param name="channel">Code channel that has acquired the lock</param>
        /// <returns>Asynchronous task</returns>
        public static void ReadCodeChannel(out CodeChannel channel)
        {
            Serialization.Reader.ReadCodeChannel(_packetData.Span, out channel);
        }

        /// <summary>
        /// Read a chunk of a <see cref="Request.FileChunk"/> packet
        /// </summary>
        /// <param name="filename">Filename</param>
        /// <param name="offset">File offset</param>
        /// <param name="maxLength">Maximum chunk size</param>
        public static void ReadFileChunkRequest(out string filename, out uint offset, out int maxLength)
        {
            Serialization.Reader.ReadFileChunkRequest(_packetData.Span, out filename, out offset, out maxLength);
        }

        /// <summary>
        /// Read the result of an expression evaluation request
        /// </summary>
        /// <param name="expression">Evaluated expression</param>
        /// <param name="result">Result</param>
        public static void ReadEvaluationResult(out string expression, out object? result)
        {
            Serialization.Reader.ReadEvaluationResult(_packetData.Span, out expression, out result);
        }

        /// <summary>
        /// Read a code request
        /// </summary>
        /// <param name="channel">Channel to execute this code on</param>
        /// <param name="code">Code to execute</param>
        public static void ReadDoCode(out CodeChannel channel, out string code)
        {
            Serialization.Reader.ReadDoCode(_packetData.Span, out channel, out code);
        }

        /// <summary>
        /// Read a request to check if a file exists
        /// </summary>
        /// <param name="filename">Name of the file</param>
        public static void ReadCheckFileExists(out string filename)
        {
            Serialization.Reader.ReadStringRequest(_packetData.Span, out filename);
        }

        /// <summary>
        /// Read a request to delete a file or directory
        /// </summary>
        /// <param name="filename">Name of the file</param>
        public static void ReadDeleteFileOrDirectory(out string filename)
        {
            Serialization.Reader.ReadStringRequest(_packetData.Span, out filename);
        }

        /// <summary>
        /// Read an open file request
        /// </summary>
        /// <param name="filename">Filename to open</param>
        /// <param name="forWriting">Whether the file is supposed to be written to</param>
        /// <param name="append">Whether data is supposed to be appended in write mode</param>
        /// <param name="preAllocSize">How many bytes to allocate if the file is created or overwritten</param>
        public static void ReadOpenFile(out string filename, out bool forWriting, out bool append, out long preAllocSize)
        {
            Serialization.Reader.ReadOpenFile(_packetData.Span, out filename, out forWriting, out append, out preAllocSize);
        }

        /// <summary>
        /// Read a request to seek in a file
        /// </summary>
        /// <param name="handle">File handle</param>
        /// <param name="offset">New file position</param>
        public static void ReadSeekFile(out uint handle, out long offset)
        {
            Serialization.Reader.ReadSeekFile(_packetData.Span, out handle, out offset);
        }

        /// <summary>
        /// Read a request to truncate a file
        /// </summary>
        /// <param name="handle">File handle</param>
        public static void ReadTruncateFile(out uint handle)
        {
            Serialization.Reader.ReadFileHandle(_packetData.Span, out handle);
        }

        /// <summary>
        /// Read a request to read data from a file
        /// </summary>
        /// <param name="handle">File handle</param>
        /// <param name="maxLength">Maximum data length</param>
        public static void ReadFileRequest(out uint handle, out int maxLength)
        {
            Serialization.Reader.ReadFileRequest(_packetData.Span, out handle, out maxLength);
        }

        /// <summary>
        /// Read a request to write data to a file
        /// </summary>
        /// <param name="handle">File handle</param>
        /// <param name="data">Data to write</param>
        public static void ReadWriteRequest(out uint handle, out ReadOnlySpan<byte> data)
        {
            int bytesRead = Serialization.Reader.ReadFileHandle(_packetData.Span, out handle);
            data = _packetData[bytesRead..].Span;
        }

        /// <summary>
        /// Read a request to close a file
        /// </summary>
        /// <param name="handle">File handle</param>
        public static void ReadCloseFile(out uint handle)
        {
            Serialization.Reader.ReadFileHandle(_packetData.Span, out handle);
        }

        /// <summary>
        /// Write the last packet + content for diagnostic purposes
        /// </summary>
        public static void DumpMalformedPacket()
        {
            using (FileStream stream = new(Path.Combine(Settings.BaseDirectory, "sys/transferDump.bin"), FileMode.Create, FileAccess.Write))
            {
                stream.Write(_rxBuffer[.._rxHeader.DataLength].Span);
            }

            string dump = "Received malformed packet:\n";
            dump += $"=== Packet #{_lastPacket.Id} from offset {_rxPointer} request {_lastPacket.Request} (length {_lastPacket.Length}) ===\n";
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
            _logger.Error(dump);
        }
        #endregion

        #region Write functions
        /// <summary>
        /// Write a packet
        /// </summary>
        /// <param name="request">SBC request to send</param>
        /// <param name="dataLength">Length of the extra payload</param>
        private static void WritePacket(Communication.SbcRequests.Request request, int dataLength = 0)
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
        private static Span<byte> GetWriteBuffer(int dataLength)
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
        public static void ResendPacket(PacketHeader packet, out Communication.SbcRequests.Request sbcRequest)
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
                    sbcRequest = (Communication.SbcRequests.Request)header.Request;
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
        public static bool WriteEmergencyStop()
        {
            if (!CanWritePacket())
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.EmergencyStop);
            return true;
        }

        /// <summary>
        /// Request a firmware reset
        /// </summary>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteReset()
        {
            if (!CanWritePacket())
            {
                return false;
            }

            _txPointer = 0;
            _resetting = true;
            WritePacket(Communication.SbcRequests.Request.Reset);
            return true;
        }

        /// <summary>
        /// Calculate the size of a binary G/M/T-code
        /// </summary>
        /// <param name="code">Code to write</param>
        /// <returns>Code size in bytes</returns>
        public static int GetCodeSize(Code code)
        {
            Span<byte> span = stackalloc byte[Settings.MaxCodeBufferSize];
            try
            {
                return Serialization.Writer.WriteCode(span, code);
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException("Failed to serialize code", e);
            }
        }

        /// <summary>
        /// Request a code to be executed
        /// </summary>
        /// <param name="code">Code to send</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteCode(Code code)
        {
            // Attempt to serialize the code first
            Span<byte> span = stackalloc byte[Settings.MaxCodeBufferSize];
            int codeLength;
            try
            {
                codeLength = Serialization.Writer.WriteCode(span, code);
            }
            catch (ArgumentException e)
            {
                throw new ArgumentException("Failed to serialize code", e);
            }

            // See if the code fits into the buffer
            if (!CanWritePacket(codeLength))
            {
                return false;
            }
            _numMeasuredCodes++;

            // Write it
            WritePacket(Communication.SbcRequests.Request.Code, codeLength);
            span[..codeLength].CopyTo(GetWriteBuffer(codeLength));
            return true;
        }

        /// <summary>
        /// Write the legacy request for the config response
        /// </summary>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteGetLegacyConfigResponse()
        {
            if (!CanWritePacket(sizeof(int)))
            {
                return false;
            }

            // Write header
            WritePacket(Communication.SbcRequests.Request.GetObjectModel, sizeof(int));

            // Write data
            byte[] configModuleRequest = [0, 0, 5, 0];
            configModuleRequest.CopyTo(GetWriteBuffer(configModuleRequest.Length));

            return true;
        }

        /// <summary>
        /// Request the key of a object module of a specific module
        /// </summary>
        /// <param name="key">Object model key to query</param>
        /// <param name="flags">Object model flags to query</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteGetObjectModel(string key, string flags)
        {
            // Serialize the request first to see how much space it requires
            Span<byte> span = stackalloc byte[bufferSize - Marshal.SizeOf<PacketHeader>()];
            int dataLength = Serialization.Writer.WriteGetObjectModel(span, key, flags);

            // See if the request fits into the buffer
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            // Write it
            WritePacket(Communication.SbcRequests.Request.GetObjectModel, dataLength);
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
        public static bool WriteSetObjectModel(string field, object value)
        {
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
        public static bool WritePrintFileInfo(GCodeFileInfo info)
        {
            // Serialize the request first to see how much space it requires
            Span<byte> span = stackalloc byte[bufferSize - Marshal.SizeOf<PacketHeader>()];
            int dataLength = Serialization.Writer.WritePrintFileInfo(span, info);

            // See if the request fits into the buffer
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            // Write it
            WritePacket(Communication.SbcRequests.Request.SetPrintFileInfo, dataLength);
            span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
            return true;
        }

        /// <summary>
        /// Notify that a file print has been stopped
        /// </summary>
        /// <param name="reason">Reason why the print has been stopped</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WritePrintStopped(PrintStoppedReason reason)
        {
            int dataLength = Marshal.SizeOf<Communication.SbcRequests.PrintStoppedHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.PrintStopped, dataLength);
            Serialization.Writer.WritePrintStopped(GetWriteBuffer(dataLength), reason);
            return true;
        }

        /// <summary>
        /// Notify the firmware about a completed macro file.
        /// This function is only used for macro files that the firmware requested
        /// </summary>
        /// <param name="channel">Code channel of the finished macro</param>
        /// <param name="error">Whether an error occurred</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteMacroCompleted(CodeChannel channel, bool error)
        {
            int dataLength = Marshal.SizeOf<Communication.SbcRequests.MacroCompleteHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.MacroCompleted, dataLength);
            Serialization.Writer.WriteMacroCompleted(GetWriteBuffer(dataLength), channel, error);
            return true;
        }

        /// <summary>
        /// Request the movement systems to be locked and wait for standstill
        /// </summary>
        /// <param name="channel">Code channel that requires the lock</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteLockAllMovementSystemsAndWaitForStandstill(CodeChannel channel)
        {
            int dataLength = Marshal.SizeOf<CodeChannelHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.LockAllMovementSystemsAndWaitForStandstill, dataLength);
            Serialization.Writer.WriteCodeChannel(GetWriteBuffer(dataLength), channel);
            return true;
        }

        /// <summary>
        /// Release all acquired locks again
        /// </summary>
        /// <param name="channel">Code channel that releases the locks</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteUnlock(CodeChannel channel)
        {
            int dataLength = Marshal.SizeOf<CodeChannelHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.Unlock, dataLength);
            Serialization.Writer.WriteCodeChannel(GetWriteBuffer(dataLength), channel);
            return true;
        }

        /// <summary>
        /// Write another segment of the IAP binary
        /// </summary>
        /// <param name="stream">IAP binary</param>
        /// <returns>Whether another segment could be written</returns>
        public static bool WriteIapSegment(Stream stream)
        {
            Span<byte> data = stackalloc byte[Consts.IapSegmentSize];
            int bytesRead = stream.Read(data);
            if (bytesRead <= 0)
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.WriteIap, bytesRead);
            data[..bytesRead].CopyTo(GetWriteBuffer(bytesRead));
            PerformFullTransfer();
            return true;
        }

        /// <summary>
        /// Instruct the firmware to start the IAP binary
        /// </summary>
        public static void StartIap()
        {
            // Tell the firmware to boot the IAP program
            WritePacket(Communication.SbcRequests.Request.StartIap);
            PerformFullTransfer();

            // Wait for the first transfer.
            // The IAP firmware will pull the transfer ready pin to high when it is ready to receive data
            _waitingForFirstTransfer = _updating = true;
        }

        /// <summary>
        /// Flash another segment of the firmware via the IAP binary
        /// </summary>
        /// <param name="stream">Stream of the firmware binary</param>
        /// <returns>Whether another segment could be sent</returns>
        public static bool FlashFirmwareSegment(Stream stream)
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
        public static bool VerifyFirmwareChecksum(long firmwareLength, ushort crc16)
        {
            // At this point IAP expects another segment so wait for it to be ready first. After that, wait a moment for IAP to acknowledge we're done
            WaitForTransfer();
            Thread.Sleep(Consts.FirmwareFinishedDelay);

            // Send the final firmware size plus CRC16 checksum to IAP
            Communication.SbcRequests.FlashVerify verifyRequest = new()
            {
                firmwareLength = (uint)firmwareLength,
                crc16 = crc16
            };
            Span<byte> transferData = stackalloc byte[Marshal.SizeOf<Communication.SbcRequests.FlashVerify>()];
            MemoryMarshal.Write(transferData, verifyRequest);
            WaitForTransfer();
            _spiDevice.TransferFullDuplex(transferData, transferData);

            // Check if the IAP can confirm our CRC16 checksum
            Span<byte> writeOk = stackalloc byte[1];
            WaitForTransfer();
            _spiDevice.TransferFullDuplex(writeOk, writeOk);
            return (writeOk[0] == 0x0C);
        }

        /// <summary>
        /// Wait for the IAP program to reset the controller
        /// </summary>
        public static void WaitForIapReset()
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
        public static bool WriteFileChunk(Span<byte> data, long fileLength)
        {
            // Serialize the request first to see how much space it requires
            Span<byte> span = stackalloc byte[bufferSize - Marshal.SizeOf<PacketHeader>()];
            int dataLength = Serialization.Writer.WriteFileChunk(span, data, fileLength);

            // See if the request fits into the buffer
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            // Write it
            WritePacket(Communication.SbcRequests.Request.FileChunk, dataLength);
            span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
            return true;
        }

        /// <summary>
        /// Write a request for an expression evaluation
        /// </summary>
        /// <param name="channel">Where to evaluate the expression</param>
        /// <param name="expression">Expression to evaluate</param>
        /// <returns>Whether the evaluation request has been written successfully</returns>
        public static bool WriteEvaluateExpression(CodeChannel channel, string expression)
        {
            // Serialize the request first to see how much space it requires
            Span<byte> span = stackalloc byte[bufferSize - Marshal.SizeOf<PacketHeader>()];
            int dataLength = Serialization.Writer.WriteEvaluateExpression(span, channel, expression);

            // See if the request fits into the buffer
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            // Write it
            WritePacket(Communication.SbcRequests.Request.EvaluateExpression, dataLength);
            span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
            return true;
        }

        /// <summary>
        /// Write a message
        /// </summary>
        /// <param name="flags">Message flags</param>
        /// <param name="message">Message content</param>
        /// <returns>Whether the firmware has been written successfully</returns>
        public static bool WriteMessage(MessageTypeFlags flags, string message)
        {
            // Serialize the request first to see how much space it requires
            Span<byte> span = stackalloc byte[bufferSize - Marshal.SizeOf<PacketHeader>()];
            int dataLength = Serialization.Writer.WriteMessage(span, flags, message);

            // See if the request fits into the buffer
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            // Write it
            WritePacket(Communication.SbcRequests.Request.Message, dataLength);
            span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
            return true;
        }

        /// <summary>
        /// Notify RepRapFirmware that a macro file could be started
        /// </summary>
        /// <param name="channel">Code channel that requires the lock</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteMacroStarted(CodeChannel channel)
        {
            int dataLength = Marshal.SizeOf<CodeChannelHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.MacroStarted, dataLength);
            Serialization.Writer.WriteCodeChannel(GetWriteBuffer(dataLength), channel);
            return true;
        }

        /// <summary>
        /// Called when a code channel is supposed to be invalidated (e.g. via abort keyword)
        /// </summary>
        /// <param name="channel">Code channel that requires the lock</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteInvalidateChannel(CodeChannel channel)
        {
            int dataLength = Marshal.SizeOf<CodeChannelHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.InvalidateChannel, dataLength);
            Serialization.Writer.WriteCodeChannel(GetWriteBuffer(dataLength), channel);
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
        public static bool WriteSetVariable(CodeChannel channel, bool createVariable, string varName, string expression)
        {
            // Serialize the request first to see how much space it requires
            Span<byte> span = stackalloc byte[bufferSize - Marshal.SizeOf<PacketHeader>()];
            int dataLength = Serialization.Writer.WriteSetVariable(span, channel, createVariable, varName, expression);

            // See if the request fits into the buffer
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            // Write it
            WritePacket(Communication.SbcRequests.Request.SetVariable, dataLength);
            span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
            return true;
        }

        /// <summary>
        /// Delete a local variable at the end of the current code block
        /// </summary>
        /// <param name="channel">G-code channel</param>
        /// <param name="varName">Name of the variable excluding var prefix</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteDeleteLocalVariable(CodeChannel channel, string varName)
        {
            // Serialize the request first to see how much space it requires
            Span<byte> span = stackalloc byte[bufferSize - Marshal.SizeOf<PacketHeader>()];
            int dataLength = Serialization.Writer.WriteDeleteLocalVariable(span, channel, varName);

            // See if the request fits into the buffer
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            // Write it
            WritePacket(Communication.SbcRequests.Request.DeleteLocalVariable, dataLength);
            span[..dataLength].CopyTo(GetWriteBuffer(dataLength));
            return true;
        }

        /// <summary>
        /// Send back whether a file exists or not
        /// </summary>
        /// <param name="exists">Whether the file exists</param>
        /// <returns>If the packet could be written</returns>
        public static bool WriteCheckFileExistsResult(bool exists)
        {
            int dataLength = Marshal.SizeOf<Communication.SbcRequests.BooleanHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.CheckFileExistsResult, dataLength);
            Serialization.Writer.WriteBoolean(GetWriteBuffer(dataLength), exists);
            return true;
        }

        /// <summary>
        /// Send back whether a file or directory could be deleted
        /// </summary>
        /// <param name="success">Whether the file operation was successful</param>
        /// <returns>If the packet could be written</returns>
        public static bool WriteFileDeleteResult(bool success)
        {
            int dataLength = Marshal.SizeOf<Communication.SbcRequests.BooleanHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.FileDeleteResult, dataLength);
            Serialization.Writer.WriteBoolean(GetWriteBuffer(dataLength), success);
            return true;
        }

        /// <summary>
        /// Write the new file handle and file length of the file that has just been opened
        /// </summary>
        /// <param name="fileHandle">New file handle or noFileHandle if the file could not be opened</param>
        /// <param name="length">Length of the file</param>
        /// <returns>If the packet could be written</returns>
        public static bool WriteOpenFileResult(uint fileHandle, long length)
        {
            int dataLength = Marshal.SizeOf<Communication.SbcRequests.OpenFileResult>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.OpenFileResult, dataLength);
            Serialization.Writer.WriteOpenFileResult(GetWriteBuffer(dataLength), fileHandle, length);
            return true;
        }

        /// <summary>
        /// Write requested read data from a file
        /// </summary>
        /// <param name="data">File data</param>
        /// <param name="bytesRead">Number of bytes read or negative on error</param>
        /// <returns>If the packet could be written</returns>
        public static bool WriteFileReadResult(Span<byte> data, int bytesRead)
        {
            int dataLength = Marshal.SizeOf<Communication.SbcRequests.FileDataHeader>() + data.Length;
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.FileReadResult, dataLength);
            Serialization.Writer.WriteFileReadResult(GetWriteBuffer(dataLength), data, bytesRead);
            return true;
        }

        /// <summary>
        /// Tell RRF if the last file block could be written
        /// </summary>
        /// <param name="success">If the file data could be written</param>
        /// <returns>If the packet could be written</returns>
        public static bool WriteFileWriteResult(bool success)
        {
            int dataLength = Marshal.SizeOf<Communication.SbcRequests.BooleanHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.FileWriteResult, dataLength);
            Serialization.Writer.WriteBoolean(GetWriteBuffer(dataLength), success);
            return true;
        }

        /// <summary>
        /// Tell RRF if the seek operation was successful
        /// </summary>
        /// <param name="success">If the seek operation succeeded</param>
        /// <returns>If the packet could be written</returns>
        public static bool WriteFileSeekResult(bool success)
        {
            int dataLength = Marshal.SizeOf<Communication.SbcRequests.BooleanHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.FileSeekResult, dataLength);
            Serialization.Writer.WriteBoolean(GetWriteBuffer(dataLength), success);
            return true;
        }

        /// <summary>
        /// Tell RRF if the seek operation was successful
        /// </summary>
        /// <param name="success">If the seek operation succeeded</param>
        /// <returns>If the packet could be written</returns>
        public static bool WriteFileTruncateResult(bool success)
        {
            int dataLength = Marshal.SizeOf<Communication.SbcRequests.BooleanHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.SbcRequests.Request.FileTruncateResult, dataLength);
            Serialization.Writer.WriteBoolean(GetWriteBuffer(dataLength), success);
            return true;
        }

        /// <summary>
        /// Checks if there is enough remaining space to accomodate a packet header plus payload data
        /// </summary>
        /// <param name="dataLength">Payload data length</param>
        /// <returns>True if there is enough space</returns>
        private static bool CanWritePacket(int dataLength = 0)
        {
            return _txPointer + Marshal.SizeOf<PacketHeader>() + dataLength <= bufferSize;
        }
        #endregion

        #region Functions for data transfers
        /// <summary>
        /// Wait for the Duet to flag when it is ready to transfer data
        /// </summary>
        /// <param name="inTransfer">Whether a full transfer is being performed</param>
        private static void WaitForTransfer(bool inTransfer = true)
        {
            if (_waitingForFirstTransfer)
            {
                // When a connection is established for the first time, the TfrRdy pin must be high
                _expectedTfrRdyPinValue = true;
            }

            _transferReadyPin.FlushEvents();
            if (_transferReadyPin.Value != _expectedTfrRdyPinValue)
            {
                // Determine how long to wait for the pin level transition
                int timeout;
                if (_waitingForFirstTransfer)
                {
                    timeout = _updating ? Consts.IapTimeout : Settings.SpiConnectTimeout;
                    _expectedTfrRdyPinValue = true;
                }
                else
                {
                    timeout = _updating ? Consts.IapTimeout : (inTransfer ? Settings.SpiTransferTimeout : Settings.SpiConnectionTimeout);
                }

                // Wait for the expected pin level
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    do
                    {
                        int timeToWait = timeout - (int)stopwatch.ElapsedMilliseconds;
                        if (timeToWait <= 0 || Program.CancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException();
                        }

                        if (_transferReadyPin.WaitForEvent(timeToWait) == _expectedTfrRdyPinValue)
                        {
                            break;
                        }
                        _numTfrPinGlitches++;
                    } while (true);
                }
                catch (OperationCanceledException)
                {
                    if (Program.CancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("Program termination");
                    }

                    if (stopwatch.ElapsedMilliseconds > timeout + 500)
                    {
                        // In case this application does not seem to get enough CPU time, log a different message
                        Logger.LogOutput(MessageType.Warning, "Did not get enough CPU time during SPI transfer, your SBC may be overloaded");
                    }
                    throw new OperationCanceledException("Timeout while waiting for transfer ready pin");
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
            _expectedTfrRdyPinValue = !_expectedTfrRdyPinValue;
            _waitingForFirstTransfer = false;
        }

        /// <summary>
        /// Write the CRC16 or CRC32 checksums
        /// </summary>
        private static void WriteCRC()
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
        private static bool ExchangeHeader()
        {
            for (int retry = 0; retry < Settings.MaxSpiRetries; retry++)
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
                    _logger.Warn("Received bad response instead of header, retrying exchange of the data response");
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
                    _logger.Warn("Restarting full transfer because a bad header format code was received (0x{0:x2})", _rxHeader.FormatCode);
                    ExchangeResponse(TransferResponse.BadResponse);
                    return false;
                }

                // Change the protocol version if necessary
                ushort lastProtocolVersion = _txHeader.ProtocolVersion;
                if (_rxHeader.ProtocolVersion != lastProtocolVersion &&
                    (_rxHeader.ProtocolVersion <= Consts.ProtocolVersion || Settings.UpdateOnly))
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
                        _logger.Warn("Bad header CRC32 (expected 0x{0:x8}, got 0x{1:x8})", _rxHeader.ChecksumHeader32, crc32);
                        responseCode = ExchangeResponse(TransferResponse.BadHeaderChecksum);
                        if (responseCode == TransferResponse.BadHeaderChecksum)
                        {
                            _logger.Warn("Note: RepRapFirmware didn't receive valid data either (code 0x{0:x8})", responseCode);
                        }
                        else
                        {
                            if (responseCode == TransferResponse.BadResponse)
                            {
                                _logger.Warn("Restarting full transfer because RepRapFirmware received a bad header response");
                            }
                            else
                            {
                                _logger.Warn("Restarting full transfer because an unexpected response code has been received (code 0x{0:x8})", responseCode);
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
                        _logger.Warn("Bad header CRC16 (expected 0x{0:x4}, got 0x{1:x4})", _rxHeader.ChecksumHeader16, crc16);
                        responseCode = ExchangeResponse(TransferResponse.BadHeaderChecksum);
                        if (responseCode == TransferResponse.BadResponse)
                        {
                            _logger.Warn("Restarting full transfer because RepRapFirmware received a bad header response");
                            return false;
                        }
                        if (responseCode != TransferResponse.Success)
                        {
                            _logger.Warn("Note: RepRapFirmware didn't receive valid data either (code 0x{0:x8})", responseCode);
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
                if (_rxHeader.ProtocolVersion > Consts.ProtocolVersion && !Settings.UpdateOnly)
                {
                    ExchangeResponse(TransferResponse.BadProtocolVersion);
                    throw new Exception($"Invalid protocol version {_rxHeader.ProtocolVersion}");
                }

                if (lastProtocolVersion != _txHeader.ProtocolVersion)
                {
                    _logger.Warn(_txHeader.ProtocolVersion < Consts.ProtocolVersion ? "Downgrading protocol version {0} to {1}" : "Upgrading protocol version {0} to {1}",
                        lastProtocolVersion, _txHeader.ProtocolVersion);
                }

                // Check the data length
                if (_rxHeader.DataLength > bufferSize)
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
                        _logger.Warn("RepRapFirmware got a bad header checksum");
                        continue;
                    case TransferResponse.BadResponse:
                        _logger.Warn("Restarting full transfer because RepRapFirmware received a bad header response");
                        return false;
                    default:
                        _logger.Warn("Restarting full transfer because a bad header response was received (0x{0:x8})", response);
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

            _logger.Warn("Restarting full transfer because the number of maximum retries has been exceeded");
            ExchangeResponse(TransferResponse.BadResponse);
            return false;
        }

        /// <summary>
        /// Exchange a response code
        /// </summary>
        /// <param name="response">Response to send</param>
        /// <returns>Received response</returns>
        private static uint ExchangeResponse(uint response)
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
        private static bool ExchangeData()
        {
            int bytesToTransfer = Math.Max(_rxHeader.DataLength, _txPointer);
            for (int retry = 0; retry < Settings.MaxSpiRetries; retry++)
            {
                WaitForTransfer();
                _spiDevice.TransferFullDuplex(_txBuffer.Value[..bytesToTransfer].Span, _rxBuffer[..bytesToTransfer].Span);

                // Check for possible response code
                uint responseCode = MemoryMarshal.Read<uint>(_rxBuffer.Span);
                if (responseCode == TransferResponse.BadResponse)
                {
                    _logger.Warn("Restarting full transfer because RepRapFirmware received a bad data response");
                    return false;
                }

                // Inspect received data
                if (_rxHeader.ProtocolVersion >= 4)
                {
                    uint crc32 = CRC32.Calculate(_rxBuffer[.._rxHeader.DataLength].Span);
                    if (crc32 != _rxHeader.ChecksumData32)
                    {
                        _logger.Warn("Bad data CRC32 (expected 0x{0:x8}, got 0x{1:x8})", _rxHeader.ChecksumData32, crc32);
                        responseCode = ExchangeResponse(TransferResponse.BadDataChecksum);
                        if (responseCode == TransferResponse.BadDataChecksum)
                        {
                            _logger.Warn("Note: RepRapFirmware didn't receive valid data either (code 0x{0:x8})", responseCode);
                        }
                        else
                        {
                            if (responseCode == TransferResponse.BadResponse)
                            {
                                _logger.Warn("Restarting full transfer because RepRapFirmware received a bad data response");
                            }
                            else
                            {
                                _logger.Warn("Restarting full transfer because an unexpected response code has been received (code 0x{0:x8})", responseCode);
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
                        _logger.Warn("Bad data CRC16 (expected 0x{0:x4}, got 0x{1:x4})", _rxHeader.ChecksumData16, crc16);
                        responseCode = ExchangeResponse(TransferResponse.BadDataChecksum);
                        if (responseCode == TransferResponse.BadResponse)
                        {
                            _logger.Warn("Restarting full transfer because RepRapFirmware received a bad data response");
                            return false;
                        }
                        if (responseCode != TransferResponse.Success)
                        {
                            _logger.Warn("Note: RepRapFirmware didn't receive valid data either (code 0x{0:x8})", responseCode);
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
        private static bool ExchangeDataResponse(out bool success)
        {
            for (int retry = 0; retry < Settings.MaxSpiRetries; retry++)
            {
                uint response = ExchangeResponse(TransferResponse.Success);
                switch (response)
                {
                    case TransferResponse.Success:
                        success = true;
                        return true;
                    case TransferResponse.BadDataChecksum:
                        _logger.Warn("RepRapFirmware got a bad data checksum");
                        success = false;
                        return false;
                    case TransferResponse.BadResponse:
                        _logger.Warn("Restarting full transfer because RepRapFirmware received a bad data response");
                        success = false;
                        return true;
                    default:
                        _logger.Warn("Restarting data response exchange because a bad code was received (0x{0:x8})", response);
                        ExchangeResponse(TransferResponse.BadResponse);
                        continue;
                }
            }
            throw new OperationCanceledException("SPI connection reset because the number of maximum retries has been exceeded");
        }
        #endregion
    }
}
