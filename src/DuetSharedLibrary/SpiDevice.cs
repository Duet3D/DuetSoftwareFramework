using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DuetSharedLibrary;

/// <summary>
/// Driver for full-duplex SPI transfers via the spidev character device
/// </summary>
public sealed class SpiDevice : IDisposable
{
    // SPI_IOC_MESSAGE(1) = _IOW(SPI_IOC_MAGIC (0x6B), 0, sizeof(spi_ioc_transfer) (32))
    private const uint SPI_IOC_MESSAGE_1 = 0x40206b00;

    private int _fd = -1;
    private readonly uint _speed;
    private bool _disposed;

    /// <summary>
    /// Open an SPI device and configure its mode, word size and speed
    /// </summary>
    /// <param name="devNode">Path to the spidev node (e.g. /dev/spidev0.0)</param>
    /// <param name="speed">Transfer speed in Hz</param>
    /// <param name="transferMode">SPI mode (0-3)</param>
    /// <exception cref="IOException">Device could not be initialized</exception>
    public unsafe SpiDevice(string devNode, int speed, int transferMode)
    {
        _speed = (uint)speed;

        _fd = Interop.open(devNode, FileOpenFlags.O_RDWR);
        if (_fd < 0)
        {
            throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot open SPI device '{devNode}'");
        }

        try
        {
            UnixSpiMode mode = transferMode switch
            {
                0 => UnixSpiMode.SPI_MODE_0,
                1 => UnixSpiMode.SPI_MODE_1,
                2 => UnixSpiMode.SPI_MODE_2,
                3 => UnixSpiMode.SPI_MODE_3,
                _ => throw new ArgumentException($"SPI transfer mode '{transferMode}' is invalid, must be between 0 and 3", nameof(transferMode))
            };
            if (Interop.ioctl(_fd, (uint)SpiSettings.SPI_IOC_WR_MODE, new IntPtr(&mode)) < 0)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot set SPI mode {transferMode}");
            }

            byte bitsPerWord = 8;
            if (Interop.ioctl(_fd, (uint)SpiSettings.SPI_IOC_WR_BITS_PER_WORD, new IntPtr(&bitsPerWord)) < 0)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot set SPI word size to 8 bits");
            }

            if (Interop.ioctl(_fd, (uint)SpiSettings.SPI_IOC_WR_MAX_SPEED_HZ, new IntPtr(&speed)) < 0)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot set SPI speed to {speed} Hz");
            }
        }
        catch
        {
            DisposeInternal();
            throw;
        }
    }

    /// <summary>
    /// Perform a full-duplex SPI transfer
    /// </summary>
    /// <param name="writeBuffer">Data to send</param>
    /// <param name="readBuffer">Buffer receiving the same number of bytes</param>
    /// <exception cref="ArgumentException">Buffers differ in length</exception>
    /// <exception cref="IOException">Transfer failed</exception>
    public unsafe void TransferFullDuplex(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer)
    {
        if (writeBuffer.Length != readBuffer.Length)
        {
            throw new ArgumentException($"'{nameof(writeBuffer)}' and '{nameof(readBuffer)}' must have the same length");
        }

        fixed (byte* writePtr = writeBuffer)
        fixed (byte* readPtr = readBuffer)
        {
            spi_ioc_transfer transfer = new()
            {
                tx_buf = (ulong)writePtr,
                rx_buf = (ulong)readPtr,
                len = (uint)writeBuffer.Length,
                speed_hz = _speed,
                bits_per_word = 8
            };
            if (Interop.ioctl(_fd, SPI_IOC_MESSAGE_1, new IntPtr(&transfer)) < 1)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. SPI transfer failed");
            }
        }
    }

    /// <summary>
    /// Finalizer
    /// </summary>
    ~SpiDevice() => DisposeInternal();

    /// <summary>
    /// Close the SPI device
    /// </summary>
    public void Dispose()
    {
        DisposeInternal();
        GC.SuppressFinalize(this);
    }

    private void DisposeInternal()
    {
        if (_disposed)
        {
            return;
        }
        if (_fd >= 0)
        {
            Interop.close(_fd);
            _fd = -1;
        }
        _disposed = true;
    }
}
