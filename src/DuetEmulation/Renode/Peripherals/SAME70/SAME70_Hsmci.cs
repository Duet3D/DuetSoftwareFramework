//
// SAME70 High Speed MultiMedia Card Interface (HSMCI).
//
// Renode's SDCard already implements the card side - command handling, the register set, and the
// backing image - so this is only the controller: SAME70 registers on one side, SDCard.HandleCommand
// / ReadData / WriteData on the other. Attach a card with:
//
//     machine SdCardFromFile @sdcard.img hsmci 0x8000000 false "sd"
//
// Data does not move through this peripheral by itself. RepRapFirmware configures XDMAC with the
// source fixed at the FIFO aperture (hsmci_start_read_blocks in CoreN2G's ASF driver) and lets DMA
// drain it, so reads here pop from a buffer filled when the command was issued. Transfers complete
// instantly; nothing models the card being slow.
//
using System;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.SD;

namespace Antmicro.Renode.Peripherals.SD
{
    public class SAME70_Hsmci : NullRegistrationPointPeripheralContainer<SDCard>, IDoubleWordPeripheral, IKnownSize
    {
        public SAME70_Hsmci(IMachine machine) : base(machine)
        {
            readBuffer = new Queue<byte>();
            writeBuffer = new List<byte>();
            response = new uint[4];
            IRQ = new GPIO();
            Reset();
        }

        public override void Reset()
        {
            RegisteredPeripheral?.Reset();
            readBuffer.Clear();
            writeBuffer.Clear();
            Array.Clear(response, 0, response.Length);
            responseIndex = 0;
            mode = 0;
            blockRegister = 0;
            argument = 0;
            interruptMask = 0;
            expectedWriteLength = 0;
            IRQ.Set(false);
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset >= FifoBase)
            {
                return PopWord();
            }

            switch(offset)
            {
                case RegisterMr: return mode;
                case RegisterArgr: return argument;
                case RegisterBlkr: return blockRegister;
                case RegisterRspr0:
                case RegisterRspr1:
                case RegisterRspr2:
                case RegisterRspr3:
                {
                    // The response registers are a FIFO, not four addressable words: the hardware
                    // auto-increments an internal pointer on each read. hsmci_get_response_128 relies
                    // on this and reads RSPR[0] four times to collect a 136-bit response. Returning
                    // response[0] every time yields a CSD of one word repeated, which decodes to a
                    // capacity of zero - and then disk_read rejects every sector, including sector 0,
                    // so not a single block read is ever issued.
                    var value = response[responseIndex & 0x3];
                    responseIndex++;
                    return value;
                }
                case RegisterRdr: return PopWord();
                case RegisterSr: return BuildStatus();
                case RegisterImr: return interruptMask;
                default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset >= FifoBase)
            {
                PushWord(value);
                return;
            }

            switch(offset)
            {
                case RegisterCr:
                    if((value & ControlSoftwareReset) != 0)
                    {
                        Reset();
                    }
                    break;
                case RegisterMr: mode = value; break;
                case RegisterArgr: argument = value; break;
                case RegisterBlkr: blockRegister = value; break;
                case RegisterCmdr: ExecuteCommand(value); break;
                case RegisterTdr: PushWord(value); break;
                case RegisterIer: interruptMask |= value; break;
                case RegisterIdr: interruptMask &= ~value; break;
                default: break;
            }
        }

        public long Size => 0x600;      // registers, then the FIFO aperture at 0x200

        public GPIO IRQ { get; }

        private void ExecuteCommand(uint command)
        {
            var card = RegisteredPeripheral;
            if(card == null)
            {
                this.WarningLog("Command 0x{0:X} issued with no card attached", command & CommandIndexMask);
                return;
            }

            var index = command & CommandIndexMask;
            responseIndex = 0;
            var result = card.HandleCommand(index, argument);

            // CMD8 (SEND_IF_COND) must come back as R7: the low 12 bits of the argument echoed, being
            // the accepted voltage range and the check pattern. Renode's SDCard only builds that in SPI
            // mode - in native mode it returns CardStatus - so a v2 host sees no echo, decides the card
            // did not understand CMD8, and restarts the identification sequence forever. Synthesise it
            // here, which is what the card on the other end of a real HSMCI would have sent.
            if(index == CommandSendInterfaceCondition && !card.TreatNextCommandAsAppCommand)
            {
                response[0] = argument & 0xFFF;
                UpdateInterrupt();
                return;
            }

            // Response ordering follows the SD frame: RSPR0 holds the most significant word of a long
            // response, and the low bit of the last word is always zero.
            switch((command >> ResponseTypeShift) & 0x3)
            {
                case ResponseType48Bit:
                    response[0] = result.AsUInt32(0);
                    break;
                case ResponseType136Bit:
                    response[0] = result.AsUInt32(96);
                    response[1] = result.AsUInt32(64);
                    response[2] = result.AsUInt32(32);
                    response[3] = result.AsUInt32(0) & 0xFFFFFFFE;
                    break;
                default:
                    break;
            }

            this.Log(LogLevel.Info, "CMD{0} arg=0x{1:X} rsp={2:X8} {3:X8} {4:X8} {5:X8}",
                     index, argument, response[0], response[1], response[2], response[3]);

            if(((command >> TransferCommandShift) & 0x3) == TransferStart)
            {
                var blockSize = (blockRegister >> BlockSizeShift) & 0xFFFF;
                var blockCount = blockRegister & 0xFFFF;
                if(blockCount == 0)
                {
                    blockCount = 1;
                }
                var length = blockSize * blockCount;

                if((command & TransferDirectionRead) != 0)
                {
                    readBuffer.Clear();
                    var data = card.ReadData(length);
                    foreach(var b in data)
                    {
                        readBuffer.Enqueue(b);
                    }
                    this.Log(LogLevel.Info, "READ cmd{0} blk={1}x{2} got={3} first={4:X2}{5:X2}{6:X2}{7:X2}",
                             index, blockCount, blockSize, data.Length,
                             data.Length > 0 ? data[0] : 0, data.Length > 1 ? data[1] : 0,
                             data.Length > 2 ? data[2] : 0, data.Length > 3 ? data[3] : 0);
                }
                else
                {
                    writeBuffer.Clear();
                    expectedWriteLength = length;
                }
            }
            UpdateInterrupt();
        }

        private uint PopWord()
        {
            uint value = 0;
            for(var i = 0; i < 4; i++)
            {
                if(readBuffer.Count == 0)
                {
                    break;
                }
                value |= (uint)readBuffer.Dequeue() << (8 * i);
            }
            UpdateInterrupt();
            return value;
        }

        private void PushWord(uint value)
        {
            for(var i = 0; i < 4; i++)
            {
                writeBuffer.Add((byte)(value >> (8 * i)));
            }
            if(expectedWriteLength != 0 && writeBuffer.Count >= expectedWriteLength)
            {
                RegisteredPeripheral?.WriteData(writeBuffer.ToArray());
                writeBuffer.Clear();
                expectedWriteLength = 0;
            }
            UpdateInterrupt();
        }

        private uint BuildStatus()
        {
            // Commands complete instantly, so the ready bits are permanently set. The transfer-done
            // bits follow the buffers, which is what the driver's wait loops actually test.
            var status = StatusCommandReady | StatusTransmitReady | StatusNotBusy;
            if(readBuffer.Count != 0)
            {
                status |= StatusReceiveReady;
            }
            else
            {
                status |= StatusFifoEmpty | StatusBlockEnded | StatusTransferDone;
            }
            if(expectedWriteLength == 0)
            {
                status |= StatusBlockEnded | StatusTransferDone;
            }
            return status;
        }

        private void UpdateInterrupt()
        {
            IRQ.Set((BuildStatus() & interruptMask) != 0);
        }

        private readonly Queue<byte> readBuffer;
        private readonly List<byte> writeBuffer;
        private readonly uint[] response;
        private uint mode;
        private uint blockRegister;
        private uint argument;
        private uint interruptMask;
        private int responseIndex;
        private uint expectedWriteLength;

        private const long RegisterCr = 0x00;
        private const long RegisterMr = 0x04;
        private const long RegisterArgr = 0x10;
        private const long RegisterCmdr = 0x14;
        private const long RegisterBlkr = 0x18;
        private const long RegisterRspr0 = 0x20;
        private const long RegisterRspr1 = 0x24;
        private const long RegisterRspr2 = 0x28;
        private const long RegisterRspr3 = 0x2C;
        private const long RegisterRdr = 0x30;
        private const long RegisterTdr = 0x34;
        private const long RegisterSr = 0x40;
        private const long RegisterIer = 0x44;
        private const long RegisterIdr = 0x48;
        private const long RegisterImr = 0x4C;
        private const long FifoBase = 0x200;

        private const uint ControlSoftwareReset = 1u << 7;

        private const uint CommandIndexMask = 0x3F;
        private const uint CommandSendInterfaceCondition = 8;
        private const int ResponseTypeShift = 6;
        private const uint ResponseType48Bit = 1;
        private const uint ResponseType136Bit = 2;
        private const int TransferCommandShift = 16;
        private const uint TransferStart = 1;
        private const uint TransferDirectionRead = 1u << 18;
        private const int BlockSizeShift = 16;

        private const uint StatusCommandReady = 1u << 0;
        private const uint StatusReceiveReady = 1u << 1;
        private const uint StatusTransmitReady = 1u << 2;
        private const uint StatusBlockEnded = 1u << 3;
        private const uint StatusNotBusy = 1u << 5;
        private const uint StatusFifoEmpty = 1u << 26;
        private const uint StatusTransferDone = 1u << 27;
    }
}
