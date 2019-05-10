using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Idle factor parameters for automatic motor current reduction
    /// </summary>
    public class Idle : ICloneable
    {
        /// <summary>
        /// Idle timeout after which the stepper motor currents are reduced (in s)
        /// </summary>
        public float Timeout { get; set; } = 30F;
        
        /// <summary>
        /// Motor current reduction factor (on a scale between 0 to 1)
        /// </summary>
        public float Factor { get; set; } = 0.3F;

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Idle
            {
                Timeout = Timeout,
                Factor = Factor
            };
        }
    }
}