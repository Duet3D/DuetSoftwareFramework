using System;

namespace DuetAPI.Machine.Fans
{
    /// <summary>
    /// Thermostatic parameters of a fan
    /// </summary>
    public class Thermostatic : ICloneable
    {
        /// <summary>
        /// Defines whether thermostatic control is enabled
        /// </summary>
        public bool Control { get; set; } = true;
        
        /// <summary>
        /// The heaters to monitor (indices)
        /// </summary>
        public uint[] Heaters { get; set; } = new uint[0];
        
        /// <summary>
        /// Minimum temperature required to turn on the fan (in degC or null if unknown)
        /// </summary>
        public double? Temperature { get; set; }
        
        
        /// <summary>
        /// Create a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
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