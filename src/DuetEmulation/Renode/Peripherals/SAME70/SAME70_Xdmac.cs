//
// SAME70 Extensible DMA Controller (XDMAC).
//
// Modelled as a plain bus-to-bus copier. When a channel is enabled the whole microblock is performed
// immediately, synchronously, through the system bus - there is no cycle-by-cycle arbitration, no
// FIFO, and no descriptor chaining.
//
// Doing it through the bus rather than by talking to peripherals directly is what keeps this model
// independent: an SD read is configured with the source fixed at the HSMCI FIFO aperture and the
// destination incrementing through RAM, so repeatedly reading that one address and writing onward is
// exactly right, and XDMAC needs to know nothing about HSMCI.
//
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.DMA
{
    public class SAME70_Xdmac : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public SAME70_Xdmac(IMachine machine)
        {
            this.machine = machine;
            channels = new Channel[NumChannels];
            for(var i = 0; i < NumChannels; i++)
            {
                channels[i] = new Channel();
            }
            var irqs = new Dictionary<int, IGPIO> { { 0, new GPIO() } };
            Connections = new ReadOnlyDictionary<int, IGPIO>(irqs);
            Reset();
        }

        public void Reset()
        {
            enabledMask = 0;
            globalInterruptMask = 0;
            foreach(var channel in channels)
            {
                channel.Reset();
            }
            UpdateInterrupt();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset >= ChannelBase)
            {
                var index = (int)((offset - ChannelBase) / ChannelSize);
                if(index >= NumChannels)
                {
                    return 0;
                }
                var channel = channels[index];
                switch((offset - ChannelBase) % ChannelSize)
                {
                    case ChannelInterruptMask: return channel.InterruptMask;
                    case ChannelInterruptStatus:
                    {
                        var status = channel.InterruptStatus;
                        channel.InterruptStatus = 0;        // clear on read
                        UpdateInterrupt();
                        return status;
                    }
                    case ChannelSourceAddress: return channel.SourceAddress;
                    case ChannelDestAddress: return channel.DestinationAddress;
                    case ChannelMicroblockControl: return channel.MicroblockLength;
                    case ChannelConfiguration: return channel.Configuration;
                    default: return 0;
                }
            }

            switch(offset)
            {
                case GlobalType: return NumChannels - 1;
                case GlobalInterruptMask: return globalInterruptMask;
                case GlobalStatus: return enabledMask;
                // The handler reads this first to find which channels interrupted, and only then reads
                // each channel's CIS to clear it. Returning 0 here meant no channel was ever serviced,
                // so the flag was never cleared and the interrupt re-entered forever - the firmware
                // wedged after exactly one DMA completion, with no error anywhere.
                case GlobalInterruptStatus:
                {
                    var pending = 0u;
                    for(var i = 0; i < NumChannels; i++)
                    {
                        if((channels[i].InterruptStatus & channels[i].InterruptMask) != 0)
                        {
                            pending |= 1u << i;
                        }
                    }
                    return pending;
                }
                default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset >= ChannelBase)
            {
                var index = (int)((offset - ChannelBase) / ChannelSize);
                if(index >= NumChannels)
                {
                    return;
                }
                var channel = channels[index];
                switch((offset - ChannelBase) % ChannelSize)
                {
                    case ChannelInterruptEnable: channel.InterruptMask |= value; break;
                    case ChannelInterruptDisable: channel.InterruptMask &= ~value; break;
                    case ChannelSourceAddress: channel.SourceAddress = value; break;
                    case ChannelDestAddress: channel.DestinationAddress = value; break;
                    case ChannelMicroblockControl: channel.MicroblockLength = value; break;
                    case ChannelConfiguration: channel.Configuration = value; break;
                    default: break;
                }
                return;
            }

            switch(offset)
            {
                case GlobalInterruptEnable: globalInterruptMask |= value; break;
                case GlobalInterruptDisable: globalInterruptMask &= ~value; break;
                case GlobalChannelEnable:
                    for(var i = 0; i < NumChannels; i++)
                    {
                        if((value & (1u << i)) != 0)
                        {
                            RunChannel(i);
                        }
                    }
                    break;
                case GlobalChannelDisable:
                    enabledMask &= ~value;
                    break;
                default:
                    break;
            }
        }

        public long Size => 0x1000;

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // Number of microblocks completed, so a test can assert that a transfer actually happened.
        public ulong CompletedTransfers { get; private set; }

        private void RunChannel(int index)
        {
            var channel = channels[index];
            var width = WidthInBytes(channel.Configuration);
            var count = channel.MicroblockLength;
            var source = channel.SourceAddress;
            var destination = channel.DestinationAddress;
            var sourceFixed = ((channel.Configuration >> SourceAddressingModeShift) & 0x3) == AddressingFixed;
            var destFixed = ((channel.Configuration >> DestAddressingModeShift) & 0x3) == AddressingFixed;

            var bus = machine.GetSystemBus(this);
            for(var i = 0u; i < count; i++)
            {
                switch(width)
                {
                    case 1: bus.WriteByte(destination, bus.ReadByte(source)); break;
                    case 2: bus.WriteWord(destination, bus.ReadWord(source)); break;
                    default: bus.WriteDoubleWord(destination, bus.ReadDoubleWord(source)); break;
                }
                if(!sourceFixed)
                {
                    source += width;
                }
                if(!destFixed)
                {
                    destination += width;
                }
            }

            channel.SourceAddress = source;
            channel.DestinationAddress = destination;
            channel.MicroblockLength = 0;
            channel.InterruptStatus |= InterruptEndOfBlock;
            CompletedTransfers++;

            // The transfer is already finished, so the channel is not left enabled: firmware polls
            // the global status to decide when a transfer is done.
            enabledMask &= ~(1u << index);
            UpdateInterrupt();
        }

        private static uint WidthInBytes(uint configuration)
        {
            switch((configuration >> DataWidthShift) & 0x3)
            {
                case 0: return 1;
                case 1: return 2;
                case 3: return 8;
                default: return 4;
            }
        }

        private void UpdateInterrupt()
        {
            var pending = false;
            for(var i = 0; i < NumChannels; i++)
            {
                if((channels[i].InterruptStatus & channels[i].InterruptMask) != 0 && (globalInterruptMask & (1u << i)) != 0)
                {
                    pending = true;
                    break;
                }
            }
            Connections[0].Set(pending);
        }

        private readonly IMachine machine;
        private readonly Channel[] channels;
        private uint enabledMask;
        private uint globalInterruptMask;

        private class Channel
        {
            public void Reset()
            {
                InterruptMask = 0;
                InterruptStatus = 0;
                SourceAddress = 0;
                DestinationAddress = 0;
                MicroblockLength = 0;
                Configuration = 0;
            }

            public uint InterruptMask;
            public uint InterruptStatus;
            public uint SourceAddress;
            public uint DestinationAddress;
            public uint MicroblockLength;
            public uint Configuration;
        }

        private const int NumChannels = 24;
        private const long ChannelBase = 0x50;
        private const long ChannelSize = 0x40;

        private const long GlobalType = 0x00;
        private const long GlobalInterruptEnable = 0x0C;
        private const long GlobalInterruptDisable = 0x10;
        private const long GlobalInterruptMask = 0x14;
        private const long GlobalInterruptStatus = 0x18;
        private const long GlobalChannelEnable = 0x1C;
        private const long GlobalChannelDisable = 0x20;
        private const long GlobalStatus = 0x24;

        private const long ChannelInterruptEnable = 0x00;
        private const long ChannelInterruptDisable = 0x04;
        private const long ChannelInterruptMask = 0x08;
        private const long ChannelInterruptStatus = 0x0C;
        private const long ChannelSourceAddress = 0x10;
        private const long ChannelDestAddress = 0x14;
        private const long ChannelMicroblockControl = 0x20;
        private const long ChannelConfiguration = 0x28;

        private const int DataWidthShift = 11;
        private const int SourceAddressingModeShift = 16;
        private const int DestAddressingModeShift = 18;
        private const uint AddressingFixed = 0;
        private const uint InterruptEndOfBlock = 1u << 0;
    }
}
