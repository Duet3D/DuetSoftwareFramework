using System;

namespace DuetAPI.Machine.Sensors
{
    /// <summary>
    /// Position of a configured endstop
    /// </summary>
    public enum EndstopPosition
    {
        /// <summary>
        /// Endstop is not configured
        /// </summary>
        None = 0,
        
        /// <summary>
        /// Endstop is configured to be hit at the low axis end
        /// </summary>
        LowEnd,
        
        /// <summary>
        /// Endstop is configured to be hit at the high axis end
        /// </summary>
        HighEnd
    }

    /// <summary>
    /// Type of a configured endstop
    /// </summary>
    public enum EndstopType
    {
        /// <summary>
        /// The signal of the endstop is pulled from HIGH to LOW when hit
        /// </summary>
        ActiveLow = 0,
        
        /// <summary>
        /// The signal of the endstop is pulled from LOW to HIGH when hit
        /// </summary>
        ActiveHigh,
        
        /// <summary>
        /// A probe is used for this endstop
        /// </summary>
        Probe,
        
        /// <summary>
        /// Motor load detection is used for this endstop
        /// </summary>
        MotorLoadDetection
    }

    /// <summary>
    /// Information about an endstop
    /// </summary>
    public class Endstop : ICloneable
    {
        /// <summary>
        /// Whether or not the endstop is hit
        /// </summary>
        public bool Triggered { get; set; }
        
        /// <summary>
        /// Position where the endstop is located
        /// </summary>
        public EndstopPosition Position { get; set; } = EndstopPosition.None;
        
        /// <summary>
        /// Type of the endstop
        /// </summary>
        public EndstopType Type { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Endstop
            {
                Triggered = Triggered,
                Position = Position,
                Type = Type
            };
        }
    }
}