using System;

namespace DuetAPI.Machine.Move
{
    /// <summary>
    /// Information about configured microstepping
    /// </summary>
    public class DriveMicrostepping : ICloneable
    {
        /// <summary>
        /// Microstepping value (e.g. x16)
        /// </summary>
        public int Value { get; set; } = 16;
        
        /// <summary>
        /// Whether the microstepping is interpolated
        /// </summary>
        /// <remarks>
        /// This may not be supported on all boards.
        /// </remarks>
        public bool Interpolated { get; set; } = true;
        
        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new DriveMicrostepping
            {
                Value = Value,
                Interpolated = Interpolated
            };
        }
    }
}