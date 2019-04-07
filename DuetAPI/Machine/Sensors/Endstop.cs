using System;

namespace DuetAPI.Machine
{
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