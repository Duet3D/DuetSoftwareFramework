//
// SAME5x/SAMC21 PORT: the GPIO groups (PA, PB, ...), each 0x80 bytes.
//
// Inputs read what drives them: an output pin reads back its OUT bit; an input pin reads the level
// something outside the chip holds it at (Drive, or the platform's drivenLow/drivenHigh lists), and
// otherwise its pull resistor (PINCFG.PULLEN, pulling towards its OUT bit), and otherwise low. That is
// how a board description sets switches and resistors that the firmware reads: the expansion boards'
// address switches, the board revision straps.
//
// Every output pin counts its rising edges, so a test can count the step pulses a driver received
// without tracing every edge.
//
// Each pin's output - what it drives - is low, high, the PWM of a timer output it is muxed to, a
// peripheral function the model knows nothing more about, or nothing (an input). Changes are recorded
// in the I/O trace (Common/DuetIoTrace.cs) except for pins excluded from it, such as STEP pins, and
// raised as OutputChanged for models that react to an output (a heater's thermal model). The bench
// tells the model which timer output each PWM-capable pin is on (MapPwm) and which EXTINT line each
// pin is on (MapExtInt), from the firmware's pin table; the level of a pin with a line is reported to
// the EIC, which interrupts when the pin is muxed to it (function A).
//
// Inputs can also be scripted to change by themselves, in emulated time, as the machine around the
// board would change them: once a pin has had so many more rising edges (an endstop or a probe that
// triggers when a driver has stepped so far: DriveAfterEdges), as a square wave (a fan's tacho:
// Pulses), or as a square wave whose frequency follows the duty of an output (a fan whose tacho runs
// with its PWM: TachoFollows). Each change a script makes is recorded in the I/O trace as an input.
//
//     port: GPIOPort.SAME5x_Port @ sysbus 0x41008000
//         groups: 4
//         eic: eic
//         drivenLow: "PC18"
//
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Duet;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    public class SAME5x_Port : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput, IGPIOReceiver, IDuetPinOutputs
    {
        public SAME5x_Port(IMachine machine, int groups = 4, SAME5x_Eic eic = null, string drivenLow = "", string drivenHigh = "")
        {
            this.machine = machine;
            this.eic = eic;
            outputs = new PinOutput[groups * PinsPerGroup];
            excluded = new bool[groups * PinsPerGroup];
            extIntLines = new int[groups * PinsPerGroup];
            connectedToEic = new bool[groups * PinsPerGroup];
            for(var i = 0; i < extIntLines.Length; i++)
            {
                extIntLines[i] = -1;
            }
            this.groups = new Group[groups];
            for(var i = 0; i < groups; i++)
            {
                this.groups[i] = new Group();
            }
            var connections = new Dictionary<int, IGPIO>();
            for(var i = 0; i < groups * PinsPerGroup; i++)
            {
                connections[i] = new GPIO();
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(connections);
            risingEdges = new ulong[groups * PinsPerGroup];
            // What drives the pins is outside the chip, so a reset of the chip does not change it
            foreach(var pin in ParsePins(drivenLow))
            {
                Drive(pin, false);
            }
            foreach(var pin in ParsePins(drivenHigh))
            {
                Drive(pin, true);
            }
            Reset();
        }

        public void Reset()
        {
            foreach(var group in groups)
            {
                group.Reset();
            }
            Array.Clear(risingEdges, 0, risingEdges.Length);
            for(var index = 0; index < groups.Length; index++)
            {
                PinsChanged(index, 0xFFFFFFFF);
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            var g = GroupAt(offset);
            if(g == null)
            {
                return 0;
            }
            var reg = offset % GroupSize;
            switch(reg)
            {
                case Dir:
                case DirClr:
                case DirSet:
                case DirTgl:
                    return g.Dir;
                case Out:
                case OutClr:
                case OutSet:
                case OutTgl:
                    return g.Out;
                case In:
                    return InputLevels(g);
                case Ctrl:
                    return g.Ctrl;
                case EvCtrl:
                    return g.EvCtrl;
                default:
                    if(reg < PmuxBase || reg >= PinCfgBase + PinsPerGroup)
                    {
                        return 0;           // WRCONFIG is write-only, and the rest of the group is reserved
                    }
                    var aligned = offset & ~3L;
                    return (uint)(ReadByte(aligned) | ReadByte(aligned + 1) << 8 | ReadByte(aligned + 2) << 16 | ReadByte(aligned + 3) << 24);
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(GroupAt(offset) == null)
            {
                return;
            }
            var index = (int)(offset / GroupSize);
            var g = groups[index];
            switch(offset % GroupSize)
            {
                case Dir:
                    SetDir(index, value);
                    break;
                case DirClr:
                    SetDir(index, g.Dir & ~value);
                    break;
                case DirSet:
                    SetDir(index, g.Dir | value);
                    break;
                case DirTgl:
                    SetDir(index, g.Dir ^ value);
                    break;
                case Out:
                    SetOut(index, value);
                    break;
                case OutClr:
                    SetOut(index, g.Out & ~value);
                    break;
                case OutSet:
                    SetOut(index, g.Out | value);
                    break;
                case OutTgl:
                    SetOut(index, g.Out ^ value);
                    break;
                case Ctrl:
                    g.Ctrl = value;
                    break;
                case WrConfig:
                    PinsChanged(index, WriteConfiguration(g, value));
                    break;
                case EvCtrl:
                    g.EvCtrl = value;
                    break;
                case In:
                    break;                  // read-only
                default:
                    if(offset % GroupSize >= PinCfgBase + PinsPerGroup)
                    {
                        break;              // reserved
                    }
                    for(var i = 0; i < 4; i++)
                    {
                        WriteByte((offset & ~3L) + i, (byte)(value >> (8 * i)));
                    }
                    break;
            }
        }

        public ushort ReadWord(long offset)
        {
            var word = ReadDoubleWord(offset & ~3L);
            return (ushort)(word >> (int)((offset & 2) * 8));
        }

        public void WriteWord(long offset, ushort value)
        {
            var reg = (offset % GroupSize) & ~3L;
            if(reg >= PmuxBase)
            {
                WriteByte(offset, (byte)value);
                WriteByte(offset + 1, (byte)(value >> 8));
                return;
            }
            // A halfword write to a set/clear register only affects its half
            var shift = (int)((offset & 2) * 8);
            WriteDoubleWord(offset & ~3L, IsSetClearToggle(reg) ? (uint)value << shift : MergeHalf(offset, value));
        }

        public byte ReadByte(long offset)
        {
            if(GroupAt(offset) == null)
            {
                return 0;
            }
            var g = groups[offset / GroupSize];
            var reg = offset % GroupSize;
            if(reg >= PinCfgBase && reg < PinCfgBase + PinsPerGroup)
            {
                return g.PinCfg[reg - PinCfgBase];
            }
            if(reg >= PmuxBase && reg < PmuxBase + PinsPerGroup / 2)
            {
                return g.Pmux[reg - PmuxBase];
            }
            return (byte)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 3) * 8));
        }

        public void WriteByte(long offset, byte value)
        {
            if(GroupAt(offset) == null)
            {
                return;
            }
            var g = groups[offset / GroupSize];
            var reg = offset % GroupSize;
            var index = (int)(offset / GroupSize);
            if(reg >= PinCfgBase && reg < PinCfgBase + PinsPerGroup)
            {
                g.PinCfg[reg - PinCfgBase] = (byte)(value & PinCfgMask);
                PinsChanged(index, 1u << (int)(reg - PinCfgBase));
                return;
            }
            if(reg >= PmuxBase && reg < PmuxBase + PinsPerGroup / 2)
            {
                g.Pmux[reg - PmuxBase] = value;
                PinsChanged(index, 3u << (int)(2 * (reg - PmuxBase)));
                return;
            }
            var shift = (int)((offset & 3) * 8);
            var aligned = offset & ~3L;
            var word = IsSetClearToggle(reg & ~3L) ? (uint)value << shift
                     : (ReadDoubleWord(aligned) & ~(0xFFu << shift)) | ((uint)value << shift);
            WriteDoubleWord(aligned, word);
        }

        public void OnGPIO(int number, bool value)
        {
            Drive(number, value);
        }

        public long Size => groups.Length * GroupSize;

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // Pin numbers are group * 32 + pin, so PA00 is 0, PB00 is 32 and PC18 is 82; or use a name, "PC18".

        public void Drive(int pin, bool level)
        {
            var g = groups[pin / PinsPerGroup];
            var bit = 1u << (pin % PinsPerGroup);
            g.Driven |= bit;
            g.DrivenLevels = level ? g.DrivenLevels | bit : g.DrivenLevels & ~bit;
            PinsChanged(pin / PinsPerGroup, bit);
        }

        public void Drive(string pin, bool level) => Drive(PinNumber(pin), level);

        public void Release(int pin)
        {
            groups[pin / PinsPerGroup].Driven &= ~(1u << (pin % PinsPerGroup));
            PinsChanged(pin / PinsPerGroup, 1u << (pin % PinsPerGroup));
        }

        // The timer output a pin produces when muxed to peripheral function `function` (0 = A, 4 = E...)
        public void MapPwm(int pin, int function, IPeripheral timer, int output)
        {
            var source = timer as IDuetPwmSource ?? throw new RecoverableException($"{timer} does not produce PWM");
            pwmMaps[pin] = new PwmMap { Function = function, Source = source, Output = output };
            if(!subscribedSources.Contains(source))
            {
                subscribedSources.Add(source);
                source.PwmChanged += () => SourceChanged(source);
            }
            PinsChanged(pin / PinsPerGroup, 1u << (pin % PinsPerGroup));
        }

        public void MapPwm(string pin, int function, IPeripheral timer, int output) => MapPwm(PinNumber(pin), function, timer, output);

        // The EIC's EXTINT line of a pin
        public void MapExtInt(int pin, int line)
        {
            extIntLines[pin] = line;
            PinsChanged(pin / PinsPerGroup, 1u << (pin % PinsPerGroup));
        }

        public void MapExtInt(string pin, int line) => MapExtInt(PinNumber(pin), line);

        // Leave a pin out of the I/O trace, for a pin that toggles too often to trace (a STEP pin)
        public void ExcludeFromTrace(int pin) => excluded[pin] = true;

        public void ExcludeFromTrace(string pin) => ExcludeFromTrace(PinNumber(pin));

        // What a pin drives now, as "mode duty frequency"
        public string Output(int pin)
        {
            var output = ComputeOutput(pin);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} {1:R} {2:R}", output.Mode.ToString().ToLowerInvariant(), output.Duty, output.Frequency);
        }

        public string Output(string pin) => Output(PinNumber(pin));

        // Raised when what a pin drives changes: pin number, mode, duty and frequency
        public event Action<int, DuetPinMode, double, double> OutputChanged;

        public void GetOutput(int pin, out DuetPinMode mode, out double duty, out double frequency)
        {
            var output = ComputeOutput(pin);
            mode = output.Mode;
            duty = output.Duty;
            frequency = output.Frequency;
        }

        public void Release(string pin) => Release(PinNumber(pin));

        // What the pin is at: what the firmware reads if it is an input, what it drives if an output
        public bool Level(int pin) => ((InputLevels(groups[pin / PinsPerGroup]) >> (pin % PinsPerGroup)) & 1) != 0;

        public bool Level(string pin) => Level(PinNumber(pin));

        public ulong RisingEdges(int pin) => risingEdges[pin];

        public ulong RisingEdges(string pin) => RisingEdges(PinNumber(pin));

        public void ClearEdgeCounts() => Array.Clear(risingEdges, 0, risingEdges.Length);

        // --- Scripted inputs ---

        // Drive `inputPin` to `level` once `edgePin` has had `edges` more rising edges than it has now
        public void DriveAfterEdges(int edgePin, ulong edges, int inputPin, bool level)
        {
            edgeTriggers.Add(new EdgeTrigger { EdgePin = edgePin, At = risingEdges[edgePin] + edges, InputPin = inputPin, Level = level });
            CheckEdgeTriggers(edgePin);
        }

        public void DriveAfterEdges(string edgePin, ulong edges, string inputPin, bool level) =>
            DriveAfterEdges(PinNumber(edgePin), edges, PinNumber(inputPin), level);

        // Forget the edge triggers that have not fired
        public void ClearEdgeTriggers() => edgeTriggers.Clear();

        // Drive `pin` as a square wave at `frequency` Hz, starting high; 0 stops it and releases the pin. A
        // wave already running keeps its level and changes only its rate, so a new speed adds no edge.
        public void Pulses(int pin, double frequency)
        {
            if(frequency <= 0)
            {
                StopPulses(pin);
                Release(pin);
                DuetIoTrace.RecordInput(machine, PinName(pin), "released");
                return;
            }
            var halfPeriod = (ulong)Math.Max(1, Math.Round(500000.0 / frequency));
            if(!pulseWaves.TryGetValue(pin, out var wave))
            {
                wave = new PulseWave();
                wave.Timer = new LimitTimer(machine.ClockSource, 1000000, this, $"pulses{pin}", halfPeriod, Direction.Ascending, false, WorkMode.Periodic, true, true);
                wave.Timer.LimitReached += () =>
                {
                    wave.Level = !wave.Level;
                    Drive(pin, wave.Level);
                };
                pulseWaves[pin] = wave;
            }
            wave.Timer.Limit = halfPeriod;
            if(!wave.Timer.Enabled)
            {
                wave.Timer.Value = 0;
                wave.Level = true;
                Drive(pin, true);
            }
            DuetIoTrace.RecordInput(machine, PinName(pin), string.Format(System.Globalization.CultureInfo.InvariantCulture, "pulses {0:0.###}Hz", frequency));
            wave.Timer.Enabled = true;
        }

        public void Pulses(string pin, double frequency) => Pulses(PinNumber(pin), frequency);

        // Pulse `tachoPin` at the rate a fan driven by `fanPin` would turn: its duty times `maxRpm`,
        // `pulsesPerRev` times a revolution. With `inverted`, the fan runs when the pin is low.
        public void TachoFollows(int fanPin, int tachoPin, double maxRpm, double pulsesPerRev = 2, bool inverted = false)
        {
            tachoFollowers[fanPin] = new Tacho { Pin = tachoPin, MaxRpm = maxRpm, PulsesPerRev = pulsesPerRev, Inverted = inverted };
            UpdateTacho(fanPin, outputs[fanPin]);
        }

        public void TachoFollows(string fanPin, string tachoPin, double maxRpm, double pulsesPerRev = 2, bool inverted = false) =>
            TachoFollows(PinNumber(fanPin), PinNumber(tachoPin), maxRpm, pulsesPerRev, inverted);

        private void UpdateTacho(int fanPin, PinOutput output)
        {
            if(!tachoFollowers.TryGetValue(fanPin, out var tacho))
            {
                return;
            }
            var duty = output.Mode == DuetPinMode.Pwm ? output.Duty : output.Mode == DuetPinMode.High ? 1.0 : 0.0;
            if(tacho.Inverted)
            {
                duty = 1.0 - duty;
            }
            var frequency = duty * tacho.MaxRpm / 60.0 * tacho.PulsesPerRev;
            if(Math.Abs(frequency - tacho.Frequency) < 1e-9)
            {
                return;
            }
            tacho.Frequency = frequency;
            Pulses(tacho.Pin, frequency);
        }

        // Kept for the next wave on the pin, since the clock source keeps every timer made on it
        private void StopPulses(int pin)
        {
            if(pulseWaves.TryGetValue(pin, out var wave))
            {
                wave.Timer.Enabled = false;
            }
        }

        private void CheckEdgeTriggers(int edgePin)
        {
            for(var i = 0; i < edgeTriggers.Count; i++)
            {
                var trigger = edgeTriggers[i];
                if(trigger.EdgePin == edgePin && risingEdges[edgePin] >= trigger.At)
                {
                    edgeTriggers.RemoveAt(i--);
                    Drive(trigger.InputPin, trigger.Level);
                    DuetIoTrace.RecordInput(machine, PinName(trigger.InputPin), trigger.Level ? "high" : "low");
                }
            }
        }

        public static int PinNumber(string name)
        {
            name = name.Trim().ToUpperInvariant();
            if(name.Length < 3 || name[0] != 'P' || name[1] < 'A' || name[1] > 'H' || !int.TryParse(name.Substring(2), out var pin) || pin >= PinsPerGroup)
            {
                throw new RecoverableException($"\"{name}\" is not a pin name such as PA05");
            }
            return (name[1] - 'A') * PinsPerGroup + pin;
        }

        private static IEnumerable<int> ParsePins(string list)
        {
            return list.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(PinNumber);
        }

        private Group GroupAt(long offset)
        {
            var index = offset / GroupSize;
            return index >= 0 && index < groups.Length ? groups[index] : null;
        }

        private static uint InputLevels(Group g)
        {
            uint pulled = 0;
            uint pullUps = 0;
            for(var pin = 0; pin < PinsPerGroup; pin++)
            {
                if((g.PinCfg[pin] & PinCfgPullEn) != 0)
                {
                    pulled |= 1u << pin;
                }
            }
            pullUps = pulled & g.Out;
            var inputs = ~g.Dir;
            return (g.Out & g.Dir)
                 | (inputs & g.Driven & g.DrivenLevels)
                 | (inputs & ~g.Driven & pullUps);
        }

        private void SetDir(int index, uint value)
        {
            var g = groups[index];
            var changed = g.Dir ^ value;
            g.Dir = value;
            PinsChanged(index, changed);
        }

        private void SourceChanged(IDuetPwmSource source)
        {
            foreach(var entry in pwmMaps)
            {
                if(entry.Value.Source == source)
                {
                    PinsChanged(entry.Key / PinsPerGroup, 1u << (entry.Key % PinsPerGroup));
                }
            }
        }

        // Report what changed about the pins of a group in `mask`: their outputs, and the inputs of pins with an EXTINT line
        private void PinsChanged(int index, uint mask)
        {
            if(mask == 0)
            {
                return;
            }
            var g = groups[index];
            var levels = InputLevels(g);
            for(var pin = 0; pin < PinsPerGroup; pin++)
            {
                var bit = 1u << pin;
                if((mask & bit) == 0)
                {
                    continue;
                }
                var number = index * PinsPerGroup + pin;
                if(eic != null && extIntLines[number] >= 0)
                {
                    var connected = IsConnectedToEic(g, pin, number);
                    var level = (levels & bit) != 0;
                    if(connected != connectedToEic[number])
                    {
                        connectedToEic[number] = connected;
                        eic.ConnectPin(extIntLines[number], connected, level);
                    }
                    else if(connected)
                    {
                        eic.OnPinLevel(extIntLines[number], level);
                    }
                }
                var output = ComputeOutput(number);
                if(!output.Equals(outputs[number]))
                {
                    outputs[number] = output;
                    if(!excluded[number])
                    {
                        DuetIoTrace.Record(machine, PinName(number), output.Mode, output.Duty, output.Frequency);
                    }
                    OutputChanged?.Invoke(number, output.Mode, output.Duty, output.Frequency);
                    UpdateTacho(number, output);
                }
            }
        }

        private PinOutput ComputeOutput(int number)
        {
            var g = groups[number / PinsPerGroup];
            var pin = number % PinsPerGroup;
            var bit = 1u << pin;
            if(IsConnectedToEic(g, pin, number))
            {
                return new PinOutput { Mode = DuetPinMode.Input };
            }
            if((g.PinCfg[pin] & PinCfgPmuxEn) != 0)
            {
                if(pwmMaps.TryGetValue(number, out var map) && map.Function == PmuxOf(g, pin))
                {
                    if(map.Source.TryGetPwm(map.Output, out var duty, out var frequency))
                    {
                        return new PinOutput { Mode = DuetPinMode.Pwm, Duty = duty, Frequency = frequency };
                    }
                    // A timer output that is not running holds its pin low
                    return new PinOutput { Mode = DuetPinMode.Low };
                }
                return new PinOutput { Mode = DuetPinMode.Peripheral };
            }
            if((g.Dir & bit) != 0)
            {
                return (g.Out & bit) != 0 ? new PinOutput { Mode = DuetPinMode.High, Duty = 1 } : new PinOutput { Mode = DuetPinMode.Low };
            }
            return new PinOutput { Mode = DuetPinMode.Input };
        }

        private static int PmuxOf(Group g, int pin) => (g.Pmux[pin / 2] >> (4 * (pin & 1))) & 0xF;

        private bool IsConnectedToEic(Group g, int pin, int number) =>
            extIntLines[number] >= 0 && (g.PinCfg[pin] & PinCfgPmuxEn) != 0 && PmuxOf(g, pin) == FunctionEic;

        private static string PinName(int number) => $"P{(char)('A' + number / PinsPerGroup)}{number % PinsPerGroup:D2}";

        private void SetOut(int index, uint value)
        {
            var g = groups[index];
            var rising = ~g.Out & value;
            var changed = g.Out ^ value;
            g.Out = value;
            if(changed == 0)
            {
                return;
            }
            PinsChanged(index, changed);
            for(var pin = 0; pin < PinsPerGroup; pin++)
            {
                var bit = 1u << pin;
                if((changed & bit) != 0)
                {
                    var number = index * PinsPerGroup + pin;
                    if((rising & bit & g.Dir) != 0)
                    {
                        risingEdges[number]++;
                        if(edgeTriggers.Count != 0)
                        {
                            CheckEdgeTriggers(number);
                        }
                    }
                    Connections[number].Set((value & bit) != 0);
                }
            }
        }

        // Returns the pins it changed
        private static uint WriteConfiguration(Group g, uint value)
        {
            var mask = value & 0xFFFF;
            var first = (value & WrConfigHwSel) != 0 ? 16 : 0;
            for(var i = 0; i < 16; i++)
            {
                if((mask & (1u << i)) == 0)
                {
                    continue;
                }
                var pin = first + i;
                if((value & WrConfigWrPinCfg) != 0)
                {
                    g.PinCfg[pin] = (byte)((value >> 16) & PinCfgMask);
                }
                if((value & WrConfigWrPmux) != 0)
                {
                    var pmux = (byte)((value >> 24) & 0xF);
                    var current = g.Pmux[pin / 2];
                    g.Pmux[pin / 2] = (pin & 1) != 0 ? (byte)((current & 0x0F) | (pmux << 4)) : (byte)((current & 0xF0) | pmux);
                }
            }
            return first == 0 ? mask : mask << 16;
        }

        private static bool IsSetClearToggle(long reg) => reg == DirClr || reg == DirSet || reg == DirTgl || reg == OutClr || reg == OutSet || reg == OutTgl;

        private uint MergeHalf(long offset, ushort value)
        {
            var shift = (int)((offset & 2) * 8);
            return (ReadDoubleWord(offset & ~3L) & ~(0xFFFFu << shift)) | ((uint)value << shift);
        }

        private readonly IMachine machine;
        private readonly SAME5x_Eic eic;
        private readonly Group[] groups;
        private readonly ulong[] risingEdges;
        private readonly PinOutput[] outputs;
        private readonly bool[] excluded;
        private readonly int[] extIntLines;
        private readonly bool[] connectedToEic;
        private readonly Dictionary<int, PwmMap> pwmMaps = new Dictionary<int, PwmMap>();
        private readonly HashSet<IDuetPwmSource> subscribedSources = new HashSet<IDuetPwmSource>();
        private readonly List<EdgeTrigger> edgeTriggers = new List<EdgeTrigger>();
        private readonly Dictionary<int, PulseWave> pulseWaves = new Dictionary<int, PulseWave>();

        private class PulseWave
        {
            public LimitTimer Timer;
            public bool Level;
        }
        private readonly Dictionary<int, Tacho> tachoFollowers = new Dictionary<int, Tacho>();

        private sealed class EdgeTrigger
        {
            public int EdgePin;
            public ulong At;
            public int InputPin;
            public bool Level;
        }

        private sealed class Tacho
        {
            public int Pin;
            public double MaxRpm;
            public double PulsesPerRev;
            public bool Inverted;
            public double Frequency;
        }

        private const int PinsPerGroup = 32;
        private const long GroupSize = 0x80;
        private const long Dir = 0x00;
        private const long DirClr = 0x04;
        private const long DirSet = 0x08;
        private const long DirTgl = 0x0C;
        private const long Out = 0x10;
        private const long OutClr = 0x14;
        private const long OutSet = 0x18;
        private const long OutTgl = 0x1C;
        private const long In = 0x20;
        private const long Ctrl = 0x24;
        private const long WrConfig = 0x28;
        private const long EvCtrl = 0x2C;
        private const long PmuxBase = 0x30;
        private const long PinCfgBase = 0x40;
        private const byte PinCfgPmuxEn = 0x01;
        private const byte PinCfgPullEn = 0x04;
        private const int FunctionEic = 0;
        private const byte PinCfgMask = 0x47;        // PMUXEN, INEN, PULLEN, DRVSTR
        private const uint WrConfigWrPmux = 1u << 28;
        private const uint WrConfigWrPinCfg = 1u << 30;
        private const uint WrConfigHwSel = 1u << 31;

        private struct PinOutput
        {
            public DuetPinMode Mode;
            public double Duty;
            public double Frequency;
        }

        private class PwmMap
        {
            public int Function;
            public IDuetPwmSource Source;
            public int Output;
        }

        private class Group
        {
            public void Reset()
            {
                Dir = 0;
                Out = 0;
                Ctrl = 0;
                EvCtrl = 0;
                Array.Clear(PinCfg, 0, PinCfg.Length);
                Array.Clear(Pmux, 0, Pmux.Length);
            }

            public uint Dir;
            public uint Out;
            public uint Ctrl;
            public uint EvCtrl;
            public uint Driven;
            public uint DrivenLevels;
            public readonly byte[] PinCfg = new byte[PinsPerGroup];
            public readonly byte[] Pmux = new byte[PinsPerGroup / 2];
        }
    }
}
