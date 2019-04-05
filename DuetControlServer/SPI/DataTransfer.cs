using DuetAPI;
using DuetAPI.Commands;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using Nito.AsyncEx;
using System;
using System.Device.Gpio;
using System.Device.Spi;
using System.Device.Spi.Drivers;
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
        private static AsyncAutoResetEvent _transferReadyEvent = new AsyncAutoResetEvent();
        private static bool _hadTimeout;
        private static DateTime _timeConnected;
        private static int _numTransfers;

        // Transfer headers
        private static readonly Memory<byte> _rxHeaderBuffer = new byte[Marshal.SizeOf(typeof(Communication.TransferHeader))];
        private static readonly Memory<byte> _txHeaderBuffer = new byte[Marshal.SizeOf(typeof(Communication.TransferHeader))];
        private static Communication.TransferHeader _rxHeader;
        private static Communication.TransferHeader _txHeader;
        private static ushort _transferNumber = 1, _packetNumber, _lastTransferNumber;

        // Transfer responses
        private static Memory<byte> _rxResponseBuffer = new byte[4];
        private static Memory<byte> _txResponseBuffer = new byte[4];

        // Transfer data. Keep two TX buffers so resend requests can be processed
        private static readonly Memory<byte> _rxBuffer = new byte[Communication.Consts.BufferSize];
        private static readonly Memory<byte>[] _txBuffers = { new byte[Communication.Consts.BufferSize], new byte[Communication.Consts.BufferSize] };
        private static int _txBufferIndex;
        private static int _rxPointer, _txPointer;
        private static Communication.PacketHeader _lastPacket;

        /// <summary>
        /// Set up the SPI device and the controller for the transfer ready pin
        /// </summary>
        public static void Initialize()
        {
            // Initialize TX header. This only needs to happen once
            Serialization.Writer.InitTransferHeader(ref _txHeader);

            // Initialize SPI subsystem
            _spiDevice = new UnixSpiDevice(new SpiConnectionSettings(Settings.SpiBusID, Settings.SpiChipSelectLine));

            // Initialize transfer ready pin
            _pinController = new GpioController(PinNumberingScheme.Logical);
            _pinController.OpenPin(Settings.TransferReadyPin);
            _pinController.SetPinMode(Settings.TransferReadyPin, PinMode.InputPullDown);
            _pinController.RegisterCallbackForPinValueChangedEvent(Settings.TransferReadyPin, PinEventTypes.Rising, OnTransferReady);
            if (_pinController.Read(Settings.TransferReadyPin) == PinValue.High)
            {
                _transferReadyEvent.Set();
            }
        }

        /// <summary>
        /// Get the average number of full transfers per second
        /// </summary>
        /// <returns>Average number of full transfers per second</returns>
        public static decimal GetFullTransfersPerSecond()
        {
            if (_numTransfers == 0)
            {
                return 0;
            }
            return _numTransfers / (decimal)(DateTime.Now - _timeConnected).TotalSeconds;
        }

        /// <summary>
        /// Perform a full data transfer
        /// </summary>
        public static async Task PerformFullTransfer()
        {
            try
            {
                //Console.WriteLine($"- Transfer {_transferNumber} -");
                _lastTransferNumber = _rxHeader.SequenceNumber;

                // This also deals with responses
                await ExchangeHeader();

                // Exchange transfer data or wait a moment before doing another transfer
                if ((_rxHeader.DataLength != 0 || _txPointer != 0) && await ExchangeData())
                {
                    _txBufferIndex = (_txBufferIndex == 0) ? 1 : 0;
                    _rxPointer = _txPointer = 0;
                    _packetNumber = 1;
                }

                // Everything OK
                if (_hadTimeout)
                {
                    using (await Model.Provider.AccessReadWrite())
                    {
                        if (Model.Provider.Get.State.Status == DuetAPI.Machine.State.Status.Off)
                        {
                            Model.Provider.Get.State.Status = DuetAPI.Machine.State.Status.Idle;
                        }
                        Model.Provider.Get.Messages.Add(new Message(MessageType.Success, "Connection to Duet established"));
                        Console.WriteLine("[info] Connection to Duet established");
                    }
                    _hadTimeout = false;
                }

                if (_numTransfers == 0)
                {
                    _timeConnected = DateTime.Now;
                    _numTransfers = 1;
                }
                else
                {
                    _numTransfers++;
                }
            }
            catch (OperationCanceledException)
            {
                if (!Program.CancelSource.IsCancellationRequested)
                {
                    if (!_hadTimeout)
                    {
                        // A timeout occurs when the firmware is being updated
                        bool isUpdating;
                        using (await Model.Provider.AccessReadOnly())
                        {
                            isUpdating = (Model.Provider.Get.State.Status == DuetAPI.Machine.State.Status.Updating);
                        }

                        // If this is the first unexpected timeout event, report it
                        if (!isUpdating)
                        {
                            _hadTimeout = true;
                            using (await Model.Provider.AccessReadWrite())
                            {
                                Model.Provider.Get.State.Status = DuetAPI.Machine.State.Status.Off;
                                Model.Provider.Get.Messages.Add(new Message(MessageType.Warning, "Lost connection to Duet"));
                                Console.WriteLine("[warn] Lost connection to Duet");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Check if the controller has been reset
        /// </summary>
        /// <returns>Whether the controller has been reset</returns>
        public static bool HadReset()
        {
            return _lastTransferNumber > _rxHeader.SequenceNumber;
        }

        #region Read functions
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

            _lastPacket = Serialization.Reader.ReadPacketHeader(_rxBuffer.Slice(_rxPointer).Span);
            _rxPointer += Marshal.SizeOf(_lastPacket);
            return _lastPacket;
        }

        private static Span<byte> _packetData { get => _rxBuffer.Slice(_rxPointer, _lastPacket.Length).Span; }

        /// <summary>
        /// Read the result of a <see cref="Communication.LinuxRequests.Request.GetState"/> request
        /// </summary>
        /// <param name="busyChannels"></param>
        public static void ReadState(out int busyChannels)
        {
            _rxPointer += Serialization.Reader.ReadState(_packetData, out busyChannels);
        }

        /// <summary>
        /// Read the result of a <see cref="Communication.LinuxRequests.Request.GetObjectModel"/> request
        /// </summary>
        /// <param name="module"></param>
        /// <param name="json"></param>
        public static void ReadObjectModel(out byte module, out string json)
        {
            _rxPointer += Serialization.Reader.ReadObjectModel(_packetData, out module, out json);
        }

        /// <summary>
        /// Read the result of a <see cref="Communication.LinuxRequests.Request.Code"/> request
        /// </summary>
        /// <param name="messageType"></param>
        /// <param name="reply"></param>
        public static void ReadCodeReply(out Communication.MessageTypeFlags messageType, out string reply)
        {
            _rxPointer += Serialization.Reader.ReadCodeReply(_packetData, out messageType, out reply);
        }

        /// <summary>
        /// Read the content of a <see cref="MacroRequest"/> packet
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="reportMissing"></param>
        /// <param name="filename"></param>
        public static void ReadMacroRequest(out CodeChannel channel, out bool reportMissing, out string filename)
        {
            _rxPointer += Serialization.Reader.ReadMacroRequest(_packetData, out channel, out reportMissing, out filename);
        }

        /// <summary>
        /// Read the content of an <see cref="AbortFileRequest"/> packet
        /// </summary>
        /// <param name="channel"></param>
        public static void ReadAbortFile(out CodeChannel channel)
        {
            _rxPointer += Serialization.Reader.ReadAbortFile(_packetData, out channel);
        }

        /// <summary>
        /// Read the content of a <see cref="StackEvent"/> packet
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="stackDepth"></param>
        /// <param name="flags"></param>
        /// <param name="feedrate"></param>
        public static void ReadStackEvent(out CodeChannel channel, out byte stackDepth, out StackFlags flags, out float feedrate)
        {
            _rxPointer += Serialization.Reader.ReadStackEvent(_packetData, out channel, out stackDepth, out flags, out feedrate);
        }

        /// <summary>
        /// Read the content of a <see cref="PrintPaused"/> packet
        /// </summary>
        /// <param name="filePosition"></param>
        /// <param name="reason"></param>
        public static void ReadPrintPaused(out uint filePosition, out Communication.PrintPausedReason reason)
        {
            _rxPointer += Serialization.Reader.ReadPrintPaused(_packetData, out filePosition, out reason);
        }

        /// <summary>
        /// Read the result of a <see cref="Communication.LinuxRequests.Request.GetHeightMap"/> request
        /// </summary>
        /// <param name="header"></param>
        /// <param name="zCoordinates"></param>
        public static void ReadHeightMap(out HeightMap header, out float[] zCoordinates)
        {
            _rxPointer += Serialization.Reader.ReadHeightMap(_packetData, out header, out zCoordinates);
        }

        /// <summary>
        /// Read the content of a <see cref="Request.Locked"/> packet
        /// </summary>
        public static void ReadResourceLocked(out CodeChannel channel)
        {
            _rxPointer += Serialization.Reader.ReadResourceLocked(_packetData, out channel);
        }

        private static Span<byte> ReadPacketData(int length)
        {
            Span<byte> span = _rxBuffer.Slice(_rxPointer, length).Span;
            int padding = 4 - (length % 4);
            _rxPointer += length + ((padding == 4) ? 0 : padding);
            return span;
        }

        /// <summary>
        /// Write the last packet + content for diagnostic purposes
        /// </summary>
        public static void DumpMalformedPacket()
        {
            string dump = "Received malformed packet:\n";
            dump += $"=== Packet #{_lastPacket.Id} request {_lastPacket.Request} (length {_lastPacket.Length}) ===\n";
            string data = System.Text.Encoding.UTF8.GetString(_packetData);
            foreach(char c in data)
            {
                dump += ((int)c).ToString("x2");
            }
            dump += "\n";
            foreach(char c in data)
            {
                dump += char.IsLetterOrDigit(c) ? c : '.';
            }
            dump += "\n";
            dump += "====================";
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
                Id = _packetNumber++,
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
        private static void OnTransferReady(object sender, PinValueChangedEventArgs pinValueChangedEventArgs)
        {
            _transferReadyEvent.Set();
        }

        private static Task WaitForTransfer()
        {
            CancellationToken token = CancellationTokenSource.CreateLinkedTokenSource(Program.CancelSource.Token, new CancellationTokenSource(Settings.SpiTimeout).Token).Token;
            return _transferReadyEvent.WaitAsync(token);
        }

        private static async Task ExchangeHeader()
        {
            do
            {
                // Prepare headers
                _rxHeader.FormatCode = Communication.Consts.InvalidFormatCode;
                _txHeader.SequenceNumber = _transferNumber++;
                _txHeader.DataLength = (ushort)_txPointer;
                // TODO Calculate checksums here

                // Perform SPI header transfer
                MemoryMarshal.Write(_rxHeaderBuffer.Span, ref _rxHeader);
                MemoryMarshal.Write(_txHeaderBuffer.Span, ref _txHeader);
                await WaitForTransfer();
                _spiDevice.TransferFullDuplex(_txHeaderBuffer.Span, _rxHeaderBuffer.Span);
                _rxHeader = MemoryMarshal.Read<Communication.TransferHeader>(_rxHeaderBuffer.Span);

                // Deal with reset requests
                while (MemoryMarshal.Read<int>(_rxHeaderBuffer.Span) == Communication.TransferResponse.RequestStateReset)
                {
                    await ExchangeResponse(Communication.TransferResponse.RequestStateReset);

                    await WaitForTransfer();
                    _spiDevice.TransferFullDuplex(_txHeaderBuffer.Span, _rxHeaderBuffer.Span);
                }

                // Inspect received header
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
                    await ExchangeResponse(Communication.TransferResponse.BadChecksum);
                    throw new Exception($"Data length too big ({_rxHeader.DataLength} bytes)");
                }
                // TODO Verify checksums

                // Acknowledge reception
                int response = await ExchangeResponse(Communication.TransferResponse.Success);
                if (response == Communication.TransferResponse.BadFormat)
                {
                    throw new Exception("RepRapFirmware refused message format");
                }
                if (response == Communication.TransferResponse.BadProtocolVersion)
                {
                    throw new Exception("RepRapFirmware refused protocol version");
                }
                if (response == Communication.TransferResponse.Success)
                {
                    break;
                }
            } while (true);
        }

        private static async Task<int> ExchangeResponse(int response)
        {
            MemoryMarshal.Write(_txResponseBuffer.Span, ref response);

            await WaitForTransfer();
            _spiDevice.TransferFullDuplex(_txResponseBuffer.Span, _rxResponseBuffer.Span);

            return MemoryMarshal.Read<int>(_rxResponseBuffer.Span);
        }

        private static async Task<bool> ExchangeData()
        {
            int bytesToTransfer = Math.Max(_rxHeader.DataLength, _txPointer);

            int response;
            do
            {
                await WaitForTransfer();
                _spiDevice.TransferFullDuplex(_txBuffers[_txBufferIndex].Slice(0, bytesToTransfer).Span, _rxBuffer.Slice(0, bytesToTransfer).Span);

                // TODO Verify checksum and send back BadChecksum if it does not match

                response = await ExchangeResponse(Communication.TransferResponse.Success);
                if (response == Communication.TransferResponse.Success)
                {
                    return true;
                }
            } while (response == Communication.TransferResponse.BadChecksum);

            return false;
        }
        #endregion
    }
}
