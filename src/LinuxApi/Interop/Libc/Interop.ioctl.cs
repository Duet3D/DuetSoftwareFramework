using System;
using System.Runtime.InteropServices;

internal partial class Interop
{
    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int ioctl(int fd, uint request, IntPtr argp);

    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int ioctl(int fd, uint request, ulong argp);
}

[Flags]
internal enum UnixSpiMode : byte
{
    None = 0x00,
    SPI_CPHA = 0x01,
    SPI_CPOL = 0x02,
    SPI_CS_HIGH = 0x04,
    SPI_LSB_FIRST = 0x08,
    SPI_3WIRE = 0x10,
    SPI_LOOP = 0x20,
    SPI_NO_CS = 0x40,
    SPI_READY = 0x80,
    SPI_MODE_0 = None,
    SPI_MODE_1 = SPI_CPHA,
    SPI_MODE_2 = SPI_CPOL,
    SPI_MODE_3 = SPI_CPOL | SPI_CPHA
}

internal enum SpiSettings : uint
{
    /// <summary>Set SPI mode.</summary>
    SPI_IOC_WR_MODE = 0x40016b01,
    /// <summary>Get SPI mode.</summary>
    SPI_IOC_RD_MODE = 0x80016b01,
    /// <summary>Set bits per word.</summary>
    SPI_IOC_WR_BITS_PER_WORD = 0x40016b03,
    /// <summary>Get bits per word.</summary>
    SPI_IOC_RD_BITS_PER_WORD = 0x80016b03,
    /// <summary>Set max speed (Hz).</summary>
    SPI_IOC_WR_MAX_SPEED_HZ = 0x40046b04,
    /// <summary>Get max speed (Hz).</summary>
    SPI_IOC_RD_MAX_SPEED_HZ = 0x80046b04
}

[StructLayout(LayoutKind.Sequential)]
internal struct spi_ioc_transfer
{
    public ulong tx_buf;
    public ulong rx_buf;
    public uint len;
    public uint speed_hz;
    public ushort delay_usecs;
    public byte bits_per_word;
    public byte cs_change;
    public byte tx_nbits;
    public byte rx_nbits;
    public ushort pad;
}

internal enum GpioHandleFlags : uint
{
    GPIOHANDLE_REQUEST_INPUT = 0x01,
    GPIOHANDLE_REQUEST_OUTPUT = 0x02,
    GPIOHANDLE_REQUEST_ACTIVE_LOW = 0x04,
    GPIOHANDLE_REQUEST_OPEN_DRAIN = 0x08,
    GPIOHANDLE_REQUEST_OPEN_SOURCE = 0x10
}

internal enum GpioEventFlags : uint
{
    GPIOEVENT_REQUEST_RISING_EDGE = 0x01,
    GPIOEVENT_REQUEST_FALLING_EDGE = 0x02,
    GPIOEVENT_REQUEST_BOTH_EDGES = 0x03
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct gpioevent_request
{
    public uint line_offset;
    public uint handle_flags;
    public uint event_flags;
    public fixed byte consumer_label[32];
    public int fd;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct gpiohandle_data
{
    public fixed byte values[64];
}

[StructLayout(LayoutKind.Sequential)]
internal struct gpioevent_data
{
    public ulong timestamp;
    public uint id;
};

internal enum GpioEvent : uint
{
    GPIOEVENT_EVENT_RISING_EDGE = 0x01,
    GPIOEVENT_EVENT_FALLING_EDGE = 0x02
}
