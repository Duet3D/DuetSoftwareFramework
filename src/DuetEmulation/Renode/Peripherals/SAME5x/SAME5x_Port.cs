//
// SAME5x/SAMC21 PORT: the GPIO groups (PA, PB, ...), each 0x80 bytes.
//
// Inputs read what drives them: an output pin reads back its OUT bit; an input pin reads the level
// something outside the chip holds it at (Drive, or the platform's drivenLow/drivenHigh lists), and
// otherwise its pull resistor (PINCFG.PULLEN, pulling towards its OUT bit), and otherwise low. That is
// how a board description sets switches and resistors that the firmware reads: the expansion boards'
// address switches, the board revision straps.
//
// Every output pin counts its rising edges, so a test can count the step pulses a driver received
// without tracing every edge.
//
//     port: GPIOPort.SAME5x_Port @ sysbus 0x41008000
//         groups: 4
//         drivenLow: "PC18"
//
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    public class SAME5x_Port : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput, IGPIOReceiver
    {
        public SAME5x_Port(IMachine machine, int groups = 4, string drivenLow = "", string drivenHigh = "")
        {
            this.machine = machine;
            this.groups = new Group[groups];
            for(var i = 0; i < groups; i++)
            {
                this.groups[i] = new Group();
            }
            var connections = new Dictionary<int, IGPIO>();
            for(var i = 0; i < groups * PinsPerGroup; i++)
            {
                connections[i] = new GPIO();
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(connections);
            risingEdges = new ulong[groups * PinsPerGroup];
            // What drives the pins is outside the chip, so a reset of the chip does not change it
            foreach(var pin in ParsePins(drivenLow))
            {
                Drive(pin, false);
            }
            foreach(var pin in ParsePins(drivenHigh))
            {
                Drive(pin, true);
            }
            Reset();
        }

        public void Reset()
        {
            foreach(var group in groups)
            {
                group.Reset();
            }
            Array.Clear(risingEdges, 0, risingEdges.Length);
        }

        public uint ReadDoubleWord(long offset)
        {
            var g = GroupAt(offset);
            if(g == null)
            {
                return 0;
            }
            var reg = offset % GroupSize;
            switch(reg)
            {
                case Dir:
                case DirClr:
                case DirSet:
                case DirTgl:
                    return g.Dir;
                case Out:
                case OutClr:
                case OutSet:
                case OutTgl:
                    return g.Out;
                case In:
                    return InputLevels(g);
                case Ctrl:
                    return g.Ctrl;
                case EvCtrl:
                    return g.EvCtrl;
                default:
                    if(reg < PmuxBase)
                    {
                        return 0;           // WRCONFIG is write-only
                    }
                    var aligned = offset & ~3L;
                    return (uint)(ReadByte(aligned) | ReadByte(aligned + 1) << 8 | ReadByte(aligned + 2) << 16 | ReadByte(aligned + 3) << 24);
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(GroupAt(offset) == null)
            {
                return;
            }
            var index = (int)(offset / GroupSize);
            var g = groups[index];
            switch(offset % GroupSize)
            {
                case Dir:
                    g.Dir = value;
                    break;
                case DirClr:
                    g.Dir &= ~value;
                    break;
                case DirSet:
                    g.Dir |= value;
                    break;
                case DirTgl:
                    g.Dir ^= value;
                    break;
                case Out:
                    SetOut(index, value);
                    break;
                case OutClr:
                    SetOut(index, g.Out & ~value);
                    break;
                case OutSet:
                    SetOut(index, g.Out | value);
                    break;
                case OutTgl:
                    SetOut(index, g.Out ^ value);
                    break;
                case Ctrl:
                    g.Ctrl = value;
                    break;
                case WrConfig:
                    WriteConfiguration(g, value);
                    break;
                case EvCtrl:
                    g.EvCtrl = value;
                    break;
                default:
                    for(var i = 0; i < 4; i++)
                    {
                        WriteByte((offset & ~3L) + i, (byte)(value >> (8 * i)));
                    }
                    break;
            }
        }

        public ushort ReadWord(long offset)
        {
            var word = ReadDoubleWord(offset & ~3L);
            return (ushort)(word >> (int)((offset & 2) * 8));
        }

        public void WriteWord(long offset, ushort value)
        {
            var reg = (offset % GroupSize) & ~3L;
            if(reg >= PmuxBase)
            {
                WriteByte(offset, (byte)value);
                WriteByte(offset + 1, (byte)(value >> 8));
                return;
            }
            // A halfword write to a set/clear register only affects its half
            var shift = (int)((offset & 2) * 8);
            WriteDoubleWord(offset & ~3L, IsSetClearToggle(reg) ? (uint)value << shift : MergeHalf(offset, value));
        }

        public byte ReadByte(long offset)
        {
            if(GroupAt(offset) == null)
            {
                return 0;
            }
            var g = groups[offset / GroupSize];
            var reg = offset % GroupSize;
            if(reg >= PinCfgBase && reg < PinCfgBase + PinsPerGroup)
            {
                return g.PinCfg[reg - PinCfgBase];
            }
            if(reg >= PmuxBase && reg < PmuxBase + PinsPerGroup / 2)
            {
                return g.Pmux[reg - PmuxBase];
            }
            return (byte)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 3) * 8));
        }

        public void WriteByte(long offset, byte value)
        {
            if(GroupAt(offset) == null)
            {
                return;
            }
            var g = groups[offset / GroupSize];
            var reg = offset % GroupSize;
            if(reg >= PinCfgBase && reg < PinCfgBase + PinsPerGroup)
            {
                g.PinCfg[reg - PinCfgBase] = (byte)(value & PinCfgMask);
                return;
            }
            if(reg >= PmuxBase && reg < PmuxBase + PinsPerGroup / 2)
            {
                g.Pmux[reg - PmuxBase] = value;
                return;
            }
            var shift = (int)((offset & 3) * 8);
            var aligned = offset & ~3L;
            var word = IsSetClearToggle(reg & ~3L) ? (uint)value << shift
                     : (ReadDoubleWord(aligned) & ~(0xFFu << shift)) | ((uint)value << shift);
            WriteDoubleWord(aligned, word);
        }

        public void OnGPIO(int number, bool value)
        {
            Drive(number, value);
        }

        public long Size => groups.Length * GroupSize;

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // Pin numbers are group * 32 + pin, so PA00 is 0, PB00 is 32 and PC18 is 82; or use a name, "PC18".

        public void Drive(int pin, bool level)
        {
            var g = groups[pin / PinsPerGroup];
            var bit = 1u << (pin % PinsPerGroup);
            g.Driven |= bit;
            g.DrivenLevels = level ? g.DrivenLevels | bit : g.DrivenLevels & ~bit;
        }

        public void Drive(string pin, bool level) => Drive(PinNumber(pin), level);

        public void Release(int pin)
        {
            groups[pin / PinsPerGroup].Driven &= ~(1u << (pin % PinsPerGroup));
        }

        public void Release(string pin) => Release(PinNumber(pin));

        // What the pin is at: what the firmware reads if it is an input, what it drives if an output
        public bool Level(int pin) => ((InputLevels(groups[pin / PinsPerGroup]) >> (pin % PinsPerGroup)) & 1) != 0;

        public bool Level(string pin) => Level(PinNumber(pin));

        public ulong RisingEdges(int pin) => risingEdges[pin];

        public ulong RisingEdges(string pin) => RisingEdges(PinNumber(pin));

        public void ClearEdgeCounts() => Array.Clear(risingEdges, 0, risingEdges.Length);

        public static int PinNumber(string name)
        {
            name = name.Trim().ToUpperInvariant();
            if(name.Length < 3 || name[0] != 'P' || name[1] < 'A' || name[1] > 'H' || !int.TryParse(name.Substring(2), out var pin) || pin >= PinsPerGroup)
            {
                throw new RecoverableException($"\"{name}\" is not a pin name such as PA05");
            }
            return (name[1] - 'A') * PinsPerGroup + pin;
        }

        private static IEnumerable<int> ParsePins(string list)
        {
            return list.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(PinNumber);
        }

        private Group GroupAt(long offset)
        {
            var index = offset / GroupSize;
            return index >= 0 && index < groups.Length ? groups[index] : null;
        }

        private static uint InputLevels(Group g)
        {
            uint pulled = 0;
            uint pullUps = 0;
            for(var pin = 0; pin < PinsPerGroup; pin++)
            {
                if((g.PinCfg[pin] & PinCfgPullEn) != 0)
                {
                    pulled |= 1u << pin;
                }
            }
            pullUps = pulled & g.Out;
            var inputs = ~g.Dir;
            return (g.Out & g.Dir)
                 | (inputs & g.Driven & g.DrivenLevels)
                 | (inputs & ~g.Driven & pullUps);
        }

        private void SetOut(int index, uint value)
        {
            var g = groups[index];
            var rising = ~g.Out & value;
            var changed = g.Out ^ value;
            g.Out = value;
            if(changed == 0)
            {
                return;
            }
            for(var pin = 0; pin < PinsPerGroup; pin++)
            {
                var bit = 1u << pin;
                if((changed & bit) != 0)
                {
                    var number = index * PinsPerGroup + pin;
                    if((rising & bit & g.Dir) != 0)
                    {
                        risingEdges[number]++;
                    }
                    Connections[number].Set((value & bit) != 0);
                }
            }
        }

        private static void WriteConfiguration(Group g, uint value)
        {
            var mask = value & 0xFFFF;
            var first = (value & WrConfigHwSel) != 0 ? 16 : 0;
            for(var i = 0; i < 16; i++)
            {
                if((mask & (1u << i)) == 0)
                {
                    continue;
                }
                var pin = first + i;
                if((value & WrConfigWrPinCfg) != 0)
                {
                    g.PinCfg[pin] = (byte)((value >> 16) & PinCfgMask);
                }
                if((value & WrConfigWrPmux) != 0)
                {
                    var pmux = (byte)((value >> 24) & 0xF);
                    var current = g.Pmux[pin / 2];
                    g.Pmux[pin / 2] = (pin & 1) != 0 ? (byte)((current & 0x0F) | (pmux << 4)) : (byte)((current & 0xF0) | pmux);
                }
            }
        }

        private static bool IsSetClearToggle(long reg) => reg == DirClr || reg == DirSet || reg == DirTgl || reg == OutClr || reg == OutSet || reg == OutTgl;

        private uint MergeHalf(long offset, ushort value)
        {
            var shift = (int)((offset & 2) * 8);
            return (ReadDoubleWord(offset & ~3L) & ~(0xFFFFu << shift)) | ((uint)value << shift);
        }

        private readonly IMachine machine;
        private readonly Group[] groups;
        private readonly ulong[] risingEdges;

        private const int PinsPerGroup = 32;
        private const long GroupSize = 0x80;
        private const long Dir = 0x00;
        private const long DirClr = 0x04;
        private const long DirSet = 0x08;
        private const long DirTgl = 0x0C;
        private const long Out = 0x10;
        private const long OutClr = 0x14;
        private const long OutSet = 0x18;
        private const long OutTgl = 0x1C;
        private const long In = 0x20;
        private const long Ctrl = 0x24;
        private const long WrConfig = 0x28;
        private const long EvCtrl = 0x2C;
        private const long PmuxBase = 0x30;
        private const long PinCfgBase = 0x40;
        private const byte PinCfgPullEn = 0x04;
        private const byte PinCfgMask = 0x47;        // PMUXEN, INEN, PULLEN, DRVSTR
        private const uint WrConfigWrPmux = 1u << 28;
        private const uint WrConfigWrPinCfg = 1u << 30;
        private const uint WrConfigHwSel = 1u << 31;

        private class Group
        {
            public void Reset()
            {
                Dir = 0;
                Out = 0;
                Ctrl = 0;
                EvCtrl = 0;
                Array.Clear(PinCfg, 0, PinCfg.Length);
                Array.Clear(Pmux, 0, Pmux.Length);
            }

            public uint Dir;
            public uint Out;
            public uint Ctrl;
            public uint EvCtrl;
            public uint Driven;
            public uint DrivenLevels;
            public readonly byte[] PinCfg = new byte[PinsPerGroup];
            public readonly byte[] Pmux = new byte[PinsPerGroup / 2];
        }
    }
}
