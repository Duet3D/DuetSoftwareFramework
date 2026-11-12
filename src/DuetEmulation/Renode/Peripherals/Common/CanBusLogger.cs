//
// CAN bus logger: writes every frame that crosses a CANHub to a file, one JSON object per line.
//
// The hub raises FrameReceived when a controller hands it a frame, before it is delivered to the
// other machines, so the time recorded is the sender's virtual time at transmission. The test harness
// (DuetEmulation's CanTrace) reads the file and decodes the frames with DSF's generated CAN message types.
//
//     emulation CreateCANHub "canBus"
//     canBus LogFramesToFile "/path/to/can.jsonl"      (a string: an @path must already exist)
//
using System;
using System.IO;
using System.Linq;
using System.Text;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.CAN;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.CAN;
using Antmicro.Renode.Time;
using Antmicro.Renode.Tools.Network;

namespace Antmicro.Renode.Duet
{
    public static class CANBusLoggerExtensions
    {
        public static void LogFramesToFile(this CANHub hub, string path)
        {
            var writer = new StreamWriter(path, append: false, encoding: new UTF8Encoding(false)) { AutoFlush = true };
            hub.FrameReceived += (_, sender, frame) => Write(writer, sender, frame);
        }

        private static void Write(StreamWriter writer, ICAN sender, CANMessageFrame frame)
        {
            var machine = sender.GetMachine();
            var microseconds = machine.ClockSource.CurrentValue.TotalMicroseconds;
            var machineName = EmulationManager.Instance.CurrentEmulation.TryGetMachineName(machine, out var name) ? name : "?";
            var line = new StringBuilder();
            line.Append("{\"t\":").Append(microseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            line.Append(",\"machine\":\"").Append(machineName).Append('"');
            line.Append(",\"id\":").Append(frame.Id);
            line.Append(",\"ext\":").Append(frame.ExtendedFormat ? "true" : "false");
            line.Append(",\"fd\":").Append(frame.FDFormat ? "true" : "false");
            line.Append(",\"brs\":").Append(frame.BitRateSwitch ? "true" : "false");
            line.Append(",\"rtr\":").Append(frame.RemoteFrame ? "true" : "false");
            line.Append(",\"data\":\"").Append(string.Concat(frame.Data.Select(b => b.ToString("x2")))).Append("\"}");
            lock(writer)
            {
                writer.WriteLine(line.ToString());
            }
        }
    }
}
