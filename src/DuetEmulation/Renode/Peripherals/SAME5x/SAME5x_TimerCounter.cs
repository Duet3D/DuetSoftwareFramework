//
// SAME5x/SAMC21 TC (timer/counter), modelled for what Duet3Expansion uses it for: the step clock.
//
// The step clock is a TC pair in COUNT32 mode (TC6+TC7 on the SAME5x boards, Movement/StepTimer.cpp),
// clocked at 48MHz / 64 = 750kHz and free-running (WAVE = NFRQ). Moves are timed by compare matches
// on CC0 (the step interrupt) and CC1 (the software timers), which raise MC0/MC1. The counter is read
// with the READSYNC command, which the model completes at once.
//
// The other TCs are PWM outputs in COUNT16 mode (CoreN2G AnalogOut.cpp, AnalogWriteTc): NPWM, whose
// top is the counter's maximum, or MPWM, whose top is CC0. The duty and frequency of each output, from
// CCx, the top, the prescaler and the GCLK frequency, are what the GPIO model reads for a pin muxed to
// the output. Buffered writes (CCBUF) take effect at once rather than at the next update: the
// difference is at most one PWM period.
//
// The counter is clocked by its GCLK channel, at the frequency the GCLK model reports for it.
//
//     tc6: Timers.SAME5x_TimerCounter @ sysbus 0x43001400
//         gclk: gclk
//         gclkChannel: 39
//         IRQ -> nvic@113
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Duet;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class SAME5x_TimerCounter : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize, IDuetPwmSource
    {
        public SAME5x_TimerCounter(IMachine machine, SAME5x_Gclk gclk = null, int gclkChannel = -1, ulong frequency = 48000000)
        {
            this.gclk = gclk;
            this.gclkChannel = gclkChannel;
            defaultFrequency = frequency;
            IRQ = new GPIO();
            // A limit timer starts again from 0 on reaching its limit, so the limit is the counter's period
            counter = new LimitTimer(machine.ClockSource, frequency, this, "count", Max32 + 1, Direction.Ascending, false, WorkMode.Periodic, true, true);
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
            PwmChanged?.Invoke();
        }

        public event Action PwmChanged;

        public bool TryGetPwm(int output, out double duty, out double frequency)
        {
            duty = 0;
            frequency = 0;
            var waveGen = storage[Wave] & WaveGenMask;
            if(!Enabled || output < 0 || output >= NumCompares || (waveGen != WaveGenNpwm && waveGen != WaveGenMpwm))
            {
                return false;
            }
            if(waveGen == WaveGenMpwm && output == 0)
            {
                return false;           // CC0 is the top, not an output
            }
            var top = (waveGen == WaveGenMpwm ? cc[0] : CountMax) + 1;
            duty = Math.Min(1.0, cc[output] / (double)top);
            frequency = InputFrequency / (double)prescalerValue / top;
            return true;
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
            var pwmChanged = (offset <= CtrlA && end > CtrlA) || (offset <= Wave && end > Wave);
            for(var channel = 0; channel < NumCompares; channel++)
            {
                var buffer = CompareBufferOffset(channel);
                if(offset < buffer + CountBytes && end > buffer)
                {
                    Array.Copy(storage, buffer, storage, CompareOffset(channel), CountBytes);
                }
                var start = CompareOffset(channel);
                if((offset < start + CountBytes && end > start) || (offset < buffer + CountBytes && end > buffer))
                {
                    cc[channel] = StoredValue(start, CountBytes) & CountMax;
                    ArmCompare(channel);
                    pwmChanged = true;
                }
            }
            UpdateInterrupt();
            if(pwmChanged)
            {
                PwmChanged?.Invoke();
            }
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
            var input = InputFrequency;
            if(mode != modeValue || prescaler != prescalerValue || input != clockedAt)
            {
                modeValue = mode;
                prescalerValue = prescaler;
                clockedAt = input;
                var frequency = input / prescaler;
                counter.Frequency = frequency;
                counter.Limit = CountMax + 1;
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
                    foreach(var compare in compares)
                    {
                        compare.Enabled = false;
                    }
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

        private long CompareBufferOffset(int channel) => CcBufOffset + channel * CountBytes;

        private bool Enabled => (storage[CtrlA] & CtrlAEnable) != 0;

        // COUNT and CCx are 8, 16 or 32 bits wide according to CTRLA.MODE
        private int CountBytes => modeValue == ModeCount32 ? 4 : modeValue == ModeCount8 ? 1 : 2;
        private ulong CountMax => modeValue == ModeCount32 ? Max32 : modeValue == ModeCount8 ? 0xFFUL : 0xFFFFUL;

        // The GCLK channel's frequency, or the platform's when there is no GCLK model
        private ulong InputFrequency
        {
            get
            {
                var frequency = gclk != null && gclkChannel >= 0 ? gclk.ChannelFrequency(gclkChannel) : 0;
                return frequency != 0 ? frequency : defaultFrequency;
            }
        }

        private readonly SAME5x_Gclk gclk;
        private readonly int gclkChannel;
        private readonly ulong defaultFrequency;
        private ulong clockedAt;
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
        private const long CcBufOffset = 0x30;
        private const long Wave = 0x0C;
        private const byte WaveGenMask = 0x3;
        private const byte WaveGenNpwm = 2;
        private const byte WaveGenMpwm = 3;
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
