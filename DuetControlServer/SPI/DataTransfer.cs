using System;
using System.Collections.Generic;
using System.Device.Gpio;
using System.Device.Spi;
using System.Device.Spi.Drivers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DuetControlServer.SPI
{
    /// <summary>
    /// Helper class for SPI data transfers
    /// </summary>
    public static class DataTransfer
    {
        private static SpiDevice spiDevice;
        private static GpioController pinController;

        private static readonly Memory<byte> rxBuffer = new byte[Communication.Consts.BufferSize];
        private static readonly Memory<byte> txBuffer = new byte[Communication.Consts.BufferSize];
        private static int bytesToWrite;
        private static ushort sequenceNumber;

        /// <summary>
        /// Set up the SPI device and the controller for the transfer ready pin
        /// </summary>
        public static void Initialize()
        {
            // Initialize SPI
            spiDevice = new UnixSpiDevice(new SpiConnectionSettings(Settings.SpiBusID, Settings.SpiChipSelectLine));

            // Initialize transfer ready pin
            pinController = new GpioController(PinNumberingScheme.Logical);
            pinController.OpenPin(Settings.TransferReadyPin);
            pinController.SetPinMode(Settings.TransferReadyPin, PinMode.InputPullDown);
        }

        /// <summary>
        /// Perform a full data transfer
        /// </summary>
        public static void PerformFullTransfer()
        {
            // Exchange transfer headers
            Communication.TransferHeader header = ExchangeHeader();

            // Exchange transfer data
            if ((header.DataLength != 0 || bytesToWrite != 0) && ExchangeData(header.DataLength))
            {
                // TODO parse packets
            }
        }

        private static void WaitForTransfer()
        {
            if (pinController.Read(Settings.TransferReadyPin) != PinValue.High)
            {
                pinController.WaitForEvent(Settings.TransferReadyPin, PinEventTypes.Rising, Program.CancelSource.Token);
            }
        }

        private static Communication.TransferHeader ExchangeHeader()
        {
            Span<byte> txSpan = new byte[Marshal.SizeOf(typeof(Communication.TransferHeader))];
            Span<byte> rxSpan = new byte[Marshal.SizeOf(typeof(Communication.TransferHeader))];

            do
            {
                // Perform SPI header transfer
                WaitForTransfer();
                Serialization.Writer.WriteTransferHeader(txSpan, 0, sequenceNumber++, 0, 0);
                spiDevice.TransferFullDuplex(txSpan, rxSpan);
                Communication.TransferHeader rxHeader = Serialization.Reader.ReadTransferHeader(rxSpan);

                // Deal with reset requests
                if (MemoryMarshal.Read<int>(rxSpan) == Communication.TransferResponse.RequestStateReset)
                {
                    ExchangeResponse(Communication.TransferResponse.RequestStateReset);

                    WaitForTransfer();
                    spiDevice.TransferFullDuplex(txSpan, rxSpan);
                }

                // Inspect received header
                if (rxHeader.FormatCode != Communication.Consts.FormatCode)
                {
                    ExchangeResponse(Communication.TransferResponse.BadFormat);
                    throw new Exception($"Invalid format code {rxSpan[0]:X2}");
                }
                if (rxHeader.ProtocolVersion != Communication.Consts.ProtocolVersion)
                {
                    ExchangeResponse(Communication.TransferResponse.BadProtocolVersion);
                    throw new Exception($"Invalid protocol version {rxHeader.ProtocolVersion}");
                }
                if (rxHeader.DataLength > Communication.Consts.BufferSize)
                {
                    ExchangeResponse(Communication.TransferResponse.BadChecksum);
                    throw new Exception($"Data length too big ({rxHeader.DataLength} bytes)");
                }
                // TODO check checksum

                // Acknowledge reception
                int response = ExchangeResponse(Communication.TransferResponse.Success);
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
                    return rxHeader;
                }
            } while (true);
        }

        private static int ExchangeResponse(int response)
        {
            Span<byte> rxResponse = new byte[4];
            Span<byte> txResponse = new byte[4];
            MemoryMarshal.Write(txResponse, ref response);

            WaitForTransfer();
            spiDevice.TransferFullDuplex(txResponse, rxResponse);

            return MemoryMarshal.Read<int>(rxResponse);
        }

        private static bool ExchangeData(int bytesToRead)
        {
            Span<byte> rxSpan = rxBuffer.Slice(0, bytesToRead).Span;
            ReadOnlySpan<byte> txSpan = txBuffer.Slice(0, bytesToWrite).Span;

            int response;
            do
            {
                WaitForTransfer();
                spiDevice.TransferFullDuplex(txSpan, rxSpan);

                // TODO check checksum and send BadChecksum if it does not match

                response = ExchangeResponse(Communication.TransferResponse.Success);
                if (response == Communication.TransferResponse.Success)
                {
                    return true;
                }
            } while (response == Communication.TransferResponse.BadChecksum);

            return false;
        }

    }
}
