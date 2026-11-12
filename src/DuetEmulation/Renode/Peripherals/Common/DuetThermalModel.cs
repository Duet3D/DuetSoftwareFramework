//
// A heater and its thermistor as a first-order thermal model, so that a heater's control loop has
// something to control: the heater's output pin heats a mass that loses heat to its surroundings, and
// the thermistor on the mass reads its temperature through a series resistor into an ADC channel.
//
// The model is the one RepRapFirmware's own heater model (M307) describes, so a model and the M307
// parameters that match it can be given from the same numbers:
//     dT/dt = heatingRate * duty(t - deadTime) - (T - ambient) / timeConstant
// It is advanced in steps of the machine's emulated time, so it is as deterministic as the rest of the
// machine. Over a step the duty is constant and the solution exact.
//
//     port AttachThermalModel 18 adc1 5 20 20 1 25 100000 4725 2200
//     port ThermalModelTemperature 18
//
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Duet
{
    // A GPIO model whose pins' outputs a thermal model can read
    public interface IDuetPinOutputs : IPeripheral
    {
        void GetOutput(int pin, out DuetPinMode mode, out double duty, out double frequency);
    }

    // An ADC model whose inputs a thermal model can set, as fractions of the reference
    public interface IDuetAnalogInputs : IPeripheral
    {
        void Set(int channel, double fraction);
    }

    public static class DuetThermalModelExtensions
    {
        // Model the heater on `pin` heating the thermistor on ADC `channel`
        public static void AttachThermalModel(this IDuetPinOutputs outputs, int pin, IPeripheral adc, int channel,
                                              double heatingRate, double timeConstant, double deadTime, double ambient,
                                              double r25, double beta, double seriesResistance)
        {
            var inputs = adc as IDuetAnalogInputs ?? throw new RecoverableException($"{adc} does not take analogue inputs");
            var models = Models.GetOrCreateValue(outputs);
            if(models.TryGetValue(pin, out var existing))
            {
                existing.Dispose();
            }
            models[pin] = new DuetThermalModel(outputs, pin, inputs, channel, heatingRate, timeConstant, deadTime, ambient, r25, beta, seriesResistance);
        }

        public static void DetachThermalModel(this IDuetPinOutputs outputs, int pin)
        {
            var models = Models.GetOrCreateValue(outputs);
            if(models.TryGetValue(pin, out var model))
            {
                model.Dispose();
                models.Remove(pin);
            }
        }

        public static string ThermalModelTemperature(this IDuetPinOutputs outputs, int pin)
        {
            if(!Models.GetOrCreateValue(outputs).TryGetValue(pin, out var model))
            {
                throw new RecoverableException($"Pin {pin} has no thermal model");
            }
            return model.Temperature.ToString("R", CultureInfo.InvariantCulture);
        }

        private static readonly ConditionalWeakTable<IDuetPinOutputs, Dictionary<int, DuetThermalModel>> Models =
            new ConditionalWeakTable<IDuetPinOutputs, Dictionary<int, DuetThermalModel>>();
    }

    public sealed class DuetThermalModel : IDisposable
    {
        public DuetThermalModel(IDuetPinOutputs outputs, int pin, IDuetAnalogInputs adc, int channel,
                                double heatingRate, double timeConstant, double deadTime, double ambient,
                                double r25, double beta, double seriesResistance)
        {
            if(timeConstant <= 0 || deadTime < 0 || r25 <= 0 || seriesResistance <= 0)
            {
                throw new RecoverableException("The time constant, R25 and series resistance must be positive and the dead time not negative");
            }
            this.outputs = outputs;
            this.pin = pin;
            this.adc = adc;
            this.channel = channel;
            this.heatingRate = heatingRate;
            this.timeConstant = timeConstant;
            this.ambient = ambient;
            this.r25 = r25;
            this.beta = beta;
            this.seriesResistance = seriesResistance;
            Temperature = ambient;
            for(var i = 0; i < (int)Math.Round(deadTime * StepsPerSecond); i++)
            {
                delayedDuty.Enqueue(0);
            }
            SetReading();

            timer = new LimitTimer(outputs.GetMachine().ClockSource, StepsPerSecond, outputs, $"thermal{pin}", 1,
                                   Direction.Ascending, true, WorkMode.Periodic, true, true);
            timer.LimitReached += Step;
        }

        public double Temperature { get; private set; }

        public void Dispose()
        {
            timer.Enabled = false;
            timer.LimitReached -= Step;
        }

        private void Step()
        {
            outputs.GetOutput(pin, out var mode, out var duty, out _);
            delayedDuty.Enqueue(mode == DuetPinMode.Pwm || mode == DuetPinMode.High ? duty : 0.0);
            var applied = delayedDuty.Dequeue();

            // Exact over a step with constant duty: T relaxes towards its equilibrium with the time constant
            var equilibrium = ambient + heatingRate * timeConstant * applied;
            Temperature = equilibrium + (Temperature - equilibrium) * Math.Exp(-1.0 / (StepsPerSecond * timeConstant));
            SetReading();
        }

        private void SetReading()
        {
            const double AbsoluteZero = -273.15;
            var resistance = r25 * Math.Exp(beta * (1.0 / (Temperature - AbsoluteZero) - 1.0 / (25.0 - AbsoluteZero)));
            adc.Set(channel, resistance / (resistance + seriesResistance));
        }

        private const long StepsPerSecond = 100;

        private readonly IDuetPinOutputs outputs;
        private readonly int pin;
        private readonly IDuetAnalogInputs adc;
        private readonly int channel;
        private readonly double heatingRate;
        private readonly double timeConstant;
        private readonly double ambient;
        private readonly double r25;
        private readonly double beta;
        private readonly double seriesResistance;
        private readonly Queue<double> delayedDuty = new Queue<double>();
        private readonly LimitTimer timer;
    }
}
