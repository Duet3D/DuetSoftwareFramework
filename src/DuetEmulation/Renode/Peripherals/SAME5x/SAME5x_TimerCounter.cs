//
// SAME5x/SAMC21 TC (timer/counter), modelled for what Duet3Expansion uses it for: the step clock.
//
// The step clock is a TC pair in COUNT32 mode (TC6+TC7 on the SAME5x boards, Movement/StepTimer.cpp),
// clocked at 48MHz / 64 = 750kHz and free-running (WAVE = NFRQ). Moves are timed by compare matches
// on CC0 (the step interrupt) and CC1 (the software timers), which raise MC0/MC1. The counter is read
// with the READSYNC command, which the model completes at once.
//
// The other TCs are only configured as PWM outputs, which nothing here observes; their registers are
// stored so that what is written reads back.
//
//     tc6: Timers.SAME5x_TimerCounter @ sysbus 0x43001400
//         frequency: 48000000
//         IRQ -> nvic@113
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class SAME5x_TimerCounter : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public SAME5x_TimerCounter(IMachine machine, ulong frequency = 48000000)
        {
            inputFrequency = frequency;
            IRQ = new GPIO();
            counter = new LimitTimer(machine.ClockSource, frequency, this, "count", Max32, Direction.Ascending, false, WorkMode.Periodic, true, true);
            counter.LimitReached += () => SetFlags(IntOvf);
            compares = new LimitTimer[NumCompares];
            for(var i = 0; i < NumCompares; i++)
            {
                var channel = i;
                compares[i] = new LimitTimer(machine.ClockSource, frequency, this, $"cc{i}", Max32, Direction.Ascending, false, WorkMode.Periodic, true, true);
                compares[i].LimitReached += () => OnCompareMatch(channel);
            }
            Reset();
        }

        public void Reset()
        {
            Array.Clear(storage, 0, storage.Length);
            counter.Enabled = false;
            counter.Value = 0;
            foreach(var compare in compares)
            {
                compare.Enabled = false;
            }
            cc = new ulong[NumCompares];
            intEnable = 0;
            intFlags = 0;
            UpdateInterrupt();
        }

        public byte ReadByte(long offset) => (byte)Read(offset, 1);
        public ushort ReadWord(long offset) => (ushort)Read(offset, 2);
        public uint ReadDoubleWord(long offset) => Read(offset, 4);
        public void WriteByte(long offset, byte value) => Write(offset, 1, value);
        public void WriteWord(long offset, ushort value) => Write(offset, 2, value);
        public void WriteDoubleWord(long offset, uint value) => Write(offset, 4, value);

        public long Size => 0x40;

        public GPIO IRQ { get; }

        // The counter as the firmware would read it
        public ulong Count => Enabled ? counter.Value & CountMax : 0;

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
                case CtrlBClr:
                case CtrlBSet:
                    return (byte)(storage[CtrlBSet] & ~CtrlBCmdMask);        // commands complete at once
                case IntEnClr:
                case IntEnSet:
                    return intEnable;
                case IntFlag:
                    return intFlags;
                case SyncBusy:
                case SyncBusy + 1:
                case SyncBusy + 2:
                case SyncBusy + 3:
                    return 0;
            }
            if(offset >= CountOffset && offset < CountOffset + CountBytes)
            {
                return (byte)(Count >> (int)(8 * (offset - CountOffset)));
            }
            var channel = CompareChannelAt(offset, out var byteIndex);
            if(channel >= 0)
            {
                return (byte)(cc[channel] >> (8 * byteIndex));
            }
            return offset < storage.Length ? storage[offset] : (byte)0;
        }

        private void Write(long offset, int width, uint value)
        {
            // Registers are written a byte at a time, but act once the whole access has been stored
            for(var i = 0; i < width; i++)
            {
                var b = (byte)(value >> (8 * i));
                var o = offset + i;
                switch(o)
                {
                    case CtrlBClr:
                        storage[CtrlBSet] &= (byte)~b;
                        break;
                    case CtrlBSet:
                        storage[CtrlBSet] |= (byte)(b & ~CtrlBCmdMask);
                        Command((b & CtrlBCmdMask) >> 5);
                        break;
                    case IntEnClr:
                        intEnable &= (byte)~b;
                        break;
                    case IntEnSet:
                        intEnable |= b;
                        break;
                    case IntFlag:
                        intFlags &= (byte)~b;
                        break;
                    default:
                        if(o < storage.Length)
                        {
                            storage[o] = b;
                        }
                        break;
                }
            }
            var end = offset + width;
            if(offset <= CtrlA && end > CtrlA)
            {
                WriteCtrlA();
            }
            if(offset < CountOffset + CountBytes && end > CountOffset)
            {
                SetCount(StoredValue(CountOffset, CountBytes));
            }
            for(var channel = 0; channel < NumCompares; channel++)
            {
                var start = CompareOffset(channel);
                if(offset < start + CountBytes && end > start)
                {
                    cc[channel] = StoredValue(start, CountBytes) & CountMax;
                    ArmCompare(channel);
                }
            }
            UpdateInterrupt();
        }

        private void WriteCtrlA()
        {
            var ctrla = (uint)StoredValue(CtrlA, 4);
            if((ctrla & CtrlASwRst) != 0)
            {
                Reset();
                return;
            }
            var mode = (ctrla >> 2) & 3;
            var prescaler = Prescalers[(ctrla >> 8) & 7];
            var enable = (ctrla & CtrlAEnable) != 0;
            if(mode != modeValue || prescaler != prescalerValue)
            {
                modeValue = mode;
                prescalerValue = prescaler;
                var frequency = inputFrequency / prescaler;
                counter.Frequency = frequency;
                counter.Limit = CountMax;
                foreach(var compare in compares)
                {
                    compare.Frequency = frequency;
                }
            }
            if(enable != counter.Enabled)
            {
                counter.Enabled = enable;
                for(var channel = 0; channel < NumCompares; channel++)
                {
                    ArmCompare(channel);
                }
            }
        }

        private void Command(int command)
        {
            switch(command)
            {
                case CmdNone:
                case CmdReadSync:
                    break;
                case CmdRetrigger:
                    SetCount(0);
                    counter.Enabled = Enabled;
                    break;
                case CmdStop:
                    counter.Enabled = false;
                    break;
                default:
                    this.Log(LogLevel.Debug, "Command {0} ignored", command);
                    break;
            }
        }

        private void SetCount(ulong value)
        {
            counter.Value = value & CountMax;
            for(var channel = 0; channel < NumCompares; channel++)
            {
                ArmCompare(channel);
            }
        }

        private void ArmCompare(int channel)
        {
            var compare = compares[channel];
            compare.Enabled = false;
            if(!Enabled)
            {
                return;
            }
            // Ticks until the counter next equals CC; a CC equal to the count now matches a whole wrap from now
            var delta = (cc[channel] - Count) & CountMax;
            compare.Limit = delta == 0 ? CountMax + 1 : delta;
            compare.Value = 0;
            compare.Enabled = true;
        }

        private void OnCompareMatch(int channel)
        {
            // Matches again after a whole wrap of the counter, unless CC is changed
            compares[channel].Limit = CountMax + 1;
            SetFlags((byte)(IntMc0 << channel));
        }

        private void SetFlags(byte flags)
        {
            intFlags |= flags;
            UpdateInterrupt();
        }

        private void UpdateInterrupt()
        {
            IRQ.Set((intFlags & intEnable) != 0);
        }

        private int CompareChannelAt(long offset, out int byteIndex)
        {
            for(var channel = 0; channel < NumCompares; channel++)
            {
                var start = CompareOffset(channel);
                if(offset >= start && offset < start + CountBytes)
                {
                    byteIndex = (int)(offset - start);
                    return channel;
                }
            }
            byteIndex = 0;
            return -1;
        }

        private ulong StoredValue(long offset, int bytes)
        {
            ulong value = 0;
            for(var i = 0; i < bytes; i++)
            {
                value |= (ulong)storage[offset + i] << (8 * i);
            }
            return value;
        }

        private long CompareOffset(int channel) => CcOffset + channel * CountBytes;

        private bool Enabled => (storage[CtrlA] & CtrlAEnable) != 0;

        // COUNT and CCx are 8, 16 or 32 bits wide according to CTRLA.MODE
        private int CountBytes => modeValue == ModeCount32 ? 4 : modeValue == ModeCount8 ? 1 : 2;
        private ulong CountMax => modeValue == ModeCount32 ? Max32 : modeValue == ModeCount8 ? 0xFFUL : 0xFFFFUL;

        private readonly ulong inputFrequency;
        private readonly LimitTimer counter;
        private readonly LimitTimer[] compares;
        private readonly byte[] storage = new byte[0x40];
        private ulong[] cc;
        private byte intEnable;
        private byte intFlags;
        private uint modeValue;
        private uint prescalerValue = 1;

        private static readonly uint[] Prescalers = { 1, 2, 4, 8, 16, 64, 256, 1024 };

        private const int NumCompares = 2;
        private const ulong Max32 = 0xFFFFFFFF;
        private const uint ModeCount8 = 1;
        private const uint ModeCount32 = 2;
        private const long CtrlA = 0x00;
        private const long CtrlBClr = 0x04;
        private const long CtrlBSet = 0x05;
        private const long IntEnClr = 0x08;
        private const long IntEnSet = 0x09;
        private const long IntFlag = 0x0A;
        private const long SyncBusy = 0x10;
        private const long CountOffset = 0x14;
        private const long CcOffset = 0x1C;
        private const uint CtrlASwRst = 0x1;
        private const uint CtrlAEnable = 0x2;
        private const byte CtrlBCmdMask = 0xE0;
        private const int CmdNone = 0;
        private const int CmdRetrigger = 1;
        private const int CmdStop = 2;
        private const int CmdReadSync = 4;
        private const byte IntOvf = 0x01;
        private const byte IntMc0 = 0x10;
    }
}
