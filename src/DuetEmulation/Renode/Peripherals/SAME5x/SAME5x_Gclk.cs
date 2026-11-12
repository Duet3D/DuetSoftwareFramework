//
// SAME5x GCLK (generic clock controller): the generators the firmware sets up and the peripheral
// channels it routes them to (GENCTRL, PCHCTRL), so that a timer knows the frequency it is clocked at.
// The generators' sources are oscillators and PLLs whose output the firmware sets up elsewhere
// (OSCCTRL); their frequencies are given by the platform. CTRLA.SWRST completes at once and SYNCBUSY
// is never busy.
//
//     gclk: Miscellaneous.SAME5x_Gclk @ sysbus 0x40001C00
//         sources: "6:48000000,7:120000000,8:96000000"
//
using System;
using System.Collections.Generic;
using System.Globalization;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class SAME5x_Gclk : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public SAME5x_Gclk(IMachine machine, string sources = "")
        {
            foreach(var entry in sources.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split(':');
                if(parts.Length != 2 || !int.TryParse(parts[0], out var source) ||
                   !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var frequency))
                {
                    throw new ConstructionException($"\"{entry}\" is not a source number and frequency such as 7:120000000");
                }
                sourceFrequencies[source] = frequency;
            }
            Reset();
        }

        public void Reset()
        {
            Array.Clear(storage, 0, storage.Length);
        }

        public byte ReadByte(long offset) => (byte)Read(offset, 1);
        public ushort ReadWord(long offset) => (ushort)Read(offset, 2);
        public uint ReadDoubleWord(long offset) => Read(offset, 4);
        public void WriteByte(long offset, byte value) => Write(offset, 1, value);
        public void WriteWord(long offset, ushort value) => Write(offset, 2, value);
        public void WriteDoubleWord(long offset, uint value) => Write(offset, 4, value);

        public long Size => 0x400;

        // The frequency a peripheral channel (its GCLK ID, such as TC0_GCLK_ID) is clocked at, or 0 if it is off
        public ulong ChannelFrequency(int channel)
        {
            var pchctrl = Register(PchCtrl + 4 * channel);
            return (pchctrl & PchCtrlChEn) == 0 ? 0 : GeneratorFrequency((int)(pchctrl & PchCtrlGenMask));
        }

        // The frequency of a generator, or 0 if it is off or its source is not known
        public ulong GeneratorFrequency(int generator)
        {
            var genctrl = Register(GenCtrl + 4 * generator);
            if((genctrl & GenCtrlGenEn) == 0 || !sourceFrequencies.TryGetValue((int)(genctrl & GenCtrlSrcMask), out var source))
            {
                return 0;
            }
            var division = genctrl >> GenCtrlDivShift;
            if((genctrl & GenCtrlDivSel) != 0)
            {
                return source >> (int)(division + 1);
            }
            return division <= 1 ? source : source / division;
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
                    b = 0;                                          // never busy
                }
                else if(o == CtrlA)
                {
                    b = (byte)(storage[CtrlA] & ~CtrlASwRst);       // reset completes at once
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
                if(o < storage.Length)
                {
                    storage[o] = (byte)(value >> (8 * i));
                }
            }
            if((storage[CtrlA] & CtrlASwRst) != 0)
            {
                Reset();
            }
        }

        private uint Register(long offset) => BitConverter.ToUInt32(storage, (int)offset);

        private readonly Dictionary<int, ulong> sourceFrequencies = new Dictionary<int, ulong>();
        private readonly byte[] storage = new byte[0x140];

        private const long CtrlA = 0x00;
        private const long SyncBusy = 0x04;
        private const long GenCtrl = 0x20;
        private const long PchCtrl = 0x80;
        private const byte CtrlASwRst = 0x1;
        private const uint GenCtrlSrcMask = 0xF;
        private const uint GenCtrlGenEn = 1u << 8;
        private const uint GenCtrlDivSel = 1u << 12;
        private const int GenCtrlDivShift = 16;
        private const uint PchCtrlGenMask = 0xF;
        private const uint PchCtrlChEn = 1u << 6;
    }
}
