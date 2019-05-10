using System;

namespace DuetAPI.Machine
{
    /// <summary>
    /// Information about an extruder drive
    /// </summary>
    public class Extruder : ICloneable
    {
        /// <summary>
        /// Drive of this extruder
        /// </summary>
        public int[] Drives = new int[0];

        /// <summary>
        /// Extrusion factor to use (1.0 equals 100%)
        /// </summary>
        public float Factor { get; set; } = 1.0F;
        
        /// <summary>
        /// Nonlinear extrusion parameters (see M592)
        /// </summary>
        public ExtruderNonlinear Nonlinear { get; set; } = new ExtruderNonlinear();

        /// <summary>
        /// Creates a clone of this instance
        /// </summary>
        /// <returns>A clone of this instance</returns>
        public object Clone()
        {
            return new Extruder
            {
                Drives = (int[])Drives.Clone(),
                Factor = Factor,
                Nonlinear = (ExtruderNonlinear)Nonlinear.Clone()
            };
        }
    }
}