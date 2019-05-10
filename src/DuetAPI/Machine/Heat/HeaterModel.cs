using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the way the heater heats up
    /// </summary>
    public class HeaterModel : ICloneable
    {
        /// <summary>
        /// Gain value or null if unknown
        /// </summary>
        public float? Gain { get; set; }
        
        /// <summary>
        /// Time constant or null if unknown
        /// </summary>
        public float? TimeConstant { get; set; }
        
        /// <summary>
        /// Dead time of this heater or null if unknown
        /// </summary>
        public float? DeadTime { get; set; }
        
        /// <summary>
        /// Maximum PWM or null if unknown
        /// </summary>
        public float? MaxPwm { get; set; }
        
        /// <summary>
        /// Standard voltage of this heater or null if unknown
        /// </summary>
        public float? StandardVoltage { get; set; }

        /// <summary>
        /// Indicates if PID control is being used
        /// </summary>
        public bool UsePID { get; set; } = true;

        /// <summary>
        /// Indicates if custom PID values are used
        /// </summary>
        public bool CustomPID { get; set; }

        /// <summary>
        /// Proportional value of the PID regulator
        /// </summary>
        public float P { get; set; }

        /// <summary>
        /// Integral value of the PID regulator
        /// </summary>
        public float I { get; set; }

        /// <summary>
        /// Derivative value of the PID regulator
        /// </summary>
        public float D { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new HeaterModel
            {
                Gain = Gain,
                TimeConstant = TimeConstant,
                DeadTime = DeadTime,
                MaxPwm = MaxPwm,
                StandardVoltage = StandardVoltage,
                UsePID = UsePID,
                CustomPID = CustomPID,
                P = P,
                I = I,
                D = D
            };
        }
    }
}