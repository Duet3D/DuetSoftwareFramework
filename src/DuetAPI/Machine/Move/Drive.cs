using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a drive
    /// </summary>
    public class Drive : ICloneable
    {
        /// <summary>
        /// Current user position of this drive (in mm)
        /// </summary>
        public float? Position { get; set; }
        
        /// <summary>
        /// Information about the configured microstepping
        /// </summary>
        public DriveMicrostepping Microstepping { get; set; } = new DriveMicrostepping();
        
        /// <summary>
        /// Configured current of this drive (in mA)
        /// </summary>
        public int? Current { get; set; }
        
        /// <summary>
        /// Acceleration of this drive (in mm/s^2)
        /// </summary>
        public float? Acceleration { get; set; }
        
        /// <summary>
        /// Minimum allowed speed for this drive (in mm/s)
        /// </summary>
        public float? MinSpeed { get; set; }
        
        /// <summary>
        /// Maximum allowed speed for this drive (in mm/s)
        /// </summary>
        public float? MaxSpeed { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Drive
            {
                Position = Position,
                Microstepping = (DriveMicrostepping)Microstepping.Clone(),
                Current = Current,
                Acceleration = Acceleration,
                MinSpeed = MinSpeed,
                MaxSpeed = MaxSpeed
            };
        }
    }
}