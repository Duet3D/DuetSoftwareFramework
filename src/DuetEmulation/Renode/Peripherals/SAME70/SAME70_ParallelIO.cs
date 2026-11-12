//
// SAME70 Parallel Input/Output Controller (PIO).
//
// Enough of it to hold pin state and, more importantly, to let us watch pins change. On the Duet 3
// MB6HC all six STEP pins are on PIOC and RepRapFirmware drives them by writing PIO_SODR and PIO_CODR
// directly (Config/Pins_Duet3_MB6HC.h, namespace StepPins), so a PIO model that tracks the output data
// register is what turns this emulator into something that can observe motion.
//
// Set TraceMask to the bits you care about and every edge on those pins is logged with the emulated
// timestamp, which post-processes into a step train. For PIOC that is 0x10050212: pins 18, 16, 28, 1,
// 4 and 9, being drivers 0 to 5.
//
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    public class SAME70_ParallelIO : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public SAME70_ParallelIO(IMachine machine)
        {
            this.machine = machine;

            var pins = new Dictionary<int, IGPIO>();
            for(var i = 0; i < NumPins; i++)
            {
                pins[i] = new GPIO();
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(pins);

            Reset();
        }

        public void Reset()
        {
            pioEnabled = 0xFFFFFFFF;    // after reset every pin is under PIO control
            outputEnabled = 0;
            outputData = 0;
            interruptMask = 0;
            multiDriveEnabled = 0;
            pullUpDisabled = 0;
            edgeCount = 0;
            UpdateConnections(0);
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
                case RegisterPsr:
                    return pioEnabled;
                case RegisterOsr:
                    return outputEnabled;
                case RegisterOdsr:
                    return outputData;
                case RegisterPdsr:
                {
                    // Output pins read back what we drive. Input pins read their pull-up, unless
                    // something outside the chip is holding them - which is how the emulator says a
                    // card is in the slot, a switch is closed, and so on. Without this every input
                    // reads high, and an active-low signal like SD card detect looks permanently
                    // inactive: the firmware then never talks to the card at all.
                    var inputs = ~outputEnabled;
                    return (outputData & outputEnabled)
                         | (inputs & drivenInputs & drivenLevels)
                         | (inputs & ~drivenInputs & ~pullUpDisabled);
                }
                case RegisterImr:
                    return interruptMask;
                case RegisterIsr:
                    return 0;           // clear-on-read, and we never raise any
                case RegisterMdsr:
                    return multiDriveEnabled;
                case RegisterPusr:
                    return pullUpDisabled;
                default:
                    return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
                case RegisterPer:
                    pioEnabled |= value;
                    break;
                case RegisterPdr:
                    pioEnabled &= ~value;
                    break;
                case RegisterOer:
                    outputEnabled |= value;
                    break;
                case RegisterOdr:
                    outputEnabled &= ~value;
                    break;
                case RegisterSodr:
                    SetOutputData(outputData | value);
                    break;
                case RegisterCodr:
                    SetOutputData(outputData & ~value);
                    break;
                case RegisterOdsr:
                    SetOutputData(value);
                    break;
                case RegisterIer:
                    interruptMask |= value;
                    break;
                case RegisterIdr:
                    interruptMask &= ~value;
                    break;
                case RegisterMder:
                    multiDriveEnabled |= value;
                    break;
                case RegisterMddr:
                    multiDriveEnabled &= ~value;
                    break;
                case RegisterPuer:
                    pullUpDisabled &= ~value;
                    break;
                case RegisterPudr:
                    pullUpDisabled |= value;
                    break;
                default:
                    break;
            }
        }

        public long Size => 0x200;

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // Pins held by something outside the chip, and the levels they are held at. Set both from the
        // platform file, e.g. DrivenInputs: 0x20000000 with DrivenLevels: 0 to pull one pin low.
        public uint DrivenInputs { get; set; }
        public uint DrivenLevels { get; set; }

        // Bits to log edges for. Zero means log nothing, which is the sane default: PIO traffic is
        // heavy and tracing all of it would swamp the log.
        public uint TraceMask { get; set; }

        // Emit a timestamped marker into the same log as the pin traces, so a test can measure the
        // delay between issuing a command and the step train reacting to it. Both use the same clock,
        // which is the point - correlating two different time bases is how latency numbers go wrong.
        public void Mark(string label)
        {
            this.Log(LogLevel.Info, "{0} mark {1}", machine.ElapsedVirtualTime.TimeElapsed.TotalMicroseconds, label);
        }

        // Number of traced edges seen. Cheap way for a test to assert that a move actually stepped.
        public ulong EdgeCount => edgeCount;

        // Current output state, for tests that want to sample rather than parse the log.
        public uint OutputState => outputData;

        // Drive an input pin to a level from outside the chip, as a switch or another chip would
        public void Drive(int pin, bool level)
        {
            var bit = 1u << pin;
            DrivenInputs |= bit;
            DrivenLevels = level ? DrivenLevels | bit : DrivenLevels & ~bit;
        }

        // Stop driving a pin, leaving it to its pull-up
        public void Release(int pin)
        {
            DrivenInputs &= ~(1u << pin);
        }

        // Rising edges of each pin while it was an output: the step pulses a driver received
        public ulong RisingEdges(int pin) => risingEdges[pin];

        public void ClearEdgeCounts() => Array.Clear(risingEdges, 0, risingEdges.Length);

        private void SetOutputData(uint newValue)
        {
            var changed = outputData ^ newValue;
            var rising = changed & newValue & outputEnabled;
            outputData = newValue;
            if(changed == 0)
            {
                return;
            }
            for(var pin = 0; rising != 0 && pin < NumPins; pin++)
            {
                if((rising & (1u << pin)) != 0)
                {
                    risingEdges[pin]++;
                }
            }
            UpdateConnections(changed);

            var traced = changed & TraceMask;
            if(traced == 0)
            {
                return;
            }
            for(var pin = 0; pin < NumPins; pin++)
            {
                if((traced & (1u << pin)) == 0)
                {
                    continue;
                }
                edgeCount++;
                this.Log(LogLevel.Info, "{0} pin {1} -> {2}", machine.ElapsedVirtualTime.TimeElapsed.TotalMicroseconds, pin,
                         ((newValue >> pin) & 1) != 0 ? 1 : 0);
            }
        }

        private void UpdateConnections(uint changed)
        {
            for(var pin = 0; pin < NumPins; pin++)
            {
                if(changed != 0 && (changed & (1u << pin)) == 0)
                {
                    continue;
                }
                Connections[pin].Set(((outputData >> pin) & 1) != 0);
            }
        }

        private readonly IMachine machine;
        private uint pioEnabled;
        private uint outputEnabled;
        private uint outputData;
        private uint interruptMask;
        private uint multiDriveEnabled;
        private uint pullUpDisabled;
        private uint drivenInputs => DrivenInputs;
        private uint drivenLevels => DrivenLevels;
        private ulong edgeCount;
        private readonly ulong[] risingEdges = new ulong[NumPins];

        private const int NumPins = 32;

        private const long RegisterPer = 0x00;
        private const long RegisterPdr = 0x04;
        private const long RegisterPsr = 0x08;
        private const long RegisterOer = 0x10;
        private const long RegisterOdr = 0x14;
        private const long RegisterOsr = 0x18;
        private const long RegisterSodr = 0x30;
        private const long RegisterCodr = 0x34;
        private const long RegisterOdsr = 0x38;
        private const long RegisterPdsr = 0x3C;
        private const long RegisterIer = 0x40;
        private const long RegisterIdr = 0x44;
        private const long RegisterImr = 0x48;
        private const long RegisterIsr = 0x4C;
        private const long RegisterMder = 0x50;
        private const long RegisterMddr = 0x54;
        private const long RegisterMdsr = 0x58;
        private const long RegisterPudr = 0x60;
        private const long RegisterPuer = 0x64;
        private const long RegisterPusr = 0x68;
    }
}
