//
// SAME5x DMAC: the CRC unit and descriptor-driven channels.
//
// The CRC unit is what makes the bootloader start the firmware: it checks the application's CRC-32
// (Bootloader.cpp, ComputeCRC32) by writing each word to CRCDATAIN and reading CRCCHKSUM. With CRC32
// selected the hardware gives the standard (zlib) CRC-32, which is what CrcAppender stamps on the image.
//
// Channels transfer one beat each time their trigger fires. A peripheral that can be served by DMA
// raises a numbered GPIO into this model with the trigger source number the datasheet gives it
// (e.g. SERCOM0 RX = 4, TX = 5), and the model performs the beat at once. Descriptors are read from
// and written back to memory as on the hardware: SRCADDR/DSTADDR of an incrementing transfer are the
// end addresses, the write-back section receives the remaining count, and linked descriptors are
// followed. Channels with no trigger source (TRIGSRC 0) run as soon as they are enabled.
//
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.DMA
{
    public class SAME5x_Dmac : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize, IGPIOReceiver, INumberedGPIOOutput
    {
        public SAME5x_Dmac(IMachine machine, int channels = 32)
        {
            sysbus = machine.GetSystemBus(this);
            this.channels = new Channel[channels];
            for(var i = 0; i < channels; i++)
            {
                this.channels[i] = new Channel();
            }
            var irqs = new Dictionary<int, IGPIO>();
            for(var i = 0; i < NumIrqs; i++)
            {
                irqs[i] = new GPIO();
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(irqs);
            Reset();
        }

        public void Reset()
        {
            Array.Clear(storage, 0, storage.Length);
            foreach(var channel in channels)
            {
                channel.Reset();
            }
            crc = 0;
            pending.Clear();
            UpdateInterrupts();
        }

        public byte ReadByte(long offset) => (byte)Read(offset, 1);
        public ushort ReadWord(long offset) => (ushort)Read(offset, 2);
        public uint ReadDoubleWord(long offset) => Read(offset, 4);
        public void WriteByte(long offset, byte value) => Write(offset, 1, value);
        public void WriteWord(long offset, ushort value) => Write(offset, 2, value);
        public void WriteDoubleWord(long offset, uint value) => Write(offset, 4, value);

        public long Size => ChannelBase + channels.Length * ChannelSize;

        // 0..3: channels 0..3 (DMAC_0..DMAC_3); 4: all others (DMAC_OTHER)
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // A peripheral's DMA request: number is the trigger source
        public void OnGPIO(int number, bool value)
        {
            if(!value)
            {
                return;
            }
            pending.Enqueue(number);
            if(processing)
            {
                return;             // a beat in progress raised it; the loop below picks it up
            }
            processing = true;
            try
            {
                while(pending.Count > 0)
                {
                    var trigger = pending.Dequeue();
                    for(var i = 0; i < channels.Length; i++)
                    {
                        var channel = channels[i];
                        if(channel.Enabled && channel.TriggerSource == trigger)
                        {
                            Beat(i);
                        }
                    }
                }
            }
            finally
            {
                processing = false;
            }
        }

        private uint Read(long offset, int width)
        {
            if(offset >= ChannelBase)
            {
                return ReadChannel(offset, width);
            }
            switch(offset)
            {
                case Ctrl:
                    return (uint)(storage[Ctrl] | storage[Ctrl + 1] << 8) & ~CtrlSwRst;
                case CrcChkSum:
                    // CRC32 reads back reflected and complemented, i.e. as zlib computes it
                    return CrcIs32 ? ~crc : crc;
                case CrcStatus:
                    return 0;         // never busy
                // The lowest channel with an enabled interrupt pending, and its flags, which DMAC_4_Handler loops on
                case IntPend:
                    {
                        var channel = LowestPendingChannel();
                        return channel < 0 ? 0u : (uint)channel | (uint)(channels[channel].IntFlag & channels[channel].IntEnable & IntPendFlags) << IntPendFlagsShift;
                    }
                case IntStatus:
                    {
                        uint status = 0;
                        for(var i = 0; i < channels.Length && i < 32; i++)
                        {
                            if((channels[i].IntFlag & channels[i].IntEnable) != 0)
                            {
                                status |= 1u << i;
                            }
                        }
                        return status;
                    }
                case BusyCh:
                case PendCh:
                case Active:
                    return 0;
            }
            uint value = 0;
            for(var i = 0; i < width && offset + i < storage.Length; i++)
            {
                value |= (uint)storage[offset + i] << (8 * i);
            }
            return value;
        }

        private void Write(long offset, int width, uint value)
        {
            if(offset >= ChannelBase)
            {
                WriteChannel(offset, width, value);
                return;
            }
            switch(offset)
            {
                case Ctrl:
                    if((value & CtrlSwRst) != 0)
                    {
                        Reset();
                        return;
                    }
                    break;
                case CrcDataIn:
                    CrcWord(value, width);
                    return;
                case CrcChkSum:
                    crc = value;      // the firmware seeds it with 0xFFFFFFFF, as zlib does
                    return;
                case CrcStatus:
                    return;
                // Writing a flag clears it on the channel the ID field names
                case IntPend:
                    {
                        var channel = (int)(value & IntPendIdMask);
                        if(channel < channels.Length)
                        {
                            channels[channel].IntFlag &= (byte)~((value >> IntPendFlagsShift) & IntPendFlags);
                            UpdateInterrupts();
                        }
                        return;
                    }
                case SwTrigCtrl:
                    for(var i = 0; i < channels.Length && i < 32; i++)
                    {
                        if((value & (1u << i)) != 0 && channels[i].Enabled)
                        {
                            RunBlock(i);
                        }
                    }
                    return;
            }
            for(var i = 0; i < width && offset + i < storage.Length; i++)
            {
                storage[offset + i] = (byte)(value >> (8 * i));
            }
        }

        private uint ReadChannel(long offset, int width)
        {
            var index = (int)((offset - ChannelBase) / ChannelSize);
            var reg = (offset - ChannelBase) % ChannelSize;
            if(index >= channels.Length)
            {
                return 0;
            }
            var channel = channels[index];
            uint value = 0;
            for(var i = 0; i < width; i++)
            {
                byte b;
                switch(reg + i)
                {
                    case ChIntEnClr:
                    case ChIntEnSet:
                        b = channel.IntEnable;
                        break;
                    case ChIntFlag:
                        b = channel.IntFlag;
                        break;
                    case ChStatus:
                        b = 0;
                        break;
                    default:
                        b = channel.Registers[reg + i];
                        break;
                }
                value |= (uint)b << (8 * i);
            }
            return value;
        }

        private void WriteChannel(long offset, int width, uint value)
        {
            var index = (int)((offset - ChannelBase) / ChannelSize);
            var reg = (offset - ChannelBase) % ChannelSize;
            if(index >= channels.Length)
            {
                return;
            }
            var channel = channels[index];
            var wasEnabled = channel.Enabled;
            for(var i = 0; i < width; i++)
            {
                var b = (byte)(value >> (8 * i));
                switch(reg + i)
                {
                    case ChIntEnClr:
                        channel.IntEnable &= (byte)~b;
                        break;
                    case ChIntEnSet:
                        channel.IntEnable |= b;
                        break;
                    case ChIntFlag:
                        channel.IntFlag &= (byte)~b;
                        break;
                    case ChCtrlB:
                        // Commands (SUSPEND, RESUME) complete at once
                        if((b & 0x3) == 1)
                        {
                            channel.IntFlag |= IntSusp;
                        }
                        break;
                    default:
                        channel.Registers[reg + i] = b;
                        break;
                }
            }
            if((channel.Registers[0] & ChCtrlASwRst) != 0)
            {
                channel.Reset();
            }
            else if(!wasEnabled && channel.Enabled)
            {
                channel.Descriptor = -1;
                if(channel.TriggerSource == 0)
                {
                    RunBlock(index);
                }
            }
            UpdateInterrupts();
        }

        // Transfer one beat (or, for a block or transaction trigger, the rest of the block)
        private void Beat(int index)
        {
            var channel = channels[index];
            if(!LoadDescriptor(index))
            {
                return;
            }
            var action = (channel.Registers[2] >> 4) & 0x3;        // CHCTRLA.TRIGACT
            if(action == TrigActBlock || action == TrigActTransaction)
            {
                RunBlock(index);
                return;
            }
            Transfer(index, 1);
        }

        private void RunBlock(int index)
        {
            var channel = channels[index];
            var guard = 0;
            while(channel.Enabled && LoadDescriptor(index) && guard++ < 1024)
            {
                Transfer(index, channel.Remaining);
                var action = (channel.Registers[2] >> 4) & 0x3;
                if(action != TrigActTransaction && channel.TriggerSource != 0)
                {
                    break;
                }
            }
        }

        // Make the channel's current descriptor the one it works from, loading it the first time
        private bool LoadDescriptor(int index)
        {
            var channel = channels[index];
            if(channel.Descriptor >= 0)
            {
                return channel.Remaining > 0;
            }
            var address = channel.NextDescriptor != 0 ? channel.NextDescriptor : BaseAddress + (ulong)(index * DescriptorSize);
            channel.Descriptor = (long)address;
            var btctrl = sysbus.ReadWord(address);
            if((btctrl & BtCtrlValid) == 0)
            {
                this.Log(LogLevel.Warning, "Channel {0} enabled with no valid descriptor at 0x{1:X}", index, address);
                channel.IntFlag |= IntTerr;
                Disable(index);
                return false;
            }
            channel.BtCtrl = btctrl;
            var count = sysbus.ReadWord(address + 2);
            channel.Remaining = count;
            channel.BeatSize = 1 << ((btctrl >> 8) & 0x3);
            var step = 1 << ((btctrl >> 13) & 0x7);
            var stepSelectsSource = (btctrl & BtCtrlStepSel) != 0;
            var srcInc = (btctrl & BtCtrlSrcInc) != 0;
            var dstInc = (btctrl & BtCtrlDstInc) != 0;
            channel.SourceStep = srcInc ? channel.BeatSize * (stepSelectsSource ? step : 1) : 0;
            channel.DestinationStep = dstInc ? channel.BeatSize * (stepSelectsSource ? 1 : step) : 0;
            // The descriptor holds end addresses for incrementing transfers
            channel.Source = sysbus.ReadDoubleWord(address + 4) - (ulong)(channel.SourceStep * count);
            channel.Destination = sysbus.ReadDoubleWord(address + 8) - (ulong)(channel.DestinationStep * count);
            channel.NextDescriptor = sysbus.ReadDoubleWord(address + 12);
            WriteBack(index);
            return count > 0;
        }

        private void Transfer(int index, int beats)
        {
            var channel = channels[index];
            for(var i = 0; i < beats && channel.Remaining > 0; i++)
            {
                switch(channel.BeatSize)
                {
                    case 1:
                        sysbus.WriteByte(channel.Destination, sysbus.ReadByte(channel.Source));
                        break;
                    case 2:
                        sysbus.WriteWord(channel.Destination, sysbus.ReadWord(channel.Source));
                        break;
                    default:
                        sysbus.WriteDoubleWord(channel.Destination, sysbus.ReadDoubleWord(channel.Source));
                        break;
                }
                channel.Source += (ulong)channel.SourceStep;
                channel.Destination += (ulong)channel.DestinationStep;
                channel.Remaining--;
            }
            WriteBack(index);
            if(channel.Remaining == 0)
            {
                BlockDone(index);
            }
        }

        private void BlockDone(int index)
        {
            var channel = channels[index];
            var blockAction = (channel.BtCtrl >> 3) & 0x3;
            if((blockAction & BlockActInt) != 0)
            {
                channel.IntFlag |= IntTcmpl;
            }
            if((blockAction & BlockActSuspend) != 0)
            {
                channel.IntFlag |= IntSusp;
            }
            if(channel.NextDescriptor == 0)
            {
                Disable(index);
            }
            else
            {
                channel.Descriptor = -1;        // load the linked descriptor on the next trigger
            }
            UpdateInterrupts();
        }

        private void Disable(int index)
        {
            var channel = channels[index];
            channel.Registers[0] &= unchecked((byte)~ChCtrlAEnable);
            channel.Descriptor = -1;
            channel.NextDescriptor = 0;
            UpdateInterrupts();
        }

        // The write-back section holds the descriptor as the channel has it now, with the beats remaining
        private void WriteBack(int index)
        {
            var channel = channels[index];
            var address = WriteBackAddress + (ulong)(index * DescriptorSize);
            if(WriteBackAddress == 0)
            {
                return;
            }
            sysbus.WriteWord(address, (ushort)(channel.Remaining > 0 ? channel.BtCtrl : channel.BtCtrl & ~BtCtrlValid));
            sysbus.WriteWord(address + 2, (ushort)channel.Remaining);
            sysbus.WriteDoubleWord(address + 4, (uint)(channel.Source + (ulong)(channel.SourceStep * channel.Remaining)));
            sysbus.WriteDoubleWord(address + 8, (uint)(channel.Destination + (ulong)(channel.DestinationStep * channel.Remaining)));
            sysbus.WriteDoubleWord(address + 12, (uint)channel.NextDescriptor);
        }

        private int LowestPendingChannel()
        {
            for(var i = 0; i < channels.Length && i < 32; i++)
            {
                if((channels[i].IntFlag & channels[i].IntEnable) != 0)
                {
                    return i;
                }
            }
            return -1;
        }

        private void UpdateInterrupts()
        {
            var lines = new bool[NumIrqs];
            for(var i = 0; i < channels.Length; i++)
            {
                if((channels[i].IntFlag & channels[i].IntEnable) != 0)
                {
                    lines[Math.Min(i, NumIrqs - 1)] = true;
                }
            }
            for(var i = 0; i < NumIrqs; i++)
            {
                Connections[i].Set(lines[i]);
            }
        }

        private void CrcWord(uint value, int width)
        {
            if(!CrcIs32)
            {
                this.Log(LogLevel.Warning, "Only the CRC-32 polynomial is modelled");
            }
            for(var i = 0; i < width; i++)
            {
                crc ^= (value >> (8 * i)) & 0xFF;
                for(var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ Crc32Reflected : crc >> 1;
                }
            }
        }

        private bool CrcIs32 => ((storage[CrcCtrl] >> 2) & 0x3) == 1;       // CRCCTRL.CRCPOLY

        private ulong BaseAddress => (ulong)(storage[BaseAddr] | storage[BaseAddr + 1] << 8 | storage[BaseAddr + 2] << 16 | storage[BaseAddr + 3] << 24);
        private ulong WriteBackAddress => (ulong)(storage[WrbAddr] | storage[WrbAddr + 1] << 8 | storage[WrbAddr + 2] << 16 | storage[WrbAddr + 3] << 24);

        private readonly IBusController sysbus;
        private readonly Channel[] channels;
        private readonly byte[] storage = new byte[ChannelBase];
        private readonly Queue<int> pending = new Queue<int>();
        private bool processing;
        private uint crc;

        private const int NumIrqs = 5;
        private const long ChannelBase = 0x40;
        private const long ChannelSize = 0x10;
        private const int DescriptorSize = 16;
        private const long Ctrl = 0x00;
        private const long CrcCtrl = 0x02;
        private const long CrcDataIn = 0x04;
        private const long CrcChkSum = 0x08;
        private const long CrcStatus = 0x0C;
        private const long SwTrigCtrl = 0x10;
        private const long IntPend = 0x20;
        private const long IntStatus = 0x24;
        private const long BusyCh = 0x28;
        private const long PendCh = 0x2C;
        private const long Active = 0x30;
        private const long BaseAddr = 0x34;
        private const long WrbAddr = 0x38;
        private const long ChCtrlB = 0x04;
        private const long ChIntEnClr = 0x0C;
        private const long ChIntEnSet = 0x0D;
        private const long ChIntFlag = 0x0E;
        private const long ChStatus = 0x0F;
        private const uint CtrlSwRst = 0x1;
        private const byte ChCtrlASwRst = 0x1;
        private const byte ChCtrlAEnable = 0x2;
        private const byte IntTerr = 0x1;
        private const byte IntTcmpl = 0x2;
        private const byte IntSusp = 0x4;
        private const uint IntPendIdMask = 0x1F;
        private const int IntPendFlagsShift = 8;          // TERR, TCMPL and SUSP, in CHINTFLAG's order
        private const uint IntPendFlags = IntTerr | IntTcmpl | IntSusp;
        private const ushort BtCtrlValid = 0x1;
        private const ushort BtCtrlSrcInc = 1 << 10;
        private const ushort BtCtrlDstInc = 1 << 11;
        private const ushort BtCtrlStepSel = 1 << 12;
        private const int BlockActInt = 1;
        private const int BlockActSuspend = 2;
        private const int TrigActBlock = 0;
        private const int TrigActTransaction = 3;
        private const uint Crc32Reflected = 0xEDB88320;

        private class Channel
        {
            public void Reset()
            {
                Array.Clear(Registers, 0, Registers.Length);
                IntEnable = 0;
                IntFlag = 0;
                Descriptor = -1;
                NextDescriptor = 0;
                Remaining = 0;
            }

            public bool Enabled => (Registers[0] & ChCtrlAEnable) != 0;
            public int TriggerSource => Registers[1] & 0x7F;               // CHCTRLA.TRIGSRC, bits 8-14

            public readonly byte[] Registers = new byte[ChannelSize];
            public byte IntEnable;
            public byte IntFlag;
            public long Descriptor;
            public ulong NextDescriptor;
            public ushort BtCtrl;
            public int Remaining;
            public int BeatSize;
            public int SourceStep;
            public int DestinationStep;
            public ulong Source;
            public ulong Destination;
        }
    }
}
