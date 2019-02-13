using System;

namespace DuetAPI.Machine.Fans
{
    public class Thermostatic : ICloneable
    {
        public bool Control { get; set; } = true;
        public uint[] Heaters { get; set; } = new uint[0];          // indices
        public double? Temperature { get; set; }                    // degC

        public object Clone()
        {
            return new Thermostatic
            {
                Control = Control,
                Heaters = (uint[])Heaters.Clone(),
                Temperature = Temperature
            };
        }
    }
}