using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DuetSharedLibrary;

/// <summary>
/// Event-based reader for a single GPIO input line via the Linux GPIO character device.
/// Prefers the v2 chardev uAPI (kernel 5.10+) for per-edge sequence numbers and falls back to the
/// legacy v1 uAPI on older kernels. This talks to /dev/gpiochipN directly and needs no libgpiod
/// </summary>
public sealed class InputGpioPin : IDisposable
{
    private int _chipFd = -1, _reqFd = -1;
    private readonly uint _offset;
    private readonly bool _useV2;
    private uint _lastSeqno;
    private bool _haveSeqno, _disposed;

    /// <summary>
    /// Whether kernel-provided edge sequence numbers are available (true on the v2 uAPI)
    /// </summary>
    public bool SupportsSequenceNumbers => _useV2;

    /// <summary>
    /// Sequence number of the most recently observed edge: the kernel's per-line seqno on v2, or a
    /// running count of observed edges on v1
    /// </summary>
    public uint SequenceNumber => _lastSeqno;

    /// <summary>
    /// Number of edges the kernel had to drop before they could be read (v2 event buffer overruns).
    /// A non-zero value means real TfrRdy transitions went unseen
    /// </summary>
    public int MissedEdges { get; private set; }

    /// <summary>
    /// Pin level as of the last observed edge or read
    /// </summary>
    public bool Value { get; private set; }

    /// <summary>
    /// Delegate invoked on every observed edge
    /// </summary>
    /// <param name="value">New pin level</param>
    /// <param name="sequenceNumber">Sequence number of the edge (see <see cref="SequenceNumber"/>)</param>
    public delegate void PinChangedDelegate(bool value, uint sequenceNumber);

    /// <summary>
    /// Raised on every observed edge while monitoring
    /// </summary>
    public event PinChangedDelegate? PinChanged;

    /// <summary>
    /// Open a GPIO line for both-edge event monitoring
    /// </summary>
    /// <param name="devNode">Path to the GPIO chip device node (e.g. /dev/gpiochip0)</param>
    /// <param name="pin">Line offset to open</param>
    /// <param name="consumerLabel">Consumer label reported to the kernel</param>
    /// <exception cref="IOException">Line could not be initialized</exception>
    public unsafe InputGpioPin(string devNode, int pin, string consumerLabel)
    {
        _offset = (uint)pin;

        // The line must not still be claimed via the legacy sysfs interface
        if (Directory.Exists($"/sys/class/gpio/gpio{pin}"))
        {
            File.WriteAllText("/sys/class/gpio/unexport", pin.ToString());
        }

        _chipFd = Interop.open(devNode, FileOpenFlags.O_RDONLY);
        if (_chipFd < 0)
        {
            throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot open GPIO device '{devNode}'");
        }

        try
        {
            _useV2 = TryRequestLineV2(consumerLabel);
            if (!_useV2)
            {
                RequestLineV1(consumerLabel);
            }
            Value = Read();
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
    private unsafe bool TryRequestLineV2(string consumerLabel)
    {
        gpio_v2_line_request request = new()
        {
            num_lines = 1,
            config = new gpio_v2_line_config
            {
                flags = (ulong)(GpioV2LineFlags.GPIO_V2_LINE_FLAG_INPUT | GpioV2LineFlags.GPIO_V2_LINE_FLAG_EDGE_RISING | GpioV2LineFlags.GPIO_V2_LINE_FLAG_EDGE_FALLING)
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
    private unsafe void RequestLineV1(string consumerLabel)
    {
        gpioevent_request request = new()
        {
            line_offset = _offset,
            handle_flags = (uint)GpioHandleFlags.GPIOHANDLE_REQUEST_INPUT,
            event_flags = (uint)GpioEventFlags.GPIOEVENT_REQUEST_BOTH_EDGES
        };

        byte[] label = Encoding.ASCII.GetBytes(consumerLabel);
        int length = Math.Min(label.Length, 31);
        for (int i = 0; i < length; i++)
        {
            request.consumer_label[i] = label[i];
        }
        request.consumer_label[length] = 0;

        if (Interop.ioctl(_chipFd, Interop.GPIO_GET_LINEEVENT_IOCTL, new IntPtr(&request)) < 0)
        {
            throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot request GPIO line {_offset} via v1 uAPI");
        }
        _reqFd = request.fd;
    }

    /// <summary>
    /// Read the current level of the line directly from the kernel
    /// </summary>
    /// <returns>True if the line is high</returns>
    /// <exception cref="IOException">Value could not be read</exception>
    public unsafe bool Read()
    {
        if (_useV2)
        {
            gpio_v2_line_values values = new() { mask = 1UL };
            if (Interop.ioctl(_reqFd, Interop.GPIO_V2_LINE_GET_VALUES_IOCTL, new IntPtr(&values)) < 0)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot read GPIO line {_offset} (v2)");
            }
            Value = (values.bits & 1UL) != 0;
        }
        else
        {
            gpiohandle_data data = new();
            if (Interop.ioctl(_reqFd, Interop.GPIOHANDLE_GET_LINE_VALUES_IOCTL, new IntPtr(&data)) < 0)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot read GPIO line {_offset} (v1)");
            }
            Value = data.values[0] != 0;
        }
        return Value;
    }

    /// <summary>
    /// Start a background thread that blocks on edge events and raises <see cref="PinChanged"/>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop monitoring</param>
    public void StartMonitoring(CancellationToken cancellationToken = default)
    {
        if (_reqFd < 0)
        {
            throw new IOException("GPIO line is not configured");
        }

        Thread thread = new(() => MonitorLoop(cancellationToken)) { Name = "GpioMonitor", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    private static readonly int _sizeofV2Event = Marshal.SizeOf<gpio_v2_line_event>();
    private static readonly int _sizeofV1Event = Marshal.SizeOf<gpioevent_data>();

    private unsafe void MonitorLoop(CancellationToken cancellationToken)
    {
        if (ProcessHelpers.IsRaspberryPi())
        {
            ProcessHelpers.PinCurrentThreadToCore(3);
        }

        PollFd pollData = new() { Fd = _reqFd, Events = (short)PollFlags.POLLIN };
        gpio_v2_line_event eventV2 = new();
        gpioevent_data eventV1 = new();

        while (!cancellationToken.IsCancellationRequested)
        {
            // Block in the kernel until an edge arrives; the 1s timeout only lets the loop notice cancellation
            pollData.REvents = 0;
            int ready = Interop.poll(new IntPtr(&pollData), 1, 1000);
            if (ready < 0)
            {
                int errno = Marshal.GetLastWin32Error();
                if (errno == 4)     // EINTR is benign
                {
                    continue;
                }
                throw new IOException($"Error {errno}. Failed to poll for GPIO events");
            }
            if (ready == 0)
            {
                continue;
            }

            if (_useV2)
            {
                if (Interop.read(_reqFd, new IntPtr(&eventV2), _sizeofV2Event) != _sizeofV2Event)
                {
                    throw new IOException("GPIO event read returned invalid size (v2)");
                }

                // line_seqno increments by 1 per edge on this line, so a larger gap means dropped edges
                if (_haveSeqno && eventV2.line_seqno > _lastSeqno + 1)
                {
                    MissedEdges += (int)(eventV2.line_seqno - _lastSeqno - 1);
                }
                _lastSeqno = eventV2.line_seqno;
                _haveSeqno = true;

                Value = eventV2.id == (uint)GpioV2LineEvent.GPIO_V2_LINE_EVENT_RISING_EDGE;
            }
            else
            {
                if (Interop.read(_reqFd, new IntPtr(&eventV1), _sizeofV1Event) != _sizeofV1Event)
                {
                    throw new IOException("GPIO event read returned invalid size (v1)");
                }

                // The v1 uAPI carries no sequence number, so count the edges we observe
                _lastSeqno++;
                Value = eventV1.id == (uint)GpioEvent.GPIOEVENT_EVENT_RISING_EDGE;
            }

            PinChanged?.Invoke(Value, _lastSeqno);
        }
    }

    /// <summary>
    /// Finalizer
    /// </summary>
    ~InputGpioPin() => DisposeInternal();

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
