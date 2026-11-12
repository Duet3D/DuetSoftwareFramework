//
// SAME5x ADC: what the analogue inputs read.
//
// CoreN2G (SAME5x/AnalogIn.cpp) converts one channel at a time: it writes INPUTCTRL, starts a
// conversion with SWTRIG.START and reads RESULT in the RESRDY interrupt, then starts the next. It
// averages 64 samples in 16-bit mode, so RESULT is a 16-bit reading. The model completes each
// conversion after `conversionTime`, as the hardware would, rather than at once: an instant result
// would have the interrupt start the next conversion before returning, and the CPU would never leave it.
//
// What each input reads is a fraction of full scale (VDDANA, the reference CoreN2G uses), per MUXPOS:
// AIN0-15 are 0-15, PTAT is 0x1C and CTAT is 0x1D. The board's platform file sets them for what is
// connected - a thermistor at room temperature, the supply through its divider - and a test can
// change one with Set, e.g. to heat a thermistor:
//
//     adc1 Set 5 0.5
//
using System;
using System.Collections.Generic;
using System.Globalization;

using Antmicro.Renode.Core;
using Antmicro.Renode.Duet;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Analog
{
    public class SAME5x_Adc : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize, IDuetAnalogInputs
    {
        // internalChannels are the MCU's own inputs (its platform file sets them), channels the board's
        public SAME5x_Adc(IMachine machine, string channels = "", string internalChannels = "", double conversionTime = 0.0004)
        {
            ResultReady = new GPIO();
            Other = new GPIO();
            foreach(var item in (internalChannels + "," + channels).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = item.Split(':');
                var channel = parts[0].Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? int.Parse(parts[0].Trim().Substring(2), NumberStyles.HexNumber)
                    : int.Parse(parts[0].Trim());
                levels[channel] = double.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            var microseconds = (ulong)Math.Max(1, Math.Round(conversionTime * 1e6));
            conversion = new LimitTimer(machine.ClockSource, 1000000, this, "conversion", microseconds, Direction.Ascending, false, WorkMode.OneShot, true);
            conversion.LimitReached += Complete;
            Reset();
        }

        public void Reset()
        {
            Array.Clear(storage, 0, storage.Length);
            conversion.Enabled = false;
            result = 0;
            intEnable = 0;
            intFlags = 0;
            Update();
        }

        public byte ReadByte(long offset) => (byte)Read(offset, 1);
        public ushort ReadWord(long offset) => (ushort)Read(offset, 2);
        public uint ReadDoubleWord(long offset) => Read(offset, 4);
        public void WriteByte(long offset, byte value) => Write(offset, 1, value);
        public void WriteWord(long offset, ushort value) => Write(offset, 2, value);
        public void WriteDoubleWord(long offset, uint value) => Write(offset, 4, value);

        public long Size => 0x100;

        public GPIO ResultReady { get; }        // RESRDY
        public GPIO Other { get; }              // OVERRUN, WINMON

        // What an input reads, as a fraction of full scale
        public void Set(int channel, double fraction) => levels[channel] = Math.Max(0, Math.Min(1, fraction));

        public double Get(int channel) => levels.TryGetValue(channel, out var level) ? level : 0;

        private uint Read(long offset, int width)
        {
            uint value = 0;
            for(var i = 0; i < width; i++)
            {
                value |= (uint)ReadRegisterByte(offset + i) << (8 * i);
            }
            return value;
        }

        private byte ReadRegisterByte(long offset)
        {
            switch(offset)
            {
                case CtrlA:
                    return (byte)(storage[CtrlA] & ~CtrlASwRst);
                case IntEnClr:
                case IntEnSet:
                    return intEnable;
                case IntFlag:
                    return intFlags;
                case Status:
                    return conversion.Enabled ? StatusAdcBusy : (byte)0;
                case Result:
                case Result + 1:
                    // Reading the result acknowledges it, as on the hardware; the interrupt handler relies on that
                    intFlags &= unchecked((byte)~IntResRdy);
                    Update();
                    return (byte)(result >> (int)(8 * (offset - Result)));
            }
            if(offset >= SyncBusy && offset < SyncBusy + 4)
            {
                return 0;
            }
            return offset < storage.Length ? storage[offset] : (byte)0;
        }

        private void Write(long offset, int width, uint value)
        {
            for(var i = 0; i < width; i++)
            {
                var b = (byte)(value >> (8 * i));
                switch(offset + i)
                {
                    case IntEnClr:
                        intEnable &= (byte)~b;
                        break;
                    case IntEnSet:
                        intEnable |= b;
                        break;
                    case IntFlag:
                        intFlags &= (byte)~b;
                        break;
                    case SwTrig:
                        if((b & SwTrigStart) != 0)
                        {
                            Start();
                        }
                        break;
                    default:
                        if(offset + i < storage.Length)
                        {
                            storage[offset + i] = b;
                        }
                        break;
                }
            }
            if((storage[CtrlA] & CtrlASwRst) != 0)
            {
                Reset();
            }
            Update();
        }

        private void Start()
        {
            if((storage[CtrlA] & CtrlAEnable) == 0)
            {
                this.Log(LogLevel.Warning, "Conversion started with the ADC disabled");
                return;
            }
            conversion.Value = 0;
            conversion.Enabled = true;
        }

        private void Complete()
        {
            var channel = storage[InputCtrl] & 0x1F;                       // INPUTCTRL.MUXPOS
            var sample = (ulong)Math.Round(Get(channel) * 4095);           // one 12-bit sample
            var resolution = (storage[CtrlB] >> 3) & 0x3;                  // CTRLB.RESSEL: 0 = 12-bit, 1 = 16-bit (averaging), 2 = 10-bit, 3 = 8-bit
            ulong value;
            switch(resolution)
            {
                case 1:
                {
                    // Accumulate 2^SAMPLENUM samples; beyond 16 the hardware shifts the sum right to fit
                    // 16 bits, and ADJRES divides it further
                    var sampleBits = Math.Min(storage[AvgCtrl] & 0xF, 10);
                    var adjres = (storage[AvgCtrl] >> 4) & 0x7;
                    var automaticShift = Math.Max(0, sampleBits - 4);
                    value = ((sample << sampleBits) >> automaticShift) >> adjres;
                    break;
                }
                case 2:
                    value = sample >> 2;
                    break;
                case 3:
                    value = sample >> 4;
                    break;
                default:
                    value = sample;
                    break;
            }
            if((intFlags & IntResRdy) != 0)
            {
                intFlags |= IntOverrun;
            }
            result = (ushort)Math.Min(value, 0xFFFF);
            intFlags |= IntResRdy;
            Update();
        }

        private void Update()
        {
            ResultReady.Set((intFlags & intEnable & IntResRdy) != 0);
            Other.Set((intFlags & intEnable & (IntOverrun | IntWinMon)) != 0);
        }

        private readonly Dictionary<int, double> levels = new Dictionary<int, double>();
        private readonly LimitTimer conversion;
        private readonly byte[] storage = new byte[0x100];
        private ushort result;
        private byte intEnable;
        private byte intFlags;

        private const long CtrlA = 0x00;
        private const long InputCtrl = 0x04;
        private const long CtrlB = 0x06;
        private const long AvgCtrl = 0x0A;
        private const long SwTrig = 0x14;
        private const long IntEnClr = 0x2C;
        private const long IntEnSet = 0x2D;
        private const long IntFlag = 0x2E;
        private const long Status = 0x2F;
        private const long SyncBusy = 0x30;
        private const long Result = 0x40;
        private const byte CtrlASwRst = 0x01;
        private const byte CtrlAEnable = 0x02;
        private const byte SwTrigStart = 0x02;
        private const byte StatusAdcBusy = 0x01;
        private const byte IntResRdy = 0x01;
        private const byte IntOverrun = 0x02;
        private const byte IntWinMon = 0x04;
    }
}
