using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LinuxApi
{
    /// <summary>
    /// Driver class for SPI transfers. Most of this is copied from the System.Device.Gpio library
    /// </summary>
    public sealed class SpiDevice : IDisposable
    {
        private const uint SPI_IOC_MESSAGE_1 = 0x40206b00;
        private int _deviceFileDescriptor = -1;
        private readonly uint _speed;

        /// <summary>
        /// Initialize an SPI device
        /// </summary>
        /// <param name="devNode">Path to the /dev node</param>
        /// <param name="speed">Transfer speed in Hz</param>
        public unsafe SpiDevice(string devNode, int speed, int transferMode)
        {
            _speed = (uint)speed;

            _deviceFileDescriptor = Interop.open(devNode, FileOpenFlags.O_RDWR);
            if (_deviceFileDescriptor < 0)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Can not open SPI device file '{devNode}'.");
            }

            UnixSpiMode mode;
            switch (transferMode)
            {
                case 0:
                    mode = UnixSpiMode.SPI_MODE_0;
                    break;
                case 1:
                    mode = UnixSpiMode.SPI_MODE_1;
                    break;
                case 2:
                    mode = UnixSpiMode.SPI_MODE_2;
                    break;
                case 3:
                    mode = UnixSpiMode.SPI_MODE_3;
                    break;
                default:
                    throw new ArgumentException($"Transfer mode '{transferMode}' not regignized. Must be between 0 and 3.");
            }
            IntPtr nativePtr = new IntPtr(&mode);

            int result = Interop.ioctl(_deviceFileDescriptor, (uint)SpiSettings.SPI_IOC_WR_MODE, nativePtr);
            if (result == -1)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Can not set SPI mode to {mode}.");
            }

            byte dataLengthInBits = 8;
            nativePtr = new IntPtr(&dataLengthInBits);

            result = Interop.ioctl(_deviceFileDescriptor, (uint)SpiSettings.SPI_IOC_WR_BITS_PER_WORD, nativePtr);
            if (result == -1)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Can not set SPI data bit length to 8.");
            }

            nativePtr = new IntPtr(&speed);

            result = Interop.ioctl(_deviceFileDescriptor, (uint)SpiSettings.SPI_IOC_WR_MAX_SPEED_HZ, nativePtr);
            if (result == -1)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Can not set SPI clock frequency to {speed}.");
            }
        }

        /// <summary>
        /// Finalizer of this class
        /// </summary>
        ~SpiDevice() => Dispose(false);

        /// <summary>
        /// Disposes this instance
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Indicates if this instance has been disposed
        /// </summary>
        private bool disposed = false;

        /// <summary>
        /// Dispose this instance internally
        /// </summary>
        /// <param name="disposing">Release managed resourcess</param>
        private void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (_deviceFileDescriptor >= 0)
            {
                Interop.close(_deviceFileDescriptor);
                _deviceFileDescriptor = -1;
            }

            disposed = true;
        }

        /// <summary>
        /// Writes and reads data from the SPI device.
        /// </summary>
        /// <param name="writeBuffer">The buffer that contains the data to be written to the SPI device.</param>
        /// <param name="readBuffer">The buffer to read the data from the SPI device.</param>
        public unsafe void TransferFullDuplex(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer)
        {
            if (writeBuffer.Length != readBuffer.Length)
            {
                throw new ArgumentException($"Parameters '{nameof(writeBuffer)}' and '{nameof(readBuffer)}' must have the same length.");
            }

            fixed (byte* writeBufferPtr = writeBuffer)
            {
                fixed (byte* readBufferPtr = readBuffer)
                {
                    Transfer(writeBufferPtr, readBufferPtr, writeBuffer.Length);
                }
            }
        }

        private unsafe void Transfer(byte* writeBufferPtr, byte* readBufferPtr, int buffersLength)
        {
            var tr = new spi_ioc_transfer()
            {
                tx_buf = (ulong)writeBufferPtr,
                rx_buf = (ulong)readBufferPtr,
                len = (uint)buffersLength,
                speed_hz = _speed,
                bits_per_word = 8,
                delay_usecs = 0
            };

            int result = Interop.ioctl(_deviceFileDescriptor, SPI_IOC_MESSAGE_1, new IntPtr(&tr));
            if (result < 1)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()} performing SPI data transfer.");
            }
        }
    }
}
