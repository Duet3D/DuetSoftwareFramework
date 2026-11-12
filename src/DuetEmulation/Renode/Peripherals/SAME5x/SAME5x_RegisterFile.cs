//
// A peripheral that is only registers: what is written reads back, except for bits forced on or off.
//
// Most SAME5x/SAMC21 system peripherals (GCLK, MCLK, OSCCTRL, OSC32KCTRL, SUPC, CMCC, WDT, PAC...) need
// nothing more from an emulator. The firmware configures them and then waits for a status bit:
// SYNCBUSY to clear, XOSCRDY or a DPLL's LOCK to set. Renode's SVD fallback cannot satisfy that,
// because it ignores writes and returns reset values, so a read-modify-write loses the value written
// and every "wait until ready" spins for ever. Forcing those bits here is the whole model.
//
//     gclk: Miscellaneous.SAME5x_RegisterFile @ sysbus 0x40001C00
//         size: 0x400
//         // SYNCBUSY reads 0 and CTRLA.SWRST self-clears
//         forceClear: "0x04:0xFFFFFFFF,0x00:0x1"
//
// Offsets in forceSet/forceClear/resetValues are of 32-bit words; narrower accesses see the
// corresponding bytes of the word. All three are comma-separated "offset:value" lists.
//
using System;
using System.Collections.Generic;
using System.Globalization;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class SAME5x_RegisterFile : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public SAME5x_RegisterFile(IMachine machine, long size = 0x400, string forceSet = "", string forceClear = "", string resetValues = "")
        {
            Size = size;
            storage = new byte[size];
            this.forceSet = Parse(forceSet);
            this.forceClear = Parse(forceClear);
            this.resetValues = Parse(resetValues);
            Reset();
        }

        public void Reset()
        {
            Array.Clear(storage, 0, storage.Length);
            foreach(var entry in resetValues)
            {
                StoreWord(entry.Key, entry.Value);
            }
        }

        public byte ReadByte(long offset) => (byte)(ReadWord32(offset & ~3L) >> (int)((offset & 3) * 8));

        public ushort ReadWord(long offset) => (ushort)(ReadWord32(offset & ~3L) >> (int)((offset & 2) * 8));

        public uint ReadDoubleWord(long offset) => ReadWord32(offset & ~3L);

        public void WriteByte(long offset, byte value)
        {
            if(InRange(offset, 1))
            {
                storage[offset] = value;
            }
        }

        public void WriteWord(long offset, ushort value)
        {
            if(InRange(offset, 2))
            {
                storage[offset] = (byte)value;
                storage[offset + 1] = (byte)(value >> 8);
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(InRange(offset, 4))
            {
                StoreWord(offset, value);
            }
        }

        public long Size { get; }

        // Change a forced bit at run time, e.g. to make a status flag report a fault.
        public void ForceSet(long offset, uint mask) => forceSet[offset] = mask;

        public void ForceClear(long offset, uint mask) => forceClear[offset] = mask;

        private uint ReadWord32(long offset)
        {
            if(!InRange(offset, 4))
            {
                return 0;
            }
            var value = (uint)(storage[offset] | storage[offset + 1] << 8 | storage[offset + 2] << 16 | storage[offset + 3] << 24);
            if(forceSet.TryGetValue(offset, out var set))
            {
                value |= set;
            }
            if(forceClear.TryGetValue(offset, out var clear))
            {
                value &= ~clear;
            }
            return value;
        }

        private void StoreWord(long offset, uint value)
        {
            storage[offset] = (byte)value;
            storage[offset + 1] = (byte)(value >> 8);
            storage[offset + 2] = (byte)(value >> 16);
            storage[offset + 3] = (byte)(value >> 24);
        }

        private bool InRange(long offset, int width)
        {
            if(offset < 0 || offset + width > Size)
            {
                this.Log(LogLevel.Warning, "Access outside the register file at 0x{0:X}", offset);
                return false;
            }
            return true;
        }

        private static Dictionary<long, uint> Parse(string list)
        {
            var result = new Dictionary<long, uint>();
            foreach(var item in list.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = item.Split(':');
                if(parts.Length != 2)
                {
                    throw new ArgumentException($"\"{item}\" is not offset:value");
                }
                var offset = ParseNumber(parts[0]);
                if((offset & 3) != 0)
                {
                    throw new ArgumentException($"Offset 0x{offset:X} is not word aligned");
                }
                result[offset] = (uint)ParseNumber(parts[1]);
            }
            return result;
        }

        private static long ParseNumber(string text)
        {
            text = text.Trim();
            return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.Parse(text.Substring(2), NumberStyles.HexNumber)
                : long.Parse(text);
        }

        private readonly byte[] storage;
        private readonly Dictionary<long, uint> forceSet;
        private readonly Dictionary<long, uint> forceClear;
        private readonly Dictionary<long, uint> resetValues;
    }
}
