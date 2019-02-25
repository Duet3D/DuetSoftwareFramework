using System;

namespace DuetAPI.Machine.Fans
{
    /// <summary>
    /// Class representing information about an attached fan
    /// </summary>
    public class Fan : ICloneable
    {
        /// <summary>
        /// Value of the fan on a scale between 0 to 1
        /// </summary>
        public double Value { get; set; }
        
        /// <summary>
        /// Name of the fan
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// Current RPM of this fan or null if unknown/unset
        /// </summary>
        public uint? Rpm { get; set; }
        
        /// <summary>
        /// Whether the PWM signal of this fan is inverted
        /// </summary>
        public bool Inverted { get; set; }
        
        /// <summary>
        /// Frequency of the fan (in Hz) or null if unknown/unset
        /// </summary>
        public double? Frequency { get; set; }
        
        /// <summary>
        /// Minimum value of this fan on a scale between 0 to 1
        /// </summary>
        public double Min { get; set; }
        
        /// <summary>
        /// Maximum value of this fan on a scale between 0 to 1
        /// </summary>
        public double Max { get; set; } = 1.0;
        
        /// <summary>
        /// Blip value indicating how long the fan is supposed to run at 100% when turning it on to get it started (in s)
        /// </summary>
        public double Blip { get; set; } = 0.1;                                             // seconds
        
        /// <summary>
        /// Thermostatic control parameters
        /// </summary>
        public Thermostatic Thermostatic { get; set; } = new Thermostatic();
        
        /// <summary>
        /// Pin number of the assigned fan or null if unknown/unset
        /// </summary>
        public uint? Pin { get; set; }

        /// <summary>
        /// Creates a copy of this instance
        /// </summary>
        /// <returns>A copy of this instance</returns>
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