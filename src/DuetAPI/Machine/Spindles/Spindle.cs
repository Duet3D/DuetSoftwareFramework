using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a CNC spindle
    /// </summary>
    public class Spindle : ICloneable
    {
        /// <summary>
        /// Active RPM
        /// </summary>
        public float Active { get; set; }
        
        /// <summary>
        /// Current RPM
        /// </summary>
        public float Current { get; set; }
        
        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Spindle
            {
                Active = Active,
                Current = Current
            };
        }
    }
}