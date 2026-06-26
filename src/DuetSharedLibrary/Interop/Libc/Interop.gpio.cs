using System;
using System.Runtime.InteropServices;

#pragma warning disable IDE1006 // Naming Styles

// GPIO character device uAPI. The legacy v1 structs (gpioevent_request etc.) live in Interop.ioctl.cs;
// this file adds the v2 uAPI (Linux 5.10+) plus the ioctl request codes for both, computed from the
// struct sizes so they cannot drift from the layout the kernel expects
internal partial class Interop
{
    // _IOWR(type, nr, size) = dir(READ|WRITE)<<30 | size<<16 | type<<8 | nr
    private static uint IOWR(uint type, uint nr, uint size) => (3u << 30) | (size << 16) | (type << 8) | nr;

    internal static uint GPIO_GET_LINEEVENT_IOCTL { get; } = IOWR(0xB4, 0x04, (uint)Marshal.SizeOf<gpioevent_request>());
    internal static uint GPIOHANDLE_GET_LINE_VALUES_IOCTL { get; } = IOWR(0xB4, 0x08, (uint)Marshal.SizeOf<gpiohandle_data>());
    internal static uint GPIO_V2_GET_LINE_IOCTL { get; } = IOWR(0xB4, 0x07, (uint)Marshal.SizeOf<gpio_v2_line_request>());
    internal static uint GPIO_V2_LINE_GET_VALUES_IOCTL { get; } = IOWR(0xB4, 0x0E, (uint)Marshal.SizeOf<gpio_v2_line_values>());
}

[Flags]
internal enum GpioV2LineFlags : ulong
{
    GPIO_V2_LINE_FLAG_ACTIVE_LOW = 1UL << 1,
    GPIO_V2_LINE_FLAG_INPUT = 1UL << 2,
    GPIO_V2_LINE_FLAG_OUTPUT = 1UL << 3,
    GPIO_V2_LINE_FLAG_EDGE_RISING = 1UL << 4,
    GPIO_V2_LINE_FLAG_EDGE_FALLING = 1UL << 5
}

internal enum GpioV2LineEvent : uint
{
    GPIO_V2_LINE_EVENT_RISING_EDGE = 1,
    GPIO_V2_LINE_EVENT_FALLING_EDGE = 2
}

// __aligned_u64 fields force 8-byte alignment, so the structs are packed accordingly to match the kernel
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct gpio_v2_line_config
{
    public ulong flags;
    public uint num_attrs;
    public fixed uint padding[5];
    public fixed byte attrs[240];   // GPIO_V2_LINE_NUM_ATTRS_MAX (10) * sizeof(gpio_v2_line_config_attribute) (24); unused while num_attrs is 0
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct gpio_v2_line_request
{
    public fixed uint offsets[64];   // GPIO_V2_LINES_MAX
    public fixed byte consumer[32];  // GPIO_MAX_NAME_SIZE
    public gpio_v2_line_config config;
    public uint num_lines;
    public uint event_buffer_size;
    public fixed uint padding[5];
    public int fd;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct gpio_v2_line_values
{
    public ulong bits;
    public ulong mask;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct gpio_v2_line_event
{
    public ulong timestamp_ns;
    public uint id;
    public uint offset;
    public uint seqno;
    public uint line_seqno;
    public fixed uint padding[6];
}

#pragma warning restore IDE1006 // Naming Styles
