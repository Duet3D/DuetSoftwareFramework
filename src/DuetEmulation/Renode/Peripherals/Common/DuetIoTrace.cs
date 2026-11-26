//
// The boards' output pins over time: every change of what an output pin drives (low, high, PWM at a
// duty and frequency, a peripheral function, or nothing because it is an input) on every machine, and
// every change a GPIO model's script makes to an input, written to one file as JSON lines in emulated time. The GPIO models compute each pin's output and record it here; the bench
// (DuetEmulation's IoTrace) reads the file and names the pins by the firmware's pin table.
//
//     emulation LogIoToFile "/path/to/io.jsonl"
//
// IDuetPwmSource is what a timer model offers the GPIO model for a pin muxed to one of its outputs.
//
using System;
using System.Globalization;
using System.IO;
using System.Text;

using Antmicro.Renode.Core;

namespace Antmicro.Renode.Duet
{
    public interface IDuetPwmSource
    {
        // The PWM an output produces now: false if the output is not producing PWM, which leaves the
        // pin to its GPIO level
        bool TryGetPwm(int output, out double duty, out double frequency);

        // Raised when the PWM of any output may have changed
        event Action PwmChanged;
    }

    public enum DuetPinMode
    {
        Input,
        Low,
        High,
        Pwm,
        // Muxed to a peripheral function that is not a timer output the model knows of (a UART, SPI...)
        Peripheral
    }

    public static class DuetIoTrace
    {
        public static void LogIoToFile(this Emulation emulation, string path)
        {
            lock(Sync)
            {
                writer?.Dispose();
                writer = new StreamWriter(path, append: false, encoding: new UTF8Encoding(false)) { AutoFlush = true };
            }
        }

        public static void Record(IMachine machine, string pin, DuetPinMode mode, double duty, double frequency)
        {
            lock(Sync)
            {
                if(writer == null)
                {
                    return;
                }
                var microseconds = machine.ClockSource.CurrentValue.TotalMicroseconds;
                var machineName = EmulationManager.Instance.CurrentEmulation.TryGetMachineName(machine, out var name) ? name : "?";
                var line = new StringBuilder();
                line.Append("{\"t\":").Append(microseconds.ToString("F3", CultureInfo.InvariantCulture));
                line.Append(",\"machine\":\"").Append(machineName).Append('"');
                line.Append(",\"pin\":\"").Append(pin).Append('"');
                line.Append(",\"mode\":\"").Append(mode.ToString().ToLowerInvariant()).Append('"');
                line.Append(",\"duty\":").Append(duty.ToString("R", CultureInfo.InvariantCulture));
                line.Append(",\"freq\":").Append(frequency.ToString("R", CultureInfo.InvariantCulture)).Append('}');
                writer.WriteLine(line.ToString());
            }
        }

        // An input changed by a script in a GPIO model rather than by a monitor command: what drives it now
        public static void RecordInput(IMachine machine, string pin, string value)
        {
            lock(Sync)
            {
                if(writer == null)
                {
                    return;
                }
                var microseconds = machine.ClockSource.CurrentValue.TotalMicroseconds;
                var machineName = EmulationManager.Instance.CurrentEmulation.TryGetMachineName(machine, out var name) ? name : "?";
                var line = new StringBuilder();
                line.Append("{\"t\":").Append(microseconds.ToString("F3", CultureInfo.InvariantCulture));
                line.Append(",\"machine\":\"").Append(machineName).Append('"');
                line.Append(",\"pin\":\"").Append(pin).Append('"');
                line.Append(",\"input\":\"").Append(value).Append("\"}");
                writer.WriteLine(line.ToString());
            }
        }

        private static readonly object Sync = new object();
        private static StreamWriter writer;
    }
}
