//
// SAME5x/SAMC21 TCC (timer/counter for control), modelled for what CoreN2G uses it for: PWM outputs in
// normal PWM mode (AnalogOut.cpp, AnalogWriteTcc). The counter itself is not run; what matters is the
// duty and frequency each output produces, which the GPIO model reads for a pin muxed to the output
// and which is computed from PER, CCx, the prescaler and the GCLK frequency.
//
// Output WO[x] is compared against CC[x % channels], as on the part with WEXCTRL.OTMX = 0, which is
// how CoreN2G uses an output that has no compare channel of its own. Buffered writes (PERBUF, CCBUF)
// take effect at once rather than at the next update: the difference is at most one PWM period.
//
// It is clocked by its GCLK channel, at the frequency the GCLK model reports for it.
//
//     tcc1: Timers.SAME5x_Tcc @ sysbus 0x41018000
//         channels: 4
//         gclk: gclk
//         gclkChannel: 25
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Duet;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class SAME5x_Tcc : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize, IDuetPwmSource
    {
        public SAME5x_Tcc(IMachine machine, int channels = 4, SAME5x_Gclk gclk = null, int gclkChannel = -1, ulong frequency = 48000000)
        {
            this.channels = channels;
            this.gclk = gclk;
            this.gclkChannel = gclkChannel;
            defaultFrequency = frequency;
            Reset();
        }

        public void Reset()
        {
            Array.Clear(storage, 0, storage.Length);
            PwmChanged?.Invoke();
        }

        public byte ReadByte(long offset) => (byte)Read(offset, 1);
        public ushort ReadWord(long offset) => (ushort)Read(offset, 2);
        public uint ReadDoubleWord(long offset) => Read(offset, 4);
        public void WriteByte(long offset, byte value) => Write(offset, 1, value);
        public void WriteWord(long offset, ushort value) => Write(offset, 2, value);
        public void WriteDoubleWord(long offset, uint value) => Write(offset, 4, value);

        public long Size => 0x400;

        public event Action PwmChanged;

        public bool TryGetPwm(int output, out double duty, out double frequency)
        {
            duty = 0;
            frequency = 0;
            var ctrla = Stored(CtrlA);
            if((ctrla & CtrlAEnable) == 0 || (Stored(Wave) & WaveGenMask) != WaveGenNpwm)
            {
                return false;
            }
            var top = (double)Stored(Per) + 1;
            var channel = output % channels;
            var cc = Stored(CcBase + 4 * channel);
            duty = Math.Min(1.0, cc / top);
            var input = gclk != null && gclkChannel >= 0 ? gclk.ChannelFrequency(gclkChannel) : 0;
            frequency = (input != 0 ? input : defaultFrequency) / (double)Prescalers[(ctrla >> 8) & 7] / top;
            return true;
        }

        private uint Read(long offset, int width)
        {
            uint value = 0;
            for(var i = 0; i < width; i++)
            {
                var o = offset + i;
                byte b;
                if(o >= SyncBusy && o < SyncBusy + 4)
                {
                    b = 0;                                              // never busy
                }
                else if(o == CtrlBClr || o == CtrlBSet)
                {
                    b = (byte)(storage[CtrlBSet] & ~CtrlBCmdMask);      // commands complete at once
                }
                else if(o == CtrlA && (storage[CtrlA] & CtrlASwRst) != 0)
                {
                    b = (byte)(storage[CtrlA] & ~CtrlASwRst);           // reset completes at once
                }
                else
                {
                    b = o < storage.Length ? storage[o] : (byte)0;
                }
                value |= (uint)b << (8 * i);
            }
            return value;
        }

        private void Write(long offset, int width, uint value)
        {
            for(var i = 0; i < width; i++)
            {
                var o = offset + i;
                var b = (byte)(value >> (8 * i));
                if(o == CtrlBClr)
                {
                    storage[CtrlBSet] &= (byte)~b;
                }
                else if(o == CtrlBSet)
                {
                    storage[CtrlBSet] |= (byte)(b & ~CtrlBCmdMask);
                }
                else if(o >= IntFlag && o < IntFlag + 4)
                {
                    storage[o] &= (byte)~b;                             // write one to clear
                }
                else if(o < storage.Length)
                {
                    storage[o] = b;
                }
            }

            var end = offset + width;
            if(offset <= CtrlA && end > CtrlA && (storage[CtrlA] & CtrlASwRst) != 0)
            {
                Reset();
                return;
            }
            if(Overlaps(offset, end, PerBuf, 4))
            {
                Copy(PerBuf, Per);
            }
            for(var channel = 0; channel < MaxChannels; channel++)
            {
                if(Overlaps(offset, end, CcBufBase + 4 * channel, 4))
                {
                    Copy(CcBufBase + 4 * channel, CcBase + 4 * channel);
                }
            }
            if(Overlaps(offset, end, CtrlA, 4) || Overlaps(offset, end, Wave, 4) || Overlaps(offset, end, Per, 4) ||
               Overlaps(offset, end, CcBase, 4 * MaxChannels) || Overlaps(offset, end, PerBuf, 4) || Overlaps(offset, end, CcBufBase, 4 * MaxChannels))
            {
                PwmChanged?.Invoke();
            }
        }

        private static bool Overlaps(long offset, long end, long register, int length) => offset < register + length && end > register;

        private void Copy(long from, long to) => Array.Copy(storage, from, storage, to, 4);

        private uint Stored(long offset) => BitConverter.ToUInt32(storage, (int)offset);

        private readonly int channels;
        private readonly SAME5x_Gclk gclk;
        private readonly int gclkChannel;
        private readonly ulong defaultFrequency;
        private readonly byte[] storage = new byte[0x80];

        private static readonly uint[] Prescalers = { 1, 2, 4, 8, 16, 64, 256, 1024 };

        private const int MaxChannels = 6;
        private const long CtrlA = 0x00;
        private const long CtrlBClr = 0x04;
        private const long CtrlBSet = 0x05;
        private const long SyncBusy = 0x08;
        private const long IntFlag = 0x2C;
        private const long Wave = 0x3C;
        private const long Per = 0x40;
        private const long CcBase = 0x44;
        private const long PerBuf = 0x6C;
        private const long CcBufBase = 0x70;
        private const uint CtrlASwRst = 0x1;
        private const uint CtrlAEnable = 0x2;
        private const byte CtrlBCmdMask = 0xE0;
        private const uint WaveGenMask = 0x7;
        private const uint WaveGenNpwm = 0x2;
    }
}
