//
// SAME70 Timer/Counter (TC).
//
// Modelled to the extent RepRapFirmware needs, which is the step clock. RRF chains two 16-bit
// channels into the 32-bit step counter (Movement/StepTimer.cpp): channel 0 is the low word, driven
// by TIMER_CLOCK1 = PCK6 = MCK/200 = 750kHz, and channel 2 is the high word, clocked once per wrap of
// channel 0 via TIOA0 and the bus matrix (TC_BMR_TC2XC2S_TIOA0 + TC_CMR_BURST_XC2).
//
// The chaining is modelled directly - channel 2 increments when channel 0 wraps - rather than by
// simulating TIOA waveform generation and burst gating. With RA=0xFFFF and RC=0, which is what RRF
// programs after init, the two are equivalent: TIOA is asserted for exactly one tick per wrap.
//
// The RB compare that raises the step interrupt is deliberately 16-bit, as the hardware is. RRF
// writes a full 32-bit tick count into a 16-bit register, so a target more than 65535 ticks away
// matches early. That happens on real silicon too, and reproducing it is the point: a 32-bit compare
// here would hide a class of bug rather than expose it.
//
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class SAME70_TimerCounter : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public SAME70_TimerCounter(IMachine machine, ulong frequency = DefaultFrequency)
        {
            this.frequency = frequency;

            var irqs = new Dictionary<int, IGPIO>();
            for(var i = 0; i < NumChannels; i++)
            {
                irqs[i] = new GPIO();
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(irqs);

            channels = new Channel[NumChannels];
            for(var i = 0; i < NumChannels; i++)
            {
                channels[i] = new Channel();
            }

            // Channel 0's counter. Free-running 16-bit, so it reaches its limit every ~87ms at 750kHz;
            // that is cheap enough to leave running for the whole emulation.
            lowWord = new LimitTimer(machine.ClockSource, frequency, this, "tc-low", CounterWrap,
                                     Direction.Ascending, false, WorkMode.Periodic, true, true);
            lowWord.LimitReached += OnLowWordWrapped;

            // Fires when the low word next equals RB. Re-armed to a whole wrap after each match so it
            // keeps matching at the same phase, which is what a hardware compare does.
            compareB = new LimitTimer(machine.ClockSource, frequency, this, "tc-rb", CounterWrap,
                                      Direction.Ascending, false, WorkMode.Periodic, true, true);
            compareB.LimitReached += OnCompareBMatched;

            Reset();
        }

        public void Reset()
        {
            lowWord.Enabled = false;
            lowWord.Limit = CounterWrap;
            compareB.Enabled = false;
            highWordValue = 0;
            foreach(var channel in channels)
            {
                channel.Reset();
            }
            UpdateInterrupt();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset >= BlockRegistersOffset)
            {
                return 0;
            }

            var index = (int)(offset / ChannelSize);
            var reg = offset % ChannelSize;
            if(index >= NumChannels)
            {
                return 0;
            }
            var channel = channels[index];

            switch(reg)
            {
                case ChannelRegisterCmr:
                    return channel.Mode;
                case ChannelRegisterCv:
                    return CurrentValue(index);
                case ChannelRegisterRa:
                    return channel.RegisterA;
                case ChannelRegisterRb:
                    return channel.RegisterB;
                case ChannelRegisterRc:
                    return channel.RegisterC;
                case ChannelRegisterSr:
                {
                    // Status bits are clear-on-read, and reading is how RRF acknowledges the step
                    // interrupt before it disables it.
                    var status = channel.Status;
                    if(channel.Enabled)
                    {
                        status |= StatusClkSta;
                    }
                    channel.Status = 0;
                    UpdateInterrupt();
                    return status;
                }
                case ChannelRegisterImr:
                    return channel.InterruptMask;
                default:
                    return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset >= BlockRegistersOffset)
            {
                // Block mode selects what clocks channel 2 (TC2XC2S). We already model the chain, so
                // there is nothing to configure, but log a selection we would not honour.
                if(offset == BlockRegisterBmr && (value & Tc2Xc2SMask) != Tc2Xc2STioa0)
                {
                    this.Log(LogLevel.Warning, "BMR selects a channel-2 clock source other than TIOA0 (0x{0:X}); the model always chains channel 2 to channel 0", value);
                }
                return;
            }

            var index = (int)(offset / ChannelSize);
            var reg = offset % ChannelSize;
            if(index >= NumChannels)
            {
                return;
            }
            var channel = channels[index];

            switch(reg)
            {
                case ChannelRegisterCcr:
                    if((value & CcrClkDis) != 0)
                    {
                        channel.Enabled = false;
                    }
                    if((value & CcrClkEn) != 0)
                    {
                        channel.Enabled = true;
                    }
                    if((value & CcrSwTrg) != 0)
                    {
                        channel.Enabled = true;
                        if(index == LowWordChannel)
                        {
                            lowWord.Value = 0;
                        }
                        else if(index == HighWordChannel)
                        {
                            highWordValue = 0;
                        }
                    }
                    if(index == LowWordChannel)
                    {
                        lowWord.Enabled = channel.Enabled;
                        if(!channel.Enabled)
                        {
                            compareB.Enabled = false;
                        }
                    }
                    break;
                case ChannelRegisterCmr:
                    channel.Mode = value;
                    break;
                case ChannelRegisterRa:
                    channel.RegisterA = value & CounterMask;
                    break;
                case ChannelRegisterRb:
                    channel.RegisterB = value & CounterMask;
                    if(index == LowWordChannel)
                    {
                        ArmCompareB();
                    }
                    break;
                case ChannelRegisterRc:
                    channel.RegisterC = value & CounterMask;
                    break;
                case ChannelRegisterIer:
                    channel.InterruptMask |= value;
                    UpdateInterrupt();
                    break;
                case ChannelRegisterIdr:
                    channel.InterruptMask &= ~value;
                    UpdateInterrupt();
                    break;
                default:
                    break;
            }
        }

        public long Size => 0x100;

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // The 32-bit step clock as RRF reconstructs it from the two channels. Handy for tests and for
        // making sense of a trace.
        public uint StepClock => (highWordValue << 16) | (CurrentValue(LowWordChannel) & CounterMask);

        private uint CurrentValue(int index)
        {
            if(index == LowWordChannel)
            {
                return (uint)(lowWord.Value & CounterMask);
            }
            if(index == HighWordChannel)
            {
                return highWordValue & CounterMask;
            }
            return 0;
        }

        private void OnLowWordWrapped()
        {
            channels[LowWordChannel].Status |= StatusCovfs;
            // The carry into the high word. On silicon this arrives as a TIOA0 pulse gated into
            // channel 2; the first one after start is famously lost to an erratum, which RRF works
            // around during init by pulsing RA/RC early. We simply do not generate that first pulse.
            if(channels[HighWordChannel].Enabled)
            {
                highWordValue = (highWordValue + 1) & CounterMask;
            }
            UpdateInterrupt();
        }

        private void OnCompareBMatched()
        {
            channels[LowWordChannel].Status |= StatusCpbs;
            compareB.Limit = CounterWrap;
            UpdateInterrupt();
        }

        private void ArmCompareB()
        {
            var target = channels[LowWordChannel].RegisterB;
            var now = CurrentValue(LowWordChannel);
            ulong delta = (target - now) & CounterMask;
            if(delta == 0)
            {
                delta = CounterWrap;
            }
            compareB.Enabled = false;
            compareB.Limit = delta;
            compareB.Value = 0;
            compareB.Enabled = channels[LowWordChannel].Enabled;
        }

        private void UpdateInterrupt()
        {
            for(var i = 0; i < NumChannels; i++)
            {
                Connections[i].Set((channels[i].Status & channels[i].InterruptMask) != 0);
            }
        }

        private readonly ulong frequency;
        private readonly LimitTimer lowWord;
        private readonly LimitTimer compareB;
        private readonly Channel[] channels;
        private uint highWordValue;

        private class Channel
        {
            public void Reset()
            {
                Mode = 0;
                RegisterA = 0;
                RegisterB = 0;
                RegisterC = 0;
                Status = 0;
                InterruptMask = 0;
                Enabled = false;
            }

            public uint Mode;
            public uint RegisterA;
            public uint RegisterB;
            public uint RegisterC;
            public uint Status;
            public uint InterruptMask;
            public bool Enabled;
        }

        // MCK/200 with MCK at 150MHz, matching the PCK6 divisor RRF computes for the SAME70 so that
        // the step clock is the same 750kHz the Duet 3 expansion boards use.
        private const ulong DefaultFrequency = 750000;

        private const int NumChannels = 3;
        private const int LowWordChannel = 0;
        private const int HighWordChannel = 2;
        private const uint CounterMask = 0xFFFF;
        private const ulong CounterWrap = 0x10000;

        private const long ChannelSize = 0x40;
        private const long BlockRegistersOffset = 0xC0;
        private const long BlockRegisterBmr = 0xC4;

        private const long ChannelRegisterCcr = 0x00;
        private const long ChannelRegisterCmr = 0x04;
        private const long ChannelRegisterCv = 0x10;
        private const long ChannelRegisterRa = 0x14;
        private const long ChannelRegisterRb = 0x18;
        private const long ChannelRegisterRc = 0x1C;
        private const long ChannelRegisterSr = 0x20;
        private const long ChannelRegisterIer = 0x24;
        private const long ChannelRegisterIdr = 0x28;
        private const long ChannelRegisterImr = 0x2C;

        private const uint CcrClkEn = 1u << 0;
        private const uint CcrClkDis = 1u << 1;
        private const uint CcrSwTrg = 1u << 2;

        private const uint StatusCovfs = 1u << 0;
        private const uint StatusCpbs = 1u << 3;
        private const uint StatusClkSta = 1u << 16;

        private const uint Tc2Xc2SMask = 3u << 4;
        private const uint Tc2Xc2STioa0 = 2u << 4;
    }
}
