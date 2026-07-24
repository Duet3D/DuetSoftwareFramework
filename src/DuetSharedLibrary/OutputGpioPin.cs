using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DuetSharedLibrary;

/// <summary>
/// Driver for a single GPIO output line via the Linux GPIO character device.
/// Prefers the v2 chardev uAPI (kernel 5.10+) and falls back to the legacy v1 uAPI on older kernels.
/// This talks to /dev/gpiochipN directly and needs no libgpiod
/// </summary>
public sealed class OutputGpioPin : IDisposable
{
    private int _chipFd = -1, _reqFd = -1;
    private readonly uint _offset;
    private readonly bool _useV2;
    private bool _disposed;

    /// <summary>
    /// Level the line was last driven to
    /// </summary>
    public bool Value { get; private set; }

    /// <summary>
    /// Open a GPIO line for output
    /// </summary>
    /// <param name="devNode">Path to the GPIO chip device node (e.g. /dev/gpiochip0)</param>
    /// <param name="pin">Line offset to open</param>
    /// <param name="consumerLabel">Consumer label reported to the kernel</param>
    /// <param name="initialValue">Level to drive the line to when it is requested</param>
    /// <exception cref="IOException">Line could not be initialized</exception>
    public unsafe OutputGpioPin(string devNode, int pin, string consumerLabel, bool initialValue = false)
    {
        _offset = (uint)pin;
        Value = initialValue;

        // The line must not still be claimed via the legacy sysfs interface
        if (Directory.Exists($"/sys/class/gpio/gpio{pin}"))
        {
            File.WriteAllText("/sys/class/gpio/unexport", pin.ToString());
        }

        _chipFd = Interop.open(devNode, FileOpenFlags.O_RDWR);
        if (_chipFd < 0)
        {
            throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot open GPIO device '{devNode}'");
        }

        try
        {
            _useV2 = TryRequestLineV2(consumerLabel, initialValue);
            if (!_useV2)
            {
                RequestLineV1(consumerLabel, initialValue);
            }
            // The kernel does not honour an initial value at request time on all drivers, so drive it explicitly
            Write(initialValue);
        }
        catch
        {
            DisposeInternal();
            throw;
        }
    }

    /// <summary>
    /// Request the line through the v2 uAPI
    /// </summary>
    /// <returns>True on success, false if the kernel does not support the v2 uAPI</returns>
    private unsafe bool TryRequestLineV2(string consumerLabel, bool initialValue)
    {
        gpio_v2_line_request request = new()
        {
            num_lines = 1,
            config = new gpio_v2_line_config
            {
                flags = (ulong)GpioV2LineFlags.GPIO_V2_LINE_FLAG_OUTPUT
            }
        };
        request.offsets[0] = _offset;

        byte[] label = Encoding.ASCII.GetBytes(consumerLabel);
        int length = Math.Min(label.Length, 31);
        for (int i = 0; i < length; i++)
        {
            request.consumer[i] = label[i];
        }
        request.consumer[length] = 0;

        if (Interop.ioctl(_chipFd, Interop.GPIO_V2_GET_LINE_IOCTL, new IntPtr(&request)) < 0)
        {
            int errno = Marshal.GetLastWin32Error();
            // ENOTTY (25) or EINVAL (22) means the kernel predates the v2 uAPI, so fall back to v1
            if (errno is 25 or 22)
            {
                return false;
            }
            throw new IOException($"Error {errno}. Cannot request GPIO line {_offset} via v2 uAPI");
        }
        _reqFd = request.fd;

        return true;
    }

    /// <summary>
    /// Request the line through the legacy v1 uAPI
    /// </summary>
    private unsafe void RequestLineV1(string consumerLabel, bool initialValue)
    {
        gpiohandle_request request = new()
        {
            lines = 1,
            flags = (uint)GpioHandleFlags.GPIOHANDLE_REQUEST_OUTPUT
        };
        request.lineoffsets[0] = _offset;
        request.default_values[0] = (byte)(initialValue ? 1 : 0);

        byte[] label = Encoding.ASCII.GetBytes(consumerLabel);
        int length = Math.Min(label.Length, 31);
        for (int i = 0; i < length; i++)
        {
            request.consumer_label[i] = label[i];
        }
        request.consumer_label[length] = 0;

        if (Interop.ioctl(_chipFd, Interop.GPIO_GET_LINEHANDLE_IOCTL, new IntPtr(&request)) < 0)
        {
            throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot request GPIO line {_offset} via v1 uAPI");
        }
        _reqFd = request.fd;
    }

    /// <summary>
    /// Drive the line to the given level
    /// </summary>
    /// <param name="value">True to drive the line high</param>
    /// <exception cref="IOException">Value could not be written</exception>
    public unsafe void Write(bool value)
    {
        if (_reqFd < 0)
        {
            throw new IOException("GPIO line is not configured");
        }

        if (_useV2)
        {
            gpio_v2_line_values values = new() { mask = 1UL, bits = value ? 1UL : 0UL };
            if (Interop.ioctl(_reqFd, Interop.GPIO_V2_LINE_SET_VALUES_IOCTL, new IntPtr(&values)) < 0)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot write GPIO line {_offset} (v2)");
            }
        }
        else
        {
            gpiohandle_data data = new();
            data.values[0] = (byte)(value ? 1 : 0);
            if (Interop.ioctl(_reqFd, Interop.GPIOHANDLE_SET_LINE_VALUES_IOCTL, new IntPtr(&data)) < 0)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot write GPIO line {_offset} (v1)");
            }
        }
        Value = value;
    }

    /// <summary>
    /// Finalizer
    /// </summary>
    ~OutputGpioPin() => DisposeInternal();

    /// <summary>
    /// Release the GPIO line
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

        if (_reqFd >= 0)
        {
            Interop.close(_reqFd);
            _reqFd = -1;
        }
        if (_chipFd >= 0)
        {
            Interop.close(_chipFd);
            _chipFd = -1;
        }
        _disposed = true;
    }
}
