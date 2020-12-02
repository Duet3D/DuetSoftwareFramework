using DuetAPI;
using DuetAPI.ObjectModel;
using DuetAPI.Utility;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Code = DuetControlServer.Commands.Code;
using Nito.AsyncEx;
using LinuxApi;
using System.Collections.Generic;
using DuetControlServer.SPI.Communication;
using DuetControlServer.SPI.Communication.Shared;
using DuetControlServer.Model;
using System.Threading.Tasks;
using DuetControlServer.Utility;

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
        private static InputGpioPin _transferReadyPin;
        private static volatile bool _transferReadyPinMonitored;
        private static SpiDevice _spiDevice;
        private static readonly AsyncManualResetEvent _transferReadyEvent = new AsyncManualResetEvent();
        private static bool _waitingForFirstTransfer = true, _started, _hadTimeout, _resetting, _updating;
        private static ushort _lastTransferNumber;

        private static DateTime _lastMeasureTime = DateTime.Now;
        private static int _numMeasuredTransfers;

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
        private static readonly LinkedList<Memory<byte>> _txBuffers = new LinkedList<Memory<byte>>();
        private static LinkedListNode<Memory<byte>> _txBuffer;
        private static int _rxPointer, _txPointer;
        private static PacketHeader _lastPacket;
        private static ReadOnlyMemory<byte> _packetData;

        /// <summary>
        /// Currently-used protocol version
        /// </summary>
        public static int ProtocolVersion { get => _rxHeader.ProtocolVersion; }

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
            _txBuffer = _txBuffers.First;

            // Initialize transfer ready pin
            _transferReadyPin = new InputGpioPin(Settings.GpioChipDevice, Settings.TransferReadyPin, $"dcs-trp-{Settings.TransferReadyPin}");
            _transferReadyPin.PinChanged += (sender, pinValue) => _transferReadyEvent.Set();
            MonitorTransferReadyPin();

            // Initialize SPI device
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
        /// Get the nubmer of full transfers per second
        /// </summary>
        /// <returns>Full transfers per second</returns>
        private static decimal GetFullTransfersPerSecond()
        {
            if (_numMeasuredTransfers == 0)
            {
                return 0;
            }

            decimal result = _numMeasuredTransfers / (decimal)(DateTime.Now - _lastMeasureTime).TotalSeconds;
            _lastMeasureTime = DateTime.Now;
            Interlocked.Exchange(ref _numMeasuredTransfers, 0);
            return result;
        }

        /// <summary>
        /// Print diagnostics to the given string builder
        /// </summary>
        /// <param name="builder">Target to write to</param>
        public static void Diagnostics(StringBuilder builder)
        {
            builder.AppendLine($"Configured SPI speed: {Settings.SpiFrequency} Hz");
            builder.AppendLine($"Full transfers per second: {GetFullTransfersPerSecond():F2}");
        }

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
            _rxHeader.ChecksumData = 0;
            _rxHeader.ChecksumHeader = 0;

            // Set up TX transfer header
            _txHeader.NumPackets = _packetId;
            _txHeader.SequenceNumber++;
            _txHeader.DataLength = (ushort)_txPointer;
            _txHeader.ChecksumData = CRC16.Calculate(_txBuffer.Value.Slice(0, _txPointer).Span);
            MemoryMarshal.Write(_txHeaderBuffer.Span, ref _txHeader);
            _txHeader.ChecksumHeader = CRC16.Calculate(_txHeaderBuffer[0..(Marshal.SizeOf<TransferHeader>() - Marshal.SizeOf<ushort>())].Span);
            MemoryMarshal.Write(_txHeaderBuffer.Span, ref _txHeader);

            do
            {
                try
                {
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
                    if ((_hadTimeout || !_started) && ProtocolVersion != Consts.ProtocolVersion)
                    {
                        _ = Logger.LogOutput(MessageType.Warning, "Incompatible firmware, please upgrade as soon as possible");
                    }

                    // Deal with timeouts
                    if (_hadTimeout)
                    {
                        _ = Logger.LogOutput(MessageType.Success, "Connection to Duet established");
                        _hadTimeout = _resetting = false;
                    }

                    // Deal with the first transmission
                    if (!_started)
                    {
                        _lastTransferNumber = (ushort)(_rxHeader.SequenceNumber - 1);
                        _started = true;
                    }

                    // Transfer OK
                    Interlocked.Increment(ref _numMeasuredTransfers);
                    _txBuffer = _txBuffer.Next ?? _txBuffers.First;
                    _rxPointer = _txPointer = 0;
                    _packetId = 0;

                    // Deal with reset requests
                    if (_resetting && Settings.NoTerminateOnReset)
                    {
                        _started = _resetting = false;
                        _waitingForFirstTransfer = true;
                        _rxHeader.SequenceNumber = 1;
                        _txHeader.SequenceNumber = 0;
                        PerformFullTransfer(connecting);
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
                    if (!_hadTimeout && _started)
                    {
                        _waitingForFirstTransfer = _hadTimeout = true;
                        Updater.ConnectionLost();
                        _ = Logger.LogOutput(MessageType.Warning, $"Lost connection to Duet ({e.Message})");
                    }
                }
            }
            while (!Program.CancellationToken.IsCancellationRequested);
        }

        /// <summary>
        /// Check if the controller has been reset
        /// </summary>
        /// <returns>Whether the controller has been reset</returns>
        public static bool HadReset()
        {
            return _started && ((ushort)(_lastTransferNumber + 1) != _rxHeader.SequenceNumber);
        }

        #region Read functions
        /// <summary>
        /// Returns the number of packets to read
        /// </summary>
        public static int PacketsToRead { get => _rxHeader.NumPackets; }

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
        /// Read the legacy result of a <see cref="Communication.LinuxRequests.Request.GetObjectModel"/> request
        /// </summary>
        /// <param name="json">JSON data</param>
        public static void ReadLegacyConfigResponse(out ReadOnlySpan<byte> json)
        {
            Serialization.Reader.ReadLegacyConfigResponse(_packetData.Span, out json);
        }

        /// <summary>
        /// Read the result of a <see cref="Communication.LinuxRequests.Request.GetObjectModel"/> request
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
        /// Read the result of a <see cref="Communication.LinuxRequests.Request.GetHeightMap"/> request
        /// </summary>
        /// <param name="map">Received heightmap</param>
        public static void ReadHeightMap(out Heightmap map)
        {
            Serialization.Reader.ReadHeightMap(_packetData.Span, out map);
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
        public static void ReadFileChunkRequest(out string filename, out uint offset, out uint maxLength)
        {
            Serialization.Reader.ReadFileChunkRequest(_packetData.Span, out filename, out offset, out maxLength);
        }

        /// <summary>
        /// Read the result of an expression evaluation request
        /// </summary>
        /// <param name="expression">Evaluated expression</param>
        /// <param name="result">Result</param>
        public static void ReadEvaluationResult(out string expression, out object result)
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
        /// Write the last packet + content for diagnostic purposes
        /// </summary>
        public static void DumpMalformedPacket()
        {
            using (FileStream stream = new FileStream(Path.Combine(Directory.GetCurrentDirectory(), "transferDump.bin"), FileMode.Create, FileAccess.Write))
            {
                stream.Write(_rxBuffer.Slice(0, _rxHeader.DataLength).Span);
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
        /// <param name="request">Linux request to send</param>
        /// <param name="dataLength">Length of the extra payload</param>
        private static void WritePacket(Communication.LinuxRequests.Request request, int dataLength = 0)
        {
            PacketHeader header = new PacketHeader
            {
                Request = (ushort)request,
                Id = _packetId++,
                Length = (ushort)dataLength,
                ResendPacketId = 0
            };

            Span<byte> span = _txBuffer.Value[_txPointer..].Span;
            MemoryMarshal.Write(span, ref header);
            _txPointer += Marshal.SizeOf<PacketHeader>();
        }

        /// <summary>
        /// Get a span on a 4-byte bounary for writing packet data
        /// </summary>
        /// <param name="dataLength">Required data length</param>
        /// <returns>Data span</returns>
        private static Span<byte> GetWriteBuffer(int dataLength)
        {
            Span<byte> result = _txBuffer.Value.Slice(_txPointer, dataLength).Span;
            int padding = 4 - (dataLength % 4);
            _txPointer += dataLength + ((padding == 4) ? 0 : padding);
            return result;
        }

        /// <summary>
        /// Resend a packet back to the firmware
        /// </summary>
        /// <param name="packet">Packet holding the resend request</param>
        public static void ResendPacket(PacketHeader packet, out Communication.LinuxRequests.Request linuxRequest)
        {
            Span<byte> buffer = (_txBuffer.Next ?? _txBuffers.First).Value.Span;

            PacketHeader header;
            int headerSize = Marshal.SizeOf<PacketHeader>();
            do
            {
                // Read next packet
                header = MemoryMarshal.Cast<byte, PacketHeader>(buffer)[0];
                if (header.Id == packet.ResendPacketId)
                {
                    // Resend it but use a new identifier
                    linuxRequest = (Communication.LinuxRequests.Request)header.Request;
                    WritePacket(linuxRequest, header.Length);
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

            WritePacket(Communication.LinuxRequests.Request.EmergencyStop);
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
            WritePacket(Communication.LinuxRequests.Request.Reset);
            return true;
        }

        /// <summary>
        /// Calculate the size of a binary G/M/T-code
        /// </summary>
        /// <param name="code">Code to write</param>
        /// <returns>Code size in bytes</returns>
        public static int GetCodeSize(Code code)
        {
            Span<byte> span = stackalloc byte[Consts.MaxCodeBufferSize];
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
            Span<byte> span = stackalloc byte[Consts.MaxCodeBufferSize];
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

            // Write it
            WritePacket(Communication.LinuxRequests.Request.Code, codeLength);
            span.Slice(0, codeLength).CopyTo(GetWriteBuffer(codeLength));
            return true;
        }

        /// <summary>
        /// Write the legacy request for the config response
        /// </summary>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteGetLegacyConfigResponse()
        {
            if (!CanWritePacket(Marshal.SizeOf<int>()))
            {
                return false;
            }

            // Write header
            WritePacket(Communication.LinuxRequests.Request.GetObjectModel, Marshal.SizeOf<int>());

            // Write data
            byte[] configModuleRequest = new byte[] { 0, 0, 5, 0 };
            configModuleRequest.CopyTo(GetWriteBuffer(configModuleRequest.Length));

            return true;
        }

        /// <summary>
        /// Request the key of a object module of a specific module
        /// </summary>
        /// <param name="key">Object model key to query</param>
        /// <param name="flags">Objecvt model flags to query</param>
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
            WritePacket(Communication.LinuxRequests.Request.GetObjectModel, dataLength);
            span.Slice(0, dataLength).CopyTo(GetWriteBuffer(dataLength));
            return true;
        }

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
            WritePacket(Communication.LinuxRequests.Request.SetObjectModel, dataLength);
            span.Slice(0, dataLength).CopyTo(GetWriteBuffer(dataLength));
            return true;
        }

        /// <summary>
        /// Notify the firmware that a file print has started
        /// </summary>
        /// <param name="info">Information about the file being printed</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WritePrintStarted(ParsedFileInfo info)
        {
            // Serialize the request first to see how much space it requires
            Span<byte> span = stackalloc byte[bufferSize - Marshal.SizeOf<PacketHeader>()];
            int dataLength = Serialization.Writer.WritePrintStarted(span, info);

            // See if the request fits into the buffer
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            // Write it
            WritePacket(Communication.LinuxRequests.Request.PrintStarted, dataLength);
            span.Slice(0, dataLength).CopyTo(GetWriteBuffer(dataLength));
            return true;
        }

        /// <summary>
        /// Notify that a file print has been stopped
        /// </summary>
        /// <param name="reason">Reason why the print has been stopped</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WritePrintStopped(PrintStoppedReason reason)
        {
            int dataLength = Marshal.SizeOf<Communication.LinuxRequests.PrintStoppedHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.LinuxRequests.Request.PrintStopped, dataLength);
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
            int dataLength = Marshal.SizeOf<Communication.LinuxRequests.MacroCompleteHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.LinuxRequests.Request.MacroCompleted, dataLength);
            Serialization.Writer.WriteMacroCompleted(GetWriteBuffer(dataLength), channel, error);
            return true;
        }

        /// <summary>
        /// Request the heightmap from the firmware
        /// </summary>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteGetHeightMap()
        {
            if (!CanWritePacket())
            {
                return false;
            }

            WritePacket(Communication.LinuxRequests.Request.GetHeightMap);
            return true;
        }
        
        /// <summary>
        /// Write a heightmap to the firmware
        /// </summary>
        /// <param name="map">Heightmap to send</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteHeightMap(Heightmap map)
        {
            // Serialize the request first to see how much space it requires
            Span<byte> span = stackalloc byte[bufferSize - Marshal.SizeOf<PacketHeader>()];
            int dataLength = Serialization.Writer.WriteHeightMap(span, map);

            // See if the request fits into the buffer
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            // Write it
            WritePacket(Communication.LinuxRequests.Request.SetHeightMap, dataLength);
            span.Slice(0, dataLength).CopyTo(GetWriteBuffer(dataLength));
            return true;
        }

        /// <summary>
        /// Request the movement to be locked and wait for standstill
        /// </summary>
        /// <param name="channel">Code channel that requires the lock</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteLockMovementAndWaitForStandstill(CodeChannel channel)
        {
            int dataLength = Marshal.SizeOf<CodeChannelHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.LinuxRequests.Request.LockMovementAndWaitForStandstill, dataLength);
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

            WritePacket(Communication.LinuxRequests.Request.Unlock, dataLength);
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

            WritePacket(Communication.LinuxRequests.Request.WriteIap, bytesRead);
            data.Slice(0, bytesRead).CopyTo(GetWriteBuffer(bytesRead));
            PerformFullTransfer();
            return true;
        }

        /// <summary>
        /// Instruct the firmware to start the IAP binary
        /// </summary>
        public static void StartIap()
        {
            // Tell the firmware to boot the IAP program
            WritePacket(Communication.LinuxRequests.Request.StartIap);
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

            if (readBuffer[bytesRead - 1] != 0x1A)
            {
                throw new Exception("Received invalid response from IAP");
            }
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
            Communication.LinuxRequests.FlashVerify verifyRequest = new Communication.LinuxRequests.FlashVerify
            {
                firmwareLength = (uint)firmwareLength,
                crc16 = crc16
            };
            Span<byte> transferData = stackalloc byte[Marshal.SizeOf<Communication.LinuxRequests.FlashVerify>()];
            MemoryMarshal.Write(transferData, ref verifyRequest);
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
        public static async Task WaitForIapReset()
        {
            // Wait a moment for the firmware to start
            await Task.Delay(Consts.IapRebootDelay);

            // Wait for the first data transfer from the firmware
            _updating = _started = false;
            _waitingForFirstTransfer = true;
            _rxHeader.SequenceNumber = 1;
            _txHeader.SequenceNumber = 0;
        }

        /// <summary>
        /// Assign a filament name to the given extruder drive
        /// </summary>
        /// <param name="extruder">Extruder index</param>
        /// <param name="filamentName">Filament name</param>
        /// <returns>Whether the firmware has been written successfully</returns>
        public static bool WriteAssignFilament(int extruder, string filamentName)
        {
            // Serialize the request first to see how much space it requires
            Span<byte> span = stackalloc byte[bufferSize - Marshal.SizeOf<PacketHeader>()];
            int dataLength = Serialization.Writer.WriteAssignFilament(span, extruder, filamentName);

            // See if the request fits into the buffer
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            // Write it
            WritePacket(Communication.LinuxRequests.Request.AssignFilament, dataLength);
            span.Slice(0, dataLength).CopyTo(GetWriteBuffer(dataLength));
            return true;
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
            WritePacket(Communication.LinuxRequests.Request.FileChunk, dataLength);
            span.Slice(0, dataLength).CopyTo(GetWriteBuffer(dataLength));
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
            WritePacket(Communication.LinuxRequests.Request.EvaluateExpression, dataLength);
            span.Slice(0, dataLength).CopyTo(GetWriteBuffer(dataLength));
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
            WritePacket(Communication.LinuxRequests.Request.Message, dataLength);
            span.Slice(0, dataLength).CopyTo(GetWriteBuffer(dataLength));
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

            WritePacket(Communication.LinuxRequests.Request.MacroStarted, dataLength);
            Serialization.Writer.WriteCodeChannel(GetWriteBuffer(dataLength), channel);
            return true;
        }

        /// <summary>
        /// Called when all the files have been aborted by DSF (e.g. via abort keyword)
        /// </summary>
        /// <param name="channel">Code channel that requires the lock</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteFilesAborted(CodeChannel channel)
        {
            int dataLength = Marshal.SizeOf<CodeChannelHeader>();
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.LinuxRequests.Request.FilesAborted, dataLength);
            Serialization.Writer.WriteCodeChannel(GetWriteBuffer(dataLength), channel);
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
        /// Internal function to monitor the transfer ready pin
        /// </summary>
        public static void MonitorTransferReadyPin()
        {
            _transferReadyPinMonitored = true;
            _transferReadyPin.StartMonitoring(Program.CancellationToken)
                .ContinueWith(async task =>
                {
                    try
                    {
                        // Wait for the task to complete
                        await task;
                    }
                    catch (Exception e)
                    {
                        if (!(e is OperationCanceledException))
                        {
                            _transferReadyPinMonitored = false;
                            _logger.Error(e, "Failed to monitor transfer ready pin");
                        }
                    }
                });
        }

        /// <summary>
        /// Wait for the Duet to flag when it is ready to transfer data
        /// </summary>
        private static void WaitForTransfer()
        {
            if (!_transferReadyPinMonitored)
            {
                throw new InvalidOperationException("Transfer ready pin is not monitored");
            }

            if (_waitingForFirstTransfer)
            {
                _transferReadyEvent.Reset();
                if (!_transferReadyPin.Value)
                {
                    if (_updating)
                    {
                        // Ignore shutdown requests and timeouts when an update is in progress
                        _transferReadyEvent.Wait();
                    }
                    else
                    {
                        // Wait a moment until the transfer ready pin is toggled or until a timeout has occurred
                        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);
                        cts.CancelAfter(Settings.SpiTransferTimeout);
                        try
                        {
                            _transferReadyEvent.Wait(cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            if (Program.CancellationToken.IsCancellationRequested)
                            {
                                throw new OperationCanceledException("Program termination");
                            }
                            throw new OperationCanceledException("Timeout while waiting for transfer ready pin");
                        }
                    }
                }
                _waitingForFirstTransfer = false;
            }
            else if (_updating)
            {
                // Ignore shutdown requests and timeouts when an update is in progress
                _transferReadyEvent.Wait();
            }
            else
            {
                // Wait a moment until the transfer ready pin is toggled or until a timeout has occurred
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(Program.CancellationToken);
                cts.CancelAfter(Settings.SpiTransferTimeout);
                try
                {
                    _transferReadyEvent.Wait(cts.Token);
                }
                catch
                {
                    if (Program.CancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("Program termination");
                    }
                    throw new OperationCanceledException("Timeout while waiting for transfer ready pin");
                }
            }
            _transferReadyEvent.Reset();
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
                WaitForTransfer();
                _spiDevice.TransferFullDuplex(_txHeaderBuffer.Span, _rxHeaderBuffer.Span);

                // Check for possible response code
                uint responseCode = MemoryMarshal.Read<uint>(_rxHeaderBuffer.Span);
                if (responseCode == TransferResponse.BadResponse)
                {
                    _logger.Warn("Restarting transfer because the Duet received a bad response (header)");
                    return false;
                }

                // Inspect received header
                _rxHeader = MemoryMarshal.Cast<byte, TransferHeader>(_rxHeaderBuffer.Span)[0];
                if (_rxHeader.FormatCode == 0 || _rxHeader.FormatCode == 0xFF)
                {
                    throw new OperationCanceledException("Board is not available (no header)");
                }

                ushort checksum = CRC16.Calculate(_rxHeaderBuffer[..(Marshal.SizeOf<TransferHeader>() - Marshal.SizeOf<ushort>())].Span);
                if (_rxHeader.ChecksumHeader != checksum)
                {
                    _logger.Warn("Bad header checksum (expected 0x{0}, got 0x{1})", _rxHeader.ChecksumHeader.ToString("x4"), checksum.ToString("x4"));
                    responseCode = ExchangeResponse(TransferResponse.BadHeaderChecksum);
                    if (responseCode == TransferResponse.BadResponse)
                    {
                        _logger.Warn("Restarting transfer because the Duet received a bad response (header response)");
                        return false;
                    }
                    if (responseCode != TransferResponse.Success)
                    {
                        _logger.Warn("Note: RepRapFirmware didn't receive valid data either (code 0x{0})", responseCode.ToString("x8"));
                    }
                    continue;
                }

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

                // Change the protocol version if necessary
                if (_rxHeader.ProtocolVersion != _txHeader.ProtocolVersion)
                {
                    if (_rxHeader.ProtocolVersion <= Consts.ProtocolVersion || Settings.UpdateOnly)
                    {
                        if (_rxHeader.ProtocolVersion < Consts.ProtocolVersion)
                        {
                            _logger.Warn("Downgrading protocol version {0} to {1}", _txHeader.ProtocolVersion, _rxHeader.ProtocolVersion);
                        }
                        else
                        {
                            _logger.Warn("Upgrading protocol version {0} to {1}", _txHeader.ProtocolVersion, _rxHeader.ProtocolVersion);
                        }
                        _txHeader.ProtocolVersion = _rxHeader.ProtocolVersion;
                        MemoryMarshal.Write(_txHeaderBuffer.Span, ref _txHeader);
                        _txHeader.ChecksumHeader = CRC16.Calculate(_txHeaderBuffer[..(Marshal.SizeOf<TransferHeader>() - Marshal.SizeOf<ushort>())].Span);
                        MemoryMarshal.Write(_txHeaderBuffer.Span, ref _txHeader);
                        ExchangeResponse(TransferResponse.BadResponse);
                        continue;
                    }
                    else
                    {
                        ExchangeResponse(TransferResponse.BadProtocolVersion);
                        throw new Exception($"Invalid protocol version {_rxHeader.ProtocolVersion}");
                    }
                }

                if (_rxHeader.DataLength > bufferSize)
                {
                    ExchangeResponse(TransferResponse.BadDataLength);
                    throw new Exception($"Data too long ({_rxHeader.DataLength} bytes)");
                }

                // Acknowledge reception
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
                        _logger.Warn("Restarting transfer because RepRapFirmware received a bad response (header response)");
                        return false;
                    case TransferResponse.LowPin:
                    case TransferResponse.HighPin:
                        throw new OperationCanceledException("Board is not available (no header response)");
                    default:
                        _logger.Warn("Restarting transfer because a bad header response was received (0x{0})", response.ToString("x8"));
                        ExchangeResponse(TransferResponse.BadResponse);
                        return false;
                }
            }

            _logger.Warn("Restarting transfer because the number of maximum retries has been exceeded");
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
            Span<byte> txResponseBuffer = stackalloc byte[Marshal.SizeOf<uint>()], rxResponseBuffer = stackalloc byte[Marshal.SizeOf<uint>()];
            MemoryMarshal.Write(txResponseBuffer, ref response);

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
                _spiDevice.TransferFullDuplex(_txBuffer.Value.Slice(0, bytesToTransfer).Span, _rxBuffer.Slice(0, bytesToTransfer).Span);

                // Check for possible response code
                uint responseCode = MemoryMarshal.Read<uint>(_rxBuffer.Span);
                if (responseCode == TransferResponse.BadResponse)
                {
                    _logger.Warn("Restarting transfer because RepRapFirmware received a bad response (data content)");
                    return false;
                }

                // Inspect received data
                ushort checksum = CRC16.Calculate(_rxBuffer.Slice(0, _rxHeader.DataLength).Span);
                if (_rxHeader.ChecksumData != checksum)
                {
                    _logger.Warn("Bad data checksum (expected 0x{0}, got 0x{1})", _rxHeader.ChecksumData.ToString("x4"), checksum.ToString("x4"));
                    responseCode = ExchangeResponse(TransferResponse.BadDataChecksum);
                    if (responseCode == TransferResponse.BadResponse)
                    {
                        _logger.Warn("Restarting transfer because the Duet received a bad response (data response)");
                        return false;
                    }
                    if (responseCode != TransferResponse.Success)
                    {
                        _logger.Warn("Note: RepRapFirmware didn't receive valid data either (code 0x{0})", responseCode.ToString("x8"));
                    }
                    continue;
                }

                uint response = ExchangeResponse(TransferResponse.Success);
                switch (response)
                {
                    case TransferResponse.Success:
                        return true;
                    case TransferResponse.BadDataChecksum:
                        _logger.Warn("RepRapFirmware got a bad data checksum");
                        continue;
                    case TransferResponse.BadResponse:
                        _logger.Warn("Restarting transfer because RepRapFirmware received a bad response (data response)");
                        return false;
                    case TransferResponse.LowPin:
                    case TransferResponse.HighPin:
                        throw new OperationCanceledException("Board is not available (no data response)");
                    default:
                        _logger.Warn("Restarting transfer because a bad data response was received (0x{0})", response.ToString("x8"));
                        ExchangeResponse(TransferResponse.BadResponse);
                        return false;
                }
            }

            _logger.Warn("Restarting transfer because the number of maximum retries has been exceeded");
            ExchangeResponse(TransferResponse.BadResponse);
            return false;
        }
#endregion
    }
}
