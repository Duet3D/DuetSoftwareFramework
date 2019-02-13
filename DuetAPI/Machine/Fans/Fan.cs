using System;

namespace DuetAPI.Machine.Fans
{
    public class Fan : ICloneable
    {
        public double Value { get; set; }                                                   // per cent
        public string Name { get; set; }
        public uint? Rpm { get; set; }
        public bool Inverted { get; set; }
        public double? Frequency { get; set; }
        public double Min { get; set; }
        public double Max { get; set; } = 1.0;
        public double Blip { get; set; } = 0.1;                                             // seconds
        public Thermostatic Thermostatic { get; set; } = new Thermostatic();
        public uint? Pin { get; set; }

        public object Clone()
        {
            return new Fan
            {
                Value = Value,
                Name = (Name != null) ? string.Copy(Name) : null,
                Rpm = Rpm,
                Inverted = Inverted,
                Frequency = Frequency,
                Min = Min,
                Max = Max,
                Blip = Blip,
                Thermostatic = (Thermostatic)Thermostatic.Clone(),
                Pin = Pin
            };
        }
    }
}