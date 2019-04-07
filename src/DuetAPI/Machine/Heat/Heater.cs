using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a heater
    /// </summary>
    public class Heater : ICloneable
    {
        /// <summary>
        /// Current temperature of the heater (in degC)
        /// </summary>
        public double Current { get; set; }
        
        /// <summary>
        /// Name of the heater or null if unset
        /// </summary>
        public string Name { get; set; }
        
        /// <summary>
        /// State of the heater
        /// </summary>
        public HeaterState? State { get; set; }
        
        /// <summary>
        /// Information about the heater model
        /// </summary>
        public HeaterModel Model { get; set; } = new HeaterModel();
        
        /// <summary>
        /// Maximum allowed temperature for this heater (in degC)
        /// </summary>
        /// <remarks>
        /// This is only temporary and should be replaced by a representation of the heater protection as in RRF
        /// </remarks>
        public double? Max { get; set; }
        
        /// <summary>
        /// Sensor number (thermistor index) of this heater or null if unknown
        /// </summary>
        public int? Sensor { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Heater
            {
                Current = Current,
                Name = (Name != null) ? string.Copy(Name) : null,
                State = State,
                Model = (HeaterModel)Model.Clone(),
                Max = Max,
                Sensor = Sensor
            };
        }
    }
}