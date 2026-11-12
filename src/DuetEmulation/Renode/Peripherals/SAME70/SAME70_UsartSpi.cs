//
// USART1 in SPI master mode, with the TMC5160 stepper driver chain behind it.
//
// On a Duet 3 MB6HC the six TMC5160s are not on a SPI controller at all: Pins_Duet3_MB6HC.h sets
// TMC_USES_USART and drives them from USART1 in SPI mode. Renode's stock UART.SAM_USART models the
// USART as a UART, so every driver read returned nothing and RepRapFirmware ran entirely on the
// shadow copies of the registers it had written - M569 looked perfectly healthy while no byte had
// ever come back from a driver.
//
// The drivers are modelled here rather than as separate Renode peripherals because they are not
// separately addressable: they form one daisy chain that is electrically a single 5*N byte shift
// register, and splitting it would mean inventing a bus that the hardware does not have.
//
// WHY THE ONE-FRAME DELAY FALLS OUT CORRECTLY. A TMC5160 returns the data of the register requested
// in the *previous* datagram, not the current one. RepRapFirmware's SetupDMA enables the receive
// channel before the transmit channel, and SAME70_Xdmac performs each channel's copy immediately on
// enable - so a frame's receive drains bytes that were computed at the end of the previous frame's
// transmit. That is exactly the hardware's behaviour, so the emulator's inability to interleave the
// two DMA channels costs nothing here. It would be wrong for a full-duplex device that answers
// within the same frame; this one does not.
//
// NOT MODELLED, deliberately: motion. DRV_STATUS always reports standstill and never a stall, open
// load or over-temperature, because this model cannot see the step pins. Driver status read back
// from here therefore says "a healthy driver that is not moving" and must not be read as evidence
// about anything the motion system is doing.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SPI
{
    public class SAME70_UsartSpi : IDoubleWordPeripheral, IBytePeripheral, IKnownSize
    {
        // Declared because the platform wires USART1's interrupt to the NVIC. It is never raised: the
        // TMC transfer completes through XDMAC, whose interrupt is what the firmware actually waits on.
        public GPIO IRQ { get; } = new GPIO();

        public SAME70_UsartSpi(IMachine machine, int numberOfDrivers = 6)
        {
            this.machine = machine;
            drivers = new Tmc5160[numberOfDrivers];
            for(var i = 0; i < numberOfDrivers; i++)
            {
                drivers[i] = new Tmc5160();
            }
            frameBytes = numberOfDrivers * BytesPerDatagram;
            Reset();
        }

        public void Reset()
        {
            outgoing.Clear();
            incoming.Clear();
            foreach(var driver in drivers)
            {
                driver.Reset();
            }
        }

        public long Size => 0x100;

        // Queryable from the Renode monitor so a probe script can assert that the driver chain was
        // actually talked to, rather than inferring it from firmware output.
        public int FrameCount => frameCount;
        public int BytesWritten => bytesWritten;

        public byte ReadByte(long offset)
        {
            // The DMA reads the receive holding register a byte at a time, which is why this model has
            // to answer byte accesses at all - a doubleword-only peripheral silently returns nothing.
            return (offset == ReceiveHolding) ? NextResponseByte() : (byte)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 3) * 8));
        }

        public void WriteByte(long offset, byte value)
        {
            if(offset == TransmitHolding)
            {
                ShiftIn(value);
                return;
            }
            WriteDoubleWord(offset & ~3L, value);
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
                case ChannelStatus:
                    // Held permanently ready. The firmware polls TXRDY/TXEMPTY before handing over to
                    // the DMA; a status that is ever not-ready would stall the TMC task rather than
                    // produce a visible error.
                    return TxReady | TxEmpty | (uint)(outgoing.Count > 0 ? RxReady : 0);
                case ReceiveHolding:
                    return NextResponseByte();
                case Mode:
                    return mode;
                case InterruptMask:
                    return interruptMask;
                default:
                    return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
                case TransmitHolding:
                    ShiftIn((byte)value);
                    break;
                case Mode:
                    mode = value;
                    break;
                case InterruptEnable:
                    interruptMask |= value;
                    break;
                case InterruptDisable:
                    interruptMask &= ~value;
                    break;
                default:
                    break;
            }
        }

        private byte NextResponseByte()
        {
            if(outgoing.Count == 0)
            {
                // Before the first complete frame there is genuinely nothing to return, which is what
                // a real chain does on the very first transfer too.
                return 0;
            }
            return outgoing.Dequeue();
        }

        private void ShiftIn(byte value)
        {
            bytesWritten++;
            incoming.Add(value);
            if(incoming.Count < frameBytes)
            {
                return;
            }

            // A complete frame: datagram j belongs to driver j, and its reply is what the next frame's
            // receive will drain. Byte 0 is the address with bit 7 set for a write; bytes 1-4 are the
            // payload, big endian.
            var reply = new byte[frameBytes];
            for(var j = 0; j < drivers.Length; j++)
            {
                var at = j * BytesPerDatagram;
                var address = incoming[at];
                var payload = ((uint)incoming[at + 1] << 24) | ((uint)incoming[at + 2] << 16)
                            | ((uint)incoming[at + 3] << 8) | incoming[at + 4];

                var value32 = drivers[j].Exchange(address, payload);
                reply[at] = drivers[j].StatusByte;
                reply[at + 1] = (byte)(value32 >> 24);
                reply[at + 2] = (byte)(value32 >> 16);
                reply[at + 3] = (byte)(value32 >> 8);
                reply[at + 4] = (byte)value32;
            }

            this.Log(LogLevel.Debug, "frame {0}: d0 addr={1:X2} -> {2:X2}{3:X2}{4:X2}{5:X2}{6:X2}",
                     ++frameCount, incoming[0], reply[0], reply[1], reply[2], reply[3], reply[4]);
            incoming.Clear();
            outgoing.Clear();
            foreach(var b in reply)
            {
                outgoing.Enqueue(b);
            }
        }

        private class Tmc5160
        {
            public void Reset()
            {
                Array.Clear(registers, 0, registers.Length);
                // GSTAT's reset flag is set after power-up and clears on read; RepRapFirmware reads
                // GSTAT on every cycle, so a flag that never cleared would be reported forever.
                registers[RegGstat] = 1;
                lastReadAddress = 0;
            }

            // Returns the value to send back in this datagram, which the hardware defines as the
            // contents of the register named by this same datagram when it is a read.
            public uint Exchange(byte address, uint payload)
            {
                var register = address & 0x7F;
                if((address & 0x80) != 0)
                {
                    registers[register] = payload;
                    return Read(lastReadAddress);
                }
                lastReadAddress = register;
                return Read(register);
            }

            public byte StatusByte => Standstill;

            private uint Read(int register)
            {
                switch(register)
                {
                    case RegGstat:
                    {
                        var value = registers[RegGstat];
                        registers[RegGstat] = 0;                        // clear on read
                        return value;
                    }
                    case RegIoin:
                        return VersionTmc5160 << 24;
                    case RegDrvStatus:
                        // Standstill, and CS_ACTUAL echoing the run current the firmware programmed so
                        // that the value is at least self-consistent with IHOLD_IRUN.
                        return DrvStatusStandstill | ((registers[RegIholdIrun] >> 8) & 0x1F) << 16;
                    case RegMscnt:
                        return 0;
                    case RegPwmScale:
                        return 0;
                    case RegPwmAuto:
                        return 0;
                    default:
                        return registers[register];
                }
            }

            private int lastReadAddress;
            private readonly uint[] registers = new uint[128];

            private const int RegGstat = 0x01;
            private const int RegIoin = 0x04;
            private const int RegIholdIrun = 0x10;
            private const int RegMscnt = 0x6A;
            private const int RegDrvStatus = 0x6F;
            private const int RegPwmScale = 0x71;
            private const int RegPwmAuto = 0x72;

            private const uint VersionTmc5160 = 0x30;
            private const uint DrvStatusStandstill = 1u << 31;
            private const byte Standstill = 1 << 3;
        }

        private int frameCount;
        private int bytesWritten;
        private uint mode;
        private uint interruptMask;
        private readonly int frameBytes;
        private readonly Tmc5160[] drivers;
        private readonly IMachine machine;
        private readonly List<byte> incoming = new List<byte>();
        private readonly Queue<byte> outgoing = new Queue<byte>();

        private const int BytesPerDatagram = 5;

        private const long Mode = 0x04;
        private const long InterruptEnable = 0x08;
        private const long InterruptDisable = 0x0C;
        private const long InterruptMask = 0x10;
        private const long ChannelStatus = 0x14;
        private const long ReceiveHolding = 0x18;
        private const long TransmitHolding = 0x1C;

        private const uint RxReady = 1u << 0;
        private const uint TxReady = 1u << 1;
        private const uint TxEmpty = 1u << 9;
    }
}
