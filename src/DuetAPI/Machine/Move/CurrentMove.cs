using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about the current move
    /// </summary>
    public class CurrentMove : ICloneable
    {
        /// <summary>
        /// Requested speed of the current move (in mm/s)
        /// </summary>
        public float RequestedSpeed { get; set; }
        
        /// <summary>
        /// Top speed of the current move (in mm/s)
        /// </summary>
        public float TopSpeed { get; set; }

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new CurrentMove
            {
                RequestedSpeed = RequestedSpeed,
                TopSpeed = TopSpeed
            };
        }
    }
}