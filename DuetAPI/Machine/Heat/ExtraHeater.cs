using System;

namespace DuetAPI.Machine.Heat
{
    /// <summary>
    /// Information about an extra heater (virtual)
    /// </summary>
    public class ExtraHeater : ICloneable
    {
        /// <summary>
        /// Current temperature (in degC)
        /// </summary>
        public double Current { get; set; }
        
        /// <summary>
        /// Name of the extra heater
        /// </summary>
        /// <remarks>
        /// This must not be set to null
        /// </remarks>
        public string Name { get; set; }
        
        /// <summary>
        /// State of the extra heater or null if unknown/unset
        /// </summary>
        public HeaterState? State { get; set; }
        
        /// <summary>
        /// Sensor number (thermistor index) of the extra heater or null if unknown/unset
        /// </summary>
        public uint? Sensor { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new ExtraHeater
            {
                Current = Current,
                Name = (Name != null) ? string.Copy(Name) : null,
                State = State,
                Sensor = Sensor
            };
        }
    }
}