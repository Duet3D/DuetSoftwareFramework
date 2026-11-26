//
// SAME70 SPI in slave mode, as DuetCANMaster drives it for its SBC interface (SBC/DataTransfer.cpp),
// and the SBC link that carries its bus to DuetControlServer: the other end of DuetRealtimeCore's
// emulator link (Hardware/Emulator/EmulatorLinkFormats.h, which this mirrors).
//
// The SPI: both directions are moved by XDMAC channels paced by the SPI's requests. The transmitter is
// a holding register (TDR) in front of a shift register, both filled by the TX channel as soon as they
// are empty, so that TDRE stays clear until the master starts clocking. Each byte the master clocks
// shifts one out and one in; the received byte goes to RDR for the RX channel. Releasing the chip
// select raises NSSR, which is how the firmware learns that a transfer has ended. Master mode and
// interrupt-driven transfers without DMA are not modelled.
//
// The link: a Unix domain socket server that a monitor command opens. The bench that drives the
// emulation keeps it in lockstep with the SBC (EmulatorLinkFormats.h): between runs of emulated time
// it calls Sync, which waits until the connection and the Exchanges the SBC says it has sent have
// arrived, applies them in order (a new connection is told the lines' levels; an Exchange is one
// chip-select cycle), and returns how many frames have been sent to the SBC, so the bench can wait
// for them in turn. The controller's TfrRdy and DataAvailable outputs (PIO pins connected to this
// model's GPIO inputs 0 and 1) go back as LineLevel frames, each stamped with the emulated time. The
// cycle runs at once, but the firmware drops TfrRdy from its end-of-transfer interrupt, after it; on
// hardware TfrRdy is low before the master has its bytes back, so the result is held until it is.
//
//     spi1: SPI.SAME70_SbcSpi @ sysbus 0x40058000
//         dma: xdmac
//         txPeripheralId: 3
//         rxPeripheralId: 4
//         chipSelect: pioc
//         chipSelectPin: 25
//         IRQ -> nvic@42
//     pioe:
//         2 -> spi1@0          // TfrRdy
//         1 -> spi1@1          // DataAvailable
//
//     spi1 ListenForSbc "/path/to/sbc.sock"
//     spi1 Sync 1 42      // after the SBC's first connection and 42 Exchanges on it; prints the frames sent
//
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.SPI
{
    public class SAME70_SbcSpi : IBytePeripheral, IDoubleWordPeripheral, IKnownSize, IGPIOReceiver, IDisposable
    {
        public SAME70_SbcSpi(IMachine machine, SAME70_Xdmac dma, int txPeripheralId, int rxPeripheralId,
                             SAME70_ParallelIO chipSelect, int chipSelectPin)
        {
            this.machine = machine;
            this.dma = dma;
            this.txPeripheralId = txPeripheralId;
            this.rxPeripheralId = rxPeripheralId;
            this.chipSelect = chipSelect;
            this.chipSelectPin = chipSelectPin;
            IRQ = new GPIO();
            dma.RegisterRequestSource(txPeripheralId, FillTransmitter);
            dma.RegisterRequestSource(rxPeripheralId, () => { });
            Reset();
        }

        public void Reset()
        {
            enabled = false;
            interruptMask = 0;
            status = 0;
            holding = null;
            shifting = null;
            received = 0;
            chipSelect.Drive(chipSelectPin, true);
            UpdateInterrupt();
        }

        public GPIO IRQ { get; }

        public long Size => 0x100;

        // TfrRdy (0) and DataAvailable (1) from the controller's PIO
        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= NumLines)
            {
                return;
            }
            lineLevels[number] = value;
            this.Log(LogLevel.Debug, "Line {0} {1}", number == 0 ? "TfrRdy" : "DataAvailable", value ? "high" : "low");
            Send(FrameLineLevel, 0, new byte[] { (byte)number, (byte)(value ? 1 : 0) });
            if(number == LineTransferReady && !value)
            {
                SendHeldResult();
            }
        }

        // Accept the SBC's connection on a Unix domain socket at `path`, one connection at a time
        public void ListenForSbc(string path)
        {
            if(listener != null)
            {
                throw new Exceptions.RecoverableException("Already listening for the SBC");
            }
            File.Delete(path);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            new Thread(AcceptLoop) { IsBackground = true, Name = "SBC link" }.Start();
        }

        // Wait until the SBC's `connections`th connection has arrived with `exchanges` Exchanges on it,
        // apply everything that has arrived, and return the frames sent to the SBC on its connection.
        // Called between runs of emulated time, so what is applied happens at the same emulated time
        // on every run.
        public int Sync(int connections, int exchanges)
        {
            List<InboxItem> items;
            lock(connectionLock)
            {
                var deadline = DateTime.UtcNow + SyncTimeout;
                while(connectionsAccepted < connections || (connectionsAccepted == connections && exchangesReceived < exchanges))
                {
                    var left = deadline - DateTime.UtcNow;
                    if(left <= TimeSpan.Zero)
                    {
                        throw new Exceptions.RecoverableException(
                            $"The SBC's connection {connections} and {exchanges} exchanges did not arrive (have connection {connectionsAccepted}, {exchangesReceived} exchanges)");
                    }
                    Monitor.Wait(connectionLock, left);
                }
                items = new List<InboxItem>(inbox);
                inbox.Clear();
            }

            foreach(var item in items)
            {
                lock(connectionLock)
                {
                    // What arrived on a connection that has since closed is not applied
                    if(item.Generation != generation || connection == null)
                    {
                        continue;
                    }
                }
                if(item.Mosi == null)
                {
                    for(var line = 0; line < NumLines; line++)
                    {
                        Send(FrameLineLevel, 0, new byte[] { (byte)line, (byte)(lineLevels[line] ? 1 : 0) });
                    }
                    continue;
                }
                var miso = Exchange(item.Mosi);
                if(!lineLevels[LineTransferReady])
                {
                    Send(FrameExchangeResult, item.Sequence, miso);
                    continue;
                }
                heldResult = Tuple.Create(item.Sequence, miso);
                machine.ScheduleAction(TimeInterval.FromMilliseconds(MaxResultHoldMs), __ => SendHeldResult());
            }

            lock(connectionLock)
            {
                return framesSent;
            }
        }

        public void Dispose()
        {
            listener?.Dispose();
            lock(connectionLock)
            {
                connection?.Dispose();
            }
        }

        public byte ReadByte(long offset) => (byte)(ReadDoubleWord(offset & ~3L) >> (int)(8 * (offset & 3)));

        public void WriteByte(long offset, byte value)
        {
            // Only TDR takes byte writes, from the TX channel
            if((offset & ~3L) == Tdr)
            {
                WriteDoubleWord(Tdr, value);
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
                case Rdr:
                    status &= ~StatusRdrf;
                    UpdateInterrupt();
                    return received;
                case Sr:
                {
                    var value = Status;
                    status &= ~(StatusModf | StatusOvres | StatusNssr | StatusUndes);       // cleared by reading
                    UpdateInterrupt();
                    return value;
                }
                case Imr:
                    return interruptMask;
                case Mr:
                    return modeRegister;
                default:
                    return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
                case Cr:
                    if((value & ControlSwrst) != 0)
                    {
                        Reset();
                    }
                    if((value & ControlSpidis) != 0)
                    {
                        enabled = false;
                    }
                    else if((value & ControlSpien) != 0)
                    {
                        enabled = true;
                        FillTransmitter();
                    }
                    break;
                case Mr:
                    modeRegister = value;
                    break;
                case Tdr:
                    holding = (byte)value;
                    break;
                case Ier:
                    interruptMask |= value;
                    break;
                case Idr:
                    interruptMask &= ~value;
                    break;
                default:
                    break;
            }
            UpdateInterrupt();
        }

        // TDRE: the holding register is empty
        private uint Status => status | (enabled ? StatusSpiens : 0) | (enabled && holding == null ? StatusTdre : 0) |
                               (holding == null && shifting == null ? StatusTxempty : 0);

        // One chip-select cycle: the master clocks `mosi` out and the slave's bytes back
        private byte[] Exchange(byte[] mosi)
        {
            var miso = new byte[mosi.Length];
            chipSelect.Drive(chipSelectPin, false);
            for(var i = 0; i < mosi.Length; i++)
            {
                if(!enabled)
                {
                    miso[i] = 0;
                    continue;
                }
                if(shifting is byte outgoing)
                {
                    miso[i] = outgoing;
                }
                else
                {
                    status |= StatusUndes;
                }
                shifting = null;
                if((status & StatusRdrf) != 0)
                {
                    status |= StatusOvres;
                }
                received = mosi[i];
                status |= StatusRdrf;
                dma.Request(rxPeripheralId);
                FillTransmitter();
            }
            chipSelect.Drive(chipSelectPin, true);
            status |= StatusNssr;
            UpdateInterrupt();
            this.Log(LogLevel.Debug, "Exchange of {0} bytes{1}: MOSI {2}, MISO {3}", mosi.Length, (status & StatusUndes) != 0 ? " with underrun" : string.Empty,
                     Hex(mosi), Hex(miso));
            return miso;
        }

        private static string Hex(byte[] data) => BitConverter.ToString(data, 0, Math.Min(data.Length, 24)) + (data.Length > 24 ? "..." : string.Empty);

        // The TX channel fills the holding register whenever it is empty, and the holding register
        // passes its byte on to the shift register whenever that is empty
        private void FillTransmitter()
        {
            while(enabled)
            {
                if(holding == null && !dma.Request(txPeripheralId))
                {
                    break;
                }
                if(shifting != null || holding == null)
                {
                    break;
                }
                shifting = holding;
                holding = null;
            }
        }

        private void UpdateInterrupt()
        {
            IRQ.Set((Status & interruptMask) != 0);
        }

        // --- The SBC link ---

        private void AcceptLoop()
        {
            while(true)
            {
                Socket accepted;
                try
                {
                    accepted = listener.Accept();
                }
                catch(Exception e) when(e is SocketException || e is ObjectDisposedException)
                {
                    return;
                }
                lock(connectionLock)
                {
                    connection?.Dispose();
                    connection = accepted;
                    generation++;
                    connectionsAccepted++;
                    exchangesReceived = 0;
                    framesSent = 0;
                    outgoing = new BlockingCollection<byte[]>();
                    var queue = outgoing;
                    new Thread(() => WriteLoop(accepted, queue)) { IsBackground = true, Name = "SBC link writer" }.Start();
                    // The SBC learns the lines' levels when Sync applies its connection
                    inbox.Add(new InboxItem(generation, 0, null));
                    Monitor.PulseAll(connectionLock);
                }
                ReadLoop(accepted);
            }
        }

        private void ReadLoop(Socket socket)
        {
            int ourGeneration;
            lock(connectionLock)
            {
                ourGeneration = generation;
            }
            var header = new byte[HeaderSize];
            while(ReadExact(socket, header))
            {
                var type = header[0];
                var sequence = BitConverter.ToUInt16(header, 2);
                var length = BitConverter.ToUInt32(header, 4);
                if(type != FrameExchange || length > MaxExchangeLength)
                {
                    this.Log(LogLevel.Error, "SBC link: unexpected frame type {0} of {1} bytes", type, length);
                    break;
                }
                var mosi = new byte[length];
                if(!ReadExact(socket, mosi))
                {
                    break;
                }
                lock(connectionLock)
                {
                    if(generation == ourGeneration)
                    {
                        inbox.Add(new InboxItem(ourGeneration, sequence, mosi));
                        exchangesReceived++;
                        Monitor.PulseAll(connectionLock);
                    }
                }
            }
            lock(connectionLock)
            {
                if(connection == socket)
                {
                    outgoing.CompleteAdding();
                    connection = null;
                }
            }
            socket.Dispose();
        }

        // Send the result of the last exchange, if it is still held
        private void SendHeldResult()
        {
            var held = heldResult;
            heldResult = null;
            if(held != null)
            {
                Send(FrameExchangeResult, held.Item1, held.Item2);
            }
        }

        private static bool ReadExact(Socket socket, byte[] buffer)
        {
            var read = 0;
            try
            {
                while(read < buffer.Length)
                {
                    var n = socket.Receive(buffer, read, buffer.Length - read, SocketFlags.None);
                    if(n <= 0)
                    {
                        return false;
                    }
                    read += n;
                }
                return true;
            }
            catch(Exception e) when(e is SocketException || e is ObjectDisposedException)
            {
                return false;
            }
        }

        // Frames are written by a thread of their own, so that a slow SBC never holds up the emulation
        private static void WriteLoop(Socket socket, BlockingCollection<byte[]> queue)
        {
            try
            {
                foreach(var frame in queue.GetConsumingEnumerable())
                {
                    socket.Send(frame);
                }
            }
            catch(Exception e) when(e is SocketException || e is ObjectDisposedException)
            {
            }
        }

        private void Send(byte type, ushort sequence, byte[] payload)
        {
            var frame = new byte[HeaderSize + payload.Length];
            frame[0] = type;
            BitConverter.GetBytes(sequence).CopyTo(frame, 2);
            BitConverter.GetBytes((uint)payload.Length).CopyTo(frame, 4);
            BitConverter.GetBytes((ulong)(machine.ClockSource.CurrentValue.TotalMicroseconds * 1000)).CopyTo(frame, 8);
            payload.CopyTo(frame, HeaderSize);
            lock(connectionLock)
            {
                if(connection != null && !outgoing.IsAddingCompleted)
                {
                    outgoing.Add(frame);
                    framesSent++;
                }
            }
        }

        // An Exchange from the SBC, or its connection when Mosi is null, waiting for Sync
        private sealed class InboxItem
        {
            public InboxItem(int generation, ushort sequence, byte[] mosi)
            {
                Generation = generation;
                Sequence = sequence;
                Mosi = mosi;
            }

            public int Generation { get; }
            public ushort Sequence { get; }
            public byte[] Mosi { get; }
        }

        private readonly IMachine machine;
        private readonly SAME70_Xdmac dma;
        private readonly int txPeripheralId;
        private readonly int rxPeripheralId;
        private readonly SAME70_ParallelIO chipSelect;
        private readonly int chipSelectPin;
        private readonly bool[] lineLevels = new bool[NumLines];
        private readonly object connectionLock = new object();
        private readonly List<InboxItem> inbox = new List<InboxItem>();

        private bool enabled;
        private uint interruptMask;
        private uint status;
        private uint modeRegister;
        private byte? holding;
        private byte? shifting;
        private uint received;
        private Tuple<ushort, byte[]> heldResult;
        private int generation;
        private int connectionsAccepted;
        private int exchangesReceived;
        private int framesSent;
        private Socket listener;
        private Socket connection;
        private BlockingCollection<byte[]> outgoing;

        // The emulator link's wire format (EmulatorLinkFormats.h)
        private const int HeaderSize = 16;
        private const byte FrameExchange = 1;
        private const byte FrameExchangeResult = 2;
        private const byte FrameLineLevel = 3;
        private const int NumLines = 2;
        private const int LineTransferReady = 0;
        // How long a result waits for the firmware to drop TfrRdy before it is sent anyway
        private const int MaxResultHoldMs = 10;
        private const uint MaxExchangeLength = 65536;
        // How long Sync waits, in real time, for what the SBC has sent to arrive
        private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(10);

        private const long Cr = 0x00;
        private const long Mr = 0x04;
        private const long Rdr = 0x08;
        private const long Tdr = 0x0C;
        private const long Sr = 0x10;
        private const long Ier = 0x14;
        private const long Idr = 0x18;
        private const long Imr = 0x1C;

        private const uint ControlSpien = 1u << 0;
        private const uint ControlSpidis = 1u << 1;
        private const uint ControlSwrst = 1u << 7;

        private const uint StatusRdrf = 1u << 0;
        private const uint StatusTdre = 1u << 1;
        private const uint StatusModf = 1u << 2;
        private const uint StatusOvres = 1u << 3;
        private const uint StatusNssr = 1u << 8;
        private const uint StatusTxempty = 1u << 9;
        private const uint StatusUndes = 1u << 10;
        private const uint StatusSpiens = 1u << 16;
    }
}
