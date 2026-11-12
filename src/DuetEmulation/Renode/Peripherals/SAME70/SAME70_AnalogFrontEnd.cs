//
// SAME70 Analog Front End Controller (AFEC).
//
// Without this the board looks broken from the outside: RepRapFirmware computes
// "channels completed" as CHSR & ISR & ~OVER (CoreN2G AnalogIn.cpp), so with SVD stubs returning zero
// no conversion ever finishes. M105 answers nothing, M122 stops dead where MCU temperature and supply
// voltage would print, and the drivers never see a supply voltage worth enabling for.
//
// Conversion is not simulated - each channel just holds a value. That is the point: it is how the
// emulator pretends there is hardware attached. Set them with the Channels property, e.g.
//
//     Channels: "9:8603,4:4302"        // AFEC0: 24V on VIN, 12V on the 12V rail
//
// MB6HC channel map (Pins_Duet3_MB6HC.h): AFEC0 ch4 = 12V rail, ch9 = VIN, ch11 = MCU temperature;
// AFEC1 ch2, 4, 5, 6 = thermistors 0-3. Readings are 14 bits on this part (AnalogIn::AdcBits).
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Analog
{
    public class SAME70_AnalogFrontEnd : IDoubleWordPeripheral, IKnownSize
    {
        public SAME70_AnalogFrontEnd(IMachine machine)
        {
            values = new uint[NumChannels];
            Reset();
        }

        public void Reset()
        {
            enabled = 0;
            endOfConversion = 0;
            selected = 0;
            mode = 0;
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
                case RegisterMr:
                    return mode;
                case RegisterChsr:
                    return enabled;
                case RegisterIsr:
                    // RepRapFirmware ANDs this with CHSR to find completed channels, so only enabled
                    // ones may show as done.
                    return endOfConversion & enabled;
                case RegisterOver:
                    return 0;                       // never report an overrun; nothing here is racing
                case RegisterCselr:
                    return selected;
                case RegisterCdr:
                {
                    var channel = selected & (NumChannels - 1);
                    endOfConversion &= ~(1u << (int)channel);   // reading the data clears that channel's EOC
                    reads++;
                    return values[channel];
                }
                case RegisterLcdr:
                    return values[selected & (NumChannels - 1)];
                default:
                    return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
                case RegisterCr:
                    if((value & CrStart) != 0)
                    {
                        endOfConversion = enabled;  // a conversion of every enabled channel, instantly
                        starts++;
                    }
                    break;
                case RegisterMr:
                    mode = value;
                    this.Log(LogLevel.Info, "MR 0x{0:X}", value);
                    break;
                case RegisterCher:
                    enabled |= value & ChannelMask;
                    this.Log(LogLevel.Info, "CHER 0x{0:X} -> enabled 0x{1:X}", value, enabled);
                    break;
                case RegisterChdr:
                    enabled &= ~value;
                    break;
                case RegisterCselr:
                    selected = value & (NumChannels - 1);
                    break;
                default:
                    break;
            }
        }

        public long Size => 0x200;

        // "channel:value,channel:value". Values are raw 14-bit ADC readings.
        public string Channels
        {
            get
            {
                var parts = new List<string>();
                for(var i = 0; i < NumChannels; i++)
                {
                    if(values[i] != 0)
                    {
                        parts.Add($"{i}:{values[i]}");
                    }
                }
                return string.Join(",", parts);
            }
            set
            {
                foreach(var entry in value.Split(','))
                {
                    if(entry.Trim().Length == 0)
                    {
                        continue;
                    }
                    var pair = entry.Split(':');
                    if(pair.Length != 2 || !int.TryParse(pair[0].Trim(), out var channel)
                        || !uint.TryParse(pair[1].Trim(), out var reading))
                    {
                        throw new Antmicro.Renode.Exceptions.ConstructionException(
                            $"AFEC Channels: expected 'channel:value' pairs, got '{entry}'");
                    }
                    if(channel < 0 || channel >= NumChannels)
                    {
                        throw new Antmicro.Renode.Exceptions.ConstructionException(
                            $"AFEC Channels: channel {channel} is outside 0..{NumChannels - 1}");
                    }
                    values[channel] = reading;
                }
            }
        }

        // For scripts and tests that want to move a reading while the machine is running - warming a
        // heater, sagging the supply, and so on.
        public void SetChannel(int channel, uint reading)
        {
            if(channel < 0 || channel >= NumChannels)
            {
                this.Log(LogLevel.Error, "channel {0} is outside 0..{1}", channel, NumChannels - 1);
                return;
            }
            values[channel] = reading;
        }

        public uint GetChannel(int channel)
        {
            return (channel >= 0 && channel < NumChannels) ? values[channel] : 0;
        }

        private readonly uint[] values;
        private uint enabled;
        private uint endOfConversion;
        private uint selected;
        private uint mode;
        private ulong starts;
        private ulong reads;

        public ulong Starts => starts;
        public ulong Reads => reads;

        private const int NumChannels = 16;
        private const uint ChannelMask = 0xFFF;      // 12 external channels take part in CHSR/ISR

        private const uint CrStart = 1u << 1;

        private const long RegisterCr = 0x00;
        private const long RegisterMr = 0x04;
        private const long RegisterCher = 0x14;
        private const long RegisterChdr = 0x18;
        private const long RegisterChsr = 0x1C;
        private const long RegisterLcdr = 0x20;
        private const long RegisterIsr = 0x30;
        private const long RegisterOver = 0x4C;
        private const long RegisterCselr = 0x64;
        private const long RegisterCdr = 0x68;
    }
}
