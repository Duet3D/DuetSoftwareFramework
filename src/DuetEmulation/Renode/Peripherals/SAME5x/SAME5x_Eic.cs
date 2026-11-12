//
// SAME5x EIC (external interrupt controller): the 16 EXTINT lines that interrupt on a pin's level or
// edges (CoreN2G Interrupts.cpp, attachInterrupt). The PORT model reports the level of each pin muxed
// to the EIC (function A) as it changes; this raises the line's flag according to its CONFIG sense and
// interrupts through the line's own NVIC vector (EIC_0_IRQn + line).
//
// Filtering and debouncing are not modelled: an input that changes changes cleanly, so they would only
// add latency. NMI is not modelled.
//
//     eic: Miscellaneous.SAME5x_Eic @ sysbus 0x40002800
//         [0-15] -> nvic@[12-27]
//
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class SAME5x_Eic : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public SAME5x_Eic(IMachine machine)
        {
            var lines = new Dictionary<int, IGPIO>();
            for(var i = 0; i < NumLines; i++)
            {
                lines[i] = new GPIO();
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(lines);
            Reset();
        }

        public void Reset()
        {
            Array.Clear(storage, 0, storage.Length);
            UpdateInterrupts();
        }

        public byte ReadByte(long offset) => (byte)Read(offset, 1);
        public ushort ReadWord(long offset) => (ushort)Read(offset, 2);
        public uint ReadDoubleWord(long offset) => Read(offset, 4);
        public void WriteByte(long offset, byte value) => Write(offset, 1, value);
        public void WriteWord(long offset, ushort value) => Write(offset, 2, value);
        public void WriteDoubleWord(long offset, uint value) => Write(offset, 4, value);

        public long Size => 0x400;

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // The pin of EXTINT line `line` is now at `level`. Only a pin muxed to the EIC (`connected`)
        // interrupts, but the level is tracked either way so that muxing a pin is not an edge.
        public void OnPinLevel(int line, bool level, bool connected)
        {
            if(line < 0 || line >= NumLines)
            {
                return;
            }
            var bit = 1u << line;
            var was = (pinState & bit) != 0;
            pinState = level ? pinState | bit : pinState & ~bit;
            if(connected && Enabled && Triggers(line, was, level))
            {
                SetFlag(bit);
            }
        }

        private bool Triggers(int line, bool was, bool level)
        {
            switch(Sense(line))
            {
                case SenseRise: return !was && level;
                case SenseFall: return was && !level;
                case SenseBoth: return was != level;
                case SenseHigh: return level;
                case SenseLow: return !level;
                default: return false;
            }
        }

        private uint Sense(int line) => (Register(Config0 + 4 * (line / 8)) >> (4 * (line % 8))) & SenseMask;

        private bool Enabled => (storage[CtrlA] & CtrlAEnable) != 0;

        private void SetFlag(uint bits)
        {
            SetRegister(IntFlag, Register(IntFlag) | bits);
            UpdateInterrupts();
        }

        // A level-sensitive line keeps its flag set for as long as the pin is at its level
        private void ReassertLevelFlags()
        {
            if(!Enabled)
            {
                return;
            }
            uint flags = 0;
            for(var line = 0; line < NumLines; line++)
            {
                var level = (pinState & (1u << line)) != 0;
                var sense = Sense(line);
                if((sense == SenseHigh && level) || (sense == SenseLow && !level))
                {
                    flags |= 1u << line;
                }
            }
            if(flags != 0)
            {
                SetFlag(flags);
            }
        }

        private void UpdateInterrupts()
        {
            var pending = Register(IntFlag) & Register(IntEnSet);
            for(var line = 0; line < NumLines; line++)
            {
                Connections[line].Set((pending & (1u << line)) != 0);
            }
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
                else if(o >= PinState && o < PinState + 4)
                {
                    b = (byte)(pinState >> (int)(8 * (o - PinState)));
                }
                else if(o >= IntEnClr && o < IntEnClr + 4)
                {
                    b = storage[o - IntEnClr + IntEnSet];
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
                if(o >= IntEnClr && o < IntEnClr + 4)
                {
                    storage[o - IntEnClr + IntEnSet] &= (byte)~b;
                }
                else if(o >= IntEnSet && o < IntEnSet + 4)
                {
                    storage[o] |= b;
                }
                else if(o >= IntFlag && o < IntFlag + 4)
                {
                    storage[o] &= (byte)~b;                         // write one to clear
                }
                else if(o < storage.Length)
                {
                    storage[o] = b;
                }
            }
            if((storage[CtrlA] & CtrlASwRst) != 0)
            {
                Reset();
                return;
            }
            ReassertLevelFlags();
            UpdateInterrupts();
        }

        private uint Register(long offset) => BitConverter.ToUInt32(storage, (int)offset);

        private void SetRegister(long offset, uint value)
        {
            for(var i = 0; i < 4; i++)
            {
                storage[offset + i] = (byte)(value >> (8 * i));
            }
        }

        private readonly byte[] storage = new byte[0x40];
        private uint pinState;

        private const int NumLines = 16;
        private const long CtrlA = 0x00;
        private const long SyncBusy = 0x04;
        private const long IntEnClr = 0x0C;
        private const long IntEnSet = 0x10;
        private const long IntFlag = 0x14;
        private const long Config0 = 0x1C;
        private const long PinState = 0x38;
        private const byte CtrlASwRst = 0x1;
        private const byte CtrlAEnable = 0x2;
        private const uint SenseMask = 0x7;
        private const uint SenseRise = 1;
        private const uint SenseFall = 2;
        private const uint SenseBoth = 3;
        private const uint SenseHigh = 4;
        private const uint SenseLow = 5;
    }
}
