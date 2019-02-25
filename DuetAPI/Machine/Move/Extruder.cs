using System;

namespace DuetAPI.Machine.Move
{
    /// <summary>
    /// Information about an extruder drive
    /// </summary>
    public class Extruder : ICloneable
    {
        /// <summary>
        /// Extrusion factor to use (1.0 equals 100%)
        /// </summary>
        public double Factor { get; set; } = 1.0;
        
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
                Factor = Factor,
                Nonlinear = (ExtruderNonlinear)Nonlinear.Clone()
            };
        }
    }
}