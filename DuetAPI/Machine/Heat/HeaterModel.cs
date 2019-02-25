using System;

namespace DuetAPI.Machine.Heat
{
    /// <summary>
    /// Information about the way the heater heats up
    /// </summary>
    public class HeaterModel : ICloneable
    {
        /// <summary>
        /// Gain value or null if unknown
        /// </summary>
        public double? Gain { get; set; }
        
        /// <summary>
        /// Time constant or null if unknown
        /// </summary>
        public double? TimeConst { get; set; }
        
        /// <summary>
        /// Dead time of this heater or null if unknown
        /// </summary>
        public double? DeadTime { get; set; }
        
        /// <summary>
        /// Maximum PWM or null if unknown
        /// </summary>
        public double? MaxPwm { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new HeaterModel
            {
                Gain = Gain,
                TimeConst = TimeConst,
                DeadTime = DeadTime,
                MaxPwm = MaxPwm
            };
        }
    }
}