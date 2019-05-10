using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about a configured axis
    /// </summary>
    public class Axis : ICloneable
    {
        /// <summary>
        /// Letter of the axis
        /// </summary>
        /// <remarks>
        /// This must be upper-case
        /// </remarks>
        public char Letter { get; set; }
        
        /// <summary>
        /// Array of the drives used (indices)
        /// </summary>
        public int[] Drives { get; set; } = new int[0];
        
        /// <summary>
        /// Whether or not the axis is homed
        /// </summary>
        public bool Homed { get; set; }
        
        /// <summary>
        /// Current machine position (in mm or null if unknown/unset)
        /// </summary>
        public float? MachinePosition { get; set; }
        
        /// <summary>
        /// Minimum travel of this axis (in mm or null if unknown/unset)
        /// </summary>
        public float? Min { get; set; }

        /// <summary>
        /// Whether the axis minimum was probed
        /// </summary>
        public bool MinProbed { get; set; }
        
        /// <summary>
        /// Maximum travel of this axis (in mm or null if unknown/unset)
        /// </summary>
        public float? Max { get; set; }

        /// <summary>
        /// Whether the axis maximum was probed
        /// </summary>
        public bool MaxProbed { get; set; }
        
        /// <summary>
        /// Whether or not the axis is visible
        /// </summary>
        public bool Visible { get; set; } = true;

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Axis
            {
                Letter = Letter,
                Drives = (int[])Drives.Clone(),
                Homed = Homed,
                MachinePosition = MachinePosition,
                Min = Min,
                Max = Max,
                Visible = Visible
            };
        }
    }
}
