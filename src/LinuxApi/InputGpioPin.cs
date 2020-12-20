using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LinuxApi
{
    /// <summary>
    /// Class for event-based polling of pin level changes
    /// </summary>
    public sealed class InputGpioPin : IDisposable
    {
        private const uint GPIO_GET_LINEEVENT_IOCTL = 0xc030b404;
        private const uint GPIOHANDLE_GET_LINE_VALUES_IOCTL = 0xc040b408;

        private int _deviceFileDescriptor = -1, _reqFd;

        /// <summary>
        /// Open a GPIO device and subscribe to pin level changes
        /// </summary>
        /// <param name="devNode">Path to the GPIO chip device node</param>
        /// <param name="pin">Pin to open</param>
        /// <param name="consumerLabel">Label of the consumer</param>
        /// <exception cref="IOException">Pin could not be initialized</exception>
        public unsafe InputGpioPin(string devNode, int pin, string consumerLabel)
        {
            // The given pin must not be available through sysfs when interfacing the GPIO chip directly...
            if (Directory.Exists($"/sys/class/gpio/gpio{pin}"))
            {
                File.WriteAllText("/sys/class/gpio/unexport", pin.ToString());
            }

            // Open the GPIO chip device
            _deviceFileDescriptor = Interop.open(devNode, FileOpenFlags.O_RDONLY);
            if (_deviceFileDescriptor < 0)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Can not open GPIO device file '{devNode}'.");
            }

            // Set up the pin
            gpioevent_request tr = new gpioevent_request
            {
                line_offset = (uint)pin,
                handle_flags = (uint)GpioHandleFlags.GPIOHANDLE_REQUEST_INPUT,
                event_flags = (uint)GpioEventFlags.GPIOEVENT_REQUEST_BOTH_EDGES,
            };

            byte[] label = Encoding.ASCII.GetBytes(consumerLabel);
            for (int i = 0; i < label.Length; i++)
            {
                tr.consumer_label[i] = label[i];
            }
            tr.consumer_label[label.Length] = 0;

            int result = Interop.ioctl(_deviceFileDescriptor, GPIO_GET_LINEEVENT_IOCTL, new IntPtr(&tr));
            if (result < 0)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot put line into event mode.");
            }
            _reqFd = tr.fd;

            // Read initial value
            gpiohandle_data data = new gpiohandle_data();

            result = Interop.ioctl(_reqFd, GPIOHANDLE_GET_LINE_VALUES_IOCTL, new IntPtr(&data));
            if (result < 0)
            {
                throw new IOException($"Error {Marshal.GetLastWin32Error()}. Cannot read initial pin value.");
            }

            Value = data.values[0] != 0;
        }

        /// <summary>
        /// Finalizer of this instance
        /// </summary>
        ~InputGpioPin() => DisposeInternal();

        /// <summary>
        /// Disposes this instance
        /// </summary>
        public void Dispose()
        {
            DisposeInternal();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Indicates if this instance has been disposed
        /// </summary>
        private bool disposed = false;

        /// <summary>
        /// Dispose this instance internally
        /// </summary>
        private void DisposeInternal()
        {
            if (disposed)
            {
                return;
            }

            if (_deviceFileDescriptor >= 0)
            {
                Interop.close(_deviceFileDescriptor);
                _deviceFileDescriptor = _reqFd = -1;
            }

            disposed = true;
        }

        /// <summary>
        /// Current value of this pin
        /// </summary>
        public bool Value { get; private set; }

        /// <summary>
        /// Delegate of the event type
        /// </summary>
        /// <param name="sender">Object invoking the callback</param>
        /// <param name="pinValue">New pin value</param>
        public delegate void PinChangeDelegate(object sender, bool pinValue);

        /// <summary>
        /// Event to call when a pin change has occurreed
        /// </summary>
        public event PinChangeDelegate PinChanged;

        /// <summary>
        /// Start polling for pin events
        /// </summary>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <returns>Asynchronous task</returns>
        public unsafe Task StartMonitoring(CancellationToken cancellationToken = default)
        {
            if (_reqFd < 0)
            {
                throw new IOException("Pin is not configured");
            }

            return Task.Run(() =>
            {
                gpioevent_data eventData = new gpioevent_data();
                int sizeOfEventData = Marshal.SizeOf(typeof(gpioevent_data));

                do
                {
                    if (Interop.read(_reqFd, new IntPtr(&eventData), sizeOfEventData) == sizeOfEventData)
                    {
                        Value = (eventData.id == (uint)GpioEvent.GPIOEVENT_EVENT_RISING_EDGE);
                        PinChanged?.Invoke(this, Value);
                    }
                    else
                    {
                        throw new IOException("Read returned invalid size");
                    }
                }
                while (!cancellationToken.IsCancellationRequested);
            });
        }
    }
}
