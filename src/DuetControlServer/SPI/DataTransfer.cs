using DuetAPI;
using DuetAPI.Machine;
using DuetAPI.Utility;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using Nito.AsyncEx;
using System;
using System.Device.Gpio;
using System.Device.Spi;
using System.Device.Spi.Drivers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Code = DuetControlServer.Commands.Code;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// Helper class for SPI data transfers
    /// </summary>
    public static class DataTransfer
    {
        // Physical devices and transfer helpers
        private static SpiDevice _spiDevice;
        private static GpioController _pinController;

        // General transfer variables
#if false
        private static AsyncAutoResetEvent _transferReadyEvent = new AsyncAutoResetEvent();
#endif
        private static ushort _lastTransferNumber;
        private static bool _started, _hadTimeout, _resetting;

        private static DateTime _lastMeasureTime = DateTime.Now;
        private static int _numMeasuredTransfers;

        // Transfer headers
        private static readonly Memory<byte> _rxHeaderBuffer = new byte[Marshal.SizeOf(typeof(Communication.TransferHeader))];
        private static readonly Memory<byte> _txHeaderBuffer = new byte[Marshal.SizeOf(typeof(Communication.TransferHeader))];
        private static Communication.TransferHeader _rxHeader;
        private static Communication.TransferHeader _txHeader;
        private static byte _packetId;

        // Transfer responses
        private static readonly Memory<byte> _rxResponseBuffer = new byte[4];
        private static readonly Memory<byte> _txResponseBuffer = new byte[4];

        // Transfer data. Keep two TX buffers so resend requests can be processed
        private static readonly Memory<byte> _rxBuffer = new byte[Communication.Consts.BufferSize];
        private static readonly Memory<byte>[] _txBuffers = { new byte[Communication.Consts.BufferSize], new byte[Communication.Consts.BufferSize] };
        private static int _txBufferIndex;
        private static int _rxPointer, _txPointer;
        private static Communication.PacketHeader _lastPacket;
        private static ReadOnlyMemory<byte> _packetData;

        /// <summary>
        /// Set up the SPI device and the controller for the transfer ready pin
        /// </summary>
        public static void Initialize()
        {
            // Initialize TX header. This only needs to happen once
            Serialization.Writer.InitTransferHeader(ref _txHeader);

            // Initialize SPI subsystem
            _spiDevice = new UnixSpiDevice(new SpiConnectionSettings(Settings.SpiBusID, Settings.SpiChipSelectLine) { ClockFrequency = Settings.SpiFrequency });

            // Initialize transfer ready pin
            _pinController = new GpioController(PinNumberingScheme.Logical);
            _pinController.OpenPin(Settings.TransferReadyPin);
            _pinController.SetPinMode(Settings.TransferReadyPin, PinMode.InputPullDown);
#if false
            _pinController.RegisterCallbackForPinValueChangedEvent(Settings.TransferReadyPin, PinEventTypes.Rising, OnTransferReady);
            if (_pinController.Read(Settings.TransferReadyPin) == PinValue.High)
            {
                _transferReadyEvent.Set();
            }
#endif
        }

        /// <summary>
        /// Get the average number of full transfers per second
        /// </summary>
        /// <returns>Average number of full transfers per second</returns>
        public static decimal GetFullTransfersPerSecond()
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
        /// Perform a full data transfer
        /// </summary>
        /// <param name="mustSucceed">Keep retrying until the transfer succeeds</param>
        /// <returns>Whether new data could be transferred</returns>
        public static async Task<bool> PerformFullTransfer(bool mustSucceed = true)
        {
            _lastTransferNumber = _rxHeader.SequenceNumber;

            bool success = false;
            do
            {
                try
                {
                    // Exchange transfer headers. This also deals with transfer responses
                    if (!await ExchangeHeader())
                    {
                        continue;
                    }

                    // Exchange data if there is anything to transfer
                    if ((_rxHeader.DataLength != 0 || _txPointer != 0) && !await ExchangeData())
                    {
                        continue;
                    }

                    // Reset some variables for the next transfer
                    Interlocked.Increment(ref _numMeasuredTransfers);
                    _txBufferIndex = (_txBufferIndex == 0) ? 1 : 0;
                    _rxPointer = _txPointer = 0;
                    _packetId = 0;

                    // Deal with timeouts
                    if (_hadTimeout)
                    {
                        using (await Model.Provider.AccessReadWrite())
                        {
                            if (Model.Provider.Get.State.Status == MachineStatus.Off)
                            {
                                Model.Provider.Get.State.Status = MachineStatus.Idle;
                            }
                            Model.Provider.Get.Messages.Add(new Message(MessageType.Success, "Connection to Duet established"));
                            Console.WriteLine("[info] Connection to Duet established");
                        }
                        _hadTimeout = _resetting = false;
                        break;
                    }

                    // Deal with resets
                    if (!_started)
                    {
                        _lastTransferNumber = (ushort)(_rxHeader.SequenceNumber - 1);
                        _started = true;
                    }

                    if (_resetting)
                    {
                        _hadTimeout = true;
                        continue;
                    }

                    // Transfer OK
                    _txHeader.SequenceNumber++;
                    success = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    if (!Program.CancelSource.IsCancellationRequested && !_hadTimeout && _started)
                    {
                        // A timeout occurs when the firmware is being updated
                        bool isUpdating;
                        using (await Model.Provider.AccessReadOnly())
                        {
                            isUpdating = (Model.Provider.Get.State.Status == MachineStatus.Updating);
                        }

                        // If this is the first unexpected timeout event, report it
                        if (!isUpdating)
                        {
                            _hadTimeout = true;
                            using (await Model.Provider.AccessReadWrite())
                            {
                                Model.Provider.Get.State.Status = MachineStatus.Off;
                                Model.Provider.Get.Messages.Add(new Message(MessageType.Warning, "Lost connection to Duet"));
                                Console.WriteLine("[warn] Lost connection to Duet");
                            }
                        }
                    }
                }
            } while (mustSucceed && !Program.CancelSource.IsCancellationRequested);

            return success;
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
        public static Communication.PacketHeader? ReadPacket()
        {
            if (_rxPointer >= _rxHeader.DataLength)
            {
                return null;
            }

            // Header
            _lastPacket = Serialization.Reader.ReadPacketHeader(_rxBuffer.Slice(_rxPointer).Span);
            _rxPointer += Marshal.SizeOf(_lastPacket);

            // Packet data
            _packetData = _rxBuffer.Slice(_rxPointer, _lastPacket.Length);
            int padding = 4 - (_lastPacket.Length % 4);
            _rxPointer += _lastPacket.Length + ((padding == 4) ? 0 : padding);

            return _lastPacket;
        }

        /// <summary>
        /// Read the result of a <see cref="Communication.LinuxRequests.Request.GetState"/> request
        /// </summary>
        /// <param name="busyChannels">Bitmap of the busy channels</param>
        public static void ReadState(out int busyChannels)
        {
            Serialization.Reader.ReadState(_packetData.Span, out busyChannels);
        }

        /// <summary>
        /// Read the result of a <see cref="Communication.LinuxRequests.Request.GetObjectModel"/> request
        /// </summary>
        /// <param name="module">Module described by the returned JSON data</param>
        /// <param name="json">JSON data</param>
        public static void ReadObjectModel(out byte module, out string json)
        {
            Serialization.Reader.ReadObjectModel(_packetData.Span, out module, out json);
        }

        /// <summary>
        /// Read the result of a <see cref="Communication.LinuxRequests.Request.Code"/> request
        /// </summary>
        /// <param name="messageType">Message type flags of the reply</param>
        /// <param name="reply">Code reply</param>
        public static void ReadCodeReply(out Communication.MessageTypeFlags messageType, out string reply)
        {
            Serialization.Reader.ReadCodeReply(_packetData.Span, out messageType, out reply);
        }

        /// <summary>
        /// Read the content of a <see cref="MacroRequest"/> packet
        /// </summary>
        /// <param name="channel">Channel requesting a macro file</param>
        /// <param name="reportMissing">Write an error message if the macro is not found</param>
        /// <param name="filename">Filename of the requested macro</param>
        public static void ReadMacroRequest(out CodeChannel channel, out bool reportMissing, out string filename)
        {
            Serialization.Reader.ReadMacroRequest(_packetData.Span, out channel, out reportMissing, out filename);
        }

        /// <summary>
        /// Read the content of an <see cref="AbortFileRequest"/> packet
        /// </summary>
        /// <param name="channel">Code channel where all files are supposed to be aborted</param>
        public static void ReadAbortFile(out CodeChannel channel)
        {
            Serialization.Reader.ReadAbortFile(_packetData.Span, out channel);
        }

        /// <summary>
        /// Read the content of a <see cref="StackEvent"/> packet
        /// </summary>
        /// <param name="channel">Code channel where the event occurred</param>
        /// <param name="stackDepth">New stack depth</param>
        /// <param name="flags">Bitmap holding info about the stack</param>
        /// <param name="feedrate">Sticky feedrate on this channel</param>
        public static void ReadStackEvent(out CodeChannel channel, out byte stackDepth, out StackFlags flags, out float feedrate)
        {
            Serialization.Reader.ReadStackEvent(_packetData.Span, out channel, out stackDepth, out flags, out feedrate);
        }

        /// <summary>
        /// Read the content of a <see cref="PrintPaused"/> packet
        /// </summary>
        /// <param name="filePosition">Position where the print has been paused</param>
        /// <param name="reason">Reason why the print has been paused</param>
        public static void ReadPrintPaused(out uint filePosition, out Communication.PrintPausedReason reason)
        {
            Serialization.Reader.ReadPrintPaused(_packetData.Span, out filePosition, out reason);
        }

        /// <summary>
        /// Read the result of a <see cref="Communication.LinuxRequests.Request.GetHeightMap"/> request
        /// </summary>
        /// <param name="header">Description of the heightmap</param>
        /// <param name="zCoordinates">Array of probed Z coordinates</param>
        public static void ReadHeightMap(out Heightmap map)
        {
            Serialization.Reader.ReadHeightMap(_packetData.Span, out map);
        }

        /// <summary>
        /// Read the content of a <see cref="Request.Locked"/> packet
        /// </summary>
        /// <param name="channel">Code channel that has acquired the lock</param>
        /// <returns>Asynchronous task</returns>
        public static void ReadResourceLocked(out CodeChannel channel)
        {
            Serialization.Reader.ReadResourceLocked(_packetData.Span, out channel);
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

            string dump = "[err] Received malformed packet:\n";
            dump += $"=== Packet #{_lastPacket.Id} from offset {_rxPointer} request {_lastPacket.Request} (length {_lastPacket.Length}) ===\n";
            foreach(byte c in _packetData.Span)
            {
                dump += ((int)c).ToString("x2");
            }
            dump += "\n";
            string str = System.Text.Encoding.UTF8.GetString(_packetData.Span);
            foreach(char c in str)
            {
                dump += char.IsLetterOrDigit(c) ? c : '.';
            }
            dump += "\n";
            dump += "====================";
            Console.WriteLine(dump);
        }
#endregion

#region Write functions
        /// <summary>
        /// Resend a packet back to the firmware
        /// </summary>
        /// <param name="packet">Packet holding the resend request</param>
        public static void ResendPacket(Communication.PacketHeader packet)
        {
            Span<byte> buffer = ((_txBufferIndex == 0) ? _txBuffers[1] : _txBuffers[0]).Span;
            Communication.PacketHeader header;
            int headerSize = Marshal.SizeOf(typeof(Communication.PacketHeader));
            do
            {
                // Read next packet
                header = MemoryMarshal.Read<Communication.PacketHeader>(buffer);
                if (header.Id == packet.ResendPacketId)
                {
                    Span<byte> destination = GetWriteBuffer(headerSize + header.Length);
                    buffer.Slice(0, headerSize + header.Length).CopyTo(destination);
                    return;
                }

                // Move on to the next one
                int padding = 4 - (header.Length % 4);
                buffer = buffer.Slice(headerSize + header.Length + ((padding == 4) ? 0 : padding));
            } while (header.Id < packet.ResendPacketId && buffer.Length > 0);

            throw new ArgumentException($"Firmware requested resend for invalid packet #{packet.ResendPacketId}");
        }

        /// <summary>
        /// Request the GCodeBuffer states from the firmware
        /// </summary>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteGetState()
        {
            if (!CanWritePacket())
            {
                return false;
            }

            WritePacket(Communication.LinuxRequests.Request.GetState);
            return true;
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

            _resetting = true;
            WritePacket(Communication.LinuxRequests.Request.Reset);
            return true;
        }

        /// <summary>
        /// Request a code to be executed
        /// </summary>
        /// <param name="code">Code to send</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteCode(Code code)
        {
            // Attempt to serialize the code first
            Span<byte> span = /*stackalloc*/ new byte[Communication.Consts.MaxCodeBufferSize];
            int codeLength;
            try
            {
                codeLength = Serialization.Writer.WriteCode(span, code);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ArgumentException("Value is too big", nameof(code));
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
        /// Request the object module of a specific module
        /// </summary>
        /// <param name="module">Module index to query</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteGetObjectModel(byte module)
        {
            int dataLength = Marshal.SizeOf(typeof(Communication.SharedRequests.ObjectModel));
            if (!CanWritePacket(dataLength))
            {
                return false;
            }

            WritePacket(Communication.LinuxRequests.Request.GetObjectModel, dataLength);
            Serialization.Writer.WriteObjectModelRequest(GetWriteBuffer(dataLength), module);
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
            // Serialize the requqest first to see how much space it requires
            Span<byte> span = new byte[Communication.Consts.BufferSize - Marshal.SizeOf(typeof(Communication.PacketHeader))];
            int dataLength = Serialization.Writer.WriteObjectModel(span, field, value);

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
            // Serialize the requqest first to see how much space it requires
            Span<byte> span = new byte[Communication.Consts.BufferSize - Marshal.SizeOf(typeof(Communication.PacketHeader))];
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
        public static bool WritePrintStopped(Communication.PrintStoppedReason reason)
        {
            int dataLength = Marshal.SizeOf(typeof(Communication.LinuxRequests.PrintStopped));
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
            int dataLength = Marshal.SizeOf(typeof(Communication.LinuxRequests.MacroCompleted));
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
        
        public static bool WriteHeightMap(Heightmap map)
        {
            // Serialize the requqest first to see how much space it requires
            Span<byte> span = new byte[Communication.Consts.BufferSize - Marshal.SizeOf(typeof(Communication.PacketHeader))];
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
            if (!CanWritePacket())
            {
                return false;
            }

            WritePacket(Communication.LinuxRequests.Request.LockMovementAndWaitForStandstill);
            return true;
        }

        /// <summary>
        /// Release all acquired locks again
        /// </summary>
        /// <param name="channel">Code channel that releases the locks</param>
        /// <returns>True if the packet could be written</returns>
        public static bool WriteUnlock(CodeChannel channel)
        {
            if (!CanWritePacket())
            {
                return false;
            }

            WritePacket(Communication.LinuxRequests.Request.UnlockAll);
            return true;
        }

        private static bool CanWritePacket(int dataLength = 0)
        {
            return _txPointer + Marshal.SizeOf(typeof(Communication.PacketHeader)) + dataLength <= Communication.Consts.BufferSize;
        }

        private static void WritePacket(Communication.LinuxRequests.Request request, int dataLength = 0)
        {
            Communication.PacketHeader header = new Communication.PacketHeader
            {
                Request = (ushort)request,
                Id = _packetId++,
                Length = (ushort)dataLength,
                ResendPacketId = 0
            };

            Span<byte> span = _txBuffers[_txBufferIndex].Slice(_txPointer).Span;
            MemoryMarshal.Write(span, ref header);
            _txPointer += Marshal.SizeOf(header);
        }

        private static Span<byte> GetWriteBuffer(int dataLength)
        {
            Span<byte> result = _txBuffers[_txBufferIndex].Slice(_txPointer, dataLength).Span;
            int padding = 4 - (dataLength % 4);
            _txPointer += dataLength + ((padding == 4) ? 0 : padding);
            return result;
        }
        #endregion

        #region Functions for data transfers
#if false
        private static void OnTransferReady(object sender, PinValueChangedEventArgs pinValueChangedEventArgs)
        {
            _transferReadyEvent.Set();
        }
#endif

        private static Task WaitForTransfer() => Task.Run(() =>
        {
#if true
            DateTime startTime = DateTime.Now;
            while (_pinController.Read(Settings.TransferReadyPin) != PinValue.High)
            {
                if (Program.CancelSource.IsCancellationRequested || (DateTime.Now - startTime).TotalMilliseconds > Settings.SpiTransferTimeout)
                {
                    throw new OperationCanceledException();
                }
            }
#else
            CancellationToken timeoutToken = new CancellationTokenSource(Settings.SpiTransferTimeout).Token;
            CancellationToken cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(Program.CancelSource.Token, timeoutToken).Token;
            return _transferReadyEvent.WaitAsync(cancellationToken);
#endif
        });

        private static async Task<bool> ExchangeHeader()
        {
            // Write TX header
            _txHeader.NumPackets = _packetId;
            _txHeader.DataLength = (ushort)_txPointer;
            _txHeader.ChecksumData = CRC16.Calculate(_txBuffers[_txBufferIndex].ToArray(), _txPointer);
            MemoryMarshal.Write(_txHeaderBuffer.Span, ref _txHeader);
            _txHeader.ChecksumHeader = CRC16.Calculate(_txHeaderBuffer.ToArray(), Marshal.SizeOf(_txHeader) - Marshal.SizeOf(typeof(ushort)));
            MemoryMarshal.Write(_txHeaderBuffer.Span, ref _txHeader);

            for (int retry = 0; retry < Settings.MaxSpiRetries; retry++)
            {
                // Write invalidated RX header
                _rxHeader.FormatCode = Communication.Consts.InvalidFormatCode;
                MemoryMarshal.Write(_rxHeaderBuffer.Span, ref _rxHeader);

                // Perform SPI header exchange
                await WaitForTransfer();
                _spiDevice.TransferFullDuplex(_txHeaderBuffer.Span, _rxHeaderBuffer.Span);

                // Check for possible response code
                uint responseCode = MemoryMarshal.Read<uint>(_rxHeaderBuffer.Span);
                if (responseCode == Communication.TransferResponse.BadResponse)
                {
                    Console.WriteLine("[warn] Restarting transfer because the Duet received a bad response (header content)");
                    return false;
                }

                // Inspect received header
                _rxHeader = MemoryMarshal.Read<Communication.TransferHeader>(_rxHeaderBuffer.Span);
                if (_rxHeader.FormatCode == 0 || _rxHeader.FormatCode == 0xFF)
                {
                    throw new OperationCanceledException("Board is not available (no data)");
                }

                ushort checksum = CRC16.Calculate(_rxHeaderBuffer.ToArray(), Marshal.SizeOf(_rxHeader) - Marshal.SizeOf(typeof(ushort)));
                if (_rxHeader.ChecksumHeader != checksum)
                {
                    Console.WriteLine($"[warn] Bad header checksum (expected 0x{checksum:x4}, got 0x{_rxHeader.ChecksumHeader:x4})");
                    await ExchangeResponse(Communication.TransferResponse.BadHeaderChecksum);
                    continue;
                }

                if (_rxHeader.FormatCode != Communication.Consts.FormatCode)
                {
                    await ExchangeResponse(Communication.TransferResponse.BadFormat);
                    throw new Exception($"Invalid format code {_rxHeader.FormatCode:x2}");
                }
                if (_rxHeader.ProtocolVersion != Communication.Consts.ProtocolVersion)
                {
                    await ExchangeResponse(Communication.TransferResponse.BadProtocolVersion);
                    throw new Exception($"Invalid protocol version {_rxHeader.ProtocolVersion}");
                }
                if (_rxHeader.DataLength > Communication.Consts.BufferSize)
                {
                    await ExchangeResponse(Communication.TransferResponse.BadDataLength);
                    throw new Exception($"Data length too big ({_rxHeader.DataLength} bytes)");
                }

                // Acknowledge reception
                uint response = await ExchangeResponse(Communication.TransferResponse.Success);
                switch (response)
                {
                    case 0:
                    case 0xFFFFFFFF:
                        throw new OperationCanceledException("Board is not available");

                    case Communication.TransferResponse.Success:
                        return true;

                    case Communication.TransferResponse.BadFormat:
                        throw new Exception("RepRapFirmware refused message format");

                    case Communication.TransferResponse.BadProtocolVersion:
                        throw new Exception("RepRapFirmware refused protocol version");

                    case Communication.TransferResponse.BadDataLength:
                        throw new Exception("RepRapFirmware refused data length");

                    case Communication.TransferResponse.BadHeaderChecksum:
                        Console.WriteLine("[warn] RepRapFirmware got a bad header checksum");
                        continue;

                    case Communication.TransferResponse.BadResponse:
                        Console.WriteLine("[warn] Restarting transfer because RepRapFirmware received a bad response (header response)");
                        return false;

                    default:
                        Console.WriteLine("[warn] Restarting transfer because a bad response was received (header)");
                        await ExchangeResponse(Communication.TransferResponse.BadResponse);
                        return false;
                }
            }

            Console.WriteLine("[warn] Restarting transfer because the number of maximum retries has been exceeded");
            await ExchangeResponse(Communication.TransferResponse.BadResponse);
            return false;
        }

        private static async Task<uint> ExchangeResponse(uint response)
        {
            MemoryMarshal.Write(_txResponseBuffer.Span, ref response);

            await WaitForTransfer();
            _spiDevice.TransferFullDuplex(_txResponseBuffer.Span, _rxResponseBuffer.Span);

            return MemoryMarshal.Read<uint>(_rxResponseBuffer.Span);
        }

        private static async Task<bool> ExchangeData()
        {
            int bytesToTransfer = Math.Max(_rxHeader.DataLength, _txPointer);
            for (int retry = 0; retry < Settings.MaxSpiRetries; retry++)
            {
                await WaitForTransfer();
                _spiDevice.TransferFullDuplex(_txBuffers[_txBufferIndex].Slice(0, bytesToTransfer).Span, _rxBuffer.Slice(0, bytesToTransfer).Span);

                // Check for possible response code
                uint responseCode = MemoryMarshal.Read<uint>(_rxBuffer.Span);
                if (responseCode == Communication.TransferResponse.BadResponse)
                {
                    Console.WriteLine("[warn] Restarting transfer because RepRapFirmware received a bad response (data content)");
                    return false;
                }

                // Inspect received data
                ushort checksum = CRC16.Calculate(_rxBuffer.ToArray(), _rxHeader.DataLength);
                if (_rxHeader.ChecksumData != checksum)
                {
                    Console.WriteLine($"[warn] Bad data checksum (expected 0x{checksum:x4}, got 0x{_rxHeader.ChecksumData:x4})");
                    await ExchangeResponse(Communication.TransferResponse.BadDataChecksum);
                    continue;
                }

                uint response = await ExchangeResponse(Communication.TransferResponse.Success);
                switch (response)
                {
                    case 0:
                    case 0xFFFFFFFF:
                        throw new OperationCanceledException("Board is not available");

                    case Communication.TransferResponse.Success:
                        return true;

                    case Communication.TransferResponse.BadDataChecksum:
                        Console.WriteLine("[warn] RepRapFirmware got a bad data checksum");
                        continue;

                    case Communication.TransferResponse.BadResponse:
                        Console.WriteLine("[warn] Restarting transfer because RepRapFirmware received a bad response (data response)");
                        return false;

                    default:
                        Console.WriteLine("[warn] Restarting transfer because a bad response was received (data)");
                        await ExchangeResponse(Communication.TransferResponse.BadResponse);
                        return false;
                }
            }

            Console.WriteLine("[warn] Restarting transfer because the number of maximum retries has been exceeded");
            await ExchangeResponse(Communication.TransferResponse.BadResponse);
            return false;
        }
#endregion
    }
}
